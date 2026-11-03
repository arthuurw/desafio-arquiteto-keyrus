# Roteiro de implementação

Ordem de implementação e situação de cada funcionalidade. Escopo decidido no
[ADR-0054](adr/0054-escopo-final-da-entrega.md). Cada funcionalidade segue
PLAN → CHECKS → BUILD → VERIFY ([ADR-0034](adr/0034-processo-spec-driven-e-codigo-como-fonte-da-verdade.md)).
A situação de cada épico é acompanhada no [board](https://github.com/users/arthuurw/projects/2) ([ADR-0029](adr/0029-board-do-github-como-versao-publica-das-specs.md)).

| # | Funcionalidade | Situação | Escopo | Entrega | ADRs | Épico |
|---|----------------|----------|--------|---------|------|-------|
| F0 | `foundation` | Entregue | Completo | Solução e projetos (hosts, módulos, Contracts, BuildingBlocks, testes), `Directory.Build.props`, Central Package Management, `global.json`, `.editorconfig`, docker-compose com PostgreSQL (schemas e roles), `Migrator`, liveness, testes de arquitetura base, CI com build e testes | 0051, 0055, 0052, 0044 | [#1](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/1) |
| F1 | `identity` | Entregue | Completo | Keycloak no compose com realm versionado (clients, escopos, login do canal separado do token exchange, usuários com `customer_id`), validação JWT, políticas por escopo, negação por padrão, emissor de token de teste | 0057, 0013 | [#2](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/2) |
| F2 | `ledger-core` | Entregue | Completo | `Money`, agregado `Account`, crédito, débito, estorno único, limite, catálogo de categorias, comandos pelo `Mediator`, lock pessimista com `lock_timeout` de 2 s, `recorded_at` monotônico, `CHECK` e `UNIQUE`, append-only por permissão, titularidade sem revelar conta alheia, Problem Details, endpoints de conta e lançamentos, testes baseados em propriedades, concorrência por barreira, Stryker no CI | 0002, 0004, 0005, 0048, 0049, 0013, 0018, 0042, 0021, 0023, 0030 | [#3](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/3) |
| F3 | `idempotency` | Entregue | Completo | `Idempotency-Key` obrigatório nos `POST` como pipeline behavior do `Mediator`, chave e resposta na mesma transação da operação, hash do comando interpretado com o titular, replay com `Idempotent-Replayed`, recusas de negócio armazenadas, falha transitória sem consumir a chave, `external_reference` única por client com barreira no índice, `400`/`409`/`422`, limpeza de chaves de 7 dias no Worker por função `SECURITY DEFINER`, teste de arquitetura sobre os comandos | 0013, 0042, 0021, 0023, 0030, 0037, 0044 | [#4](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/4) |
| F4 | `positions` | Entregue | Completo | `ledger.outbox` gravado na transação do lançamento, relay no Worker por polling com `SKIP LOCKED` e cursor, funções `SECURITY DEFINER` para reservar, marcar e limpar eventos, projeção pela próxima versão estrita no MongoDB (`positions` e `entries`, microssegundos), usuários leitor e escritor no MongoDB, `ILedgerQueries` como fronteira, saldo atual e em um momento, extrato por cursor `[from, to)`, verificação de versão com fallback e timeout de 300 ms, respostas iguais pelos dois caminhos, testes com MongoDB pausado e duas réplicas | 0006, 0045, 0009, 0013, 0042, 0021, 0052, 0044, 0056 | [#5](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/5) |
| F5 | `customers` | Entregue | Completo | OpenBao no compose em modo servidor (auto-unseal estático, bootstrap idempotente, AppRole com política mínima), cadastro com AES-256-GCM campo a campo e DEK por cliente no KV, AAD com cliente e campo, blind index do CPF pelo Transit com índice único, hash de idempotência por HMAC, leitura mascarada e completa em rotas separadas com log de auditoria, cache de DEK de 5 min, `503` com o cofre fora sem consumir a chave, abertura de conta só para cliente cadastrado, `customers_worker` na limpeza de chaves, facade de comandos por módulo, seed de ana e bruno | 0050, 0017, 0036, 0037, 0044, 0042 | [#6](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/6) |
| F6 | `lgpd-and-audit` | Entregue | Reduzido | Trilha de auditoria por módulo só de inserção (privilégio, trigger contra `UPDATE`, `DELETE` e `TRUNCATE`, DDL no log do servidor), gravada na transação da escrita confirmada e no fim da requisição para recusa, repetição, `401`, `403`, `500` e `499`; leitura completa registrada antes de os dados saírem; `[PersonalData]` apagado dos logs da Api e do Worker, com regra de arquitetura contra dado pessoal sem a marca | 0013, 0017, 0041, 0042, 0044 | [#7](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/7) |
| F7 | `resilience` | Entregue | Mínimo | Readiness com a semântica do ADR-0021 e `statement_timeout` de 3 s nas escritas do Ledger, com recusa transitória `503 database-timeout` | 0021, 0054 | [#8](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/8) |
| F8 | `observability` | Fora da entrega | Fora | Decidido e documentado, não implementado | 0022, 0054 | [#9](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/9) |
| F9 | `load-test` | Entregue | Condicional | Script k6 no perfil `load` do compose (escritas, leituras e uma conta disputada, até 30 VUs) e uma execução com resultados em [teste-de-carga.md](arquitetura/teste-de-carga.md) | 0023, 0054 | [#10](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/10) |
| F10 | `final-docs` | Entregue | Reduzido | Visão da solução no README com diagrama de contêineres, [modelo de falhas](arquitetura/modelo-de-falhas.md), [modelo de ameaças](arquitetura/modelo-de-ameacas.md) (com o risco aceito do `409 cpf-already-registered`, que diz a quem tem `customers.write` se um CPF já é cliente; mitigado pelo escopo e pela trilha de auditoria) e Dependabot | 0055, 0026, 0054 | [#11](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/11) |

Entre a F6 e a F7, o reforço do [épico #69](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/69)
entregou:

- o limite de 16 KB no corpo da requisição ([ADR-0047](adr/0047-limite-de-16-kb-no-corpo-da-requisicao.md));
- a leitura de um instante passado esperando a escrita em voo na conta
  ([ADR-0045](adr/0045-frescor-e-instante-passado-imutavel-na-leitura.md));
- o lote do relay do outbox, que termina e é marcado antes de o Worker sair, e o evento que o
  MongoDB recusa, isolado sem segurar o lote;
- a leitura mascarada de cliente na trilha de auditoria;
- as versões das chaves do Transit fixas na configuração, e `https` exigido para o OpenBao fora de
  Development;
- nos testes, um PostgreSQL compartilhado por execução, o reuso local de containers e as imagens
  fixadas por digest, iguais às do compose.

Depois dele, a reconciliação do [épico #79](https://github.com/arthuurw/desafio-arquiteto-keyrus/issues/79)
alinhou documentação e código:

- o `to` do extrato no futuro recusado como o `at` do saldo;
- a identidade do chamador num projeto só com a BCL, o único que os Contracts referenciam
  ([ADR-0052](adr/0052-monolito-modular-com-identidade-do-chamador-isolada.md));
- seis ADRs substituídos pelos que registram o que o código mantém (ADR-0048 a ADR-0052, e o do
  relay, hoje no ADR-0056), com cada citação de ADR substituído anotada com o sucessor;
- o contrato publicando todo cabeçalho e regra de schema que a Api aplica, e o
  [`requests.http`](api/requests.http) com as dez rotas.

## Decidido, fora da entrega

O [ADR-0054](adr/0054-escopo-final-da-entrega.md) fechou o escopo pelo prazo. Continuam decididos
nos seus ADRs, sem implementação:

1. F6: pedido de eliminação, bloqueio e crypto-shredding (ADR-0017).
2. F7: timeout da requisição inteira, bulkhead, rate limiting, load shedding, circuit breakers, pools separados, encerramento
   gracioso da Api (ADR-0021).
3. F8: OpenTelemetry, Grafana LGTM, métricas de negócio, rastreamento através do outbox, dashboard e
   alertas (ADR-0022).
4. F9: recalibração dos parâmetros de resiliência (ADR-0021, ADR-0023).
5. F10: CodeQL (ADR-0055), runbook e guia de desenvolvimento separado do README (ADR-0026).
