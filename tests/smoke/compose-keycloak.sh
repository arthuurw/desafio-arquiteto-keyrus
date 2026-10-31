#!/usr/bin/env bash
# Em clone limpo, o Keycloak do docker compose serve a descoberta do realm `ledger` na porta KEYCLOAK_PORT do host.
set -euo pipefail
source "$(dirname "$0")/lib.sh"

smoke_clone
cd "$SMOKE_DIR"

docker compose -p "$SMOKE_PROJECT" up -d keycloak
wait_issuer "http://localhost:$KEYCLOAK_PORT/realms/ledger/.well-known/openid-configuration" "http://localhost:$KEYCLOAK_PORT/realms/ledger"
