#!/usr/bin/env bash
# O glossário define os termos da auditoria e o dado pessoal classificado com o nome usado no código, e a leitura dos
# dados pessoais completos cita a trilha de auditoria, não o log `Audit` que ela substituiu.
set -euo pipefail
glossary="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/docs/glossario.md"

fail() { echo "FAIL: $1" >&2; exit 1; }

# Só a seção dada: do título até o próximo título de seção.
section() {
    awk -v title="## $1" '/^## /{ inside = ($0 == title) } inside' "$glossary"
}

audit="$(section Auditoria)"
customers="$(section Customers)"
[ -n "$audit" ] || fail "glossary has no ## Auditoria section"
[ -n "$customers" ] || fail "glossary has no ## Customers section"

row() {
    local name="$1" text="$2" prefix="$3"
    grep -qF -- "$prefix" <<<"$text" || fail "$name section has no row starting with $prefix"
    echo "OK: $name: $prefix"
}

row Auditoria "$audit" '| Registro de auditoria | `AuditEvent` |'
row Auditoria "$audit" '| Trilha de auditoria | `AuditTrail` |'
row Customers "$customers" '| Dado pessoal classificado | `PersonalData` |'

complete="$(grep -F -- '| Dados pessoais completos | `personal-data` |' <<<"$customers" || true)"
[ -n "$complete" ] || fail "Customers section has no Dados pessoais completos row"
grep -qF -- 'trilha de auditoria' <<<"$complete" || fail "Dados pessoais completos does not cite the trilha de auditoria"
status=0
grep -qF -- 'log `Audit`' <<<"$complete" || status=$?
[ "$status" -ne 0 ] || fail "Dados pessoais completos still cites the log \`Audit\`"
[ "$status" -eq 1 ] || fail "grep failed with status $status on the Dados pessoais completos row"
echo "OK: Dados pessoais completos cites the trilha de auditoria, not the log \`Audit\`"
