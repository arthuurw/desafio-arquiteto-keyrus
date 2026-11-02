#!/usr/bin/env bash
# `instant_before` compara os instantes do `docker inspect` como números, com frações de qualquer tamanho.
set -euo pipefail
source "$(dirname "$0")/lib.sh"

before() {
    instant_before "$1" "$2" || fail "$1 should be before $2"
    echo "OK: $1 < $2"
}

not_before() {
    if instant_before "$1" "$2"; then
        fail "$1 should not be before $2"
    fi
    echo "OK: $1 is not before $2"
}

before 2026-11-01T10:00:05.1234567Z 2026-11-01T10:00:05.12345678Z
before 2026-11-01T10:00:05Z 2026-11-01T10:00:05.1Z
before 2026-11-01T10:00:05.9Z 2026-11-01T10:00:06Z
before 2026-11-01T23:59:59.999999999Z 2026-11-02T00:00:00Z
not_before 2026-11-01T10:00:05.12345678Z 2026-11-01T10:00:05.1234567Z
not_before 2026-11-01T10:00:05.1Z 2026-11-01T10:00:05Z
not_before 2026-11-01T10:00:05.100Z 2026-11-01T10:00:05.1Z
not_before 2026-11-01T10:00:06Z 2026-11-01T10:00:05.9Z

# Fora do formato, a comparação falha em vez de responder.
if (instant_before 2026-11-01T10:00:05+00:00 2026-11-01T10:00:06Z) 2>/dev/null; then
    fail "an instant with offset was compared"
fi
echo "OK: an instant outside RFC 3339 UTC is refused"

# O smoke do cofre compara a subida da Api com o fim do bootstrap por esta função, não pelo texto.
customers="$(dirname "$0")/compose-customers.sh"
grep -qF 'instant_before "$finished" "$started"' "$customers" || fail "compose-customers.sh does not compare the instants with instant_before"
if grep -qE '\[\[ "\$finished" < "\$started" \]\]' "$customers"; then
    fail "compose-customers.sh still compares the instants as text"
fi
echo "OK: compose-customers.sh compares the instants with instant_before"
