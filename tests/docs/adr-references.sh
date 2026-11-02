#!/usr/bin/env bash
# Nenhum arquivo versionado fora de docs/adr/ cita um ADR substituído. O conjunto de substituídos vem
# do Status de cada ADR. Dentro de docs/adr/, um ADR vigente que cita um substituído diz, na mesma
# linha, qual o substituiu: "ADR-NNNN (substituído pelo ADR-MMMM)", com o sucessor do Status do citado.
# Todo link relativo de um .md para docs/adr/*.md resolve. Antes de cada varredura, o script prova o detector
# em arquivos plantados.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

fail() { echo "FAIL: $1" >&2; exit 1; }

# Números dos ADRs com Status "Substituída por ...", na raiz de docs/adr ou em history/.
superseded="$(grep -lE '^- \*\*Status:\*\* Substituída por ADR-[0-9]{4}$' \
    "$root"/docs/adr/[0-9][0-9][0-9][0-9]-*.md "$root"/docs/adr/history/[0-9][0-9][0-9][0-9]-*.md \
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

# Sucessor direto de um ADR substituído: o número do Status dele.
successor() {
    local file
    for file in "$root"/docs/adr/"$1"-*.md "$root"/docs/adr/history/"$1"-*.md; do
        [ -f "$file" ] || continue
        sed -n 's/^- \*\*Status:\*\* Substituída por ADR-\([0-9]\{4\}\)$/\1/p' "$file"
    done | head -n1
}

# Citações de ADR substituído sem a anotação do sucessor certo, fora da linha `- **Substitui:**`.
unannotated() {
    local file line number cited annotated
    for file in "$@"; do
        while IFS= read -r line; do
            number="${line%%:*}"
            [[ "${line#*:}" == "- **Substitui:** "* ]] && continue
            while IFS= read -r cited; do
                annotated="$(sed -nE 's/^ADR-[0-9]{4} \(substituído pelo ADR-([0-9]{4})\)$/\1/p' <<<"$cited")"
                cited="${cited:4:4}"
                [ "$annotated" = "$(successor "$cited")" ] \
                    || echo "${file#"$root"/}:$number: ADR-$cited needs (substituído pelo ADR-$(successor "$cited"))"
            done < <(grep -oE "ADR-($superseded)\b( \(substituído pelo ADR-[0-9]{4}\))?" <<<"${line#*:}")
        done < <(grep -nE "ADR-($superseded)\b" "$file" || true)
        # Link sem o número no texto: a linha precisa trazer o citado com a anotação do sucessor.
        while IFS= read -r line; do
            number="${line%%:*}"
            [[ "${line#*:}" == "- **Substitui:** "* ]] && continue
            while IFS= read -r cited; do
                cited="${cited: -5:4}"
                grep -qF "ADR-$cited (substituído pelo ADR-$(successor "$cited"))" <<<"${line#*:}" \
                    || echo "${file#"$root"/}:$number: link to ADR-$cited needs (substituído pelo ADR-$(successor "$cited"))"
            done < <(grep -oE "\]\(([^)]*/)?($superseded)-" <<<"${line#*:}")
        done < <(grep -nE "\]\(([^)]*/)?($superseded)-" "$file" || true)
    done
}

# Prova do detector: sem anotação e com o sucessor errado, acha; com o sucessor certo, aceita.
right="$(successor "$planted")"
printf 'Decisão em ADR-%s.\n' "$planted" >"$probe/bare.md"
printf 'Decisão em ADR-%s (substituído pelo ADR-9999).\n' "$planted" >"$probe/wrong.md"
printf 'Decisão em ADR-%s (substituído pelo ADR-%s).\n- **Substitui:** ADR-%s\n' "$planted" "$right" "$planted" >"$probe/right.md"
[ "$(unannotated "$probe/bare.md" | wc -l)" -eq 1 ] || fail "detector missed an unannotated ADR-$planted"
[ "$(unannotated "$probe/wrong.md" | wc -l)" -eq 1 ] || fail "detector missed ADR-$planted annotated with the wrong successor"
[ -z "$(unannotated "$probe/right.md")" ] || fail "detector refused ADR-$planted annotated with ADR-$right"
printf 'Ver [a decisão](history/%s-x.md).\n' "$planted" >"$probe/bare-link.md"
printf 'Ver [ADR-%s (substituído pelo ADR-%s)](history/%s-x.md).\n' "$planted" "$right" "$planted" >"$probe/right-link.md"
[ "$(unannotated "$probe/bare-link.md" | wc -l)" -eq 1 ] || fail "detector missed a bare link to ADR-$planted"
[ -z "$(unannotated "$probe/right-link.md")" ] || fail "detector refused a link to ADR-$planted annotated with ADR-$right"
echo "OK: detector finds an unannotated citation, a wrong successor and a bare link, and accepts ADR-$planted (substituído pelo ADR-$right)"

# ADRs vigentes: a raiz de docs/adr, sem o índice.
current=()
for file in "$root"/docs/adr/[0-9][0-9][0-9][0-9]-*.md; do
    grep -qxF -- '- **Status:** Aceita' "$file" && current+=("$file")
done
[ "${#current[@]}" -gt 0 ] || fail "no current ADR in docs/adr"
hits="$(unannotated "${current[@]}")"
[ -z "$hits" ] || { echo "FAIL: citations of superseded ADRs without the successor:" >&2; echo "$hits" >&2; exit 1; }
echo "OK: ${#current[@]} current ADRs annotate every citation of a superseded ADR with its successor"

# Links relativos de Markdown que apontam para docs/adr/*.md e não resolvem.
broken_links() {
    local base="$1" file target resolved
    shift
    for file in "$@"; do
        while IFS= read -r target; do
            target="${target%%#*}"
            resolved="$(realpath -m --relative-to="$base" "$base/$(dirname "$file")/$target")"
            [[ "$resolved" == docs/adr/*.md ]] || continue
            [ -f "$base/$resolved" ] || echo "$file: $target"
        done < <(grep -oE '\]\([^):]+\.md(#[^)]*)?\)' "$base/$file" | sed -E 's/^\]\(//; s/\)$//' || true)
    done
}

mkdir -p "$probe/repo/docs/adr" "$probe/repo/docs/guias"
printf '# ADR\n' >"$probe/repo/docs/adr/9001-existe.md"
printf '[ok](../adr/9001-existe.md) e [quebrado](../adr/9002-falta.md)\n' >"$probe/repo/docs/guias/guia.md"
[ "$(broken_links "$probe/repo" docs/guias/guia.md)" = "docs/guias/guia.md: ../adr/9002-falta.md" ] \
    || fail "link detector did not find exactly the planted broken link"
echo "OK: link detector finds a planted broken link to docs/adr"

mapfile -t markdown < <(git -C "$root" ls-files -- '*.md')
hits="$(broken_links "$root" "${markdown[@]}")"
[ -z "$hits" ] || { echo "FAIL: links to docs/adr that do not resolve:" >&2; echo "$hits" >&2; exit 1; }
echo "OK: every link from ${#markdown[@]} Markdown files to docs/adr resolves"
