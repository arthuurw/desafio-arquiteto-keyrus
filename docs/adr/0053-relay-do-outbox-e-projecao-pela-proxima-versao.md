# ADR-0053: Relay do outbox por polling e projeção pela próxima versão estrita

- **Status:** Aceita
- **Data:** 2026-11-02
- **Substitui:** ADR-0035

## Contexto

O ADR-0006 decidiu o modelo de leitura no MongoDB alimentado por um outbox gravado na transação do
lançamento. O ADR-0009 separou a Api do Worker e previu várias réplicas do Worker lendo o outbox
com `FOR UPDATE SKIP LOCKED`. O ADR-0044 deu ao `ledger_worker` só `EXECUTE` nas funções do Worker,
sem privilégio de tabela.

O ADR substituído respondeu como o outbox é lido, como a ordem por conta sobrevive a várias réplicas
e com que privilégio o Worker reserva e marca eventos. Ele também tinha uma seção de consulta: toda
leitura, inclusive a de um instante passado, lia a versão da conta sem lock. O ADR-0045 trocou essa
decisão: a leitura com instante espera a escrita em voo na conta. A seção ficou desatualizada, e ADR
aceito não é editado no mérito.

Depois dele, o relay ganhou dois comportamentos que nenhum ADR registra: o evento que o MongoDB
recusa fica pendente sem segurar o lote, e o lote em andamento termina antes de o Worker sair.

## Alternativas consideradas

### Como tratar a seção de consulta

1. **Editar o ADR aceito.** Rejeitada: ADR aceito não é editado no mérito (ADR-0034).
2. **Um ADR novo que repete as decisões do relay e da projeção, ainda vigentes, e remete a consulta
   ao ADR-0045.**

### Leitura do outbox

1. **Replicação lógica (CDC).** Entrega em ordem de commit, sem polling. Exige configurar
   `wal_level`, slots e um consumidor do protocolo de replicação; um slot esquecido retém WAL até
   encher o disco.
2. **Polling com `FOR UPDATE SKIP LOCKED`.** Uma consulta indexada nos pendentes a cada intervalo;
   várias réplicas dividem o trabalho sem coordenação.

### Ordem por conta

Com `SKIP LOCKED`, a réplica A pode travar o evento 1 de uma conta enquanto a réplica B reserva o
evento 2 da mesma conta. Se B aplicar o 2 antes do 1, a regra "ignora versão menor ou igual" do
ADR-0006 descartaria o 1 quando chegasse.

1. **Uma réplica só, ou um lock por conta no relay.** A réplica única não escala, e o lock por conta
   acrescenta a espera que o `SKIP LOCKED` existe para evitar.
2. **Próxima versão estrita.** A projeção aplica o evento só quando `sequence` é a versão projetada
   mais 1.

### Privilégio do Worker

1. **`SELECT` e `UPDATE (processed_at)` no outbox para o `ledger_worker`.** Dá DML a um processo
   que só precisa de três operações.
2. **Funções `SECURITY DEFINER`**, no desenho do ADR-0044.

## Decisão

**Novo ADR. Polling com `FOR UPDATE SKIP LOCKED`, próxima versão estrita e funções
`SECURITY DEFINER`.**

### Outbox

- `ledger.outbox` recebe um evento `EntryRecorded.v1` por lançamento (crédito, débito e estorno),
  gravado na transação do lançamento. Recusa, repetição idempotente e transação desfeita não gravam
  evento. Abertura de conta e troca de limite não geram evento: não mudam a versão da conta.
- `id` é a ordem de reserva; `(account_id, sequence)` é único.
- O `ledger_app` só insere no outbox.

### Funções do Worker

O `ledger_worker` toca o outbox só por funções `SECURITY DEFINER`, uma operação cada, com
`search_path` fixo e `EXECUTE` só a ele:

- `claim_outbox_batch` reserva um lote de pendentes depois de um cursor, em ordem de `id`, com
  `FOR UPDATE SKIP LOCKED` até o fim da transação de quem chamou;
- outra marca como processados os eventos entregues;
- outra apaga, em lotes, os processados há mais de 7 dias, no ciclo da limpeza de chaves de
  idempotência. Uma falha só adia a limpeza.

Os limites de cada função e os grants vivem na migração do outbox (ADR-0046).

### Relay e projeção

- O relay (Worker) abre uma transação, reserva um lote, entrega cada evento à projeção de Positions
  e marca como processados os aplicados e os já aplicados, no mesmo commit.
- A projeção guarda, por conta, a versão projetada e o saldo, e um documento por lançamento. Aplica
  o evento só se `sequence` é a versão projetada mais 1. Versão menor ou igual já foi aplicada;
  versão maior fica pendente e volta numa rodada seguinte, quando a anterior já chegou. Nenhuma
  réplica espera lock de outra.
- O lançamento é gravado antes do avanço da versão, e os dois são idempotentes: uma entrega repetida
  depois de uma queda regrava o mesmo documento e não duplica nada.
- Fica pendente, sem segurar o lote, com log `Error`: o payload que não é um `EntryRecorded.v1`
  válido e o documento que o MongoDB recusa (erro de escrita, não de conexão). O evento "ainda não"
  também fica pendente.
- Cursor: um lote cheio sem nada marcado faz o próximo começar depois do último `id` dele, para que
  eventos que nunca aplicam não ocupem todo lote. Um lote incompleto devolve o cursor a 0.
- Qualquer outra falha no lote, como o MongoDB fora do ar ou acima do prazo, desfaz a transação, e
  nada é marcado. O lote volta depois de uma espera crescente, de 1 s até 30 s.
- No encerramento, o lote em andamento roda até o commit, sem o token de parada; a espera entre
  lotes é que termina (ADR-0021).

### Usuários do MongoDB

O banco `positions` tem dois usuários, criados pelo init do container: `positions_reader` (`read`),
usado pela Api, e `positions_writer` (`readWrite`), usado pelo Worker. A Api não grava no modelo de
leitura.

### Consulta

A consulta de saldo e extrato segue o ADR-0045. Este ADR não decide nada sobre o caminho de
leitura.

## Consequências

- **Positivas:**
  - Ordem por conta correta com qualquer número de réplicas, sem lock entre elas.
  - O Worker continua sem privilégio de tabela.
  - Nenhuma configuração de replicação no PostgreSQL.
  - Um evento que nunca aplica não para a projeção das outras contas.
- **Negativas:**
  - Polling: atraso médio de meio intervalo (`Outbox:PollInterval`, padrão 1 s) e uma consulta por
    intervalo mesmo sem eventos, barata pelo índice parcial dos pendentes.
  - Um evento travado por uma réplica lenta segura os seguintes da mesma conta até ela terminar;
    enquanto isso, a versão projetada daquela conta fica atrás.
  - Um evento recusado ou malformado fica pendente até alguém agir, e só o log `Error` o aponta.
  - O modelo de leitura é derivado: reconstruí-lo é apagar o banco `positions` e zerar
    `processed_at` dos eventos ainda guardados. Eventos já apagados pela limpeza não voltam; a
    reconstrução completa a partir dos lançamentos fica para o runbook.
- **Em aberto:**
  - Métricas de atraso do outbox e o alerta de atraso do ADR-0006.

Explicação didática dos conceitos: [guia do modelo de leitura](../guias/modelo-de-leitura-explicado.md).
