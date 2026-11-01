#!/usr/bin/env bash
# Afirmações da documentação que o código sustenta.
#   guides: o guia de testes descreve o Money e as fronteiras como o código faz, o passo de mutação
#           do CI cita o ADR que define o limiar, e os guias não marcam como previsto o que a F5 entrega.
#   readme: o README documenta a recriação do volume, as portas do compose, o contrato em Development,
#           o aviso sobre o volume do OpenBao e o seed da ana e do bruno.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

fail() { echo "FAIL: $1" >&2; exit 1; }

has() { grep -qF -- "$2" "$1" || fail "${1#"$root"/} has no '$2'"; echo "OK: ${1#"$root"/} has '$2'"; }

# Ausência exige o código 1 do grep: um erro do grep (2 ou mais) não passa por "não encontrado".
lacks() {
    local status=0
    grep -qF -- "$2" "$1" || status=$?
    [ "$status" -ne 0 ] || fail "${1#"$root"/} still has '$2'"
    [ "$status" -eq 1 ] || fail "grep failed with status $status on ${1#"$root"/}"
    echo "OK: ${1#"$root"/} lacks '$2'"
}

guides() {
    local guide="$root/docs/guias/testes-explicados.md" ci="$root/.github/workflows/ci.yml" comment
    lacks "$guide" 'rejeita operação entre moedas'
    lacks "$guide" 'moedas diferentes'
    lacks "$guide" 'quebra o build'
    lacks "$guide" 'o build quebra'
    grep -qF -- '`Money` só aceita BRL' "$guide" || fail "testes-explicados.md does not say \`Money\` só aceita BRL"
    echo "OK: testes-explicados.md says \`Money\` só aceita BRL"
    grep -qE -- 'fronteira.*testes de arquitetura' "$guide" \
        || fail "testes-explicados.md does not say a broken boundary fails the testes de arquitetura"
    echo "OK: testes-explicados.md says a broken boundary fails the testes de arquitetura"

    # Comentário imediatamente acima do passo de mutação.
    comment="$(grep -B1 -E '^[[:space:]]*- name: Mutation - Ledger domain$' "$ci" | head -n1)"
    [[ "$comment" =~ ^[[:space:]]*# ]] || fail "ci.yml has no comment above the Mutation - Ledger domain step"
    [[ "$comment" == *ADR-0034* ]] || fail "mutation step comment does not cite ADR-0034: $comment"
    [[ "$comment" != *ADR-0023* ]] || fail "mutation step comment still cites ADR-0023: $comment"
    echo "OK: mutation step comment cites ADR-0034"

    # O que a F5 entrega deixa de ser previsto nos guias.
    local crypto="$root/docs/guias/criptografia-explicada.md"
    lacks "$crypto" 'o blind index e o mascaramento estão previstos'
    lacks "$crypto" 'previsto, F5'
    lacks "$crypto" 'prevista no F5'
    lacks "$crypto" 'previstos no F5'
    lacks "$crypto" 'O OpenBao entra no projeto no F5'
    lacks "$crypto" 'customers/dek/{customerId}'
    has "$crypto" 'customers/data/dek/{customerId}'
    has "$crypto" 'customers-request-hash'
    has "$crypto" '/personal-data'
    lacks "$root/docs/guias/resiliencia-explicada.md" 'Previsto (F5'
    lacks "$root/docs/guias/qualidade-e-ci-explicados.md" 'quando ele chegar (F5)'
}

readme() {
    local readme="$root/README.md" pair var default line
    has "$readme" 'docker compose down -v'
    has "$readme" '127.0.0.1'
    for pair in "POSTGRES_PORT 5432" "KEYCLOAK_PORT 8180" "OPENBAO_PORT 8200" "API_PORT 8080" "WORKER_PORT 8081"; do
        read -r var default <<<"$pair"
        line="$(grep -F -- "\`$var\`" "$readme" | grep -F -- "$default" || true)"
        [ -n "$line" ] || fail "README.md has no line with \`$var\` and its default $default"
        echo "OK: README.md documents \`$var\` with default $default"
    done
    has "$readme" '/openapi/v1.json'
    has "$readme" '/scalar'
    has "$readme" 'Development'
    has "$readme" 'Nunca recrie só o volume do OpenBao (`openbao-data`) com clientes cadastrados no PostgreSQL.'
    has "$readme" 'Customers__Seed__Enabled'
    has "$readme" '`ana` (`11111111-1111-4111-8111-111111111111`)'
    has "$readme" '(`22222222-2222-4222-8222-222222222222`) do realm'
}

case "${1:-}" in
    guides) guides ;;
    readme) readme ;;
    *) fail "usage: doc-claims.sh guides|readme" ;;
esac
