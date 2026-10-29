# ADR-0003: Saldo materializado na conta e saldo acumulado por lançamento

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

O sistema precisa responder duas perguntas com baixa latência e sem inconsistência:

- Qual o saldo atual da conta? (necessário para validar débitos contra o limite, ver ADR-0001)
- Qual era o saldo da conta em um momento T? (requisito de negócio, ver ADR-0002)

O sistema legado sofre com lentidão; calcular saldo agregando todo o histórico a cada consulta
é uma causa típica desse tipo de degradação.

## Alternativas consideradas

1. **Calcular na leitura.** `SUM(amount) WHERE account_id = X AND recorded_at <= T`.
   - Prós: simples, sempre correto, nenhuma redundância.
   - Contras: custo O(n) no número de lançamentos; degrada com o crescimento do histórico.
2. **Saldo corrente materializado na conta + saldo acumulado (`balance_after`) em cada lançamento.**
   Em uma única transação: ler a conta (saldo e versão), validar o limite, inserir o lançamento
   com `balance_after`, atualizar saldo e versão da conta.
   - Prós: saldo atual em leitura de uma linha; saldo em T resolvido pelo último lançamento com
     `recorded_at <= T` (busca por índice, O(log n)), sem snapshots nem replay; invariante
     verificável.
   - Contras: escritas na mesma conta são serializadas; redundância controlada (saldo existe em
     dois lugares, mantidos na mesma transação).
3. **Event sourcing completo.** Event store, agregado reconstruído por replay, snapshots periódicos.
   - Prós: auditabilidade total, projeções flexíveis.
   - Contras: infraestrutura e complexidade conceitual desproporcionais ao escopo; a alternativa 2
     já oferece histórico imutável e auditável.

## Decisão

**Alternativa 2.**

- A tabela de contas mantém `balance` e `version`.
- Cada lançamento registra `amount`, `recorded_at` e `balance_after`.
- Inserção do lançamento e atualização da conta ocorrem na **mesma transação**.
- Consulta de saldo em T: lançamento mais recente da conta com `recorded_at <= T`, via índice
  `(account_id, recorded_at)`. Se não houver lançamento, o saldo é 0.

Invariantes garantidas e cobertas por testes:

- `balance_after(n) = balance_after(n-1) + amount(n)` para lançamentos consecutivos da mesma conta.
- `account.balance = balance_after` do lançamento mais recente da conta.
- `balance_after >= -credit_limit` em todo lançamento de débito.

## Consequências

- **Positivas:**
  - Consulta de saldo atual e histórico com custo praticamente constante, independente do volume
    do histórico.
  - Consistência auditável: qualquer divergência entre conta e lançamentos é detectável por
    reconciliação.
- **Negativas:**
  - Contenção em contas com altíssimo volume de escrita ("contas quentes"), pois as escritas por
    conta são serializadas.
- **Evolução futura:**
  - **Contas quentes:** particionar a conta em sub-ledgers (N "baldes" com saldo próprio,
    consolidados na leitura) ou acumular lançamentos em lote antes de aplicar.
  - **Event sourcing:** caso surjam necessidades de múltiplas projeções sobre o mesmo histórico
    (extratos, relatórios regulatórios, antifraude), o ledger append-only já é a base natural
    para publicar eventos (ex.: via outbox) ou migrar para um event store.
  - **Reconciliação periódica:** job que recalcula saldos a partir dos lançamentos e alerta sobre
    divergências, como defesa em profundidade.
