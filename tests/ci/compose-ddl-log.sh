#!/usr/bin/env bash
# No docker compose, o PostgreSQL loga todo DDL com usuário e banco, como o dos testes de integração: o DDL da dona
# sobre a trilha de auditoria deixa rastro (ADR-0044).
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# Arquivo de variáveis vazio: um .env local não pode mudar a configuração lida.
: > "$work/empty.env"

fail() { echo "FAIL: $*" >&2; exit 1; }

command_line="$(docker compose --project-directory "$root" -f "$root/docker-compose.yml" --env-file "$work/empty.env" config --format json \
    | jq -r '.services.postgres.command | join(" ")' | tr -d '\r')"

for setting in 'log_statement=ddl' 'log_line_prefix=%m [%p] %u@%d '; do
    [[ " $command_line " == *" -c $setting"* ]] || fail "postgres command has no '-c $setting': $command_line"
    echo "OK: postgres runs with -c $setting"
done

container="$root/tests/IntegrationTests/Infrastructure/PostgresDatabase.cs"
grep -qF -- '.WithCommand("-c", "log_statement=ddl", "-c", "log_line_prefix=%m [%p] %u@%d ")' "$container" \
    || fail "PostgresDatabase.cs does not start the test container with the same DDL log"
echo "OK: the test container logs DDL the same way"
