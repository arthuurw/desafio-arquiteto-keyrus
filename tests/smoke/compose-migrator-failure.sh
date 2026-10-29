#!/usr/bin/env bash
# Com a senha do migrator errada, o migrator sai com 1 e api e worker nunca iniciam.
set -euo pipefail
source "$(dirname "$0")/lib.sh"

smoke_clone
cd "$SMOKE_DIR"

if docker compose -p "$SMOKE_PROJECT" -f docker-compose.yml -f tests/smoke/migrator-wrong-password.yml up -d --build; then
    fail "docker compose up succeeded with a failing migrator"
fi

migrator="$(docker compose -p "$SMOKE_PROJECT" ps -a -q migrator)"
[ -n "$migrator" ] || fail "migrator container was not created"
exit_code="$(docker inspect -f '{{.State.ExitCode}}' "$migrator")"
[ "$exit_code" = "1" ] || fail "migrator exit code was $exit_code, expected 1"
echo "OK: migrator exited with 1"

for service in api worker; do
    container="$(docker compose -p "$SMOKE_PROJECT" ps -a -q "$service")"
    if [ -n "$container" ]; then
        started="$(docker inspect -f '{{.State.StartedAt}}' "$container")"
        case "$started" in
            0001-01-01*) ;;
            *) fail "$service started at $started" ;;
        esac
    fi
    echo "OK: $service never started"
done
