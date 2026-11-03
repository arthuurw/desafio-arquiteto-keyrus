# Modelo de falhas

Como a solução se comporta quando uma dependência cai, fica lenta ou recebe carga concorrente: o que
o cliente vê, o que acontece com os dados e qual teste prova. Escrito a partir do código; se os dois
divergirem, o código prevalece. O que foi decidido e ainda não está no código aparece como tal.

## Cenários

Os erros seguem Problem Details (RFC 9457), e o cliente trata pelo `type`
([ADR-0042](../adr/0042-design-da-api-com-trilha-de-auditoria.md)). "Chave não consumida" quer dizer que a
repetição com o mesmo `Idempotency-Key` executa de novo, em vez de devolver a recusa guardada.

| Cenário | Comportamento observado | Dados | Mecanismo | ADR | Prova |
|---------|-------------------------|-------|-----------|-----|-------|
| PostgreSQL fora, para a Api | Escritas e consultas respondem `500 internal-error`, com `traceId` e sem detalhe da exceção. `/health/live` segue `200` | Nada gravado: a transação não abre | Nenhuma rota trata a falha do driver; o `UseExceptionHandler` a converte. A consulta lê a versão no PostgreSQL antes do MongoDB, então também falha | [0004](../adr/0004-postgresql-como-banco-de-dados.md), [0021](../adr/0021-resiliencia-sob-carga-e-falhas.md), [0042](../adr/0042-design-da-api-com-trilha-de-auditoria.md) | [ErrorResponseTests.cs](../../tests/IntegrationTests/Hosts/ErrorResponseTests.cs), [LivenessTests.cs](../../tests/IntegrationTests/Hosts/LivenessTests.cs) |
| MongoDB fora ou lento, na consulta | Saldo e extrato respondem `200` com os mesmos valores, pelo PostgreSQL. A espera pelo MongoDB para em 300 ms | Nenhuma mudança | `FreshRead`: o MongoDB só responde quando a versão projetada é a versão lida no PostgreSQL; erro, versão diferente ou prazo vencido caem no Ledger. Erro e prazo vencido logam `Warning`; atraso, só `Debug` | [0006](../adr/0006-cqrs-com-modelo-de-leitura-separado.md), [0045](../adr/0045-frescor-e-instante-passado-imutavel-na-leitura.md) | [BalanceTests.cs](../../tests/IntegrationTests/Positions/BalanceTests.cs) |
| MongoDB fora, para o Worker | Escritas e consultas da Api seguem normais. O relay registra a falha e tenta de novo; o Worker segue vivo | Eventos ficam pendentes no outbox e são projetados quando o MongoDB volta | Falha do consumidor desfaz o lote inteiro; nova tentativa depois de 1 s, 2 s, 4 s... até 30 s. A projeção espera o MongoDB até 5 s | [0053](../adr/0053-relay-do-outbox-e-projecao-pela-proxima-versao.md) | [ProjectionTests.cs](../../tests/IntegrationTests/Positions/ProjectionTests.cs) |
| MongoDB recusa um evento | Os outros eventos do lote são aplicados. A conta afetada passa a ser lida pelo PostgreSQL | O evento recusado fica pendente, com log `Error`; os seguintes da mesma conta esperam por ele | `MongoWriteException` com erro de escrita vira `Rejected`; só falha de conexão desfaz o lote. A versão projetada para, e a checagem de versão desvia a leitura | [0053](../adr/0053-relay-do-outbox-e-projecao-pela-proxima-versao.md), [0045](../adr/0045-frescor-e-instante-passado-imutavel-na-leitura.md) | [ProjectionTests.cs](../../tests/IntegrationTests/Positions/ProjectionTests.cs) |
| Worker parado | A Api grava e responde normalmente. Consultas trazem o saldo e o extrato atuais, pelo PostgreSQL | O outbox acumula eventos pendentes; nada se perde | Api e Worker são processos separados. A versão da conta no PostgreSQL mostra que o MongoDB está atrasado | [0009](../adr/0009-processos-api-e-worker-separados.md), [0045](../adr/0045-frescor-e-instante-passado-imutavel-na-leitura.md) | [BalanceTests.cs](../../tests/IntegrationTests/Positions/BalanceTests.cs) |
| Worker cai no meio de um lote | Nenhum efeito para o cliente | A marcação do lote é desfeita; o evento volta a ser entregue e não duplica nada no MongoDB | Projeção pela próxima versão estrita: sequência menor ou igual à projetada é "já aplicada". O lançamento é gravado antes da versão, por upsert. No encerramento normal, o lote em andamento termina antes de o Worker sair | [0053](../adr/0053-relay-do-outbox-e-projecao-pela-proxima-versao.md), [0021](../adr/0021-resiliencia-sob-carga-e-falhas.md) | [ProjectionTests.cs](../../tests/IntegrationTests/Positions/ProjectionTests.cs), [OutboxShutdownTests.cs](../../tests/IntegrationTests/Positions/OutboxShutdownTests.cs) |
| Dois Workers ao mesmo tempo | Nenhum efeito para o cliente | Cada lançamento é projetado uma vez | Reserva com `FOR UPDATE SKIP LOCKED` e projeção pela próxima versão estrita | [0053](../adr/0053-relay-do-outbox-e-projecao-pela-proxima-versao.md) | [ProjectionConcurrencyTests.cs](../../tests/IntegrationTests/Positions/ProjectionConcurrencyTests.cs) |
| Queda do processo entre a escrita e o outbox | O cliente não recebe resposta e repete com a mesma chave | Tudo ou nada: lançamento, saldo, evento do outbox, chave de idempotência e registro de auditoria confirmam na mesma transação | `IdempotentExecution` abre a transação; `EntryWrites` grava o evento antes do commit | [0037](../adr/0037-idempotencia-por-modulo-com-hash-sem-dado-pessoal.md), [0053](../adr/0053-relay-do-outbox-e-projecao-pela-proxima-versao.md), [0041](../adr/0041-trilha-de-auditoria-por-modulo.md) | [OutboxTests.cs](../../tests/IntegrationTests/Ledger/OutboxTests.cs) |
| OpenBao fora, lento ou selado | Cadastro e leitura de dados pessoais respondem `503 personal-data-unavailable` com `Retry-After: 5`. Cada chamada ao cofre tem 1 s de prazo. Ledger e saldo não dependem do cofre | Nada gravado; chave não consumida. Leitura com a DEK no cache (5 min) continua atendida | `OpenBaoClient` converte falha de rede, timeout e erro do servidor em indisponível; a resposta é transitória e desfaz a transação | [0050](../adr/0050-criptografia-de-dados-pessoais-com-seed-de-desenvolvimento.md), [0021](../adr/0021-resiliencia-sob-carga-e-falhas.md), [0037](../adr/0037-idempotencia-por-modulo-com-hash-sem-dado-pessoal.md) | [VaultOutageTests.cs](../../tests/IntegrationTests/Customers/VaultOutageTests.cs), [DekCacheTests.cs](../../tests/IntegrationTests/Customers/DekCacheTests.cs) |
| Keycloak fora | A Api sobe sem ele. Nenhum token novo é emitido. Esperado, sem teste: tokens já emitidos seguem aceitos até expirar (5 min) enquanto as chaves de assinatura estão em cache | Nenhuma mudança | A Api valida o JWT localmente (RS256, `iss`, `aud`, `exp`). Metadados e JWKS são lidos do provedor e guardados em cache pelo `JwtBearer`, com os padrões da biblioteca; o código não os altera | [0012](../adr/0012-autenticacao-oauth2-client-credentials.md) | [LivenessTests.cs](../../tests/IntegrationTests/Hosts/LivenessTests.cs) (só a subida) |
| Disputa pelo lock de uma conta | A escrita que espera mais de 2 s recebe `409 concurrency-timeout`. A consulta de instante passado (`at`, `to`), que espera a escrita em voo, também. A consulta do saldo atual não espera o lock | Nada gravado; chave não consumida | `SET LOCAL lock_timeout = '2s'` em toda transação de escrita do Ledger; `SELECT ... FOR UPDATE` só na conta da operação, então outras contas não esperam | [0005](../adr/0005-controle-de-concorrencia-por-lock-pessimista.md), [0021](../adr/0021-resiliencia-sob-carga-e-falhas.md), [0045](../adr/0045-frescor-e-instante-passado-imutavel-na-leitura.md) | [DebitConcurrencyTests.cs](../../tests/IntegrationTests/Ledger/DebitConcurrencyTests.cs), [IdempotencyConcurrencyTests.cs](../../tests/IntegrationTests/Ledger/IdempotencyConcurrencyTests.cs), [PastInstantTests.cs](../../tests/IntegrationTests/Positions/PastInstantTests.cs) |
| Cliente repete a requisição | Mesma chave e mesmo conteúdo: a resposta original, com `Idempotent-Replayed`. Mesma chave com outro conteúdo: `422 idempotency-key-reused`. Outra chave com a mesma referência externa: `409 duplicate-external-reference` | Um único lançamento. Repetições simultâneas esperam a primeira e recebem a resposta dela | Chave por client, registrada na transação da operação; o `INSERT` da mesma chave espera o desfecho da transação aberta. Índice único em `external_reference` por client | [0037](../adr/0037-idempotencia-por-modulo-com-hash-sem-dado-pessoal.md) | [IdempotentReplayTests.cs](../../tests/IntegrationTests/Ledger/IdempotentReplayTests.cs), [IdempotencyConcurrencyTests.cs](../../tests/IntegrationTests/Ledger/IdempotencyConcurrencyTests.cs), [ExternalReferenceTests.cs](../../tests/IntegrationTests/Ledger/ExternalReferenceTests.cs) |
| Débitos simultâneos além do limite | Os que cabem no saldo mais o limite recebem `201`; os demais, `422 insufficient-funds` | O saldo nunca passa do limite de crédito; versão e lançamentos batem com os aceitos | Lock da conta serializa as escritas; o agregado decide com o saldo já travado | [0005](../adr/0005-controle-de-concorrencia-por-lock-pessimista.md), [0048](../adr/0048-limite-de-credito-e-teto-tecnico-do-saldo.md) | [DebitConcurrencyTests.cs](../../tests/IntegrationTests/Ledger/DebitConcurrencyTests.cs) |
| Corpo acima de 16 KB | `413 payload-too-large`, antes de ler o JSON, inclusive com corpo `chunked` | Nada gravado; chave não consumida; a recusa vai para a trilha de auditoria | Middleware `UseRequestBodyLimit`, depois da autorização | [0047](../adr/0047-limite-de-16-kb-no-corpo-da-requisicao.md) | [Ledger/BodyLimitTests.cs](../../tests/IntegrationTests/Ledger/BodyLimitTests.cs), [Customers/BodyLimitTests.cs](../../tests/IntegrationTests/Customers/BodyLimitTests.cs) |

Comandos não fazem retry interno. Quem repete é o chamador, com o mesmo `Idempotency-Key`, o que é
seguro em todos os cenários acima.

## Sob alta demanda

O que existe no código:

- **Leitura separada da escrita.** Saldo e extrato vêm do MongoDB quando ele está em dia
  ([ADR-0006](../adr/0006-cqrs-com-modelo-de-leitura-separado.md)). A consulta do saldo atual não
  espera o lock da conta.
- **Lock só da conta da operação.** Escritas em contas diferentes não disputam entre si
  ([ADR-0005](../adr/0005-controle-de-concorrencia-por-lock-pessimista.md)).
- **Espera limitada pelo lock.** `lock_timeout` de 2 s: a requisição falha rápido com `409` em vez
  de prender a conexão.
- **Fallback da leitura com prazo.** O MongoDB tem 300 ms para responder; depois, a consulta segue
  pelo PostgreSQL sem esperar a leitura abandonada
  ([ADR-0045](../adr/0045-frescor-e-instante-passado-imutavel-na-leitura.md)).
- **Prazo no cofre.** O OpenBao tem 1 s; acima disso, `503` imediato.
- **Corpo limitado.** Nenhuma requisição aloca mais de 16 KB de corpo
  ([ADR-0047](../adr/0047-limite-de-16-kb-no-corpo-da-requisicao.md)).
- **Projeção em lotes.** O relay entrega até 100 eventos por lote e aceita mais de um Worker
  ([ADR-0053](../adr/0053-relay-do-outbox-e-projecao-pela-proxima-versao.md)).

Riscos aceitos pelo [ADR-0054](../adr/0054-escopo-final-da-entrega.md), decididos no
[ADR-0021](../adr/0021-resiliencia-sob-carga-e-falhas.md) e não implementados:

- **Sem bulkhead entre leitura e escrita.** Um pico de consultas disputa conexões e threads com as
  escritas.
- **Sem rate limiting por client** e **sem load shedding**: não há `429` nem `503` por sobrecarga.
  Um client defeituoso pode ocupar a Api inteira.
- **Sem circuit breaker** no MongoDB e no OpenBao: cada chamada paga o prazo inteiro enquanto a
  dependência está fora.
- **Sem pools de conexão separados** para escrita e leitura no PostgreSQL.
- **Sem timeout da requisição inteira (5 s).** Uma requisição lenta segura a conexão até o fim.
- **Sem encerramento gracioso da Api.** O Worker termina o lote em andamento; a Api não tem
  tratamento próprio.

Previsto na F7, ainda não implementado:

- **Readiness.** Api e Worker expõem só `/health/live`, que não consulta dependências. Com o
  PostgreSQL fora, a Api segue recebendo tráfego e responde `500`.
- **`statement_timeout` de 3 s.** Uma consulta lenta no PostgreSQL espera o quanto o driver deixar.

Os valores existentes (2 s, 300 ms, 1 s, 16 KB, lotes de 100) são os iniciais do ADR-0021, sem
calibração por teste de carga.

## O que não é coberto

- Proteção da entrada, circuit breakers, pools separados e timeout da requisição inteira:
  [ADR-0021](../adr/0021-resiliencia-sob-carga-e-falhas.md).
- Métricas, rastreamento e alertas. Hoje a degradação silenciosa (fallback para o PostgreSQL,
  espera por lock, eventos pendentes) só aparece nos logs:
  [ADR-0022](../adr/0022-observabilidade-com-opentelemetry.md).
- Teste de carga e recalibração dos parâmetros: [ADR-0054](../adr/0054-escopo-final-da-entrega.md).
- O PostgreSQL é ponto único: com ele fora, a Api não registra nem consulta, e não há réplica nem
  failover nesta entrega.
- Contenção em contas quentes (sub-ledgers), escala da escrita (sharding), cofre em alta
  disponibilidade e reconciliação periódica dos saldos: [melhorias futuras](../melhorias-futuras.md).
