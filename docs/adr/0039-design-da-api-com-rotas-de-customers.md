# ADR-0039: Design da API HTTP, com leitura mascarada e completa de cliente em rotas separadas

- **Status:** Aceita
- **Data:** 2026-11-01
- **Substitui:** ADR-0020

## Contexto

A API é o contrato com os sistemas consumidores do banco. Precisa expressar as regras de
autorização (ADR-0013), idempotência (ADR-0037) e representação de dinheiro (ADR-0018), ser
evolutiva sem quebrar consumidores e ter erros tratáveis por máquina.

O ADR-0020 previa `GET /v1/customers/{id}` com dois escopos alternativos: `customers.read` devolveria
os dados mascarados e `customers.pii.read`, os completos. A F5 constrói Customers e decide outra
forma: uma rota por escopo. Com dois escopos numa rota, a mesma URL devolve conteúdos diferentes
conforme o token, o cache e o log de acesso não distinguem uma leitura da outra, e a regra "cada
rota exige exatamente um escopo" (ADR-0013) deixa de valer. Este ADR substitui o ADR-0020 por
inteiro e acrescenta os erros de Customers.

## Decisão

### Recursos

Prefixo `/v1`.

| Método | Rota | Escopo | Resposta |
|--------|------|--------|----------|
| `POST` | `/customers` | `customers.write` | `201` com `{ id }` |
| `GET` | `/customers/{id}` | `customers.read` | `200` com os dados do cliente mascarados |
| `GET` | `/customers/{id}/personal-data` | `customers.pii.read` | `200` com os dados do cliente completos; todo acesso gera log de auditoria |
| `POST` | `/customers/{id}/erasure-requests` | `customers.erasure.manage` | `202` com `{ id }` |
| `POST` | `/accounts` | `ledger.accounts.manage` | `201` com `{ id }` |
| `PUT` | `/accounts/{id}/credit-limit` | `ledger.accounts.manage` | `204` |
| `POST` | `/accounts/{id}/credits` | `ledger.credits.write` | `201` com lançamento, `balanceAfter`, `version` |
| `POST` | `/accounts/{id}/debits` | `ledger.debits.write` | `201` com lançamento, `balanceAfter`, `version` |
| `POST` | `/accounts/{id}/entries/{entryId}/reversal` | `ledger.entries.reverse` | `201` com lançamento de estorno |
| `GET` | `/accounts/{id}/balance?at={instante}` | `positions.read` | `200` com `{ balance, at, version }`; sem `at`, saldo atual |
| `GET` | `/accounts/{id}/statement?from&to&cursor` | `positions.read` | `200` com página de lançamentos e próximo cursor |

### Leitura de cliente em duas rotas

- `GET /customers/{id}` devolve `id` e `registeredAt` em claro e nome, CPF, nascimento e e-mail
  mascarados: primeiro nome e iniciais, `***.982.247-**`, `****-**-15`, `a***@dominio`.
- `GET /customers/{id}/personal-data` devolve os mesmos campos completos, com o CPF formatado
  (`529.982.247-25`). Cada acesso, com sucesso ou não, emite um log de auditoria só com
  identificadores (client, cliente consultado, operação, resultado e `traceId`).
- Nas duas, o chamador delegado só lê o próprio cliente; outro cliente responde `404`
  `customer-not-found`, igual a um id inexistente (ADR-0013).

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
  | `customer-not-found` | `404` | Cliente não cadastrado ou de outro titular (ADR-0013) |
  | `cpf-already-registered` | `409` | CPF já cadastrado; a resposta não traz o id do cliente existente |
  | `personal-data-unavailable` | `503` | Cofre de chaves indisponível; `Retry-After: 5` |
  | `idempotency-key-missing` | `400` | Requisição de escrita sem `Idempotency-Key` |
  | `idempotency-key-reused` | `422` | Mesma chave com payload diferente (ADR-0037) |
  | `duplicate-external-reference` | `409` | Referência externa já registrada |
  | `concurrency-timeout` | `409` | Tempo de espera pelo lock esgotado; repetir |
  | `validation-error` | `400` | Dados de entrada inválidos, com lista de campos |
  | `personal-data-blocked` | `403` | Dados do cliente bloqueados (ADR-0017) |

- **Paginação do extrato por cursor** (keyset em `sequence`): custo constante por página e sem
  itens pulados ou repetidos quando novos lançamentos chegam durante a navegação. Paginação por
  offset foi rejeitada por degradar com o tamanho do histórico.
- **Datas** em ISO 8601, sempre em UTC.
- **Valores monetários** como string decimal (ADR-0018).
- **Cabeçalhos:** `Idempotency-Key` obrigatório em `POST` (ADR-0037); `traceparent` (W3C Trace
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
4. **Uma rota de cliente com dois escopos** (o ADR-0020). Rejeitada pelo motivo do contexto: o
   conteúdo dependeria do token, e uma rota passaria a aceitar dois escopos.

## Consequências

- **Positivas:**
  - Privilégio mínimo expresso no próprio formato da API, inclusive na leitura de dados pessoais.
  - O acesso aos dados completos é identificável pela rota, no log e em qualquer gateway.
  - Erros tratáveis por máquina e estáveis entre versões.
  - Extrato navegável com custo constante, independente do tamanho do histórico.
- **Negativas:**
  - Mais endpoints para manter; a lógica comum fica no domínio, não nos endpoints.
  - O consumidor que precisa dos dois formatos faz duas chamadas.
