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

grep -qF -- '| [0053](0053-relay-do-outbox-e-projecao-pela-proxima-versao.md) |' "$index" || fail "ADR-0053 is not in the index"
echo "OK: ADR-0053 indexed"

# Convenção do catálogo: um ADR vigente complementa o ADR-0026 com a regra, e o índice a traz junto da tabela de onde
# vive cada catálogo.
convention="$(grep -lxF -- '- **Complementa:** ADR-0026' "$adr"/[0-9][0-9][0-9][0-9]-*.md | xargs grep -lF 'openapi-v1.json' || true)"
[ -n "$convention" ] || fail "no current ADR complements ADR-0026 with the catalog convention"
grep -qxF -- '- **Status:** Aceita' $convention || fail "${convention#"$adr"/} is not Aceita"
grep -qxF -- '## Regra e catálogo' "$index" || fail "README.md has no ## Regra e catálogo section"
grep -qF -- 'Só uma regra que' "$index" || fail "README.md does not state that only a rule change asks for a new ADR"
echo "OK: ${convention#"$adr"/} complements ADR-0026, and the index states the catalog rule"
