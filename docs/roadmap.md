# Roteiro de implementação

Ordem de implementação e situação de cada funcionalidade. Escopo decidido no
[ADR-0027](adr/0027-escopo-de-implementacao.md). Cada funcionalidade segue
PLAN → CHECKS → BUILD → VERIFY ([ADR-0024](adr/0024-processo-spec-driven-e-codigo-como-fonte-da-verdade.md)).

| # | Funcionalidade | Escopo | Entrega | ADRs | Situação |
|---|----------------|--------|---------|------|----------|
| F0 | `foundation` | Completo | Solução e projetos (hosts, módulos, Contracts, BuildingBlocks, testes), `Directory.Build.props`, Central Package Management, `global.json`, `.editorconfig`, docker-compose com PostgreSQL (schemas e roles), `Migrator`, liveness, testes de arquitetura base, CI com build e testes | 0010, 0015, 0019, 0025 | Pendente |
| F1 | `identity` | Completo | Keycloak no compose com realm versionado (clients, escopos, token exchange, usuários com `customer_id`), validação JWT, políticas por escopo, negação por padrão, emissor de token de teste | 0012, 0013 | Pendente |
| F2 | `ledger-core` | Completo | `Money`, agregado `Account`, crédito, débito, estorno, limite, lock pessimista, `recorded_at` monotônico, `CHECK`, append-only por permissão, titularidade, Problem Details, endpoints de conta e lançamentos, testes baseados em propriedades, concorrência, Stryker | 0001-0005, 0018, 0020 | Pendente |
| F3 | `idempotency` | Completo | `Idempotency-Key` como pipeline behavior, `external_reference` única, replay, `422`/`409`, limpeza de chaves no Worker | 0014 | Pendente |
| F4 | `positions` | Completo | Outbox, Worker com `SKIP LOCKED`, projeção idempotente no MongoDB, saldo em um momento, extrato por cursor, verificação de frescor com fallback, testes de falha | 0006, 0007, 0009 | Pendente |
| F5 | `customers` | Completo | OpenBao no compose (auto-unseal estático e bootstrap), cadastro com AES-256-GCM e DEK por cliente, blind index, mascaramento, abertura de conta validando cliente | 0016 | Pendente |
| F6 | `lgpd-and-audit` | Reduzido | Redação de dados pessoais nos logs com teste, trilha de auditoria | 0013, 0017 | Pendente |
| F7 | `resilience` | Reduzido | Bulkhead leitura/escrita, timeouts, health checks | 0021 | Pendente |
| F8 | `observability` | Reduzido | OpenTelemetry e Grafana LGTM no ambiente local | 0022 | Pendente |
| F9 | `load-test` | Reduzido | Script k6 e uma execução com resultados registrados | 0023 | Pendente |
| F10 | `final-docs` | Completo | README, C4, sequências, modelo de falhas, modelo de ameaças, runbook, guia de desenvolvimento, `requests.http`, CodeQL e Dependabot | 0025, 0026 | Pendente |

## Complementos, se houver tempo

Na ordem:

1. F6: pedido de eliminação, bloqueio e crypto-shredding.
2. F7: rate limiting, load shedding, circuit breakers, pools separados, encerramento gracioso.
3. F8: métricas de negócio, rastreamento através do outbox, dashboard e alertas.
4. F9: recalibração dos parâmetros de resiliência.
