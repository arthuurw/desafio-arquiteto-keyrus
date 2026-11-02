# ADR-0024: Desenvolvimento orientado a especificação, com o código como fonte da verdade

- **Status:** Substituída por ADR-0034
- **Data:** 2026-10-29
- **Complementado por:** ADR-0028, ADR-0029

## Contexto

É preciso definir como as funcionalidades são planejadas, construídas e verificadas, e qual
artefato prevalece quando documentação e código divergem.

Riscos a controlar:

- Um requisito lido e nunca transformado em obrigação verificável.
- Uma declaração de "pronto" baseada em autoavaliação de quem construiu.
- Testes que passam sem provar o comportamento.
- Documentação que diverge do código com o tempo e passa a enganar.

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
   no código; um mutante sobrevivente reprova a funcionalidade. O relatório passa por um validador
   automático.

**Perfil `standard`**, declarado em [`AGENTS.md`](../../../AGENTS.md). O perfil padrão da skill
(`light`) não detecta testes que passariam sob uma implementação errada; o `standard` inclui
recálculo da cobertura e injeção de falhas, com Stryker.NET como ferramenta de mutação.

### Hierarquia de verdade

| Artefato | Papel | Em caso de divergência |
|----------|-------|------------------------|
| **Código e testes** | Comportamento vigente; os testes são a especificação executável | Prevalecem |
| **Specs** (`.specs/`, local, ADR-0028) | Planejamento e obrigações de uma entrega | Descartáveis após a entrega; não são mantidas em paralelo ao código |
| **ADRs** | O porquê das decisões e as alternativas rejeitadas | Código que contraria um ADR é defeito ou mudança de decisão; mudança exige novo ADR |
| **OpenAPI** | Contrato da API | Gerada a partir do código; não diverge |
| **Glossário e fronteiras de módulos** | Linguagem e estrutura | Testes de arquitetura quebram o build em caso de violação |

Princípio: **nada que possa ser derivado do código é mantido à mão.** O que não pode ser
derivado (motivação, alternativas, trade-offs) fica nos ADRs.

### Relação com DDD

DDD continua sendo a abordagem de modelagem do domínio (ADR-0010). Especificação e DDD atuam em
planos diferentes: DDD decide a forma do modelo; a especificação decide o que cada entrega precisa
provar.

## Consequências

- **Positivas:**
  - Todo critério de aceite tem um teste nomeado que o decide; omissões aparecem como células vazias.
  - "Pronto" é declarado por um verificador independente, com evidência, e não pelo autor.
  - Testes falsos são detectados por injeção de falhas.
  - Documentação não envelhece silenciosamente: o que é derivável é gerado, o resto é decisão datada.
- **Negativas:**
  - Mais artefatos por funcionalidade (plan, checks, verificação).
  - A verificação independente adiciona tempo ao fim de cada entrega.
