#!/usr/bin/env bash
# No docker compose, o Worker conecta ao banco como ledger_worker, e a Api como ledger_app.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# Arquivo de variáveis vazio: um .env local não pode mudar a configuração lida.
: > "$work/empty.env"

fail() { echo "FAIL: $*" >&2; exit 1; }

# Usuário da connection string `Ledger` do serviço.
ledger_user() {
    docker compose --project-directory "$root" -f "$root/docker-compose.yml" --env-file "$work/empty.env" config --format json \
        | jq -r --arg service "$1" '.services[$service].environment.ConnectionStrings__Ledger' \
        | tr -d '\r' | tr ';' '\n' | sed -n 's/^Username=//p'
}

worker="$(ledger_user worker)"
[ "$worker" = "ledger_worker" ] || fail "worker connects as '$worker', expected 'ledger_worker'"
echo "OK: worker connects as ledger_worker"

api="$(ledger_user api)"
[ "$api" = "ledger_app" ] || fail "api connects as '$api', expected 'ledger_app'"
echo "OK: api connects as ledger_app"
