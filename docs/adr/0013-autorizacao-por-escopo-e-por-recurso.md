# ADR-0013: Autorização por escopo e por recurso com propagação do usuário final

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

A autenticação identifica o sistema chamador (ADR-0012). Resta decidir **o que** cada chamador
pode fazer e **sobre quais recursos**.

O risco central é a autorização em nível de objeto quebrada (*Broken Object Level Authorization*,
BOLA, item 1 do OWASP API Security Top 10): um canal autenticado que, por defeito ou abuso,
acessa ou movimenta a conta de um cliente diferente daquele que está logado. Em um sistema
financeiro, é a vulnerabilidade de maior impacto.

## Alternativas consideradas

1. **Somente escopos.** O canal que autenticou o cliente é considerado confiável para escolher a
   conta. Rejeitada: um defeito no canal permite movimentar a conta de outro cliente; a API não
   tem como detectar.
2. **Escopos e contexto do usuário final propagado via token exchange (RFC 8693).** O canal troca
   o token do cliente logado por um token destinado a esta API, que carrega a identidade do
   cliente. A API verifica a titularidade da conta em cada requisição.
3. **Somente escopos implementados; alternativa 2 documentada.** Rejeitada: segurança é prioridade
   explícita do projeto e BOLA é o risco mais relevante para este tipo de sistema.

## Decisão

**Alternativa 2.**

### Negação por padrão

A política padrão da API exige autenticação, e todo endpoint declara explicitamente sua política
de autorização. Um endpoint novo sem política declarada fica inacessível, e um teste de
arquitetura verifica que todos os endpoints possuem política.

### Escopos

| Escopo | Permite |
|--------|---------|
| `ledger.credits.write` | Registrar créditos |
| `ledger.debits.write` | Registrar débitos |
| `ledger.entries.reverse` | Estornar lançamentos |
| `ledger.accounts.manage` | Abrir conta, alterar limite |
| `positions.read` | Consultar saldo e extrato |
| `customers.write` | Cadastrar e atualizar clientes |
| `customers.read` | Consultar clientes com dados pessoais mascarados |
| `customers.pii.read` | Consultar dados pessoais completos; todo acesso é auditado |
| `customers.erasure.manage` | Registrar pedidos de eliminação e desbloqueios por ordem legal (ADR-0017) |

Crédito, débito e estorno têm escopos e endpoints separados (ADR-0020): um sistema que apenas
recebe dinheiro de fora, como o `payments-hub`, não consegue debitar nenhuma conta.

### Dois modos de acesso

| Modo | Token | Regra de recurso |
|------|-------|------------------|
| **Delegado** (canal agindo em nome do cliente) | Obtido por token exchange; contém a claim `customer_id` do cliente logado | A conta acessada precisa pertencer a `customer_id`; caso contrário, resposta `404` |
| **Serviço** (sistema agindo por conta própria: backoffice, compliance, integrações de crédito) | Client credentials, sem usuário | Apenas escopo; clients deste modo são privilegiados e todo acesso é auditado |

- **Resposta `404`, não `403`,** para conta de outro titular: não revela a existência do recurso,
  impedindo enumeração de contas.
- **Um canal não pode contornar a verificação** usando client credentials: clients de canal não
  recebem escopos do Ledger, Positions ou Customers no próprio service account; só obtêm acesso por
  token exchange com um usuário. Essa configuração faz parte do realm versionado e é coberta por
  teste de integração.
- **Vínculo entre identidade e cliente:** o usuário no provedor de identidade possui o atributo
  `customer_id`, emitido como claim por um *protocol mapper*. O agregado `Account` já referencia
  `CustomerId` (ADR-0010), então a verificação não depende de consulta a outro módulo.

### Clients de exemplo no realm local

| Client | Modo | Escopos |
|--------|------|---------|
| `channel-app` | Delegado | `ledger.debits.write`, `positions.read`, `customers.read` |
| `backoffice` | Serviço | `ledger.accounts.manage`, `ledger.entries.reverse`, `customers.write`, `customers.read`, `positions.read` |
| `compliance` | Serviço | `customers.pii.read`, `customers.erasure.manage`, `positions.read` |
| `payments-hub` | Serviço | `ledger.credits.write` (créditos de origem externa) |

### Auditoria

Toda operação de escrita e todo acesso a `customers.pii.read` gera registro de auditoria com
client, usuário final (se houver), recurso, operação, resultado e identificador de correlação.

## Consequências

- **Positivas:**
  - BOLA mitigado na própria API, independentemente da correção dos canais.
  - Privilégio mínimo por client; dados pessoais completos restritos e auditados.
- **Negativas:**
  - Fluxo de token exchange exige configuração adicional no provedor e torna o ambiente local
    mais complexo; um script de demonstração documenta como obter tokens de cada modo.
  - Usuários de teste no realm precisam de `customer_id` consistente com os dados iniciais do
    banco.
- **Em aberto:**
  - Armazenamento e retenção dos registros de auditoria (junto às decisões de LGPD).
