#!/usr/bin/env bash
# O PostgreSQL do docker compose é 18.6.
set -euo pipefail
source "$(dirname "$0")/lib.sh"

smoke_clone
cd "$SMOKE_DIR"

docker compose -p "$SMOKE_PROJECT" up -d --wait postgres
version="$(docker compose -p "$SMOKE_PROJECT" exec -T postgres psql -U postgres -d ledger_db -tAc 'SHOW server_version')"
case "$version" in
    18.6*) echo "OK: server_version $version" ;;
    *) fail "server_version was $version, expected 18.6" ;;
esac
