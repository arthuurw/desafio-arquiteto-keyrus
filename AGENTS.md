# AGENTS.md

Ledger bancário em C#: registro de movimentações e consulta de saldo em um momento.

## Fontes de verdade

- **Código e testes** definem o comportamento vigente. Specs em `.specs/` são planejamento: depois de
  implementadas, o código prevalece.
- **Decisões:** `docs/adr/`. Antes de mudar um comportamento coberto por um ADR, leia o ADR. Mudar a
  decisão exige um novo ADR que substitui o anterior; ADR aceito não é editado no mérito.
- **Nomes de domínio:** `docs/glossario.md`. Todo termo novo de negócio entra no glossário.

## Idioma

Código, banco e eventos em inglês. Comentários curtos, documentação e OpenAPI em português
(ADR-0011).

## tlc-spec-lean

profile: standard
budget: 150k

Os scripts da skill rodam com `python` (não há `python3` neste ambiente).

## Regras de teste

Cada teste prova o cenário real descrito no check que ele cobre:

- **Integração contra infraestrutura real:** PostgreSQL, MongoDB e OpenBao via Testcontainers.
- **Efeito observável:** cada teste afirma o valor que o check define (status HTTP, linha no banco,
  documento no MongoDB, conteúdo de log), nunca chamadas internas.
- **Asserções incondicionais**, no corpo do teste, sempre executadas.
- **Concorrência sincronizada por barreira explícita**, garantindo que as requisições disparem juntas;
  a ordem nunca depende de espera por tempo.
- **Sistema sob teste sempre real;** dublês apenas nas bordas, preferindo fakes escritos à mão.
- **Todo teste roda em toda execução.** O build falha se algum teste for ignorado.
- **Mutação:** Stryker.NET no domínio do Ledger é a ferramenta de injeção de falhas do Verifier.
