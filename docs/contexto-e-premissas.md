# Contexto e premissas

## Problema

Um banco digital possui um sistema legado que registra movimentações financeiras de clientes
(créditos e débitos) e fornece a posição consolidada de saldo ao longo do tempo. O sistema
apresenta lentidão, instabilidade em horários de pico e dificuldade de manutenção. O objetivo
é repensar a solução do zero, apta a operar em ambiente financeiro real.

## Requisitos funcionais

- Registrar movimentações financeiras (créditos e débitos) em contas de clientes.
- Consultar a posição consolidada (saldo) de um cliente **em um determinado momento**.

## Requisitos não funcionais

- Operar de forma confiável sob instabilidade, alta demanda e falhas parciais.
- Nenhuma inconsistência de dados é aceitável: impacto direto ao cliente e à operação.
- Proteção de dados sensíveis de clientes.

## Requisitos obrigatórios da entrega

- Implementação em C#.
- Testes automatizados.
- Repositório público no GitHub.
- README com instruções claras para rodar localmente.
- Toda documentação no próprio repositório.
- Compilação sem erros e sem warnings.

## Escopo da entrega

Prazo estimado de 2 a 4 dias. Decisão: **um serviço bem construído** cobrindo o núcleo do
problema (ledger, idempotência, concorrência, consulta de saldo em um momento), com a evolução
para produção (alta disponibilidade, mensageria, separação de serviços, infraestrutura em nuvem)
**documentada**, não implementada.

Código e documentação têm o mesmo peso na avaliação.

## Premissas de trabalho

Premissas assumidas no início do trabalho e onde cada uma foi formalizada:

| Premissa | Formalizada em |
|----------|----------------|
| O núcleo do domínio é um **ledger append-only**: movimentações são imutáveis; correções são feitas por estorno, nunca por alteração ou exclusão | [ADR-0003](adr/0003-saldo-materializado-e-saldo-acumulado-por-lancamento.md), [ADR-0033](adr/0033-isolamento-de-dados-por-schema-e-role.md) |
| O saldo é um valor **derivado** dos lançamentos | [ADR-0003](adr/0003-saldo-materializado-e-saldo-acumulado-por-lancamento.md) |
| Requisições de escrita são **idempotentes** | [ADR-0032](adr/0032-idempotencia-por-chave-e-referencia-externa.md) |
| Pico de consulta não degrada o registro de movimentações | [ADR-0006](adr/0006-cqrs-com-modelo-de-leitura-separado.md), [ADR-0021](adr/0021-resiliencia-sob-carga-e-falhas.md) |
| Os consumidores são sistemas internos do banco, não o cliente final | [ADR-0012](adr/0012-autenticacao-oauth2-client-credentials.md) |

Visão geral do raciocínio: [mapa de decisões](mapa-de-decisoes.md). Requisitos: [PRD](prd.md).

## Critério de sucesso

O avaliador clona o repositório, sobe a aplicação com um comando, executa os testes, lê o README
e os ADRs e conclui que as decisões foram tomadas com critério de arquiteto.
