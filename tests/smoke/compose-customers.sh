#!/usr/bin/env bash
# Em clone limpo, `docker compose up` sobe o OpenBao e o bootstrap dele antes da Api; os clientes semeados da ana e do
# bruno respondem, a ana abre conta, e reiniciar o cofre e a Api mantém uma linha de cada e os mesmos dados.
set -euo pipefail
source "$(dirname "$0")/lib.sh"

ana=11111111-1111-4111-8111-111111111111
bruno=22222222-2222-4222-8222-222222222222

new_uuid() {
    local hex
    hex="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
    printf '%s-%s-4%s-8%s-%s\n' "${hex:0:8}" "${hex:8:4}" "${hex:13:3}" "${hex:17:3}" "${hex:20:12}"
}

token() {
    curl -sf -d grant_type=client_credentials -d "client_id=$1" -d "client_secret=$1_dev" \
        "http://localhost:$KEYCLOAK_PORT/realms/ledger/protocol/openid-connect/token" | jq -r .access_token
}

# Status HTTP do GET com o token.
status_of() {
    curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $2" "http://localhost:$API_PORT$1"
}

# Espera o GET responder 200, por até 120 s: o seed roda em segundo plano na subida da Api.
wait_ok() {
    local route="$1" access="$2" status=""
    for _ in $(seq 1 60); do
        status="$(status_of "$route" "$access")"
        [ "$status" = "200" ] && { echo "OK: GET $route -> 200"; return 0; }
        sleep 2
    done
    fail "GET $route answered $status"
}

psql_count() {
    docker compose -p "$SMOKE_PROJECT" exec -T postgres psql -U postgres -d ledger_db -tA -c "$1" | tr -d '\r'
}

smoke_clone
cd "$SMOKE_DIR"

docker compose -p "$SMOKE_PROJECT" up -d --build
wait_healthy "http://localhost:$API_PORT/health/live"
wait_healthy "http://localhost:$WORKER_PORT/health/live"
wait_issuer "http://localhost:$KEYCLOAK_PORT/realms/ledger/.well-known/openid-configuration" "http://localhost:$KEYCLOAK_PORT/realms/ledger"

bootstrap="$(docker compose -p "$SMOKE_PROJECT" ps -a -q openbao-bootstrap)"
api_container="$(docker compose -p "$SMOKE_PROJECT" ps -q api)"
[ "$(docker inspect -f '{{.State.ExitCode}}' "$bootstrap")" = "0" ] || fail "openbao-bootstrap did not exit 0"
finished="$(docker inspect -f '{{.State.FinishedAt}}' "$bootstrap")"
started="$(docker inspect -f '{{.State.StartedAt}}' "$api_container")"
[[ "$finished" < "$started" ]] || fail "the api started at $started, before openbao-bootstrap finished at $finished"
echo "OK: openbao-bootstrap exited 0 before the api started"

backoffice="$(token backoffice)"
compliance="$(token compliance)"
wait_ok "/v1/customers/$ana" "$backoffice"
wait_ok "/v1/customers/$bruno" "$backoffice"

opened="$(curl -s -o /dev/null -w '%{http_code}' -X POST "http://localhost:$API_PORT/v1/accounts" \
    -H "Authorization: Bearer $backoffice" -H "Idempotency-Key: $(new_uuid)" -H 'Content-Type: application/json' \
    -d "{\"customerId\":\"$ana\"}")"
[ "$opened" = "201" ] || fail "opening an account for ana answered $opened"
echo "OK: backoffice opens an account for ana"

before="$(curl -sf -H "Authorization: Bearer $compliance" "http://localhost:$API_PORT/v1/customers/$ana/personal-data")"
[ -n "$before" ] || fail "personal-data of ana did not answer"

docker compose -p "$SMOKE_PROJECT" restart openbao api
wait_healthy "http://localhost:$API_PORT/health/live"
compliance="$(token compliance)"
wait_ok "/v1/customers/$ana/personal-data" "$compliance"

after="$(curl -sf -H "Authorization: Bearer $compliance" "http://localhost:$API_PORT/v1/customers/$ana/personal-data")"
[ "$after" = "$before" ] || fail "personal-data of ana changed across the restart: '$before' -> '$after'"
echo "OK: personal-data of ana is the same after restarting openbao and the api"

rows="$(psql_count "SELECT count(*) FROM customers.customers WHERE id IN ('$ana', '$bruno') GROUP BY id ORDER BY id")"
[ "$rows" = $'1\n1' ] || fail "expected one row for ana and one for bruno, got: $rows"
echo "OK: one row for ana and one for bruno after the second start"
