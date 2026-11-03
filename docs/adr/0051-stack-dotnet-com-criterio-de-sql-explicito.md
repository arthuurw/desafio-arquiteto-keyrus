# ADR-0051: Stack .NET com critério de SQL explícito: .NET 10, Minimal APIs, EF Core e migrações em processo separado

- **Status:** Aceita
- **Data:** 2026-11-02
- **Substitui:** ADR-0019

## Contexto

O ADR substituído fixou runtime, modelo de endpoints, acesso a dados e aplicação de migrações. Ele
admitia SQL escrito à mão apenas nos pontos de concorrência e de consulta temporal, e mandava
todo o resto para o EF Core.

O código foi além desse critério, por motivos que o próprio projeto decidiu depois:

- O relay do outbox e a limpeza de dados vencidos rodam por funções do banco criadas pelas
  migrações; a role do Worker recebe só `EXECUTE` nelas e não faz DML (ADR-0044).
- A trilha de auditoria e o outbox são tabelas em que a role da Api só tem `INSERT`
  (ADR-0041, ADR-0044). A migração as cria em SQL, fora do modelo do EF Core.
- O registro da chave de idempotência depende do `INSERT ... ON CONFLICT DO NOTHING`, que espera
  a transação aberta com a mesma chave (ADR-0037 (substituído pelo ADR-0059)).

Com o critério escrito menor que a prática, ele deixa de orientar quem decide entre LINQ e SQL.

## Alternativas consideradas

1. **Dapper em todo o acesso a dados.** Rejeitada: mais controle e desempenho bruto, mas perde o
   mapeamento do agregado e as migrações. O gargalo de escrita é o lock por conta, não o ORM.
2. **Controllers MVC.** Rejeitada: a organização por recurso espalha um caso de uso entre pastas e
   conflita com as vertical slices.
3. **Migrações aplicadas na inicialização da Api.** Rejeitada: exigiria que a Api tivesse
   privilégio de DDL, violando o ADR-0044, e causaria disputa quando várias réplicas iniciam ao
   mesmo tempo.
4. **Manter o critério anterior e reescrever o resto em LINQ.** Rejeitada: o Worker só
   alcança o outbox e a limpeza pelas funções; a Api, só com `INSERT` no outbox, não pode usar o
   `RETURNING` que o EF Core gera para a coluna de identidade; e o `ON CONFLICT DO NOTHING` não tem
   equivalente no `SaveChanges`.
5. **Ampliar o critério ao que o código faz.**

## Decisão

**Alternativa 5.**

| Item | Escolha | Justificativa |
|------|---------|---------------|
| Runtime | **.NET 10 (LTS)** | Versão de suporte estendido vigente. |
| Endpoints | **Minimal APIs** | Cada slice registra seu próprio endpoint dentro da sua pasta, mantendo o caso de uso completo em um único lugar. |
| Escrita | **EF Core com Npgsql** | Mapeia os agregados e o value object `Money`; fornece as migrações. |
| Leitura | **MongoDB.Driver oficial** | O modelo de leitura é um documento simples; um ORM não agrega valor. |
| Migrações | **Processo `Migrator` dedicado** | Aplica as migrações do EF Core de cada módulo com a role de migração e encerra. A Api e o Worker só sobem depois que ele termina com sucesso. A Api nunca possui privilégio de DDL (ADR-0044). |

**Critério de SQL explícito.** O acesso ao PostgreSQL passa por LINQ e `SaveChanges` do EF Core,
inclusive a leitura por chave. SQL escrito à mão, sempre pelo próprio EF Core (`FromSql`,
`SqlQuery`, `ExecuteSql`), só nestes casos:

1. **Concorrência:** o lock da conta (`FOR UPDATE` na escrita, `FOR KEY SHARE` na leitura com
   instante), o `SET LOCAL lock_timeout` da transação (ADR-0005, ADR-0045) e o registro da chave de
   idempotência por `INSERT ... ON CONFLICT DO NOTHING`, com as demais operações sobre a chave no
   mesmo componente (ADR-0037 (substituído pelo ADR-0059)).
2. **Consulta temporal:** o relógio do banco (`clock_timestamp()`) lido para carimbar e para
   responder, o saldo em um instante e a página do extrato (ADR-0002, ADR-0045).
3. **Funções do banco criadas pelas migrações e chamadas pela aplicação**, como
   `claim_outbox_batch` no relay do outbox e as funções de limpeza em lotes (ADR-0044).
4. **`INSERT` em tabela só de inserção fora do modelo do EF Core:** o `INSERT` da trilha de
   auditoria (ADR-0041) e o do outbox (ADR-0006).

Nomes de tabela e de função entram no SQL como identificadores fixos do módulo, nunca como
entrada; valores seguem sempre como parâmetros. O SQL das migrações (grants, funções, triggers)
não é acesso a dados e fica fora deste critério.

## Consequências

- **Positivas:**
  - Cada slice é autocontido: endpoint, validação e handler na mesma pasta.
  - SQL dos pontos de consistência explícito e revisável, sem depender do SQL gerado pelo ORM.
  - O critério escrito corresponde ao código: um SQL fora dos quatro casos é desvio a corrigir.
  - Privilégio mínimo preservado: a Api sem DDL e só com `INSERT` nas tabelas só de inserção, o
    Worker só com as funções.
- **Negativas:**
  - Um processo adicional (`Migrator`) no ambiente; executa apenas na implantação.
  - Mistura de EF Core e SQL explícito: quem escreve acesso a dados precisa conhecer o critério.
  - O SQL escrito à mão não é verificado pelo compilador; só os testes de integração contra o
    PostgreSQL real o exercitam.
