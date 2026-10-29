# ADR-0027: Escopo de implementação da primeira entrega

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

As decisões dos ADRs 0001 a 0026 descrevem a solução completa. A implementação de tudo foi
estimada em 6 a 8 dias de trabalho, acima do prazo planejado para a entrega. É preciso decidir o
que é implementado agora e o que permanece decidido e documentado, sem implementação.

Princípio aplicado (ver [mapa de decisões](../mapa-de-decisoes.md)): demonstrar o essencial,
documentar a evolução. O essencial são os riscos centrais do enunciado: consistência financeira,
concorrência, idempotência, leitura correta sob falha parcial e proteção de dados sensíveis.

## Decisão

Implementação organizada em funcionalidades (ADR-0024). Roteiro e situação em
[`docs/roadmap.md`](../roadmap.md).

| Funcionalidade | Escopo nesta entrega |
|----------------|----------------------|
| F0 `foundation` | Completo |
| F1 `identity` | Completo |
| F2 `ledger-core` | Completo |
| F3 `idempotency` | Completo |
| F4 `positions` | Completo |
| F5 `customers` | Completo |
| F6 `lgpd-and-audit` | **Reduzido:** redação de dados pessoais nos logs com teste automatizado, trilha de auditoria |
| F7 `resilience` | **Reduzido:** bulkhead entre leitura e escrita, timeouts, health checks com a semântica do ADR-0021 |
| F8 `observability` | **Reduzido:** instrumentação OpenTelemetry e backend Grafana LGTM no ambiente local |
| F9 `load-test` | **Reduzido:** script k6 e uma execução com resultados registrados |
| F10 `final-docs` | Completo |

### Decidido e documentado, não implementado nesta entrega

| Item | ADR |
|------|-----|
| Pedido de eliminação, bloqueio durante a guarda legal e rotina de crypto-shredding | 0017 |
| Rate limiting por client, load shedding, circuit breakers, pools de conexão separados, encerramento gracioso | 0021 |
| Métricas de negócio, rastreamento através do outbox, dashboard e alertas provisionados | 0022 |
| Recalibração dos parâmetros de resiliência a partir do teste de carga | 0021, 0023 |

Esses itens permanecem como decisões vigentes: o desenho implementado não os impede, e cada um
pode ser acrescentado sem alterar as funcionalidades entregues. Se houver tempo ao fim da entrega,
são implementados na mesma ordem.

## Alternativas consideradas

1. **Implementar tudo.** Rejeitada pelo prazo.
2. **Reduzir as funcionalidades centrais (ex.: retirar o modelo de leitura ou a criptografia).**
   Rejeitada: são as respostas diretas aos requisitos de alta demanda, falha parcial e proteção de
   dados sensíveis.
3. **Reduzir os mecanismos operacionais (F6 a F9), mantendo o núcleo completo.** Adotada.

## Consequências

- **Positivas:**
  - Os riscos centrais do enunciado são implementados e provados por testes.
  - O que não foi implementado está explícito, com a decisão já tomada e documentada.
- **Negativas:**
  - Parte da resiliência e da observabilidade descrita nos ADRs existe apenas como decisão. O
    README e o modelo de falhas indicam claramente o que está implementado.
