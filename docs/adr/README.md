# Architecture Decision Records (ADRs)

Registro das decisões arquiteturais do projeto. Cada ADR documenta **uma** decisão: o contexto,
as alternativas consideradas, a escolha e suas consequências.

## Status possíveis

- **Proposta**: em discussão, ainda não aceita.
- **Aceita**: decisão vigente.
- **Substituída por ADR-NNNN**: decisão revista; o ADR novo explica o motivo.

ADRs aceitos não são editados no mérito. Mudança de decisão gera um novo ADR que substitui o anterior.

## Índice

| ADR | Título | Status |
|-----|--------|--------|
| [0001](0001-limite-de-saldo-por-conta.md) | Limite de saldo configurável por conta | Aceita |
| [0002](0002-modelo-temporal-tempo-de-registro.md) | Modelo temporal baseado em tempo de registro | Aceita |
| [0003](0003-saldo-materializado-e-saldo-acumulado-por-lancamento.md) | Saldo materializado na conta e saldo acumulado por lançamento | Aceita |
| [0004](0004-postgresql-como-banco-de-dados.md) | PostgreSQL como banco de dados | Aceita |
| [0005](0005-controle-de-concorrencia-por-lock-pessimista.md) | Controle de concorrência por lock pessimista na conta | Aceita |
| [0006](0006-cqrs-com-modelo-de-leitura-separado.md) | CQRS com modelo de leitura separado via outbox | Aceita |
| [0007](0007-garantia-de-frescor-na-leitura.md) | Garantia de frescor na leitura com fallback para o modelo de escrita | Aceita |
| [0008](0008-biblioteca-mediator-com-geracao-de-codigo.md) | Biblioteca `Mediator` (geração de código) para comandos e consultas | Aceita |
| [0009](0009-processos-api-e-worker-separados.md) | Processos separados para API e Worker | Aceita |
| [0010](0010-monolito-modular-com-vertical-slices-e-ddd.md) | Monólito modular com vertical slices e DDD | Aceita |
| [0011](0011-convencao-de-idioma.md) | Convenção de idioma | Aceita |
| [0012](0012-autenticacao-oauth2-client-credentials.md) | Autenticação serviço a serviço com OAuth 2.0 client credentials | Aceita |
| [0013](0013-autorizacao-por-escopo-e-por-recurso.md) | Autorização por escopo e por recurso com propagação do usuário final | Aceita |
| [0014](0014-idempotencia-por-chave-e-referencia-externa.md) | Idempotência por chave de requisição e referência externa | Aceita |
| [0015](0015-isolamento-de-dados-por-schema-e-role.md) | Isolamento de dados por schema e role de banco por módulo | Aceita |
| [0016](0016-criptografia-de-dados-pessoais.md) | Criptografia de dados pessoais com chave por cliente no OpenBao | Aceita |
| [0017](0017-lgpd-minimizacao-direitos-e-bloqueio.md) | LGPD: minimização, direitos do titular e bloqueio durante guarda legal | Aceita |
| [0018](0018-representacao-de-dinheiro.md) | Representação de dinheiro com `decimal` e value object `Money` | Aceita |
| [0019](0019-stack-dotnet.md) | Stack .NET: .NET 10, Minimal APIs, EF Core e migrações em processo separado | Aceita |
| [0020](0020-design-da-api.md) | Design da API HTTP | Aceita |
| [0021](0021-resiliencia-sob-carga-e-falhas.md) | Resiliência sob carga e falhas parciais | Aceita |
| [0022](0022-observabilidade-com-opentelemetry.md) | Observabilidade com OpenTelemetry e Grafana LGTM | Aceita |
| [0023](0023-estrategia-de-testes.md) | Estratégia de testes | Aceita |
| [0024](0024-processo-spec-driven-e-codigo-como-fonte-da-verdade.md) | Desenvolvimento orientado a especificação, com o código como fonte da verdade | Aceita |
| [0025](0025-qualidade-de-build-e-integracao-continua.md) | Qualidade de build e integração contínua | Aceita |
| [0026](0026-estrutura-da-documentacao.md) | Estrutura da documentação | Aceita |

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
