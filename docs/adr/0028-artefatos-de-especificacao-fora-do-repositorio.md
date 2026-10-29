# ADR-0028: Artefatos de especificação fora do repositório

- **Status:** Aceita
- **Data:** 2026-10-29
- **Complementa:** ADR-0024 e ADR-0026
- **Complementado por:** ADR-0029

## Contexto

O processo orientado a especificação (ADR-0024) produz, por funcionalidade, planos, checks,
relatórios de verificação e um arquivo de handoff entre sessões, todos em `.specs/`. O ADR-0026
previa versioná-los. É preciso decidir se esses artefatos de trabalho fazem parte do repositório
público.

## Decisão

`.specs/` é **local e não versionado** (incluído no `.gitignore`).

- O que descreve a solução de forma permanente continua versionado em `docs/`: decisões (ADRs),
  roteiro ([`docs/roadmap.md`](../roadmap.md)), guias e, ao final, modelos de falhas e ameaças.
- O comportamento vigente é descrito pelo código e pelos testes (ADR-0024), que são versionados.
- Planos, checks e relatórios de verificação são instrumentos de construção, consumidos durante a
  implementação.

## Consequências

- **Positivas:**
  - O repositório público contém apenas a solução e a documentação permanente.
  - Artefatos de trabalho podem ser criados e descartados sem poluir o histórico.
- **Negativas:**
  - A evidência da verificação independente de cada funcionalidade não fica disponível no
    repositório; o que fica é o seu resultado: testes versionados, executados no CI.
