#!/usr/bin/env bash
# Em clone limpo, sem .env, `docker compose up` deixa Api e Worker respondendo 200 Healthy e o Keycloak no ar.
set -euo pipefail
source "$(dirname "$0")/lib.sh"

smoke_clone
cd "$SMOKE_DIR"
[ ! -e .env ] || fail ".env present in the clean clone"

docker compose -p "$SMOKE_PROJECT" up -d --build

wait_healthy "http://localhost:$API_PORT/health/live"
wait_healthy "http://localhost:$WORKER_PORT/health/live"
wait_issuer "http://localhost:$KEYCLOAK_PORT/realms/ledger/.well-known/openid-configuration" "http://localhost:$KEYCLOAK_PORT/realms/ledger"

# Corpo acima de 16 384 bytes, pelo Kestrel real: `413 payload-too-large` com e sem `Content-Length`, e o corpo no
# limite segue até o endpoint, que recusa o cliente inexistente.
token="$(curl -sf -d grant_type=client_credentials -d client_id=backoffice -d client_secret=backoffice_dev \
    "http://localhost:$KEYCLOAK_PORT/realms/ledger/protocol/openid-connect/token" | jq -r .access_token)"
[ -n "$token" ] && [ "$token" != null ] || fail "no backoffice token from Keycloak"
bodies="$SMOKE_TMP/bodies"
mkdir -p "$bodies"
padded() {
    local json="$1" bytes="$2"
    printf '%s' "$json" >"$bodies/$3"
    head -c $((bytes - ${#json})) /dev/zero | tr '\0' ' ' >>"$bodies/$3"
    [ "$(wc -c <"$bodies/$3")" -eq "$bytes" ] || fail "$3 is not $bytes bytes"
}
padded '{"customerId":"00000000-0000-4000-8000-000000000000"}' 16384 at-limit.json
padded '{"customerId":"00000000-0000-4000-8000-000000000000"}' 16385 over-limit.json
head -c $((30 * 1024 * 1024)) /dev/zero | tr '\0' x >"$bodies/huge.json"

open_account() {
    local file="$1" key
    shift
    key="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n' | sed -E 's/^(.{8})(.{4}).(.{3}).(.{3})(.{12})$/\1-\2-4\3-8\4-\5/')"
    curl -s -o "$bodies/answer.json" -w '%{http_code}' -X POST "http://localhost:$API_PORT/v1/accounts" \
        -H "Authorization: Bearer $token" -H "Idempotency-Key: $key" -H 'Content-Type: application/json' \
        "$@" --data-binary "@$bodies/$file" || true
}

for case in "huge.json" "over-limit.json chunked"; do
    read -r file mode <<<"$case"
    if [ "${mode:-}" = chunked ]; then
        status="$(open_account "$file" -H 'Transfer-Encoding: chunked')"
    else
        status="$(open_account "$file")"
    fi
    [ "$status" = 413 ] || fail "$case answered $status, expected 413"
    [ "$(jq -r .type "$bodies/answer.json")" = payload-too-large ] || fail "$case type was $(cat "$bodies/answer.json")"
    echo "OK: $case -> 413 payload-too-large"
done

status="$(open_account at-limit.json)"
[ "$status" = 404 ] || fail "16384-byte body answered $status, expected 404 from the endpoint"
[ "$(jq -r .type "$bodies/answer.json")" = customer-not-found ] || fail "16384-byte body type was $(cat "$bodies/answer.json")"
echo "OK: 16384-byte body reaches the endpoint -> 404 customer-not-found"
