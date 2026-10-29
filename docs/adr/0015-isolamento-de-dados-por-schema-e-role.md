# ADR-0015: Isolamento de dados por schema e role de banco por módulo

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

Os módulos Ledger e Customers persistem no PostgreSQL (ADR-0004, ADR-0010). O ADR-0010 estabelece
que cada módulo é dono exclusivo do seu estado. Customers detém dados pessoais, e o Ledger detém
dados financeiros; um defeito ou uma vulnerabilidade em um módulo (ex.: SQL injection) não pode
dar acesso aos dados do outro.

## Alternativas consideradas

1. **Mesmo schema e mesma credencial para todos os módulos.** Rejeitada: o isolamento depende
   apenas de disciplina no código; qualquer falha expõe todos os dados.
2. **Uma instância de banco por módulo.** Isolamento máximo, mas multiplica custo operacional
   (backup, monitoramento, atualização) sem ganho proporcional neste momento.
3. **Mesma instância, schema e role de banco separados por módulo.**

## Decisão

**Alternativa 3.**

- Schemas `ledger` e `customers`.
- Cada módulo conecta com sua própria role (`ledger_app`, `customers_app`), com permissões
  apenas no próprio schema. `ledger_app` não possui nenhum privilégio em `customers`, e
  vice-versa.
- Roles da aplicação têm apenas privilégios de DML (`SELECT`, `INSERT`, `UPDATE`) nas suas tabelas.
  Migrações de esquema rodam com uma role de migração separada, fora do ciclo de vida da aplicação.
- Na tabela de lançamentos, a role da aplicação **não recebe** `UPDATE` nem `DELETE`: o ledger
  append-only (ADR-0003) é imposto pelo banco, não só pelo código.
- Credenciais de cada role são fornecidas por configuração/segredo e nunca versionadas, exceto as
  de desenvolvimento local, identificadas como tal.

## Consequências

- **Positivas:**
  - Isolamento de estado entre módulos garantido pelo banco.
  - Uma vulnerabilidade no Ledger não alcança dados pessoais.
  - Imutabilidade dos lançamentos protegida contra defeitos da aplicação.
  - Separar um módulo em instância própria no futuro é uma mudança de string de conexão.
- **Negativas:**
  - Mais credenciais para gerenciar.
  - Instância compartilhada: uma sobrecarga de um módulo afeta o outro. Aceitável enquanto a
    carga de Customers for baixa em relação à do Ledger.
