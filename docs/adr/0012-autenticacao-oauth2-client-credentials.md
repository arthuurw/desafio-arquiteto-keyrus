# ADR-0012: Autenticação serviço a serviço com OAuth 2.0 client credentials

- **Status:** Aceita
- **Data:** 2026-10-29

## Contexto

O sistema é interno ao banco. Seus consumidores são **outros sistemas** (backend do aplicativo,
internet banking, backoffice), não o cliente final diretamente. O usuário final se autentica nos
canais; os canais chamam este sistema. Portanto, o problema é autenticação **serviço a serviço**.

Requisitos:

- Identificar com segurança qual sistema está chamando.
- Credenciais com expiração curta e rotação sem redeploy da API.
- Base para autorização granular por sistema chamador (ADR próprio).
- Rodar localmente sem configuração manual.

## Alternativas consideradas

1. **OAuth 2.0 client credentials com JWT, emitido por um provedor de identidade (Keycloak).**
   - Prós: padrão de mercado; a API apenas valida tokens e nunca manipula segredos de terceiros;
     escopos por cliente; expiração curta nativa; troca do provedor em produção (Entra ID, Auth0,
     Okta) é apenas configuração.
   - Contras: um container adicional (~400 MB, cerca de 15 s para iniciar).
2. **JWT emitido por endpoint de desenvolvimento da própria API** (ou `dotnet user-jwts`).
   Rejeitada: não é um provedor de identidade real; misturar emissor e validador no mesmo serviço
   é um anti-padrão de segurança e não representa o ambiente de produção.
3. **API key por sistema chamador.**
   Rejeitada: sem expiração nativa, sem escopos granulares, rotação manual e propensa a vazamento
   em logs e configurações. Inadequada para sistema financeiro.

## Decisão

**Alternativa 1.**

- **Provedor local:** Keycloak no docker-compose, com realm importado de arquivo JSON versionado
  no repositório (clients, escopos e segredos de desenvolvimento pré-configurados). Sobe com o
  restante do ambiente, sem passos manuais.
- **Fluxo:** client credentials. Cada sistema chamador é um client com seu próprio conjunto de
  escopos.
- **Validação na API** (JWT Bearer):
  - Assinatura assimétrica (RS256) com chaves obtidas do endpoint JWKS do provedor, com cache e
    rotação automática.
  - Validação obrigatória de `iss`, `aud`, `exp` e `nbf`; tolerância de relógio reduzida (30 s).
  - Tokens de vida curta (5 minutos).
  - Requisições sem token válido recebem `401`; nenhum endpoint de negócio é anônimo. Health
    checks e documentação OpenAPI são as únicas exceções, e a documentação fica disponível
    apenas em ambiente de desenvolvimento.
- **HTTPS obrigatório** fora do ambiente de desenvolvimento local.
- **Testes de integração:** tokens emitidos por um emissor de teste com chave própria, sem
  depender do Keycloak, mantendo a mesma validação da produção.
- **Segredos de desenvolvimento** do realm são exclusivos do ambiente local e documentados como
  tal; em produção, segredos ficam em cofre (ex.: Azure Key Vault, HashiCorp Vault).

## Consequências

- **Positivas:**
  - Identidade do chamador disponível em toda requisição (claim `azp`/`client_id`), usada em
    autorização e trilha de auditoria.
  - Ambiente local fiel ao de produção.
- **Negativas:**
  - Dependência de disponibilidade do provedor para emissão de tokens; a validação na API continua
    funcionando com as chaves em cache.
  - Mais um componente no ambiente local.
- **Evolução (produção):**
  - **mTLS** entre serviços (ex.: via service mesh) como defesa em profundidade: um token vazado é
    inútil sem o certificado do cliente.
  - Tokens com vínculo ao certificado (*certificate-bound tokens*, RFC 8705).
- **Em aberto:**
  - Modelo de autorização (escopos, políticas) em ADR próprio.
