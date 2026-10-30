# Funções comuns dos testes de fumaça: cada um roda o docker compose sobre um clone limpo de HEAD.

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

# Clona o commit atual em diretório temporário: só o que está versionado, sem .env nem build local.
smoke_clone() {
    local repo
    repo="$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse --show-toplevel)"
    SMOKE_DIR="$(mktemp -d)"
    SMOKE_PROJECT="ledger-smoke-$$"
    git clone --quiet "$repo" "$SMOKE_DIR/repo"
    git -C "$SMOKE_DIR/repo" checkout --quiet "$(git -C "$repo" rev-parse HEAD)"
    SMOKE_DIR="$SMOKE_DIR/repo"
    trap smoke_cleanup EXIT
}

smoke_cleanup() {
    (cd "$SMOKE_DIR" && docker compose -p "$SMOKE_PROJECT" down -v --remove-orphans >/dev/null 2>&1) || true
}

# Espera `200` com corpo `Healthy` na URL, por até 180 s.
wait_healthy() {
    local url="$1" body status
    for _ in $(seq 1 90); do
        body="$(curl -s -o - -w '\n%{http_code}' "$url" 2>/dev/null || true)"
        status="${body##*$'\n'}"
        body="${body%$'\n'*}"
        if [ "$status" = "200" ] && [ "$body" = "Healthy" ]; then
            echo "OK: $url -> 200 Healthy"
            return 0
        fi
        sleep 2
    done
    fail "$url did not answer 200 Healthy (last: status=$status body=$body)"
}

# Espera o documento de descoberta OpenID na URL, por até 180 s, e confere o `issuer`.
wait_issuer() {
    local url="$1" expected="$2" body issuer
    for _ in $(seq 1 90); do
        body="$(curl -sf "$url" 2>/dev/null || true)"
        if [ -n "$body" ]; then
            issuer="$(printf '%s' "$body" | sed -n 's/.*"issuer":"\([^"]*\)".*/\1/p')"
            [ "$issuer" = "$expected" ] || fail "$url issuer was '$issuer', expected '$expected'"
            echo "OK: $url -> issuer $issuer"
            return 0
        fi
        sleep 2
    done
    fail "$url did not serve the discovery document"
}
