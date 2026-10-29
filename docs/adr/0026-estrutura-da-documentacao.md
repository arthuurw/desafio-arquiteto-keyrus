# ADR-0026: Estrutura da documentação

- **Status:** Aceita
- **Data:** 2026-10-29
- **Complementado por:** ADR-0028

## Contexto

O enunciado exige README com instruções para rodar localmente e toda a documentação no
repositório, e pergunta o que seria essencial documentar para que outra pessoa do time entenda e
evolua a solução. Pelo ADR-0024, o código é a fonte da verdade: a documentação deve registrar o que
o código não consegue expressar e nunca duplicar o que pode ser derivado dele.

## Decisão

| Documento | Propósito | Quando é escrito |
|-----------|-----------|------------------|
| `README.md` | Porta de entrada: o que é, como subir com um comando, URLs locais, como obter tokens, como rodar testes, mutação e carga, decisões-chave, limitações, mapa da documentação | Por último, refletindo o sistema construído |
| `AGENTS.md` | Regras operacionais para quem implementa: fontes de verdade, idioma, perfil do processo, regras de teste | Mantido junto com o código |
| `docs/contexto-e-premissas.md` | Problema, requisitos, escopo | Início |
| `docs/prd.md` | Requisitos do produto (`RF-NN`, `RNF-NN`) e rastreabilidade para as funcionalidades | Início, revisado a cada funcionalidade |
| Board do GitHub | Épicos com TDD resumido, user stories e evidência de verificação (ADR-0029) | A cada funcionalidade |
| `docs/adr/` | Decisões, alternativas rejeitadas e consequências | A cada decisão |
| `docs/mapa-de-decisoes.md` | Visão do raciocínio: princípios, mapa mental, encadeamento das decisões | A cada decisão |
| `docs/glossario.md` | Linguagem ubíqua português/inglês | A cada termo novo |
| `docs/guias/` | Explicações didáticas dos temas técnicos | Junto com o ADR correspondente |
| `docs/melhorias-futuras.md` | Evoluções conscientemente adiadas | A cada decisão |
| `docs/arquitetura/` | Diagramas C4 (contexto, containers, componentes do Ledger) e sequências dos fluxos críticos | Durante e após a implementação |
| `docs/modelo-de-falhas.md` | Por componente: efeito da falha, detecção, mitigação, recuperação | Durante e após a implementação |
| `docs/modelo-de-ameacas.md` | STRIDE do sistema, cada ameaça ligada ao ADR que a mitiga | Durante e após a implementação |
| `docs/runbook.md` | Resposta a cada alerta, rotação de chaves, pedidos LGPD, restauração de backup | Durante e após a implementação |
| `docs/guia-de-desenvolvimento.md` | Como adicionar um slice, convenções, uso do processo, armadilhas conhecidas | Durante e após a implementação |
| `requests.http` | Chamadas de exemplo executáveis | Junto com cada endpoint |
| `docs/roadmap.md` | Ordem e situação das funcionalidades (ADR-0027) | A cada funcionalidade |
| `.specs/` | Planos, checks e verificações de cada funcionalidade (ADR-0024); local, não versionado (ADR-0028) | Por funcionalidade |

Diagramas em Mermaid, renderizados nativamente pelo GitHub e versionados como texto.

## Alternativas consideradas

1. **Wiki do GitHub ou ferramenta externa.** Rejeitada: o enunciado exige documentação no
   repositório, e documentação fora do repositório não acompanha as versões do código.
2. **Documento único extenso.** Rejeitada: mistura públicos e propósitos (avaliador, operador,
   desenvolvedor) e dificulta a manutenção.
3. **Diagramas em ferramenta gráfica (imagens).** Rejeitada: não são revisáveis em diff e
   envelhecem sem que ninguém perceba.

## Consequências

- **Positivas:**
  - Cada público encontra o que precisa: avaliador (README, mapa, ADRs), operador (runbook, modelo
    de falhas), desenvolvedor (AGENTS.md, guia de desenvolvimento, glossário).
  - Documentos que descrevem o sistema são escritos a partir do código real, não de intenções.
- **Negativas:**
  - Vários documentos para manter; mitigado pela regra de não duplicar o que o código expressa.
