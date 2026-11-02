#!/usr/bin/env bash
# O arquivo docs/api/requests.http exercita a Api como o contrato e o realm local a descrevem.
#   tokens: um token nomeado por client, por client credentials, e o modo delegado da ana (senha no
#           channel-web, depois troca no channel-app para a Api).
#   routes: toda rota do openapi-v1.json tem ao menos uma requisição, e nenhuma requisição à Api fica
#           fora do documento; antes, o script prova que acha uma rota faltando e uma a mais plantadas.
#   auth:   toda requisição à Api usa o token de um client que tem o escopo da rota no realm, e todo
#           `POST` manda uma `Idempotency-Key` nova.
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
        [[ "$url" == "{{api}}"* ]] || continue
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
    echo "OK: detector finds a planted missing route and a planted extra route"

    gaps="$(route_gaps "$requests")"
    [ -z "$gaps" ] || fail "requests.http does not match openapi-v1.json:"$'\n'"$gaps"
    echo "OK: requests.http has every route of openapi-v1.json ($(operations | wc -l | tr -d ' ')) and no other"
}

auth() {
    local parsed name method url token key body found scope token_body client count=0
    parsed="$(parse "$requests")"
    while IFS=$'\t' read -r name method url token key body; do
        [[ "$url" == "{{api}}"* ]] || continue
        found="$(template_of "$method" "$url")" || fail "$method $url is not a route of openapi-v1.json"
        scope="${found#* }"
        [[ "$token" =~ ^\{\{([A-Za-z0-9_]+)\.response\.body\.\$\.access_token\}\}$ ]] \
            || fail "$method $url does not send Authorization: Bearer {{<token>.response.body.\$.access_token}}"
        token_body="$(awk -F'\t' -v n="${BASH_REMATCH[1]}" '$1 == n { print $6 }' <<<"$parsed")"
        [ -n "$token_body" ] || fail "$method $url uses '${BASH_REMATCH[1]}', which is not a token request"
        client="$(field "$token_body" client_id)"
        jq -e --arg c "$client" --arg s "$scope" \
            '.clients[] | select(.clientId == $c) | .defaultClientScopes | index($s)' "$realm" >/dev/null \
            || fail "$method $url needs $scope, which client $client does not have in the realm"
        if [ "$method" = "POST" ]; then
            [ "$key" = '{{$guid}}' ] || fail "$method $url does not send Idempotency-Key: {{\$guid}}"
        fi
        count=$((count + 1))
    done <<<"$parsed"
    [ "$count" -gt 0 ] || fail "requests.http has no request to the Api"
    echo "OK: $count Api requests use a token whose client has the route scope, and every POST sends a new Idempotency-Key"
}

case "$mode" in
    tokens) tokens ;;
    routes) routes ;;
    auth) auth ;;
    *) fail "usage: http-requests.sh tokens|routes|auth" ;;
esac
