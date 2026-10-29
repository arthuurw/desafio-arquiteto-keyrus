# ADR-0021: Resiliência sob carga e falhas parciais

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

O sistema legado apresenta lentidão e instabilidade em horários de pico. O enunciado exige
operação confiável sob alta demanda e falhas parciais. Os padrões típicos de colapso são:

- Picos de consulta consumindo os recursos necessários às escritas.
- Requisições acumuladas em filas até que todas expirem (falha lenta e total).
- Espera indefinida por dependências lentas, prendendo threads e conexões.
- Retries agressivos multiplicando a carga sobre um componente já degradado.
- Health checks mal desenhados que transformam a falha de um componente em falha geral.

Explicação didática dos conceitos: [guia de resiliência](../guias/resiliencia-explicada.md).

## Decisão

### 1. Proteção da entrada

- **Rate limiting por client** (`System.Threading.RateLimiting` do ASP.NET Core, token bucket
  particionado por `client_id`). Excedido o limite: `429` com `Retry-After`.
- **Bulkhead entre leitura e escrita:** limites de concorrência independentes para rotas de
  leitura e de escrita. Um pico de consultas não consome a capacidade de registrar movimentações.
- **Load shedding:** cada limite de concorrência tem fila curta. Excedida a fila, resposta
  imediata `503` com `Retry-After`, em vez de aguardar até o timeout.

### 2. Timeouts em todos os pontos de espera

| Ponto | Valor inicial | Ao exceder |
|-------|---------------|------------|
| Requisição HTTP | 5 s | `503` |
| `lock_timeout` (espera por lock da conta) | 2 s | `409 concurrency-timeout` (ADR-0020) |
| `statement_timeout` | 3 s | Erro transitório; transação desfeita |
| Leitura no MongoDB | 300 ms | Fallback para o PostgreSQL (ADR-0007) |
| Chamadas ao OpenBao | 1 s | Erro transitório; operação de Customers falha |

Valores iniciais, configuráveis, a calibrar com teste de carga.

### 3. Chamadas a dependências

Com `Microsoft.Extensions.Resilience` (baseado em Polly v8):

- **Circuit breaker** em MongoDB e OpenBao: após falhas sucessivas, chamadas falham imediatamente
  por um período, sem acumular latência de timeouts, e são testadas novamente aos poucos.
- **Retry com backoff exponencial e jitter** apenas em operações idempotentes de leitura e no
  projetor do Worker.
- **Comandos não fazem retry interno.** O chamador repete com o mesmo `Idempotency-Key`
  (ADR-0014), o que é seguro. Retries internos de comandos multiplicariam a carga no momento em
  que o sistema já está pressionado.

### 4. Pools de conexão separados

Pools distintos no PostgreSQL para o caminho de escrita e para as leituras de versão e de fallback
(ADR-0007). Leituras nunca esgotam as conexões necessárias às escritas. Em produção, um pooler
(PgBouncer) protege o limite de conexões do servidor.

### 5. Health checks

| Verificação | O que checa | Por quê |
|-------------|-------------|---------|
| **Liveness** | O processo responde | Não verifica dependências: uma dependência lenta faria o orquestrador reiniciar todas as réplicas em cascata |
| **Readiness da Api** | PostgreSQL e OpenBao | Sem eles, a Api não cumpre suas funções essenciais |
| MongoDB | Reportado como *degradado*, **fora** da readiness | Com o fallback, MongoDB indisponível não impede o atendimento. Incluí-lo retiraria todas as réplicas do balanceador, transformando falha parcial em falha total |
| **Readiness do Worker** | PostgreSQL e MongoDB | O Worker não tem função sem os dois |

### 6. Encerramento gracioso

- A Api deixa de aceitar novas requisições e conclui as que estão em andamento.
- O Worker conclui o lote atual de eventos do outbox antes de encerrar; eventos não concluídos
  permanecem pendentes e são reprocessados (projeção idempotente, ADR-0006).

## Alternativas consideradas

1. **Escalar horizontalmente sem limites de entrada.** Rejeitada: escalar não protege contra
   picos súbitos nem contra um client defeituoso, e o banco de escrita tem limite de conexões.
2. **Retry automático em todas as chamadas.** Rejeitada: multiplica a carga sobre componentes
   degradados (*retry storm*) e, sem idempotência, duplicaria efeitos.
3. **Fila longa de requisições para absorver picos.** Rejeitada: requisições esperam até o timeout
   do chamador, consumindo recursos para entregar respostas que ninguém mais aguarda.

## Consequências

- **Positivas:**
  - Escritas protegidas de picos de leitura.
  - Falhas rápidas e explícitas no lugar de lentidão generalizada.
  - Falha de MongoDB ou do Worker não reduz a disponibilidade da Api.
- **Negativas:**
  - Em sobrecarga, parte das requisições recebe `429` ou `503`; chamadores precisam respeitar
    `Retry-After` e repetir com a mesma chave de idempotência.
  - Muitos parâmetros a calibrar; valores iniciais precisam de validação com teste de carga.
