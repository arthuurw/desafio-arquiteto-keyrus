#!/usr/bin/env bash
# O glossário define, na seção Positions, os termos do modelo de leitura com o nome usado no código.
set -euo pipefail
glossary="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/docs/glossario.md"

fail() { echo "FAIL: $1" >&2; exit 1; }

# Só a seção Positions: do título até o próximo título de seção.
positions="$(awk '/^## /{ inside = ($0 == "## Positions") } inside' "$glossary")"
[ -n "$positions" ] || fail "glossary has no ## Positions section"

row() {
    local prefix="$1"
    grep -qF -- "$prefix" <<<"$positions" || fail "Positions section has no row starting with $prefix"
    echo "OK: $prefix"
}

row '| Outbox | `OutboxEvent` |'
row '| Evento pendente | `claim_outbox_batch` |'
row '| Evento processado | `mark_outbox_processed` |'
row '| Projeção | `PositionProjection` |'
row '| Versão projetada | `Position.Version` |'
row '| Fallback | `FreshRead` |'
row '| Cursor do extrato | `StatementCursor` |'
