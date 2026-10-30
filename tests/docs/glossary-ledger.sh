#!/usr/bin/env bash
# O glossário define o catálogo de categorias, com cada código, e o lançamento estornado.
set -euo pipefail
glossary="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/docs/glossario.md"

catalog="$(grep -F -- '| Catálogo de categorias | `Categories` |' "$glossary" || true)"
[ -n "$catalog" ] || { echo 'FAIL: glossary has no row starting with | Catálogo de categorias | `Categories` |' >&2; exit 1; }
echo 'OK: | Catálogo de categorias | `Categories` |'

for code in PIX_IN TED_IN BOLETO_IN DEPOSIT ADJUSTMENT_IN PIX_OUT TED_OUT BOLETO_OUT CARD FEE ADJUSTMENT_OUT REVERSAL; do
    grep -qF -- "\`$code\`" <<<"$catalog" || { echo "FAIL: category catalog row has no \`$code\`" >&2; exit 1; }
done
echo "OK: catalog row lists the 12 category codes"

grep -qF -- '| Lançamento estornado | `ReversedEntryId` |' "$glossary" \
    || { echo 'FAIL: glossary has no row starting with | Lançamento estornado | `ReversedEntryId` |' >&2; exit 1; }
echo 'OK: | Lançamento estornado | `ReversedEntryId` |'
