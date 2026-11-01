#!/usr/bin/env bash
# O docker compose publica as portas só em 127.0.0.1, com a porta do host sobrescrevível por variável de ambiente.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# Arquivo de variáveis vazio: um .env local não pode mudar as portas que o teste espera.
: > "$work/empty.env"

fail() { echo "FAIL: $*" >&2; exit 1; }

# Imprime `serviço=host_ip:porta` de cada porta publicada, em ordem, com as variáveis dadas e nenhuma outra.
published_ports() {
    env -u POSTGRES_PORT -u MONGO_PORT -u KEYCLOAK_PORT -u API_PORT -u WORKER_PORT "$@" \
        docker compose --project-directory "$root" -f "$root/docker-compose.yml" --env-file "$work/empty.env" \
        config --format json \
        | jq -r '.services | to_entries[] | .key as $s | .value.ports[]? | "\($s)=\(.host_ip // "*"):\(.published)"' \
        | tr -d '\r' | sort
}

expect_ports() {
    local label="$1" expected="$2" actual
    shift 2
    actual="$(published_ports "$@")"
    [ "$actual" = "$expected" ] || fail "$label: published ports were
$actual
expected
$expected"
    echo "OK: $label -> $(printf '%s' "$actual" | tr '\n' ' ')"
}

expect_ports "defaults" "$(printf '%s\n' \
    api=127.0.0.1:8080 \
    keycloak=127.0.0.1:8180 \
    mongo=127.0.0.1:27017 \
    postgres=127.0.0.1:5432 \
    worker=127.0.0.1:8081)"

expect_ports "POSTGRES_PORT=15432" "$(printf '%s\n' \
    api=127.0.0.1:8080 \
    keycloak=127.0.0.1:8180 \
    mongo=127.0.0.1:27017 \
    postgres=127.0.0.1:15432 \
    worker=127.0.0.1:8081)" \
    POSTGRES_PORT=15432

expect_ports "all ports overridden" "$(printf '%s\n' \
    api=127.0.0.1:18080 \
    keycloak=127.0.0.1:18180 \
    mongo=127.0.0.1:37017 \
    postgres=127.0.0.1:15432 \
    worker=127.0.0.1:18081)" \
    POSTGRES_PORT=15432 MONGO_PORT=37017 KEYCLOAK_PORT=18180 API_PORT=18080 WORKER_PORT=18081
