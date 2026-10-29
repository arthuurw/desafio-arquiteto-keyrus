# ADR-0029: Board do GitHub como versão pública das especificações

- **Status:** Aceita
- **Data:** 2026-10-29
- **Complementa:** ADR-0024 e ADR-0028

## Contexto

Os artefatos de trabalho do processo orientado a especificação ficam locais (ADR-0028). Ainda
assim, quem avalia ou evolui a solução precisa enxergar **o que** foi planejado em cada entrega,
**por quê**, com quais critérios de aceite, e a evidência de que foi verificado de forma
independente.

## Decisão

Um **GitHub Project** (board) é a versão pública das especificações.

| Artefato | Onde fica | Conteúdo |
|----------|-----------|----------|
| **PRD** | [`docs/prd.md`](../prd.md), versionado | Problema, consumidores, objetivos, requisitos funcionais (`RF-NN`) e não funcionais (`RNF-NN`), fora de escopo, rastreabilidade requisito → funcionalidade |
| **Épico** | Uma issue por funcionalidade (F0 a F10) | **TDD resumido** na descrição: contexto, problema, escopo, solução técnica, riscos, segurança, testes, observabilidade, rollback, ADRs relacionados |
| **User story** | Sub-issue do épico | História no formato "Como `<consumidor>`, quero `<capacidade>`, para `<benefício>`", critérios de aceite em EARS, checks (afirmação e teste que a prova), requisitos do PRD atendidos, ADRs |
| **Evidência de verificação** | Comentário no épico, antes de fechar | Resultado do verificador independente: checks provados, cobertura recalculada, falhas injetadas e mortas |

### Ciclo

1. **Agora:** PRD e os 11 épicos com TDD resumido, no estado `Backlog`.
2. **PLAN de cada funcionalidade:** as user stories são criadas como sub-issues a partir do
   `plan.md`; o épico vai para `Planejada`.
3. **BUILD:** commits referenciam a user story (`refs #N`); épico em `Em construção`.
4. **VERIFY:** épico em `Em verificação`; ao final, o relatório do verificador é publicado como
   comentário e a funcionalidade vai para `Concluída`.

User stories são detalhadas apenas quando a funcionalidade é planejada: antes disso, o detalhe
seria especulação e envelheceria.

## Consequências

- **Positivas:**
  - Planejamento, critérios e evidência de verificação públicos, sem versionar artefatos de
    trabalho.
  - Rastreabilidade de ponta a ponta: requisito do PRD → épico → user story → commit → teste.
- **Negativas:**
  - Informação replicada entre `.specs/` local e o board; o board é atualizado nos marcos do ciclo,
    não a cada mudança local.
  - O board fica fora do repositório git; o PRD e os ADRs, que são a base dele, ficam versionados.
