#!/usr/bin/env bash
# O contexto de build do Docker não leva segredos locais, saídas de teste nem arquivos de agente.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
# Contexto descartável com a cópia do .dockerignore: a árvore real, que pode ter um .env de verdade, não é tocada.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
ctx="$work/ctx"
mkdir -p "$ctx"
cp "$root/.dockerignore" "$ctx/.dockerignore"

fail() { echo "FAIL: $*" >&2; exit 1; }

sentinels=(
    ".env"
    ".env.local"
    "x/StrykerOutput/sentinel.txt"
    "x/TestResults/sentinel.txt"
    ".claude/sentinel.txt"
    ".specs/sentinel.txt"
)
for path in "${sentinels[@]}" control.txt; do
    mkdir -p "$ctx/$(dirname "$path")"
    echo sentinel > "$ctx/$path"
done

# O primeiro estágio lista o contexto copiado; o segundo exporta só a listagem para o disco.
cat > "$work/Dockerfile" <<'EOF'
FROM busybox:1.37.0@sha256:a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec AS list
COPY . /ctx
RUN cd /ctx && find . -type f | sort > /listing.txt

FROM scratch
COPY --from=list /listing.txt /
EOF

docker build --quiet -f "$work/Dockerfile" --output "type=local,dest=$work/out" "$ctx" >/dev/null
listing="$(tr -d '\r' < "$work/out/listing.txt")"

# Arquivo de controle: sem ele na listagem, o teste passaria sem ter visto o contexto.
grep -qxF "./control.txt" <<< "$listing" || fail "control file missing from the build context listing:
$listing"
echo "OK: control.txt reached the build context"

for path in "${sentinels[@]}"; do
    if grep -qxF "./$path" <<< "$listing"; then
        fail "$path reached the build context"
    fi
    echo "OK: $path excluded"
done
