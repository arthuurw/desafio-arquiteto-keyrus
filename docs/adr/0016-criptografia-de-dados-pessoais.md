# ADR-0016: Criptografia de dados pessoais com chave por cliente no OpenBao

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

O módulo Customers armazena dados pessoais (nome, CPF, data de nascimento, contato). O enunciado
exige proteção de dados sensíveis, e a LGPD garante ao titular o direito de eliminação dos seus
dados pessoais.

Ameaças consideradas:

- Vazamento de dump ou backup do banco.
- Acesso indevido ao banco (SQL injection, credencial vazada, acesso administrativo).
- Exposição de dados em logs.
- Impossibilidade de eliminar dados pessoais de backups imutáveis.

## Alternativas consideradas

### Onde criptografar

1. **Somente criptografia em repouso (disco ou volume).** Rejeitada: protege apenas contra roubo
   de mídia física. Dumps, backups, SQL injection e acesso administrativo veem dados em claro.
2. **Criptografia no banco com `pgcrypto`.** Rejeitada: a chave trafega nas queries e pode
   aparecer em logs do PostgreSQL (`pg_stat_statements`, log de consultas lentas).
3. **Criptografia na aplicação, campo a campo.** O banco nunca vê dado pessoal em claro nem chave.

### Onde guardar as chaves de dados (DEKs)

1. **DEK cifrada por uma chave mestra, armazenada no PostgreSQL** (envelope encryption clássico).
   Rejeitada: o backup do banco contém a PII cifrada e a DEK cifrada juntas; com a chave mestra
   ainda ativa, restaurar um backup antigo permite decifrar os dados de um cliente já eliminado.
   A eliminação só seria efetiva após expirar todo backup do banco, cuja retenção costuma ser
   longa.
2. **DEK armazenada no cofre de chaves, fora do banco.** O backup do banco contém apenas
   ciphertext; destruir a DEK no cofre torna os dados ilegíveis, inclusive nos backups do banco.
3. **Uma chave de criptografia gerenciada pelo cofre por cliente** (Transit). Rejeitada: milhões
   de chaves de Transit é uso atípico, com escala e operação ruins.

### Qual cofre de chaves

1. **OpenBao.** Fork do HashiCorp Vault mantido sob a Linux Foundation (OpenSSF), licença MPL 2.0.
   Mesma API do Vault: KV v2, Transit (criptografia e HMAC como serviço, com versionamento e
   rotação de chaves), AppRole, auto-unseal estático.
2. **HashiCorp Vault Community.** Tecnicamente equivalente. Rejeitada: licença BUSL 1.1 desde 2023,
   que não é open source e restringe usos concorrentes; mesma linha de raciocínio do ADR-0008.
3. **Serviços gerenciados (Azure Key Vault, AWS KMS, Google Cloud KMS).** Alternativas válidas
   para produção, pagas e dependentes de nuvem; não adotadas no projeto para manter o ambiente
   local gratuito e independente de provedor. A abstração `IKeyStore` permite adotá-las depois.

## Decisão

Criptografia na aplicação, campo a campo, com **uma DEK por cliente armazenada no OpenBao**.

Explicação didática dos conceitos e fluxos: [guia de criptografia](../guias/criptografia-explicada.md).

### Algoritmo e formato

- **AES-256-GCM** (`System.Security.Cryptography.AesGcm`): confidencialidade e integridade.
  Ciphertext adulterado é detectado na decifragem.
- Nonce de 96 bits aleatório por operação; tag de 128 bits.
- **Dados autenticados associados (AAD):** `customer_id` + nome do campo. Impede que um ciphertext
  seja copiado para outro cliente ou outro campo e decifrado com sucesso.
- Formato armazenado: `versão do formato (1 byte) | nonce (12) | ciphertext | tag (16)`.

### Chaves

- **DEK:** 256 bits, gerada com `RandomNumberGenerator` no cadastro do cliente, armazenada no KV v2
  do OpenBao em `customers/dek/{customerId}`. Nunca é persistida no PostgreSQL nem registrada em
  log.
- **Cache da DEK em memória** com TTL curto (5 minutos) e tamanho limitado, para não consultar o
  cofre a cada leitura. Ao sair do cache, o buffer é zerado (`CryptographicOperations.ZeroMemory`).
- **Busca por CPF via blind index:** HMAC-SHA256 do CPF normalizado (apenas dígitos), calculado
  pelo Transit do OpenBao com a chave `customers-blind-index`, que nunca sai do cofre. Coluna
  `cpf_blind_index` com constraint `UNIQUE`, usada para busca e para impedir cadastro duplicado.
  - Um hash simples (SHA-256 sem chave) seria inseguro: o CPF tem cerca de 10⁹ combinações
    válidas, e um hash sem segredo seria revertido por força bruta em minutos.

### Acesso ao cofre

- A aplicação autentica no OpenBao via **AppRole**, com política de privilégio mínimo: ler e criar
  em `customers/dek/*`, destruir apenas no fluxo de eliminação, e `hmac` na chave de blind index.
- Nenhum componente usa o token root fora do bootstrap.

### Eliminação (crypto-shredding)

- A eliminação de dados pessoais de um cliente destrói todas as versões da DEK no OpenBao
  (`metadata delete`). Os campos cifrados tornam-se ilegíveis no banco **e em todos os backups do
  banco**.
- O registro do cliente permanece como marcador (identificador e data de eliminação), mantendo
  válidas as referências do Ledger, cujo histórico financeiro tem obrigação legal de guarda.
- **Ressalva:** os backups do próprio OpenBao contêm a DEK até expirarem. Por isso, os backups do
  cofre têm retenção curta e separada (ex.: 30 dias); a eliminação é definitiva após esse prazo.

### Ambiente local

- OpenBao em modo servidor com storage persistente em volume e **auto-unseal estático** (chave de
  unseal por variável de ambiente, exclusiva de desenvolvimento).
- O modo `-dev` foi descartado: é em memória, e um reinício do container destruiria todas as
  DEKs, tornando toda a PII ilegível.
- Um container de bootstrap idempotente inicializa o cofre na primeira subida, habilita KV v2 e
  Transit, cria a chave de blind index, as políticas e o AppRole da aplicação.

### Testes

- Unitários: implementações em memória de `IKeyStore` e `IBlindIndexer`.
- Integração: OpenBao real via Testcontainers.

## Consequências

- **Positivas:**
  - Dump, backup ou acesso indevido ao banco não expõem dados pessoais.
  - Direito de eliminação atendido sem alterar o histórico financeiro nem os backups do banco.
  - Indisponibilidade do OpenBao afeta apenas operações com dados pessoais; Ledger e Positions
    não dependem do cofre e continuam operando.
- **Negativas:**
  - O OpenBao passa a ser dependência crítica do módulo Customers; em produção, precisa de alta
    disponibilidade (cluster Raft) e monitoramento próprio.
  - Campos cifrados não permitem busca por conteúdo além do blind index (ex.: busca parcial por
    nome não é possível).
  - Um segredo por cliente no cofre exige acompanhar o crescimento do storage do OpenBao.
- **Evolução:**
  - Rotação da chave de blind index exige recalcular os índices a partir dos dados decifrados;
    procedimento documentado, com coluna de versão do índice.
  - Em produção: TLS obrigatório até o cofre e autenticação por identidade da plataforma (ex.:
    Kubernetes auth) no lugar de AppRole com secret estático.
