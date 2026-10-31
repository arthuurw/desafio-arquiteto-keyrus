# ADR-0034: Desenvolvimento orientado a especificação, com mutações declaradas no AGENTS.md

- **Status:** Aceita
- **Data:** 2026-10-31
- **Substitui:** ADR-0024
- **Complementado por:** ADR-0028, ADR-0029

## Contexto

É preciso definir como as funcionalidades são planejadas, construídas e verificadas, e qual
artefato prevalece quando documentação e código divergem.

Riscos a controlar:

- Um requisito lido e nunca transformado em obrigação verificável.
- Uma declaração de "pronto" baseada em autoavaliação de quem construiu.
- Testes que passam sem provar o comportamento.
- Documentação que diverge do código com o tempo e passa a enganar.

O ADR-0024 decidiu o processo com a skill `tlc-spec-lean` no perfil `standard`, declarado no
`AGENTS.md`. A skill deixou de ter perfis: a configuração passou a ser o número de mutações que o
verificador injeta, `mutations: N`, e é isso que o `AGENTS.md` declara. O ADR-0024 também afirmava
que os testes de arquitetura "quebram o build", quando eles falham na execução dos testes. Este ADR
substitui o ADR-0024 por inteiro.

## Alternativas consideradas

1. **TDD (teste falhando antes do código).** Rejeitada como processo: o comportamento já está
   definido na especificação antes da construção, e o ritual de falhar primeiro não acrescenta
   informação sobre o que construir. A função técnica que ele cumpre (provar que o teste detecta
   um erro) é coberta, de forma mais rigorosa, pela injeção de falhas na verificação.
2. **Especificação detalhada como fonte da verdade** (catálogo de componentes, interfaces e
   tarefas). Rejeitada: detalhes reversíveis envelhecem em semanas e passam a enganar com a
   autoridade de um documento escrito.
3. **Desenvolvimento orientado a especificação com obrigações congeladas e código como fonte da
   verdade**, usando a skill `tlc-spec-lean`.

## Decisão

**Alternativa 3.**

### Processo por funcionalidade

```
PLAN → CHECKS → BUILD → VERIFY
```

1. **Plan** (`.specs/features/<feature>/plan.md`): problema, fluxo, impacto, entidades, interface,
   decisões irreversíveis e critérios de aceite em EARS. Revisado e aprovado antes de qualquer
   código.
2. **Checks** (`checks.md`): cada critério vira afirmações observáveis com valor concreto, cada uma
   com a **prova** (o teste específico que a decide). Uma tabela de cobertura liga cada membro de
   cada conjunto enumerado (cada status de cada rota, cada transição) a um check. Uma vez
   aprovados, os checks não são alterados; um check errado é levado ao responsável, não editado.
3. **Build:** testes escritos a partir dos checks, nunca a partir da implementação; código e testes
   evoluem juntos.
4. **Verify:** um verificador independente (agente novo, sem o contexto de quem construiu) roda
   todas as provas, localiza a asserção de cada check, recalcula a cobertura e **injeta falhas**
   no código. O relatório passa por um validador automático.

### Configuração: `mutations: N` no AGENTS.md

A seção `## tlc-spec-lean` do [`AGENTS.md`](../../AGENTS.md) declara `mutations: N`, hoje 3. O
`checks.md` de cada funcionalidade copia o número na linha `Mutations:`.

- O verificador injeta N mutações onde as regras da entrega vivem: uma condição invertida, um
  sinal trocado, um filtro removido. Cada mutação é executada de novo contra as provas; uma
  mutação que nenhum teste mata é um achado e reprova a verificação até ser corrigido.
- Quando N parece baixo para a funcionalidade, o verificador diz isso no relatório e o usuário
  decide se aumenta.

### Mutação contínua no CI

Independente das N mutações da verificação, o CI roda o Stryker.NET sobre o domínio do Ledger em
cada execução, no passo `Mutation - Ledger domain`, com a configuração em
`tests/Ledger.UnitTests/stryker-config.json`. O limiar de quebra é **80**: com pontuação de mutação
abaixo de 80, o CI reprova. O limiar fica no CI, e não no `AGENTS.md`, porque é aplicado por
ferramenta em toda execução, não por funcionalidade.

### Hierarquia de verdade

| Artefato | Papel | Em caso de divergência |
|----------|-------|------------------------|
| **Código e testes** | Comportamento vigente; os testes são a especificação executável | Prevalecem |
| **Specs** (`.specs/`, local, ADR-0028) | Planejamento e obrigações de uma entrega | Descartáveis após a entrega; não são mantidas em paralelo ao código |
| **ADRs** | O porquê das decisões e as alternativas rejeitadas | Código que contraria um ADR é defeito ou mudança de decisão; mudança exige novo ADR |
| **OpenAPI** | Contrato da API | Gerada a partir do código; a cópia versionada é verificada por teste contra a gerada |
| **Glossário e fronteiras de módulos** | Linguagem e estrutura | Testes de arquitetura falham em caso de violação de fronteira, e o CI reprova |

Princípio: **nada que possa ser derivado do código é mantido à mão.** O que não pode ser
derivado (motivação, alternativas, trade-offs) fica nos ADRs. Uma citação a ADR substituído fora de
`docs/adr/` é recusada por script no CI.

### Relação com DDD

DDD continua sendo a abordagem de modelagem do domínio (ADR-0031). Especificação e DDD atuam em
planos diferentes: DDD decide a forma do modelo; a especificação decide o que cada entrega precisa
provar.

## Consequências

- **Positivas:**
  - Todo critério de aceite tem um teste nomeado que o decide; omissões aparecem como células vazias.
  - "Pronto" é declarado por um verificador independente, com evidência, e não pelo autor.
  - Testes falsos são detectados por injeção de falhas, na verificação de cada entrega e no CI.
  - A configuração do processo é um número no `AGENTS.md`, que o `checks.md` copia e o verificador
    aplica, sem perfil a interpretar.
  - Documentação não envelhece silenciosamente: o que é derivável é gerado, o resto é decisão datada.
- **Negativas:**
  - Mais artefatos por funcionalidade (plan, checks, verificação).
  - A verificação independente adiciona tempo ao fim de cada entrega.
  - O Stryker no CI aumenta o tempo de cada execução do pipeline.
