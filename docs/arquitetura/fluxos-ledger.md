# Fluxos do Ledger e de Positions

Os `POST` passam antes pelo [pipeline comum de escrita](README.md#pipeline-comum-de-escrita-post):
autenticação, escopo, `Idempotency-Key`, validação do corpo e reserva da chave. Os diagramas dos
`POST` começam no `IdempotencyBehavior`, com a transação já aberta, `lock_timeout` de 2 s e
`statement_timeout` de 3 s. Toda
rota de escrita deixa uma linha em `ledger.audit_events` (ADR-0041): a escrita confirmada, na própria
transação; recusa, repetição, `409 concurrency-timeout`, `503 database-timeout`, `401`, `403`, `413`, `500` e `499`, no fim da
requisição, pelo `AuditMiddleware` do pipeline comum.

## Crédito e débito

`POST /v1/accounts/{id}/credits` (escopo `ledger.credits.write`) e
`POST /v1/accounts/{id}/debits` (escopo `ledger.debits.write`). As rotas e os escopos são separados:
quem só credita não consegue debitar (ADR-0042).

```mermaid
sequenceDiagram
  autonumber
  participant B as IdempotencyBehavior
  participant H as RecordEntryHandler
  participant A as Account (agregado)
  participant DB as PostgreSQL ledger

  rect rgba(80, 140, 255, 0.12)
    Note over B,DB: uma transação para chave, lançamento, saldo e outbox (ADR-0056, ADR-0059)
    B->>H: RecordEntryCommand
    H->>DB: SELECT conta FOR UPDATE (delegado filtra customer_id do token no próprio lock)
    alt conta inexistente ou de outro titular
      H-->>B: 404 account-not-found
    end
    H->>DB: busca lançamento com o mesmo client_id e external_reference
    alt referência já usada por este client
      H-->>B: 409 duplicate-external-reference
    end
    H->>DB: GREATEST(clock_timestamp(), last_recorded_at)
    Note over H,DB: instante lido depois do lock, nunca menor que o do lançamento anterior (ADR-0005)
    H->>A: Credit ou Debit (valor, categoria, referência, client, instante)
    alt débito deixaria o saldo abaixo de menos o limite
      A-->>H: 422 insufficient-funds
    else crédito deixaria o saldo acima do máximo
      A-->>H: 422 balance-limit-exceeded
    end
    A-->>H: Entry com sequence igual a version + 1 e balanceAfter
    H->>DB: UPDATE accounts (balance, version, last_recorded_at) e INSERT entries
    alt índice único pega a corrida da referência entre contas
      DB-->>H: 23505, o savepoint desfaz o UPDATE da conta e o INSERT do lançamento
      H-->>B: 409 duplicate-external-reference
    end
    H->>DB: INSERT outbox EntryRecorded.v1
    H-->>B: 201 EntryResponse
    B->>DB: INSERT audit_events (201), guarda a resposta com a chave e COMMIT
  end
  Note over B,DB: lock da conta ou espera no índice único da referência não obtidos em 2 s (55P03) viram 409 concurrency-timeout transitório, com ROLLBACK e chave livre (ADR-0021, ADR-0059)
  Note over B,DB: comando acima de 3 s (57014), com a requisição viva, vira 503 database-timeout transitório, com Retry-After: 1, ROLLBACK e chave livre (ADR-0021, ADR-0059)
```

Id de conta fora do formato não chega ao comando: o endpoint responde `404 account-not-found` antes da
idempotência, sem guardar resposta, e a chave segue livre ([pipeline comum](README.md#pipeline-comum-de-escrita-post)).
O `404` de um id no formato, sem conta, é guardado com a chave, porque a conta pode passar a existir. O `409 duplicate-external-reference` leva `entryId` e `accountId` do
lançamento existente quando o chamador pode ver a conta dele (ADR-0013).

| Etapa | Código | Decisão |
|-------|--------|---------|
| Endpoint, leitura do corpo, envio do comando e operação auditada (`credit`, `debit`, recurso `account`) | [RecordEntry.cs](../../src/Modules/Ledger/Ledger/Features/RecordEntry/RecordEntry.cs), [LedgerAudit.cs](../../src/Modules/Ledger/Ledger/Features/LedgerAudit.cs) | ADR-0030, ADR-0041, ADR-0042 |
| Referência externa, outbox e recusa de duplicata | [EntryWrites.cs](../../src/Modules/Ledger/Ledger/Features/EntryWrites.cs) | ADR-0059 |
| Lock pessimista, prazos do lock e do comando, filtro do titular e instante de registro | [AccountWrites.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/AccountWrites.cs), [LedgerProblems.cs](../../src/Modules/Ledger/Ledger/Features/LedgerProblems.cs) | ADR-0005, ADR-0013, ADR-0021 |
| Regras de saldo, limite e versão | [Account.cs](../../src/Modules/Ledger/Ledger/Domain/Account.cs) | ADR-0048, ADR-0049 |
| Payload do evento | [OutboxEvent.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Outbox/OutboxEvent.cs) | ADR-0006, ADR-0056 |
| Catálogo de recusas | [LedgerProblems.cs](../../src/Modules/Ledger/Ledger/Features/LedgerProblems.cs), [ProblemResponses.cs](../../src/BuildingBlocks/Http/ProblemResponses.cs) | ADR-0042 |

## Estorno

`POST /v1/accounts/{id}/entries/{entryId}/reversal`, escopo `ledger.entries.reverse`. Mesmo caminho
do crédito e do débito: lock da conta, referência externa, outbox. Muda só a decisão no meio.

```mermaid
sequenceDiagram
  autonumber
  participant B as IdempotencyBehavior
  participant H as ReverseEntryHandler
  participant A as Account (agregado)
  participant DB as PostgreSQL ledger

  rect rgba(80, 140, 255, 0.12)
    B->>H: ReverseEntryCommand
    H->>DB: SELECT conta FOR UPDATE (delegado filtra customer_id do token no próprio lock)
    alt conta inexistente ou de outro titular
      H-->>B: 404 account-not-found
    end
    H->>DB: busca lançamento com o mesmo client_id e external_reference
    alt referência já usada por este client
      H-->>B: 409 duplicate-external-reference
    end
    H->>DB: busca o lançamento original nesta conta
    alt não existe nesta conta
      H-->>B: 404 entry-not-found
    end
    H->>DB: existe lançamento com reversed_entry_id igual ao original?
    H->>DB: GREATEST(clock_timestamp(), last_recorded_at)
    H->>A: Reverse(original, já estornado, referência, client, instante)
    alt original já é um estorno
      A-->>H: 422 entry-is-reversal
    else original já tem estorno
      A-->>H: 409 entry-already-reversed
    else estorno de crédito passa do limite
      A-->>H: 422 insufficient-funds
    else estorno de débito passa do saldo máximo
      A-->>H: 422 balance-limit-exceeded
    end
    A-->>H: Entry do tipo oposto, categoria REVERSAL, reversed_entry_id preenchido
    H->>DB: UPDATE accounts e INSERT entries
    alt índice único pega a corrida da referência entre contas
      DB-->>H: 23505, o savepoint desfaz o UPDATE da conta e o INSERT do lançamento
      H-->>B: 409 duplicate-external-reference
    end
    H->>DB: INSERT outbox EntryRecorded.v1
    H-->>B: 201 EntryResponse
    B->>DB: INSERT audit_events (201), guarda a resposta com a chave e COMMIT
  end
  Note over H,DB: com a conta travada, dois estornos do mesmo lançamento se enfileiram e o segundo já vê o primeiro (ADR-0005)
  Note over B,DB: lock da conta ou espera no índice único da referência não obtidos em 2 s (55P03) viram 409 concurrency-timeout transitório, com ROLLBACK e chave livre (ADR-0021, ADR-0059)
  Note over B,DB: comando acima de 3 s (57014), com a requisição viva, vira 503 database-timeout transitório, com Retry-After: 1, ROLLBACK e chave livre (ADR-0021, ADR-0059)
```

Id de conta ou `entryId` fora do formato não chega ao comando: o endpoint responde `404 account-not-found`
ou `404 entry-not-found` antes da idempotência, sem guardar resposta, e a chave segue livre.

| Etapa | Código | Decisão |
|-------|--------|---------|
| Endpoint, ids fora do formato, busca do original e operação auditada (`reverse-entry`, recurso `entry`: o lançamento estornado) | [ReverseEntry.cs](../../src/Modules/Ledger/Ledger/Features/ReverseEntry/ReverseEntry.cs) | ADR-0013, ADR-0041, ADR-0042 |
| Lock, referência externa e outbox, como no crédito | [EntryWrites.cs](../../src/Modules/Ledger/Ledger/Features/EntryWrites.cs), [AccountWrites.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/AccountWrites.cs) | ADR-0005, ADR-0056, ADR-0059 |
| Regras do estorno | `Account.Reverse` em [Account.cs](../../src/Modules/Ledger/Ledger/Domain/Account.cs) | ADR-0048, ADR-0049 |

## Abertura de conta

`POST /v1/accounts`, escopo `ledger.accounts.manage`. Não gera lançamento nem evento: a conta nova
está na versão 0, que é também a versão de uma conta sem documento no MongoDB.

```mermaid
sequenceDiagram
  autonumber
  participant B as IdempotencyBehavior
  participant H as OpenAccountHandler
  participant CQ as ICustomerQueries
  participant PGC as PostgreSQL customers
  participant DB as PostgreSQL ledger

  rect rgba(80, 140, 255, 0.12)
    B->>H: OpenAccountCommand(customerId)
    alt delegado pedindo conta para outro cliente
      H-->>B: 404 customer-not-found
    end
    H->>CQ: ExistsAsync(customerId)
    CQ->>PGC: SELECT por id, sem o cofre
    Note over CQ,PGC: outra conexão, com a role customers_app (ADR-0044)
    alt cliente não cadastrado
      H-->>B: 404 customer-not-found
    end
    H->>DB: INSERT accounts (saldo 0, limite 0, versão 0)
    H-->>B: 201 com o id da conta (UUIDv7)
    B->>DB: INSERT audit_events (201, id da conta criada), guarda a resposta com a chave e COMMIT
  end
  Note over B,DB: comando acima de 3 s (57014), com a requisição viva, vira 503 database-timeout transitório, com Retry-After: 1, ROLLBACK e chave livre (ADR-0021, ADR-0059)
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Endpoint, recusa do delegado, INSERT e operação auditada (`open-account`, recurso da conta criada) | [OpenAccount.cs](../../src/Modules/Ledger/Ledger/Features/OpenAccount/OpenAccount.cs) | ADR-0013, ADR-0041, ADR-0042 |
| Cliente existe, só pelo PostgreSQL | [CustomerQueries.cs](../../src/Modules/Customers/Customers/Infrastructure/Persistence/CustomerQueries.cs) | ADR-0058, ADR-0052 |
| Prazo de 3 s do comando e recusa `503 database-timeout` | [IdempotencyBehavior.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Idempotency/IdempotencyBehavior.cs), [AccountWrites.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/AccountWrites.cs), [LedgerProblems.cs](../../src/Modules/Ledger/Ledger/Features/LedgerProblems.cs) | ADR-0021, ADR-0059 |

O `404` vale para os dois casos e não revela se o cliente existe (ADR-0013).

## Troca do limite de crédito

`PUT /v1/accounts/{id}/credit-limit`, escopo `ledger.accounts.manage`. Único comando de escrita sem
`Idempotency-Key`: não passa pelo behavior, e a transação é do próprio handler, que grava também o
registro de auditoria da troca confirmada.

```mermaid
sequenceDiagram
  autonumber
  actor C as Client
  participant E as Endpoint
  participant H as ChangeCreditLimitHandler
  participant A as Account (agregado)
  participant DB as PostgreSQL ledger

  C->>E: PUT com creditLimit
  Note over C,E: antes do endpoint, pelo pipeline comum: 401 authentication-required, 403 insufficient-scope e, com corpo acima de 16 384 bytes, 413 payload-too-large. Toda resposta sai com Cache-Control: no-store (ADR-0057, ADR-0013, ADR-0047)
  alt id fora do formato
    E-->>C: 404 account-not-found
  else limite inválido
    E-->>C: 400 validation-error
  end
  E->>H: ChangeCreditLimitCommand
  rect rgba(80, 140, 255, 0.12)
    H->>DB: BEGIN, lock_timeout 2 s e statement_timeout 3 s
    H->>DB: SELECT conta FOR UPDATE (delegado filtra customer_id do token no próprio lock)
    alt conta inexistente ou de outro titular
      H-->>E: AccountNotFound
      E-->>C: 404 account-not-found
    else lock não obtido em 2 s (55P03)
      H-->>E: ConcurrencyTimeout
      E-->>C: 409 concurrency-timeout
    else comando acima de 3 s (57014), com a requisição viva
      H-->>E: StatementTimeout
      E-->>C: 503 database-timeout, Retry-After: 1
    end
    H->>A: ChangeCreditLimit(novo limite)
    alt saldo negativo maior que o novo limite
      H-->>E: CreditLimitBelowBalance
      E-->>C: 422 credit-limit-below-balance
    end
    H->>DB: UPDATE accounts SET credit_limit
    H->>DB: INSERT audit_events (204) e COMMIT, sem o cancelamento da requisição
    alt comando acima de 3 s (57014) no UPDATE, no registro ou no COMMIT, com a requisição viva
      H-->>E: StatementTimeout
      E-->>C: 503 database-timeout, Retry-After: 1
    end
  end
  H-->>E: sucesso
  E-->>C: 204
  Note over H,DB: recusa sai sem COMMIT, e o descarte da transação desfaz tudo (ADR-0005). Se o INSERT da auditoria falha, a troca é desfeita e a resposta é 500 internal-error (ADR-0041)
  Note over C,E: 400, 404, 409, 413, 422, 503, 401, 403, 500 e 499 ficam na trilha pelo AuditMiddleware, no fim da requisição (ADR-0041)
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Endpoint, transação do handler, recusas e registro de auditoria | [ChangeCreditLimit.cs](../../src/Modules/Ledger/Ledger/Features/ChangeCreditLimit/ChangeCreditLimit.cs) | ADR-0021, ADR-0041, ADR-0042 |
| Autenticação, escopo e limite do corpo, antes do endpoint | [IdentityExtensions.cs](../../src/BuildingBlocks/Identity/IdentityExtensions.cs), [RequestBodyLimit.cs](../../src/BuildingBlocks/Http/RequestBodyLimit.cs) | ADR-0057, ADR-0013, ADR-0047 |
| Lock, prazos do lock e do comando e filtro do titular | [AccountWrites.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/AccountWrites.cs) | ADR-0005, ADR-0013, ADR-0021 |
| Recusas, com o `Retry-After` do `503` | [LedgerProblems.cs](../../src/Modules/Ledger/Ledger/Features/LedgerProblems.cs), [WriteResult.cs](../../src/BuildingBlocks/Idempotency/WriteResult.cs) | ADR-0021, ADR-0042 |
| Regra do limite | [Account.cs](../../src/Modules/Ledger/Ledger/Domain/Account.cs) | ADR-0048 |

Não muda a versão nem gera lançamento, então não há evento para projetar.

## Do outbox ao MongoDB (Worker)

Assíncrono. O `OutboxRelay` consulta o outbox a cada `Outbox:PollInterval` (padrão 1 s) e entrega
cada evento à projeção de Positions pelo contrato `IEntryRecordedConsumer`.

```mermaid
sequenceDiagram
  autonumber
  participant R as OutboxRelay
  participant DB as PostgreSQL ledger
  participant P as PositionProjection
  participant M as MongoDB positions

  loop a cada intervalo, ou logo em seguida quando o lote veio cheio
    rect rgba(80, 140, 255, 0.12)
      R->>DB: BEGIN
      R->>DB: claim_outbox_batch(100, cursor), pendentes por id, FOR UPDATE SKIP LOCKED
      loop cada evento, em ordem de id
        R->>R: lê o EntryRecorded.v1
        alt tipo desconhecido ou payload malformado
          R->>R: log de erro só na primeira vez que o processo vê o id: o evento fica pendente e o lote segue
        end
        R->>P: ConsumeAsync(evento)
        P->>M: cria os índices únicos, até a primeira criação bem-sucedida no processo
        P->>M: versão projetada da conta (0 se não há documento)
        alt sequence menor ou igual à versão
          P-->>R: AlreadyApplied
        else sequence maior que versão + 1
          P-->>R: NotYet, espera a versão anterior
        else sequence igual a versão + 1
          P->>M: upsert do lançamento em entries (mesmo documento a cada entrega)
          alt o MongoDB recusa o documento (MongoWriteException com WriteError, como outro lançamento na mesma sequence)
            P-->>R: Rejected
            R->>R: log de erro com o id do evento, a conta e a sequence, só na primeira vez que o processo vê o id: o evento fica pendente e o lote segue
          end
          P->>M: avança positions de versão para sequence só se a versão ainda for a lida
          alt outra entrega moveu a versão entre a leitura e o avanço
            P->>M: relê a versão
            P-->>R: AlreadyApplied ou NotYet
          end
          P-->>R: Applied
        end
      end
      opt algum Applied ou AlreadyApplied
        R->>DB: mark_outbox_processed(Applied e AlreadyApplied)
      end
      R->>DB: COMMIT
    end
    alt lote cheio (100), marcado ou não
      R->>R: o próximo lote sai na hora, com o cursor depois do último id reservado, e os pendentes voltam na varredura seguinte
    else lote incompleto
      R->>R: cursor volta a 0 e espera o intervalo
    end
  end
  Note over R,M: qualquer falha no lote (reserva, consumidor, marcação ou COMMIT), como o MongoDB fora do ar ou acima do prazo de 5 s, desfaz a transação e nada fica marcado. O lote volta após espera de 1 s, 2 s, 4 s, até 30 s, e o que já foi gravado no MongoDB é regravado igual (ADR-0056)
  Note over R,DB: no encerramento do Worker, o lote em andamento roda até o COMMIT, sem o token de parada, dentro do prazo de encerramento do host (30 s). A espera entre lotes é que termina, e o Worker sai (ADR-0021)
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Polling, lote, cursor, espera crescente, evento recusado e encerramento | [OutboxRelay.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Outbox/OutboxRelay.cs) | ADR-0021, ADR-0056 |
| Funções `claim_outbox_batch` e `mark_outbox_processed` | [Migração do outbox](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/Migrations/20261031233000_Outbox.cs) | ADR-0056, ADR-0044 |
| Regra da próxima versão estrita e escrita recusada | [PositionProjection.cs](../../src/Modules/Positions/Positions/Projections/PositionProjection.cs), [IEntryRecordedConsumer.cs](../../src/Modules/Ledger/Ledger.Contracts/IEntryRecordedConsumer.cs) | ADR-0056 |
| Documentos e índices únicos no MongoDB | [ReadModelStore.cs](../../src/Modules/Positions/Positions/Infrastructure/ReadModelStore.cs), [ReadModelIndexes.cs](../../src/Modules/Positions/Positions/Projections/ReadModelIndexes.cs) | ADR-0006 |

O lançamento é gravado antes da versão. Uma queda entre os dois passos deixa a versão parada, e a
próxima entrega regrava o mesmo documento e avança. Quem lê vê só lançamentos até a versão.

## Saldo atual e saldo em um momento

`GET /v1/accounts/{id}/balance[?at=...]`, escopo `positions.read`. O PostgreSQL diz qual é a versão
da conta, e o MongoDB responde só se estiver nela. Em qualquer outro caso, a resposta sai do Ledger
com os mesmos valores. Com `at`, a leitura espera a escrita em voo na conta, e o saldo de um instante
passado não muda depois de respondido.

```mermaid
sequenceDiagram
  autonumber
  actor C as Client
  participant E as GetBalanceEndpoint
  participant FR as FreshRead
  participant LQ as ILedgerQueries
  participant DB as PostgreSQL ledger
  participant M as MongoDB positions

  C->>E: GET balance, com at opcional
  Note over C,E: antes do endpoint, pelo pipeline comum: 401 authentication-required, 403 insufficient-scope e, com corpo acima de 16 384 bytes, 413 payload-too-large. Toda resposta sai com Cache-Control: no-store (ADR-0057, ADR-0013, ADR-0047)
  alt at fora de ISO 8601 com offset, repetido ou vazio
    E-->>C: 400 validation-error
  end
  E->>FR: SnapshotAsync(id, caller, com at?)
  alt id fora do formato
    FR-->>E: inexistente, sem consulta
    E-->>C: 404 account-not-found
  end
  FR->>LQ: AccountSnapshotAsync
  alt com at
    rect rgba(80, 140, 255, 0.12)
      LQ->>DB: BEGIN, SET LOCAL lock_timeout = '2s'
      LQ->>DB: SELECT ... FOR KEY SHARE (espera a escrita em voo, e o delegado filtra o titular)
      alt espera passa de 2 s (55P03)
        LQ-->>E: ROLLBACK e AccountBusyException
        E-->>C: 409 concurrency-timeout
      end
      LQ->>DB: SELECT version, balance, clock_timestamp()
      LQ->>DB: COMMIT
    end
  else sem at
    LQ->>DB: SELECT version, balance, clock_timestamp() sem lock (delegado filtra o titular)
  end
  alt conta inexistente ou de outro titular
    FR-->>E: inexistente
    E-->>C: 404 account-not-found
  end
  alt sem at
    FR->>M: posição da conta (prazo de 300 ms)
    alt versão do MongoDB igual à do snapshot
      FR-->>E: saldo do MongoDB
    else defasado, erro ou prazo esgotado
      FR-->>E: saldo do snapshot do PostgreSQL
    end
    E-->>C: 200 saldo, at igual ao relógio do banco, versão
  else at no futuro
    E-->>C: 400 validation-error
  else at no passado
    FR->>M: versão em dia? último lançamento até at, com sequence até a versão
    alt MongoDB em dia
      FR-->>E: balanceAfter do lançamento
    else defasado, erro ou prazo esgotado
      FR->>LQ: LastEntryAtAsync no PostgreSQL, mesmos filtros
      LQ-->>FR: balanceAfter do lançamento
    end
    E-->>C: 200 saldo em at, ou zero na versão 0 se nada foi registrado até lá
  end
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Autenticação, escopo e limite do corpo, antes do endpoint | [IdentityExtensions.cs](../../src/BuildingBlocks/Identity/IdentityExtensions.cs), [RequestBodyLimit.cs](../../src/BuildingBlocks/Http/RequestBodyLimit.cs) | ADR-0057, ADR-0013, ADR-0047 |
| Endpoint, regra do `at` e truncamento ao microssegundo | [GetBalance.cs](../../src/Modules/Positions/Positions/Features/GetBalance/GetBalance.cs), [Microsecond.cs](../../src/Modules/Positions/Positions/Features/Microsecond.cs) | ADR-0002, ADR-0049, ADR-0045 |
| Escolha entre MongoDB e PostgreSQL, com prazo | [FreshRead.cs](../../src/Modules/Positions/Positions/Features/FreshRead.cs) | ADR-0045 |
| Leitura no MongoDB em microssegundos e prazo padrão de 300 ms | [ReadModelStore.cs](../../src/Modules/Positions/Positions/Infrastructure/ReadModelStore.cs), [PositionsModule.cs](../../src/Modules/Positions/Positions/PositionsModule.cs) | ADR-0006, ADR-0045 |
| Consultas do Ledger; com instante, a espera pelo lock | [LedgerQueries.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/LedgerQueries.cs), [AccountWrites.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/AccountWrites.cs) | ADR-0045, ADR-0052 |
| Recusa do lock esgotado | [AccountBusyException.cs](../../src/Modules/Ledger/Ledger.Contracts/AccountBusyException.cs), [ProblemResponses.cs](../../src/BuildingBlocks/Http/ProblemResponses.cs) | ADR-0045, ADR-0042 |
| Leitura de query string e recusa do instante no futuro | [PositionsQuery.cs](../../src/Modules/Positions/Positions/Features/PositionsQuery.cs) | ADR-0042, ADR-0045 |

O instante `at` é truncado ao microssegundo antes da comparação. O MongoDB guarda microssegundos
desde a época Unix, porque um `date` BSON só guarda milissegundos e o saldo divergiria do PostgreSQL.

## Extrato

`GET /v1/accounts/{id}/statement?from&to&limit&cursor`, escopo `positions.read`. Mesmo snapshot e
mesma escolha de fonte do saldo, aplicados a uma página. Com `to`, o snapshot espera a escrita em voo
como o saldo com `at`, e o `to` depois do relógio lido no snapshot é recusado como o `at` no futuro.

```mermaid
sequenceDiagram
  autonumber
  actor C as Client
  participant E as GetStatementEndpoint
  participant FR as FreshRead
  participant DB as PostgreSQL ledger
  participant M as MongoDB positions

  C->>E: GET statement
  Note over C,E: antes do endpoint, pelo pipeline comum: 401 authentication-required, 403 insufficient-scope e, com corpo acima de 16 384 bytes, 413 payload-too-large. Toda resposta sai com Cache-Control: no-store (ADR-0057, ADR-0013, ADR-0047)
  alt from ou to fora de ISO 8601 com offset, from depois de to, limit fora de 1 a 200, cursor inválido, parâmetro repetido ou vazio
    E-->>C: 400 validation-error
  end
  E->>FR: snapshot da conta
  alt id fora do formato
    FR-->>E: inexistente, sem consulta
    E-->>C: 404 account-not-found
  end
  alt com to
    rect rgba(80, 140, 255, 0.12)
      FR->>DB: BEGIN, SET LOCAL lock_timeout = '2s'
      FR->>DB: SELECT ... FOR KEY SHARE (espera a escrita em voo, e o delegado filtra o titular)
      alt espera passa de 2 s (55P03)
        FR-->>E: ROLLBACK e AccountBusyException
        E-->>C: 409 concurrency-timeout
      end
      FR->>DB: SELECT version, balance, clock_timestamp(): versão V e relógio
      FR->>DB: COMMIT
    end
  else sem to
    FR->>DB: SELECT version, balance, clock_timestamp() sem lock (delegado filtra o titular), versão V e relógio
  end
  alt conta inexistente ou de outro titular
    E-->>C: 404 account-not-found
  end
  alt to depois do relógio lido
    E-->>C: 400 validation-error em errors.to
  end
  E->>FR: página com sequence até V, abaixo do cursor, from inclusivo e to exclusivo arredondados para cima ao microssegundo, limit + 1 itens
  FR->>M: versão projetada (prazo de 300 ms)
  alt MongoDB na versão V
    FR->>M: entries por sequence decrescente
  else defasado, erro ou prazo esgotado
    FR->>DB: ledger.entries com os mesmos filtros
  end
  E-->>C: 200 itens e nextCursor se veio o item a mais
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Endpoint, recusa do `to` no futuro, página e cursor seguinte | [GetStatement.cs](../../src/Modules/Positions/Positions/Features/GetStatement/GetStatement.cs) | ADR-0045, ADR-0042 |
| `from` e `to` arredondados para cima ao microssegundo, até o maior instante representável | [Microsecond.cs](../../src/Modules/Positions/Positions/Features/Microsecond.cs) | ADR-0006, ADR-0045 |
| Autenticação, escopo e limite do corpo, antes do endpoint | [IdentityExtensions.cs](../../src/BuildingBlocks/Identity/IdentityExtensions.cs), [RequestBodyLimit.cs](../../src/BuildingBlocks/Http/RequestBodyLimit.cs) | ADR-0057, ADR-0013, ADR-0047 |
| Snapshot, id fora do formato sem consulta e, com instante, a espera pelo lock | [FreshRead.cs](../../src/Modules/Positions/Positions/Features/FreshRead.cs), [LedgerQueries.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/LedgerQueries.cs), [AccountWrites.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/AccountWrites.cs) | ADR-0045, ADR-0052 |
| Recusa do lock esgotado | [AccountBusyException.cs](../../src/Modules/Ledger/Ledger.Contracts/AccountBusyException.cs), [ProblemResponses.cs](../../src/BuildingBlocks/Http/ProblemResponses.cs) | ADR-0045, ADR-0042 |
| Página no MongoDB e prazo padrão de 300 ms | [ReadModelStore.cs](../../src/Modules/Positions/Positions/Infrastructure/ReadModelStore.cs), [PositionsModule.cs](../../src/Modules/Positions/Positions/PositionsModule.cs) | ADR-0006, ADR-0045 |
| Cursor opaco | [StatementCursor.cs](../../src/Modules/Positions/Positions/Features/StatementCursor.cs) | ADR-0042 |
| Leitura de query string e recusa do instante no futuro | [PositionsQuery.cs](../../src/Modules/Positions/Positions/Features/PositionsQuery.cs) | ADR-0042, ADR-0045 |

O teto em `V` faz as páginas seguintes ignorarem lançamentos novos: o extrato é um retrato da versão
lida. Um `from` no último microssegundo representável arredonda para o maior instante, que nenhum
`recordedAt` alcança, e a página sai vazia.

## Limpeza (Worker)

```mermaid
sequenceDiagram
  autonumber
  participant P as IdempotencyKeyPurge
  participant O as OutboxPurge
  participant DB as PostgreSQL ledger

  loop na subida e a cada Idempotency:PurgeInterval (padrão 1 h)
    loop lotes de 1000, até um lote apagar menos que 1000
      rect rgba(80, 140, 255, 0.12)
        P->>DB: SELECT ledger.purge_expired_idempotency_keys(1000), chaves com mais de 7 dias
      end
    end
    alt falha
      P->>P: log de erro, as chaves ficam para a rodada seguinte
    end
    P->>O: PurgeAsync, mesmo se as chaves falharam
    loop lotes de 1000, até um lote apagar menos que 1000
      rect rgba(80, 200, 120, 0.12)
        O->>DB: SELECT ledger.purge_processed_outbox(1000), processados há mais de 7 dias
      end
    end
    alt falha
      O->>O: log de erro, os eventos ficam para a rodada seguinte
    end
  end
  Note over P,DB: role ledger_worker, e cada lote confirma sozinho. As funções do migrator são o único caminho de DELETE da aplicação (ADR-0044)
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Agenda e chaves vencidas | [IdempotencyKeyPurge.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Idempotency/IdempotencyKeyPurge.cs) | ADR-0059, ADR-0044 |
| Eventos processados | [OutboxPurge.cs](../../src/Modules/Ledger/Ledger/Infrastructure/Outbox/OutboxPurge.cs) | ADR-0056, ADR-0044 |
| Funções de limpeza e prazo de 7 dias | [Migração da limpeza em lotes](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/Migrations/20261031152234_LeastPrivilegeAndBatchedPurge.cs), [migração do outbox](../../src/Modules/Ledger/Ledger/Infrastructure/Persistence/Migrations/20261031233000_Outbox.cs) | ADR-0059, ADR-0044 |
| Lotes que confirmam sozinhos | [BatchedPurge.cs](../../src/BuildingBlocks/Persistence/BatchedPurge.cs) | ADR-0044 |
