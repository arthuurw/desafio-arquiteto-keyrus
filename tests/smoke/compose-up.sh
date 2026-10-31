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
