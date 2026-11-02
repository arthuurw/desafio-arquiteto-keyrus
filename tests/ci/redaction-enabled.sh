#!/usr/bin/env bash
# A Api e o Worker ligam a redação de dado pessoal pelo mesmo registro do BuildingBlocks, e o registro apaga o valor
# classificado: um host que deixasse de chamá-lo logaria [PersonalData] em claro.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
registration="$root/src/BuildingBlocks/Logging/PersonalDataRedaction.cs"

fail() { echo "FAIL: $1" >&2; exit 1; }

# Linha de código (sem comentário) com o texto dado.
calls() {
    grep -vE '^[[:space:]]*//' "$1" | grep -qF -- "$2"
}

for marker in 'public static IHostApplicationBuilder AddPersonalDataRedaction(' 'SetRedactor<ErasingRedactor>(' \
    'PersonalDataTaxonomy.PersonalData' 'builder.Logging.EnableRedaction();'; do
    calls "$registration" "$marker" || fail "PersonalDataRedaction.cs has no '$marker'"
    echo "OK: PersonalDataRedaction.cs has '$marker'"
done

for host in Api Worker; do
    program="$root/src/Hosts/$host/Program.cs"
    calls "$program" 'builder.AddPersonalDataRedaction();' || fail "$host Program.cs does not call builder.AddPersonalDataRedaction()"
    calls "$program" 'using BuildingBlocks.Logging;' || fail "$host Program.cs does not use BuildingBlocks.Logging"
    echo "OK: $host Program.cs calls builder.AddPersonalDataRedaction()"
done
