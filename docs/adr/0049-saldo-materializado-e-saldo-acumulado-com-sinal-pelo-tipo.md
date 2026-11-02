# ADR-0049: Saldo materializado na conta e saldo acumulado por lançamento, com o sinal dado pelo tipo

- **Status:** Aceita
- **Data:** 2026-11-02
- **Substitui:** ADR-0003

## Contexto

O sistema precisa responder duas perguntas com baixa latência e sem inconsistência:

- Qual o saldo atual da conta? (necessário para validar débitos contra o limite, ver ADR-0048)
- Qual era o saldo da conta em um momento T? (requisito de negócio, ver ADR-0002)

O sistema legado sofre com lentidão; calcular saldo agregando todo o histórico a cada consulta
é uma causa típica desse tipo de degradação.

O ADR-0003 (substituído pelo ADR-0049) resolveu isso com saldo materializado na conta e saldo acumulado (`balance_after`) em
cada lançamento, e escreveu a invariante como `balance_after(n) = balance_after(n-1) + amount(n)`.
Essa forma só vale se o débito tiver `amount` negativo. O código segue o ADR-0018: `amount` é
sempre positivo (`ck_entries_amount_positive`), e o sentido vem do tipo (`Credit` soma, `Debit`
subtrai). Este ADR mantém a decisão e escreve a invariante com o sinal dado pelo tipo.

## Alternativas consideradas

1. **Calcular na leitura.** Somar os créditos e subtrair os débitos da conta com
   `recorded_at <= T`.
   - Prós: simples, sempre correto, nenhuma redundância.
   - Contras: custo O(n) no número de lançamentos; degrada com o crescimento do histórico.
2. **Saldo materializado + `balance_after`, com `amount` de sinal próprio.**
   Rejeitada: o débito com valor negativo contraria o ADR-0018, que proíbe o "débito negativo" e
   faz do tipo a única fonte do sentido da movimentação.
3. **Saldo materializado + `balance_after`, com `amount` positivo e o sinal dado pelo tipo.**
   Em uma única transação: ler a conta (saldo e versão), validar o limite, inserir o lançamento
   com `balance_after`, atualizar saldo e versão da conta.
   - Prós: saldo atual em leitura de uma linha; saldo em T resolvido pelo último lançamento com
     `recorded_at <= T` (busca por índice, O(log n)), sem snapshots nem replay; invariante
     verificável; coerente com o ADR-0018.
   - Contras: escritas na mesma conta são serializadas; redundância controlada (saldo existe em
     dois lugares, mantidos na mesma transação).
4. **Event sourcing completo.** Event store, agregado reconstruído por replay, snapshots periódicos.
   - Prós: auditabilidade total, projeções flexíveis.
   - Contras: infraestrutura e complexidade conceitual desproporcionais ao escopo; a alternativa 3
     já oferece histórico imutável e auditável.

## Decisão

**Alternativa 3.**

- A tabela de contas mantém `balance`, `credit_limit`, `version` e `last_recorded_at`. A
  `version` conta os lançamentos: o próximo recebe `sequence = version + 1`.
- Cada lançamento registra `sequence`, `type`, `amount`, `balance_after` e `recorded_at`. O
  lançamento é imutável: a aplicação insere e lê, sem permissão de `UPDATE` na tabela.
- Inserção do lançamento e atualização da conta ocorrem na **mesma transação**.
- Consulta de saldo em T: lançamento mais recente da conta com `recorded_at <= T`, via índice
  `(account_id, recorded_at, sequence)`; no empate de `recorded_at`, vale a maior `sequence`. Se
  não houver lançamento, o saldo é 0. A leitura se limita à versão lida da conta (ADR-0045).

Invariantes garantidas e cobertas por testes, para lançamentos consecutivos da mesma conta:

- Crédito: `balance_after(n) = balance_after(n-1) + amount(n)`.
- Débito: `balance_after(n) = balance_after(n-1) - amount(n)`.
- O estorno tem o mesmo `amount` do lançamento estornado e o tipo oposto ao dele: o estorno de um
  crédito é um débito, e o de um débito é um crédito. Segue a regra do seu tipo.
- `amount > 0` em todo lançamento.
- `account.balance = balance_after` do lançamento mais recente da conta, e
  `account.version = sequence` desse lançamento.
- `balance_after >= -credit_limit` em todo lançamento de débito, com o limite vigente no momento
  do débito, inclusive no estorno de crédito. No banco, `balance >= -credit_limit` na conta.

## Consequências

- **Positivas:**
  - Consulta de saldo atual e histórico com custo praticamente constante, independente do volume
    do histórico.
  - Consistência auditável: qualquer divergência entre conta e lançamentos é detectável por
    reconciliação.
  - Uma só convenção de sinal no domínio, no banco e na invariante: o valor nunca carrega o
    sentido.
- **Negativas:**
  - Contenção em contas com altíssimo volume de escrita ("contas quentes"), pois as escritas por
    conta são serializadas.
  - Recalcular a cadeia exige ler o tipo de cada lançamento, não apenas somar `amount`.
- **Evolução futura:**
  - **Contas quentes:** particionar a conta em sub-ledgers (N "baldes" com saldo próprio,
    consolidados na leitura) ou acumular lançamentos em lote antes de aplicar.
  - **Event sourcing:** caso surjam necessidades de múltiplas projeções sobre o mesmo histórico
    (extratos, relatórios regulatórios, antifraude), o ledger append-only já é a base natural
    para publicar eventos (ex.: via outbox) ou migrar para um event store.
  - **Reconciliação periódica:** job que recalcula saldos a partir dos lançamentos e alerta sobre
    divergências, como defesa em profundidade.
