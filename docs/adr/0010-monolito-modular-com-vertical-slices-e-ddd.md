# ADR-0010: Monólito modular com vertical slices e DDD

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

É preciso definir como o código é organizado: quantas unidades, onde ficam as fronteiras e como
cada caso de uso é estruturado internamente. Forças em jogo:

- O ledger exige transação forte entre conta e lançamento (ADR-0003, ADR-0005).
- Leitura e escrita têm armazenamento e perfil de carga distintos (ADR-0006).
- Dados pessoais de clientes são sensíveis e sujeitos à LGPD.
- Prazo curto e time pequeno, mas a solução precisa evoluir sem reescrita.

## Mapeamento de domínio (DDD estratégico)

| Bounded context | Tipo de subdomínio | Dono de | Linguagem principal |
|-----------------|--------------------|---------|---------------------|
| **Ledger** | Core | PostgreSQL: contas, lançamentos, outbox | Account, Entry, Credit, Debit, Reversal, CreditLimit, Balance |
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

## Alternativas consideradas (organização)

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

## Decisão

**Alternativa 3.**

### Estrutura

```
src/
  Hosts/
    Api/                      composition root HTTP
    Worker/                   composition root: relay do outbox e projetores
  Modules/
    Ledger/
      Ledger.Contracts/       público: eventos de integração, ILedgerQueries
      Ledger/                 interno
        Domain/               agregado Account, entidade Entry, value objects
        Features/
          RecordEntry/        endpoint, comando, validador, handler
          ReverseEntry/
        Infrastructure/       EF Core, outbox
    Positions/
      Positions.Contracts/
      Positions/
        Features/
          GetBalance/
          GetStatement/
        Projections/
        Infrastructure/       MongoDB
    Customers/
      Customers.Contracts/
      Customers/
  BuildingBlocks/             kernel compartilhado mínimo: Money, Result, abstrações de outbox
tests/
  Ledger.UnitTests/
  Positions.UnitTests/
  Customers.UnitTests/
  IntegrationTests/           Testcontainers (PostgreSQL, MongoDB)
  ArchitectureTests/
```

### Regras

- **Vertical slice:** cada pasta em `Features/` contém um caso de uso completo (endpoint, comando
  ou consulta, validação, handler). Alinha-se 1:1 com CQRS: cada slice é um comando ou uma
  consulta. Uma mudança de funcionalidade toca uma pasta.
- **DDD tático:** o domínio rico (agregados, entidades, value objects, invariantes) fica em
  `Domain/`, compartilhado pelos slices do módulo. O slice orquestra; o domínio decide.
- **Fronteiras forçadas, não apenas convencionadas:**
  - Tipos são `internal` por padrão; apenas os projetos `*.Contracts` expõem tipos públicos.
  - Testes de arquitetura falham se um módulo referenciar o projeto interno de outro módulo, ou se
    o domínio depender de infraestrutura.
- **`BuildingBlocks` é mínimo:** apenas conceitos estáveis e realmente transversais. Não pode se
  tornar um depósito de utilitários, que viraria o verdadeiro ponto de acoplamento.
- **Hosts sem regra de negócio:** apenas configuração e composição dos módulos.

## Consequências

- **Positivas:**
  - Fronteiras de negócio visíveis na estrutura do código e verificadas automaticamente.
  - Extração futura de um módulo como serviço exige trocar o transporte, não reescrever.
  - Casos de uso localizados: fácil de navegar, revisar e testar.
- **Negativas:**
  - Mais projetos na solução do que um monólito simples.
  - Disciplina necessária para que `BuildingBlocks` permaneça mínimo.
  - Vertical slices permitem duplicação pontual entre slices; aceita conscientemente em troca de
    baixo acoplamento, extraindo para o domínio apenas o que é regra de negócio.
