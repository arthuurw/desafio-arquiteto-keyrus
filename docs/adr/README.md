# Architecture Decision Records (ADRs)

Registro das decisões arquiteturais do projeto. Cada ADR documenta **uma** decisão: o contexto,
as alternativas consideradas, a escolha e suas consequências.

## Status possíveis

- **Proposta**: em discussão, ainda não aceita.
- **Aceita**: decisão vigente.
- **Substituída por ADR-NNNN**: decisão revista; o ADR novo explica o motivo.

ADRs aceitos não são editados no mérito. Mudança de decisão gera um novo ADR que substitui o anterior.
O ADR substituído vai para [`history/`](history/), com o mesmo nome e conteúdo: a raiz desta pasta
guarda só as decisões vigentes.

## Regra e catálogo

O ADR registra a regra. O catálogo vive no artefato que o código executa ou que um teste confere
contra ele ([ADR-0046](0046-adr-registra-a-regra-e-o-catalogo-vive-no-artefato.md)):

| Catálogo | Onde vive |
|----------|-----------|
| Rotas, escopo de cada rota, status e `type` de Problem Details | [`docs/api/openapi-v1.json`](../api/openapi-v1.json) |
| Passos, recusas e operações auditadas de cada rota e rotina | [`docs/arquitetura/`](../arquitetura/README.md) |
| Schemas, roles, grants e funções do banco | Migrações de cada módulo e `docker/postgres/init` |
| Clients e escopos do provedor de identidade | `docker/keycloak/realm-ledger.json` |
| Termos de negócio | [`docs/glossario.md`](../glossario.md) |

Uma linha nova num catálogo vai para o artefato dele, no mesmo commit do código. Só uma regra que
muda pede ADR novo. As tabelas dos ADRs aceitos valem como exemplo do momento em que foram escritas.

## Índice

| ADR | Título | Status |
|-----|--------|--------|
| [0001](history/0001-limite-de-saldo-por-conta.md) | Limite de saldo configurável por conta | Substituída por ADR-0048 |
| [0002](0002-modelo-temporal-tempo-de-registro.md) | Modelo temporal baseado em tempo de registro | Aceita |
| [0003](history/0003-saldo-materializado-e-saldo-acumulado-por-lancamento.md) | Saldo materializado na conta e saldo acumulado por lançamento | Substituída por ADR-0049 |
| [0004](0004-postgresql-como-banco-de-dados.md) | PostgreSQL como banco de dados | Aceita |
| [0005](0005-controle-de-concorrencia-por-lock-pessimista.md) | Controle de concorrência por lock pessimista na conta | Aceita |
| [0006](0006-cqrs-com-modelo-de-leitura-separado.md) | CQRS com modelo de leitura separado via outbox | Aceita |
| [0007](history/0007-garantia-de-frescor-na-leitura.md) | Garantia de frescor na leitura com fallback para o modelo de escrita | Substituída por ADR-0045 |
| [0008](history/0008-biblioteca-mediator-com-geracao-de-codigo.md) | Biblioteca `Mediator` (geração de código) para comandos e consultas | Substituída por ADR-0030 |
| [0009](0009-processos-api-e-worker-separados.md) | Processos separados para API e Worker | Aceita |
| [0010](history/0010-monolito-modular-com-vertical-slices-e-ddd.md) | Monólito modular com vertical slices e DDD | Substituída por ADR-0031 |
| [0011](0011-convencao-de-idioma.md) | Convenção de idioma | Aceita |
| [0012](history/0012-autenticacao-oauth2-client-credentials.md) | Autenticação serviço a serviço com OAuth 2.0 client credentials | Substituída por ADR-0057 |
| [0013](0013-autorizacao-por-escopo-e-por-recurso.md) | Autorização por escopo e por recurso com propagação do usuário final | Aceita |
| [0014](history/0014-idempotencia-por-chave-e-referencia-externa.md) | Idempotência por chave de requisição e referência externa | Substituída por ADR-0032 |
| [0015](history/0015-isolamento-de-dados-por-schema-e-role.md) | Isolamento de dados por schema e role de banco por módulo | Substituída por ADR-0033 |
| [0016](history/0016-criptografia-de-dados-pessoais.md) | Criptografia de dados pessoais com chave por cliente no OpenBao | Substituída por ADR-0050 |
| [0017](0017-lgpd-minimizacao-direitos-e-bloqueio.md) | LGPD: minimização, direitos do titular e bloqueio durante guarda legal | Aceita |
| [0018](0018-representacao-de-dinheiro.md) | Representação de dinheiro com `decimal` e value object `Money` | Aceita |
| [0019](history/0019-stack-dotnet.md) | Stack .NET: .NET 10, Minimal APIs, EF Core e migrações em processo separado | Substituída por ADR-0051 |
| [0020](history/0020-design-da-api.md) | Design da API HTTP | Substituída por ADR-0039 |
| [0021](0021-resiliencia-sob-carga-e-falhas.md) | Resiliência sob carga e falhas parciais | Aceita |
| [0022](0022-observabilidade-com-opentelemetry.md) | Observabilidade com OpenTelemetry e Grafana LGTM | Aceita |
| [0023](0023-estrategia-de-testes.md) | Estratégia de testes | Aceita |
| [0024](history/0024-processo-spec-driven-e-codigo-como-fonte-da-verdade.md) | Desenvolvimento orientado a especificação, com o código como fonte da verdade | Substituída por ADR-0034 |
| [0025](history/0025-qualidade-de-build-e-integracao-continua.md) | Qualidade de build e integração contínua | Substituída por ADR-0055 |
| [0026](0026-estrutura-da-documentacao.md) | Estrutura da documentação | Aceita |
| [0027](history/0027-escopo-de-implementacao.md) | Escopo de implementação da primeira entrega | Substituída por ADR-0054 |
| [0028](0028-artefatos-de-especificacao-fora-do-repositorio.md) | Artefatos de especificação fora do repositório | Aceita |
| [0029](0029-board-do-github-como-versao-publica-das-specs.md) | Board do GitHub como versão pública das especificações | Aceita |
| [0030](0030-mediator-e-validacao-de-entrada-no-endpoint.md) | Biblioteca `Mediator` para comandos, com validação de entrada no endpoint | Aceita |
| [0031](history/0031-monolito-modular-com-vertical-slices-e-ddd.md) | Monólito modular com vertical slices e DDD, com o domínio dono dos seus tipos | Substituída por ADR-0052 |
| [0032](history/0032-idempotencia-por-chave-e-referencia-externa.md) | Idempotência por chave de requisição e referência externa, com limpeza em lotes | Substituída por ADR-0037 |
| [0033](history/0033-isolamento-de-dados-por-schema-e-role.md) | Isolamento de dados por schema e role, com privilégio mínimo por coluna | Substituída por ADR-0038 |
| [0034](0034-processo-spec-driven-e-codigo-como-fonte-da-verdade.md) | Desenvolvimento orientado a especificação, com mutações declaradas no AGENTS.md | Aceita |
| [0035](history/0035-relay-do-outbox-e-projecao-pela-proxima-versao.md) | Relay do outbox por polling e projeção pela próxima versão estrita | Substituída por ADR-0053 |
| [0036](0036-despacho-de-comandos-por-facade-do-modulo.md) | Despacho de comandos por facade do módulo | Aceita |
| [0037](0037-idempotencia-por-modulo-com-hash-sem-dado-pessoal.md) | Idempotência por módulo, com hash de requisição sem dado pessoal reversível | Aceita |
| [0038](history/0038-isolamento-por-schema-e-role-com-worker-por-modulo.md) | Isolamento de dados por schema e role, com role de Worker por módulo | Substituída por ADR-0043 |
| [0039](history/0039-design-da-api-com-rotas-de-customers.md) | Design da API HTTP, com leitura mascarada e completa de cliente em rotas separadas | Substituída por ADR-0042 |
| [0040](0040-fluxo-de-cada-rota-e-rotina-conferido-contra-o-codigo.md) | Fluxo de cada rota e rotina, conferido contra o código | Aceita |
| [0041](0041-trilha-de-auditoria-por-modulo.md) | Trilha de auditoria por módulo, gravada na transação da escrita | Aceita |
| [0042](0042-design-da-api-com-trilha-de-auditoria.md) | Design da API HTTP, com o acesso aos dados pessoais completos na trilha de auditoria | Aceita |
| [0043](history/0043-isolamento-por-schema-e-role-com-trilha-imutavel.md) | Isolamento de dados por schema e role, com trilha de auditoria só de inserção | Substituída por ADR-0044 |
| [0044](0044-isolamento-por-schema-e-role-com-ddl-no-log.md) | Isolamento de dados por schema e role, com trilha de auditoria só de inserção e DDL no log | Aceita |
| [0045](0045-frescor-e-instante-passado-imutavel-na-leitura.md) | Garantia de frescor na leitura, com o instante passado esperando a escrita em voo | Aceita |
| [0046](0046-adr-registra-a-regra-e-o-catalogo-vive-no-artefato.md) | ADR registra a regra; o catálogo vive no artefato que o executa | Aceita |
| [0047](0047-limite-de-16-kb-no-corpo-da-requisicao.md) | Limite de 16 KB no corpo de toda requisição à Api | Aceita |
| [0048](0048-limite-de-credito-e-teto-tecnico-do-saldo.md) | Limite de crédito por conta e teto técnico do saldo | Aceita |
| [0049](0049-saldo-materializado-e-saldo-acumulado-com-sinal-pelo-tipo.md) | Saldo materializado na conta e saldo acumulado por lançamento, com o sinal dado pelo tipo | Aceita |
| [0050](0050-criptografia-de-dados-pessoais-com-seed-de-desenvolvimento.md) | Criptografia de dados pessoais com chave por cliente no OpenBao, testes sem cofre e seed de desenvolvimento | Aceita |
| [0051](0051-stack-dotnet-com-criterio-de-sql-explicito.md) | Stack .NET com critério de SQL explícito: .NET 10, Minimal APIs, EF Core e migrações em processo separado | Aceita |
| [0052](0052-monolito-modular-com-identidade-do-chamador-isolada.md) | Monólito modular com vertical slices e DDD, e a identidade do chamador em projeto próprio | Aceita |
| [0053](history/0053-relay-do-outbox-e-projecao-pela-proxima-versao.md) | Relay do outbox por polling e projeção pela próxima versão estrita | Substituída por ADR-0056 |
| [0054](0054-escopo-final-da-entrega.md) | Escopo final da entrega, fechado pelo prazo | Aceita |
| [0055](0055-qualidade-de-build-com-auditoria-no-ci.md) | Qualidade de build e integração contínua, com a auditoria de pacotes no CI e não na imagem | Aceita |
| [0056](0056-relay-do-outbox-com-cursor-que-sempre-avanca.md) | Relay do outbox por polling e projeção pela próxima versão estrita, com o cursor que sempre avança | Aceita |
| [0057](0057-autenticacao-oauth2-com-tls-no-proxy.md) | Autenticação serviço a serviço com OAuth 2.0 client credentials, com o TLS terminado no proxy | Aceita |

## Template

```markdown
# ADR-NNNN: Título

- **Status:** Proposta | Aceita | Substituída por ADR-NNNN
- **Data:** AAAA-MM-DD

## Contexto
Qual problema ou força motiva esta decisão.

## Alternativas consideradas
Opções avaliadas, com prós e contras.

## Decisão
O que foi decidido.

## Consequências
Efeitos positivos, negativos e o que fica em aberto.
```
