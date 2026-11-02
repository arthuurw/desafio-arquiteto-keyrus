#!/usr/bin/env bash
# Todo `FROM` dos Dockerfiles dos hosts, todo `image:` do docker-compose.yml e toda imagem que os testes sobem pelo
# Testcontainers (`const string Image` em tests/) têm tag de versão exata e digest sha256.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
files=(
    "$root/src/Hosts/Api/Dockerfile"
    "$root/src/Hosts/Worker/Dockerfile"
    "$root/src/Hosts/Migrator/Dockerfile"
    "$root/docker-compose.yml"
)

# Tag de versão (ex.: 10.0.302, 18.6-alpine) seguida do digest do índice multi-arquitetura.
pinned='^[^@[:space:]]+:[0-9]+(\.[0-9]+)+(-[A-Za-z0-9.]+)?@sha256:[0-9a-f]{64}$'

# Lista as referências de imagem de um arquivo, uma por linha: o primeiro argumento de `FROM` que não é opção, o valor
# de `image:`, ou a constante `Image` de um arquivo C#. Estágios nomeados (`FROM build`) não são imagens e ficam de fora.
image_refs() {
    case "$1" in
        *.cs)
            sed -n 's/.*const string Image = "\([^"]*\)".*/\1/p' "$1" | tr -d '\r'
            return
            ;;
    esac
    awk '
        tolower($1) == "from" {
            for (i = 2; i <= NF; i++) if ($i !~ /^--/) break
            ref = $i
            if (!(ref in stages)) print ref
            for (j = i + 1; j < NF; j++) if (tolower($j) == "as") stages[$(j + 1)] = 1
        }
        $1 == "image:" || $1 == "-" && $2 == "image:" { print $NF }
    ' "$1" | tr -d '"'"'"'\r'
}

# Sai com 1 e lista as referências sem tag exata e digest; imprime o total verificado.
check_files() {
    local refs bad
    refs="$(for f in "$@"; do image_refs "$f"; done)"
    [ -n "$refs" ] || { echo "FAIL: no image references found" >&2; return 1; }
    bad="$(printf '%s\n' "$refs" | grep -vE "$pinned" || true)"
    [ -z "$bad" ] || { echo "FAIL: not pinned by exact tag and digest:" >&2; echo "$bad" >&2; return 1; }
    printf '%s\n' "$refs" | wc -l | tr -d ' '
}

# Prova do próprio script: uma imagem flutuante sem digest precisa ser recusada, num Dockerfile e num teste.
probe_dir="$(mktemp -d)"
trap 'rm -rf "$probe_dir"' EXIT
printf 'FROM mcr.microsoft.com/dotnet/aspnet:10.0\n' > "$probe_dir/Dockerfile"
if check_files "$probe_dir/Dockerfile" >/dev/null 2>&1; then
    echo "FAIL: the check accepted an image without digest" >&2
    exit 1
fi
printf '    internal const string Image = "mongo:8.0.34";\n' > "$probe_dir/Probe.cs"
if check_files "$probe_dir/Probe.cs" >/dev/null 2>&1; then
    echo "FAIL: the check accepted a Testcontainers image with only a tag" >&2
    exit 1
fi
echo "OK: image without digest is rejected, in a Dockerfile and in a test"

# As imagens dos testes de integração: as constantes `Image` versionadas em tests/.
mapfile -t test_files < <(git -C "$root" grep -l 'const string Image = "' -- 'tests/*.cs' | sed "s|^|$root/|")
[ "${#test_files[@]}" -ge 4 ] || { echo "FAIL: found ${#test_files[@]} Testcontainers image constants in tests/, expected 4" >&2; exit 1; }
files+=("${test_files[@]}")

count="$(check_files "${files[@]}")"
echo "OK: $count image references pinned by exact tag and digest"

# O cofre também: as DEKs dependem da versão do OpenBao que as gravou.
openbao="$(image_refs "$root/docker-compose.yml" | grep -c '^openbao/openbao:' || true)"
[ "$openbao" -ge 2 ] || { echo "FAIL: docker-compose.yml has $openbao openbao/openbao references, expected the server and the bootstrap" >&2; exit 1; }
echo "OK: openbao and openbao-bootstrap images pinned"
