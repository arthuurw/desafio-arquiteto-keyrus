# ADR-0009: Processos separados para API e Worker

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

O padrão outbox (ADR-0006) exige um processo que leia os eventos pendentes e projete o modelo de
leitura no MongoDB. É preciso decidir se esse trabalho roda dentro do processo da API ou em um
processo próprio.

O enunciado exige que o sistema continue operando de forma confiável sob falhas parciais.

## Alternativas consideradas

1. **Processo único: API com `BackgroundService` interno.**
   - Prós: um container a menos; implantação mais simples.
   - Contras: falha do projetor (travamento, vazamento de memória, exceção não tratada) afeta a
     API; escalar a API para N réplicas sobe N projetores, acoplando a escala de duas cargas de
     natureza diferente; implantar uma correção no projetor exige reiniciar a API.
2. **Dois processos: `Api` e `Worker`.**
   - Prós: isolamento de falha; escala independente; implantação independente.
   - Contras: um container a mais; múltiplas réplicas do Worker exigem coordenação na leitura do
     outbox.

## Decisão

**Alternativa 2.** Dois executáveis, cada um com seu container:

- **Api:** recebe comandos e consultas via HTTP.
- **Worker:** lê o outbox e atualiza o modelo de leitura.

O código de domínio e de infraestrutura é compartilhado via bibliotecas dos módulos; os
executáveis são apenas *composition roots* (configuração e ligação dos módulos), sem regra de
negócio.

Coordenação entre réplicas do Worker: leitura do outbox com `FOR UPDATE SKIP LOCKED`, permitindo
que várias réplicas processem lotes distintos sem duplicar trabalho, preservando a ordem por conta.

## Consequências

- **Positivas:**
  - Se o Worker cair, a API continua registrando operações; o outbox acumula eventos e as
    consultas são servidas corretamente pelo fallback (ADR-0007). Ao voltar, o Worker processa o
    acúmulo.
  - API escala por tráfego HTTP; Worker escala por volume do outbox.
- **Negativas:**
  - Mais um serviço para monitorar (health check, métricas de atraso do outbox).
  - A garantia de ordem por conta com múltiplas réplicas exige que eventos de uma mesma conta não
    sejam processados em paralelo; o desenho do lote precisa respeitar isso.
