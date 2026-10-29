# ADR-0019: Stack .NET: .NET 10, Minimal APIs, EF Core e migrações em processo separado

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

As decisões anteriores fixaram C#, PostgreSQL para escrita, MongoDB para leitura, vertical slices
(ADR-0010) e separação entre role de aplicação e role de migração (ADR-0015). Resta definir
runtime, modelo de endpoints, acesso a dados e aplicação de migrações.

## Decisão

| Item | Escolha | Justificativa |
|------|---------|---------------|
| Runtime | **.NET 10 (LTS)** | Versão de suporte estendido vigente. |
| Endpoints | **Minimal APIs** | Cada slice registra seu próprio endpoint dentro da sua pasta, mantendo o caso de uso completo em um único lugar. Controllers agrupam por recurso e espalham um caso de uso entre pastas. |
| Escrita | **EF Core com Npgsql** | Mapeia o agregado `Account`, a entidade `Entry` e o value object `Money`; fornece migrações. |
| Pontos críticos de SQL | **SQL explícito via EF Core** (`FromSql`) | O `SELECT ... FOR UPDATE` (ADR-0005), a atribuição de `recorded_at` com `clock_timestamp()` e a consulta de saldo em um momento (ADR-0003) ficam visíveis no código, sem depender do SQL gerado pelo ORM. |
| Leitura | **MongoDB.Driver oficial** | O modelo de leitura é um documento simples; um ORM não agrega valor. |
| Migrações | **Processo `Migrator` dedicado** | Executa as migrações do EF Core com a role de migração e encerra. Roda antes da Api e do Worker no docker-compose. A Api nunca possui privilégio de DDL. |

## Alternativas consideradas

1. **Dapper em todo o acesso a dados.** Rejeitada: mais controle e desempenho bruto, mas perde o
   mapeamento do agregado e as migrações. O gargalo de escrita é o lock por conta, não o ORM.
2. **Controllers MVC.** Rejeitada: organização por recurso conflita com vertical slices.
3. **Migrações aplicadas na inicialização da Api.** Rejeitada: exigiria que a Api tivesse
   privilégio de DDL, violando o ADR-0015, e causaria disputa quando várias réplicas iniciam ao
   mesmo tempo.

## Consequências

- **Positivas:**
  - Cada slice é autocontido: endpoint, validação e handler na mesma pasta.
  - SQL dos pontos de consistência explícito e revisável.
  - Privilégio mínimo da Api preservado.
- **Negativas:**
  - Um processo adicional (`Migrator`) no ambiente; executa apenas na implantação.
  - Mistura de EF Core e SQL explícito exige critério documentado: SQL explícito apenas nos pontos
    de concorrência e consulta temporal.
