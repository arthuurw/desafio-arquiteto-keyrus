# ADR-0006: CQRS com modelo de leitura separado via outbox

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

Uma das premissas do projeto é que picos de consulta não podem degradar o registro de
movimentações. O sistema legado apresenta lentidão e instabilidade em horários de pico.

A consulta de saldo em um momento já é eficiente no modelo de escrita (O(log n), ADR-0003).
O problema que resta é a **disputa de recursos**: consultas e escritas competindo por conexões,
CPU e I/O do mesmo banco. Além disso, consultas mais pesadas (extrato, histórico, agregações)
se beneficiam de um modelo desnormalizado e otimizado para leitura.

## Alternativas consideradas

1. **CQRS apenas no código, lendo do PostgreSQL.** Separa comandos e consultas na aplicação,
   mas não isola carga. Rejeitada: não resolve a disputa de recursos.
2. **Réplica de leitura do PostgreSQL.** Isola carga com poucos componentes. Rejeitada como
   solução principal: não oferece modelo desnormalizado para leitura e mantém o mesmo esquema
   otimizado para escrita. Continua válida como complemento em produção.
3. **Read model no próprio PostgreSQL** (tabela desnormalizada alimentada pelo outbox).
   Demonstra o padrão com um componente a menos. Rejeitada: não isola fisicamente a carga.
4. **CQRS com MongoDB como modelo de leitura, alimentado por outbox.** Isolamento físico de carga
   e modelo de leitura desnormalizado.

### Por que outbox e não escrita direta nos dois bancos

PostgreSQL e MongoDB não compartilham transação. Gravar nos dois na mesma requisição
(*dual write*) leva inevitavelmente a inconsistência em algum cenário de falha:

- Gravar no MongoDB antes do commit no PostgreSQL: se o commit falhar, o MongoDB exibe uma
  operação inexistente.
- Gravar no MongoDB após o commit: se o MongoDB falhar, a escrita já confirmada não pode ser
  desfeita atomicamente; compensação (estorno) também pode falhar.

Também foi descartado **desfazer a operação quando o modelo de leitura não puder ser atualizado**:
além de não ser atômico, tornaria a movimentação de dinheiro dependente da disponibilidade de um
componente secundário.

### Por que sem message broker

Um broker (RabbitMQ, Kafka) desacopla publicação e consumo e permite múltiplos consumidores.
Com um único consumidor (o projetor do modelo de leitura), ele adiciona um componente
operacional sem benefício proporcional no escopo atual. Registrado em
[melhorias futuras](../melhorias-futuras.md).

## Decisão

**Alternativa 4**, sem broker:

- **Modelo de escrita:** PostgreSQL, fonte da verdade. Toda validação de negócio (limite de
  saldo) usa exclusivamente este modelo.
- **Outbox:** na mesma transação que insere o lançamento e atualiza a conta (ADR-0005), um
  registro de evento é inserido na tabela de outbox. A operação só é confirmada ao cliente após
  esse commit.
- **Publicação e projeção:** um processo em segundo plano lê os eventos pendentes do outbox em
  ordem, aplica-os no MongoDB e marca-os como processados.
  - Entrega *at-least-once*: um evento pode ser aplicado mais de uma vez em caso de falha entre
    a aplicação e a marcação.
  - **Projeção idempotente:** cada documento do modelo de leitura guarda a `version` da conta já
    projetada; eventos com versão menor ou igual são ignorados.
  - **Ordem por conta preservada:** eventos de uma conta são aplicados em ordem de `sequence`.
  - Falha ao aplicar: retry com backoff; o evento permanece pendente até ser aplicado. Atraso
    acima de um limiar gera alerta.
- **Consultas:** atendidas pelo modelo de leitura, com garantia de frescor definida no ADR-0007.
- **Resposta do comando:** inclui `balance_after` e `version` do lançamento.

O MongoDB Community Server (licença SSPL) é gratuito e adequado ao projeto.

## Consequências

- **Positivas:**
  - Escrita nunca depende da disponibilidade do MongoDB.
  - Nenhum cenário de falha produz operação fantasma ou perdida no modelo de leitura: o outbox
    garante que todo lançamento confirmado será projetado.
  - Consultas pesadas não competem com escritas.
- **Negativas:**
  - Mais componentes: segundo banco, processo de projeção, tabela de outbox (que precisa de
    limpeza periódica dos eventos processados).
  - Modelo de leitura eventualmente consistente; a garantia de consulta correta depende do
    mecanismo do ADR-0007.
- **Em aberto:**
  - Biblioteca de mediação para comandos e consultas (MediatR ou alternativa), em ADR próprio.
  - Mecanismo de leitura do outbox (polling com `FOR UPDATE SKIP LOCKED` ou replicação lógica).
