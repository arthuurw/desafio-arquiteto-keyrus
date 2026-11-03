#!/usr/bin/env bash
# A tabela do roteiro diz a situação de cada funcionalidade, no escopo do ADR-0054: F0 a F7, F9 e F10
# entregues, F8 fora da entrega.
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
    case "$n" in
        8) expected="Fora da entrega" ;;
        *) expected=Entregue ;;
    esac
    row="$(grep -m1 -E "^\| F$n \|" "$roadmap" || true)"
    [ -n "$row" ] || fail "roadmap table has no row for F$n"
    actual="$(awk -F'|' -v c="$column" '{ gsub(/^ +| +$/, "", $c); print $c }' <<<"$row")"
    [ "$actual" = "$expected" ] || fail "F$n is '$actual', expected '$expected'"
done
echo "OK: F0-F7, F9 and F10 Entregue, F8 Fora da entrega"

# O F10 não lista o que já existe, e registra o risco aceito do `409 cpf-already-registered` para o modelo de ameaças.
f10="$(grep -m1 -E '^\| F10 \|' "$roadmap")"
[[ "$f10" != *sequências* ]] || fail "F10 still lists the sequências, which docs/arquitetura already has"
[[ "$f10" == *'409 cpf-already-registered'* ]] || fail "F10 does not record the accepted risk of 409 cpf-already-registered"
echo "OK: F10 lists no sequências and records the 409 cpf-already-registered risk"
