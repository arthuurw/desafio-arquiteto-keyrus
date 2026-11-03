# ADR-0058: Criptografia de dados pessoais com chave por cliente no OpenBao, testes sem cofre e seed só em Development

- **Status:** Aceita
- **Data:** 2026-11-03
- **Substitui:** ADR-0050

## Contexto

O módulo Customers armazena dados pessoais (nome, CPF, data de nascimento, e-mail). O enunciado
exige proteção de dados sensíveis, e a LGPD garante ao titular o direito de eliminação dos seus
dados pessoais.

Ameaças consideradas:

- Vazamento de dump ou backup do banco.
- Acesso indevido ao banco (SQL injection, credencial vazada, acesso administrativo).
- Exposição de dados em logs.
- Impossibilidade de eliminar dados pessoais de backups imutáveis.

O ADR substituído decidiu a criptografia, os testes sem cofre e os clientes de desenvolvimento, e
segue valendo nisso. Ele disse, sobre os clientes de desenvolvimento, que só o compose liga a
configuração. Duas coisas não cabem nessa frase:

- **Api fora do compose.** Quem roda a Api com `dotnet run`, para depurar, também precisa dos
  clientes de `ana` e `bruno` para o exemplo do modo delegado.
- **Nada impedia ligar fora do ambiente local.** Basta `Customers:Seed:Enabled=true` para criar,
  em qualquer ambiente, clientes de ids e CPFs fixos, conhecidos de quem lê o repositório.

Este ADR repete o que segue vigente e troca a regra de quem liga os clientes de desenvolvimento.

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
2. **DEK armazenada no cofre de chaves, fora do banco.** O backup do banco contém apenas
   ciphertext; destruir a DEK no cofre torna os dados ilegíveis, inclusive nos backups do banco.
3. **Uma chave de criptografia gerenciada pelo cofre por cliente** (Transit). Rejeitada: milhões
   de chaves de Transit é uso atípico, com escala e operação ruins.

### Qual cofre de chaves

1. **OpenBao.** Fork do HashiCorp Vault mantido sob a Linux Foundation, licença MPL 2.0. Mesma API
   do Vault: KV v2, Transit, AppRole, seal estático.
2. **HashiCorp Vault Community.** Tecnicamente equivalente. Rejeitada: licença BUSL 1.1 desde 2023,
   que não é open source e restringe usos concorrentes.
3. **Serviços gerenciados (Azure Key Vault, AWS KMS, Google Cloud KMS).** Válidos para produção,
   pagos e dependentes de nuvem; não adotados para manter o ambiente local gratuito e independente
   de provedor. As abstrações `IKeyStore` e `IBlindIndexer` permitem adotá-los depois.

### Como testar sem depender do cofre em todo teste

1. **Dublês em memória de `IKeyStore` e `IBlindIndexer` nos unitários.** Rejeitada: o dublê pode
   divergir do cofre real, e o que importa no cofre (política, `cas=0`, formato do HMAC) só se
   prova contra ele.
2. **Unitários da cifra, do cache e do cliente HTTP, com o cofre substituído só na borda HTTP; o
   cofre real só na integração.**

### Clientes para o ambiente local

1. **Cadastro manual depois da subida.** Rejeitada: os usuários `ana` e `bruno` do realm já têm
   `customer_id` fixo, e sem o cliente cadastrado nenhuma conta abre para eles.
2. **Script SQL de seed.** Rejeitada: a linha precisa dos campos cifrados com a DEK do cofre e do
   blind index do Transit, que o SQL não calcula.
3. **Rotina da Api que cadastra pelo mesmo caminho do `POST`, ligada por configuração.**

### Onde a configuração do seed vale

1. **Só no compose, por convenção.** Rejeitada: a decisão substituída. Não serve à Api rodada fora
   do compose, e nada impede ligar a configuração em outro ambiente.
2. **Em qualquer ambiente, com um aviso no log.** Rejeitada: o aviso não impede criar clientes de
   ids e CPFs fixos fora do ambiente local.
3. **Só em Development: fora dele, a configuração ligada recusa a subida**, como o endereço do
   cofre por http.

## Decisão

Criptografia na aplicação, campo a campo, com **uma DEK por cliente armazenada no OpenBao**.

Explicação didática dos conceitos e fluxos: [guia de criptografia](../guias/criptografia-explicada.md).

### Algoritmo e formato

- **AES-256-GCM** (`System.Security.Cryptography.AesGcm`): confidencialidade e integridade.
  Campo que não decifra (adulterado, ou copiado de outro cliente ou de outro campo) é recusado,
  nunca devolvido em parte.
- Nonce de 96 bits aleatório por operação; tag de 128 bits.
- **Dados autenticados associados (AAD):** `customer_id` + nome do campo.
- Formato armazenado: `versão do formato (1 byte) | nonce (12) | ciphertext | tag (16)`.

### Chaves

- **DEK:** 256 bits, gerada com `RandomNumberGenerator` no cadastro, gravada no KV v2 do OpenBao
  em `customers/data/dek/{customerId}` com `cas=0`: só grava se ainda não existe. A DEK vai ao
  cofre antes da linha ao banco; uma falha entre os dois deixa uma DEK órfã, nunca uma linha sem
  chave. Nunca é persistida no PostgreSQL nem registrada em log.
- **Cache da DEK em memória** com TTL de 5 minutos por padrão, contado da leitura no cofre, e
  tamanho limitado. Cada leitura recebe uma cópia; ao sair do cache, o buffer é zerado
  (`CryptographicOperations.ZeroMemory`).
- **Busca por CPF via blind index:** HMAC-SHA256 dos 11 dígitos do CPF, calculado pelo Transit
  com a chave `customers-blind-index`, não exportável. Coluna `cpf_blind_index` com constraint
  `UNIQUE`, que impede cadastro duplicado, e `cpf_blind_index_version` com a versão da chave que
  calculou o índice. A versão da chave usada no HMAC fica fixa na configuração: um `rotate` no
  cofre não muda o índice do mesmo CPF.
  - Um hash sem chave seria inseguro: o CPF tem cerca de 10⁹ combinações válidas, e o hash seria
    revertido por força bruta em minutos.
- O hash das requisições idempotentes de Customers usa outra chave do Transit,
  `customers-request-hash` (ADR-0059).

### Acesso ao cofre

- A Api autentica no OpenBao via **AppRole**, com política de privilégio mínimo: criar e ler em
  `customers/data/dek/*` e calcular os dois HMACs. Sem `update`, a política recusa sobrescrever uma
  DEK; sem `delete`, a Api não destrói nenhuma.
- Nenhum componente usa o token root fora do bootstrap.
- Fora de Development, o endereço do cofre precisa ser `https`.
- Cofre sem resposta no prazo (1 s por padrão) ou com erro de servidor conta como indisponível: o
  cadastro e a leitura de cliente respondem `503`. Ledger e Positions não dependem do cofre.

### Eliminação (crypto-shredding)

- A eliminação de dados pessoais de um cliente destrói todas as versões da DEK no OpenBao. Os
  campos cifrados tornam-se ilegíveis no banco **e em todos os backups do banco**.
- O registro do cliente permanece como marcador, mantendo válidas as referências do Ledger, cujo
  histórico financeiro tem obrigação legal de guarda.
- Quem destrói é a rotina de eliminação ao fim da guarda legal (ADR-0017), fora da política do
  AppRole da Api. A rotina está decidida e não implementada nesta entrega (ADR-0027 (substituído pelo ADR-0054)).
- **Ressalva:** os backups do próprio OpenBao contêm a DEK até expirarem. Por isso, os backups do
  cofre têm retenção curta e separada (ex.: 30 dias); a eliminação é definitiva após esse prazo.

### Ambiente local

- OpenBao em modo servidor, com storage Raft em volume e **seal estático** (chave de 32 bytes por
  variável de ambiente, exclusiva do ambiente local): o cofre se deslacra sozinho a cada subida.
- O modo `-dev` foi descartado: é em memória, e um reinício do container destruiria todas as
  DEKs, tornando toda a PII ilegível.
- Um container de bootstrap idempotente inicializa o cofre na primeira subida, habilita KV v2 e
  Transit, cria as chaves de HMAC, a política e o AppRole da Api; nas subidas seguintes, confere o
  que já existe e não troca nenhuma chave. A Api só sobe depois dele.

### Clientes de desenvolvimento

- A Api cadastra os clientes dos usuários `ana` e `bruno` do realm local, com os mesmos
  `customer_id` do realm e dados fictícios, quando `Customers:Seed:Enabled` é verdadeiro.
- A configuração só vale em Development: o compose a liga (`Customers__Seed__Enabled`), e quem roda
  a Api fora dele a liga com `ASPNETCORE_ENVIRONMENT=Development`. Fora de Development, a
  configuração ligada recusa a subida, e a mensagem cita `Customers:Seed:Enabled`.
- O cadastro passa pelo mesmo caminho do `POST`: blind index, DEK no cofre, campos cifrados. Cliente
  já presente no banco é pulado, e subir de novo não duplica.
- Se o cliente não está no banco mas a DEK dele está no cofre (banco recriado, cofre mantido), a
  DEK existente é reaproveitada; o `cas=0` recusaria gravar outra.
- Com o cofre indisponível, a rotina espera 2 s e tenta de novo, até 30 tentativas, sem derrubar a
  Api. Outro erro, ou o cofre ainda fora na última tentativa, vai ao log e encerra a rotina.

### Testes

- **Unitários, sem cofre:** a cifra (nonce, AAD, formato), o cache de DEK (TTL, cópia por leitura,
  leitura concorrente com a substituição) e o cliente HTTP do OpenBao, com um `HttpMessageHandler`
  escrito à mão no lugar do cofre. Não há dublê de `IKeyStore` nem de `IBlindIndexer`.
- **Integração, com o cofre real:** OpenBao via Testcontainers, com o mesmo `config.hcl`, a mesma
  política e o mesmo script de bootstrap do compose. As falhas do cofre (sem servidor, respondendo
  `503`, sem resposta) usam dublês na borda de rede (ADR-0023).

## Consequências

- **Positivas:**
  - Dump, backup ou acesso indevido ao banco não expõem dados pessoais.
  - Direito de eliminação atendível sem alterar o histórico financeiro nem os backups do banco.
  - Indisponibilidade do OpenBao afeta apenas operações com dados pessoais; Ledger e Positions
    continuam operando.
  - O código que fala com o cofre é provado contra o OpenBao real, com a configuração do compose.
  - O ambiente local sobe com os clientes de `ana` e `bruno` cadastrados, sem passo manual.
  - Nenhum ambiente fora de Development sobe com os clientes de ids e CPFs fixos.
- **Negativas:**
  - O OpenBao é dependência crítica do módulo Customers; em produção, precisa de alta
    disponibilidade e monitoramento próprio.
  - Campos cifrados não permitem busca por conteúdo além do blind index (ex.: busca parcial por
    nome).
  - Um segredo por cliente no cofre exige acompanhar o crescimento do storage do OpenBao.
  - O cadastro e a leitura de cliente, que passam pelo cofre, só são exercitados nos testes de
    integração, mais lentos que os unitários.
  - Recriar o volume do cofre com o do banco mantido deixa os clientes cadastrados ilegíveis,
    inclusive os do seed.
- **Evolução:**
  - Rotação da chave de blind index exige recalcular os índices a partir dos dados decifrados, com
    os cadastros parados; o procedimento está no guia de criptografia.
  - Em produção: autenticação por identidade da plataforma (ex.: Kubernetes auth) no lugar de
    AppRole com secret estático.
