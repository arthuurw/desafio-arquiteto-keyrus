# ADR-0002: Modelo temporal baseado em tempo de registro

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

O sistema precisa informar o saldo de um cliente **em um determinado momento**. É preciso definir
o que "momento" significa: o instante em que o lançamento foi registrado no sistema, ou uma data de
competência (data-valor) informada no lançamento, que pode ser retroativa.

A escolha afeta a imutabilidade do histórico, a viabilidade de *snapshots* de saldo e a
complexidade da consulta.

## Alternativas consideradas

1. **Somente tempo de registro.** Cada lançamento recebe um timestamp atribuído pelo servidor no
   momento da gravação. Sem lançamento retroativo. "Saldo em T" é a soma dos lançamentos com
   `recorded_at <= T`.
   - Prós: passado imutável; snapshots nunca são invalidados; consulta simples; mesmo modelo do
     extrato bancário.
   - Contras: não representa diretamente operações com data-valor diferente da data de registro.
2. **Bitemporal (tempo de registro + data de competência).** Permite lançamentos retroativos.
   - Prós: fiel a cenários como ajustes contábeis e operações agendadas.
   - Contras: lançamento retroativo invalida snapshots posteriores e exige recálculo; consulta passa
     a ter dois eixos ("saldo em T segundo o conhecimento em T'"); aumenta muito a complexidade.
3. **Alternativa 1 implementada, alternativa 2 documentada como evolução.**

## Decisão

**Alternativa 3**. O sistema usa apenas tempo de registro, atribuído pelo servidor
(relógio do banco de dados, não do cliente). Correções são feitas por estorno com timestamp do
presente. O modelo bitemporal fica registrado como evolução possível.

## Consequências

- **Positivas:**
  - Histórico imutável permite snapshots/checkpoints de saldo e cache de saldos históricos sem
    invalidação.
  - Ordenação dos lançamentos não depende de relógio de cliente.
- **Negativas:**
  - Operações com data-valor diferente da data de registro não são representadas nativamente.
- **Em aberto:**
  - Precisão e fonte do timestamp (ex.: `timestamptz` do banco) a definir junto com a escolha de
    persistência.
