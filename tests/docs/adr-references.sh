#!/usr/bin/env bash
# Nenhum arquivo versionado fora de docs/adr/ cita um ADR substituído. O conjunto de substituídos vem
# do Status de cada ADR. Antes de varrer o repositório, o script prova que acha uma citação plantada.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

fail() { echo "FAIL: $1" >&2; exit 1; }

# Números dos ADRs com Status "Substituída por ...".
superseded="$(grep -lE '^- \*\*Status:\*\* Substituída por ADR-[0-9]{4}$' "$root"/docs/adr/[0-9][0-9][0-9][0-9]-*.md \
    | sed -E 's|.*/([0-9]{4})-[^/]*$|\1|' | sort -u | paste -sd'|' -)"
[ -n "$superseded" ] || fail "no superseded ADR found in docs/adr"

# Citação: "ADR-NNNN" ou link "adr/NNNN-" em qualquer arquivo; em tabela Markdown, também o número solto.
scan() {
    local base="$1" file pattern="ADR-($superseded)\b|adr/($superseded)-" status
    shift
    for file in "$@"; do
        status=0
        case "$file" in
            *.md) grep -nHE -- "$pattern|^\|.*\b($superseded)\b" "$base/$file" || status=$? ;;
            *) grep -nHE -- "$pattern" "$base/$file" || status=$? ;;
        esac
        # 1 é "sem citação"; um erro do grep não pode passar por isso.
        [ "$status" -le 1 ] || fail "grep failed with status $status on $file"
    done
}

# Prova do detector: uma citação plantada num arquivo temporário precisa ser achada.
probe="$(mktemp -d)"
trap 'rm -rf "$probe"' EXIT
planted="${superseded%%|*}"
printf '# Decisão em ADR-%s.\n' "$planted" >"$probe/probe.sh"
printf '| Pergunta | Resposta | %s |\n' "$planted" >"$probe/probe.md"
[ "$(scan "$probe" probe.sh | wc -l)" -eq 1 ] || fail "detector missed ADR-$planted planted in a script"
[ "$(scan "$probe" probe.md | wc -l)" -eq 1 ] || fail "detector missed $planted planted in a Markdown table"
echo "OK: detector finds a planted citation of $planted"

# Arquivos cobertos: src/, tests/, .github/, docker/, docker-compose.yml, *.md da raiz e docs/ sem docs/adr/.
mapfile -t files < <(git -C "$root" ls-files -- src tests .github docker docker-compose.yml docs ':(glob)*.md' \
    | grep -v '^docs/adr/')
[ "${#files[@]}" -gt 0 ] || fail "no versioned files to scan"

hits="$(scan "$root" "${files[@]}")"
[ -z "$hits" ] || { echo "FAIL: citations of superseded ADRs ($superseded):" >&2; echo "${hits//$root\//}" >&2; exit 1; }

echo "OK: ${#files[@]} files cite no superseded ADR ($superseded)"
