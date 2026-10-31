# ADR-0033: Isolamento de dados por schema e role, com privilégio mínimo por coluna

- **Status:** Aceita
- **Data:** 2026-10-31
- **Substitui:** ADR-0015

## Contexto

Os módulos Ledger e Customers persistem no PostgreSQL (ADR-0004, ADR-0031). Cada módulo é dono
exclusivo do seu estado. Customers detém dados pessoais, e o Ledger detém dados financeiros; um
defeito ou uma vulnerabilidade em um módulo (ex.: SQL injection) não pode dar acesso aos dados do
outro.

O ADR-0015 decidiu schema e role por módulo, com DML de `SELECT`, `INSERT` e `UPDATE` nas tabelas.
Esse privilégio se mostrou largo demais:

- Com `UPDATE` na tabela inteira, quem tem a credencial da Api reescreve o titular da conta
  (`accounts.customer_id`) e o momento de criação da chave (`idempotency_keys.created_at`). Recuar
  o `created_at` e chamar a limpeza equivale a um `DELETE`.
- As roles de aplicação escrevem no `__EFMigrationsHistory` do próprio schema. Inserir o id de uma
  migração futura faz o Migrator pulá-la.
- O Worker usava a role da Api, com DML completo, mas só precisa da limpeza de chaves.
- O ADR-0015 não registra a função `SECURITY DEFINER` da limpeza, a única exceção ao "sem
  `DELETE`".

Este ADR substitui o ADR-0015 por inteiro.

## Alternativas consideradas

### Isolamento entre módulos

1. **Mesmo schema e mesma credencial para todos os módulos.** Rejeitada: o isolamento depende
   apenas de disciplina no código; qualquer falha expõe todos os dados.
2. **Uma instância de banco por módulo.** Isolamento máximo, mas multiplica custo operacional
   (backup, monitoramento, atualização) sem ganho proporcional neste momento.
3. **Mesma instância, schema e role de banco separados por módulo.**

### Privilégio de escrita

1. **`UPDATE` na tabela inteira** (o que existia). Rejeitada: deixa o titular e o prazo da chave
   regraváveis por quem tem a credencial da Api.
2. **`UPDATE` só nas colunas que a aplicação escreve,** concedido por coluna.

### Role do Worker

1. **Worker com a role da Api.** Rejeitada: DML completo para um processo que só limpa chaves.
2. **Role própria, `ledger_worker`,** só com o que o Worker executa.

## Decisão

**Isolamento: alternativa 3. Escrita: alternativa 2. Worker: alternativa 2.**

### Roles

| Role | Usada por | Alcance |
|------|-----------|---------|
| `migrator` | `Migrator` (ADR-0019) | Dono dos schemas `ledger` e `customers`: aplica DDL só neles, sem privilégio administrativo |
| `ledger_app` | Api, módulo Ledger | DML sem `DELETE` no schema `ledger`, com `UPDATE` por coluna |
| `ledger_worker` | Worker | Só `EXECUTE` na função de limpeza de chaves do schema `ledger` |
| `customers_app` | Api, módulo Customers | DML sem `DELETE` no schema `customers` |

- Schemas `ledger` e `customers`. `ledger_app` e `ledger_worker` não possuem nenhum privilégio em
  `customers`, e `customers_app` nenhum em `ledger`.
- O banco nega tudo ao `PUBLIC`: só essas quatro roles conectam, e ninguém cria objetos em `public`.
- As roles nascem no script de inicialização do PostgreSQL, que roda só com volume vazio. Uma role
  nova exige recriar o volume local (`docker compose down -v`), documentado no README.

### DML sem `DELETE`, com `UPDATE` por coluna

- Roles de aplicação têm `SELECT` e `INSERT` nas tabelas do próprio schema, e nunca `DELETE`.
  Migrações de esquema rodam com a role `migrator`, fora do ciclo de vida da aplicação.
- No schema `ledger`, o default privilege não concede `UPDATE`. O `ledger_app` recebe `UPDATE`
  apenas nas colunas que escreve:
  - `GRANT UPDATE (balance, credit_limit, version, last_recorded_at) ON ledger.accounts`;
  - `GRANT UPDATE (response_status, response_body) ON ledger.idempotency_keys`.
- **Toda tabela nova concede `UPDATE` por coluna na própria migração.** Sem isso, a aplicação não
  altera nenhuma coluna dela.
- Na tabela de lançamentos, a role da aplicação **não recebe** `UPDATE` nem `DELETE`: o ledger
  append-only (ADR-0003) é imposto pelo banco, não só pelo código.
- O schema `customers` ainda não tem tabela além do histórico de migração. A primeira tabela do
  módulo segue a mesma regra de `UPDATE` por coluna.

### Histórico de migração

O `__EFMigrationsHistory` de cada módulo fica no schema do módulo, nunca em `public`. As roles de
aplicação não têm nenhum privilégio sobre ele (`REVOKE ALL` para `ledger_app` em `ledger` e para
`customers_app` em `customers`). Só o `migrator` lê e escreve o histórico.

### A exceção `SECURITY DEFINER`

A limpeza das chaves de idempotência vencidas (ADR-0032) precisa de `DELETE`, que nenhuma role de
aplicação tem. Ela passa pela função `ledger.purge_expired_idempotency_keys(batch_size integer)`,
criada pelo `migrator` com `SECURITY DEFINER`: executa com os privilégios do dono, mas só faz uma
coisa.

- Apaga só chaves com mais de 7 dias, e no máximo `batch_size` por chamada (1 a 10000). O prazo
  vive na função: quem a executa não escolhe o que apagar.
- `SET search_path = ledger, pg_temp`: um objeto com o mesmo nome criado em outro schema não é
  usado no lugar do verdadeiro.
- Funções do `migrator` não são executáveis por padrão (`REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC`
  nos default privileges). O `EXECUTE` da limpeza é concedido só ao `ledger_worker`, na migração;
  o `ledger_app` não a executa.

Qualquer outra função `SECURITY DEFINER` exige o mesmo desenho: uma operação só, sem parâmetro que
amplie o alcance, `search_path` fixo e `EXECUTE` concedido a uma role nomeada.

### Credenciais

Credenciais de cada role são fornecidas por configuração ou segredo e nunca versionadas, exceto as
de desenvolvimento local, identificadas como tal (terminadas em `_dev`).

## Consequências

- **Positivas:**
  - Isolamento de estado entre módulos garantido pelo banco.
  - Uma vulnerabilidade no Ledger não alcança dados pessoais.
  - Imutabilidade dos lançamentos protegida contra defeitos da aplicação.
  - A credencial da Api não reescreve o titular da conta nem o prazo de uma chave, e não faz o
    Migrator pular uma migração.
  - A credencial do Worker não lê nem escreve nenhuma tabela.
  - Separar um módulo em instância própria no futuro é uma mudança de string de conexão.
- **Negativas:**
  - Mais credenciais para gerenciar.
  - Cada coluna nova que a aplicação precisa alterar exige o `GRANT` na migração; esquecê-lo
    aparece como `42501` nos testes de integração, não em produção.
  - Role nova exige recriar o volume local.
  - Instância compartilhada: uma sobrecarga de um módulo afeta o outro. Aceitável enquanto a
    carga de Customers for baixa em relação à do Ledger.
