# Arquitetura: fluxos

Cada funcionalidade é mostrada como um diagrama de sequência, escrito a partir do código. O código
prevalece: um diagrama que diverge dele está desatualizado.

| Documento | Conteúdo |
|-----------|----------|
| Este arquivo | Legenda, mapa dos processos e o pipeline comum a toda escrita `POST` |
| [fluxos-ledger.md](fluxos-ledger.md) | Abertura de conta, crédito e débito, estorno, troca de limite, relay do outbox, projeção, saldo e extrato |
| [fluxos-customers.md](fluxos-customers.md) | Cadastro de cliente, leitura mascarada e leitura dos dados pessoais completos |

## Legenda

| Elemento | Significado |
|----------|-------------|
| Seta cheia `->>` | Chamada síncrona |
| Seta tracejada `-->>` | Resposta, ou entrega assíncrona entre processos |
| Bloco colorido (`rect`) | Fronteira de uma transação do PostgreSQL: tudo dentro confirma ou desfaz junto |
| `alt` | Caminho de recusa, com o status HTTP e o `type` do Problem Details |
| `Note` | A regra que o passo aplica e o ADR que a decide |

## Mapa dos processos

```mermaid
flowchart LR
  C([Client])
  K[Keycloak]
  subgraph API[Processo Api]
    L[Ledger]
    P[Positions]
    CU[Customers]
  end
  subgraph WK[Processo Worker]
    R[OutboxRelay]
    PP[PositionProjection]
    PU[Limpezas de chaves e outbox]
  end
  PGL[(PostgreSQL schema ledger)]
  PGC[(PostgreSQL schema customers)]
  M[(MongoDB positions)]
  V[OpenBao]

  C -->|obtém token| K
  C -->|JWT Bearer| API
  L -->|role ledger_app| PGL
  CU -->|role customers_app| PGC
  L -->|ICustomerQueries| CU
  P -->|ILedgerQueries| L
  P -->|usuário só leitura| M
  CU -->|Transit HMAC e KV de DEKs| V
  R -->|role ledger_worker| PGL
  R -->|IEntryRecordedConsumer| PP
  PP -->|usuário de escrita| M
  PU -->|ledger_worker e customers_worker| PGL
  PU --> PGC
```

- Módulos só se falam pelos contratos públicos (`*.Contracts`), nunca pelas tabelas uns dos outros
  (ADR-0031). Cada módulo tem schema e role próprios (ADR-0038).
- A Api escreve e lê. O Worker entrega o outbox, projeta no MongoDB e apaga chaves e eventos
  vencidos (ADR-0009, ADR-0035).

## Pipeline comum de escrita (`POST`)

Todo `POST` do Ledger e de Customers passa por este caminho antes do handler do slice. Os diagramas
dos módulos começam onde este termina: no `Handler`.

```mermaid
sequenceDiagram
  autonumber
  actor C as Client
  participant MW as ASP.NET (auth)
  participant E as Endpoint do slice
  participant F as Facade do módulo
  participant B as IdempotencyBehavior
  participant H as Handler do slice
  participant DB as PostgreSQL (schema do módulo)

  C->>MW: POST com Bearer, Idempotency-Key e corpo JSON
  MW->>MW: valida o JWT (emissor, audience ledger-api, só RS256, expiração, folga de 30 s)
  alt token ausente ou inválido
    MW-->>C: 401 authentication-required
  else escopo da rota ausente ou claims ambíguas
    MW-->>C: 403 insufficient-scope
  end
  MW->>E: Caller resolvido (modo serviço ou delegado)
  E->>E: lê Idempotency-Key (um único UUID com hífens)
  alt header ausente ou fora do formato
    E-->>C: 400 idempotency-key-missing ou idempotency-key-invalid
  end
  E->>E: lê o corpo campo a campo
  alt algum campo inválido
    E-->>C: 400 validation-error, com todos os campos de uma vez
  end
  E->>F: Send(command)
  F->>B: mediador gerado do próprio módulo
  B->>B: hash da requisição (campos do comando, modo e titular)
  rect rgba(80, 140, 255, 0.12)
    B->>DB: BEGIN
    B->>DB: INSERT idempotency_keys ON CONFLICT DO NOTHING
    Note over B,DB: a mesma chave numa transação aberta faz este INSERT esperar o desfecho dela
    alt chave já confirmada com o mesmo hash
      B-->>C: resposta guardada, com Idempotent-Replayed true
    else chave já confirmada com outro hash
      B-->>C: 422 idempotency-key-reused
    end
    B->>H: next(command)
    H->>DB: operação do slice
    H-->>B: WriteResult (status e corpo)
    alt resultado transitório
      B->>DB: ROLLBACK, a chave não é consumida
      B-->>C: 409 ou 503, repetir com a mesma chave executa de novo
    else sucesso ou recusa de negócio
      B->>DB: UPDATE idempotency_keys com status e corpo da resposta
      B->>DB: COMMIT
      B-->>C: 201 ou Problem Details
    end
  end
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| JWT, escopo e fallback que fecha rota sem política | [IdentityExtensions.cs](../../src/BuildingBlocks/Identity/IdentityExtensions.cs) | ADR-0012, ADR-0013 |
| Modo serviço ou delegado a partir das claims | [Caller.cs](../../src/BuildingBlocks/Identity/Caller.cs) | ADR-0013 |
| Leitura do header | [IdempotencyKeyHeader.cs](../../src/BuildingBlocks/Idempotency/IdempotencyKeyHeader.cs) | ADR-0037 |
| Campos que entram no hash | [RequestFields.cs](../../src/BuildingBlocks/Idempotency/RequestFields.cs) | ADR-0037 |
| Facade que despacha pelo mediador do módulo | [LedgerCommands.cs](../../src/Modules/Ledger/Ledger/Features/LedgerCommands.cs), [CustomersCommands.cs](../../src/Modules/Customers/Customers/Features/CustomersCommands.cs) | ADR-0036 |
| Reserva da chave, replay, commit ou rollback | [IdempotentExecution.cs](../../src/BuildingBlocks/Idempotency/IdempotentExecution.cs) | ADR-0037 |
| Resposta HTTP e header de replay | [WriteResult.cs](../../src/BuildingBlocks/Idempotency/WriteResult.cs) | ADR-0037, ADR-0039 |

Diferenças por módulo:

| | Ledger | Customers |
|---|--------|-----------|
| Hash da requisição | SHA-256 local: valores financeiros, sem dado pessoal | HMAC no Transit do OpenBao, antes do `BEGIN`: o corpo tem CPF |
| Abertura da transação | `SET LOCAL lock_timeout = '2s'` | Padrão |
| Falha transitória | Lock esgotado: `409 concurrency-timeout` | Cofre fora: `503 personal-data-unavailable` com `Retry-After: 5` |
