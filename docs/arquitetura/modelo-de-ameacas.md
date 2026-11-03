# Modelo de ameaças

Responde à pergunta do enunciado "Como proteger dados sensíveis dos clientes". Cada mitigação foi
conferida no código e aponta o ADR que a decide e o teste que a prova. O que está decidido e não
implementado aparece marcado assim, com o ADR. O código prevalece: uma linha que diverge dele está
desatualizada.

## Ativos

| Ativo | Onde vive | Proteção principal |
|-------|-----------|--------------------|
| Dinheiro: lançamentos e saldo | `ledger.entries` e `ledger.accounts` no PostgreSQL; projeção de saldo e extrato no MongoDB | Privilégio de banco, `CHECK`, lock da conta, idempotência |
| Dados pessoais: nome, CPF, data de nascimento e e-mail | Só em `customers.customers`, cifrados campo a campo. Ledger e Positions guardam só ids opacos | AES-256-GCM com DEK por cliente, blind index, máscara, redação de log |
| Chaves | DEK de cada cliente no KV v2 e chaves de HMAC no Transit, ambos no OpenBao | AppRole com política mínima; chaves do Transit não exportáveis |
| Trilha de auditoria | `ledger.audit_events` e `customers.audit_events`, só com identificadores | Só `INSERT` para a Api; trigger que recusa alteração; DDL no log do servidor |

## Fronteiras de confiança

| Fronteira | Credencial | Alcance |
|-----------|------------|---------|
| Client → Api | JWT do Keycloak: client credentials (modo serviço) ou token exchange com a claim `customer_id` (modo delegado) | Um escopo exato por rota; no modo delegado, só os recursos do próprio cliente |
| Api → PostgreSQL | Roles `ledger_app` e `customers_app` | Cada uma só no próprio schema, com DML sem `DELETE` |
| Api → OpenBao | AppRole `customers-api` | Criar e ler DEK; calcular os dois HMACs; nada mais |
| Api → MongoDB | Usuário `positions_reader` | Só leitura do banco `positions` |
| Worker → PostgreSQL e MongoDB | Roles `ledger_worker` e `customers_worker`; usuário `positions_writer` | Sem privilégio de tabela no PostgreSQL, só `EXECUTE` nas funções `SECURITY DEFINER` da própria rotina; único que grava no MongoDB |
| Migrator → PostgreSQL | Role `migrator` | Dona dos schemas, sem privilégio administrativo |

## Ameaças e mitigações

Método STRIDE: **S** falsificação de identidade, **T** adulteração, **R** repúdio, **I** vazamento
de informação, **D** negação de serviço, **E** elevação de privilégio. Os caminhos de prova são
relativos a `tests/`.

| Ameaça | Cenário concreto | Mitigação implementada | ADR | Prova |
|--------|------------------|------------------------|-----|-------|
| S | Token com assinatura forjada, de outro emissor, emitido para outra API (`aud`), assinado em HS256 com a chave pública, ou expirado | JWT Bearer com `aud` `ledger-api`, emissor comparado de forma exata, só RS256, `exp` obrigatório e tolerância de 30 s. Tokens de 5 min no realm. Toda causa recebe o mesmo `401` `authentication-required`, sem dizer qual validação falhou | [0012](../adr/0012-autenticacao-oauth2-client-credentials.md) | `IntegrationTests/Identity/AuthenticationTests.cs` |
| S | Api fora de Development lendo os metadados do emissor ou falando com o OpenBao por http | A Api não sobe: `Identity:MetadataAddress` e `OpenBao:Address` exigem https fora de Development | [0012](../adr/0012-autenticacao-oauth2-client-credentials.md), [0050](../adr/0050-criptografia-de-dados-pessoais-com-seed-de-desenvolvimento.md) | `IntegrationTests/Hosts/ApiStartupTests.cs` |
| E | Rota nova mapeada sem política de autorização | Política padrão nega tudo: `401` sem token, `403` com qualquer token. Teste de arquitetura exige política em todo endpoint | [0013](../adr/0013-autorizacao-por-escopo-e-por-recurso.md) | `ArchitectureTests/EndpointPolicyTests.cs`, `IntegrationTests/Identity/AuthorizationTests.cs` |
| E | Client com `ledger.credits.write` (o `payments-hub`) tenta debitar | Crédito e débito em rotas e escopos separados; o escopo é comparado de forma exata. Resposta `403` `insufficient-scope`, sem linha gravada. O realm dá a cada client exatamente os seus escopos | [0013](../adr/0013-autorizacao-por-escopo-e-por-recurso.md), [0042](../adr/0042-design-da-api-com-trilha-de-auditoria.md) | `IntegrationTests/Ledger/LedgerScopeTests.cs`, `IntegrationTests/Identity/KeycloakRealmTests.cs` |
| E, I | BOLA: usuário do canal (modo delegado) chama a conta, o saldo ou o cadastro de outro cliente | `Caller.CanAccessCustomer`. No Ledger, o filtro de titular vai no próprio `SELECT ... FOR UPDATE`: a conta alheia não é travada e responde `404` `account-not-found`, como uma inexistente. Customers responde `404` `customer-not-found` sem consultar o banco. O canal não recebe escopos por client credentials, e o usuário não altera o próprio `customer_id`. Claims ambíguas não resolvem chamador: `403` | [0013](../adr/0013-autorizacao-por-escopo-e-por-recurso.md) | `IntegrationTests/Ledger/OwnershipTests.cs`, `IntegrationTests/Customers/CustomerOwnershipTests.cs`, `IntegrationTests/Positions/PositionsAccessTests.cs`, `IntegrationTests/Identity/KeycloakRealmTests.cs`, `IntegrationTests/Identity/CallerTests.cs` |
| T | Repetição de um débito, por retry de rede ou de propósito | `Idempotency-Key` obrigatória em todo `POST`, única por `(client_id, chave)` e gravada na transação da operação. A repetição devolve a resposta original com `Idempotent-Replayed: true`. Mesma chave com outro conteúdo ou outro titular: `422` `idempotency-key-reused`. `external_reference` única por client: `409` `duplicate-external-reference` | [0037](../adr/0037-idempotencia-por-modulo-com-hash-sem-dado-pessoal.md) | `IntegrationTests/Ledger/IdempotentReplayTests.cs`, `IntegrationTests/Ledger/ExternalReferenceTests.cs`, `IntegrationTests/Customers/CustomerIdempotencyTests.cs` |
| I | A linha da chave de idempotência do cadastro vira oráculo para testar CPFs | Em Customers, o `request_hash` é HMAC-SHA256 do Transit, com a chave não exportável `customers-request-hash`, e a resposta guardada tem só o id. No Ledger é SHA-256: os valores são financeiros, não pessoais | [0037](../adr/0037-idempotencia-por-modulo-com-hash-sem-dado-pessoal.md) | `IntegrationTests/Customers/CustomerIdempotencyTests.cs` |
| T | Api comprometida ou SQL injection tenta reescrever ou apagar lançamentos | `ledger_app` tem só `INSERT` e `SELECT` em `ledger.entries`; nenhuma role de aplicação tem `DELETE` ou `TRUNCATE`; em `accounts`, `UPDATE` só nas colunas de saldo, limite, versão e instante. `CHECK` de valor positivo, de tipo (`credit` ou `debit`) e de `balance >= -credit_limit`. Sequência única por conta e um estorno por lançamento. Correção só por estorno, que é outro lançamento | [0044](../adr/0044-isolamento-por-schema-e-role-com-ddl-no-log.md), [0049](../adr/0049-saldo-materializado-e-saldo-acumulado-com-sinal-pelo-tipo.md) | `IntegrationTests/Ledger/LedgerTablesTests.cs`, `IntegrationTests/Database/DatabaseRolesTests.cs` |
| R, T | Quem operou nega a operação, ou apaga o registro dela | Toda escrita e as duas leituras de cliente geram uma linha, inclusive `401`, `403`, `413`, `499` e `500`: client, cliente do token, operação, recurso, status e `traceId`, sem dado pessoal. A escrita confirmada grava a linha na própria transação; se o registro falha, a escrita é desfeita. A Api só insere; um trigger recusa `UPDATE`, `DELETE` e `TRUNCATE` de qualquer role, inclusive `migrator` e superusuário. O PostgreSQL loga todo DDL com usuário e banco. **Residual:** a dona do schema e o superusuário ainda desligam o trigger por DDL; só o log do servidor denuncia | [0041](../adr/0041-trilha-de-auditoria-por-modulo.md), [0044](../adr/0044-isolamento-por-schema-e-role-com-ddl-no-log.md) | `IntegrationTests/Database/AuditTrailTests.cs`, `IntegrationTests/Ledger/WriteAuditTests.cs`, `ci/compose-ddl-log.sh` |
| I | Dump, backup ou SQL injection expõe a tabela de clientes | AES-256-GCM campo a campo, nonce aleatório por operação. DEK de 32 bytes por cliente no OpenBao, nunca no banco. AAD `{customerId}\|{campo}`: o ciphertext copiado para outro cliente ou campo não decifra, e a leitura responde `500` com só o id e o nome do campo no log. A política do AppRole não tem `update` nem `delete` na DEK | [0050](../adr/0050-criptografia-de-dados-pessoais-com-seed-de-desenvolvimento.md) | `IntegrationTests/Customers/CustomerStorageTests.cs`, `IntegrationTests/Customers/CustomerIntegrityTests.cs`, `Customers.UnitTests/FieldCipherTests.cs`, `IntegrationTests/Customers/VaultTests.cs` |
| I | Quem tem o dump enumera CPFs calculando o hash de cada um | Blind index é HMAC-SHA256 dos 11 dígitos pelo Transit, com a chave não exportável `customers-blind-index`: sem o cofre, não há como calcular. O `409` `cpf-already-registered` não traz o id do cliente existente | [0050](../adr/0050-criptografia-de-dados-pessoais-com-seed-de-desenvolvimento.md) | `IntegrationTests/Customers/BlindIndexTests.cs`, `IntegrationTests/Customers/DuplicateCpfTests.cs` |
| I | Dado pessoal escrito em log | Parâmetro marcado `[PersonalData]` sai apagado pela redação do framework, na Api e no Worker. Teste de arquitetura exige a marca; teste de integração procura nome, CPF e e-mail nos logs dos cenários. Erro não tratado vira `500` `internal-error` com `traceId`, e a exceção vai só para o log | [0017](../adr/0017-lgpd-minimizacao-direitos-e-bloqueio.md), [0041](../adr/0041-trilha-de-auditoria-por-modulo.md) | `ArchitectureTests/PersonalDataLogTests.cs`, `IntegrationTests/Hosts/LogRedactionTests.cs`, `IntegrationTests/Customers/CustomerLogTests.cs` |
| I | Client que só precisa identificar o cliente lê os dados completos | Duas rotas e dois escopos: `GET /v1/customers/{id}` (`customers.read`) devolve mascarado; `GET /v1/customers/{id}/personal-data` (`customers.pii.read`, só o `compliance`) devolve completo, e só depois de gravar o acesso na trilha. As duas leituras ficam na trilha | [0013](../adr/0013-autorizacao-por-escopo-e-por-recurso.md), [0041](../adr/0041-trilha-de-auditoria-por-modulo.md) | `IntegrationTests/Customers/CustomerReadTests.cs`, `IntegrationTests/Customers/CustomersScopeTests.cs`, `IntegrationTests/Customers/MaskedReadAuditTests.cs`, `IntegrationTests/Customers/PersonalDataAuditTests.cs` |
| E | Código de um módulo lê as tabelas de outro, ou o Ledger passa a guardar dado pessoal | Schema e role por módulo: `ledger_app` é recusada no schema `customers`, e o inverso. O Ledger consulta Customers só pelo contrato `ICustomerQueries`. Testes de arquitetura barram tipos internos de outro módulo e dado pessoal fora de Customers | [0044](../adr/0044-isolamento-por-schema-e-role-com-ddl-no-log.md), [0052](../adr/0052-monolito-modular-com-identidade-do-chamador-isolada.md), [0017](../adr/0017-lgpd-minimizacao-direitos-e-bloqueio.md) | `IntegrationTests/Database/DatabaseRolesTests.cs`, `ArchitectureTests/ModuleBoundaryTests.cs`, `ArchitectureTests/PersonalDataBoundaryTests.cs` |
| T | Api comprometida grava no modelo de leitura | A Api usa `positions_reader`, só leitura; gravar ou criar índice é recusado. Só o Worker grava, com `positions_writer` | [0053](../adr/0053-relay-do-outbox-e-projecao-pela-proxima-versao.md) | `IntegrationTests/Positions/MongoSchemaTests.cs` |
| D | Corpo enorme para gastar memória e CPU do parser | Corpo acima de 16 KB recebe `413` `payload-too-large` antes de ler o JSON, também sem `Content-Length`. Vem depois da autorização, não consome a chave de idempotência e fica na trilha | [0047](../adr/0047-limite-de-16-kb-no-corpo-da-requisicao.md) | `IntegrationTests/Ledger/BodyLimitTests.cs`, `IntegrationTests/Customers/BodyLimitTests.cs` |
| D | Cofre fora do ar derruba a Api inteira | Só Customers depende do OpenBao: cadastro e leitura respondem `503` `personal-data-unavailable` com `Retry-After`, sem consumir a chave; Ledger e Positions seguem | [0050](../adr/0050-criptografia-de-dados-pessoais-com-seed-de-desenvolvimento.md), [0037](../adr/0037-idempotencia-por-modulo-com-hash-sem-dado-pessoal.md) | `IntegrationTests/Customers/VaultOutageTests.cs` |
| I | Segredo real no repositório, ou serviço local exposto na rede | O repositório só tem segredos terminados em `_dev`, exclusivos do ambiente local (realm, scripts de init e `docker-compose.yml`). Portas publicadas só em `127.0.0.1`. Contrato OpenAPI só em Development | [0012](../adr/0012-autenticacao-oauth2-client-credentials.md), [0044](../adr/0044-isolamento-por-schema-e-role-com-ddl-no-log.md), [0042](../adr/0042-design-da-api-com-trilha-de-auditoria.md) | `ci/compose-ports.sh`, `IntegrationTests/Hosts/OpenApiTests.cs` |

## Riscos aceitos

- **`409` `cpf-already-registered` diz se um CPF já é cliente.** Quem tem `customers.write` descobre,
  pela recusa, que o CPF está cadastrado. A unicidade do CPF exige a recusa. Mitigações: o escopo é
  só do `backoffice`, a recusa não traz o id, e toda tentativa fica na trilha com o client. Rate
  limiting por client: decidido, não implementado (ADR-0021), fora desta entrega pelo
  [ADR-0054](../adr/0054-escopo-final-da-entrega.md).
- **Sem rate limiting nem bulkhead.** Um client autenticado pode gerar volume sem limite, e um pico
  de leitura disputa conexões com a escrita. Decidido, não implementado
  ([ADR-0021](../adr/0021-resiliencia-sob-carga-e-falhas.md)), fora desta entrega pelo ADR-0054.
- **Sem mTLS.** O token é portador: quem o vaza usa até ele expirar, em no máximo 5 min. No
  ambiente local, o tráfego entre contêineres é http.
- **OpenBao de nó único com seal estático.** A chave de deslacre vem de variável de ambiente, e
  perder o volume do cofre perde as DEKs: os dados pessoais ficam ilegíveis. Desenho exclusivo do
  ambiente local ([ADR-0050](../adr/0050-criptografia-de-dados-pessoais-com-seed-de-desenvolvimento.md)).
- **Trilha alterável pela dona do schema.** `migrator` e o superusuário desligam o trigger por DDL;
  o `log_statement=ddl` registra quem fez, mas não impede
  ([ADR-0044](../adr/0044-isolamento-por-schema-e-role-com-ddl-no-log.md)).
- **Saldo e extrato fora da trilha.** A trilha cobre escritas e leituras de cliente; consultas de
  `positions.read` não geram linha ([ADR-0013](../adr/0013-autorizacao-por-escopo-e-por-recurso.md)).
- **Eliminação e bloqueio.** Pedido de eliminação, bloqueio durante a guarda legal e
  crypto-shredding: decidido, não implementado
  ([ADR-0017](../adr/0017-lgpd-minimizacao-direitos-e-bloqueio.md)). O escopo
  `customers.erasure.manage` existe no realm, sem rota que o use.
- **Sem análise estática de segurança.** CodeQL no CI: decidido, não implementado
  ([ADR-0055](../adr/0055-qualidade-de-build-com-auditoria-no-ci.md)), fora desta entrega pelo
  ADR-0054.

## Fora do escopo

Evoluções registradas em [melhorias-futuras.md](../melhorias-futuras.md):

- mTLS entre serviços e tokens vinculados a certificado (RFC 8705).
- Cofre gerenciado ou cluster OpenBao em alta disponibilidade.
- Trilha protegida também da dona do schema, por event trigger do superusuário ou cópia em
  armazenamento WORM.
- Consulta da trilha por role só de leitura ou rota própria, sem o `migrator`.
- Limpeza da trilha ao fim do prazo legal.
