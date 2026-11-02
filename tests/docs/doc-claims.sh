#!/usr/bin/env bash
# Afirmações da documentação que o código sustenta.
#   guides: o guia de testes descreve o Money e as fronteiras como o código faz, o passo de mutação
#           do CI cita o ADR que define o limiar, os guias não marcam como previsto o que a F5 e a F6
#           entregam, e o guia de resiliência conta os mecanismos e os retries que o código tem.
#   map:    o mapa de decisões só cita o CodeQL como previsto (F10).
#   flows:  os fluxos mostram a espera do lock e o `409` das leituras com instante, a recusa do `to` no
#           futuro do extrato, o `413` do corpo
#           grande, a linha `read-customer` da leitura mascarada e o relay que conclui o lote e isola o
#           evento recusado.
#   readme: o README documenta a recriação do volume, as portas do compose, o contrato em Development,
#           o aviso sobre o volume do OpenBao e o seed da ana e do bruno.
#   audit:  os fluxos de cada rota auditada citam a trilha, melhorias-futuras.md lista a limpeza e a consulta dela, o
#           guia de criptografia mostra a consulta por SQL, e o contrato descreve a leitura completa com a trilha.
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

    # O que a F5 e a F6 entregam não é previsto em guia nenhum; o complemento do F6 continua previsto.
    local file stale
    for file in "$root"/docs/guias/*.md; do
        stale="$(grep -nE 'previst[oa]s?[^.|]*F[56]' "$file" | grep -vF 'complemento do F6' || true)"
        [ -z "$stale" ] || fail "${file#"$root"/} still marks F5 or F6 work as previsto: $stale"
    done
    echo "OK: no guide marks F5 or F6 work as previsto"

    # Mecanismos e retries do guia de resiliência, cada um conferido no código que o implementa.
    local resilience="$root/docs/guias/resiliencia-explicada.md" pair code marker
    has "$resilience" 'hoje já existem sete mecanismos'
    lacks "$resilience" 'quatro mecanismos'
    has "$resilience" 'Hoje há três retries no sistema'
    lacks "$resilience" 'único retry'
    for pair in \
        "src/Modules/Ledger/Ledger/Infrastructure/Persistence/AccountWrites.cs|SET LOCAL lock_timeout = '2s'" \
        "src/Modules/Positions/Positions/PositionsModule.cs|DefaultReadTimeout = TimeSpan.FromMilliseconds(300)" \
        "src/Modules/Customers/Customers/Infrastructure/Vault/OpenBaoClient.cs|DefaultTimeout = TimeSpan.FromSeconds(1)" \
        "src/Modules/Ledger/Ledger/Infrastructure/Outbox/OutboxRelay.cs|FirstBackoff = TimeSpan.FromSeconds(1)" \
        "src/BuildingBlocks/Http/RequestBodyLimit.cs|MaxBytes = 16 * 1024" \
        "src/Modules/Ledger/Ledger/Infrastructure/Outbox/OutboxRelay.cs|RelayBatchAsync(after, CancellationToken.None)" \
        "src/Hosts/Api/Program.cs|/health/live" \
        "src/Hosts/Worker/Program.cs|/health/live" \
        "src/Modules/Customers/Customers/Infrastructure/Vault/OpenBaoClient.cs|TokenAsync(renew: true" \
        "src/Modules/Customers/Customers/Infrastructure/Seed/CustomerSeed.cs|RetryEvery = TimeSpan.FromSeconds(2)"; do
        code="${pair%%|*}"
        marker="${pair#*|}"
        has "$root/$code" "$marker"
    done
}

map() {
    local map="$root/docs/mapa-de-decisoes.md" line
    has "$map" 'CodeQL previsto (F10)'
    while IFS= read -r line; do
        [[ "$line" == *"CodeQL previsto (F10)"* ]] || fail "mapa-de-decisoes.md cites CodeQL as existing: $line"
    done < <(grep -F 'CodeQL' "$map")
    echo "OK: mapa-de-decisoes.md cites CodeQL only as previsto (F10)"
}

flows() {
    local ledger="$root/docs/arquitetura/fluxos-ledger.md" customers="$root/docs/arquitetura/fluxos-customers.md"
    local readme="$root/docs/arquitetura/README.md" pair file title marker text
    for pair in \
        "$ledger|Saldo atual e saldo em um momento|FOR KEY SHARE" \
        "$ledger|Saldo atual e saldo em um momento|409 concurrency-timeout" \
        "$ledger|Extrato|FOR KEY SHARE" \
        "$ledger|Extrato|409 concurrency-timeout"         "$ledger|Extrato|alt to depois do relógio lido"         "$ledger|Extrato|400 validation-error em errors.to" \
        "$ledger|Troca do limite de crédito|413 payload-too-large" \
        "$readme|Pipeline comum de escrita (\`POST\`)|413 payload-too-large" \
        "$customers|Leitura mascarada e leitura completa|read-customer" \
        "$ledger|Do outbox ao MongoDB (Worker)|Rejected" \
        "$ledger|Do outbox ao MongoDB (Worker)|no encerramento do Worker, o lote em andamento roda até o COMMIT"; do
        IFS='|' read -r file title marker <<<"$pair"
        text="$(section_of "$file" "$title")"
        [ -n "$text" ] || fail "${file#"$root"/} has no ## $title section"
        grep -qF -- "$marker" <<<"$text" || fail "${file#"$root"/} section '$title' has no '$marker'"
        echo "OK: ${file#"$root"/} section '$title' has '$marker'"
    done
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

# Seção `## $2` do arquivo `$1`: do título até o próximo título de seção.
section_of() {
    awk -v title="## $2" '/^## /{ inside = ($0 == title) } inside' "$1"
}

audit() {
    local ledger="$root/docs/arquitetura/fluxos-ledger.md" customers="$root/docs/arquitetura/fluxos-customers.md" pair file title text
    for pair in "$ledger|Crédito e débito" "$ledger|Estorno" "$ledger|Abertura de conta" "$ledger|Troca do limite de crédito"         "$customers|Cadastro de cliente" "$customers|Leitura mascarada e leitura completa"; do
        file="${pair%%|*}"
        title="${pair#*|}"
        text="$(section_of "$file" "$title")"
        [ -n "$text" ] || fail "${file#"$root"/} has no ## $title section"
        grep -qF -- 'audit_events' <<<"$text" || fail "${file#"$root"/} section '$title' does not cite audit_events"
        echo "OK: ${file#"$root"/} section '$title' cites audit_events"
    done

    local improvements="$root/docs/melhorias-futuras.md" guide="$root/docs/guias/criptografia-explicada.md"
    has "$improvements" '| Limpeza da trilha de auditoria ao fim do prazo legal |'
    has "$improvements" '| Consulta da trilha de auditoria por role de leitura ou rota própria |'
    has "$guide" 'FROM customers.audit_events'
    has "$guide" 'FROM ledger.audit_events'

    local contract="$root/docs/api/openapi-v1.json" description
    description="$(jq -r '.paths["/v1/customers/{id}/personal-data"].get.description' "$contract" | tr -d '')"
    [[ "$description" == *"trilha de auditoria"* ]] || fail "openapi-v1.json personal-data description does not cite the trilha de auditoria"
    [[ "$description" != *"log de auditoria"* ]] || fail "openapi-v1.json personal-data description still cites the log de auditoria"
    echo "OK: openapi-v1.json describes personal-data with the trilha de auditoria"
}

case "${1:-}" in
    guides) guides ;;
    readme) readme ;;
    audit) audit ;;
    map) map ;;
    flows) flows ;;
    *) fail "usage: doc-claims.sh guides|readme|audit|map|flows" ;;
esac
