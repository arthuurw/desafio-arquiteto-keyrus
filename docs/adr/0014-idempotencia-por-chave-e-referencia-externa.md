# ADR-0014: Idempotência por chave de requisição e referência externa

- **Status:** Substituída por ADR-0032
- **Data:** 2026-10-29

## Contexto

Falhas de rede são inevitáveis: um chamador envia um débito, a conexão cai antes da resposta e o
chamador não sabe se a operação foi efetuada. O único comportamento seguro para ele é repetir a
requisição. Sem idempotência, a repetição duplica o lançamento.

Requisito: **qualquer requisição de escrita pode ser repetida com segurança, quantas vezes for
necessário, produzindo o efeito uma única vez.**

## Alternativas consideradas

1. **Header `Idempotency-Key`** (draft IETF *The Idempotency-Key HTTP Header Field*), com a chave e
   a resposta armazenadas na mesma transação da operação.
   - Prós: genérico para qualquer comando; devolve a resposta original; padrão conhecido.
   - Contras: depende de o mecanismo estar corretamente aplicado em todos os comandos.
2. **Referência de negócio no lançamento** (`external_reference`, ex.: identificador fim a fim do
   PIX) com constraint de unicidade.
   - Prós: natural ao domínio; essencial para conciliação com o sistema de origem.
   - Contras: cobre apenas lançamentos; não armazena a resposta original.
3. **Alternativa 1 como mecanismo principal e alternativa 2 como barreira final no banco.**

## Decisão

**Alternativa 3.**

### Chave de idempotência

- **Obrigatória** em todo endpoint que altera estado (`POST`). Ausente ou malformada: `400`.
- Formato UUID, gerado pelo chamador por operação lógica (não por tentativa).
- **Escopo por client:** a unicidade é `(client_id, idempotency_key)`, então chaves de sistemas
  diferentes nunca colidem.
- Armazenada na tabela `idempotency_keys` do módulo que executa o comando (cada módulo é dono do
  seu estado, ADR-0010), com `request_hash` (SHA-256 da rota e do corpo canônico), código de
  status e corpo da resposta.
- Implementada como *pipeline behavior* do Mediator (ADR-0008), aplicada a todos os comandos.

### Fluxo, dentro da mesma transação da operação

1. Inserir `(client_id, idempotency_key, request_hash)`.
2. Se a chave **não existir**: executar o comando, gravar a resposta na mesma linha, commit.
3. Se a chave **já existir e estiver confirmada**:
   - mesmo `request_hash`: devolver a resposta armazenada, sem reexecutar;
   - `request_hash` diferente: `422` (reuso indevido da chave é defeito do chamador).
4. Se outra transação estiver processando a mesma chave, o PostgreSQL faz a inserção aguardar o
   término dela: se ela confirmar, aplica-se o passo 3; se ela falhar, esta transação prossegue.
   Ao exceder o `lock_timeout`, responder `409` (o chamador repete).

Como chave, operação e resposta são confirmadas juntas, não existe estado intermediário em que a
operação ocorreu e a chave não foi registrada, nem o inverso.

### Quais resultados são armazenados

- **Sucesso e rejeições de negócio determinísticas** (ex.: saldo insuficiente, conta inexistente):
  armazenados. A repetição devolve o mesmo resultado, mesmo que o estado da conta mude depois.
  Uma nova tentativa da operação exige nova chave.
- **Falhas transitórias** (timeout, indisponibilidade, erro inesperado): a transação é desfeita e
  a chave não é consumida, permitindo que a repetição execute a operação.

### Referência externa no lançamento

- Todo lançamento exige `external_reference`, a identificação da operação no sistema de origem.
- Constraint `UNIQUE (client_id, external_reference)` na tabela de lançamentos.
- Se uma requisição com chave nova trouxer uma referência já registrada: `409`, informando o
  lançamento existente. Isso cobre o caso de o chamador perder a chave original, e protege contra
  defeito no mecanismo de chave.

### Retenção

Chaves mantidas por **7 dias** e removidas por rotina periódica no Worker. A referência externa é
permanente, pois faz parte do lançamento.

## Consequências

- **Positivas:**
  - Repetição segura de qualquer escrita, inclusive sob retries concorrentes.
  - Duas barreiras independentes contra lançamento duplicado.
  - Referência externa viabiliza conciliação com sistemas de origem.
- **Negativas:**
  - Armazenamento adicional por requisição de escrita, limitado pela retenção.
  - Chamadores precisam gerar e reutilizar a chave corretamente; documentado na OpenAPI.
- **Verificação:**
  - Testes de integração para repetição sequencial, repetição concorrente, reuso com payload
    diferente, falha transitória seguida de repetição e referência externa duplicada.
