# ADR-0032: Idempotência por chave de requisição e referência externa, com limpeza em lotes

- **Status:** Aceita
- **Data:** 2026-10-31
- **Substitui:** ADR-0014

## Contexto

Falhas de rede são inevitáveis: um chamador envia um débito, a conexão cai antes da resposta e o
chamador não sabe se a operação foi efetuada. O único comportamento seguro para ele é repetir a
requisição. Sem idempotência, a repetição duplica o lançamento.

Requisito: **qualquer requisição de escrita pode ser repetida com segurança, quantas vezes for
necessário, produzindo o efeito uma única vez.**

O ADR-0014 decidiu o mecanismo. O código construído diverge dele em pontos que precisam ficar
registrados:

- O `request_hash` não é o SHA-256 da rota e do corpo canônico, mas da operação, dos valores já
  interpretados e do titular (`RequestHash`).
- A chave é obrigatória nas quatro `POST` do Ledger. A troca de limite
  (`PUT /v1/accounts/{id}/credit-limit`) fica fora do behavior de idempotência, e não "todos os
  comandos".
- A limpeza das chaves vencidas era um único `DELETE`. Preso ao timeout de comando de 30 s, ele
  estoura sempre depois de uma parada longa do Worker, e a tabela cresce sem limite.
- Se a limpeza apaga uma chave enquanto uma repetição espera por ela, a repetição respondia `500`.

Este ADR substitui o ADR-0014 por inteiro.

## Alternativas consideradas

### Mecanismo

1. **Header `Idempotency-Key`** (draft IETF *The Idempotency-Key HTTP Header Field*), com a chave e
   a resposta armazenadas na mesma transação da operação.
   - Prós: genérico para qualquer comando; devolve a resposta original; padrão conhecido.
   - Contras: depende de o mecanismo estar corretamente aplicado em todos os comandos.
2. **Referência de negócio no lançamento** (`external_reference`, ex.: identificador fim a fim do
   PIX) com constraint de unicidade.
   - Prós: natural ao domínio; essencial para conciliação com o sistema de origem.
   - Contras: cobre apenas lançamentos; não armazena a resposta original.
3. **Alternativa 1 como mecanismo principal e alternativa 2 como barreira final no banco.**

### O que o hash compara

1. **Rota e corpo canônico.** Rejeitada: `"10.0"` e `"10.00"` seriam requisições diferentes, e a
   canonicalização do corpo vira uma segunda interpretação da entrada.
2. **Operação, valores interpretados e titular.** O hash vê o que o comando executa.

### Limpeza das chaves vencidas

1. **Um único `DELETE`** (o que existia). Rejeitada: preso ao timeout de comando.
2. **Laço dentro da função.** Rejeitada: uma função SQL roda numa transação só e volta ao mesmo
   timeout.
3. **Função que apaga um lote limitado, chamada em laço pelo Worker,** cada chamada na própria
   transação.

## Decisão

**Mecanismo: alternativa 3. Hash: alternativa 2. Limpeza: alternativa 3.**

### Chave de idempotência

- **Obrigatória** nas quatro `POST` do Ledger: abertura de conta, crédito, débito e estorno. A
  chave é lida antes do corpo e do id da rota. Ausente: `400` `idempotency-key-missing`. Fora da
  forma textual padrão do UUID (com hífens, sem chaves) ou enviada com dois valores: `400`
  `idempotency-key-invalid`.
- Formato UUID, gerado pelo chamador por operação lógica (não por tentativa).
- **Escopo por client:** a unicidade é `(client_id, idempotency_key)`, então chaves de sistemas
  diferentes nunca colidem.
- Armazenada na tabela `idempotency_keys` do módulo que executa o comando (cada módulo é dono do
  seu estado, ADR-0031), com `request_hash`, código de status e corpo da resposta.
- **`RequestHash`:** SHA-256 de um array JSON com a operação (`open-account`, `credit`, `debit`,
  `reversal`), os valores já interpretados do comando e o titular (modo do chamador e
  `customer_id`). Array e não concatenação: nenhum valor consegue se passar por dois campos. Com o
  titular no hash, outro cliente do mesmo client que reuse a chave recebe `422`, nunca a resposta
  do primeiro.
- Implementada como *pipeline behavior* do `Mediator` (ADR-0030), o `IdempotencyBehavior`,
  aplicado aos comandos que implementam `IIdempotentCommand`. Um teste de arquitetura exige que
  todo comando do Ledger implemente a interface, com a troca de limite como única exceção nomeada.

### Troca de limite fora do behavior

`PUT /v1/accounts/{id}/credit-limit` não exige chave. O `PUT` define um valor absoluto: repetir
leva ao mesmo estado. A transação é do próprio handler, e qualquer recusa a desfaz.

### Fluxo, dentro da mesma transação da operação

1. Inserir `(client_id, idempotency_key, request_hash)` com `ON CONFLICT DO NOTHING`.
2. Se a chave **não existir**: executar o comando, gravar a resposta na mesma linha, commit.
3. Se a chave **já existir e estiver confirmada**:
   - mesmo `request_hash`: devolver a resposta armazenada, sem reexecutar, com
     `Idempotent-Replayed: true`;
   - `request_hash` diferente: `422` `idempotency-key-reused` (reuso indevido da chave é defeito
     do chamador).
4. Se outra transação estiver processando a mesma chave, o PostgreSQL faz a inserção aguardar o
   término dela: se ela confirmar, aplica-se o passo 3; se ela falhar, esta transação prossegue.
   Ao exceder o `lock_timeout` de 2 s, responder `409` `concurrency-timeout` (o chamador repete).
5. Se a chave **sumir entre o conflito e a leitura**, porque a limpeza a apagou enquanto esta
   transação esperava, a operação executa como nova, como para qualquer chave desconhecida. Nunca
   `500`.

Como chave, operação e resposta são confirmadas juntas, não existe estado intermediário em que a
operação ocorreu e a chave não foi registrada, nem o inverso.

### Quais resultados são armazenados

- **Sucesso e recusas de negócio determinísticas** (ex.: saldo insuficiente, conta inexistente,
  lançamento já estornado, referência externa duplicada): armazenados. A repetição devolve o
  mesmo resultado, mesmo que o estado da conta mude depois. Uma nova tentativa da operação exige
  nova chave.
- **Falhas transitórias** (conta ocupada além do `lock_timeout`, indisponibilidade, erro
  inesperado): a transação é desfeita e a chave não é consumida, permitindo que a repetição
  execute a operação.
- **Erros de entrada** (`400` do corpo ou da chave) acontecem no endpoint, antes do behavior
  (ADR-0030): nada é armazenado.

### Referência externa no lançamento

- Todo lançamento exige `external_reference`, a identificação da operação no sistema de origem.
- Índice único `(client_id, external_reference)` na tabela de lançamentos.
- Se uma requisição com chave nova trouxer uma referência já registrada: `409`
  `duplicate-external-reference`. Os ids do lançamento existente só vão na resposta quando o
  chamador pode ver a conta dele (ADR-0013). A consulta recusa o caso comum; o índice único recusa
  a corrida entre contas diferentes. Isso cobre o caso de o chamador perder a chave original, e
  protege contra defeito no mecanismo de chave.

### Retenção e limpeza em lotes

- Chaves mantidas por **7 dias**. A referência externa é permanente, pois faz parte do lançamento.
- A aplicação não tem `DELETE` (ADR-0033). A limpeza passa pela função
  `ledger.purge_expired_idempotency_keys(batch_size integer) RETURNS bigint`, criada pela
  migração com `SECURITY DEFINER` e `SET search_path = ledger, pg_temp`. Ela apaga até
  `batch_size` chaves com `created_at < now() - interval '7 days'` e devolve quantas apagou.
  `batch_size` fora de 1 a 10000 levanta erro, sem apagar nada. O prazo vive na função: quem tem
  `EXECUTE` não consegue apagar chave recente.
- O Worker chama a função com lote de **1000**, cada chamada na própria transação, até uma chamada
  apagar menos de 1000, e registra no log o total da rodada. A primeira rodada roda na subida, e as
  seguintes a cada `Idempotency:PurgeInterval` (padrão 1 hora).
- `Idempotency:PurgeInterval` é validado na subida do Worker: fora de 1 s a 1 dia, o processo
  encerra com erro que cita a configuração, sem chegar a servir.
- Uma falha numa rodada só adia a limpeza para a rodada seguinte; o Worker segue de pé.

## Consequências

- **Positivas:**
  - Repetição segura de qualquer escrita com chave, inclusive sob retries concorrentes.
  - Duas barreiras independentes contra lançamento duplicado.
  - Referência externa viabiliza conciliação com sistemas de origem.
  - A mesma operação com outra formatação dos valores é reconhecida como repetição.
  - A limpeza termina sempre, qualquer que seja o acúmulo: cada chamada fica bem abaixo do
    timeout de comando.
- **Negativas:**
  - Armazenamento adicional por requisição de escrita, limitado pela retenção.
  - Chamadores precisam gerar e reutilizar a chave corretamente; documentado na OpenAPI.
  - Uma repetição que chega depois da retenção, ou cuja chave é apagada enquanto espera, executa
    como nova. A referência externa, permanente, segue barrando o lançamento duplicado.
- **Verificação:**
  - Testes de integração para repetição sequencial, repetição concorrente, reuso com conteúdo
    diferente, falha transitória seguida de repetição, referência externa duplicada, header
    inválido, chave apagada durante a espera e limpeza em lotes.
