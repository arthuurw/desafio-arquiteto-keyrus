# AGENTS.md

Ledger bancário em C#: registro de movimentações e consulta de saldo em um momento.

## Fontes de verdade

- **Código e testes** definem o comportamento vigente. Specs em `.specs/` (locais, não versionadas) são planejamento: depois de
  implementadas, o código prevalece. Código, comentários e scripts se explicam sozinhos: não citam
  arquivos do `.specs/` nem identificadores de check (`C<n>`), que não existem para quem clona o repositório.
- **Decisões:** `docs/adr/`. Antes de mudar um comportamento coberto por um ADR, leia o ADR. Mudar a
  decisão exige um novo ADR que substitui o anterior; ADR aceito não é editado no mérito.
- **Nomes de domínio:** `docs/glossario.md`. Todo termo novo de negócio entra no glossário.

## Idioma

Código, banco e eventos em inglês. Comentários curtos, documentação e OpenAPI em português
(ADR-0011).

## tlc-spec-lean

mutations: 3

Os scripts da skill rodam com `python` (não há `python3` neste ambiente).

## Fluxo de trabalho

Board → Research → Plan → Implement → Verify, conduzido pela skill `tlc-spec-lean`. O board do
GitHub é a versão pública das specs locais (ADR-0029). Toda decisão relevante é perguntada ao
usuário, uma por vez, com opções e recomendação.

Toda mudança nasce em branch própria a partir da `main` atualizada (`feature/<nome>` para um
épico, criada no início do Research); a `main` recebe mudanças só por pull request, inclusive
documentação.

1. **Board.** O trabalho parte de um épico no board, com o TDD resumido na descrição. O épico
   avança pelo campo `Status`: `Backlog → Planejada → Em construção → Em verificação → Concluída`.
2. **Research.** Antes de planejar: ler `.specs/STATE.md` e reconciliar com o git; ler o épico, os
   ADRs que ele cita, os requisitos do `docs/prd.md` e as lições confirmadas
   (`lessons.py list --status confirmed`). Fontes, nesta ordem: código e convenções do repositório,
   documentação do projeto, documentação das bibliotecas (Context7), busca na web. O que não for
   encontrado é declarado como incerto, nunca inventado.
3. **Plan** (`tlc-spec-lean`: PLAN e CHECKS). `plan.md` validado por `validate_plan.py` e aprovado
   pelo usuário; decisão que alcança além do épico vira ADR novo. `checks.md` validado por
   `validate_checks.py`, com o `## Handoff`. Publicação no board: cada user story como sub-issue do
   épico, com a história ("Como `<consumidor>`, quero..., para..."), critérios EARS, checks com a
   prova de cada um, requisitos do PRD e ADRs. Épico em `Planejada` quando todo check pertence a
   exatamente uma user story.
4. **Implement** (`tlc-spec-lean`: BUILD). Épico em `Em construção`. Testes escritos a partir dos checks, código junto. Antes de cada commit: build sem warnings,
   testes verdes e `check_commit.py`. Commits em Conventional Commits, em inglês, com
   `refs #<user story>`. O fluxo de cada rota ou rotina em segundo plano tocada muda no mesmo
   commit que o código (seção Fluxos).
5. **Verify** (`tlc-spec-lean`: VERIFY). Épico em `Em verificação`. Verificador independente
   (sub-agente novo, nunca o autor) sobre a branch inteira, com todos os checks, e os achados
   fechados por commit. Depois, o `/code-review` e a conferência dos fluxos (seções abaixo). Por
   último, a higiene de comentários (seção abaixo), seguida de
   `check_proofs.py` em `HEAD` e `validate_verification.py` com saída 0; lições registradas. O
   relatório vai como comentário no épico. Pull request para `main` com `closes #<user story>`;
   push e merge somente com autorização explícita do usuário, precedidos de auditoria do conteúdo
   enviado (segredos, dados pessoais, caminhos locais, e-mails). Épico fechado em `Concluída`;
   `docs/roadmap.md`, handoff e documentos afetados atualizados.

### Code review

Toda mudança, épico ou não, passa pelo `/code-review` da branch contra a `main`, com tudo já
commitado e depois do verificador independente. Todo achado é corrigido por padrão, sem perguntar
nem esperar autorização do usuário: cada correção vai em commit próprio, com build sem warnings e
testes verdes. A exceção é o achado cuja correção mudaria uma decisão de ADR, um check ou o
contrato aprovado no plano: esse vai ao usuário, uma pergunta por vez. Um achado descartado como
falso positivo é registrado com o motivo no relatório do épico.

### Fluxos

Toda rota da Api e toda rotina em segundo plano, na Api ou no Worker, tem o seu fluxo em
`docs/arquitetura/`: diagrama de sequência na legenda de `docs/arquitetura/README.md`, ou passos
numerados quando não tem desvio. Em toda mudança, épico ou não, depois do `/code-review`, um
sub-agente novo confere, com tudo já commitado, cada fluxo que a branch cria ou altera e cada um cujo
código a branch altera. Feito quando toda rota ou rotina que a branch adiciona tem o seu, e cada
fluxo conferido:

- mostra cada passo, recusa (status e `type`) e fronteira de transação do código em `HEAD`, e só eles;
- liga cada etapa a um arquivo que existe em `HEAD` e a um ADR vigente.

As correções vão num commit `docs:` próprio, seguido de build sem warnings e testes verdes.

### Higiene de comentários

Última tarefa de toda mudança, épico ou não, antes do pull request. Um sub-agente novo revisa, com
tudo já commitado, cada comentário que o diff da branch contra a `main` adiciona, altera ou deixa
junto a código alterado, e mexe só em comentários. Feito quando cada um deles:

- é curto, em português, e explica o porquê que o código sozinho não mostra;
- descreve o código em `HEAD` como ele é;
- cita ADR vigente e segue as regras de "Fontes de verdade";
- ainda tem o código que explica ao lado (o órfão sai).

As correções vão num commit `docs:` próprio, seguido de build sem warnings e testes verdes.

### Board

Projeto número 2 de `arthuurw`, ligado a `arthuurw/desafio-arquiteto-keyrus`. IDs para
`gh project item-edit`: projeto `PVT_kwHOAg7S384BpWqL`, campo `Status`
`PVTSSF_lAHOAg7S384BpWqLzhnyrEo`, opções `Backlog` `df6444b4`, `Planejada` `138f51dd`,
`Em construção` `73981f15`, `Em verificação` `cf99bf03`, `Concluída` `93055f7e`.

## Regras de teste

Cada teste prova o cenário real descrito no check que ele cobre:

- **Integração contra infraestrutura real:** PostgreSQL, MongoDB e Keycloak via Testcontainers, e o OpenBao do mesmo jeito quando entrar.
- **Efeito observável:** cada teste afirma o valor que o check define (status HTTP, linha no banco,
  documento no MongoDB, conteúdo de log), nunca chamadas internas.
- **Asserções incondicionais**, no corpo do teste, sempre executadas.
- **Concorrência sincronizada por barreira explícita**, garantindo que as requisições disparem juntas;
  a ordem nunca depende de espera por tempo.
- **Sistema sob teste sempre real;** dublês apenas nas bordas, preferindo fakes escritos à mão.
- **Todo teste roda em toda execução.** Teste pulado faz a execução falhar, e teste explícito roda como qualquer outro.
- **Mutação:** Stryker.NET no domínio do Ledger é a ferramenta de injeção de falhas do Verifier.
