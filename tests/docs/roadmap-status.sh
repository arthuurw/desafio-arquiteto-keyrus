#!/usr/bin/env bash
# A tabela do roteiro diz a situação de cada funcionalidade: F0 a F6 entregues, F7 a F10 pendentes.
set -euo pipefail
roadmap="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/docs/roadmap.md"

fail() { echo "FAIL: $1" >&2; exit 1; }

header="$(grep -m1 -E '^\| # \| Funcionalidade \|' "$roadmap" || true)"
[ -n "$header" ] || fail "roadmap.md has no table starting with | # | Funcionalidade |"

# Posição da coluna Situação no cabeçalho, contada como os campos de `awk -F'|'`.
column="$(awk -F'|' '{ for (i = 1; i <= NF; i++) { gsub(/^ +| +$/, "", $i); if ($i == "Situação") print i } }' <<<"$header")"
[ -n "$column" ] || fail "roadmap table has no Situação column"
echo "OK: roadmap table has a Situação column"

for n in 0 1 2 3 4 5 6 7 8 9 10; do
    expected="$([ "$n" -le 6 ] && echo Entregue || echo Pendente)"
    row="$(grep -m1 -E "^\| F$n \|" "$roadmap" || true)"
    [ -n "$row" ] || fail "roadmap table has no row for F$n"
    actual="$(awk -F'|' -v c="$column" '{ gsub(/^ +| +$/, "", $c); print $c }' <<<"$row")"
    [ "$actual" = "$expected" ] || fail "F$n is '$actual', expected '$expected'"
done
echo "OK: F0-F6 Entregue, F7-F10 Pendente"
