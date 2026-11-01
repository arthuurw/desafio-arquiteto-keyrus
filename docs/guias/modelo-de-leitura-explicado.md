# Modelo de leitura explicado

Guia didático de como o saldo e o extrato saem do MongoDB sem nunca mostrar um saldo velho. As
decisões formais estão no [ADR-0006](../adr/0006-cqrs-com-modelo-de-leitura-separado.md) (outbox),
no [ADR-0007](../adr/0007-garantia-de-frescor-na-leitura.md) (frescor) e no
[ADR-0035](../adr/0035-relay-do-outbox-e-projecao-pela-proxima-versao.md) (relay e projeção).

**Situação:** tudo o que este guia descreve existe hoje, entregue com a funcionalidade F4
(`positions`). Só o runbook citado na seção 7 é previsto (F10, ver o
[roteiro](../roadmap.md)).

## 1. Por que dois bancos

O PostgreSQL é o livro-razão: trava a conta, decide se o débito cabe no limite e grava o
lançamento. Se as consultas de saldo e extrato lessem o mesmo banco, um pico de consultas
disputaria conexões, CPU e disco com o registro de movimentações. É o problema do legado.

As consultas vão para uma cópia separada, no MongoDB, desenhada para leitura: um documento por
conta com o saldo e um por lançamento. Essa cópia é o **modelo de leitura**.

> **Analogia:** o caixa do banco anota cada operação no livro oficial. Um quadro na parede mostra
> os saldos para quem chega. Quem quer saber o saldo olha o quadro e não atrapalha o caixa.

O risco do quadro é ficar atrasado. O resto deste guia é sobre como ele nunca mente.

## 2. O outbox: o recado na mesma transação

Gravar no PostgreSQL e no MongoDB na mesma requisição não funciona: os dois bancos não
compartilham transação. Se o MongoDB falhar depois do commit, o quadro perde a operação para sempre.

A solução é o **outbox**. Na mesma transação do lançamento, o Ledger grava também uma linha em
`ledger.outbox`: o evento `EntryRecorded.v1`, com os valores do lançamento. Ou os dois existem, ou
nenhum. Recusa, repetição idempotente e transação desfeita não deixam evento.

> **Analogia:** o caixa anota a operação no livro e, na mesma folha, escreve um recado "atualizar
> o quadro". Não existe operação sem recado, nem recado sem operação.

A Api só tem `INSERT` no outbox. Ler, marcar e apagar eventos é trabalho do Worker.

## 3. O relay: o Worker entrega os recados

O Worker roda o **relay** a cada `Outbox:PollInterval` (padrão 1 s, aceito entre 100 ms e 1 min):

1. Abre uma transação e chama `ledger.claim_outbox_batch(100, cursor)`: recebe até 100 eventos
   pendentes depois do cursor, em ordem de `id`, travados com `FOR UPDATE SKIP LOCKED`.
2. Entrega cada evento à **projeção**, que responde: aplicado, já aplicado ou ainda não.
3. Chama `ledger.mark_outbox_processed` para os aplicados e os já aplicados, e confirma.

Um lote cheio (100 eventos) indica que há mais pendentes: o próximo lote sai na hora, sem esperar o
intervalo.

`SKIP LOCKED` é o que permite várias réplicas do Worker: o evento travado por uma réplica é pulado
pela outra, e ninguém espera ninguém. Se o MongoDB cair, a transação é desfeita, nada é marcado, e
o relay espera 1 s, 2 s, 4 s... até 30 s antes de tentar de novo. Os eventos esperam no outbox.

O cursor existe por causa dos eventos que nunca aplicam (um payload corrompido, uma conta com
lacuna). Eles ficam pendentes; se fossem 100 seguidos, ocupariam todo lote e o Worker pararia de
projetar todas as contas. Um lote cheio em que nada aplicou faz o próximo começar depois dele, e o
fim da fila devolve o cursor ao início.

O Worker não tem privilégio de tabela: as três operações são funções `SECURITY DEFINER`, como a
limpeza de chaves (ADR-0038).

E os lançamentos gravados antes de o outbox existir? A migração que criou `ledger.outbox` gerou um
evento pendente para cada um deles, em ordem de `sequence` por conta. Na primeira rodada, o relay
os projeta como qualquer outro.

## 4. A projeção pela próxima versão estrita

Cada conta tem uma `version` que conta os lançamentos. O documento da conta no MongoDB guarda a
**versão projetada**: até onde o quadro chegou. A regra de aplicar um evento é uma só:

| `sequence` do evento | Resposta | Por quê |
|----------------------|----------|---------|
| menor ou igual à projetada | já aplicado | entrega repetida, nada muda |
| igual à projetada + 1 | aplicado | é exatamente o próximo |
| maior que a projetada + 1 | ainda não | falta o anterior; volta na rodada seguinte |

Por que "estrita"? Com duas réplicas, a réplica A pode travar o evento 1 de uma conta enquanto a B
pega o evento 2. Se a B aplicasse o 2, o quadro mostraria o saldo depois do 2 sem o 1, e a regra
"ignora versão menor" descartaria o 1 quando ele chegasse. Com a regra estrita, a B devolve "ainda
não", e o 2 entra logo depois do 1.

> **Analogia:** o quadro só aceita o recado número 8 se o último afixado foi o 7. Chegou o 9
> antes? Volta para a pilha.

A gravação também é idempotente: o lançamento é gravado antes de a versão avançar. Se o Worker cair
entre os dois passos, a nova entrega regrava o mesmo documento e avança a versão.

## 5. A consulta nunca mostra saldo velho

Toda consulta faz:

1. Lê no PostgreSQL a versão da conta, o saldo e o relógio do banco, numa leitura simples e sem
   lock (`ILedgerQueries`). Conta inexistente, ou de outro titular no modo delegado, vira `404`.
2. Lê o documento da conta no MongoDB, com teto de 300 ms (o padrão de `Positions:ReadTimeout`,
   aceito entre 10 ms e 10 s).
3. Versão projetada igual à do PostgreSQL: responde pelo MongoDB. Diferente, erro ou timeout:
   responde pelo PostgreSQL (o **fallback**).

Os dois caminhos devolvem o mesmo corpo, byte a byte: o extrato e o saldo em um momento usam só
lançamentos com `sequence` até a versão lida no passo 1. Um lançamento gravado no meio da consulta
não entra, por nenhum dos dois caminhos.

O passo 1 continua tocando o PostgreSQL, mas é uma leitura de uma linha pela chave primária. O
trabalho pesado (o extrato, o saldo em um instante) sai do MongoDB sempre que ele está em dia.

A consulta tem pressa; a projeção, não. Por isso o Worker espera o MongoDB por até 5 s: se ele
demorar mais, o relay tenta de novo depois.

Cada processo usa um usuário próprio no MongoDB, só com o que precisa no banco `positions`:

| Processo | Usuário | Permissão |
|----------|---------|-----------|
| Api | `positions_reader` | só leitura |
| Worker | `positions_writer` | leitura e gravação |

Mesmo com um defeito, a Api não consegue alterar o modelo de leitura.

## 6. Saldo em um momento e extrato

- **Saldo em `at`:** o `balanceAfter` do último lançamento com `recordedAt <= at`. O MongoDB guarda
  o instante em microssegundos (`recordedAtMicros`), a mesma precisão do PostgreSQL. Um `date` BSON
  guardaria só milissegundos, e os dois caminhos divergiriam para um `at` entre os dois.
- **`at` no futuro** é recusado com `400`: o futuro ainda pode receber lançamentos, e a mesma
  consulta daria respostas diferentes.
- **Extrato:** do mais recente ao mais antigo. O `nextCursor` carrega a sequência do último item, e
  a página seguinte traz só sequências menores. Lançamentos novos têm sequências maiores, então
  nunca deslocam a navegação: nada é pulado nem repetido.
- **Intervalo do extrato:** `from` entra, `to` não entra (`from <= recordedAt < to`). Os dois são
  opcionais; `from` depois de `to` é recusado com `400`.
- **Tamanho da página:** `limit` de 1 a 200, padrão 50.
- **Instantes** (`at`, `from`, `to`) exigem offset explícito, como `2026-10-01T12:00:00Z` ou
  `2026-10-01T09:00:00-03:00`. Sem offset, o instante dependeria do fuso de quem envia; por isso é
  recusado com `400`.

## 7. Limpeza e reconstrução

Eventos processados há mais de 7 dias são apagados em lotes, no mesmo ciclo da limpeza de chaves. O
modelo de leitura é derivado: apagar o banco `positions` e zerar `processed_at` dos eventos
guardados o reconstrói. Eventos já apagados pela limpeza não voltam; a reconstrução completa a
partir dos lançamentos ficará no runbook (previsto, F10).

## 8. Perguntas prováveis em entrevista

**Por que não gravar direto no MongoDB depois do commit?** Se o MongoDB falhar depois do commit, a
operação some do modelo de leitura, e não há como desfazer o commit. O outbox torna a entrega uma
consequência garantida do commit.

**Por que polling e não replicação lógica (CDC)?** CDC exige configurar o WAL, slots e um consumidor
do protocolo de replicação; um slot esquecido retém WAL até encher o disco. Polling com `SKIP LOCKED`
é uma consulta indexada nos pendentes. O custo é meio intervalo de atraso médio, que a regra de
frescor cobre.

**E se o MongoDB estiver atrasado?** A consulta percebe pela versão e responde pelo PostgreSQL. O
atraso custa desempenho, nunca correção.

**O evento pode ser aplicado duas vezes?** Pode ser entregue duas vezes (o Worker cai entre aplicar
e marcar). A projeção percebe pela versão e não muda nada: entrega *at-least-once*, efeito
*exactly-once*.

**Como várias réplicas não invertem a ordem de uma conta?** Pela próxima versão estrita: um evento
fora de ordem volta para a fila, e nenhuma réplica espera lock de outra.
