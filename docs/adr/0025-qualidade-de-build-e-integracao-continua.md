# ADR-0025: Qualidade de build e integração contínua

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

Requisito obrigatório: o código deve compilar sem erros e sem warnings. Além disso, em um sistema
financeiro, dependências vulneráveis, segredos versionados e regressões não detectadas são riscos
de segurança, não apenas de qualidade.

Explicação didática: [guia de qualidade e CI](../guias/qualidade-e-ci-explicados.md).

## Decisão

### Build

Configuração centralizada em `Directory.Build.props`, aplicada a todos os projetos:

| Configuração | Efeito |
|--------------|--------|
| `TreatWarningsAsErrors=true` | Qualquer warning interrompe o build; é impossível entregar com warnings |
| `Nullable=enable` | O compilador sinaliza possíveis referências nulas |
| `AnalysisLevel=latest-recommended` | Analyzers nativos do .NET (qualidade, segurança, desempenho) |
| `EnforceCodeStyleInBuild=true` + `.editorconfig` | Estilo de código verificado no build |
| `NuGetAudit` com `NuGetAuditLevel=low` | Pacote com vulnerabilidade conhecida, de qualquer severidade, gera warning e, portanto, erro |

- **Central Package Management** (`Directory.Packages.props`): versões de todos os pacotes em um
  único arquivo.
- **`global.json`** fixando o SDK 10.0.x, garantindo a mesma versão de compilador em qualquer
  máquina.
- Supressões de warning são pontuais, no menor escopo possível, sempre com justificativa no
  próprio código. Supressão global é proibida.

### Integração contínua (GitHub Actions)

A cada push e pull request:

1. Restauração e build em Release, com zero warnings.
2. Testes unitários, de arquitetura e de integração (Docker disponível nos runners hospedados).
3. Falha se algum teste for ignorado.
4. Stryker.NET no domínio do Ledger, com limite mínimo de mutantes mortos.

O teste de carga (k6) roda localmente, com resultados registrados no repositório (ADR-0023).

### Cadeia de suprimentos

| Mecanismo | Função |
|-----------|--------|
| CodeQL | Análise estática de segurança do código C# |
| Dependabot | Pull requests de atualização de pacotes NuGet e de actions |
| Secret scanning e push protection do GitHub | Bloqueiam commits com segredos |
| Actions fixadas por hash de commit | Uma tag pode ser movida para código malicioso; um hash, não |

Os segredos de desenvolvimento local (realm do Keycloak, chave de unseal do OpenBao) são
versionados de propósito, identificados como exclusivos do ambiente local e documentados como tal.

## Alternativas consideradas

1. **Warnings permitidos com revisão manual.** Rejeitada: viola o requisito e depende de disciplina.
2. **Analyzers de terceiros (ex.: SonarAnalyzer).** Rejeitada: os analyzers nativos cobrem as
   regras relevantes sem dependência adicional nem análise de licença.
3. **Versões de pacote em cada projeto.** Rejeitada: permite divergência de versões entre projetos.

## Consequências

- **Positivas:**
  - Zero warnings garantido mecanicamente, não por revisão.
  - Vulnerabilidades conhecidas em dependências impedem o build.
  - Qualidade verificada de forma idêntica em qualquer máquina e no CI.
- **Negativas:**
  - Uma vulnerabilidade divulgada em um pacote pode quebrar o build de um dia para o outro, exigindo
    atualização imediata. Comportamento desejado em sistema financeiro.
  - Analyzers rigorosos exigem atenção a cada warning novo de versões futuras do SDK.
