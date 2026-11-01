# PRD: Ledger de movimentações e posição consolidada

| Campo | Valor |
|-------|-------|
| Produto | Ledger de movimentações financeiras e posição consolidada de saldo |
| Responsável | Arthur Webster |
| Situação | Aprovado |
| Criado | 2026-10-29 |
| Decisões técnicas | [ADRs](adr/README.md) · [mapa de decisões](mapa-de-decisoes.md) |
| Roteiro | [roadmap](roadmap.md) |

## 1. Problema

O banco possui um sistema interno que registra as movimentações financeiras dos clientes (créditos
e débitos) e informa a posição consolidada de saldo ao longo do tempo. O sistema cresceu sem
planejamento e hoje apresenta:

- **Lentidão**, que piora conforme o histórico de movimentações cresce.
- **Instabilidade em horários de pico**, quando consultas e registros competem pelos mesmos
  recursos.
- **Dificuldade de manutenção**, sem fronteiras claras entre responsabilidades.

Por se tratar de dados financeiros e pessoais, **qualquer inconsistência gera impacto direto ao
cliente e à operação**, e qualquer vazamento gera impacto legal (LGPD) e reputacional.

## 2. Consumidores

O sistema é interno: seus consumidores são outros sistemas do banco, não o cliente final.

| Consumidor | Papel | Exemplos de uso |
|------------|-------|-----------------|
| `channel-app` | Canal (aplicativo, internet banking) agindo em nome do cliente logado | Débitos iniciados pelo cliente, consulta de saldo e extrato da própria conta |
| `payments-hub` | Integração que recebe dinheiro de fora do banco | Créditos de transferências recebidas |
| `backoffice` | Operação interna | Cadastro de cliente, abertura de conta, ajuste de limite, estorno |
| `compliance` | Conformidade e atendimento a titulares | Acesso a dados pessoais completos, pedidos LGPD |

## 3. Objetivos e métricas de sucesso

| Objetivo | Métrica | Meta |
|----------|---------|------|
| Consistência financeira | Débitos aceitos acima do limite; lançamentos duplicados ou perdidos | Zero, inclusive sob concorrência e repetição de requisições |
| Leitura correta | Consultas que omitem uma operação já confirmada | Zero, inclusive com o modelo de leitura atrasado ou indisponível |
| Desempenho independente do histórico | Custo da consulta de saldo em um momento | Constante em relação ao número de lançamentos da conta |
| Estabilidade no pico | Registro de movimentações durante pico de consultas | Escritas não degradam por causa de leituras; medido em teste de carga |
| Proteção de dados | Dados pessoais legíveis fora do módulo de clientes, em logs ou em backups | Zero |

Metas de latência e vazão são estabelecidas pela medição do teste de carga
([ADR-0023](adr/0023-estrategia-de-testes.md)), não arbitradas antes dela.

## 4. Requisitos funcionais

| ID | Requisito | Consumidores |
|----|-----------|--------------|
| RF-01 | Cadastrar cliente com nome, CPF, data de nascimento e e-mail, sem duplicidade de CPF | `backoffice` |
| RF-02 | Consultar cliente: dados pessoais mascarados por padrão; completos apenas para quem tem permissão específica, com auditoria | `backoffice`, `channel-app`, `compliance` |
| RF-03 | Abrir conta para um cliente existente, com limite de crédito padrão 0 | `backoffice` |
| RF-04 | Alterar o limite de crédito de uma conta | `backoffice` |
| RF-05 | Registrar crédito em uma conta | `payments-hub` |
| RF-06 | Registrar débito em uma conta, recusando-o se o saldo resultante ficar abaixo do limite negativo da conta | `channel-app` |
| RF-07 | Estornar um lançamento, por meio de um novo lançamento de sentido oposto | `backoffice` |
| RF-08 | Consultar o saldo atual de uma conta | `channel-app`, `backoffice`, `compliance` |
| RF-09 | Consultar o saldo de uma conta em um momento informado | `channel-app`, `backoffice`, `compliance` |
| RF-10 | Consultar o extrato de uma conta em um intervalo, paginado | `channel-app`, `backoffice`, `compliance` |
| RF-11 | Registrar pedido de eliminação de dados pessoais, com bloqueio durante a guarda legal e eliminação ao fim do prazo | `compliance` |

## 5. Requisitos não funcionais

| ID | Requisito |
|----|-----------|
| RNF-01 | **Consistência:** nenhum débito aceito acima do limite, mesmo com requisições simultâneas na mesma conta; lançamentos imutáveis |
| RNF-02 | **Idempotência:** qualquer requisição de escrita pode ser repetida com segurança, produzindo efeito uma única vez |
| RNF-03 | **Leitura correta:** nenhuma consulta exibe estado anterior a uma operação já confirmada |
| RNF-04 | **Desempenho:** consultas de saldo com custo independente do tamanho do histórico; capacidade medida por teste de carga |
| RNF-05 | **Resiliência:** picos de leitura não degradam escritas; falha de componente secundário não interrompe o registro de movimentações |
| RNF-06 | **Autenticação e autorização:** todo chamador autenticado; privilégio mínimo por consumidor; canal só acessa contas do cliente logado |
| RNF-07 | **Proteção de dados pessoais:** criptografia na aplicação, dados pessoais restritos a um único módulo, ausência de dados pessoais em logs, eliminação possível mesmo com backups |
| RNF-08 | **Observabilidade:** estados de degradação visíveis por métricas, logs e traces |
| RNF-09 | **Auditoria:** operações de escrita e acessos a dados pessoais registrados de forma imutável |
| RNF-10 | **Qualidade:** compilação sem erros e sem warnings; testes automatizados que provam os cenários reais |
| RNF-11 | **Operabilidade local:** ambiente completo sobe com um comando, com documentação clara |

## 6. Fora de escopo

- Interface de usuário final: os consumidores são sistemas.
- Múltiplas moedas: apenas BRL ([ADR-0018](adr/0018-representacao-de-dinheiro.md)).
- Lançamentos com data-valor retroativa ([ADR-0002](adr/0002-modelo-temporal-tempo-de-registro.md)).
- Transferência entre contas como operação atômica única: créditos e débitos são registrados
  individualmente pelos sistemas de origem.
- Infraestrutura de produção (nuvem, alta disponibilidade, recuperação de desastre):
  documentada, não implementada ([melhorias futuras](melhorias-futuras.md)).
- Gestão de consentimento: as bases legais do tratamento não dependem dele
  ([ADR-0017](adr/0017-lgpd-minimizacao-direitos-e-bloqueio.md)).

## 7. Rastreabilidade

| Requisito | Funcionalidades | ADRs principais |
|-----------|-----------------|-----------------|
| RF-01, RF-02 | F5 `customers` | 0016, 0017 |
| RF-03, RF-04 | F2 `ledger-core` (validação do cliente em F5) | 0001, 0031 |
| RF-05, RF-06, RF-07 | F2 `ledger-core` | 0001, 0003, 0005, 0018, 0039 |
| RF-08, RF-09, RF-10 | F4 `positions` | 0003, 0006, 0007, 0039, 0035 |
| RF-11 | F6 `lgpd-and-audit` (escopo reduzido: decidido e documentado, ver [ADR-0027](adr/0027-escopo-de-implementacao.md)) | 0017 |
| RNF-01 | F2 `ledger-core` | 0001, 0005 |
| RNF-02 | F3 `idempotency` | 0037 |
| RNF-03 | F4 `positions` | 0007, 0035 |
| RNF-04 | F2, F4, F9 `load-test` | 0003, 0023 |
| RNF-05 | F4, F7 `resilience` | 0009, 0021 |
| RNF-06 | F1 `identity`, F2 | 0012, 0013, 0039 |
| RNF-07 | F5, F6 | 0016, 0017, 0038 |
| RNF-08 | F8 `observability` | 0022 |
| RNF-09 | F6 | 0013 |
| RNF-10 | F0 `foundation`, todas | 0023, 0025, 0034 |
| RNF-11 | F0, F10 `final-docs` | 0026 |

Cada user story no board indica os requisitos que atende
([ADR-0029](adr/0029-board-do-github-como-versao-publica-das-specs.md)).
