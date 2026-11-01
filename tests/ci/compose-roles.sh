#!/usr/bin/env bash
# No docker compose, o Worker conecta ao PostgreSQL como ledger_worker e ao MongoDB como positions_writer; a Api,
# como ledger_app e positions_reader.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# Arquivo de variáveis vazio: um .env local não pode mudar a configuração lida.
: > "$work/empty.env"

fail() { echo "FAIL: $*" >&2; exit 1; }

# Connection string `$2` do serviço `$1`.
connection_string() {
    docker compose --project-directory "$root" -f "$root/docker-compose.yml" --env-file "$work/empty.env" config --format json \
        | jq -r --arg service "$1" --arg name "ConnectionStrings__$2" '.services[$service].environment[$name]' \
        | tr -d '\r'
}

# Usuário da connection string `Ledger` do serviço.
ledger_user() {
    connection_string "$1" Ledger | tr ';' '\n' | sed -n 's/^Username=//p'
}

# Usuário da connection string `Positions` (mongodb://usuario:senha@...) do serviço.
positions_user() {
    connection_string "$1" Positions | sed -n 's|^mongodb://\([^:@/]*\):.*|\1|p'
}

worker="$(ledger_user worker)"
[ "$worker" = "ledger_worker" ] || fail "worker connects as '$worker', expected 'ledger_worker'"
echo "OK: worker connects as ledger_worker"

api="$(ledger_user api)"
[ "$api" = "ledger_app" ] || fail "api connects as '$api', expected 'ledger_app'"
echo "OK: api connects as ledger_app"

worker="$(positions_user worker)"
[ "$worker" = "positions_writer" ] || fail "worker reads the read model as '$worker', expected 'positions_writer'"
echo "OK: worker connects to the read model as positions_writer"

api="$(positions_user api)"
[ "$api" = "positions_reader" ] || fail "api reads the read model as '$api', expected 'positions_reader'"
echo "OK: api connects to the read model as positions_reader"
