#!/usr/bin/env bash
# C31: toda linha `uses:` dos workflows referencia a action por SHA de commit de 40 caracteres.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
lines="$(grep -rhE '^[[:space:]]*-?[[:space:]]*uses:' "$root/.github/workflows" || true)"
[ -n "$lines" ] || { echo "FAIL: no uses: lines found" >&2; exit 1; }

unpinned="$(printf '%s\n' "$lines" | grep -vE 'uses:[[:space:]]*[^@[:space:]]+@[0-9a-f]{40}([[:space:]]|$)' || true)"
[ -z "$unpinned" ] || { echo "FAIL: not pinned by SHA:" >&2; echo "$unpinned" >&2; exit 1; }

echo "OK: $(printf '%s\n' "$lines" | wc -l | tr -d ' ') uses pinned by SHA"
