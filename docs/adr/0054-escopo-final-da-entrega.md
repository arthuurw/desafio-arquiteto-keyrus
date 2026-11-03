# ADR-0054: Escopo final da entrega, fechado pelo prazo

- **Status:** Aceita
- **Data:** 2026-11-02
- **Substitui:** ADR-0027

## Contexto

O ADR substituído dividiu a solução em funcionalidades (F0 a F10), com o núcleo completo e a
operação reduzida. No fim do prazo, F0 a F6 estão entregues e F7 a F10 não começaram. O tempo
restante não comporta as quatro no escopo que o ADR substituído fixou.

O princípio continua o mesmo: demonstrar o essencial, documentar a evolução
([mapa de decisões](../mapa-de-decisoes.md)). Os riscos centrais do enunciado (consistência,
concorrência, idempotência, leitura sob falha parcial e dados sensíveis) já estão implementados e
provados por testes. O que falta é tornar a solução legível para quem a avalia e mostrar o
comportamento sob falha de dependência.

## Alternativas consideradas

1. **Manter o escopo do ADR substituído.** Rejeitada: não cabe no prazo e deixaria funcionalidades
   pela metade.
2. **Cortar a documentação final e priorizar código operacional (F7 e F8).** Rejeitada: o enunciado
   pede que a solução seja justificada e documentada para outra pessoa evoluir. Sem a visão
   consolidada, as decisões ficam espalhadas em ADRs e guias.
3. **Priorizar a documentação final, depois o mínimo de resiliência em código, e deixar
   observabilidade e carga como decisões documentadas.** Adotada.

## Decisão

Ordem de execução: F10, F7, F9. Situação de cada funcionalidade em
[`docs/roadmap.md`](../roadmap.md).

| Funcionalidade | Escopo final |
|----------------|--------------|
| F0 a F5 | Completo, entregue |
| F6 `lgpd-and-audit` | Reduzido, entregue: redação de dados pessoais nos logs e trilha de auditoria |
| F7 `resilience` | **Mínimo:** readiness com a semântica do ADR-0021 e `statement_timeout` de 3 s nas transações de escrita, com a recusa transitória `503` |
| F8 `observability` | **Fora da entrega** |
| F9 `load-test` | **Condicional:** script k6 e uma execução registrada, só se a F7 terminar no prazo |
| F10 `final-docs` | **Reduzido:** visão da solução no README, diagrama de contêineres, modelo de falhas, modelo de ameaças e Dependabot |

Na F10, a estrutura muda em relação ao ADR-0026:

- os modelos de falhas e de ameaças ficam em `docs/arquitetura/`, ao lado dos fluxos que eles
  citam;
- o modelo de falhas é por cenário (o que o chamador vê, o que acontece com os dados, o mecanismo e
  o teste que prova), e não por componente: cada linha aponta a prova;
- o C4 se reduz ao diagrama de contêineres no README, porque o mapa dos processos de
  `docs/arquitetura/` já mostra os componentes.

### Decidido e documentado, não implementado nesta entrega

| Item | ADR |
|------|-----|
| Pedido de eliminação, bloqueio durante a guarda legal e rotina de crypto-shredding | 0017 |
| Timeout da requisição inteira (5 s), bulkhead entre leitura e escrita, rate limiting por client, load shedding, circuit breakers, pools de conexão separados, encerramento gracioso da Api | 0021 |
| OpenTelemetry, Grafana LGTM, métricas de negócio, rastreamento através do outbox, dashboard e alertas | 0022 |
| Teste de carga, se a F9 não couber, e recalibração dos parâmetros de resiliência | 0021, 0023 |
| CodeQL no CI | 0025 |
| Runbook e guia de desenvolvimento separado do README | 0026 |

Esses itens continuam como decisões vigentes nos seus ADRs. O desenho implementado não os impede,
e cada um pode ser acrescentado sem alterar o que foi entregue.

## Consequências

- **Positivas:**
  - Quem avalia encontra no README a resposta a cada pergunta do enunciado, com o ADR e o teste
    que a sustentam.
  - A readiness e o `statement_timeout` mostram em código como a Api se comporta com uma
    dependência fora ou lenta.
- **Negativas:**
  - A degradação silenciosa (fallback do MongoDB, espera por lock) só aparece nos logs: sem
    métricas nem alertas, não há como vê-la antes de virar incidente.
  - Sem bulkhead nem rate limiting, um pico de leitura ainda disputa conexões e threads com a
    escrita. O modelo de falhas registra esse risco.
  - Sem teste de carga, os valores de timeout e de limite são os iniciais do ADR-0021, não
    calibrados.
