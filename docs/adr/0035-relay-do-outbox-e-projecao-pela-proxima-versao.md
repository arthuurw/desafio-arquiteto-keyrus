# ADR-0035: Relay do outbox por polling e projeção pela próxima versão estrita

- **Status:** Aceita
- **Data:** 2026-10-31
- **Complementa:** ADR-0006, ADR-0009, ADR-0033

## Contexto

O ADR-0006 decidiu o modelo de leitura no MongoDB alimentado por um outbox gravado na transação do
lançamento, e deixou em aberto como o outbox é lido. O ADR-0009 separou a Api do Worker e previu
várias réplicas do Worker lendo o outbox com `FOR UPDATE SKIP LOCKED`. O ADR-0033 deu ao
`ledger_worker` só a função de limpeza de chaves.

Três perguntas ficaram sem resposta:

- **Leitura do outbox.** Polling na tabela ou replicação lógica do PostgreSQL.
- **Ordem por conta com várias réplicas.** Com `SKIP LOCKED`, a réplica A pode travar o evento 1 de
  uma conta enquanto a réplica B reserva o evento 2 da mesma conta. Se B aplicar o 2 antes do 1, o
  modelo de leitura mostra o saldo depois do 2 e perde o 1, porque a regra "ignora versão menor ou
  igual" do ADR-0006 descartaria o 1 quando chegasse.
- **Privilégio do Worker.** Reservar, marcar e apagar eventos exige ler e alterar o outbox, e o
  `ledger_worker` não tem privilégio em nenhuma tabela.

## Alternativas consideradas

### Leitura do outbox

1. **Replicação lógica (CDC).** Entrega em ordem de commit, sem polling. Exige configurar
   `wal_level`, slots e um consumidor do protocolo de replicação; um slot esquecido retém WAL até
   encher o disco.
2. **Polling com `FOR UPDATE SKIP LOCKED`.** Uma consulta indexada nos pendentes a cada intervalo;
   várias réplicas dividem o trabalho sem coordenação.

### Ordem por conta

1. **Uma réplica só, ou um lock por conta no relay.** Simples, mas a réplica única não escala, e o
   lock por conta acrescenta uma espera que o `SKIP LOCKED` existe para evitar.
2. **Próxima versão estrita.** A projeção aplica o evento só quando `sequence` é a versão projetada
   mais 1. Versão menor ou igual já foi aplicada. Versão maior que a próxima fica pendente para a
   rodada seguinte, quando a anterior já chegou.

### Privilégio do Worker

1. **`SELECT` e `UPDATE (processed_at)` no outbox para o `ledger_worker`.** Dá DML a um processo
   que só precisa de três operações.
2. **Funções `SECURITY DEFINER`**, como a limpeza de chaves (ADR-0033).

## Decisão

**Polling com `FOR UPDATE SKIP LOCKED`, próxima versão estrita e funções `SECURITY DEFINER`.**

### Outbox

- `ledger.outbox`: um evento `EntryRecorded.v1` por lançamento (crédito, débito e estorno), gravado
  na transação do lançamento e da chave de idempotência. Recusa, repetição idempotente e transação
  desfeita não gravam evento. Abertura de conta e troca de limite não geram evento: não mudam a
  versão da conta.
- `id` é a ordem global de reserva; `(account_id, sequence)` é único.
- O `ledger_app` tem só `INSERT` no outbox.

### Funções do Worker

Todas criadas pelo `migrator`, `SECURITY DEFINER`, `SET search_path = ledger, pg_temp`, com
`EXECUTE` só ao `ledger_worker`:

| Função | Faz |
|--------|-----|
| `ledger.claim_outbox_batch(batch_size, after_id)` | Devolve até `batch_size` (1 a 1000) eventos pendentes com `id` maior que `after_id`, em ordem de `id`, travados com `FOR UPDATE SKIP LOCKED` até o fim da transação de quem chamou |
| `ledger.mark_outbox_processed(ids)` | Preenche `processed_at` dos ids ainda pendentes |
| `ledger.purge_processed_outbox(batch_size)` | Apaga até `batch_size` (1 a 10000) eventos processados há mais de 7 dias |

A tabela de roles do ADR-0033 fica assim para o `ledger_worker`: `EXECUTE` em
`purge_expired_idempotency_keys`, `claim_outbox_batch`, `mark_outbox_processed` e
`purge_processed_outbox`, e nenhum privilégio de tabela.

### Relay e projeção

- O relay (Worker) abre uma transação, reserva um lote, entrega cada evento à projeção de Positions
  e marca como processados os aplicados e os já aplicados. O "ainda não" segue pendente. Com o
  MongoDB fora, nada é marcado e o relay espera com backoff exponencial até 30 s.
- Um evento que nunca aplica (payload que não é um `EntryRecorded.v1` válido, ou uma conta com
  lacuna na sequência) fica pendente, com log `Error` no caso do payload. Para que 100 deles não
  ocupem todo lote para sempre, o relay guarda um cursor: um lote cheio sem nada aplicado faz o
  próximo começar depois do último `id` dele, e um lote incompleto (o fim da fila) devolve o cursor
  a 0.
- A projeção guarda, por conta, a versão projetada e o saldo, e um documento por lançamento. Aplica
  o evento só se `sequence == versão projetada + 1`. A gravação do lançamento e o avanço da versão
  são idempotentes: um evento reaplicado depois de uma queda entre aplicar e marcar não duplica nem
  altera nada.
- Com várias réplicas, o evento 2 reservado antes do 1 fica pendente e volta na rodada seguinte.
  Nenhuma réplica espera lock de outra.

### Usuários do MongoDB

O banco `positions` tem dois usuários, criados pelo init do container: `positions_reader` (`read`),
usado pela Api, e `positions_writer` (`readWrite`), usado pelo Worker. A Api não grava no modelo de
leitura.

### Consulta

Toda consulta lê a versão da conta no PostgreSQL, sem lock, e responde pelo MongoDB só quando a
versão projetada é igual. Senão, ou com erro ou timeout do MongoDB (ADR-0021), responde pelo
PostgreSQL (ADR-0007). A regra vale também para o saldo em um momento passado: a exceção opcional
do ADR-0007 não é implementada.

## Consequências

- **Positivas:**
  - Ordem por conta correta com qualquer número de réplicas, sem lock entre elas.
  - O Worker continua sem privilégio de tabela.
  - Nenhuma configuração de replicação no PostgreSQL.
- **Negativas:**
  - Polling: atraso médio de meio intervalo (`Outbox:PollInterval`, padrão 1 s) e uma consulta por
    intervalo mesmo sem eventos, barata pelo índice parcial dos pendentes.
  - Um evento antigo travado por uma réplica lenta segura os seguintes da mesma conta até ela
    terminar; enquanto isso, a consulta daquela conta vai ao PostgreSQL.
  - O modelo de leitura é derivado: reconstruí-lo é apagar o banco `positions` e zerar
    `processed_at` dos eventos ainda guardados. Eventos já apagados pela limpeza não voltam; a
    reconstrução completa a partir dos lançamentos fica para o runbook.
- **Em aberto:**
  - Métricas de atraso do outbox e taxa de fallback, e o alerta de atraso do ADR-0006 (F8).
  - Circuit breaker e health do MongoDB (ADR-0021, F7).

Explicação didática dos conceitos: [guia do modelo de leitura](../guias/modelo-de-leitura-explicado.md).
