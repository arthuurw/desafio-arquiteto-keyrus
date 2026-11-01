# Observabilidade explicada

Guia didático de como o sistema é observado em funcionamento. A decisão formal está no
[ADR-0022](../adr/0022-observabilidade-com-opentelemetry.md).

> **Situação:** hoje o sistema só escreve logs no console padrão do .NET e devolve um `traceId` em
> toda resposta de erro. A [seção 9](#9-o-que-existe-hoje) lista o que existe. OpenTelemetry,
> traces, envio por OTLP e o ambiente Grafana LGTM estão previstos na funcionalidade **F8** do
> [roteiro](../roadmap.md). Métricas de negócio, rastreamento através do outbox, dashboard e
> alertas são complementos do F8, feitos se houver tempo. A redação automática de dados pessoais
> nos logs está prevista no **F6**. Os trechos ainda não construídos estão marcados como previstos.

## 1. O problema

Um sistema em produção falha de formas sutis antes de falhar de vez. Neste projeto, por exemplo:

- O MongoDB pode estar 40 segundos atrasado, e as consultas estão sendo respondidas pelo PostgreSQL
  (fallback). Nenhum cliente percebe nada, mas o PostgreSQL está trabalhando dobrado. Hoje esse
  estado só aparece num log de nível `Debug`, e o nível padrão dos hosts é `Information`: na
  prática, ninguém o vê. A métrica de fallback prevista vai torná-lo visível.
- Uma conta muito movimentada pode estar fazendo débitos esperarem 1,5 segundo pelo lock. Ainda
  abaixo do limite de 2 segundos, mas perto.
- O rate limiting (previsto, ver o [guia de resiliência](resiliencia-explicada.md)) pode estar
  recusando 30% das requisições de um sistema chamador com defeito.

Tudo isso "funciona". E tudo isso é um incidente prestes a acontecer. **Observabilidade é a
capacidade de enxergar esses estados por fora, sem precisar abrir o código ou o banco.**

> **Analogia:** o painel de um carro. Você não abre o motor para saber se a temperatura está
> subindo; o ponteiro avisa antes de o motor ferver.

## 2. Os três sinais

| Sinal | Pergunta que responde | Analogia |
|-------|-----------------------|----------|
| **Métricas** | "Quanto? Com que frequência? Está piorando?" | Ponteiros do painel do carro |
| **Logs** | "O que exatamente aconteceu neste momento?" | Diário de bordo |
| **Traces** | "Por onde esta operação passou e onde demorou?" | Rastreamento de encomenda dos Correios |

### Métricas

Números agregados ao longo do tempo. Baratas de armazenar, ótimas para gráficos e alertas.

Tipos usados:

- **Contador:** só cresce. Ex.: total de débitos recusados. O gráfico mostra a taxa por minuto.
- **Gauge:** sobe e desce. Ex.: quantos eventos estão pendentes no outbox agora.
- **Histograma:** distribuição de valores. Ex.: tempo de espera pelo lock. Permite ver não só a
  média, mas o pior caso, como "99% das esperas ficam abaixo de 200 ms".

Por que o pior caso importa: a média esconde problemas. Se 99 débitos levam 10 ms e 1 leva 5
segundos, a média é 60 ms (parece ótimo), mas um cliente esperou 5 segundos.

### Logs

Registros de eventos individuais. Num log **estruturado**, em vez de uma frase solta, cada log é um
conjunto de campos pesquisáveis. Exemplo ilustrativo (este log não existe no código):

```
Frase solta:     "Débito recusado na conta 8f2c por saldo insuficiente"
Estruturado:     { evento: "DebitRejected", account_id: "8f2c", motivo: "insufficient-funds",
                   trace_id: "4bf9..." }
```

O estruturado permite perguntar "quantos débitos foram recusados por saldo insuficiente na última
hora?" sem procurar texto.

Hoje os logs do projeto já nascem com campos nomeados: cada mensagem é declarada com
`[LoggerMessage]`, e valores como `{Claimed}` ou `{Timeout}` viram campos. Mas eles vão para o
console padrão do .NET, como texto, e sem `trace_id`. O envio dos logs estruturados pelo
OpenTelemetry, com o `trace_id`, está previsto no F8.

**Dados pessoais não devem aparecer em logs**: a redação automática do ADR-0017 vai substituir
campos classificados antes de o log ser escrito (prevista, F6).

### Traces

Um **trace** é a história completa de uma operação. Ele é composto de **spans**: cada span é um
trecho do trabalho, com início, fim e duração. Os traces estão previstos no F8; o exemplo abaixo é
ilustrativo.

```
POST /v1/accounts/8f2c/debits                     [========================] 48 ms
  ├─ autenticação e autorização                   [==]                        3 ms
  ├─ verificação de idempotência                  [==]                        2 ms
  ├─ SELECT ... FOR UPDATE (espera pelo lock)       [==========]             19 ms
  ├─ INSERT lançamento + outbox                                 [====]        8 ms
  └─ COMMIT                                                         [===]     6 ms
```

Olhando esse trace, fica óbvio onde o tempo foi gasto: a espera pelo lock. Sem trace, só se saberia
"o débito demorou 48 ms".

## 3. OpenTelemetry (previsto, F8)

**OpenTelemetry** (OTel) é um padrão aberto, mantido pela CNCF, para gerar e transmitir métricas,
logs e traces. O código do sistema fala "OpenTelemetry", e quem recebe os dados pode ser qualquer
ferramenta compatível (Grafana, Datadog, New Relic, Azure Monitor...).

> **Analogia:** a tomada padrão. O aparelho (sistema) não precisa saber de qual usina vem a energia
> (ferramenta de observabilidade).

O protocolo de envio se chama **OTLP**. Trocar de ferramenta em produção é mudar o endereço de
destino, sem alterar o código.

O OpenTelemetry ainda não está no código. Quando entrar, a maior parte da instrumentação será
**automática**: as bibliotecas de ASP.NET Core, PostgreSQL e MongoDB geram spans e métricas sozinhas.
O código só vai adicionar o que é específico do negócio (débitos recusados, atraso do outbox,
fallbacks), e isso é um complemento do F8.

## 4. Seguindo uma operação através do outbox (previsto, complemento do F8)

Este é o ponto mais interessante do desenho. Um débito acontece em **dois processos e em dois
momentos**:

1. A Api registra o débito no PostgreSQL e grava o evento no outbox.
2. Segundos depois, o Worker lê o evento e atualiza o MongoDB.

Esses dois passos já existem. Sem nada mais, seriam dois traces sem relação. Para ligá-los, a Api
vai gravar no próprio evento do outbox o **`traceparent`**, um identificador padronizado (W3C Trace
Context) do trace em andamento. Quando o Worker processar o evento, ele criará seu span
**vinculado** a esse identificador. Hoje a tabela `ledger.outbox` não guarda o `traceparent`.

```mermaid
sequenceDiagram
    participant C as Chamador
    participant A as Api
    participant P as PostgreSQL
    participant W as Worker
    participant M as MongoDB

    C->>A: POST /debits (traceparent: T1)
    A->>P: lançamento + evento no outbox (com T1)
    A-->>C: 201 Created
    Note over W: segundos depois
    W->>P: lê evento (encontra T1)
    W->>M: atualiza posição (span vinculado a T1)
```

Resultado esperado: abrindo o trace T1 no Grafana, aparecem a requisição, o commit **e** a projeção. Se um
cliente reclamar que "o débito demorou para aparecer no extrato", o trace mostra exatamente quanto
tempo o evento ficou no outbox.

## 5. As métricas deste sistema e o que cada uma revela (previsto, complemento do F8)

Nenhuma métrica existe ainda. A tabela mostra as métricas de negócio planejadas.

| Métrica | Situação normal | Sinal de problema |
|---------|-----------------|-------------------|
| `outbox.lag.seconds` | Abaixo de 1 s | Subindo: Worker parado, lento ou MongoDB com problema |
| `outbox.pending` | Perto de zero | Crescendo sem parar: projeção não acompanha o volume |
| `positions.fallback` | Raro | Frequente: MongoDB atrasado ou fora; PostgreSQL sob carga extra |
| `ledger.lock.wait` | Poucos milissegundos | Alto: conta quente; candidata a sub-ledger (ADR-0003) |
| `ledger.debits.rejected` | Estável | Pico súbito: possível defeito em um canal ou fraude |
| `idempotency.replays` | Baixo | Alto: chamadores repetindo muito, rede instável |
| `http.server.rejected` | Perto de zero | Alto: sobrecarga ou client com defeito |
| `resilience.circuit.state` | Fechado | Aberto: dependência fora do ar |

### Cuidado com cardinalidade

Cada combinação de rótulos de uma métrica vira uma série separada no banco de métricas. Rotular por
`client_id` (poucas dezenas) é seguro. Rotular por `account_id` (milhões) criaria milhões de séries e
derrubaria o Prometheus. Por isso, **identificadores de conta e de cliente são proibidos como rótulos
de métricas**; eles aparecem apenas em traces e logs.

## 6. Alertas (previsto, complemento do F8)

Uma métrica que ninguém olha não protege nada. Os alertas vão avisar quando algo sair do normal:

| Alerta | Dispara quando | Por que importa |
|--------|----------------|-----------------|
| Modelo de leitura atrasado | Atraso do outbox > 30 s por 2 min | O fallback está segurando as consultas, mas o PostgreSQL está sobrecarregado |
| Fallback elevado | > 5% das consultas por 5 min | Mesmo motivo, visto pelo lado da consulta |
| Disjuntor aberto | > 1 min | Uma dependência está fora |
| Descarte de carga | `503` > 1% por 5 min | O sistema está recusando trabalho; precisa de capacidade |

A condição "por N minutos" evita alarmes falsos por oscilações momentâneas.

Dashboard e alertas serão **provisionados como código**: vão ficar em arquivos no repositório e
aparecer prontos no Grafana ao subir o ambiente. Ninguém precisará configurar nada à mão, e qualquer mudança
passa por revisão de código.

## 7. O ambiente local: Grafana LGTM (previsto, F8)

O container `grafana/otel-lgtm` reúne quatro ferramentas. Ele ainda não está no
`docker-compose.yml`.

| Letra | Ferramenta | Guarda |
|-------|------------|--------|
| **L** | Loki | Logs |
| **G** | Grafana | Interface: dashboards, buscas, alertas |
| **T** | Tempo | Traces |
| **M** | Mimir / Prometheus | Métricas |

O sistema vai enviar tudo via OTLP para esse container. No Grafana será possível, por exemplo, clicar em um
pico no gráfico de latência, abrir um trace daquele momento e, a partir do trace, ver os logs
daquela requisição exata, tudo ligado pelo `trace_id`.

## 8. Auditoria não é log

Parecem a mesma coisa, mas têm propósitos opostos:

| | Log de observabilidade | Registro de auditoria |
|---|---|---|
| Para quê | Diagnosticar o sistema | Provar quem fez o quê, para fins legais |
| Onde fica | Loki | Banco de dados, tabela própria |
| Pode ser descartado? | Sim: amostragem, retenção curta | Nunca, dentro do prazo legal |
| Pode ser alterado? | Irrelevante | Não: imutável |

Por isso a auditoria (ADR-0013) não vai depender da pilha de observabilidade: se o Loki cair, nenhum
registro de auditoria se perderá. A trilha de auditoria está prevista no F6.

## 9. O que existe hoje

Antes do OpenTelemetry, o sistema já oferece três pistas.

**O `traceId` nas respostas de erro.** Toda resposta de erro da Api em Problem Details traz o
campo `traceId`. Num erro não tratado, a resposta é `500` e o detalhe da exceção vai só para o
log, nunca para a resposta (ADR-0042). Quem chama informa o `traceId` ao suporte. Hoje o console padrão não imprime esse
identificador ao lado do erro; ligar os dois numa busca é parte do F8.

**Os logs das consultas de posição.** Cada consulta de saldo ou extrato tenta o MongoDB e registra
por que caiu no PostgreSQL:

| Nível | Mensagem | Quando |
|-------|----------|--------|
| `Warning` | `Read model did not answer within {Timeout}; answering from the ledger` | O MongoDB passou do timeout |
| `Warning` | `Read model failed; answering from the ledger` | O MongoDB respondeu com erro |
| `Debug` | `Read model behind the ledger; answering from the ledger` | O MongoDB está atrasado em relação ao PostgreSQL |

Com o nível padrão `Information`, só os dois `Warning` aparecem.

**Os logs do relay do outbox**, no Worker:

| Nível | Mensagem | Quando |
|-------|----------|--------|
| `Information` | `Outbox relay claimed {Claimed} events: {Applied} applied, {AlreadyApplied} already applied, {Deferred} deferred, {Malformed} malformed` | Fim de cada lote com eventos |
| `Error` | `Outbox relay failed; retrying in {Wait}` | Um lote falhou e será repetido depois da espera |
| `Error` | `Outbox event {Id} of type {EventType} is not a valid EntryRecorded.v1 and stays pending` | Um evento não pôde ser lido e fica pendente |
