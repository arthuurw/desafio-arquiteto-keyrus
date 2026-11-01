# Ledger

Ledger bancário em C#: registro de movimentações e consulta de saldo em um momento.

## Como executar

Pré-requisitos: Docker com Docker Compose; `curl` e `jq` para os exemplos.

```bash
docker compose up
```

O comando sobe o PostgreSQL e o MongoDB (modelo de leitura do saldo e do extrato), aplica as
migrações e inicia a Api (`http://localhost:8080`), o Worker (`http://localhost:8081`), o Keycloak
(`http://localhost:8180`), provedor de identidade local com o realm `ledger` importado de
`docker/keycloak/realm-ledger.json`, e o OpenBao (`http://localhost:8200`), cofre das chaves dos
dados pessoais dos clientes (ADR-0016). O Worker leva cada lançamento do outbox do PostgreSQL ao
MongoDB (ADR-0035) e apaga as chaves de idempotência e os eventos processados com mais de 7 dias.

O OpenBao sobe em modo servidor, com os dados no volume `openbao-data` e auto-unseal estático: ele
se deslacra sozinho a cada subida. Na primeira, o container `openbao-bootstrap` inicializa o cofre
e cria as chaves, a política e o AppRole da Api; nas seguintes, ele não troca nenhuma chave. A Api
só sobe depois do bootstrap e cadastra, cifrados como qualquer cliente, os clientes fictícios dos
usuários `ana` (`11111111-1111-4111-8111-111111111111`) e `bruno`
(`22222222-2222-4222-8222-222222222222`) do realm (seed ligado por `Customers__Seed__Enabled`, só no
compose). Uma conta só é aberta para cliente cadastrado.
Para conferir: `curl http://localhost:8080/health/live` e `curl http://localhost:8081/health/live`
respondem `Healthy`.

### Depois de atualizar o repositório

As roles do banco são criadas pelo script de inicialização do PostgreSQL
(`docker/postgres/init/`), e os usuários do MongoDB pelo de `docker/mongo/init/`; os dois só rodam
com o volume vazio. Uma role ou um usuário novo, como a `customers_worker` do Worker, não chega a um
volume que já existe. Depois de atualizar o repositório, recrie o
ambiente local, o que apaga os dados locais:

```bash
docker compose down -v
docker compose up --build
```

O `--build` recompila a Api, o Worker e o Migrator com o código atualizado; sem ele, o compose
reaproveita as imagens já construídas.

**Nunca recrie só o volume do OpenBao (`openbao-data`) com clientes cadastrados no PostgreSQL.** As
DEKs vivem só no cofre: sem elas, os dados pessoais gravados no banco ficam ilegíveis para sempre.
O `docker compose down -v` acima apaga os dois volumes juntos, e o seed cadastra a `ana` e o
`bruno` de novo.

### Portas

Todas as portas são publicadas só em `127.0.0.1`. A porta do host pode ser trocada por variável
de ambiente:

| Variável | Padrão | Serviço |
|----------|--------|---------|
| `POSTGRES_PORT` | 5432 | PostgreSQL |
| `MONGO_PORT` | 27017 | MongoDB |
| `KEYCLOAK_PORT` | 8180 | Keycloak |
| `OPENBAO_PORT` | 8200 | OpenBao |
| `API_PORT` | 8080 | Api |
| `WORKER_PORT` | 8081 | Worker |

Exemplo, com outro PostgreSQL já usando a 5432: `POSTGRES_PORT=15432 docker compose up`.

### Contrato da Api

Em Development, que é como o compose sobe a Api, o documento OpenAPI fica em
`http://localhost:8080/openapi/v1.json` e a referência navegável do Scalar em
`http://localhost:8080/scalar`, sem token. Fora de Development, essas rotas não existem e respondem
`401` ou `403`, como qualquer rota sem política. A cópia versionada do documento está em
[`docs/api/openapi-v1.json`](docs/api/openapi-v1.json).

### Tokens

Toda rota de negócio da Api exige um token do Keycloak com o escopo da rota (ADR-0012, ADR-0013):

| Escopo | Rotas | Clients |
|--------|-------|---------|
| `ledger.accounts.manage` | `POST /v1/accounts`, `PUT /v1/accounts/{id}/credit-limit` | `backoffice` |
| `ledger.credits.write` | `POST /v1/accounts/{id}/credits` | `payments-hub` |
| `ledger.debits.write` | `POST /v1/accounts/{id}/debits` | `channel-app` (delegado) |
| `ledger.entries.reverse` | `POST /v1/accounts/{id}/entries/{entryId}/reversal` | `backoffice` |
| `positions.read` | `GET /v1/accounts/{id}/balance`, `GET /v1/accounts/{id}/statement` | `backoffice`, `compliance`, `channel-app` (delegado) |
| `customers.write` | `POST /v1/customers` | `backoffice` |
| `customers.read` | `GET /v1/customers/{id}` (dados mascarados) | `backoffice`, `channel-app` (delegado) |
| `customers.pii.read` | `GET /v1/customers/{id}/personal-data` (dados completos, com log de auditoria) | `compliance` |

No modo delegado, o chamador só alcança o cliente e as contas do `customer_id` do usuário; os
demais respondem `404`.

Modo serviço, por client credentials (`backoffice`, `compliance` ou `payments-hub`):

```bash
curl -s http://localhost:8180/realms/ledger/protocol/openid-connect/token \
  -d grant_type=client_credentials -d client_id=payments-hub -d client_secret=payments-hub_dev
```

Modo delegado: o usuário entra no `channel-web` por senha (habilitado só neste realm local), e o
`channel-app` troca esse token por um destinado à Api. A Api só aceita o token trocado:

```bash
token=$(curl -s http://localhost:8180/realms/ledger/protocol/openid-connect/token \
  -d grant_type=password -d client_id=channel-web \
  -d username=ana -d password=ana_dev | jq -r .access_token)
curl -s http://localhost:8180/realms/ledger/protocol/openid-connect/token \
  -d grant_type=urn:ietf:params:oauth:grant-type:token-exchange \
  -d client_id=channel-app -d client_secret=channel-app_dev \
  -d subject_token_type=urn:ietf:params:oauth:token-type:access_token \
  -d subject_token="$token" -d audience=ledger-api
```

Usuários de teste: `ana` (`customer_id` `11111111-1111-4111-8111-111111111111`), `bruno`
(`22222222-2222-4222-8222-222222222222`) e `sem-cliente`, sem `customer_id`; a senha de cada um é o
nome seguido de `_dev`.

As senhas do `docker-compose.yml`, de `docker/postgres/init/` e de `docker/mongo/init/`, os
segredos e senhas do realm em `docker/keycloak/`, a chave de unseal e as credenciais do AppRole do
OpenBao (terminados em `_dev`) são exclusivos do ambiente local.

### Exemplo

Abrir uma conta para a `ana`, creditar R$ 150,75 e consultar saldo e extrato. Todo `POST` exige o
cabeçalho `Idempotency-Key` com um UUID novo por operação; repetir o `POST` com a mesma chave e o
mesmo corpo devolve a resposta original com `Idempotent-Replayed: true` (ADR-0037).

```bash
token() {
  curl -s http://localhost:8180/realms/ledger/protocol/openid-connect/token \
    -d grant_type=client_credentials -d client_id=$1 -d client_secret=$1_dev | jq -r .access_token
}
backoffice=$(token backoffice)
payments=$(token payments-hub)

conta=$(curl -s -X POST http://localhost:8080/v1/accounts \
  -H "Authorization: Bearer $backoffice" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 0f8b7a52-3c1e-4d6a-9b2f-5e4c3d2b1a01' \
  -d '{"customerId":"11111111-1111-4111-8111-111111111111"}' | jq -r .id)

curl -s -X POST http://localhost:8080/v1/accounts/$conta/credits \
  -H "Authorization: Bearer $payments" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 6a1d2e3f-4b5c-4d7e-8f90-a1b2c3d4e5f6' \
  -d '{"amount":"150.75","category":"PIX_IN","externalReference":"EXEMPLO-1"}'

curl -s http://localhost:8080/v1/accounts/$conta/balance -H "Authorization: Bearer $backoffice"
curl -s "http://localhost:8080/v1/accounts/$conta/balance?at=2026-01-01T00:00:00Z" \
  -H "Authorization: Bearer $backoffice"
curl -s "http://localhost:8080/v1/accounts/$conta/statement?limit=50" \
  -H "Authorization: Bearer $backoffice"
```

Cadastrar um cliente e consultá-lo, mascarado pelo `backoffice` e completo pelo `compliance`:

```bash
cliente=$(curl -s -X POST http://localhost:8080/v1/customers \
  -H "Authorization: Bearer $backoffice" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 3b9e2c71-5d4a-4f8e-9c1b-7a6d5e4f3c21' \
  -d '{"name":"Carla Dias","cpf":"390.533.447-05","birthDate":"1988-07-21","email":"carla@ledger.local"}' | jq -r .id)

curl -s http://localhost:8080/v1/customers/$cliente -H "Authorization: Bearer $backoffice"
curl -s http://localhost:8080/v1/customers/$cliente/personal-data -H "Authorization: Bearer $(token compliance)"
```

O saldo em um momento usa `at` com fuso explícito e nunca no futuro; antes do primeiro
lançamento, o saldo é zero. O extrato aceita `from` (inclusivo), `to`
(exclusivo), `limit` e o `cursor` da página anterior. Categorias de crédito: `PIX_IN`, `TED_IN`,
`BOLETO_IN`, `DEPOSIT`, `ADJUSTMENT_IN`; de débito: `PIX_OUT`, `TED_OUT`, `BOLETO_OUT`, `CARD`,
`FEE`, `ADJUSTMENT_OUT`.

## Desenvolvimento

Pré-requisitos: .NET SDK 10 (versão em `global.json`) e Docker, usado pelos testes de integração.

```bash
dotnet build Ledger.slnx
dotnet test --solution Ledger.slnx
```

Para rodar a Api ou o Worker fora do compose (`appsettings.Development.json` aponta para as portas
padrão em `localhost`), suba só as dependências e use `ASPNETCORE_ENVIRONMENT=Development`:

```bash
docker compose up postgres mongo keycloak openbao openbao-bootstrap migrator
```

Documentação: decisões de arquitetura em [`docs/adr/`](docs/adr/README.md), roteiro em
[`docs/roadmap.md`](docs/roadmap.md), requisitos em [`docs/prd.md`](docs/prd.md), termos em
[`docs/glossario.md`](docs/glossario.md) e guias didáticos em [`docs/guias/`](docs/guias/).
