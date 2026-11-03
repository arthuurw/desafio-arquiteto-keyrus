#!/usr/bin/env bash
# A auditoria NuGet vale no CI e no build local; as imagens publicam sem ela, para um alerta novo não
# impedir o compose de subir (ADR-0055).
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
fail() { echo "FAIL: $*" >&2; exit 1; }

props="$root/Directory.Build.props"
for setting in '<NuGetAudit>true</NuGetAudit>' '<NuGetAuditLevel>low</NuGetAuditLevel>' '<NuGetAuditMode>all</NuGetAuditMode>'; do
    grep -qF "$setting" "$props" || fail "Directory.Build.props lacks $setting"
    echo "OK: Directory.Build.props has $setting"
done

for host in Api Worker Migrator; do
    dockerfile="$root/src/Hosts/$host/Dockerfile"
    publish="$(grep -E '^RUN dotnet publish ' "$dockerfile")" || fail "$host Dockerfile has no dotnet publish"
    grep -qE -- '(^| )-p:NuGetAudit=false( |$)' <<< "$publish" || fail "$host Dockerfile publishes with the NuGet audit: $publish"
    echo "OK: $host image publishes with -p:NuGetAudit=false"
done
