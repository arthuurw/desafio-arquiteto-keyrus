#!/usr/bin/env bash
# O arquivo docs/api/requests.http exercita a Api como o contrato e o realm local a descrevem.
#   tokens: um token nomeado por client, por client credentials, e o modo delegado da ana (senha no
#           channel-web, depois troca no channel-app para a Api).
#   routes: toda rota do openapi-v1.json tem ao menos uma requisição, nenhuma requisição à Api fica
#           fora do documento e nenhuma vai a um host que não seja `{{api}}` ou `{{keycloak}}`; antes,
#           o script prova que acha uma rota faltando, uma a mais e um host literal plantados.
#   auth:   toda requisição à Api usa o token de um client que tem o escopo da rota no realm, e todo
#           `POST` manda uma `Idempotency-Key` nova, menos o cadastro de exemplo; antes, o script prova
#           que acha uma requisição sem token e uma a um host literal plantadas.
#   register: o cadastro de exemplo manda a chave, o client e o corpo do `curl` do README, para que
#           repetir um ou outro devolva o `201` guardado; antes, prova que acha `{{$guid}}` e outra chave.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
requests="$root/docs/api/requests.http"
contract="$root/docs/api/openapi-v1.json"
realm="$root/docker/keycloak/realm-ledger.json"
mode="${1:-}"

fail() { echo "FAIL: $1" >&2; exit 1; }

[ -f "$requests" ] || fail "docs/api/requests.http does not exist"

# Uma linha por requisição: nome, método, URL, token do `Authorization`, `Idempotency-Key` e corpo, separados por tab;
# campo ausente vira `-`, porque o `read` junta tabs seguidos.
parse() {
    tr -d '\r' <"$1" | awk '
        function or_dash(value) { return value == "" ? "-" : value }
        function flush() {
            if (method != "") printf "%s\t%s\t%s\t%s\t%s\t%s\n", or_dash(name), method, url, or_dash(auth), or_dash(key), or_dash(body)
            name = ""; method = ""; url = ""; auth = ""; key = ""; body = ""; inbody = 0
        }
        /^###/ { flush(); next }
        /^# @name / { name = $3; next }
        /^#/ { next }
        method == "" && /^[A-Z]+ / { method = $1; url = $2; next }
        method == "" { next }
        !inbody && /^$/ { inbody = 1; next }
        !inbody && /^Authorization: Bearer / { auth = $3; next }
        !inbody && /^Idempotency-Key: / { key = $2; next }
        inbody && !/^$/ { body = body $0 }
        END { flush() }'
}

# Valor de um campo do corpo `application/x-www-form-urlencoded`.
field() { tr '&' '\n' <<<"$1" | sed -n "s/^$2=//p" | head -n1; }

# Destino de uma URL. Só `{{api}}` e `{{keycloak}}` valem: uma requisição a um host literal escaparia
# de toda conferência.
host_of() {
    case "$1" in
        "{{api}}"*) echo api ;;
        "{{keycloak}}"*) echo keycloak ;;
        *) echo other ;;
    esac
}

# Rotas do contrato: método, caminho e escopo exigido.
operations() {
    jq -r '.paths | to_entries[] | .key as $path | .value | to_entries[]
        | "\(.key | ascii_upcase) \($path) \(.value.security[0].Bearer[0])"' "$contract" | tr -d '\r'
}

# Caminho do contrato que a URL da requisição alcança, com `{{...}}` e `{id}` casando qualquer segmento.
template_of() {
    local method="$1" path="${2%%\?*}" op_method op_path scope
    path="${path#\{\{api\}\}}"
    while read -r op_method op_path scope; do
        [ "$op_method" = "$method" ] || continue
        local -a want got
        IFS='/' read -ra want <<<"$op_path"
        IFS='/' read -ra got <<<"$path"
        [ "${#want[@]}" -eq "${#got[@]}" ] || continue
        local i matched=1
        for i in "${!want[@]}"; do
            [[ "${want[$i]}" == \{*\} ]] && [ -n "${got[$i]}" ] && continue
            [ "${want[$i]}" = "${got[$i]}" ] || { matched=0; break; }
        done
        [ "$matched" -eq 1 ] && { echo "$op_path $scope"; return 0; }
    done < <(operations)
    return 1
}

tokens() {
    local parsed client name body password exchange
    parsed="$(parse "$requests")"
    for client in backoffice compliance payments-hub; do
        name="$(awk -F'\t' -v c="$client" '$6 ~ "grant_type=client_credentials" && $6 ~ "client_id=" c "(&|$)" { print $1 }' <<<"$parsed" | head -n1)"
        [ -n "$name" ] || fail "requests.http has no named client_credentials token request for $client"
        echo "OK: $client token by client credentials in '$name'"
    done

    password="$(awk -F'\t' '$6 ~ "grant_type=password" && $6 ~ "client_id=channel-web(&|$)" && $6 ~ "username=ana(&|$)" { print $1 }' <<<"$parsed" | head -n1)"
    [ -n "$password" ] || fail "requests.http has no named password token request for ana on channel-web"
    echo "OK: ana signs in on channel-web in '$password'"

    exchange="$(awk -F'\t' '$6 ~ "grant_type=urn:ietf:params:oauth:grant-type:token-exchange" { print $1 "\t" $6 }' <<<"$parsed" | head -n1)"
    name="${exchange%%$'\t'*}"
    body="${exchange#*$'\t'}"
    [ -n "$name" ] || fail "requests.http has no named token-exchange request"
    [ "$(field "$body" client_id)" = "channel-app" ] || fail "token exchange '$name' is not done by channel-app"
    [ "$(field "$body" audience)" = "ledger-api" ] || fail "token exchange '$name' does not ask for audience ledger-api"
    [ "$(field "$body" subject_token)" = "{{$password.response.body.\$.access_token}}" ] \
        || fail "token exchange '$name' does not take the subject_token from '$password'"
    echo "OK: channel-app exchanges the token of '$password' for ledger-api in '$name'"
}

# Diferença entre as rotas do contrato e as do arquivo; imprime cada falta e cada sobra.
route_gaps() {
    local parsed method url found covered=() op_method op_path scope
    parsed="$(parse "$1")"
    while IFS=$'\t' read -r _ method url _ _ _; do
        case "$(host_of "$url")" in
            api) ;;
            keycloak) continue ;;
            *) echo "unknown host: $method $url"; continue ;;
        esac
        found="$(template_of "$method" "$url")" || { echo "extra: $method $url"; continue; }
        covered+=("$method ${found%% *}")
    done <<<"$parsed"
    while read -r op_method op_path scope; do
        printf '%s\n' "${covered[@]}" | grep -qxF -- "$op_method $op_path" || echo "missing: $op_method $op_path"
    done < <(operations)
}

routes() {
    local probe gaps
    probe="$(mktemp -d)"
    trap 'rm -rf "$probe"' RETURN

    # Prova do detector: sem a única requisição de uma rota, ela falta; com uma rota inventada, ela sobra.
    tr -d '\r' <"$requests" | awk '/^###/ { skip = 0 } /\/personal-data$/ { skip = 1 } { lines[NR] = $0; skips[NR] = skip }
        END { start = 0; for (i = 1; i <= NR; i++) { if (lines[i] ~ /^###/) start = i; if (skips[i]) drop[start] = 1 }
              keep = 1; for (i = 1; i <= NR; i++) { if (lines[i] ~ /^###/) keep = !drop[i]; if (keep) print lines[i] } }' \
        >"$probe/missing.http"
    gaps="$(route_gaps "$probe/missing.http")"
    grep -qxF 'missing: GET /v1/customers/{id}/personal-data' <<<"$gaps" || fail "detector missed a planted missing route: $gaps"
    { cat "$requests"; printf '\n### Rota inventada\nGET {{api}}/v1/accounts/{{x}}/history\n'; } >"$probe/extra.http"
    gaps="$(route_gaps "$probe/extra.http")"
    grep -qxF 'extra: GET {{api}}/v1/accounts/{{x}}/history' <<<"$gaps" || fail "detector missed a planted extra route: $gaps"
    { cat "$requests"; printf '\n### Host literal\nGET http://localhost:8080/v1/accounts/x/balance\n'; } >"$probe/literal.http"
    gaps="$(route_gaps "$probe/literal.http")"
    grep -qxF 'unknown host: GET http://localhost:8080/v1/accounts/x/balance' <<<"$gaps" \
        || fail "detector missed a planted literal host: $gaps"
    echo "OK: detector finds a planted missing route, a planted extra route and a planted literal host"

    gaps="$(route_gaps "$requests")"
    [ -z "$gaps" ] || fail "requests.http does not match openapi-v1.json:"$'\n'"$gaps"
    echo "OK: requests.http has every route of openapi-v1.json ($(operations | wc -l | tr -d ' ')) and no other"
}

# Cada requisição do arquivo que não vai à Api com o token e a chave certos; imprime um problema por linha.
auth_problems() {
    local parsed name method url token key body found scope token_body client
    parsed="$(parse "$1")"
    while IFS=$'\t' read -r name method url token key body; do
        case "$(host_of "$url")" in
            api) ;;
            keycloak) continue ;;
            *) echo "$method $url targets neither {{api}} nor {{keycloak}}"; continue ;;
        esac
        found="$(template_of "$method" "$url")" || { echo "$method $url is not a route of openapi-v1.json"; continue; }
        scope="${found#* }"
        [[ "$token" =~ ^\{\{([A-Za-z0-9_]+)\.response\.body\.\$\.access_token\}\}$ ]] \
            || { echo "$method $url does not send Authorization: Bearer {{<token>.response.body.\$.access_token}}"; continue; }
        token_body="$(awk -F'\t' -v n="${BASH_REMATCH[1]}" '$1 == n { print $6 }' <<<"$parsed")"
        [ -n "$token_body" ] || { echo "$method $url uses '${BASH_REMATCH[1]}', which is not a token request"; continue; }
        client="$(field "$token_body" client_id)"
        jq -e --arg c "$client" --arg s "$scope" \
            '.clients[] | select(.clientId == $c) | .defaultClientScopes | index($s)' "$realm" >/dev/null \
            || { echo "$method $url needs $scope, which client $client does not have in the realm"; continue; }
        # O cadastro de exemplo usa a chave fixa do README, conferida pelo modo `register`.
        if [ "$method" = "POST" ] && [ "$key" != '{{$guid}}' ] && [ "$name" != registerCustomer ]; then
            echo "$method $url does not send Idempotency-Key: {{\$guid}}"
        fi
    done <<<"$parsed"
}

auth() {
    local probe problems count
    probe="$(mktemp -d)"
    trap 'rm -rf "$probe"' RETURN

    # Prova do detector: uma requisição à Api sem token e uma a um host literal, plantadas, são achadas.
    { cat "$requests"; printf '\n### Sem token\nGET {{api}}/v1/accounts/x/balance\n'; } >"$probe/no-token.http"
    problems="$(auth_problems "$probe/no-token.http")"
    grep -qF 'GET {{api}}/v1/accounts/x/balance does not send Authorization' <<<"$problems" \
        || fail "detector missed a planted request without a token: $problems"
    { cat "$requests"; printf '\n### Host literal\nGET http://localhost:8080/v1/accounts/x/balance\n'; } >"$probe/literal.http"
    problems="$(auth_problems "$probe/literal.http")"
    grep -qF 'GET http://localhost:8080/v1/accounts/x/balance targets neither' <<<"$problems" \
        || fail "detector missed a planted literal host: $problems"
    echo "OK: detector finds a planted request without a token and a planted literal host"

    problems="$(auth_problems "$requests")"
    [ -z "$problems" ] || fail "requests.http has requests the Api would refuse or the script cannot check:"$'\n'"$problems"
    count="$(parse "$requests" | awk -F'\t' 'index($3, "{{api}}") == 1' | wc -l | tr -d ' ')"
    [ "$count" -gt 0 ] || fail "requests.http has no request to the Api"
    echo "OK: $count Api requests use a token whose client has the route scope, and every POST but registerCustomer sends a new Idempotency-Key"
}

# Chave, client e corpo do cadastro de exemplo no README, uma linha `campo<tab>valor` cada.
readme_register() {
    local text
    text="$(tr -d '\r' <"$root/README.md" | sed -n '\#-X POST http://localhost:8080/v1/customers#,/jq -r \.id/p')"
    sed -nE 's/.*Bearer \$([A-Za-z]+).*/client\t\1/p' <<<"$text" | head -n1
    sed -nE 's/.*Idempotency-Key: ([0-9a-f-]+).*/key\t\1/p' <<<"$text" | head -n1
    sed -nE "s/.*-d '(\{.*\})'.*/body\t\1/p" <<<"$text" | head -n1
}

# Problemas do cadastro de exemplo do arquivo `$1` contra o do README; um por linha.
register_problems() {
    local readme name method url token key body expected
    readme="$(readme_register)"
    IFS=$'\t' read -r name method url token key body < <(parse "$1" | awk -F'\t' '$1 == "registerCustomer"') || true
    [ -n "${method:-}" ] || { echo "requests.http has no request named registerCustomer"; return; }
    expected="$(awk -F'\t' '$1 == "key" { print $2 }' <<<"$readme")"
    [ -n "$expected" ] || { echo "README.md has no Idempotency-Key in the customer registration"; return; }
    [ "$key" = "$expected" ] || echo "registerCustomer sends Idempotency-Key $key, the README sends $expected"
    expected="$(awk -F'\t' '$1 == "client" { print $2 }' <<<"$readme")"
    [ "$token" = "{{$expected.response.body.\$.access_token}}" ] || echo "registerCustomer uses $token, the README uses \$$expected"
    expected="$(awk -F'\t' '$1 == "body" { print $2 }' <<<"$readme")"
    [ "$(jq -cS . <<<"$body")" = "$(jq -cS . <<<"$expected")" ] || echo "registerCustomer sends a body different from the README"
}

register() {
    local probe problems
    probe="$(mktemp -d)"
    trap 'rm -rf "$probe"' RETURN

    # Prova do detector: a chave nova a cada envio e uma chave fixa diferente da do README são achadas.
    sed '/^# @name registerCustomer/,/^###/ s/^Idempotency-Key: .*/Idempotency-Key: {{$guid}}/' "$requests" >"$probe/guid.http"
    problems="$(register_problems "$probe/guid.http")"
    grep -qF 'registerCustomer sends Idempotency-Key {{$guid}}' <<<"$problems" \
        || fail "detector missed {{\$guid}} in registerCustomer: $problems"
    sed '/^# @name registerCustomer/,/^###/ s/^Idempotency-Key: .*/Idempotency-Key: 00000000-0000-4000-8000-000000000000/' "$requests" >"$probe/other.http"
    problems="$(register_problems "$probe/other.http")"
    grep -qF 'registerCustomer sends Idempotency-Key 00000000-0000-4000-8000-000000000000' <<<"$problems" \
        || fail "detector missed a key different from the README: $problems"
    echo "OK: detector finds {{\$guid}} and a key different from the README in registerCustomer"

    problems="$(register_problems "$requests")"
    [ -z "$problems" ] || fail "registerCustomer differs from the README example:"$'\n'"$problems"
    echo "OK: registerCustomer sends the README key, client and body: repeating either one replays the 201"
}

case "$mode" in
    tokens) tokens ;;
    routes) routes ;;
    auth) auth ;;
    register) register ;;
    *) fail "usage: http-requests.sh tokens|routes|auth|register" ;;
esac
