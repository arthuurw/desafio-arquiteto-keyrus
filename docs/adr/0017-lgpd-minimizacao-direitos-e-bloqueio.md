# ADR-0017: LGPD: minimização, direitos do titular e bloqueio durante guarda legal

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

O sistema trata dados pessoais de clientes de uma instituição financeira. Duas obrigações legais
convivem e podem entrar em conflito:

- **LGPD:** minimização, segurança, direitos do titular (acesso, correção, eliminação).
- **Regulação financeira:** dever de guarda de registros de identificação de clientes e de
  operações por prazo determinado após o fim do relacionamento (ex.: Lei 9.613/1998, prevenção à
  lavagem de dinheiro). A própria LGPD (art. 16, I) autoriza a conservação para cumprimento de
  obrigação legal ou regulatória.

A criptografia e a eliminação técnica estão no ADR-0016. Este ADR define as regras de tratamento.

## Decisão

### 1. Minimização

Customers coleta apenas: nome, CPF, data de nascimento e e-mail. Novos campos pessoais exigem
justificativa registrada.

### 2. Dados pessoais confinados ao módulo Customers

- **Ledger e Positions não armazenam nenhum dado pessoal.** Referenciam apenas `CustomerId` e
  `AccountId`, identificadores opacos.
- **Lançamentos não têm campo de texto livre.** Um campo de descrição permitiria gravar dados
  pessoais de terceiros (ex.: nome e CPF da contraparte de um PIX) no Ledger, no MongoDB, nos
  backups e nos logs, onde não há criptografia por cliente nem eliminação. O lançamento possui
  apenas `category` codificada (ex.: `PIX_IN`, `TED_OUT`) e `external_reference`; os dados da
  contraparte permanecem no sistema de origem.
- **Comandos de Customers retornam apenas identificadores.** As respostas são armazenadas pelo
  mecanismo de idempotência (ADR-0014); devolver dados pessoais as copiaria para essa tabela.
- Testes de arquitetura e de contrato verificam que nenhum tipo de Ledger, Positions ou eventos de
  integração contém campos classificados como dado pessoal.

### 3. Logs sem dados pessoais

- Campos pessoais são anotados com classificação de dados e o log usa redação automática
  (`Microsoft.Extensions.Compliance.Redaction`). A proteção é imposta pelo framework, não por
  disciplina.
- Teste automatizado verifica que nenhum log emitido durante os cenários de teste contém CPF,
  nome ou e-mail.
- Registros de auditoria (ADR-0013) contêm apenas identificadores, nunca o dado acessado.

### 4. Direitos do titular

| Direito | Implementação | Quem pode acionar |
|---------|---------------|-------------------|
| Acesso | Exportação dos dados pessoais do cliente | `compliance`, auditado |
| Correção | Atualização dos campos pessoais | `backoffice`, auditado |
| Eliminação | Pedido registrado, bloqueio e eliminação ao fim da guarda legal (abaixo) | `compliance`, auditado |

### 5. Eliminação com bloqueio durante a guarda legal

Fluxo do pedido de eliminação:

1. O pedido é registrado com data e solicitante.
2. Os dados entram em estado **bloqueado** (LGPD, art. 5º, XIII): nenhum client consegue lê-los,
   inclusive `compliance`. Consultas retornam o cliente com dados pessoais indisponíveis.
3. O desbloqueio só é possível por operação específica de ordem legal, com justificativa
   obrigatória e auditoria.
4. Ao fim do prazo de guarda, uma rotina no Worker executa a eliminação por crypto-shredding
   (ADR-0016) e o cliente permanece apenas como marcador.

O prazo de guarda é configurável. Padrão: **5 anos após o encerramento da última conta do
cliente**. O valor definitivo deve ser validado pelo jurídico. No ambiente local o prazo é curto,
para permitir demonstração e testes.

### Fora do escopo

Gestão de consentimento: o tratamento tem como bases legais a execução de contrato e o
cumprimento de obrigação legal (LGPD, art. 7º, II e V), que não dependem de consentimento.

## Alternativas consideradas (eliminação)

1. **Eliminação imediata a pedido.** Rejeitada: viola o dever legal de guarda da instituição
   financeira.
2. **Recusar o pedido enquanto houver obrigação de guarda.** Rejeitada: deixa os dados plenamente
   acessíveis sem necessidade, contrariando a minimização.
3. **Bloqueio imediato e eliminação automática ao fim da guarda.** Adotada.

## Consequências

- **Positivas:**
  - Atende ao titular e à regulação financeira ao mesmo tempo.
  - Dados pessoais concentrados em um único módulo, criptografados e elimináveis.
  - Vazamentos acidentais (logs, descrições, respostas armazenadas) bloqueados por desenho e
    verificados por teste.
- **Negativas:**
  - Extrato sem descrição livre exige que os canais componham a descrição exibida ao cliente a
    partir da categoria e de dados do sistema de origem.
  - Prazo de guarda depende de validação jurídica.
