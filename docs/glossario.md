# Glossário (linguagem ubíqua)

Correspondência entre os termos de negócio (português) e os nomes usados no código (inglês),
conforme o [ADR-0011](adr/0011-convencao-de-idioma.md).

## Ledger

| Termo de negócio | Código | Definição |
|------------------|--------|-----------|
| Conta | `Account` | Agregado que mantém saldo, limite e versão. Fronteira de consistência das movimentações. |
| Titular | `CustomerId` | Cliente dono da conta, referenciado só pelo identificador, sem dado pessoal. A titularidade é o vínculo entre a conta e o titular: no modo delegado, o chamador só alcança contas do cliente em nome de quem age e só abre conta para ele; fora disso, a resposta é `404` (ADR-0013). |
| Lançamento | `Entry` | Registro imutável de uma movimentação em uma conta. Nunca é alterado ou excluído. |
| Crédito | `Credit` | Lançamento que aumenta o saldo. |
| Débito | `Debit` | Lançamento que diminui o saldo. Sujeito ao limite. |
| Tipo de lançamento | `EntryType` | Crédito ou débito. O sentido da movimentação vem só do tipo, porque o valor do lançamento é sempre positivo. O estorno tem o tipo oposto ao do lançamento estornado. |
| Estorno | `Reversal` | Lançamento que anula o efeito de um lançamento anterior: mesmo valor, tipo oposto e categoria `REVERSAL`. Registrado por `Account.Reverse`. |
| Lançamento estornado | `ReversedEntryId` | Lançamento anulado por um estorno, que guarda o id dele em `ReversedEntryId`. Cada lançamento tem no máximo um estorno, e um estorno não é estornado. |
| Limite de crédito | `CreditLimit` | Valor não negativo até o qual o saldo pode ficar negativo. Padrão 0. |
| Saldo | `Balance` | Valor consolidado da conta. |
| Teto de saldo | `MaxBalance` | Maior saldo que uma conta pode ter: 999999999999999.99 (`Account.MaxBalance`). Um crédito, ou o estorno de um débito, que passaria dele é recusado com `422` `balance-limit-exceeded`. |
| Dinheiro | `Money` | Value object com valor (`decimal`) e moeda. Apenas BRL nesta versão (ADR-0018). |
| Saldo após o lançamento | `BalanceAfter` | Saldo acumulado da conta imediatamente após um lançamento. |
| Momento de registro | `RecordedAt` | Instante, atribuído pelo servidor, em que o lançamento foi gravado. |
| Sequência | `Sequence` | Número de ordem do lançamento dentro da conta. |
| Versão | `Version` | Contador incrementado a cada lançamento na conta; usado na projeção e na verificação de frescor. |
| Recusa de negócio | `LedgerError` | Motivo pelo qual o Ledger não executa uma operação válida na forma, como saldo insuficiente ou lançamento já estornado. Cada recusa tem um `type` estável em Problem Details (ADR-0039). Nas operações com chave de idempotência, a recusa é guardada com a chave, exceto a conta ocupada. |
| Conta ocupada | `ConcurrencyTimeout` | Recusa quando a conta segue travada por outra requisição depois do `lock_timeout` de 2 s: `409` `concurrency-timeout`. É transitória: a chave de idempotência não é consumida, e o chamador repete. |
| Categoria | `Category` | Tipo codificado do lançamento (ex.: `PIX_IN`, `TED_OUT`). Lançamentos não têm descrição livre (ADR-0017). |
| Catálogo de categorias | `Categories` | Lista fechada de categorias por tipo. Crédito: `PIX_IN`, `TED_IN`, `BOLETO_IN`, `DEPOSIT`, `ADJUSTMENT_IN`. Débito: `PIX_OUT`, `TED_OUT`, `BOLETO_OUT`, `CARD`, `FEE`, `ADJUSTMENT_OUT`. `REVERSAL` só no estorno, atribuída pela Api. |
| Referência externa | `ExternalReference` | Identificador da operação no sistema de origem; único por client (ADR-0037). |
| Chave de idempotência | `IdempotencyKey` | UUID gerado pelo chamador por operação lógica e enviado no cabeçalho `Idempotency-Key`; única por client e mantida por 7 dias (ADR-0037). |
| Comando idempotente | `IIdempotentCommand` | Comando de escrita executado uma única vez por chave de idempotência. Todos os comandos do Ledger, exceto a troca de limite. |
| Hash da requisição | `RequestHash` | SHA-256 da operação, dos valores já interpretados e do titular; decide se a mesma chave traz o mesmo conteúdo. |
| Repetição | `Replayed` | Resposta devolvida do armazenamento, sem reexecutar a operação, marcada com `Idempotent-Replayed: true`. |
| Resultado de escrita | `WriteResult` | Status e corpo da resposta de um comando idempotente, guardados com a chave. |
| Limpeza de chaves | `IdempotencyKeyPurge` | Rotina do Worker que apaga as chaves com mais de 7 dias e, no mesmo ciclo, aciona a limpeza do outbox. |

## Positions

| Termo de negócio | Código | Definição |
|------------------|--------|-----------|
| Posição consolidada | `Position` | Visão de leitura de uma conta no MongoDB: a versão projetada e o saldo depois dela. Fica no banco `positions`, um documento por conta na coleção `positions` e um por lançamento projetado na coleção `entries`. |
| Saldo em um momento | `BalanceAt` | Saldo da conta em um instante T: `BalanceAfter` do último lançamento com `RecordedAt <= T`, com precisão de microssegundo; sem lançamento até T, zero. |
| Extrato | `Statement` | Lançamentos de uma conta com `from <= RecordedAt < to`, do mais recente ao mais antigo, paginados por cursor. |
| Outbox | `OutboxEvent` | Tabela `ledger.outbox`: um evento `EntryRecorded.v1` por lançamento, gravado na mesma transação dele (ADR-0006). |
| Evento pendente | `claim_outbox_batch` | Evento do outbox ainda sem `processed_at`; o Worker o reserva para entregar à projeção. |
| Evento processado | `mark_outbox_processed` | Evento já aplicado ao modelo de leitura, marcado com `processed_at`; apagado depois de 7 dias. |
| Limpeza do outbox | `OutboxPurge` | Apaga em lotes os eventos processados há mais de 7 dias, no mesmo ciclo da limpeza de chaves (ADR-0035). |
| Relay do outbox | `OutboxRelay` | Rotina do Worker que reserva os eventos pendentes em lotes, entrega cada um à projeção e marca os aplicados (ADR-0035). |
| Projeção | `PositionProjection` | Aplicação de um evento ao modelo de leitura, só quando ele é a próxima versão da conta (ADR-0035). |
| Versão projetada | `Position.Version` | Até que versão da conta o modelo de leitura chegou; 0 para conta sem lançamento projetado. |
| Consulta ao Ledger | `ILedgerQueries` | Leitura do Ledger oferecida aos outros módulos, sem lock e sem expor as tabelas: retrato da conta, último lançamento até um instante e página do extrato (ADR-0031). |
| Timeout de leitura | `ReadTimeout` | Prazo de cada leitura no MongoDB: 300 ms por padrão (`Positions:ReadTimeout`, de 10 ms a 10 s). Passado dele, a resposta vem do Ledger (ADR-0007). |
| Fallback | `FreshRead` | Resposta pelo Ledger quando a versão projetada difere da versão da conta, ou o MongoDB falha ou passa do timeout; mesmos valores pelos dois caminhos (ADR-0007). |
| Retrato da conta | `AccountSnapshot` | Versão, saldo e relógio do banco lidos no Ledger no início da consulta; as duas respostas, pelo MongoDB ou pelo Ledger, param nessa versão. |
| Resultado do consumo | `ConsumeResult` | O que a projeção fez com o evento: aplicado (`Applied`), já aplicado (`AlreadyApplied`) ou ainda não (`NotYet`), quando falta a versão anterior. No log do relay, "ainda não" aparece como `deferred`. |
| Cursor do extrato | `StatementCursor` | Texto opaco com a sequência do último item da página; a página seguinte traz só sequências menores. |

## Customers

| Termo de negócio | Código | Definição |
|------------------|--------|-----------|
| Cliente | `Customer` | Pessoa titular de contas, maior de idade. Detém os dados pessoais: nome, CPF, data de nascimento e e-mail. Só cliente cadastrado abre conta. |
| CPF | `Cpf` | Documento de identificação do cliente, com os dígitos verificadores conferidos. Guardado com os 11 dígitos, cifrado; com ou sem pontuação é o mesmo CPF. Único entre os clientes. |
| Dados pessoais | `CustomerField` | Os quatro campos do cliente, cada um cifrado com AES-256-GCM e com o id do cliente e o nome do campo como dados associados (ADR-0016). |
| DEK | `IKeyStore` | Chave de dados de 256 bits, uma por cliente, que cifra os dados pessoais dele. Fica no OpenBao, nunca no banco; na memória da Api, por no máximo 5 minutos (`DekCache`). |
| Blind index | `IBlindIndexer` | HMAC-SHA256 do CPF calculado pelo Transit do OpenBao, com chave que nunca sai do cofre. Permite achar e recusar o CPF repetido sem guardar o CPF legível. |
| Máscara | `CustomerMask` | Forma do cliente na leitura com `customers.read`: primeiro nome e iniciais, `***.982.247-**`, `****-**-15`, `a***@dominio`. |
| Dados pessoais completos | `personal-data` | Leitura do cliente sem máscara, em rota própria e com o escopo `customers.pii.read`; todo acesso gera um log `Audit` só com identificadores. |
| Cofre | `OpenBaoClient` | O OpenBao: guarda as DEKs e calcula os HMACs. Fora do ar, só cadastro e leitura de cliente param (`personal-data-unavailable`); o Ledger segue. |

## Identidade

| Termo de negócio | Código | Definição |
|------------------|--------|-----------|
| Client | `ClientId` | Sistema registrado no provedor de identidade que obtém tokens, como `backoffice` ou `channel-app`. Não confundir com cliente (`Customer`). |
| Canal | - | Sistema que atende o cliente final, como app ou internet banking, e chama a Api em nome dele no modo delegado. |
| Chamador | `Caller` | Quem chama a Api: o client e, no modo delegado, o cliente final em nome de quem ele age (ADR-0013). |
| Modo delegado | `Delegated` | Canal agindo em nome do cliente logado; o token carrega `customer_id` e não carrega `client_id`. |
| Modo serviço | `Service` | Sistema agindo por conta própria, sem usuário; o token carrega `client_id` e não carrega `customer_id`. |
| Escopo | `Scopes` | Capacidade concedida a um client, como `ledger.credits.write` (`Scopes.LedgerCreditsWrite`); cada rota exige exatamente um. |

## Integração

| Termo de negócio | Código | Definição |
|------------------|--------|-----------|
| Lançamento registrado | `EntryRecorded` | Evento de integração publicado pelo Ledger via outbox, tipo `EntryRecorded.v1`, com os valores do lançamento. |
