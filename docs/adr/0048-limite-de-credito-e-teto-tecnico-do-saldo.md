# ADR-0048: Limite de crédito por conta e teto técnico do saldo

- **Status:** Aceita
- **Data:** 2026-11-02
- **Substitui:** ADR-0001

## Contexto

O sistema registra créditos e débitos em contas de clientes. É preciso definir o que acontece
quando um débito deixaria o saldo abaixo de zero. A regra determina a estratégia de concorrência:
se débitos podem ser rejeitados por saldo insuficiente, duas requisições simultâneas não podem ambas
ser aceitas quando, somadas, excedem o disponível (*double-spend*).

O saldo também tem um limite superior. A coluna `balance` é `numeric(19,4)` (ADR-0018), e o maior
valor com 2 casas que ela guarda é 999999999999999.99. Um crédito que levasse o saldo acima disso
estouraria no banco.

O ADR-0001 (substituído pelo ADR-0048) decidia só o limite inferior e dizia que créditos nunca são
rejeitados por regra de saldo. O código recusa o crédito acima do teto (`MaxBalance`,
`422 balance-limit-exceeded`), e a troca do limite de crédito, que ele deixava em aberto, existe.
Este ADR registra as duas regras e o substitui.

## Alternativas consideradas

Limite inferior:

1. **Rejeitar qualquer débito que torne o saldo negativo.** Realista para conta corrente sem limite.
   Exige controle de concorrência forte por conta.
2. **Aceitar saldo negativo; o sistema apenas registra.** Concorrência trivial (append-only sem
   disputa), mas transfere a responsabilidade de validação para outro sistema e elimina a garantia
   de consistência de saldo que o negócio exige.
3. **Limite configurável por conta (cheque especial).** Mesma mecânica da alternativa 1, com o
   limite como parâmetro. A alternativa 1 é o caso particular `credit_limit = 0`.

Limite superior:

4. **Deixar o banco recusar o valor que não cabe na coluna.** O erro chega como falha interna, não
   como recusa de negócio, e o consumidor não sabe o que fazer.
5. **Teto técnico no domínio.** O agregado recusa o crédito antes da gravação, com um `type` estável
   na Api (ADR-0042).

## Decisão

**Alternativas 3 e 5.**

Limite de crédito:

- Cada conta possui um **limite de crédito** (valor não negativo). Um débito é aceito somente se
  `current_balance - debit_amount >= -credit_limit`; senão, `422 insufficient-funds`.
- A conta abre com limite **0**: por padrão, ela não pode ficar negativa.
- O limite é trocado por `PUT /v1/accounts/{id}/credit-limit`. A troca trava a conta como um débito
  (ADR-0005), não gera lançamento nem muda a versão da conta, e o limite novo precisa cobrir o saldo
  já negativo (`balance >= -credit_limit`); senão, `422 credit-limit-below-balance`.
- A conta guarda só o limite vigente; a troca fica na trilha de auditoria (ADR-0041).

Teto técnico do saldo:

- `Account.MaxBalance` é 999999999999999.99, o maior saldo que `numeric(19,4)` guarda com 2 casas.
- Um crédito é aceito somente se `current_balance + credit_amount <= MaxBalance`; senão,
  `422 balance-limit-exceeded`. O limite de crédito nunca recusa um crédito.

Estorno: o estorno de um crédito é um débito e respeita o limite de crédito; o estorno de um débito
é um crédito e respeita o teto.

As duas regras são decididas no agregado `Account`, com a conta travada (ADR-0005). O banco repete
o limite inferior como defesa em profundidade: `CHECK (balance >= -credit_limit)` e
`CHECK (credit_limit >= 0)`.

## Consequências

- **Positivas:**
  - Modelo de domínio mais rico com custo técnico igual ao da alternativa 1.
  - Garantia de consistência explícita e testável: nenhuma conta ultrapassa seu limite, mesmo sob
    concorrência.
  - O saldo nunca passa do que a coluna guarda: o excesso é uma recusa de negócio, não um erro do
    banco.
- **Negativas:**
  - Escritas na mesma conta precisam ser serializadas, o que limita a vazão por conta (contas muito
    movimentadas viram ponto de contenção). O mecanismo é o lock pessimista (ADR-0005).
  - O teto deriva do tipo da coluna: mudar a precisão de `balance` exige rever `MaxBalance`.
  - Sem histórico de limites: a conta guarda só o limite vigente.
