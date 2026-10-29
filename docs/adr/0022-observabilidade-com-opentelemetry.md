# ADR-0022: Observabilidade com OpenTelemetry e Grafana LGTM

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

O desenho tem pontos de degradação silenciosa: o modelo de leitura pode atrasar (ADR-0006), as
consultas podem cair no fallback (ADR-0007), contas podem sofrer contenção de lock (ADR-0005) e os
mecanismos de proteção podem estar recusando tráfego (ADR-0021). Sem observabilidade, esses estados
só seriam percebidos quando já tivessem virado incidente.

O fluxo também é assíncrono: uma requisição gera um evento que é processado depois, em outro
processo (ADR-0009). Diagnosticar exige seguir uma operação do início ao fim.

Explicação didática dos conceitos: [guia de observabilidade](../guias/observabilidade-explicada.md).

## Decisão

### Instrumentação

- **OpenTelemetry** para traces, métricas e logs, exportados via **OTLP**. Padrão aberto: o backend
  pode ser substituído sem alterar o código.
- Instrumentação automática de ASP.NET Core, `HttpClient`, Npgsql e MongoDB.Driver, mais
  instrumentação manual nos pontos de negócio.
- **Logs** com `Microsoft.Extensions.Logging`, estruturados, exportados via OpenTelemetry, com
  redação de dados pessoais (ADR-0017). Toda entrada de log carrega `trace_id` e `span_id`.
- O `traceId` também é devolvido nas respostas de erro (Problem Details, ADR-0020), permitindo que
  o chamador informe exatamente qual requisição falhou.

### Métricas de negócio e de saúde

| Métrica | Tipo | Responde |
|---------|------|----------|
| `ledger.entries.recorded` (por tipo) | Contador | Volume de créditos, débitos e estornos |
| `ledger.debits.rejected` (por motivo) | Contador | Débitos recusados por limite ou validação |
| `ledger.lock.wait` | Histograma | Contenção por conta (contas quentes) |
| `idempotency.replays` | Contador | Repetições de chamadores (indício de instabilidade de rede) |
| `outbox.pending` | Gauge | Eventos aguardando projeção |
| `outbox.lag.seconds` | Gauge | Idade do evento pendente mais antigo |
| `positions.fallback` (por motivo) | Contador | Consultas atendidas pelo PostgreSQL: atraso, erro ou disjuntor aberto |
| `resilience.circuit.state` (por dependência) | Gauge | Estado dos disjuntores de MongoDB e OpenBao |
| `http.server.rejected` (por status, rota, client) | Contador | Atuação de rate limiting (`429`) e load shedding (`503`) |

### Rastreamento através do outbox

O contexto de rastreamento (`traceparent`, W3C Trace Context) da requisição original é gravado no
evento do outbox. Ao projetar o evento, o Worker cria um span **vinculado** ao trace original.
Resultado: um único trace mostra a requisição, o commit e a projeção no MongoDB, mesmo ocorrendo em
processos e momentos diferentes.

### Backend local

Container **`grafana/otel-lgtm`**: Grafana com Loki (logs), Tempo (traces) e Prometheus (métricas),
recebendo OTLP diretamente.

- **Dashboard do ledger provisionado como código** no repositório, disponível ao subir o ambiente.
- **Alertas provisionados como código:**

  | Alerta | Condição inicial |
  |--------|------------------|
  | Modelo de leitura atrasado | `outbox.lag.seconds` > 30 s por 2 min |
  | Fallback elevado | `positions.fallback` > 5% das consultas por 5 min |
  | Disjuntor aberto | qualquer disjuntor aberto por mais de 1 min |
  | Descarte de carga | `503` > 1% das requisições por 5 min |

### Auditoria não é observabilidade

Os registros de auditoria (ADR-0013) **não** são logs de observabilidade. Ficam no banco, são
imutáveis e têm retenção definida por obrigação legal. Logs de observabilidade podem ser amostrados,
agregados e descartados; auditoria, nunca.

## Alternativas consideradas

1. **.NET Aspire Dashboard standalone.** Leve e excelente para traces e logs. Rejeitada como backend
   principal: não oferece dashboards persistentes nem alertas, que são parte essencial da
   operação descrita.
2. **Jaeger, Prometheus e Grafana em containers separados.** Mesmo resultado da escolha, com mais
   containers e mais configuração.
3. **Serilog como biblioteca de logs.** Rejeitada: dependência adicional sem ganho, dado que o
   `Microsoft.Extensions.Logging` com exportador OpenTelemetry e redação nativa cobre os requisitos.

## Consequências

- **Positivas:**
  - Estados de degradação visíveis e alertados antes de virarem incidente.
  - Operações assíncronas rastreáveis de ponta a ponta.
  - Instrumentação independente de fornecedor.
- **Negativas:**
  - Mais um container no ambiente local (~500 MB).
  - Métricas com rótulos por client e rota exigem cuidado com cardinalidade; rótulos com
    identificadores de conta ou cliente são proibidos.
- **Evolução (produção):**
  - Amostragem de traces na cauda (*tail sampling*) no OpenTelemetry Collector, mantendo 100% dos
    traces com erro.
  - SLOs formais (ex.: 99,9% dos débitos em menos de 300 ms) com alertas por consumo de orçamento de
    erro.
