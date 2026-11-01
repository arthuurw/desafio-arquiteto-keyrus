# Fluxos de Customers

Customers guarda os dados pessoais cifrados campo a campo, cada cliente com a própria chave (DEK) no
OpenBao (ADR-0016). O PostgreSQL só tem ciphertext e o blind index do CPF. O `POST` passa antes pelo
[pipeline comum de escrita](README.md#pipeline-comum-de-escrita-post).

## Cadastro de cliente

`POST /v1/customers`, escopo `customers.write`. O endpoint valida e normaliza os quatro campos antes
do comando: nome com 1 a 150 caracteres, CPF válido com ou sem pontuação, nascimento em `yyyy-MM-dd`
de alguém com 18 anos ou mais, e-mail com até 254 caracteres. As mensagens de erro nunca repetem o
valor recebido.

```mermaid
sequenceDiagram
  autonumber
  participant B as IdempotencyBehavior
  participant H as RegisterCustomerHandler
  participant R as CustomerRegistration
  participant V as OpenBao
  participant DB as PostgreSQL customers

  B->>V: Transit HMAC customers-request-hash sobre os campos da requisição
  Note over B,V: antes do BEGIN, o hash da chave não pode ser um SHA-256 sem segredo sobre um CPF (ADR-0037)
  rect rgba(80, 140, 255, 0.12)
    B->>DB: BEGIN e reserva da chave (pipeline comum)
    B->>H: RegisterCustomerCommand
    H->>H: Customer.Register, id UUIDv7, dados em claro só na memória
    H->>R: RegisterAsync(customer)
    R->>V: Transit HMAC customers-blind-index sobre os 11 dígitos do CPF
    R->>DB: existe linha com este cpf_blind_index?
    alt CPF já cadastrado
      R-->>H: CpfAlreadyRegistered, vira 409 cpf-already-registered sem revelar de quem é o CPF
    end
    R->>R: DEK nova, 32 bytes aleatórios
    R->>V: KV customers/data/dek/{id} com cas=0, nunca sobrescreve
    R->>R: AES-256-GCM em nome, CPF, nascimento e e-mail, AAD igual a id e campo
    R->>DB: INSERT customers (quatro ciphertexts, blind index e versão da chave)
    alt índice único pega a corrida do CPF
      DB-->>R: 23505, o savepoint desfaz só o INSERT
      R-->>H: CpfAlreadyRegistered, vira 409 cpf-already-registered
    end
    R->>R: zera a DEK na memória
    H-->>B: 201 só com o id
    B->>DB: guarda a resposta com a chave e COMMIT
  end
  Note over B,DB: cofre fora ou lento (1 s) em qualquer passo vira 503 personal-data-unavailable com Retry-After 5, ROLLBACK e chave livre
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Endpoint e validação dos campos | [RegisterCustomer.cs](../../src/Modules/Customers/Customers/Features/RegisterCustomer/RegisterCustomer.cs), [CustomersInput.cs](../../src/Modules/Customers/Customers/Features/CustomersInput.cs) | ADR-0030, ADR-0039 |
| Hash da requisição no Transit e tratamento do cofre fora | [IdempotencyBehavior.cs](../../src/Modules/Customers/Customers/Infrastructure/Idempotency/IdempotencyBehavior.cs) | ADR-0037 |
| Blind index, DEK e INSERT | [CustomerRegistration.cs](../../src/Modules/Customers/Customers/Infrastructure/Persistence/CustomerRegistration.cs) | ADR-0016 |
| Cifra campo a campo | [CustomerSeal.cs](../../src/Modules/Customers/Customers/Infrastructure/Persistence/CustomerSeal.cs), [FieldCipher.cs](../../src/Modules/Customers/Customers/Infrastructure/Crypto/FieldCipher.cs) | ADR-0016 |
| HMAC no Transit e DEK no KV | [IBlindIndexer.cs](../../src/Modules/Customers/Customers/Infrastructure/Vault/IBlindIndexer.cs), [IKeyStore.cs](../../src/Modules/Customers/Customers/Infrastructure/Vault/IKeyStore.cs) | ADR-0016 |

A DEK vai para o cofre antes da linha. Uma falha entre os dois deixa no máximo uma DEK sem linha,
nunca uma linha sem chave. A resposta guardada com a chave de idempotência tem só o id, sem dado
pessoal (ADR-0037).

Formato de cada campo cifrado: `0x01 | nonce (12 bytes) | ciphertext | tag (16 bytes)`. A AAD
`{customerId}|{campo}` impede que um ciphertext copiado de outro cliente ou de outro campo decifre.

## Leitura mascarada e leitura completa

Duas rotas sobre o mesmo `CustomerReader`:

| Rota | Escopo | Resposta |
|------|--------|----------|
| `GET /v1/customers/{id}` | `customers.read` | Mascarada: `Ana M. S.`, `***.982.247-**`, `****-**-15`, `a***@dominio` |
| `GET /v1/customers/{id}/personal-data` | `customers.pii.read` | Completa, com CPF formatado, e um registro de auditoria por acesso |

```mermaid
sequenceDiagram
  autonumber
  actor C as Client
  participant E as Endpoint
  participant RD as CustomerReader
  participant DB as PostgreSQL customers
  participant K as CustomerKeys (cache de DEK)
  participant V as OpenBao
  participant AU as Log de auditoria

  C->>E: GET cliente
  E->>RD: ReadAsync(id, caller)
  alt id fora do formato, ou delegado lendo outro cliente
    RD-->>E: 404 customer-not-found, sem tocar o banco
  end
  RD->>DB: SELECT linha por id
  alt linha inexistente
    RD-->>E: 404 customer-not-found
  end
  RD->>K: DEK do cliente
  alt DEK no cache, lida do cofre há menos de 5 min
    K-->>RD: cópia da DEK
  else fora do cache
    K->>V: KV customers/data/dek/{id}
    alt cofre fora ou lento
      RD-->>E: 503 personal-data-unavailable com Retry-After 5
    end
    K-->>RD: DEK, que entra no cache
  end
  RD->>RD: decifra os quatro campos com a AAD de cada um
  alt algum campo não decifra
    RD-->>E: 500 internal-error, log só com id e nome do campo
  end
  RD->>RD: zera a DEK na memória
  alt rota personal-data
    E->>AU: client, titular do token, cliente consultado, status e traceId
    Note over E,AU: registrado em todo acesso, com sucesso ou recusa, só com identificadores
    E-->>C: 200 dados completos
  else rota mascarada
    E-->>C: 200 dados mascarados
  end
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Endpoints, leitor e auditoria | [ReadCustomer.cs](../../src/Modules/Customers/Customers/Features/ReadCustomer/ReadCustomer.cs) | ADR-0013, ADR-0039 |
| Cache de DEK com prazo contado da leitura no cofre | [DekCache.cs](../../src/Modules/Customers/Customers/Infrastructure/Vault/DekCache.cs) | ADR-0016 |
| Máscaras | [CustomerMask.cs](../../src/Modules/Customers/Customers/Domain/CustomerMask.cs) | ADR-0039 |
| Catálogo de recusas | [CustomersProblems.cs](../../src/Modules/Customers/Customers/Features/CustomersProblems.cs) | ADR-0039 |

## Acesso ao OpenBao

Um cliente HTTP único para a Api, com timeout de 1 s por chamada:

1. Login por AppRole na primeira chamada. O token fica em memória e é renovado 1 min antes de vencer.
2. Resposta `403` com token em uso: um login novo e uma segunda tentativa. Se ainda assim vier `403`,
   é recusa da política e sobe como erro.
3. Timeout, qualquer outro status de erro (fora `400`, `403` e `404`) ou resposta incompleta viram `PersonalDataUnavailableException`, que os fluxos
   acima traduzem em `503 personal-data-unavailable`.

Código: [OpenBaoClient.cs](../../src/Modules/Customers/Customers/Infrastructure/Vault/OpenBaoClient.cs).
Endereço e credenciais são lidos na subida: sem eles, a Api não sobe.

O Ledger confirma que um cliente existe por `ICustomerQueries.ExistsAsync`, que consulta só o
PostgreSQL. Com o cofre fora, a abertura de conta continua funcionando (ADR-0016).

## Limpeza (Worker)

Na subida e a cada `Idempotency:PurgeInterval` (padrão 1 h), o Worker apaga em lotes as chaves de
idempotência de Customers com mais de 7 dias, com a role `customers_worker` (ADR-0037, ADR-0038).
Código: [IdempotencyKeyPurge.cs](../../src/Modules/Customers/Customers/Infrastructure/Idempotency/IdempotencyKeyPurge.cs).
