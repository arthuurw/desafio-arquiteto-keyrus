# ADR-0055: Qualidade de build e integração contínua, com a auditoria de pacotes no CI e não na imagem

- **Status:** Aceita
- **Data:** 2026-11-03
- **Substitui:** ADR-0025

## Contexto

Requisito obrigatório: o código deve compilar sem erros e sem warnings. Além disso, em um sistema
financeiro, dependências vulneráveis, segredos versionados e regressões não detectadas são riscos
de segurança, não apenas de qualidade.

O ADR substituído ligou a auditoria de pacotes em todo build e aceitou que uma vulnerabilidade
divulgada quebrasse o build de um dia para o outro. Os Dockerfiles herdam o `Directory.Build.props`.
Por isso, a mesma auditoria rodava no `dotnet publish` de cada imagem. Um alerta novo em qualquer
pacote, mesmo indireto e de severidade baixa, impedia o `docker compose up` de quem só quer subir e
avaliar a solução. Quem atualiza o pacote não é essa pessoa, e o aviso no build da imagem não
chega a quem pode agir.

O mesmo ADR fixou o SDK 10.0.x pelo `global.json`, com `rollForward: latestPatch`. Essa regra
aceita só a faixa de recursos 10.0.3xx, e quem tem o SDK 10.0.1xx ou 10.0.4xx recebe "A compatible
.NET SDK was not found".

Explicação didática: [guia de qualidade e CI](../guias/qualidade-e-ci-explicados.md).

## Alternativas consideradas

1. **Auditoria em todo build, inclusive na imagem.** Rejeitada: a decisão substituída. O alerta
   novo trava quem roda o compose, que não é quem atualiza o pacote.
2. **Na imagem, auditoria como warning sem erro.** Rejeitada: o log do build da imagem não é onde
   alguém lê o alerta, e o CI já barra o pacote vulnerável.
3. **Auditoria no CI e no build local; imagem publicada sem ela.** Adotada.
4. **`rollForward: latestPatch`.** Rejeitada: recusa um SDK 10.0 de outra faixa de recursos, que
   compila o mesmo código.
5. **`rollForward: latestMajor`.** Rejeitada: aceita o SDK 11, que traz analisadores novos e, com
   warning como erro, quebra o build.
6. **`rollForward: latestFeature`.** Adotada.

## Decisão

### Build

Configuração centralizada em `Directory.Build.props`, aplicada a todos os projetos:

| Configuração | Efeito |
|--------------|--------|
| `TreatWarningsAsErrors=true` | Qualquer warning interrompe o build; é impossível entregar com warnings |
| `Nullable=enable` | O compilador sinaliza possíveis referências nulas |
| `AnalysisLevel=latest-recommended` | Analyzers nativos do .NET (qualidade, segurança, desempenho) |
| `EnforceCodeStyleInBuild=true` + `.editorconfig` | Estilo de código verificado no build |
| `NuGetAudit` com `NuGetAuditLevel=low` e `NuGetAuditMode=all` | Pacote com vulnerabilidade conhecida, de qualquer severidade, direto ou indireto, gera warning e, portanto, erro |

- **A auditoria de pacotes vale no CI e no build local.** Os Dockerfiles de `Api`, `Worker` e
  `Migrator` publicam com `-p:NuGetAudit=false`. O script `tests/ci/nuget-audit-scope.sh` confere as
  duas metades no CI.
- **Central Package Management** (`Directory.Packages.props`): versões de todos os pacotes em um
  único arquivo.
- **`global.json`** com `"version": "10.0.300"` e `"rollForward": "latestFeature"`: aceita o
  10.0.300 ou qualquer faixa de recursos mais nova do 10.0, nunca o 11. O compose não depende
  disso, porque a imagem do SDK é fixada por tag e digest.
- Supressões de warning são pontuais, no menor escopo possível, sempre com justificativa no
  próprio código. Supressão global é proibida.

### Integração contínua (GitHub Actions)

A cada push e pull request:

1. Restauração e build em Release, com zero warnings e a auditoria de pacotes.
2. Testes unitários, de arquitetura e de integração (Docker disponível nos runners hospedados).
3. Falha se algum teste for ignorado.
4. Stryker.NET no domínio do Ledger, com limite mínimo de mutantes mortos.

O teste de carga (k6) roda localmente, com resultados registrados no repositório (ADR-0023).

### Cadeia de suprimentos

| Mecanismo | Função |
|-----------|--------|
| CodeQL | Análise estática de segurança do código C#; decidido, fora da entrega (ADR-0054) |
| Dependabot | Pull requests de atualização de pacotes NuGet e de actions |
| Secret scanning e push protection do GitHub | Bloqueiam commits com segredos |
| Actions fixadas por hash de commit | Uma tag pode ser movida para código malicioso; um hash, não |

Os segredos de desenvolvimento local (realm do Keycloak, chave de unseal do OpenBao) são
versionados de propósito, identificados como exclusivos do ambiente local e documentados como tal.

## Consequências

- **Positivas:**
  - Zero warnings garantido mecanicamente, não por revisão.
  - Vulnerabilidades conhecidas em dependências impedem o build no CI e na máquina de quem
    desenvolve.
  - O `docker compose up` não depende do dia em que roda.
  - Qualquer SDK 10.0 a partir do 10.0.300 compila a solução.
- **Negativas:**
  - Uma vulnerabilidade divulgada em um pacote pode quebrar o CI e o build local de um dia para o
    outro, exigindo atualização imediata. Comportamento desejado em sistema financeiro.
  - Uma imagem pode ser publicada com pacote vulnerável divulgado depois do último CI verde. O
    CI do commit seguinte e o Dependabot apontam o pacote.
  - Uma faixa de recursos nova do SDK 10.0 pode trazer analisadores com warnings novos, e o build
    local passa a exigir a correção.
