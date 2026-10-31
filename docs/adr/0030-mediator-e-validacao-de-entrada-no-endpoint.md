# ADR-0030: Biblioteca `Mediator` para comandos, com validação de entrada no endpoint

- **Status:** Aceita
- **Data:** 2026-10-31
- **Substitui:** ADR-0008

## Contexto

A arquitetura CQRS (ADR-0006) separa comandos (registrar lançamento) de consultas (saldo,
extrato). Um mediador desacopla os pontos de entrada (endpoints HTTP) dos handlers e oferece um
pipeline para preocupações transversais.

O ADR-0008 escolheu a biblioteca `Mediator` e previa a validação de entrada como *pipeline
behavior*, antes do handler. O Ledger foi construído de outro jeito: a entrada é lida e validada no
endpoint, pelo `LedgerInput`, e o único behavior registrado é o de idempotência. Este ADR substitui
o ADR-0008 por inteiro: mantém a escolha da biblioteca e registra onde a validação de fato vive, e
por quê.

Forças que levaram a validação para o endpoint:

- **Dinheiro chega como string JSON** (ADR-0018). Com a desserialização automática do corpo, um
  número JSON no lugar da string derruba a desserialização inteira, sem erro por campo.
- **O comando carrega valores já interpretados** (`Money`, categoria do catálogo, UUID). O hash de
  idempotência é calculado sobre esses valores (ADR-0032): `"10.0"` e `"10.00"` são a mesma
  requisição. Um comando com texto bruto obrigaria a interpretar duas vezes.
- **Ordem com a idempotência.** A chave é lida antes do corpo (ADR-0032). Uma entrada inválida
  responde `400` antes de o comando chegar ao behavior de idempotência, e não consome a chave: o
  chamador corrige o corpo e repete com a mesma chave.

## Alternativas consideradas

### Biblioteca de mediação

1. **MediatR 13 ou superior.** Biblioteca mais conhecida do ecossistema.
   Rejeitada: passou a ter licença comercial (com edição comunitária gratuita sujeita a condições
   de receita e uso). Em um sistema bancário, adotar dependência com licenciamento comercial exige
   avaliação jurídica e de custo que não se justifica quando há alternativa equivalente livre.
2. **MediatR 12.x** (última versão sob Apache 2.0).
   Rejeitada: versão congelada, sem evolução e sem garantia de correções de segurança. Adotar uma
   dependência abandonada em sistema financeiro cria dívida técnica desde o primeiro dia.
3. **`Mediator`** (martinothamar/Mediator, licença MIT).
   API praticamente equivalente à do MediatR (`ICommand`, `ICommandHandler`, `IPipelineBehavior`),
   implementada com *source generators*.
4. **Sem biblioteca** (handlers injetados diretamente, preocupações transversais via decorators).
   Rejeitada: menos indireção, mas exige reimplementar o pipeline e perde a convenção conhecida
   pelo time.

### Onde validar a entrada

1. **Behavior de validação no pipeline do `Mediator`.** Rejeitada: o comando precisaria carregar
   o texto bruto do corpo, e a interpretação aconteceria duas vezes, no behavior e no handler. A
   falha de desserialização de um número no lugar de string aconteceria antes de qualquer behavior.
2. **Desserialização automática do corpo para um tipo de requisição.** Rejeitada: um campo com o
   tipo JSON errado vira falha da requisição inteira, não erro do campo.
3. **Leitura campo a campo no endpoint, pelo `LedgerInput`,** antes de montar o comando.

## Decisão

**Biblioteca: alternativa 3, `Mediator`. Validação: alternativa 3, no endpoint.**

### `Mediator`

- **Licença MIT**, sem restrição de uso comercial.
- **Verificação em tempo de compilação:** handlers são descobertos e ligados por geração de código.
  Um comando sem handler resulta em erro de build, não em exceção em produção.
- **Desempenho:** sem reflection em tempo de execução; despacho por código gerado.
- **Migração barata:** API próxima à do MediatR; o time reconhece o padrão imediatamente.
- Tipos gerados como `internal`, com o tempo de vida do `DbContext` (scoped), como o resto do
  módulo (ADR-0031).

### Validação de entrada no endpoint

- Cada endpoint lê o corpo com o `LedgerInput`, campo a campo. Campo ausente, com tipo JSON errado
  ou fora do formato vira erro daquele campo; todos os erros saem juntos num `400`
  `validation-error` em Problem Details (ADR-0020).
- O `LedgerInput` devolve valores já interpretados: `Money` para valores e limite, categoria do
  catálogo fechado do tipo de lançamento, referência externa no formato aceito, UUID do cliente.
- O comando só é montado com entrada válida. O domínio mantém as próprias invariantes como segunda
  barreira: `Money.Of` e a `Account` lançam exceção para valores que o endpoint nunca deixa passar.

### Pipeline behaviors

- **Idempotência** (`IdempotencyBehavior`), restrita aos comandos que implementam
  `IIdempotentCommand` (ADR-0032). É o único behavior registrado.
- Nenhum behavior de validação. Outro behavior transversal, como logging ou métricas por comando,
  entra com decisão própria.

## Consequências

- **Positivas:**
  - Sem risco de licenciamento.
  - Erros de configuração de handlers detectados no build.
  - Erros de entrada respondidos por campo, inclusive o número JSON no lugar de string.
  - O handler e o hash de idempotência recebem só valores interpretados e válidos.
  - Entrada inválida não consome a chave de idempotência.
- **Negativas:**
  - Biblioteca menos conhecida que o MediatR; menos material de referência.
  - Source generators podem tornar mensagens de erro de compilação menos óbvias para quem não
    conhece o mecanismo.
  - Cada endpoint novo precisa chamar o `LedgerInput`; a validação não é aplicada sozinha a todo
    comando, como seria num behavior.
