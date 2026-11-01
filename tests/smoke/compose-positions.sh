#!/usr/bin/env bash
# Em clone limpo, `docker compose up` sobe o MongoDB com os usuários do modelo de leitura: um crédito aparece no saldo
# pela Api e é projetado pelo Worker no banco `positions`, e o usuário da Api não grava nele.
set -euo pipefail
source "$(dirname "$0")/lib.sh"

# UUID v4 a partir de bytes aleatórios, sem depender de uuidgen.
new_uuid() {
    local hex
    hex="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
    printf '%s-%s-4%s-8%s-%s\n' "${hex:0:8}" "${hex:8:4}" "${hex:13:3}" "${hex:17:3}" "${hex:20:12}"
}

token() {
    curl -sf -d grant_type=client_credentials -d "client_id=$1" -d "client_secret=$1_dev" \
        "http://localhost:$KEYCLOAK_PORT/realms/ledger/protocol/openid-connect/token" | jq -r .access_token
}

# POST com chave nova; imprime o corpo e falha se o status não for 201.
post() {
    local route="$1" access="$2" body="$3" response status
    response="$(curl -s -w '\n%{http_code}' -X POST "http://localhost:$API_PORT$route" \
        -H "Authorization: Bearer $access" -H "Idempotency-Key: $(new_uuid)" -H 'Content-Type: application/json' -d "$body")"
    status="${response##*$'\n'}"
    [ "$status" = "201" ] || fail "POST $route answered $status: ${response%$'\n'*}"
    printf '%s\n' "${response%$'\n'*}"
}

mongo_admin() {
    docker compose -p "$SMOKE_PROJECT" exec -T mongo mongosh --quiet -u admin -p admin_dev --authenticationDatabase admin positions --eval "$1" | tr -d '\r'
}

smoke_clone
cd "$SMOKE_DIR"

docker compose -p "$SMOKE_PROJECT" up -d --build --wait mongo
docker compose -p "$SMOKE_PROJECT" up -d --build
wait_healthy "http://localhost:$API_PORT/health/live"
wait_healthy "http://localhost:$WORKER_PORT/health/live"
wait_issuer "http://localhost:$KEYCLOAK_PORT/realms/ledger/.well-known/openid-configuration" "http://localhost:$KEYCLOAK_PORT/realms/ledger"

backoffice="$(token backoffice)"
payments="$(token payments-hub)"
# A conta precisa de cliente cadastrado: o da ana, semeado pela Api do compose.
account="$(post /v1/accounts "$backoffice" '{"customerId":"11111111-1111-4111-8111-111111111111"}' | jq -r .id)"
post "/v1/accounts/$account/credits" "$payments" "{\"amount\":\"25.00\",\"category\":\"PIX_IN\",\"externalReference\":\"SMOKE-$(new_uuid)\"}" >/dev/null

balance="$(curl -sf -H "Authorization: Bearer $backoffice" "http://localhost:$API_PORT/v1/accounts/$account/balance" | jq -r .balance)"
[ "$balance" = "25.00" ] || fail "balance was '$balance', expected 25.00"
echo "OK: balance 25.00 through the Api"

projected=""
for _ in $(seq 1 60); do
    projected="$(mongo_admin "db.positions.countDocuments({ _id: UUID('$account'), version: 1 })")"
    [ "$projected" = "1" ] && break
    sleep 1
done
[ "$projected" = "1" ] || fail "the Worker did not project account $account into positions"
echo "OK: the Worker projected the credit into the positions database"

if docker compose -p "$SMOKE_PROJECT" exec -T mongo mongosh --quiet \
    "mongodb://positions_reader:positions_reader_dev@localhost:27017/positions?authSource=positions" \
    --eval "db.positions.insertOne({ probe: 1 })" >/dev/null 2>&1; then
    fail "positions_reader wrote into the positions database"
fi
echo "OK: positions_reader cannot write into the positions database"
