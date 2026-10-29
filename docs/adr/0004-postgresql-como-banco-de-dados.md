# ADR-0004: PostgreSQL como banco de dados

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

As decisões anteriores impõem requisitos à persistência:

- Transação ACID abrangendo inserção do lançamento e atualização da conta (ADR-0003).
- Controle de concorrência por conta para impedir que débitos simultâneos ultrapassem o limite
  (ADR-0001).
- Restrições de unicidade para garantir idempotência das requisições de escrita.
- Timestamp confiável atribuído pelo servidor (ADR-0002).
- Consulta eficiente por índice composto `(account_id, recorded_at)` (ADR-0003).
- Execução local simples via Docker e nos testes automatizados via Testcontainers, para que
  qualquer pessoa rode o projeto sem instalação manual.

## Alternativas consideradas

### 1. PostgreSQL

Atende todos os requisitos nativamente: transações ACID, `SELECT ... FOR UPDATE`, isolamento
`SERIALIZABLE`, `timestamptz`, índices compostos, constraints `CHECK` e `UNIQUE`, particionamento
declarativo de tabelas e extensão `pgcrypto`. Driver Npgsql e provider EF Core maduros.
Imagem Docker leve, inicialização rápida, suporte multiplataforma (x64 e ARM). Open source,
sem custo de licença.

### 2. SQL Server

Tecnicamente atende os requisitos (`rowversion`, hints `UPDLOCK`/`ROWLOCK`, isolamento
`SNAPSHOT`, TDE, Always Encrypted) e é comum em instituições financeiras e ambientes .NET.

Motivos da rejeição:

- **Experiência de execução local:** a imagem Docker tem cerca de 1,5 GB, inicialização lenta
  e suporte limitado em ARM (ex.: Macs com Apple Silicon), o que prejudica a premissa de que
  qualquer pessoa rode o projeto com um comando.
- **Testes automatizados:** o tempo de inicialização do container torna a suíte de testes de
  integração sensivelmente mais lenta.
- **Custo:** licenciamento pago em produção, sem ganho técnico que o justifique para este
  problema.

Nada no desenho depende de recurso exclusivo do PostgreSQL. Se o padrão corporativo do banco
exigir SQL Server, a modelagem é portável: os mesmos mecanismos (lock de linha, constraints,
transação) existem lá.

### 3. Banco NoSQL (MongoDB, Cosmos DB)

Motivos da rejeição:

- **Transações multi-documento** existem, mas com limitações de desempenho e operação; o modelo
  é otimizado para operações atômicas em um único documento.
- **Invariantes relacionais:** o ledger depende de invariantes entre conta e lançamentos
  (saldo da conta igual ao `balance_after` do último lançamento; limite respeitado). Garanti-las
  sem constraints relacionais e com transações limitadas desloca a responsabilidade para a
  aplicação e aumenta o risco de inconsistência.
- **Alternativa de documento único** (embutir lançamentos na conta) esbarra no limite de
  tamanho de documento e degrada com o crescimento do histórico.
- O domínio é naturalmente relacional e transacional; flexibilidade de esquema não é uma
  necessidade deste problema.

## Decisão

**PostgreSQL** como banco de dados do ledger.

## Consequências

- **Positivas:**
  - Garantias de consistência no próprio banco (transações, constraints `CHECK`/`UNIQUE`), que
    funcionam como defesa em profundidade além das validações da aplicação.
  - Ambiente local e testes de integração rodando contra o mesmo motor de produção, sem mocks
    de banco.
  - Caminho de crescimento claro: particionamento da tabela de lançamentos por tempo, réplicas
    de leitura para consultas.
- **Negativas:**
  - Escrita concentrada em um nó primário; escala horizontal de escrita exige sharding
    (ex.: por `account_id`), documentado como evolução.
- **Em aberto:**
  - Mecanismo de controle de concorrência a ser definido em ADR próprio.
