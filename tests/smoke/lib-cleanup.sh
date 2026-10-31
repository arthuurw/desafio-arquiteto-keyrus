#!/usr/bin/env bash
# Um smoke que termina com sucesso ou com falha apaga o diretório de smoke_tempdir e derruba o compose do projeto.
set -euo pipefail

lib="$(cd "$(dirname "$0")" && pwd)/lib.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

# Dublê de borda: o docker só registra a chamada, sem subir contêiner nenhum.
mkdir -p "$work/bin"
cat > "$work/bin/docker" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_DOCKER_LOG"
EOF
chmod +x "$work/bin/docker"

# Gera um smoke no formato dos reais: clona, entra no clone, roda o compose e termina com o final dado.
write_smoke() {
    local name="$1" ending="$2"
    cat > "$work/$name.sh" <<EOF
#!/usr/bin/env bash
set -euo pipefail
source "$lib"
smoke_clone
[ -d "\$SMOKE_TMP" ] || fail "smoke_tempdir did not create \$SMOKE_TMP"
echo "\$SMOKE_TMP" > "$work/$name.tmpdir"
cd "\$SMOKE_DIR"
docker compose -p "\$SMOKE_PROJECT" up -d
$ending
EOF
}

# Roda o smoke e confere o código de saída, a remoção do diretório e o `down -v` do compose.
run_smoke() {
    local name="$1" expected_exit="$2" status=0 dir
    : > "$work/$name.docker.log"
    FAKE_DOCKER_LOG="$work/$name.docker.log" PATH="$work/bin:$PATH" bash "$work/$name.sh" >/dev/null 2>&1 || status=$?
    [ "$status" = "$expected_exit" ] || fail "$name exited with $status, expected $expected_exit"

    dir="$(cat "$work/$name.tmpdir" 2>/dev/null || true)"
    [ -n "$dir" ] || fail "$name did not record its temp directory"
    [ ! -e "$dir" ] || fail "$name left $dir on disk"
    grep -qE '^compose -p ledger-smoke-[0-9]+ down -v' "$work/$name.docker.log" \
        || fail "$name did not run docker compose down -v"
    echo "OK: $name exited with $status, removed its temp directory and ran docker compose down -v"
}

write_smoke success 'echo done'
write_smoke failure 'fail "failing on purpose"'
write_smoke command-error 'false'

run_smoke success 0
run_smoke failure 1
run_smoke command-error 1
