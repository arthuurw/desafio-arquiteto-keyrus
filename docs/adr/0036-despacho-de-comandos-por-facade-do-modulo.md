# ADR-0036: Despacho de comandos por facade do módulo

- **Status:** Aceita
- **Data:** 2026-11-01
- **Complementa:** ADR-0030

## Contexto

O ADR-0030 escolheu a biblioteca `Mediator` (geração de código) para os comandos. Até a F4 só o
Ledger tinha comandos: o gerador rodava no projeto do Ledger e os endpoints injetavam `IMediator`.

A F5 traz o segundo módulo com comandos, Customers. Cada módulo mantém seus handlers `internal`
(ADR-0031), então o gerador precisa rodar em cada projeto de módulo, e cada um gera o próprio
`Mediator`. Um spike com dois módulos mostrou o problema: os dois registram `IMediator` no
contêiner, o endpoint que injeta `IMediator` recebe o do último módulo registrado, e o `Send` de um
comando do outro módulo falha em tempo de execução com `MissingMessageHandlerException`. Nada
disso aparece no build.

## Alternativas consideradas

1. **`IMediator` no endpoint** (o que existia). Rejeitada: com dois módulos, resolve o mediador
   errado, e a falha só aparece na requisição.
2. **O `Mediator` gerado de cada módulo injetado no endpoint.** Funciona, mas acopla todo endpoint
   ao tipo gerado; trocar a biblioteca ou o namespace gerado muda todos os endpoints.
3. **Gerador só no host.** Rejeitada: o host não enxerga handlers `internal` dos módulos sem
   quebrar a visibilidade do ADR-0031.
4. **`ISender` registrado com chave por módulo.** Rejeitada: esquecer a chave no endpoint cai no
   mediador errado sem erro de compilação.
5. **Facade de comandos por módulo:** uma interface `internal` do módulo que recebe o `Mediator`
   gerado dele e é a única porta de despacho dos endpoints.

## Decisão

**Alternativa 5.**

- Cada módulo com comandos roda o gerador com o próprio namespace:
  `options.Namespace = "<Módulo>.Mediation"`, tipos gerados `internal` (ADR-0030).
- Cada módulo declara a facade e a implementa sobre o `Mediator` gerado dele:

  ```csharp
  internal interface ICustomersCommands
  {
      ValueTask<WriteResult> Send(ICommand<WriteResult> command, CancellationToken cancellationToken);
  }

  internal sealed class CustomersCommands(Customers.Mediation.Mediator mediator) : ICustomersCommands
  ```

  O Ledger tem o par `ILedgerCommands` e `LedgerCommands`, com uma sobrecarga a mais para a troca de
  limite, que devolve `Outcome<Account>` e não passa pela idempotência.
- Os endpoints injetam só a facade do próprio módulo.
- **Teste de arquitetura:** nenhum tipo do Ledger ou de Customers fora de `LedgerCommands` e
  `CustomersCommands` depende de `IMediator`, `ISender`, `IPublisher` ou do `Mediator` gerado. O
  teste falha para uma fixture cujo endpoint depende de `IMediator`.

## Consequências

- **Positivas:**
  - Cada endpoint despacha para o mediador do próprio módulo, garantido pelo tipo e pelo teste de
    arquitetura, e não pela ordem de registro.
  - Um terceiro módulo com comandos repete o desenho sem tocar nos outros.
  - Os endpoints não dependem do tipo gerado: a biblioteca fica atrás da facade.
- **Negativas:**
  - Um tipo a mais por módulo.
  - A facade aceita qualquer `ICommand<WriteResult>`. O que impede um endpoint de enviar o comando
    de outro módulo é a visibilidade: comandos são `internal` do módulo dono (ADR-0031).
