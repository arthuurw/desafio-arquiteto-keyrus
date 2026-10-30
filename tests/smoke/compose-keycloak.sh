#!/usr/bin/env bash
# Em clone limpo, o Keycloak do docker compose serve a descoberta do realm `ledger` na porta 8180 do host.
set -euo pipefail
source "$(dirname "$0")/lib.sh"

smoke_clone
cd "$SMOKE_DIR"

docker compose -p "$SMOKE_PROJECT" up -d keycloak
wait_issuer http://localhost:8180/realms/ledger/.well-known/openid-configuration http://localhost:8180/realms/ledger
