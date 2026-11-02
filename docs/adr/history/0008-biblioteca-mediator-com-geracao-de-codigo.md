# ADR-0008: Biblioteca `Mediator` (geração de código) para comandos e consultas

- **Status:** Substituída por ADR-0030
- **Data:** 2026-10-29

## Contexto

A arquitetura CQRS (ADR-0006) separa comandos (registrar lançamento) de consultas (saldo,
extrato). Um mediador desacopla os pontos de entrada (endpoints HTTP) dos handlers e oferece um
pipeline para preocupações transversais: validação, logging, métricas e controle de transação.

A escolha natural no ecossistema .NET seria o MediatR, mas sua licença mudou.

## Alternativas consideradas

1. **MediatR 13 ou superior.** Biblioteca mais conhecida do ecossistema.
   Rejeitada: passou a ter licença comercial (com edição comunitária gratuita sujeita a condições
   de receita e uso). Em um sistema bancário, adotar dependência com licenciamento comercial exige
   avaliação jurídica e de custo que não se justifica quando há alternativa equivalente livre.
2. **MediatR 12.x** (última versão sob Apache 2.0).
   Rejeitada: versão congelada, sem evolução e sem garantia de correções de segurança. Adotar uma
   dependência abandonada em sistema financeiro cria dívida técnica desde o primeiro dia.
3. **`Mediator`** (martinothamar/Mediator, licença MIT).
   API praticamente equivalente à do MediatR (`IRequest`, `IRequestHandler`, `IPipelineBehavior`),
   implementada com *source generators*.
4. **Sem biblioteca** (handlers injetados diretamente, preocupações transversais via decorators).
   Rejeitada: menos indireção, mas exige reimplementar o pipeline e perde a convenção conhecida
   pelo time.

## Decisão

**Alternativa 3: `Mediator`.**

Motivos:

- **Licença MIT**, sem restrição de uso comercial.
- **Verificação em tempo de compilação:** handlers são descobertos e ligados por geração de código.
  Um comando sem handler resulta em erro de build, não em exceção em produção.
- **Desempenho:** sem reflection em tempo de execução; despacho por código gerado.
- **Migração barata:** API próxima à do MediatR; o time reconhece o padrão imediatamente.

Pipeline behaviors previstos:

- Logging estruturado e métricas por comando/consulta.
- Validação de entrada antes do handler.
- (Comandos) controle de transação e idempotência, a detalhar em ADRs próprios.

## Consequências

- **Positivas:**
  - Sem risco de licenciamento.
  - Erros de configuração de handlers detectados no build.
- **Negativas:**
  - Biblioteca menos conhecida que o MediatR; menos material de referência.
  - Source generators podem tornar mensagens de erro de compilação menos óbvias para quem não
    conhece o mecanismo; documentar no guia de desenvolvimento.
