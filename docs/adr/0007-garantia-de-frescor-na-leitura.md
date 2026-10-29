# ADR-0007: Garantia de frescor na leitura com fallback para o modelo de escrita

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

Com o modelo de leitura alimentado de forma assíncrona (ADR-0006), há uma janela em que o
MongoDB não reflete lançamentos já confirmados no PostgreSQL. O MongoDB também pode ficar
indisponível ou atrasado por falha no processo de projeção.

Requisito de negócio: **nenhuma consulta pode exibir saldo ou extrato que não reflita uma
operação já efetuada.** Para um cliente, ver um débito confirmado que "não aparece" no saldo é
uma inconsistência inaceitável, mesmo que momentânea.

## Alternativas consideradas

1. **Aceitar consistência eventual sem verificação.** Rejeitada: viola diretamente o requisito.
2. **Token de consistência fornecido pelo cliente.** O comando retorna `version`; o cliente envia
   `minVersion` na consulta; sem token, a consulta é servida pelo MongoDB sem verificação.
   - Prós: isolamento total de carga quando não há token.
   - Contras: a garantia vale apenas para quem possui o token. Outro canal (internet banking após
     operar no app) ou outro sistema consumidor pode ver dado defasado. Rejeitada por não cumprir
     o requisito de forma universal.
3. **Verificação de versão contra o modelo de escrita em toda consulta, com fallback.**

## Decisão

**Alternativa 3.**

Fluxo de toda consulta de saldo ou extrato:

1. Ler a `version` atual da conta no PostgreSQL: leitura de uma linha por chave primária, sem
   lock. Pelo MVCC do PostgreSQL, essa leitura **não é bloqueada** pelo `SELECT ... FOR UPDATE`
   das escritas (ADR-0005).
2. Ler o documento da conta no MongoDB.
3. Se a versão projetada no MongoDB for **igual** à versão do PostgreSQL, responder pelo MongoDB.
4. Caso contrário (MongoDB atrasado, indisponível ou com erro), responder pelo **PostgreSQL**,
   registrar métrica de fallback e, se recorrente, alertar.

Proteções:

- **Circuit breaker** no acesso ao MongoDB: com falhas sucessivas, as consultas vão direto ao
  PostgreSQL por um período, evitando acumular latência de timeouts.
- **Timeout curto** na leitura do MongoDB.
- Consulta de saldo em um momento T anterior à versão projetada também pode ser servida pelo
  MongoDB com segurança, pois o histórico é imutável (ADR-0002), desde que o MongoDB contenha
  todos os lançamentos até T.

## Consequências

- **Positivas:**
  - Garantia universal: nenhuma consulta, por nenhum canal, exibe estado anterior a uma operação
    confirmada.
  - Degradação controlada: com o MongoDB fora do ar, consultas continuam corretas, apenas com mais
    carga no PostgreSQL.
- **Negativas:**
  - Toda consulta faz um acesso mínimo ao PostgreSQL; o isolamento de carga é parcial. O trabalho
    pesado (histórico, extrato, agregações) fica no MongoDB, o PostgreSQL responde apenas a
    versão da conta.
  - Em falha prolongada do MongoDB, toda a carga de leitura recai sobre o PostgreSQL. Mitigação em
    produção: réplica de leitura do PostgreSQL como destino do fallback, com a ressalva de que a
    réplica também tem atraso e precisaria da mesma verificação de versão contra o primário.
- **Observabilidade necessária:**
  - Atraso do outbox (idade do evento pendente mais antigo).
  - Taxa de fallback de consultas para o PostgreSQL.
  - Estado do circuit breaker.
