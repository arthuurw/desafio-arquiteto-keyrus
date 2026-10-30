# Ledger

Ledger bancário em C#: registro de movimentações e consulta de saldo em um momento.

## Como executar

Pré-requisito: Docker com Docker Compose.

```bash
docker compose up
```

O comando sobe o PostgreSQL, aplica as migrações e inicia a Api (`http://localhost:8080`), o
Worker (`http://localhost:8081`) e o Keycloak (`http://localhost:8180`), provedor de identidade
local com o realm `ledger` importado de `docker/keycloak/realm-ledger.json`. Para conferir:
`curl http://localhost:8080/health/live` responde `Healthy`.

### Tokens

Toda rota de negócio da Api exige um token do Keycloak com o escopo da rota (ADR-0012, ADR-0013).
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

Usuários de teste: `ana` e `bruno`, com `customer_id`, e `sem-cliente`, sem; a senha de cada um é o
nome seguido de `_dev`.

As senhas do `docker-compose.yml`, de `docker/postgres/init/` e os segredos e senhas do realm em
`docker/keycloak/` (terminados em `_dev`) são exclusivos do ambiente local.

## Desenvolvimento

Pré-requisitos: .NET SDK 10 (versão em `global.json`) e Docker, usado pelos testes de integração.

```bash
dotnet build Ledger.slnx
dotnet test --solution Ledger.slnx
```

Decisões de arquitetura em [`docs/adr/`](docs/adr/README.md); roteiro em [`docs/roadmap.md`](docs/roadmap.md).
