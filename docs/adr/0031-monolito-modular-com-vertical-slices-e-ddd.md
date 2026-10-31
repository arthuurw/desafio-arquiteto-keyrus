# ADR-0031: Monólito modular com vertical slices e DDD, com o domínio dono dos seus tipos

- **Status:** Aceita
- **Data:** 2026-10-31
- **Substitui:** ADR-0010

## Contexto

É preciso definir como o código é organizado: quantas unidades, onde ficam as fronteiras e como
cada caso de uso é estruturado internamente. Forças em jogo:

- O ledger exige transação forte entre conta e lançamento (ADR-0003, ADR-0005).
- Leitura e escrita têm armazenamento e perfil de carga distintos (ADR-0006).
- Dados pessoais de clientes são sensíveis e sujeitos à LGPD.
- Prazo curto e time pequeno, mas a solução precisa evoluir sem reescrita.

O ADR-0010 tomou essa decisão, mas o código construído divergiu dele em dois pontos:

- **`Money` e o tipo de resultado ficam no domínio do Ledger**, não em `BuildingBlocks`. O tipo de
  resultado se chama `Outcome`, não `Result`. `BuildingBlocks` tem só a identidade do chamador.
- **Cada módulo tem um tipo público no projeto interno:** o tipo de entrada `<Módulo>Module`
  (`LedgerModule`, `PositionsModule`, `CustomersModule`), usado pelos hosts para compor o módulo.
  O ADR-0010 dizia que só os projetos `*.Contracts` expõem tipos públicos.

Este ADR substitui o ADR-0010 por inteiro: repete o que continua valendo e registra os dois pontos.

## Mapeamento de domínio (DDD estratégico)

| Bounded context | Tipo de subdomínio | Dono de | Linguagem principal |
|-----------------|--------------------|---------|---------------------|
| **Ledger** | Core | PostgreSQL: contas, lançamentos, chaves de idempotência, outbox | Account, Entry, Credit, Debit, Reversal, CreditLimit, Balance |
| **Positions** | Core (leitura) | MongoDB: posição por conta, extrato | Position, Statement, BalanceAt |
| **Customers** | Supporting | Dados pessoais (PII), criptografados | Customer, Cpf |
| Identidade | Generic | Provedor de identidade externo | — |

O glossário completo, com a correspondência português/inglês, está em
[`docs/glossario.md`](../glossario.md).

### Decisões de fronteira

- **`CreditLimit` pertence ao Ledger, não a Customers.** A validação do limite precisa ocorrer na
  mesma transação do débito (ADR-0001, ADR-0005). Uma invariante transacional define a fronteira
  do agregado: se o limite pertencesse a outro módulo, haveria leitura cruzada de persistência ou
  validação contra valor defasado.
- **Positions é um bounded context próprio**, e não apenas o lado de consulta do Ledger: possui
  armazenamento, modelo (desnormalizado) e perfil de carga próprios. Cada módulo é dono exclusivo
  do seu dado: o Ledger nunca acessa o MongoDB, Positions nunca acessa tabelas do Ledger.
- **O fallback de leitura (ADR-0007) passa por contrato público do Ledger** (`ILedgerQueries` em
  `Ledger.Contracts`), nunca por acesso direto às tabelas. Sem isso, o fallback violaria a
  fronteira de dados.
- **Customers isola os dados pessoais.** O Ledger referencia apenas `CustomerId`, um identificador
  opaco, sem nenhum dado pessoal. Isso permite atender pedidos de eliminação de dados da LGPD
  (anonimização da PII) sem alterar o histórico financeiro, que tem obrigação legal de retenção.

### Comunicação entre módulos

| De | Para | Mecanismo | Consistência |
|----|------|-----------|--------------|
| Ledger | Positions | Evento de integração `EntryRecorded` via outbox, com payload enriquecido (valor, `balance_after`, `version`, `recorded_at`) | Eventual, com garantia de frescor na leitura (ADR-0007) |
| Positions | Ledger | Contrato `ILedgerQueries` (versão atual da conta, fallback de consulta) | Imediata |
| Ledger | Customers | Contrato `ICustomerQueries` (existência e situação do cliente na abertura de conta) | Imediata |

O payload enriquecido evita que o consumidor precise consultar o Ledger para processar o evento.
Hoje a entrega é em processo no Worker (ADR-0009); com um broker, muda apenas o transporte.

## Alternativas consideradas

### Organização

1. **Microsserviços (um serviço por bounded context).**
   Rejeitada: impõe desde o primeiro dia custo de rede, versionamento de contratos, observabilidade
   distribuída e operação de múltiplos deploys, sem benefício proporcional para um time pequeno.
   As fronteiras do monólito modular já permitem a extração futura.
2. **Monólito em camadas horizontais (Clean Architecture clássica: Domain, Application,
   Infrastructure, Api como projetos globais).**
   Rejeitada: uma mudança em um caso de uso se espalha por quatro projetos; as camadas são
   globais e não expressam as fronteiras de negócio; tende a gerar serviços de aplicação
   genéricos e acoplamento entre áreas não relacionadas.
3. **Monólito modular, com vertical slices dentro de cada módulo e domínio rico (DDD tático).**

### Onde ficam `Money` e o tipo de resultado

1. **Em `BuildingBlocks`, como kernel compartilhado** (o previsto no ADR-0010).
   Rejeitada: hoje só o Ledger usa os dois. Um kernel compartilhado com regra de dinheiro acopla
   os outros módulos às regras do Ledger, e `BuildingBlocks` passaria a crescer com conceitos de
   um módulo só.
2. **No domínio do módulo que os usa** (`Ledger.Domain`), `internal` como o resto do domínio.

### Como os hosts compõem um módulo

1. **Só tipos públicos em `*.Contracts`,** com a composição também num projeto de contrato.
   Rejeitada: o contrato passaria a depender de EF Core, ASP.NET Core e do `Mediator` para
   registrar o módulo, e qualquer módulo que lesse o contrato herdaria essas dependências.
2. **Um único tipo público no projeto interno, o tipo de entrada `<Módulo>Module`,** com os
   métodos de composição (registro de serviços, rotas, migrações). Todo o resto é `internal`.

## Decisão

**Organização: alternativa 3. `Money` e `Outcome`: alternativa 2. Composição: alternativa 2.**

### Estrutura

```
src/
  Hosts/
    Api/                      composition root HTTP
    Worker/                   composition root: rotinas em segundo plano (limpeza de chaves; relay do outbox e projetores)
    Migrator/                 aplica as migrações com a role de migração (ADR-0019, ADR-0033)
  Modules/
    Ledger/
      Ledger.Contracts/       público: eventos de integração, ILedgerQueries
      Ledger/                 interno, exceto LedgerModule
        LedgerModule.cs       tipo de entrada público: composição usada pelos hosts
        Domain/               agregado Account, entidade Entry, Money, Outcome, LedgerError, EntryType
        Features/
          OpenAccount/        endpoint, comando, handler
          ChangeCreditLimit/
          RecordEntry/
          ReverseEntry/
        Infrastructure/       EF Core, idempotência, outbox
    Positions/
      Positions.Contracts/
      Positions/              interno, exceto PositionsModule
        Features/
          GetBalance/
          GetStatement/
        Projections/
        Infrastructure/       MongoDB
    Customers/
      Customers.Contracts/
      Customers/              interno, exceto CustomersModule
  BuildingBlocks/             kernel compartilhado mínimo: identidade do chamador (Caller, Scopes)
tests/
  Ledger.UnitTests/
  IntegrationTests/           Testcontainers (PostgreSQL, MongoDB, OpenBao)
  ArchitectureTests/
```

### Regras

- **Vertical slice:** cada pasta em `Features/` contém um caso de uso completo (endpoint, comando
  ou consulta, handler). A validação de entrada fica no endpoint (ADR-0030). Alinha-se 1:1 com
  CQRS: cada slice é um comando ou uma consulta. Uma mudança de funcionalidade toca uma pasta; o
  que vários slices do módulo compartilham (leitura da entrada, catálogo de erros) fica em
  `Features/`, fora das pastas dos slices.
- **DDD tático:** o domínio rico (agregados, entidades, value objects, invariantes) fica em
  `Domain/`, compartilhado pelos slices do módulo. O slice orquestra; o domínio decide.
- **`Money` e `Outcome` no domínio do Ledger.** `Money` é o value object de dinheiro (ADR-0018).
  `Outcome<T>` é o resultado de uma operação de domínio: o valor produzido ou a recusa de negócio
  (`LedgerError`), nunca os dois. Os dois são `internal` em `Ledger.Domain`.
- **Fronteiras forçadas, não apenas convencionadas:**
  - Tipos são `internal` por padrão. São públicos os tipos dos projetos `*.Contracts` e, no projeto
    interno de cada módulo, só o tipo de entrada `<Módulo>Module`.
  - Testes de arquitetura falham se:
    - um módulo referenciar o projeto interno de outro módulo;
    - o domínio depender de infraestrutura (EF Core, Npgsql, MongoDB, ASP.NET Core ou a
      `Infrastructure` do próprio módulo);
    - o projeto interno de um módulo expuser outro tipo público além do `<Módulo>Module`.
  - A regra de domínio falha também quando não encontra nenhum tipo de domínio para avaliar.
- **`BuildingBlocks` é mínimo:** apenas conceitos estáveis e realmente transversais. Hoje, só a
  identidade do chamador, usada por todos os módulos. Não pode se tornar um depósito de
  utilitários, que viraria o verdadeiro ponto de acoplamento. Um conceito de um módulo só vai
  para `BuildingBlocks` quando outro módulo passar a precisar do mesmo comportamento, com ADR.
- **Hosts sem regra de negócio:** apenas configuração e composição dos módulos, pelo
  `<Módulo>Module`.

## Consequências

- **Positivas:**
  - Fronteiras de negócio visíveis na estrutura do código e verificadas pelos testes de
    arquitetura, que reprovam o CI.
  - Extração futura de um módulo como serviço exige trocar o transporte, não reescrever.
  - Casos de uso localizados: fácil de navegar, revisar e testar.
  - Regras de dinheiro e de recusa ficam com o módulo que as decide; `BuildingBlocks` não cresce
    com conceitos de um módulo só.
  - Os projetos `*.Contracts` não dependem de EF Core, ASP.NET Core nem do `Mediator`.
- **Negativas:**
  - Mais projetos na solução do que um monólito simples.
  - Disciplina necessária para que `BuildingBlocks` permaneça mínimo.
  - Vertical slices permitem duplicação pontual entre slices; aceita conscientemente em troca de
    baixo acoplamento, extraindo para o domínio apenas o que é regra de negócio.
  - Se Positions ou Customers precisarem de `Money`, recebem o valor pelos contratos ou o tipo
    migra para `BuildingBlocks` com um novo ADR.
