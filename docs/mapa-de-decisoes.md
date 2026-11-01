# Mapa de decisões

Visão geral de como o raciocínio foi organizado: do problema às decisões, e como cada decisão
abriu ou restringiu as seguintes. Os detalhes de cada ponto estão nos [ADRs](adr/README.md).

## Princípios que guiaram as escolhas

Em ordem de prioridade, quando entram em conflito:

1. **Correção antes de tudo.** Dinheiro não admite inconsistência: nenhum débito acima do limite,
   nenhum lançamento perdido ou duplicado, nenhuma consulta que omita uma operação confirmada.
2. **Segurança como requisito, não como camada.** Autenticação, autorização, idempotência,
   criptografia e LGPD entram no desenho desde o início.
3. **Uma única fonte da verdade.** O PostgreSQL do Ledger. Todo o resto é derivado e verificável.
4. **Falhas contidas.** Componente secundário (modelo de leitura, projetor) nunca derruba o
   principal (registro de movimentações).
5. **Desempenho por desenho, não por força bruta.** Consultas com custo independente do tamanho do
   histórico.
6. **Demonstrar o essencial, documentar a evolução.** O escopo implementado cobre os riscos
   centrais; o caminho para produção fica registrado em [melhorias futuras](melhorias-futuras.md).

## Mapa mental

```mermaid
mindmap
  root((Ledger bancário))
    Problema
      Lentidão no pico
      Instabilidade
      Inconsistência inaceitável
      Saldo em um momento
    Domínio
      Ledger append-only
      Dinheiro em decimal, VO Money
        ADR-0018
      Limite por conta, padrão 0
        ADR-0001
      Tempo de registro do servidor
        ADR-0002
      Bounded contexts
        Ledger core
        Positions leitura
        Customers PII
        ADR-0031
    Consistência
      Saldo acumulado por lançamento
        ADR-0003
      Lock pessimista na conta
        ADR-0005
      Ordenação temporal monotônica
        ADR-0005
      Outbox sem dual write
        ADR-0006
      Relay por polling, projeção pela próxima versão
        ADR-0035
      Resiliência: bulkhead, load shedding, timeouts
        ADR-0021
      Observabilidade: OpenTelemetry, Grafana LGTM
        ADR-0022
      Frescor garantido na leitura
        ADR-0007
    Arquitetura
      Monólito modular
      Vertical slices
      DDD tático
        ADR-0031
      Api e Worker separados
        ADR-0009
      CQRS com Mediator, validação no endpoint
        ADR-0030
      Facade de comandos por módulo
        ADR-0036
      .NET 10, Minimal APIs, EF Core, Migrator
        ADR-0019
      API REST v1, Problem Details, cursor
        ADR-0042
      Código em inglês, docs em PT-BR
        ADR-0011
    Persistência
      PostgreSQL escrita
        ADR-0004
      MongoDB leitura
        ADR-0006
    Segurança
      OAuth2 client credentials
        ADR-0012
      Escopos e titularidade, anti-BOLA
        ADR-0013
      Idempotency-Key, referência externa, limpeza em lotes
        ADR-0037
      Schema e role por módulo, UPDATE por coluna, trilha só INSERT
        ADR-0043
      Trilha de auditoria por módulo, na transação da escrita
        ADR-0041
      AES-GCM, DEK por cliente no OpenBao
        ADR-0016
      Crypto-shredding
        ADR-0016
      LGPD
        Sem PII fora de Customers
        Logs com redação automática
        Bloqueio durante guarda legal
        ADR-0017
    Qualidade
      Testes contra infra real, mutação, carga
        ADR-0023
      Spec-driven, código como fonte da verdade
        ADR-0034
      Fluxo de cada rota conferido contra o código
        ADR-0040
      Zero warnings, NuGet Audit, CI, CodeQL
        ADR-0025
    Evolução
      Broker
      Bitemporal
      Sub-ledgers
      Sharding
      mTLS
```

## Encadeamento das decisões

Cada seta indica que a decisão de origem motivou ou restringiu a de destino.

```mermaid
flowchart TD
    P[Problema: lentidão, instabilidade,<br/>dados financeiros sensíveis]

    P --> D1[ADR-0001<br/>Limite por conta]
    P --> D2[ADR-0002<br/>Tempo de registro]
    P -->|exatidão monetária| D18[ADR-0018<br/>Money em decimal]
    P --> D12[ADR-0012<br/>OAuth2 client credentials]

    D2 -->|passado imutável| D3[ADR-0003<br/>Saldo acumulado por lançamento]
    D1 -->|double-spend| D5[ADR-0005<br/>Lock pessimista]
    D3 -->|ordem de recorded_at| D5
    D1 & D3 & D5 -->|requisitos transacionais| D4[ADR-0004<br/>PostgreSQL]

    D4 -->|isolar carga de leitura| D6[ADR-0006<br/>CQRS + outbox + MongoDB]
    D6 -->|consistência eventual| D7[ADR-0007<br/>Frescor com fallback]
    D6 -->|comandos e consultas| D30[ADR-0030<br/>Mediator, validação no endpoint]
    D6 -->|quem processa o outbox| D9[ADR-0009<br/>Api e Worker]
    D7 -->|fallback via contrato| D31[ADR-0031<br/>Monólito modular + DDD]
    D31 -->|nomes| D11[ADR-0011<br/>Idioma]
    D31 -->|slice com endpoint próprio| D19[ADR-0019<br/>Stack .NET]
    D33 -->|Api sem DDL| D19
    D13 -->|escopos crédito e débito separados| D20[ADR-0042<br/>Design da API]
    D32 & D18 --> D20
    P -->|pico derruba o legado| D21[ADR-0021<br/>Resiliência]
    D7 -->|Mongo fora = degradado| D21
    D6 & D7 & D21 -->|degradação silenciosa precisa ser vista| D22[ADR-0022<br/>Observabilidade]
    D5 & D32 & D21 -->|riscos provados sob concorrência e falha reais| D23[ADR-0023<br/>Testes]
    D23 -->|testes falsos detectados por mutação| D34[ADR-0034<br/>Spec-driven, mutações no AGENTS.md]
    P -->|compilar sem warnings| D25[ADR-0025<br/>Build e CI]
    D23 -->|testes no pipeline| D25

    D12 -->|identidade do chamador| D13[ADR-0013<br/>Escopos + titularidade]
    D31 -->|Account referencia CustomerId| D13
    D31 -->|estado por módulo| D33[ADR-0043<br/>Schema e role, UPDATE por coluna, Worker por módulo, trilha só INSERT]
    D13 -->|onde guardar a auditoria| D41[ADR-0041<br/>Trilha de auditoria por módulo]
    D22 -->|auditoria fora do log| D41
    D17 -->|só identificadores, redação nos logs| D41
    D41 -->|INSERT só da role de app, trigger| D33
    D32 -->|registro antes do commit da chave| D41
    D31 -->|PII isolada em Customers| D16[ADR-0016<br/>Criptografia + OpenBao]
    D16 -->|eliminação via crypto-shredding| D17[ADR-0017<br/>LGPD]
    D32 -->|respostas armazenadas sem PII| D17
    P -->|retries de rede| D32[ADR-0037<br/>Idempotência por módulo, hash sem PII]
    D4 -->|UNIQUE na mesma transação| D32
    D30 -->|behavior do Mediator| D32
    D33 -->|DELETE só pela função de limpeza| D32
    D6 -->|entrega do outbox ao modelo de leitura| D35[ADR-0035<br/>Relay por polling + próxima versão estrita]
    D9 -->|relay no Worker| D35
    D33 -->|outbox só por funções SECURITY DEFINER| D35
    D35 -->|versão projetada comparada na leitura| D7
    D30 -->|um mediador por módulo| D36[ADR-0036<br/>Facade de comandos por módulo]
    D16 -->|HMAC do hash da chave no Transit| D32

```


## Perguntas que orientaram cada área

| Área | Pergunta | Resposta curta | ADR |
|------|----------|----------------|-----|
| Domínio | O que acontece quando um débito excede o saldo? | Rejeita além do limite da conta (padrão 0) | 0001 |
| Domínio | O que significa "saldo em um momento"? | Tempo de registro do servidor; sem retroativo | 0002 |
| Domínio | Como representar dinheiro sem erro de arredondamento? | `decimal` + `numeric(19,4)` no VO `Money`; JSON como string | 0018 |
| Desempenho | Como consultar saldo sem somar todo o histórico? | Saldo acumulado em cada lançamento | 0003 |
| Persistência | Onde guardar a fonte da verdade? | PostgreSQL | 0004 |
| Concorrência | Como impedir dois débitos simultâneos de estourar o limite? | Lock pessimista na conta, `CHECK` no banco | 0005 |
| Carga | Como impedir que picos de leitura degradem a escrita? | Modelo de leitura separado via outbox | 0006 |
| Consistência | Como o cliente nunca vê saldo sem uma operação confirmada? | Verificação de versão e fallback | 0007 |
| Consistência | Como o modelo de leitura recebe cada lançamento sem perder nenhum nem aplicar fora de ordem? | Relay do outbox por polling com `SKIP LOCKED` e cursor; projeção só da próxima versão da conta | 0035 |
| Código | Como despachar comandos sem risco de licença, e onde validar a entrada? | `Mediator` com geração de código; validação no endpoint (`LedgerInput`) | 0030 |
| Código | Como cada endpoint despacha para o mediador do próprio módulo? | Facade de comandos por módulo (`ICustomersCommands`, `ILedgerCommands`) sobre o `Mediator` gerado dele | 0036 |
| Resiliência | Falha no projetor pode derrubar a API? | Não: processos separados | 0009 |
| Resiliência | Como um pico de consultas não derruba o registro de movimentações? | Bulkhead leitura/escrita, load shedding, timeouts, pools separados | 0021 |
| Operação | Como enxergar degradação antes de virar incidente? | OpenTelemetry, métricas de negócio, trace atravessando o outbox, alertas como código | 0022 |
| Qualidade | Como provar que cada teste prova o cenário real? | Infra real, concorrência por barreira, mutação com Stryker, carga com k6 | 0023 |
| Processo | Como garantir que todo requisito vira teste e que "pronto" não é autoavaliação? | tlc-spec-lean com `mutations: N` no AGENTS.md, verificador independente, Stryker no CI; código como fonte da verdade | 0034 |
| Qualidade | Como garantir zero warnings e dependências sem vulnerabilidade? | `TreatWarningsAsErrors`, NuGet Audit, CI com CodeQL, actions por hash | 0025 |
| Documentação | O que documentar para outra pessoa entender e evoluir a solução? | ADRs, mapa, guias, modelo de falhas, modelo de ameaças, runbook, C4 | 0026 |
| Documentação | Como mostrar o caminho completo de cada rota sem o diagrama envelhecer? | Fluxo de cada rota e rotina em `docs/arquitetura/`, ligado a código e ADR, conferido contra `HEAD` no Verify | 0040 |
| Escopo | O que implementar no prazo e o que só documentar? | Núcleo completo (F0-F5, F10); operação reduzida (F6-F9) | 0027 |
| Processo | Onde ficam planos, checks e relatórios de verificação? | Em `.specs/`, local e não versionado; o que é permanente fica em `docs/`, e o comportamento vigente no código e nos testes | 0028 |
| Processo | Como tornar as specs e a verificação públicas sem versionar artefatos de trabalho? | PRD versionado; board com épicos (TDD) e user stories; relatório do verificador no épico | 0029 |
| Organização | Como expressar o domínio no código e manter fronteiras? | Monólito modular, vertical slices, DDD; `Money` e `Outcome` no domínio do módulo | 0031 |
| Clareza | Em que idioma escrever? | Código em inglês, documentação em PT-BR | 0011 |
| Tecnologia | Qual stack .NET e como aplicar migrações sem dar DDL à Api? | .NET 10, Minimal APIs, EF Core, `Migrator` dedicado | 0019 |
| Contrato | Como impedir que quem só credita consiga debitar? | Endpoints e escopos separados para crédito, débito e estorno; leitura mascarada e completa de cliente em rotas separadas, com o acesso completo na trilha de auditoria | 0042 |
| Segurança | Como identificar quem chama a API? | OAuth2 client credentials (Keycloak) | 0012 |
| Segurança | Como impedir que um canal acesse a conta de outro cliente? | Token exchange com `customer_id` e verificação de titularidade | 0013 |
| Segurança | Como um retry nunca duplica um lançamento? | `Idempotency-Key` na mesma transação + `UNIQUE` em `external_reference`; limpeza em lotes; hash por HMAC do cofre quando há dado pessoal | 0037 |
| Segurança | Como um módulo comprometido não alcança dados do outro? | Schema e role de banco por módulo; `UPDATE` por coluna; lançamentos sem `UPDATE`/`DELETE`; role de Worker por módulo; trilha de auditoria só com `INSERT` | 0043 |
| Segurança | Como provar quem movimentou uma conta ou acessou o CPF de um cliente? | Trilha de auditoria por módulo no banco, gravada na transação da escrita, só com identificadores e imutável; dado pessoal apagado dos logs pelo framework | 0041 |
| Segurança | Como proteger PII e ainda permitir eliminação, mesmo com backups? | AES-256-GCM com DEK por cliente no OpenBao; destruir a DEK elimina | 0016 |
| Privacidade | Como conciliar eliminação (LGPD) e dever de guarda (regulação financeira)? | Bloqueio imediato, eliminação automática ao fim da guarda | 0017 |
