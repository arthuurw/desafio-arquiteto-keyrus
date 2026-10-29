# ADR-0020: Design da API HTTP

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

A API é o contrato com os sistemas consumidores do banco. Precisa expressar as regras de
autorização (ADR-0013), idempotência (ADR-0014) e representação de dinheiro (ADR-0018), ser
evolutiva sem quebrar consumidores e ter erros tratáveis por máquina.

## Decisão

### Recursos

Prefixo `/v1`.

| Método | Rota | Escopo | Resposta |
|--------|------|--------|----------|
| `POST` | `/customers` | `customers.write` | `201` com `{ id }` |
| `GET` | `/customers/{id}` | `customers.read` (mascarado) ou `customers.pii.read` | `200` com dados do cliente |
| `POST` | `/customers/{id}/erasure-requests` | `customers.erasure.manage` | `202` com `{ id }` |
| `POST` | `/accounts` | `ledger.accounts.manage` | `201` com `{ id }` |
| `PUT` | `/accounts/{id}/credit-limit` | `ledger.accounts.manage` | `204` |
| `POST` | `/accounts/{id}/credits` | `ledger.credits.write` | `201` com lançamento, `balanceAfter`, `version` |
| `POST` | `/accounts/{id}/debits` | `ledger.debits.write` | `201` com lançamento, `balanceAfter`, `version` |
| `POST` | `/accounts/{id}/entries/{entryId}/reversal` | `ledger.entries.reverse` | `201` com lançamento de estorno |
| `GET` | `/accounts/{id}/balance?at={instante}` | `positions.read` | `200` com `{ balance, at, version }`; sem `at`, saldo atual |
| `GET` | `/accounts/{id}/statement?from&to&cursor` | `positions.read` | `200` com página de lançamentos e próximo cursor |

### Crédito e débito em endpoints separados

Um endpoint único com o tipo no corpo exigiria um único escopo de escrita, e um sistema que só
deveria creditar (ex.: `payments-hub`, recebendo transferências externas) poderia debitar qualquer
conta. Com endpoints e escopos separados, cada client recebe exatamente a capacidade de que
precisa. O estorno tem escopo próprio, restrito ao backoffice.

### Convenções

- **Versionamento na URL** (`/v1`): visível, simples de rotear em gateways e de depurar.
  Mudanças compatíveis (novos campos opcionais) não mudam a versão; mudanças incompatíveis criam
  `/v2`, com convivência das versões durante a migração dos consumidores.
- **Erros no formato RFC 9457 (Problem Details)**, com `type` estável por erro, título e detalhe em
  português e `traceId` para correlação. Consumidores tratam erros pelo `type`, nunca pelo texto.
  Exemplos:

  | `type` | Status | Situação |
  |--------|--------|----------|
  | `insufficient-funds` | `422` | Débito excederia o limite |
  | `account-not-found` | `404` | Conta inexistente ou de outro titular (ADR-0013) |
  | `idempotency-key-missing` | `400` | Requisição de escrita sem `Idempotency-Key` |
  | `idempotency-key-reused` | `422` | Mesma chave com payload diferente (ADR-0014) |
  | `duplicate-external-reference` | `409` | Referência externa já registrada |
  | `concurrency-timeout` | `409` | Tempo de espera pelo lock esgotado; repetir |
  | `validation-error` | `400` | Dados de entrada inválidos, com lista de campos |
  | `personal-data-blocked` | `403` | Dados do cliente bloqueados (ADR-0017) |

- **Paginação do extrato por cursor** (keyset em `sequence`): custo constante por página e sem
  itens pulados ou repetidos quando novos lançamentos chegam durante a navegação. Paginação por
  offset foi rejeitada por degradar com o tamanho do histórico.
- **Datas** em ISO 8601, sempre em UTC.
- **Valores monetários** como string decimal (ADR-0018).
- **Cabeçalhos:** `Idempotency-Key` obrigatório em `POST` (ADR-0014); `traceparent` (W3C Trace
  Context) aceito e propagado.

### Documentação OpenAPI

- Geração com `Microsoft.AspNetCore.OpenApi`, nativo do .NET 10.
- Interface de navegação com **Scalar** (licença MIT), disponível apenas em desenvolvimento.
  O Swashbuckle deixou de ser o padrão dos templates a partir do .NET 9.
- Descrições, exemplos e mensagens em português (ADR-0011), incluindo o uso do `Idempotency-Key`,
  os escopos exigidos por operação e o catálogo de erros.

## Alternativas consideradas

1. **Endpoint único `POST /entries` com tipo no corpo.** Rejeitada pelo problema de escopo descrito
   acima.
2. **Versionamento por header ou media type.** Rejeitada: mais difícil de operar, rotear e depurar,
   sem benefício prático para consumidores internos.
3. **Paginação por offset.** Rejeitada: degrada com o volume e é inconsistente com inserções
   concorrentes.

## Consequências

- **Positivas:**
  - Privilégio mínimo expresso no próprio formato da API.
  - Erros tratáveis por máquina e estáveis entre versões.
  - Extrato navegável com custo constante, independente do tamanho do histórico.
- **Negativas:**
  - Mais endpoints de escrita para manter; a lógica comum fica no domínio, não nos endpoints.
