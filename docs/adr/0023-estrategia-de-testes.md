# ADR-0023: Estratégia de testes

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

Em um sistema financeiro, um teste que passa sem provar nada é pior do que a ausência de teste:
dá falsa confiança. O requisito é que **cada teste prove o cenário real** que afirma cobrir, e
que nenhum teste seja pulado, enfraquecido ou escrito para passar de qualquer maneira.

Os riscos centrais do desenho (double-spend, duplicidade por retry, leitura defasada, vazamento de
dados pessoais, acesso à conta de outro cliente, falha parcial de dependências) só são provados
contra infraestrutura real e sob concorrência real.

Explicação didática: [guia de testes](../guias/testes-explicados.md).

## Decisão

### Camadas

| Camada | O que prova | Ferramenta |
|--------|-------------|------------|
| Unitários de domínio | `Money`, agregado `Account` (limite, estorno, versão), mascaramento, regras de idempotência | xUnit v3 |
| Baseados em propriedades | Invariantes sob milhares de sequências aleatórias de créditos e débitos: saldo nunca abaixo de `-credit_limit`; cadeia `balance_after` íntegra; `Money` sem perda de precisão | FsCheck |
| Integração | Api real via HTTP (`WebApplicationFactory`) contra PostgreSQL, MongoDB e OpenBao reais, com tokens de um emissor de teste | Testcontainers |
| Concorrência | N débitos simultâneos na mesma conta nunca excedem o limite; N requisições simultâneas com a mesma `Idempotency-Key` geram um único lançamento | Testcontainers, barreira explícita |
| Falha | MongoDB parado: consultas corretas via fallback. Worker interrompido entre aplicar e marcar o evento: nenhuma duplicata. OpenBao fora: Ledger e Positions seguem operando | Testcontainers (parar/pausar containers) |
| Segurança | Conta de outro titular retorna `404`; client sem escopo recebe `403`; `payments-hub` não debita; nenhum CPF, nome ou e-mail nos logs | Testcontainers |
| Arquitetura | Módulos não acessam internos de outros; domínio não depende de infraestrutura; todo endpoint declara política; nenhum tipo com dado pessoal em Ledger, Positions ou eventos | ArchUnitNET |
| Mutação | Testes do domínio do Ledger detectam alterações plausíveis no código | Stryker.NET |
| Carga | Vazão de débitos, latência p99, comportamento do bulkhead sob pico de leitura; resultados registrados em `docs/` | k6 |

### Regras

As regras operacionais ficam em [`AGENTS.md`](../../AGENTS.md), fonte única para quem implementa:
infraestrutura real, efeito observável, asserções incondicionais, concorrência por barreira
explícita, sistema sob teste sempre real, nenhum teste ignorado.

Preferência por **fakes escritos à mão** (ex.: `InMemoryKeyStore`) sobre bibliotecas de mock:
são mais legíveis e não acoplam o teste a detalhes de implementação.

### Bibliotecas e licenças

| Uso | Escolha | Rejeitada | Motivo da rejeição |
|-----|---------|-----------|--------------------|
| Asserções | Shouldly (BSD) | FluentAssertions | Licença comercial a partir da versão 8 (2025) |
| Dublês, quando inevitáveis | NSubstitute (BSD) | Moq | Controvérsia de coleta de dados (SponsorLink, 2023) |
| Testes de arquitetura | ArchUnitNET (Apache 2.0) | NetArchTest | Regras menos expressivas para dependências entre módulos |

### Teste de carga

Script k6 versionado no repositório, executado contra o ambiente local, com resultados (vazão,
latência p50/p95/p99, taxa de `429`/`503`, comportamento das escritas sob pico de leitura)
registrados em documento próprio. Os valores iniciais do ADR-0021 são recalibrados a partir
desses resultados.

## Alternativas consideradas

1. **Banco em memória (EF InMemory, SQLite) nos testes de integração.** Rejeitada: não reproduz
   `SELECT ... FOR UPDATE`, `clock_timestamp()`, constraints nem isolamento do PostgreSQL. Os
   testes passariam sem provar a concorrência, que é o risco central.
2. **Meta de cobertura de linhas como critério de qualidade.** Rejeitada: cobertura mede o código
   executado, não o comportamento verificado. A garantia vem da rastreabilidade critério → teste e
   da mutação (ADR-0024).
3. **Teste de carga apenas documentado.** Rejeitada: o enunciado exige operação sob alta demanda,
   e números medidos são evidência; promessas não são.

## Consequências

- **Positivas:**
  - Os riscos centrais são provados contra o mesmo motor de produção, sob concorrência e falha reais.
  - Testes falsos são detectados objetivamente pela mutação.
- **Negativas:**
  - Suíte de integração mais lenta (inicialização de containers); mitigada com containers
    compartilhados por coleção de testes.
  - Requer Docker para executar a suíte completa.
- **Evolução:** mutação estendida aos demais módulos.
