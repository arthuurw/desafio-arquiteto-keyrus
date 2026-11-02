# ADR-0041: Trilha de auditoria por módulo, gravada na transação da escrita

- **Status:** Aceita
- **Data:** 2026-11-01

## Contexto

O ADR-0013 decidiu que toda operação de escrita e todo acesso a `customers.pii.read` geram registro
de auditoria com client, usuário final, recurso, operação, resultado e identificador de correlação,
e deixou em aberto o armazenamento e a retenção. O ADR-0022 separou auditoria de observabilidade:
log pode ser amostrado e descartado, auditoria nunca. O ADR-0017 exige que o registro tenha só
identificadores.

Até aqui nenhuma escrita deixava registro de quem a pediu, e o único registro de acesso aos dados
pessoais completos era uma linha de log `Audit`: some com a rotação, pode ser alterada por quem opera
o coletor e não existe quando o log falha. Recusas de autenticação e de escopo não deixavam rastro.

## Alternativas consideradas

### Onde fica a trilha

1. **Schema `audit` com role e conexão próprias.** Isola a trilha, mas a gravação sai da transação
   da escrita: uma escrita confirmada pode ficar sem registro se o processo cair entre as duas.
2. **Módulo `Audit` alimentado por outbox.** Registro com atraso e o maior esforço de construção.
3. **Uma tabela por módulo, no schema dele,** gravada pela role de aplicação do módulo na mesma
   transação da escrita.

### Quando o registro não grava

1. **Mantém a resposta e loga o erro, sempre.** A escrita confirmada ou o dado pessoal entregue
   ficariam sem registro.
2. **Fecha onde há algo a proteger:** a escrita confirmada desfaz junto, e a leitura completa não
   entrega os dados; nas recusas, a resposta já decidida é mantida e o erro vai para o log.

### Retenção

1. **Limpeza por prazo no Worker.** O prazo legal depende do jurídico e ainda não está definido.
2. **Sem exclusão nesta versão:** nenhuma role apaga a trilha.

## Decisão

**Trilha: alternativa 3. Falha: alternativa 2. Retenção: alternativa 2.**

### Tabela

`ledger.audit_events` e `customers.audit_events`, com as mesmas colunas e sem chave estrangeira (a
tentativa recusada aponta para recurso que não existe):

| Coluna | Tipo | Conteúdo |
|--------|------|----------|
| `id` | `uuid` PK | Gerado na Api |
| `occurred_at` | `timestamptz not null` | `clock_timestamp()` do banco |
| `client_id` | `text null` | Client do token; null quando o token não resolve um chamador |
| `caller_customer_id` | `uuid null` | Cliente final, só no modo delegado |
| `operation` | `text not null` | `open-account`, `change-credit-limit`, `credit`, `debit`, `reverse-entry`, `register-customer`, `read-personal-data` |
| `resource_type` | `text not null` | `account`, `entry`, `customer` |
| `resource_id` | `uuid null` | Id da rota, ou do recurso criado; null quando o id não é UUID ou o recurso não chegou a existir |
| `result` | `integer not null` | Status HTTP devolvido |
| `trace_id` | `text not null` | O mesmo `traceId` do corpo de erro e dos logs da requisição |

Índice em `occurred_at`. Nome, CPF, nascimento, e-mail e corpo da requisição nunca entram (ADR-0017).

### Uma linha por requisição auditada

- A rota auditada declara a operação, o tipo de recurso e de onde vem o id. Um middleware entre o
  tratador de exceção e a autenticação abre o registro pendente da requisição; assim ele vê também
  `401` e `403`, decididos antes da rota.
- **Escrita confirmada** (`201`, `204`): o registro entra na transação da escrita, antes do commit.
  Nos comandos com `Idempotency-Key` é o `IdempotentExecution` que o insere (ADR-0037); na troca de
  limite, o handler dela.
- **Leitura completa com `200`:** o registro é inserido depois de ler e decifrar e antes de enviar os
  dados.
- **Demais desfechos** (recusa, validação, repetição idempotente, falha transitória, `401`, `403`,
  `500` da exceção, `499` do cliente que desistiu): o middleware insere o registro no fim da
  requisição, em transação própria, com o status devolvido.
- A repetição idempotente é uma requisição do chamador e gera a própria linha, com o status
  devolvido.
- O estorno registra o lançamento estornado (`entry`), não a conta.

### Falha fechada

- Escrita confirmada: o `INSERT` do registro falhou, a transação desfaz a escrita e a chave de
  idempotência, e a resposta é `500` `internal-error`.
- Leitura completa: o `INSERT` falhou, a resposta é `500` sem nenhum dado pessoal.
- Recusas, `401` e `403`: a resposta já decidida é mantida, e o erro vai para o log só com a operação,
  o status e o `traceId`.

### Imutabilidade e retenção

- A role de aplicação do módulo só tem `INSERT` na tabela, e um trigger recusa `UPDATE`, `DELETE` e
  `TRUNCATE` de qualquer role, inclusive a dona (ADR-0043).
- Sem exclusão nesta versão. O prazo legal de guarda fica para o jurídico, e a limpeza ao fim dele,
  em `docs/melhorias-futuras.md`.
- Ninguém lê a trilha pela Api: a consulta é por SQL, com a role dona do schema.

### Dado pessoal nos logs

O ADR-0017 decidiu que a redação nos logs é do framework. Ela fica assim:

- `PersonalDataAttribute`, em `BuildingBlocks`, é a classificação de dado pessoal
  (`Microsoft.Extensions.Compliance.Redaction`).
- Api e Worker ligam a redação com o `ErasingRedactor` para essa classificação: o valor marcado sai
  do log como texto vazio.
- Teste de arquitetura exige a marca em todo parâmetro de método de log com tipo ou nome de nome,
  CPF, nascimento ou e-mail, e recusa a marca em tipos de Ledger, Positions e dos `Contracts`.

Com isso fica fechado o ponto "Em aberto" do ADR-0013 sobre armazenamento e retenção dos registros.

## Consequências

- **Positivas:**
  - Escrita confirmada nunca fica sem registro, e dado pessoal completo nunca sai sem registro.
  - Tentativas barradas na autenticação ou no escopo também ficam na trilha.
  - A trilha não depende da pilha de observabilidade nem da rotação do log.
  - A redação de dado pessoal nos logs é imposta pelo framework e checada por teste.
- **Negativas:**
  - Duas trilhas, uma por schema; uma visão unificada exigiria cruzar schemas (ADR-0043).
  - Um banco recusando o `INSERT` derruba as escritas e a leitura completa.
  - A tabela só cresce até existir a limpeza por prazo.
- **Em aberto:**
  - Prazo legal de guarda da trilha e a rotina que limpa ao fim dele.
  - Consulta da trilha por role ou rota própria.
