# ADR-0043: Isolamento de dados por schema e role, com trilha de auditoria só de inserção

- **Status:** Substituída por ADR-0044
- **Data:** 2026-11-01
- **Substitui:** ADR-0038

## Contexto

Os módulos Ledger e Customers persistem no PostgreSQL (ADR-0004, ADR-0031). Cada módulo é dono
exclusivo do seu estado. Customers detém dados pessoais, e o Ledger detém dados financeiros; um
defeito ou uma vulnerabilidade em um módulo (ex.: SQL injection) não pode dar acesso aos dados do
outro.

O ADR-0038 decidiu schema e role por módulo, DML sem `DELETE`, `UPDATE` por coluna e uma role de
Worker por módulo. A F6 cria a trilha de auditoria de cada módulo (ADR-0041), que é prova: nem a
aplicação nem o dono do schema podem alterá-la. O default privilege dá à role de aplicação `SELECT`,
`INSERT` e `UPDATE` em toda tabela nova, mais do que a trilha admite.

Este ADR substitui o ADR-0038 por inteiro.

## Alternativas consideradas

### Isolamento entre módulos

1. **Mesmo schema e mesma credencial para todos os módulos.** Rejeitada: o isolamento depende
   apenas de disciplina no código; qualquer falha expõe todos os dados.
2. **Uma instância de banco por módulo.** Isolamento máximo, mas multiplica custo operacional
   (backup, monitoramento, atualização) sem ganho proporcional neste momento.
3. **Mesma instância, schema e role de banco separados por módulo.**

### Privilégio de escrita

1. **`UPDATE` na tabela inteira.** Rejeitada: deixa o titular e o prazo da chave regraváveis por
   quem tem a credencial da Api.
2. **`UPDATE` só nas colunas que a aplicação escreve,** concedido por coluna.

### Imutabilidade da trilha de auditoria

1. **Só privilégio:** a role de aplicação com `INSERT` e nada mais. Rejeitada sozinha: o dono do
   schema (`migrator`) e o superusuário ainda alterariam linhas sem deixar rastro.
2. **Privilégio e trigger:** `INSERT` só para a role de aplicação, e um trigger que recusa `UPDATE`,
   `DELETE` e `TRUNCATE` de qualquer role.

### Role do Worker

1. **Worker com a role da Api.** Rejeitada: DML completo para um processo que só limpa chaves.
2. **Uma role de Worker para todos os módulos.** Rejeitada: a mesma credencial alcançaria os dois
   schemas.
3. **Uma role de Worker por módulo** (`ledger_worker`, `customers_worker`), cada uma só com o que o
   Worker executa naquele schema.

## Decisão

**Isolamento: alternativa 3. Escrita: alternativa 2. Trilha: alternativa 2. Worker: alternativa 3.**

### Roles

| Role | Usada por | Alcance |
|------|-----------|---------|
| `migrator` | `Migrator` (ADR-0019) | Dono dos schemas `ledger` e `customers`: aplica DDL só neles, sem privilégio administrativo |
| `ledger_app` | Api, módulo Ledger | DML sem `DELETE` no schema `ledger`, com `UPDATE` por coluna |
| `ledger_worker` | Worker, rotinas do Ledger | Só `EXECUTE` nas funções do Worker no schema `ledger` |
| `customers_app` | Api, módulo Customers | `SELECT` e `INSERT` no schema `customers`, com `UPDATE` por coluna |
| `customers_worker` | Worker, rotinas de Customers | Só `USAGE` no schema `customers` e `EXECUTE` na função de limpeza de chaves dele |

- Schemas `ledger` e `customers`. `ledger_app` e `ledger_worker` não possuem nenhum privilégio em
  `customers`; `customers_app` e `customers_worker`, nenhum em `ledger`.
- O banco nega tudo ao `PUBLIC`: só essas cinco roles conectam, e ninguém cria objetos em `public`.
- As roles nascem no script de inicialização do PostgreSQL, que roda só com volume vazio. Uma role
  nova exige recriar o volume local (`docker compose down -v`), documentado no README.
- O Worker abre uma conexão por módulo, cada uma com a role de Worker dele.

### DML sem `DELETE`, com `UPDATE` por coluna

- Roles de aplicação têm `SELECT` e `INSERT` nas tabelas do próprio schema, e nunca `DELETE`; a
  trilha de auditoria é a exceção, só com `INSERT` (abaixo).
  Migrações de esquema rodam com a role `migrator`, fora do ciclo de vida da aplicação.
- Nos dois schemas, o default privilege não concede `UPDATE`. A role de aplicação recebe `UPDATE`
  apenas nas colunas que escreve:
  - `GRANT UPDATE (balance, credit_limit, version, last_recorded_at) ON ledger.accounts`;
  - `GRANT UPDATE (response_status, response_body) ON ledger.idempotency_keys`;
  - `GRANT UPDATE (response_status, response_body) ON customers.idempotency_keys`.
- **Toda tabela nova concede `UPDATE` por coluna na própria migração.** Sem isso, a aplicação não
  altera nenhuma coluna dela.
- Na tabela de lançamentos, a role da aplicação **não recebe** `UPDATE` nem `DELETE`: o ledger
  append-only (ADR-0003) é imposto pelo banco, não só pelo código.
- Em `customers.customers`, a `customers_app` não recebe `UPDATE` em coluna nenhuma: nenhuma rota
  altera o cliente depois do cadastro. A correção de dados (ADR-0017) entra com o `GRANT` das
  colunas que ela escrever.

### Trilha de auditoria só de inserção

Em `ledger.audit_events` e `customers.audit_events` (ADR-0041):

- `REVOKE ALL ... FROM <schema>_app` e `GRANT INSERT ... TO <schema>_app`: a role de aplicação insere
  e não lê, não altera nem apaga. Sem `SELECT`, um defeito na Api também não lê a trilha.
- A função `<schema>.reject_audit_change()` lança erro e é ligada por `BEFORE UPDATE OR DELETE ...
  FOR EACH ROW` e `BEFORE TRUNCATE ... FOR EACH STATEMENT`: a recusa vale para qualquer role,
  inclusive o `migrator` e o superusuário.
- As roles de Worker não têm privilégio nenhum na trilha, e nenhuma role apaga linhas dela.
- A consulta da trilha é por SQL, com o `migrator`.

### Histórico de migração

O `__EFMigrationsHistory` de cada módulo fica no schema do módulo, nunca em `public`. As roles de
aplicação não têm nenhum privilégio sobre ele (`REVOKE ALL` para `ledger_app` em `ledger` e para
`customers_app` em `customers`). Só o `migrator` lê e escreve o histórico.

### A exceção `SECURITY DEFINER`

A limpeza das chaves de idempotência vencidas (ADR-0037) precisa de `DELETE`, que nenhuma role de
aplicação tem. Ela passa por uma função por módulo,
`ledger.purge_expired_idempotency_keys(batch_size integer)` e
`customers.purge_expired_idempotency_keys(batch_size integer)`, criadas pelo `migrator` com
`SECURITY DEFINER`: executam com os privilégios do dono, mas só fazem uma coisa.

- Apagam só chaves com mais de 7 dias, e no máximo `batch_size` por chamada (1 a 10000). O prazo
  vive na função: quem a executa não escolhe o que apagar.
- `search_path` fixo no schema do módulo e `pg_temp`: um objeto com o mesmo nome criado em outro
  schema não é usado no lugar do verdadeiro.
- Funções do `migrator` não são executáveis por padrão (`REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC`
  nos default privileges). O `EXECUTE` de cada limpeza é concedido só à role de Worker do módulo,
  na migração; as roles de aplicação não as executam.

Qualquer outra função `SECURITY DEFINER` exige o mesmo desenho: uma operação só, sem parâmetro que
amplie o alcance, `search_path` fixo e `EXECUTE` concedido a uma role nomeada.

### Credenciais

Credenciais de cada role são fornecidas por configuração ou segredo e nunca versionadas, exceto as
de desenvolvimento local, identificadas como tal (terminadas em `_dev`).

## Consequências

- **Positivas:**
  - Isolamento de estado entre módulos garantido pelo banco, também no Worker.
  - Uma vulnerabilidade no Ledger não alcança dados pessoais.
  - Imutabilidade dos lançamentos e do cadastro protegida contra defeitos da aplicação.
  - A trilha de auditoria não é alterada nem apagada por role nenhuma, nem pela dona.
  - A credencial da Api não reescreve o titular da conta nem o prazo de uma chave, e não faz o
    Migrator pular uma migração.
  - As credenciais do Worker não leem nem escrevem nenhuma tabela.
  - Separar um módulo em instância própria no futuro é uma mudança de string de conexão.
- **Negativas:**
  - Mais credenciais para gerenciar; o Worker carrega duas.
  - Cada coluna nova que a aplicação precisa alterar exige o `GRANT` na migração; esquecê-lo
    aparece como `42501` nos testes de integração, não em produção.
  - Role nova exige recriar o volume local.
  - Corrigir uma linha errada da trilha exige remover o trigger numa migração, o que fica no
    histórico.
  - Instância compartilhada: uma sobrecarga de um módulo afeta o outro. Aceitável enquanto a
    carga de Customers for baixa em relação à do Ledger.
