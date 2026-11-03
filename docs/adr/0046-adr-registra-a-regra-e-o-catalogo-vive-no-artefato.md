# ADR-0046: ADR registra a regra; o catálogo vive no artefato que o executa

- **Status:** Aceita
- **Data:** 2026-11-02
- **Complementa:** ADR-0026

## Contexto

ADR aceito não é editado no mérito: mudar a decisão pede um ADR novo que substitui o anterior
(ADR-0034, `docs/adr/README.md`). Vários ADRs trazem, além da regra, um catálogo: as rotas e os
`type` da API, as operações auditadas, os grants de cada role. A cada funcionalidade, o catálogo
ganha uma linha, e a regra fica igual.

Em seis funcionalidades, isso substituiu ADRs inteiros só para acrescentar linhas:

- **Isolamento:** 0015 → 0033 → 0038 → 0043 → 0044.
- **API:** 0020 → 0039 → 0042.
- **Idempotência:** 0014 → 0032 → 0037 (substituído pelo ADR-0059).

Quem lê percorre cinco versões para achar uma linha nova, e o catálogo de um ADR vigente já
descreve rotas e `type` que o código adiou ou ainda não tem.

## Alternativas consideradas

1. **Substituir o ADR inteiro a cada linha de catálogo**, como até aqui. A regra de não editar
   ADR aceito fica intacta, mas o histórico vira uma pilha de cópias quase iguais.
2. **Editar a tabela do ADR aceito.** Rejeitada: o ADR deixa de registrar o que foi decidido
   naquele momento, e a regra de imutabilidade passa a ter exceção.
3. **O ADR registra a regra, e o catálogo vive no artefato que o código executa ou que um teste
   confere contra ele.**

## Decisão

**Alternativa 3.**

| Catálogo | Onde vive |
|----------|-----------|
| Rotas, escopo de cada rota, status e `type` de Problem Details | `docs/api/openapi-v1.json`, igual ao documento servido (teste) |
| Passos, recusas e operações auditadas de cada rota e rotina | Fluxos em `docs/arquitetura/` (ADR-0040) e o código de auditoria de cada módulo |
| Schemas, roles, grants e funções do banco | Migrações e `docker/postgres/init` |
| Clients e escopos do provedor de identidade | `docker/keycloak/realm-ledger.json` |
| Termos de negócio | `docs/glossario.md` |

- Uma linha nova num catálogo vai para o artefato dele, no mesmo commit do código que a cria.
- Só uma regra que muda pede ADR novo.
- As tabelas dos ADRs aceitos, inclusive as do ADR-0041 e do ADR-0042, ficam como estão. Elas
  valem como exemplo do momento em que foram escritas, e o catálogo vigente é o do artefato.

## Consequências

- **Positivas:**
  - O ADR muda quando a decisão muda, e o histórico mostra decisões, não linhas de tabela.
  - O catálogo vigente fica num lugar que um teste ou o código confere, e não envelhece em
    silêncio.
- **Negativas:**
  - Quem lê um ADR antigo pode tomar a tabela dele pelo catálogo de hoje; o `README.md` de
    `docs/adr/` diz onde fica cada catálogo.
  - Um ADR que mistura regra e catálogo precisa separar os dois ao ser substituído no futuro.
