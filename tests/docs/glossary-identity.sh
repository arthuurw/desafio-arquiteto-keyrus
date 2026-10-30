#!/usr/bin/env bash
# O glossário define os termos de identidade usados pelo código: chamador, modos e escopo.
set -euo pipefail
glossary="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/docs/glossario.md"

for row in '| Chamador | `Caller` |' '| Modo delegado | `Delegated` |' '| Modo serviço | `Service` |' '| Escopo | `Scope` |'; do
    grep -qF -- "$row" "$glossary" || { echo "FAIL: glossary has no row starting with $row" >&2; exit 1; }
    echo "OK: $row"
done
