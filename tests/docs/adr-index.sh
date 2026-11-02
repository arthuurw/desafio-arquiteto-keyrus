#!/usr/bin/env bash
# Todo ADR em docs/adr aparece no índice do README com o mesmo status do arquivo, e todo ADR do índice existe.
# ADR substituído fica em docs/adr/history; o vigente, na raiz de docs/adr.
set -euo pipefail
adr="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/docs/adr"
index="$adr/README.md"

fail() { echo "FAIL: $1" >&2; exit 1; }

count=0
for file in "$adr"/[0-9][0-9][0-9][0-9]-*.md "$adr"/history/[0-9][0-9][0-9][0-9]-*.md; do
    [ -f "$file" ] || continue
    name="$(basename "$file")"
    number="${name:0:4}"
    path="${file#"$adr"/}"
    status="$(sed -n 's/^- \*\*Status:\*\* //p' "$file" | tr -d '\r')"
    [ -n "$status" ] || fail "$path has no Status line"
    case "$status" in
        "Substituída por "*) [ "$path" = "history/$name" ] || fail "$path is '$status' but is not in docs/adr/history" ;;
        *) [ "$path" = "$name" ] || fail "$path is '$status' but is in docs/adr/history" ;;
    esac
    row="$(grep -F -- "| [$number]($path) |" "$index" | tr -d '\r' || true)"
    [ -n "$row" ] || fail "index has no row for $path"
    indexed="$(sed 's/ *|$//; s/.*| *//' <<<"$row")"
    [ "$indexed" = "$status" ] || fail "$path is '$status' but the index says '$indexed'"
    count=$((count + 1))
done

rows="$(grep -cE '^\| \[[0-9]{4}\]\(' "$index")"
[ "$rows" -eq "$count" ] || fail "index has $rows rows for $count ADR files"
echo "OK: $count ADRs indexed with their status"

grep -qF -- '| [0035](0035-relay-do-outbox-e-projecao-pela-proxima-versao.md) |' "$index" || fail "ADR-0035 is not in the index"
echo "OK: ADR-0035 indexed"
