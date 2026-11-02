# ADR-0045: Garantia de frescor na leitura, com o instante passado esperando a escrita em voo

- **Status:** Aceita
- **Data:** 2026-11-02
- **Substitui:** ADR-0007

## Contexto

Com o modelo de leitura alimentado de forma assíncrona (ADR-0006), há uma janela em que o
MongoDB não reflete lançamentos já confirmados no PostgreSQL. O MongoDB também pode ficar
indisponível ou atrasado por falha no processo de projeção.

Requisito de negócio: **nenhuma consulta pode exibir saldo ou extrato que não reflita uma
operação já efetuada.** Para um cliente, ver um débito confirmado que "não aparece" no saldo é
uma inconsistência inaceitável, mesmo que momentânea.

O ADR-0007 (substituído pelo ADR-0045) cumpria esse requisito lendo a versão da conta sem lock. Faltava outra garantia, a do
passado imutável (ADR-0002). A escrita carimba o `recorded_at` depois de travar a conta e antes
do commit (ADR-0005). Uma leitura sem lock com `at` (ou `to`) entre o carimbo e o commit responde
sem o lançamento, e a mesma consulta, repetida depois do commit, responde com ele. O instante
já respondido mudou.

## Alternativas consideradas

1. **Aceitar consistência eventual sem verificação.** Rejeitada: viola diretamente o requisito.
2. **Token de consistência fornecido pelo cliente.** O comando retorna `version`; o cliente envia
   `minVersion` na consulta; sem token, a consulta é servida pelo MongoDB sem verificação.
   Rejeitada: a garantia vale apenas para quem possui o token.
3. **Verificação de versão contra o modelo de escrita em toda consulta, com fallback**, sem lock
   (ADR-0007 (substituído pelo ADR-0045)). Cumpre o frescor, mas não o passado imutável.
4. **Horizonte de tempo:** recusar `at` mais recente que o maior tempo possível de uma transação
   de escrita (`transaction_timeout`). Rejeitada: recusaria o saldo de segundos atrás.
5. **Alternativa 3, e a leitura de um instante passado espera a escrita em voo na conta.**

## Decisão

**Alternativa 5.**

Fluxo de toda consulta de saldo ou extrato:

1. Ler a `version`, o saldo e o relógio do banco (`clock_timestamp()`) da conta no PostgreSQL.
   - **Sem instante** (saldo atual, extrato sem `to`): leitura de uma linha por chave primária,
     sem lock. Pelo MVCC, ela não espera o `FOR UPDATE` das escritas (ADR-0005).
   - **Com instante** (`at` no saldo, `to` no extrato): numa transação com
     `SET LOCAL lock_timeout = '2s'`, a leitura toma `FOR KEY SHARE` na conta e só então lê versão,
     saldo e relógio. O `FOR KEY SHARE` espera o `FOR UPDATE` da escrita em voo, e não espera outra
     leitura. Com isso, o relógio lido fica à frente de todo `recorded_at` já carimbado. A escrita
     seguinte espera esse lock e carimba depois dele. Se a espera passa de 2 s, a consulta
     responde `409 concurrency-timeout`, a mesma recusa das escritas.
2. Instante depois do relógio lido é recusado com `400`: o futuro ainda pode receber lançamentos.
3. Ler o documento da conta no MongoDB.
4. Se a versão projetada no MongoDB for **igual** à versão do PostgreSQL, responder pelo MongoDB.
5. Caso contrário (MongoDB atrasado, indisponível ou com erro), responder pelo **PostgreSQL**,
   limitado a `sequence <= version`, com os mesmos valores.

Proteções:

- **Timeout curto** na leitura do MongoDB.
- **Circuit breaker** no acesso ao MongoDB, previsto (ADR-0021): com falhas sucessivas, as
  consultas iriam direto ao PostgreSQL por um período.
- A consulta de um instante passado pode ser servida pelo MongoDB com segurança, porque o
  histórico é imutável (ADR-0002), desde que o MongoDB esteja na versão lida.

## Consequências

- **Positivas:**
  - Garantia universal: nenhuma consulta, por nenhum canal, exibe estado anterior a uma operação
    confirmada.
  - Instante passado imutável: a mesma consulta com `at` ou `to` responde sempre igual.
  - Degradação controlada: com o MongoDB fora do ar, as consultas continuam corretas, apenas com
    mais carga no PostgreSQL.
  - Sem migração: o `ledger_app` já tem `UPDATE` por coluna em `ledger.accounts` (ADR-0044), o que
    basta para o `FOR KEY SHARE`.
- **Negativas:**
  - Toda consulta faz um acesso mínimo ao PostgreSQL; o isolamento de carga é parcial.
  - A consulta com instante pode esperar até 2 s por uma escrita na mesma conta, e uma escrita
    espera a leitura com instante que chegou antes, por uma consulta de uma linha.
  - Em falha prolongada do MongoDB, toda a carga de leitura recai sobre o PostgreSQL. Mitigação em
    produção: réplica de leitura como destino do fallback. A leitura com instante continua no
    primário, porque o lock só existe nele.
- **Observabilidade necessária:**
  - Atraso do outbox (idade do evento pendente mais antigo).
  - Taxa de fallback de consultas para o PostgreSQL.
  - Taxa de `409` nas leituras com instante.
