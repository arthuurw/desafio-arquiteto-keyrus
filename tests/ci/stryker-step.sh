#!/usr/bin/env bash
# O CI roda o Stryker.NET da ferramenta versionada sobre o domínio do Ledger e quebra abaixo de 80.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
config="tests/Ledger.UnitTests/stryker-config.json"

fail() { echo "FAIL: $1" >&2; exit 1; }

grep -qE "^[[:space:]]*run:.*dotnet tool run dotnet-stryker --config-file $config\$" "$root/.github/workflows/ci.yml" \
    || fail "ci.yml has no step running dotnet-stryker with $config"
echo "OK: ci.yml runs dotnet-stryker with $config"

grep -qE '"dotnet-stryker":[[:space:]]*\{' "$root/dotnet-tools.json" || fail "dotnet-tools.json does not pin dotnet-stryker"
echo "OK: dotnet-stryker pinned in dotnet-tools.json"

for expected in '"project": "Ledger.csproj"' '"\*\*/Domain/\*\*/\*.cs"' '"break": 80'; do
    grep -qE -- "$expected" "$root/$config" || fail "$config has no $expected"
    echo "OK: $config has $expected"
done
