#!/usr/bin/env bash
# Cada ADR novo substitui um antigo: o antigo aponta para ele, e o índice e o mapa de decisões citam o novo.
# O novo está Aceita, ou foi substituído por sua vez num par seguinte. Com "content", confere um marcador
# de cada decisão no ADR novo.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
adr="$root/docs/adr"
mode="${1:-structure}"

fail() { echo "FAIL: $1" >&2; exit 1; }

# Pares "novo antigo", na ordem em que foram substituídos.
pairs=("0030 0008" "0031 0010" "0032 0014" "0033 0015" "0034 0024" "0037 0032" "0038 0033" "0039 0020" "0042 0039" "0043 0038")

# Único arquivo do ADR com o número dado.
adr_file() {
    local files=("$adr/$1"-*.md)
    [ "${#files[@]}" -eq 1 ] && [ -f "${files[0]}" ] || fail "expected exactly one docs/adr/$1-*.md"
    printf '%s\n' "${files[0]}"
}

# Verdadeiro quando algum par tem este ADR como antigo.
replaced_later() {
    local pair
    for pair in "${pairs[@]}"; do
        [ "${pair#* }" != "$1" ] || return 0
    done
    return 1
}

structure() {
    local index="$adr/README.md" map="$root/docs/mapa-de-decisoes.md" pair new old new_file old_file status
    for pair in "${pairs[@]}"; do
        read -r new old <<<"$pair"
        new_file="$(adr_file "$new")"
        old_file="$(adr_file "$old")"

        grep -qxF -- "- **Substitui:** ADR-$old" "$new_file" || fail "ADR-$new does not declare it replaces ADR-$old"
        grep -qxF -- "- **Status:** Substituída por ADR-$new" "$old_file" || fail "ADR-$old is not Substituída por ADR-$new"
        grep -qE "^\| \[$old\]\($old-[^)]+\.md\) \| .+ \| Substituída por ADR-$new \|$" "$index" \
            || fail "index row for $old is not Substituída por ADR-$new"

        # Novo já substituído: o par seguinte confere ele como antigo, e o mapa cita só o vigente.
        if replaced_later "$new"; then
            echo "OK: ADR-$new replaces ADR-$old, and is replaced in turn"
            continue
        fi

        grep -qxF -- '- **Status:** Aceita' "$new_file" || fail "ADR-$new is not Aceita"
        grep -qE "^\| \[$new\]\($new-[^)]+\.md\) \| .+ \| Aceita \|$" "$index" || fail "index has no Aceita row for $new"
        grep -qF -- "ADR-$new" "$map" || fail "mapa-de-decisoes.md does not cite ADR-$new"
        grep -qF -- "| $new |" "$map" || fail "mapa-de-decisoes.md has no question row for $new"
        status=0
        grep -qE -- "ADR-$old\b|\| $old \|" "$map" || status=$?
        [ "$status" -ne 0 ] || fail "mapa-de-decisoes.md still cites $old"
        [ "$status" -eq 1 ] || fail "grep failed with status $status on mapa-de-decisoes.md"

        echo "OK: ADR-$new replaces ADR-$old"
    done
}

content() {
    local pair new file marker
    for pair in "0030 LedgerInput" "0031 Outcome" "0031 LedgerModule" "0032 RequestHash" "0032 credit-limit" \
        "0032 batch_size" "0033 ledger_worker" "0033 UPDATE (" "0033 SECURITY DEFINER" "0034 mutations:" "0034 80" \
        "0036 ICustomersCommands" "0037 customers-request-hash" "0037 batch_size" "0038 customers_worker" "0038 UPDATE (" \
        "0038 SECURITY DEFINER" "0039 personal-data" "0042 personal-data" "0042 trilha de auditoria" \
        "0043 customers_worker" "0043 UPDATE (" "0043 SECURITY DEFINER" "0043 audit_events" "0043 reject_audit_change"; do
        new="${pair%% *}"
        marker="${pair#* }"
        file="$(adr_file "$new")"
        grep -qF -- "$marker" "$file" || fail "ADR-$new has no '$marker'"
        echo "OK: ADR-$new has '$marker'"
    done
}

case "$mode" in
    structure) structure ;;
    content) content ;;
    *) fail "usage: adr-superseded.sh [content]" ;;
esac
