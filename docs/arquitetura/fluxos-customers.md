# Fluxos de Customers

Customers guarda os dados pessoais cifrados campo a campo, cada cliente com a própria chave (DEK) no
OpenBao (ADR-0016). O PostgreSQL só tem ciphertext e o blind index do CPF. O `POST` passa antes pelo
[pipeline comum de escrita](README.md#pipeline-comum-de-escrita-post).

## Cadastro de cliente

`POST /v1/customers`, escopo `customers.write`. O endpoint valida e normaliza os quatro campos antes
do comando: nome com 1 a 150 caracteres, CPF válido com ou sem pontuação, nascimento em `yyyy-MM-dd`,
a partir de 1900-01-01, de alguém com 18 anos ou mais na data de hoje em UTC, e-mail com uma `@` e
domínio com ponto, até 254 caracteres. As mensagens de erro nunca repetem o valor recebido.

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
    R->>DB: INSERT customers (quatro ciphertexts, blind index do CPF e versão da chave do HMAC)
    alt índice único pega a corrida do CPF
      DB-->>R: 23505, o savepoint desfaz só o INSERT
      R-->>H: CpfAlreadyRegistered, vira 409 cpf-already-registered
    end
    R->>R: zera a DEK na memória
    H-->>B: 201 só com o id
    B->>DB: guarda a resposta com a chave e COMMIT
  end
  Note over B,DB: cofre fora ou lento (1 s) em qualquer passo vira 503 personal-data-unavailable com Retry-After 5, ROLLBACK e chave livre (ADR-0021, ADR-0037)
  Note over B,DB: recusa do cofre (400, 404 ou 403 que persiste) vira 500 internal-error pelo tratador global, também com ROLLBACK e chave livre (ADR-0039)
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
      V-->>K: falha
      K-->>RD: PersonalDataUnavailableException
      RD-->>E: 503 personal-data-unavailable com Retry-After 5
    else DEK ausente ou recusa da política
      V-->>K: 404, 400 ou 403 que persiste
      K-->>RD: InvalidOperationException
      RD-->>E: a exceção sobe sem passar pela auditoria
      E-->>C: 500 internal-error pelo tratador global
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
    Note over E,AU: registrado quando a leitura termina (200, 404, 503 ou 500 de integridade), só com identificadores (ADR-0039)
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

Um cliente HTTP único para a Api, com timeout de 1 s por chamada. Endereço e credenciais são lidos
na subida: sem eles, a Api não sobe.

```mermaid
sequenceDiagram
  autonumber
  participant U as Quem chama (cadastro, leitura, hash)
  participant OB as OpenBaoClient
  participant V as OpenBao

  U->>OB: SendAsync(método, caminho, corpo)
  alt sem token, ou token a 1 min de vencer (metade da vida se o TTL é menor que 2 min)
    OB->>V: login AppRole (role_id e secret_id)
    V-->>OB: token e lease, guardados em memória
  end
  OB->>V: chamada com o token
  alt 403 com o token em uso
    OB->>V: login AppRole de novo
    OB->>V: segunda tentativa
  end
  alt 2xx
    OB-->>U: corpo da resposta
  else 404
    OB-->>U: nada, e quem chama trata a DEK ou chave ausente como erro interno
  else 400, ou 403 que persiste
    OB-->>U: InvalidOperationException, vira 500 internal-error
  else timeout de 1 s, outro status de erro ou resposta incompleta
    OB-->>U: PersonalDataUnavailableException, vira 503 personal-data-unavailable
  end
  Note over OB,V: 400 aparece, por exemplo, num cas=0 sobre uma DEK que já existe (ADR-0016, ADR-0021)
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Login, renovação, nova tentativa e tradução dos status | [OpenBaoClient.cs](../../src/Modules/Customers/Customers/Infrastructure/Vault/OpenBaoClient.cs) | ADR-0016, ADR-0021 |
| DEK ausente como erro interno | [IKeyStore.cs](../../src/Modules/Customers/Customers/Infrastructure/Vault/IKeyStore.cs), [IBlindIndexer.cs](../../src/Modules/Customers/Customers/Infrastructure/Vault/IBlindIndexer.cs) | ADR-0016, ADR-0039 |

O Ledger confirma que um cliente existe por `ICustomerQueries.ExistsAsync`, que consulta só o
PostgreSQL. Com o cofre fora, a abertura de conta continua funcionando (ADR-0016).

## Clientes de desenvolvimento (Api)

Rotina da Api, ligada só quando `Customers:Seed:Enabled` é verdadeiro (o compose liga). Cadastra os
clientes dos usuários `ana` e `bruno` do realm local, com dados fictícios e os mesmos `customer_id`.

```mermaid
sequenceDiagram
  autonumber
  participant S as CustomerSeed
  participant DB as PostgreSQL customers
  participant V as OpenBao
  participant R as CustomerRegistration

  loop até 30 tentativas
    loop cada cliente fixo (ana, bruno)
      S->>DB: a linha já existe?
      alt já existe
        S->>S: passa ao próximo, subir de novo não duplica
      else não existe
        S->>V: DEK do cliente no KV
        alt DEK encontrada (banco recriado, cofre mantido)
          S->>R: RegisterAsync com a DEK de antes, sem gravar outra no KV
        else sem DEK
          S->>R: RegisterAsync com DEK nova, como no POST
        end
        R->>DB: blind index, cifra e INSERT, que confirma sozinho
        R-->>S: Registered ou CpfAlreadyRegistered, para o log
      end
    end
    alt cofre fora em qualquer passo
      S->>S: espera 2 s e tenta de novo, sem derrubar a Api
    else outro erro
      S->>S: log de erro e fim da rotina
    end
  end
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Tentativas, clientes fixos e DEK reaproveitada | [CustomerSeed.cs](../../src/Modules/Customers/Customers/Infrastructure/Seed/CustomerSeed.cs) | ADR-0016 |
| Cadastro sem chave de idempotência | [CustomerRegistration.cs](../../src/Modules/Customers/Customers/Infrastructure/Persistence/CustomerRegistration.cs) | ADR-0016 |

## Limpeza (Worker)

```mermaid
sequenceDiagram
  autonumber
  participant P as IdempotencyKeyPurge
  participant DB as PostgreSQL customers

  loop na subida e a cada Idempotency:PurgeInterval (padrão 1 h)
    loop lotes de 1000, até um lote apagar menos que 1000
      P->>DB: SELECT customers.purge_expired_idempotency_keys(1000), chaves com mais de 7 dias
    end
    alt falha
      P->>P: log de erro, as chaves ficam para a rodada seguinte
    end
  end
  Note over P,DB: role customers_worker, e cada lote confirma sozinho (ADR-0038)
```

| Etapa | Código | Decisão |
|-------|--------|---------|
| Agenda e chaves vencidas | [IdempotencyKeyPurge.cs](../../src/Modules/Customers/Customers/Infrastructure/Idempotency/IdempotencyKeyPurge.cs) | ADR-0037, ADR-0038 |
| Lotes que confirmam sozinhos | [BatchedPurge.cs](../../src/BuildingBlocks/Persistence/BatchedPurge.cs) | ADR-0038 |
