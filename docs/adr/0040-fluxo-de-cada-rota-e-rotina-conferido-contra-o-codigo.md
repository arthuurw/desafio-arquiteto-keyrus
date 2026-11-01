# ADR-0040: Fluxo de cada rota e rotina, conferido contra o código

- **Status:** Aceita
- **Data:** 2026-11-01
- **Complementa:** ADR-0026

## Contexto

O ADR-0026 reserva `docs/arquitetura/` para os diagramas C4 e as "sequências dos fluxos críticos",
sem dizer quais fluxos são críticos nem quando conferir um diagrama contra o código. Num ledger,
quase toda rota decide dinheiro, dado pessoal ou consistência: crédito, estorno, troca de limite,
leitura de dados pessoais, relay do outbox, limpezas. Quem chega ao projeto precisa ver o caminho
completo de cada uma, com recusas e fronteiras de transação, sem ler todo o código.

Diagrama escrito à mão envelhece em silêncio. A primeira conferência dos fluxos contra o código, ao
fim do épico F5, achou mais de vinte divergências, entre elas uma recusa que faltava e uma regra de
auditoria descrita de forma mais forte do que o código cumpre.

## Alternativas consideradas

1. **Só os fluxos críticos, como no ADR-0026.** Menos documentos, mas cada pessoa decide o que é
   crítico, e a rota que fica de fora é justamente a que ninguém revisa.
2. **Diagramas gerados do código.** Sempre atuais, mas mostram chamadas, não decisões: não dizem
   qual recusa sai com qual `type`, onde a transação começa nem qual ADR decide cada passo.
3. **Todo fluxo documentado e conferido no Verify.** Mais texto para manter, pago por uma
   conferência por sub-agente a cada mudança que toca o código de um fluxo.

## Decisão

Toda rota da Api e toda rotina em segundo plano, na Api ou no Worker, tem o seu fluxo em
`docs/arquitetura/`: diagrama de sequência em Mermaid, na legenda de `docs/arquitetura/README.md`, ou
passos numerados quando não tem desvio. Cada etapa liga um arquivo do código e o ADR que a decide.

O fluxo muda no mesmo commit que o código. No Verify, depois do `/code-review`, um sub-agente novo
confere contra `HEAD` cada fluxo que a branch cria ou altera e cada um cujo código ela altera. A regra
operacional fica na seção "Fluxos" do `AGENTS.md`.

## Consequências

- **Positivas:**
  - Cada rota tem um caminho completo legível sem o código: passos, recusas com status e `type`,
    transações e ADRs.
  - Divergência entre fluxo e código vira achado do Verify, não surpresa de quem lê.
- **Negativas:**
  - Toda mudança de comportamento edita também um diagrama.
  - O Verify ganha mais um sub-agente por mudança.
- **Em aberto:** os diagramas C4 do ADR-0026 continuam por escrever.
