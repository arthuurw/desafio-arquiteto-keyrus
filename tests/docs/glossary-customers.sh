#!/usr/bin/env bash
# O glossário define, na seção Customers, os termos do cadastro com o nome usado no código, sem a marca de termos
# ainda não implementados.
set -euo pipefail
glossary="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/docs/glossario.md"

fail() { echo "FAIL: $1" >&2; exit 1; }

# Só a seção Customers: do título até o próximo título de seção.
customers="$(awk '/^## /{ inside = ($0 == "## Customers") } inside' "$glossary")"
[ -n "$customers" ] || fail "glossary has no ## Customers section"

status=0
grep -qiE -- 'ainda não implementad|previst' <<<"$customers" || status=$?
[ "$status" -ne 0 ] || fail "Customers section still marks its terms as not implemented"
[ "$status" -eq 1 ] || fail "grep failed with status $status on the Customers section"
echo "OK: Customers section has no not-implemented mark"

row() {
    local prefix="$1"
    grep -qF -- "$prefix" <<<"$customers" || fail "Customers section has no row starting with $prefix"
    echo "OK: $prefix"
}

row '| Cliente | `Customer` |'
row '| CPF | `Cpf` |'
row '| DEK | `IKeyStore` |'
row '| Blind index | `IBlindIndexer` |'
row '| Máscara | `CustomerMask` |'
row '| Dados pessoais completos | `personal-data` |'
