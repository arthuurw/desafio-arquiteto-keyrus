#!/usr/bin/env bash
# Em clone limpo, sem .env, `docker compose up` deixa Api e Worker respondendo 200 Healthy e o Keycloak no ar.
set -euo pipefail
source "$(dirname "$0")/lib.sh"

smoke_clone
cd "$SMOKE_DIR"
[ ! -e .env ] || fail ".env present in the clean clone"

docker compose -p "$SMOKE_PROJECT" up -d --build

wait_healthy http://localhost:8080/health/live
wait_healthy http://localhost:8081/health/live
wait_issuer http://localhost:8180/realms/ledger/.well-known/openid-configuration http://localhost:8180/realms/ledger
