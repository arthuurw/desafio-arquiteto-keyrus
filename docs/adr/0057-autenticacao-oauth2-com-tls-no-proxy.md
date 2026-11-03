# ADR-0057: Autenticação serviço a serviço com OAuth 2.0 client credentials, com o TLS terminado no proxy

- **Status:** Aceita
- **Data:** 2026-11-03
- **Substitui:** ADR-0012

## Contexto

O sistema é interno ao banco. Seus consumidores são **outros sistemas** (backend do aplicativo,
internet banking, backoffice), não o cliente final diretamente. O usuário final se autentica nos
canais; os canais chamam este sistema. Portanto, o problema é autenticação **serviço a serviço**.

Requisitos:

- Identificar com segurança qual sistema está chamando.
- Credenciais com expiração curta e rotação sem redeploy da API.
- Base para autorização granular por sistema chamador (ADR-0013).
- Rodar localmente sem configuração manual.

O ADR substituído decidiu a autenticação e segue valendo nela. Ele também exigiu HTTPS fora do
ambiente de desenvolvimento local, sem dizer onde o TLS termina. A Api serve http na porta 8080, e
nada no código liga HTTPS, HSTS ou redirecionamento. Quem implanta não sabe se a Api deveria
terminar o TLS ou se isso é papel da infraestrutura na frente dela.

As respostas com dado pessoal ou financeiro também não diziam nada sobre cache. Um proxy ou um
navegador no caminho poderia guardar um saldo, um extrato ou os dados pessoais completos de um
cliente.

## Alternativas consideradas

### Autenticação

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

### Onde termina o TLS

1. **Na própria Api (Kestrel com certificado, `UseHsts`, `UseHttpsRedirection`).** Rejeitada: cada
   réplica carrega e renova o certificado, e o código passa a depender de `X-Forwarded-Proto` atrás
   de qualquer balanceador.
2. **No proxy reverso ou no ingress na frente da Api.** Adotada: o certificado e o HSTS ficam num
   lugar só, e a Api segue igual em qualquer implantação.

### Cache das respostas

1. **`no-store` só nas rotas de dado pessoal.** Rejeitada: saldo e extrato também são sigilosos, e
   uma lista de rotas esquece a próxima.
2. **`Cache-Control: no-store` em toda resposta das rotas `/v1`.** Adotada.

## Decisão

**OAuth 2.0 client credentials com JWT, como no ADR substituído. O TLS termina no proxy ou no
ingress, e toda resposta `/v1` leva `Cache-Control: no-store`.**

### Autenticação

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
- **Testes de integração:** tokens emitidos por um emissor de teste com chave própria, sem
  depender do Keycloak, mantendo a mesma validação da produção.
- **Segredos de desenvolvimento** do realm são exclusivos do ambiente local e documentados como
  tal; em produção, segredos ficam em cofre (ex.: Azure Key Vault, HashiCorp Vault).

### Transporte

- **O TLS termina no proxy reverso ou no ingress** na frente da Api. A Api fala http só na rede
  interna, sem porta exposta fora dela.
- **HSTS é do proxy**, que é quem fala https com o chamador. A Api não envia HSTS nem redireciona
  para https.
- **As conexões que a Api abre exigem https fora de Development:** os metadados do emissor
  (`Identity:MetadataAddress`) e o cofre (`OpenBao:Address`). Com http, a Api não sobe.
- **Toda resposta das rotas `/v1`**, de sucesso ou Problem Details, leva `Cache-Control: no-store`.

## Consequências

- **Positivas:**
  - Identidade do chamador disponível em toda requisição (claim `azp`/`client_id`), usada em
    autorização e trilha de auditoria.
  - Ambiente local fiel ao de produção.
  - Certificado e HSTS num lugar só, sem código de TLS na Api.
  - Nenhum saldo, extrato ou dado pessoal fica em cache de proxy ou de navegador.
- **Negativas:**
  - Dependência de disponibilidade do provedor para emissão de tokens; a validação na API continua
    funcionando com as chaves em cache.
  - Mais um componente no ambiente local.
  - Entre o proxy e a Api o tráfego é http: quem está na rede interna vê o token e o corpo. A
    premissa vai no modelo de ameaças; o mTLS abaixo a remove.
- **Evolução (produção):**
  - **mTLS** entre serviços (ex.: via service mesh) como defesa em profundidade: um token vazado é
    inútil sem o certificado do cliente.
  - Tokens com vínculo ao certificado (*certificate-bound tokens*, RFC 8705).
