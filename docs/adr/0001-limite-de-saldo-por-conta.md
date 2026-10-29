# ADR-0001: Limite de saldo configurável por conta

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

O sistema registra débitos em contas de clientes. É preciso definir o que acontece quando um débito
deixaria o saldo abaixo de zero. A regra determina a estratégia de concorrência: se débitos podem ser
rejeitados por saldo insuficiente, duas requisições simultâneas não podem ambas ser aceitas quando,
somadas, excedem o disponível (*double-spend*).

## Alternativas consideradas

1. **Rejeitar qualquer débito que torne o saldo negativo.** Realista para conta corrente sem limite.
   Exige controle de concorrência forte por conta.
2. **Aceitar saldo negativo; o sistema apenas registra.** Concorrência trivial (append-only sem
   disputa), mas transfere a responsabilidade de validação para outro sistema e elimina a garantia
   de consistência de saldo que o negócio exige.
3. **Limite configurável por conta (cheque especial).** Mesma mecânica da alternativa 1, com o
   limite como parâmetro. A alternativa 1 é o caso particular `credit_limit = 0`.

## Decisão

Cada conta possui um **limite de crédito** (valor não negativo). Um débito é aceito somente se
`current_balance - debit_amount >= -credit_limit`. O limite padrão é **0**, ou seja, por padrão a conta não
pode ficar negativa.

Créditos nunca são rejeitados por regra de saldo.

## Consequências

- **Positivas:**
  - Modelo de domínio mais rico com custo técnico igual ao da alternativa 1.
  - Garantia de consistência explícita e testável: nenhuma conta ultrapassa seu limite, mesmo sob
    concorrência.
- **Negativas:**
  - Escritas de débito na mesma conta precisam ser serializadas, o que limita a vazão por conta
    (contas muito movimentadas viram ponto de contenção). A estratégia de concorrência será
    definida em ADR próprio.
- **Em aberto:**
  - Alteração de limite ao longo do tempo (histórico de limites) fica fora do escopo inicial.
  - Mecanismo de concorrência (lock otimista com versão ou lock pessimista) a ser decidido.
