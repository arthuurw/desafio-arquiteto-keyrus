# Ledger

Ledger bancário em C#: registro de movimentações e consulta de saldo em um momento.

## Como executar

Pré-requisito: Docker com Docker Compose.

```bash
docker compose up
```

O comando sobe o PostgreSQL, aplica as migrações e inicia a Api (`http://localhost:8080`) e o
Worker (`http://localhost:8081`). Para conferir: `curl http://localhost:8080/health/live` responde
`Healthy`.

As senhas do `docker-compose.yml` e de `docker/postgres/init/` são exclusivas do ambiente local.

## Desenvolvimento

Pré-requisitos: .NET SDK 10 (versão em `global.json`) e Docker, usado pelos testes de integração.

```bash
dotnet build Ledger.slnx
dotnet test --solution Ledger.slnx
```

Decisões de arquitetura em [`docs/adr/`](docs/adr/README.md); roteiro em [`docs/roadmap.md`](docs/roadmap.md).
