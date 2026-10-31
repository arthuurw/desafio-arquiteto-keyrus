#!/usr/bin/env bash
# O glossário define o catálogo de categorias, com cada código, o lançamento estornado e, na seção
# Ledger, os termos de titularidade, recusa, tipo, conta ocupada, teto de saldo e estorno usados pelo código.
set -euo pipefail
glossary="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/docs/glossario.md"

fail() { echo "FAIL: $1" >&2; exit 1; }

catalog="$(grep -F -- '| Catálogo de categorias | `Categories` |' "$glossary" || true)"
[ -n "$catalog" ] || fail 'glossary has no row starting with | Catálogo de categorias | `Categories` |'
echo 'OK: | Catálogo de categorias | `Categories` |'

for code in PIX_IN TED_IN BOLETO_IN DEPOSIT ADJUSTMENT_IN PIX_OUT TED_OUT BOLETO_OUT CARD FEE ADJUSTMENT_OUT REVERSAL; do
    grep -qF -- "\`$code\`" <<<"$catalog" || fail "category catalog row has no \`$code\`"
done
echo "OK: catalog row lists the 12 category codes"

grep -qF -- '| Lançamento estornado | `ReversedEntryId` |' "$glossary" \
    || fail 'glossary has no row starting with | Lançamento estornado | `ReversedEntryId` |'
echo 'OK: | Lançamento estornado | `ReversedEntryId` |'

# Só a seção Ledger: do título até o próximo título de seção.
ledger="$(awk '/^## /{ inside = ($0 == "## Ledger") } inside' "$glossary")"
[ -n "$ledger" ] || fail "glossary has no ## Ledger section"

# Linha do termo na seção Ledger; os demais argumentos precisam aparecer nela.
row() {
    local prefix="$1" line expected
    shift
    line="$(grep -F -- "$prefix" <<<"$ledger" || true)"
    [ -n "$line" ] || fail "Ledger section has no row starting with $prefix"
    for expected in "$@"; do
        grep -qF -- "$expected" <<<"$line" || fail "row $prefix has no $expected"
    done
    echo "OK: $prefix"
}

row '| Titular | `CustomerId` |' 'titularidade'
row '| Recusa de negócio | `LedgerError` |'
row '| Tipo de lançamento | `EntryType` |'
row '| Conta ocupada | `ConcurrencyTimeout` |'
row '| Teto de saldo | `MaxBalance` |' '999999999999999.99' '`Account.MaxBalance`' '`balance-limit-exceeded`'
row '| Estorno | `Reversal` |' '`Account.Reverse`' '`REVERSAL`'
