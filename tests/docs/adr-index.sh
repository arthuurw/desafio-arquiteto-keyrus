#!/usr/bin/env bash
# Todo ADR em docs/adr aparece no índice do README com o mesmo status do arquivo, e todo ADR do índice existe.
set -euo pipefail
adr="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/docs/adr"
index="$adr/README.md"

fail() { echo "FAIL: $1" >&2; exit 1; }

count=0
for file in "$adr"/[0-9][0-9][0-9][0-9]-*.md; do
    name="$(basename "$file")"
    number="${name:0:4}"
    status="$(sed -n 's/^- \*\*Status:\*\* //p' "$file" | tr -d '\r')"
    [ -n "$status" ] || fail "$name has no Status line"
    row="$(grep -F -- "| [$number]($name) |" "$index" | tr -d '\r' || true)"
    [ -n "$row" ] || fail "index has no row for $name"
    indexed="$(sed 's/ *|$//; s/.*| *//' <<<"$row")"
    [ "$indexed" = "$status" ] || fail "$name is '$status' but the index says '$indexed'"
    count=$((count + 1))
done

rows="$(grep -cE '^\| \[[0-9]{4}\]\(' "$index")"
[ "$rows" -eq "$count" ] || fail "index has $rows rows for $count ADR files"
echo "OK: $count ADRs indexed with their status"

grep -qF -- '| [0035](0035-relay-do-outbox-e-projecao-pela-proxima-versao.md) |' "$index" || fail "ADR-0035 is not in the index"
echo "OK: ADR-0035 indexed"
