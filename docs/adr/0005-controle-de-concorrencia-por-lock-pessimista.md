# ADR-0005: Controle de concorrência por lock pessimista na conta

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

Débitos concorrentes na mesma conta não podem, somados, ultrapassar o limite (ADR-0001).
Exemplo: conta com saldo R$ 100 e limite 0 recebe simultaneamente dois débitos de R$ 80;
apenas um pode ser aceito.

Além disso, a consulta de saldo em um momento (ADR-0003) depende de que a ordem de
`recorded_at` dos lançamentos de uma conta seja a mesma ordem em que os saldos acumulados
(`balance_after`) foram calculados.

O sistema legado apresenta instabilidade justamente em horários de pico, quando a disputa pela
mesma conta é maior. O mecanismo escolhido precisa se comportar bem sob contenção.

## Alternativas consideradas

1. **Lock otimista** (coluna de versão, `UPDATE ... WHERE version = @lida`, retry em conflito).
   - Prós: nenhum lock mantido; excelente com baixa contenção.
   - Contras: sob rajadas na mesma conta, conflitos se multiplicam e geram tempestade de
     retries, elevando latência e falhas por esgotamento de tentativas. Comporta-se pior
     exatamente no cenário que motivou a reescrita.
2. **Lock pessimista explícito** (`SELECT ... FOR UPDATE` na conta, validação no domínio,
   inserção do lançamento, atualização da conta, commit).
   - Prós: transações concorrentes aguardam em fila em vez de falhar; regra de negócio permanece
     no modelo de domínio em C#, testável sem banco.
   - Contras: lock mantido durante alguns round-trips entre aplicação e banco.
3. **UPDATE atômico condicional** (`UPDATE accounts SET saldo = saldo + @v WHERE id = @id AND
   balance + @v >= -credit_limit RETURNING balance`).
   - Prós: menor tempo de lock possível, um único comando.
   - Contras: regra de negócio migra para SQL; modelo de domínio fica anêmico; regra mais difícil
     de testar e evoluir.
4. **Isolamento `SERIALIZABLE`.**
   - Prós: o banco detecta anomalias automaticamente.
   - Contras: aborta transações com `serialization_failure`, exigindo retry; sob contenção
     reproduz o problema da alternativa 1 em escala maior.

## Decisão

**Alternativa 2**, com as seguintes regras:

- **Fluxo de escrita**, em uma única transação:
  1. `SELECT ... FROM accounts WHERE id = @id FOR UPDATE`.
  2. O agregado de domínio valida a operação (limite, valor positivo).
  3. Atribuição do timestamp de registro (regra abaixo).
  4. Inserção do lançamento com `balance_after` e `sequence`.
  5. Atualização de `balance`, `version` e `last_recorded_at` da conta.
  6. Commit.
- **Transação curta e com tempo limite:** `lock_timeout` configurado para que, em contenção
  extrema, a requisição falhe rápido com erro explícito (passível de retry pelo cliente, que é
  seguro graças à idempotência) em vez de acumular conexões presas.
- **Defesa em profundidade:** constraint `CHECK (balance >= -credit_limit)` na tabela de contas. Se um
  defeito na aplicação permitir uma violação, o banco rejeita a transação.
- **Ordenação temporal garantida por conta:**
  - O timestamp é atribuído **após** a obtenção do lock, com `clock_timestamp()`. Usar `now()`
    seria incorreto: `now()` retorna o início da transação, e uma transação iniciada antes, mas
    que obteve o lock depois, gravaria `recorded_at` menor com um `balance_after` que já inclui a
    outra transação, corrompendo a consulta de saldo em um momento.
  - Monotonicidade forçada: `recorded_at = GREATEST(clock_timestamp(), account.last_recorded_at)`,
    protegendo contra ajustes de relógio do servidor.
  - `sequence` por conta (derivada de `version`) com constraint `UNIQUE (account_id, sequence)`,
    usada como critério de desempate na consulta.

## Consequências

- **Positivas:**
  - Sob pico, requisições na mesma conta são enfileiradas e atendidas, em vez de falharem em
    cascata.
  - Regra de negócio no domínio; banco como segunda barreira.
  - Ordenação de lançamentos consistente com os saldos acumulados.
- **Negativas:**
  - Vazão de escrita por conta limitada pela duração da transação. Mitigação para contas quentes
    documentada no ADR-0003.
  - Conexões ocupadas enquanto aguardam o lock; dimensionamento do pool e `lock_timeout` precisam
    ser ajustados em conjunto.
- **Verificação:**
  - Teste de concorrência com N débitos simultâneos na mesma conta, verificando que o total
    aceito nunca ultrapassa o limite e que as invariantes do ADR-0003 se mantêm.
