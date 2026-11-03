# Teste de carga

Uma execução do script k6 contra o ambiente local, com os números medidos. O objetivo, do
[ADR-0023](../adr/0023-estrategia-de-testes.md), é registrar vazão e latência por tipo de operação e
ver se as escritas seguem saudáveis com as leituras em paralelo. O script está em
[`tests/load/ledger.js`](../../tests/load/ledger.js).

## Como rodar

Com a stack de pé (`docker compose up --build -d`) e a readiness respondendo:

```bash
docker compose --profile load run --rm k6
```

O serviço `k6` só sobe com o perfil `load`. O resumo sai no terminal, e o JSON completo vai para
`tests/load/results/summary.json`, fora do controle de versão. O diretório é versionado vazio, e o
k6 roda com o uid 1000 para gravar nele sem deixar arquivo de root no host; no Linux com outro uid,
use `K6_UID=$(id -u) K6_GID=$(id -g)` na frente do comando. Não rode junto com a suíte de testes:
as duas disputam memória e CPU da mesma máquina.

## Ambiente

| Item | Valor |
|------|-------|
| Máquina | 16 núcleos lógicos, 16 GB de RAM, Windows 11 com Docker Desktop (VM com 5,8 GB) |
| Topologia | Tudo no mesmo host: Api, Worker, PostgreSQL, MongoDB, Keycloak, OpenBao e o próprio k6 |
| Versão | `main` com readiness e `statement_timeout` (F7), imagens do `docker-compose.yml` |
| Data | 2026-11-03 |

Com tudo no mesmo host, o gerador de carga disputa CPU com o sistema medido, e não há rede entre
os componentes. Os números servem para comparar uma versão com outra nesta máquina, não para
prever a capacidade de produção.

## Perfil

Cerca de 3 min 30 s, até 30 usuários virtuais (VUs). Antes da carga, o `setup` abre 21 contas para
a cliente `ana` e credita R$ 100.000,00 em cada.

| Cenário | VUs | Operações |
|---------|-----|-----------|
| `writes` | Sobe a 10 em 30 s, fica 2 min 30 s, desce em 30 s | Crédito de R$ 10,00 (`payments-hub`) ou débito de R$ 5,00 (`ana` no modo delegado), cada um com `Idempotency-Key` nova, numa das 20 contas |
| `reads` | Sobe a 15 em 30 s, fica 2 min 30 s, desce em 30 s | Saldo atual (50 %), saldo de 5 s atrás com `at` (25 %) e extrato com `limit=50` (25 %) |
| `hot` | 5 fixos por 2 min 30 s | Créditos e débitos na mesma conta, sem pausa, para disputar o lock dela |

Os tokens duram 5 min. Cada VU guarda o seu e renova quando falta menos de 1 min.

## Resultados

63.746 requisições em 3 min 31 s, **302 por segundo**. O total inclui 105 requisições de preparo,
de readiness e de token; as dos cenários somam 63.641. Nenhum status inesperado, nenhum `409`,
`422` ou `503`. Todos os limiares passaram.

A vazão de cada tipo é a contagem dividida pela janela ativa do seu cenário: 210 s para escritas e
leituras (rampas incluídas) e 150 s para a conta disputada, que começa em 30 s e para em 3 min. A
vazão geral divide o total pela execução inteira, de 211 s.

| Tipo | Requisições | Janela | Por segundo | p50 | p95 | p99 | Máximo |
|------|-------------|--------|-------------|-----|-----|-----|--------|
| Crédito | 4.294 | 210 s | 20,4 | 6,9 ms | 15,7 ms | 110,0 ms | 470,7 ms |
| Débito | 4.197 | 210 s | 20,0 | 6,8 ms | 16,8 ms | 145,8 ms | 454,8 ms |
| Escrita na conta disputada | 29.441 | 150 s | 196,3 | 22,9 ms | 45,1 ms | 66,8 ms | 527,8 ms |
| Saldo atual | 12.753 | 210 s | 60,7 | 2,0 ms | 4,8 ms | 8,8 ms | 34,8 ms |
| Saldo em instante passado | 6.447 | 210 s | 30,7 | 4,7 ms | 12,1 ms | 102,8 ms | 482,4 ms |
| Extrato | 6.509 | 210 s | 31,0 | 3,1 ms | 6,7 ms | 12,0 ms | 24,9 ms |
| **Todas** (com preparo e tokens) | 63.746 | 211 s | 302 | 9,6 ms | 37,9 ms | 60,6 ms | 527,8 ms |

| Limiar | Valor | Resultado |
|--------|-------|-----------|
| Status inesperado | 0 | Passou (0) |
| p95 de crédito e de débito | < 500 ms | Passou |
| p95 da conta disputada | < 2.500 ms | Passou |
| p95 de saldo, saldo em instante passado e extrato | < 300 ms | Passou |

## Observações

- **Escritas com leituras em paralelo.** Com 15 VUs lendo sem parar, crédito e débito ficaram com
  p95 abaixo de 17 ms. A leitura do saldo atual, servida pelo MongoDB, teve p95 de 4,8 ms.
- **Conta disputada sem `409`.** Os 5 VUs na mesma conta fizeram 196 escritas por segundo,
  serializadas pelo lock da conta ([ADR-0005](../adr/0005-controle-de-concorrencia-por-lock-pessimista.md)),
  com p50 de 22,9 ms, três vezes o das escritas espalhadas. A espera ficou longe dos 2 s do
  `lock_timeout`, então nenhuma escrita foi recusada. Para ver o `409` seria preciso muito mais
  concorrência na mesma conta, fora do perfil moderado.
- **Instante passado mais lento na cauda.** O p99 do saldo com `at` (102,8 ms) é bem maior que o do
  saldo atual: a consulta espera a escrita em voo na conta
  ([ADR-0045](../adr/0045-frescor-e-instante-passado-imutavel-na-leitura.md)).
- **Projeção em dia.** Ao fim da execução, os 37.953 eventos do outbox estavam processados, sem
  nenhum pendente. Os logs da Api não registraram fallback por erro ou prazo do MongoDB, nem
  `database-timeout`. O fallback por atraso do MongoDB loga em `Debug` e não foi medido.
- **Recursos.** A VM do Docker tem 5,8 GB. Ao fim, Api e Worker usavam 123 MB e 84 MB, o PostgreSQL
  218 MB, o MongoDB 276 MB e o Keycloak 610 MB.

## De onde vêm os números

- Vazão, latência e limiares vêm do `summary.json` da execução.
- O k6 omite do resumo os contadores que nunca foram incrementados. A ausência de `status_409`,
  `status_422`, `status_503` e `unexpected_status` no `summary.json`, junto com as 63.641
  verificações de status aprovadas, é a evidência de zero.
- A fila do outbox, os logs e a memória foram observados fora do resumo, com a stack ainda de pé:
  - fila: `SELECT count(*) FILTER (WHERE processed_at IS NULL), count(*) FROM ledger.outbox` pelo
    `docker compose exec postgres psql`;
  - logs: `docker compose logs api` e `docker compose logs worker`, filtrando `warn:` e `fail:`;
  - memória: `docker stats --no-stream`.
- Depois da execução registrada, o script passou a contar como inesperada a falha ao obter um token.
  Isso só muda o resultado quando o Keycloak falha, o que não aconteceu nesta execução.

## Limites

- Uma execução, numa máquina, com tudo no mesmo host. Não há medida de variância entre execuções.
- O perfil não satura o sistema: ele mostra o comportamento sob carga moderada, não o limite.
- Não houve execução só com escritas, sem as leituras em paralelo. Sem essa linha de base, que as
  escritas não degradam por causa das leituras aparece só de forma indireta, pelas latências baixas
  das escritas com as leituras rodando.
- Os valores do [ADR-0021](../adr/0021-resiliencia-sob-carga-e-falhas.md) (2 s, 3 s, 300 ms, 1 s)
  continuam os iniciais. A recalibração a partir destes números ficou fora da entrega
  ([ADR-0054](../adr/0054-escopo-final-da-entrega.md)).
- Sem bulkhead, rate limiting e load shedding, o teste não mede `429` nem descarte de carga: esses
  mecanismos não existem nesta entrega.
