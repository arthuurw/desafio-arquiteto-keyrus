# ADR-0037: Idempotência por módulo, com hash de requisição sem dado pessoal reversível

- **Status:** Aceita
- **Data:** 2026-11-01
- **Substitui:** ADR-0032

## Contexto

Falhas de rede são inevitáveis: um chamador envia um débito, a conexão cai antes da resposta e o
chamador não sabe se a operação foi efetuada. O único comportamento seguro para ele é repetir a
requisição. Sem idempotência, a repetição duplica o efeito.

Requisito: **qualquer requisição de escrita pode ser repetida com segurança, quantas vezes for
necessário, produzindo o efeito uma única vez.**

O ADR-0032 decidiu o mecanismo para o Ledger. A F5 traz o segundo módulo com escrita, Customers,
e três pontos do ADR-0032 não servem para ele:

- O `IdempotencyBehavior` vivia no Ledger, preso ao `LedgerDbContext` e às recusas do Ledger.
- O `request_hash` é um SHA-256 sem chave dos valores interpretados. No cadastro de cliente, esses
  valores são nome, CPF, nascimento e e-mail. Com nome, e-mail e nascimento conhecidos, o CPF sai
  da linha da chave por força bruta: são cerca de 10⁹ CPFs válidos (ADR-0016).
- A limpeza das chaves vencidas era uma função do schema `ledger` executada pelo `ledger_worker`,
  que não alcança o schema `customers` (ADR-0038).

Este ADR substitui o ADR-0032 por inteiro.

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

### Como o hash é calculado em Customers

1. **SHA-256 sem chave, como no Ledger.** Rejeitada: o CPF sai da linha da chave por força bruta.
2. **Não guardar hash, só a chave.** Rejeitada: a mesma chave com outro corpo devolveria a resposta
   do primeiro, sem o `422`.
3. **HMAC-SHA256 pelo Transit do OpenBao, com chave própria (`customers-request-hash`)**, que nunca
   sai do cofre.

### Onde vive o behavior

1. **Uma cópia por módulo.** Rejeitada: duas implementações do mesmo fluxo divergem com o tempo.
2. **Um behavior no `BuildingBlocks`, fechado por módulo:** o fluxo é um só, e cada módulo informa
   a tabela, o `DbContext`, o cálculo do hash e as falhas transitórias dele.

### Limpeza das chaves vencidas

1. **Um único `DELETE`.** Rejeitada: preso ao timeout de comando.
2. **Laço dentro da função.** Rejeitada: uma função SQL roda numa transação só e volta ao mesmo
   timeout.
3. **Função que apaga um lote limitado, uma por módulo, chamada em laço pelo Worker,** cada chamada
   na própria transação e com a role de Worker do módulo.

## Decisão

**Mecanismo: alternativa 3. Hash: alternativa 2, calculado com a alternativa 3 em Customers.
Behavior: alternativa 2. Limpeza: alternativa 3.**

### Chave de idempotência

- **Obrigatória** em toda `POST`: abertura de conta, crédito, débito e estorno no Ledger, e
  cadastro de cliente em Customers. A chave é lida antes do corpo e do id da rota. Ausente: `400`
  `idempotency-key-missing`. Fora da forma textual padrão do UUID (com hífens, sem chaves) ou
  enviada com dois valores: `400` `idempotency-key-invalid`.
- Formato UUID, gerado pelo chamador por operação lógica (não por tentativa).
- **Escopo por client:** a unicidade é `(client_id, idempotency_key)`, então chaves de sistemas
  diferentes nunca colidem.
- Armazenada na tabela `idempotency_keys` do módulo que executa o comando (`ledger.idempotency_keys`,
  `customers.idempotency_keys`; cada módulo é dono do seu estado, ADR-0031), com `request_hash`,
  código de status e corpo da resposta.
- **Hash da requisição:** calculado sobre um array JSON com a operação (`open-account`, `credit`,
  `debit`, `reversal`, `register-customer`), os valores já interpretados do comando e o titular
  (modo do chamador e `customer_id`). Array e não concatenação: nenhum valor consegue se passar por
  dois campos. Com o titular no hash, outro cliente do mesmo client que reuse a chave recebe `422`,
  nunca a resposta do primeiro.
  - **Ledger:** SHA-256 do array (`RequestHash`). Os valores são financeiros, não pessoais.
  - **Customers:** HMAC-SHA256 do mesmo array pelo Transit do OpenBao, com a chave
    `customers-request-hash`, não exportável. Os 32 bytes decodificados de `vault:v<n>:<base64>`
    são o `request_hash`. Sem a chave, a linha não permite testar CPFs.
- Implementada como *pipeline behavior* do `Mediator` (ADR-0030), o `IdempotencyBehavior` do
  `BuildingBlocks`, que cada módulo fecha com o seu `DbContext`, a sua tabela, o seu cálculo de
  hash e as suas falhas transitórias, e registra no próprio mediador (ADR-0036). Aplicado aos
  comandos que implementam `IIdempotentCommand`. Um teste de arquitetura exige que todo comando do
  Ledger e de Customers implemente a interface, com a troca de limite do Ledger como única exceção
  nomeada.

### Troca de limite fora do behavior

`PUT /v1/accounts/{id}/credit-limit` não exige chave. O `PUT` define um valor absoluto: repetir
leva ao mesmo estado. A transação é do próprio handler, e qualquer recusa a desfaz.

### Fluxo, dentro da mesma transação da operação

0. Calcular o hash da requisição, antes de abrir a transação. Em Customers, o OpenBao fora do ar
   ou sem responder em 1 s responde `503` `personal-data-unavailable`, sem tocar a chave.
1. Inserir `(client_id, idempotency_key, request_hash)` com `ON CONFLICT DO NOTHING`.
2. Se a chave **não existir**: executar o comando, gravar a resposta na mesma linha, commit.
3. Se a chave **já existir e estiver confirmada**:
   - mesmo `request_hash`: devolver a resposta armazenada, sem reexecutar, com
     `Idempotent-Replayed: true`;
   - `request_hash` diferente: `422` `idempotency-key-reused` (reuso indevido da chave é defeito
     do chamador).
4. Se outra transação estiver processando a mesma chave, o PostgreSQL faz a inserção aguardar o
   término dela: se ela confirmar, aplica-se o passo 3; se ela falhar, esta transação prossegue.
   No Ledger, ao exceder o `lock_timeout` de 2 s, responder `409` `concurrency-timeout` (o chamador
   repete).
5. Se a chave **sumir entre o conflito e a leitura**, porque a limpeza a apagou enquanto esta
   transação esperava, a operação executa como nova, como para qualquer chave desconhecida. Nunca
   `500`.

Como chave, operação e resposta são confirmadas juntas, não existe estado intermediário em que a
operação ocorreu e a chave não foi registrada, nem o inverso.

### Quais resultados são armazenados

- **Sucesso e recusas de negócio determinísticas** (ex.: saldo insuficiente, conta inexistente,
  cliente não cadastrado, lançamento já estornado, referência externa duplicada, CPF já
  cadastrado): armazenados. A repetição devolve o mesmo resultado, mesmo que o estado mude depois.
  Uma nova tentativa da operação exige nova chave.
- **Falhas transitórias** (conta ocupada além do `lock_timeout`, OpenBao indisponível, erro
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
- A aplicação não tem `DELETE` (ADR-0038). A limpeza passa por uma função por módulo,
  `ledger.purge_expired_idempotency_keys(batch_size integer)` e
  `customers.purge_expired_idempotency_keys(batch_size integer)`, ambas `RETURNS bigint`, criadas
  pela migração com `SECURITY DEFINER` e `search_path` fixo no schema do módulo e `pg_temp`. Cada
  uma apaga até `batch_size` chaves com `created_at < now() - interval '7 days'` e devolve quantas
  apagou. `batch_size` fora de 1 a 10000 levanta erro, sem apagar nada. O prazo vive na função:
  quem tem `EXECUTE` não consegue apagar chave recente.
- O Worker chama cada função com lote de **1000**, com a role de Worker do módulo (`ledger_worker`,
  `customers_worker`, ADR-0038), cada chamada na própria transação, até uma chamada apagar menos de
  1000, e registra no log o total da rodada. A primeira rodada roda na subida, e as seguintes a
  cada `Idempotency:PurgeInterval` (padrão 1 hora).
- `Idempotency:PurgeInterval` é validado na subida do Worker: fora de 1 s a 1 dia, o processo
  encerra com erro que cita a configuração, sem chegar a servir.
- Uma falha numa rodada só adia a limpeza para a rodada seguinte; o Worker segue de pé, e a falha
  de um módulo não impede a limpeza do outro.

## Consequências

- **Positivas:**
  - Repetição segura de qualquer escrita com chave, inclusive sob retries concorrentes.
  - Duas barreiras independentes contra lançamento duplicado.
  - Referência externa viabiliza conciliação com sistemas de origem.
  - A mesma operação com outra formatação dos valores é reconhecida como repetição, inclusive o CPF
    com ou sem pontuação.
  - A linha da chave de Customers não revela dado pessoal, nem por força bruta.
  - Um fluxo de idempotência só, compartilhado pelos módulos.
  - A limpeza termina sempre, qualquer que seja o acúmulo: cada chamada fica bem abaixo do
    timeout de comando.
- **Negativas:**
  - Armazenamento adicional por requisição de escrita, limitado pela retenção.
  - Chamadores precisam gerar e reutilizar a chave corretamente; documentado na OpenAPI.
  - O cadastro de cliente depende do OpenBao também para o hash: com o cofre fora, nem a repetição
    de um cadastro já confirmado responde.
  - Uma repetição que chega depois da retenção, ou cuja chave é apagada enquanto espera, executa
    como nova. A referência externa, permanente, segue barrando o lançamento duplicado, e o blind
    index do CPF segue barrando o cadastro duplicado.
- **Verificação:**
  - Testes de integração para repetição sequencial, repetição concorrente, reuso com conteúdo
    diferente, falha transitória seguida de repetição, referência externa duplicada, header
    inválido, chave apagada durante a espera, limpeza em lotes nos dois módulos e o hash de
    Customers igual ao HMAC do Transit e diferente do SHA-256 sem chave.
