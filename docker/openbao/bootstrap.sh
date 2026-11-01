#!/bin/sh
# Inicializa e configura o OpenBao uma vez (ADR-0016). Rodar de novo sobre um cofre configurado não troca nenhuma
# chave: cada passo confere o que já existe antes de criar.
# Ambiente: BAO_ADDR, CUSTOMERS_ROLE_ID, CUSTOMERS_SECRET_ID; BOOTSTRAP_DIR guarda o token root.
set -eu

: "${BAO_ADDR:?BAO_ADDR is required}"
: "${CUSTOMERS_ROLE_ID:?CUSTOMERS_ROLE_ID is required}"
: "${CUSTOMERS_SECRET_ID:?CUSTOMERS_SECRET_ID is required}"
dir="${BOOTSTRAP_DIR:-/openbao/bootstrap}"
here="$(dirname "$0")"

fail() { echo "openbao-bootstrap: $1" >&2; exit 1; }

# `bao status` sai com 0 deslacrado, 2 lacrado e 1 quando o servidor não responde.
wait_status() {
    tries=0
    while :; do
        code=0
        bao status >/dev/null 2>&1 || code=$?
        case " $1 " in *" $code "*) return 0 ;; esac
        tries=$((tries + 1))
        [ "$tries" -lt 120 ] || fail "server at $BAO_ADDR never reached status $1 (last $code)"
        sleep 1
    done
}

mkdir -p "$dir"
chmod 700 "$dir"
wait_status "0 2"

if bao status -format=json | grep -q '"initialized": false'; then
    # Com seal estático, o init gera só a recovery key e o token root; o deslacre é automático.
    bao operator init -recovery-shares=1 -recovery-threshold=1 -format=json >"$dir/init.json"
    chmod 600 "$dir/init.json"
    echo "openbao-bootstrap: initialized"
fi

[ -s "$dir/init.json" ] || fail "server is initialized but $dir/init.json is missing: the root token was lost with its volume"
wait_status "0"

BAO_TOKEN="$(sed -n 's/^ *"root_token": *"\([^"]*\)".*/\1/p' "$dir/init.json")"
[ -n "$BAO_TOKEN" ] || fail "no root_token in $dir/init.json"
export BAO_TOKEN

bao secrets list -format=json | grep -q '"customers/"' || bao secrets enable -path=customers -version=2 kv
bao secrets list -format=json | grep -q '"transit/"' || bao secrets enable transit

# Chaves não exportáveis, criadas só quando ainda não existem: recriar mudaria todo blind index e todo hash.
for key in customers-blind-index customers-request-hash; do
    bao read "transit/keys/$key" >/dev/null 2>&1 || bao write -f "transit/keys/$key" exportable=false >/dev/null
done

bao policy write customers-app "$here/customers-app.hcl" >/dev/null

bao auth list -format=json | grep -q '"approle/"' || bao auth enable approle
bao write auth/approle/role/customers-api \
    token_policies=customers-app token_ttl=1h token_max_ttl=4h secret_id_ttl=0 secret_id_num_uses=0 >/dev/null
bao write auth/approle/role/customers-api/role-id role_id="$CUSTOMERS_ROLE_ID" >/dev/null
# O secret_id fixo só é registrado quando o login com ele ainda falha.
if ! bao write auth/approle/login role_id="$CUSTOMERS_ROLE_ID" secret_id="$CUSTOMERS_SECRET_ID" >/dev/null 2>&1; then
    bao write auth/approle/role/customers-api/custom-secret-id secret_id="$CUSTOMERS_SECRET_ID" >/dev/null
fi

echo "openbao-bootstrap: configured"
