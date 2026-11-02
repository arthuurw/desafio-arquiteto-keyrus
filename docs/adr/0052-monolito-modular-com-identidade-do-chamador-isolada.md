# ADR-0052: Monólito modular com vertical slices e DDD, e a identidade do chamador em projeto próprio

- **Status:** Aceita
- **Data:** 2026-11-02
- **Substitui:** ADR-0031

## Contexto

É preciso definir como o código é organizado: quantas unidades, onde ficam as fronteiras e como
cada caso de uso é estruturado internamente. Forças em jogo:

- O ledger exige transação forte entre conta e lançamento (ADR-0005).
- Leitura e escrita têm armazenamento e perfil de carga distintos (ADR-0006).
- Dados pessoais de clientes são sensíveis e sujeitos à LGPD (ADR-0017).
- Prazo curto e time pequeno, mas a solução precisa evoluir sem reescrita.

O ADR-0031 (substituído pelo ADR-0052) tomou essa decisão, mas dois pontos dele não descrevem o
código:

- **`BuildingBlocks` não tem só a identidade do chamador.** Tem auditoria, HTTP, idempotência,
  logging, configuração, persistência e identidade, e depende de ASP.NET Core, EF Core, JwtBearer,
  OpenAPI e telemetria.
- **Os projetos `*.Contracts` dependiam dessa infraestrutura.** Eles referenciavam
  `BuildingBlocks` por causa do `Caller`, que o `Ledger.Contracts` usa em `ILedgerQueries`. Quem lia
  um contrato herdava ASP.NET Core e EF Core.

Este ADR substitui o anterior por inteiro: repete o que continua valendo, registra o conteúdo real
de `BuildingBlocks` e tira a identidade do chamador para um projeto sem dependências.

## Mapeamento de domínio (DDD estratégico)

| Bounded context | Tipo de subdomínio | Dono de |
|-----------------|--------------------|---------|
| **Ledger** | Core | Contas, lançamentos e limite de crédito, no PostgreSQL |
| **Positions** | Core (leitura) | Posição por conta e extrato, no MongoDB |
| **Customers** | Supporting | Dados pessoais (PII), criptografados, no PostgreSQL |
| Identidade | Generic | Provedor de identidade externo |

Os termos de cada contexto estão em [`docs/glossario.md`](../glossario.md) (ADR-0046).

### Decisões de fronteira

- **`CreditLimit` pertence ao Ledger, não a Customers.** A validação do limite ocorre na mesma
  transação do débito (ADR-0005). Se o limite pertencesse a outro módulo, haveria leitura cruzada
  de persistência ou validação contra valor defasado.
- **Positions é um bounded context próprio**, e não apenas o lado de consulta do Ledger: tem
  armazenamento, modelo desnormalizado e perfil de carga próprios. Cada módulo é dono exclusivo do
  seu dado: o Ledger não referencia o MongoDB nem Positions, e Positions não referencia o projeto
  interno do Ledger nem o EF Core.
- **O fallback de leitura (ADR-0045) passa pelo contrato público do Ledger** (`ILedgerQueries` em
  `Ledger.Contracts`), nunca por acesso direto às tabelas.
- **Customers isola os dados pessoais.** O Ledger referencia apenas `CustomerId`, um identificador
  opaco. A eliminação de dados da LGPD não altera o histórico financeiro, que tem retenção legal.

### Comunicação entre módulos

| De | Para | Mecanismo | Consistência |
|----|------|-----------|--------------|
| Ledger | Positions | Evento de integração `EntryRecorded` via outbox, com o saldo após o lançamento e a `sequence` | Eventual, com garantia de frescor na leitura (ADR-0045) |
| Positions | Ledger | Contrato `ILedgerQueries` (versão atual da conta, fallback de consulta) | Imediata |
| Ledger | Customers | Contrato `ICustomerQueries` (existência do cliente na abertura de conta) | Imediata |

O payload do evento evita que o consumidor consulte o Ledger para processá-lo. Hoje a entrega é em
processo no Worker (ADR-0009); com um broker, muda apenas o transporte.

## Alternativas consideradas

### Organização

1. **Microsserviços (um serviço por bounded context).**
   Rejeitada: impõe desde o primeiro dia custo de rede, versionamento de contratos, observabilidade
   distribuída e vários deploys, sem benefício proporcional para um time pequeno.
2. **Monólito em camadas horizontais** (Domain, Application, Infrastructure, Api como projetos
   globais). Rejeitada: uma mudança num caso de uso se espalha por quatro projetos, e as camadas não
   expressam as fronteiras de negócio.
3. **Monólito modular, com vertical slices dentro de cada módulo e domínio rico (DDD tático).**

### Onde ficam `Money` e o tipo de resultado

1. **Em `BuildingBlocks`, como kernel compartilhado.** Rejeitada: só o Ledger usa os dois, e um
   kernel com regra de dinheiro acopla os outros módulos às regras do Ledger.
2. **No domínio do módulo que os usa** (`Ledger.Domain`), `internal` como o resto do domínio.

### Como os hosts compõem um módulo

1. **Só tipos públicos em `*.Contracts`,** com a composição também num projeto de contrato.
   Rejeitada: o contrato passaria a depender de EF Core, ASP.NET Core e do `Mediator`.
2. **Um único tipo público no projeto interno, o tipo de entrada `<Módulo>Module`,** com os
   métodos de composição (registro de serviços, rotas, rotinas do Worker, migrações).

### Onde fica a identidade do chamador

1. **Contratos continuam referenciando `BuildingBlocks`.** Rejeitada: todo contrato herda ASP.NET
   Core, EF Core, JwtBearer, OpenAPI e telemetria, e um módulo que lê o contrato de outro recebe
   tudo isso.
2. **`Caller` registrado na injeção de dependência, montado por `IHttpContextAccessor`.** Os
   endpoints ficariam iguais, mas a origem do valor some da assinatura: quem lê o endpoint não vê
   que o chamador vem do token.
3. **Um invólucro `RequestCaller` com `BindAsync`, no projeto com ASP.NET Core, e `Caller` puro no
   projeto da identidade.** Rejeitada: dois tipos para um conceito só.
4. **Projeto `BuildingBlocks.Identity` só com a BCL, e o endpoint recebe `ClaimsPrincipal`.** O
   `Caller` deixa de ter `BindAsync`, que exigia `HttpContext`; o endpoint resolve o chamador a
   partir do usuário da requisição.

## Decisão

**Organização: alternativa 3. `Money` e `Outcome`: alternativa 2. Composição: alternativa 2.
Identidade do chamador: alternativa 4.**

### Estrutura

```
src/
  Hosts/
    Api/                      composition root HTTP
    Worker/                   composition root das rotinas em segundo plano
    Migrator/                 aplica as migrações dos módulos (ADR-0044)
  Modules/
    Ledger/
      Ledger.Contracts/       público: eventos de integração, ILedgerQueries
      Ledger/                 interno, exceto LedgerModule
        LedgerModule.cs       tipo de entrada público: composição usada pelos hosts
        Domain/               Account, Entry, Money, Outcome, LedgerError, EntryType
        Features/             uma pasta por caso de uso
        Infrastructure/       EF Core, idempotência, outbox
    Positions/
      Positions.Contracts/
      Positions/              interno, exceto PositionsModule
        Features/
        Projections/
        Infrastructure/       MongoDB
    Customers/
      Customers.Contracts/    público: ICustomerQueries
      Customers/              interno, exceto CustomersModule
        Domain/
        Features/
        Infrastructure/
  BuildingBlocks/             infraestrutura transversal dos módulos e hosts
  BuildingBlocks.Identity/    identidade do chamador, só com a BCL
tests/
  ArchitectureTests/
  IntegrationTests/           Testcontainers
  Ledger.UnitTests/
  Customers.UnitTests/
```

### `BuildingBlocks` e `BuildingBlocks.Identity`

- **`BuildingBlocks.Identity`** não tem `ProjectReference`, `PackageReference` nem
  `FrameworkReference`. Contém `Caller` (com `FromPrincipal`, que resolve o chamador a partir das
  claims), `CallerMode`, `Scopes` e as constantes `internal` de nomes de claim. O namespace é
  `BuildingBlocks.Identity`.
- **Os três projetos `*.Contracts` referenciam só `BuildingBlocks.Identity`.** Nenhum referencia
  `BuildingBlocks`, ASP.NET Core, EF Core ou `Microsoft.Extensions.*`.
- **`BuildingBlocks`** referencia `BuildingBlocks.Identity` e guarda a infraestrutura usada por mais
  de um módulo ou host, em uma pasta por assunto: `Audit`, `Configuration`, `Http`, `Idempotency`,
  `Identity` (autenticação, `RequireScope` e as políticas de autorização), `Logging` e
  `Persistence`. Depende de ASP.NET Core, EF Core, JwtBearer, OpenAPI e telemetria.
- **O endpoint recebe `ClaimsPrincipal user` e chama `user.ResolvedCaller()`**, extensão em
  `BuildingBlocks`. A política de `RequireScope` já recusa a requisição cujo chamador não resolve
  (ADR-0013); por isso `ResolvedCaller()` lança `InvalidOperationException` quando não há chamador:
  só acontece numa rota mapeada sem `RequireScope`.
- Um conceito de um módulo só vai para `BuildingBlocks` quando outro módulo passa a precisar do
  mesmo comportamento. `BuildingBlocks.Identity` só recebe o que um contrato precisa e cabe na BCL.

### Regras

- **Vertical slice:** cada pasta em `Features/` contém um caso de uso completo (endpoint, comando
  ou consulta, handler). A validação de entrada fica no endpoint (ADR-0030), e o endpoint despacha
  comandos pela facade do módulo (ADR-0036). O que vários slices do módulo compartilham fica em
  `Features/`, fora das pastas dos slices.
- **DDD tático:** o domínio rico (agregados, entidades, value objects, invariantes) fica em
  `Domain/`, compartilhado pelos slices do módulo. O slice orquestra; o domínio decide.
- **`Money` e `Outcome` no domínio do Ledger.** `Money` é o value object de dinheiro (ADR-0018).
  `Outcome<T>` é o resultado de uma operação de domínio: o valor produzido ou a recusa de negócio
  (`LedgerError`), nunca os dois. Os dois são `internal` em `Ledger.Domain`.
- **Fronteiras forçadas, não apenas convencionadas:**
  - Nos módulos, tipos são `internal` por padrão. São públicos os tipos dos projetos `*.Contracts`
    e, no projeto interno de cada módulo, só o tipo de entrada `<Módulo>Module` (`LedgerModule`,
    `PositionsModule`, `CustomersModule`).
  - Testes de arquitetura falham se:
    - um módulo usar o projeto interno de outro módulo;
    - o domínio depender de infraestrutura (EF Core, Npgsql, MongoDB, ASP.NET Core ou a
      `Infrastructure` do próprio módulo);
    - o projeto interno de um módulo expuser outro tipo público além do `<Módulo>Module`;
    - `BuildingBlocks.Identity` tiver qualquer referência fora da BCL;
    - um `*.Contracts` referenciar algo além da BCL e de `BuildingBlocks.Identity`;
    - `Caller`, `CallerMode` ou `Scopes` forem definidos fora de `BuildingBlocks.Identity`.
  - A regra de domínio falha também quando não encontra nenhum tipo de domínio para avaliar.
- **Hosts sem regra de negócio:** apenas configuração e composição dos módulos, pelo
  `<Módulo>Module`.

## Consequências

- **Positivas:**
  - Fronteiras de negócio visíveis na estrutura do código e verificadas pelos testes de
    arquitetura, que reprovam o CI.
  - Os contratos dependem só da BCL e de `BuildingBlocks.Identity`: ler o contrato de outro módulo
    não traz ASP.NET Core, EF Core nem o `Mediator`.
  - A assinatura do endpoint mostra que o chamador vem do usuário autenticado, e há um só tipo para
    ele.
  - Extração futura de um módulo como serviço exige trocar o transporte, não reescrever.
  - Casos de uso localizados: fácil de navegar, revisar e testar.
  - Regras de dinheiro e de recusa ficam com o módulo que as decide.
- **Negativas:**
  - Mais projetos na solução do que um monólito simples, agora com dois de base.
  - `BuildingBlocks` é infraestrutura compartilhada por todos os módulos: uma mudança nele alcança
    todos, e manter fora dele o que é de um módulo só pede disciplina.
  - Cada endpoint chama `ResolvedCaller()`, e uma rota sem `RequireScope` que o chame só falha na
    requisição. O teste de arquitetura reprova a rota da Api que não declara escopo nem acesso
    anônimo explícito, mas não a rota anônima que chame `ResolvedCaller()`.
  - Vertical slices permitem duplicação pontual entre slices; aceita em troca de baixo acoplamento,
    extraindo para o domínio apenas o que é regra de negócio.
  - Se Positions ou Customers precisarem de `Money`, recebem o valor pelos contratos ou o tipo
    migra para um projeto compartilhado com um novo ADR.
