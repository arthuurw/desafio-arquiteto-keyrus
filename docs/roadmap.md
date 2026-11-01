# Roteiro de implementação

Ordem de implementação e situação de cada funcionalidade. Escopo decidido no
[ADR-0027](adr/0027-escopo-de-implementacao.md). Cada funcionalidade segue
PLAN → CHECKS → BUILD → VERIFY ([ADR-0034](adr/0034-processo-spec-driven-e-codigo-como-fonte-da-verdade.md)).
A situação de cada épico é acompanhada no [board](https://github.com/users/arthuurw/projects/2) ([ADR-0029](adr/0029-board-do-github-como-versao-publica-das-specs.md)).

| # | Funcionalidade | Situação | Escopo | Entrega | ADRs | Épico |
|---|----------------|----------|--------|---------|------|-------|
| F0 | `foundation` | Entregue | Completo | Solução e projetos (hosts, módulos, Contracts, BuildingBlocks, testes), `Directory.Build.props`, Central Package Management, `global.json`, `.editorconfig`, docker-compose com PostgreSQL (schemas e roles), `Migrator`, liveness, testes de arquitetura base, CI com build e testes | 0019, 0025, 0031, 0038 | [#1](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/1) |
| F1 | `identity` | Entregue | Completo | Keycloak no compose com realm versionado (clients, escopos, login do canal separado do token exchange, usuários com `customer_id`), validação JWT, políticas por escopo, negação por padrão, emissor de token de teste | 0012, 0013 | [#2](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/2) |
| F2 | `ledger-core` | Entregue | Completo | `Money`, agregado `Account`, crédito, débito, estorno único, limite, catálogo de categorias, comandos pelo `Mediator`, lock pessimista com `lock_timeout` de 2 s, `recorded_at` monotônico, `CHECK` e `UNIQUE`, append-only por permissão, titularidade sem revelar conta alheia, Problem Details, endpoints de conta e lançamentos, testes baseados em propriedades, concorrência por barreira, Stryker no CI | 0001-0005, 0013, 0018, 0039, 0021, 0023, 0030 | [#3](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/3) |
| F3 | `idempotency` | Entregue | Completo | `Idempotency-Key` obrigatório nos `POST` como pipeline behavior do `Mediator`, chave e resposta na mesma transação da operação, hash do comando interpretado com o titular, replay com `Idempotent-Replayed`, recusas de negócio armazenadas, falha transitória sem consumir a chave, `external_reference` única por client com barreira no índice, `400`/`409`/`422`, limpeza de chaves de 7 dias no Worker por função `SECURITY DEFINER`, teste de arquitetura sobre os comandos | 0013, 0039, 0021, 0023, 0030, 0037, 0038 | [#4](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/4) |
| F4 | `positions` | Entregue | Completo | `ledger.outbox` gravado na transação do lançamento, relay no Worker por polling com `SKIP LOCKED` e cursor, funções `SECURITY DEFINER` para reservar, marcar e limpar eventos, projeção pela próxima versão estrita no MongoDB (`positions` e `entries`, microssegundos), usuários leitor e escritor no MongoDB, `ILedgerQueries` como fronteira, saldo atual e em um momento, extrato por cursor `[from, to)`, verificação de versão com fallback e timeout de 300 ms, respostas iguais pelos dois caminhos, testes com MongoDB pausado e duas réplicas | 0006, 0007, 0009, 0013, 0039, 0021, 0031, 0038, 0035 | [#5](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/5) |
| F5 | `customers` | Pendente | Completo | OpenBao no compose (auto-unseal estático e bootstrap), cadastro com AES-256-GCM e DEK por cliente, blind index, mascaramento, abertura de conta validando cliente | 0016 | [#6](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/6) |
| F6 | `lgpd-and-audit` | Pendente | Reduzido | Redação de dados pessoais nos logs com teste, trilha de auditoria | 0013, 0017 | [#7](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/7) |
| F7 | `resilience` | Pendente | Reduzido | Bulkhead leitura/escrita, timeouts, health checks | 0021 | [#8](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/8) |
| F8 | `observability` | Pendente | Reduzido | OpenTelemetry e Grafana LGTM no ambiente local | 0022 | [#9](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/9) |
| F9 | `load-test` | Pendente | Reduzido | Script k6 e uma execução com resultados registrados | 0023 | [#10](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/10) |
| F10 | `final-docs` | Pendente | Completo | README, C4, sequências, modelo de falhas, modelo de ameaças, runbook, guia de desenvolvimento, `requests.http`, CodeQL e Dependabot | 0025, 0026 | [#11](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/11) |

## Complementos, se houver tempo

Na ordem:

1. F6: pedido de eliminação, bloqueio e crypto-shredding.
2. F7: rate limiting, load shedding, circuit breakers, pools separados, encerramento gracioso.
3. F8: métricas de negócio, rastreamento através do outbox, dashboard e alertas.
4. F9: recalibração dos parâmetros de resiliência.
