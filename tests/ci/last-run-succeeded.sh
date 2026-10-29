#!/usr/bin/env bash
# C30: a última execução do ci.yml na branch, disparada por push, terminou com success sobre o HEAD local.
set -euo pipefail

branch="${1:?usage: last-run-succeeded.sh <branch>}"
head="$(git rev-parse HEAD)"
run="$(gh run list --workflow ci.yml --branch "$branch" --event push --limit 1 --json conclusion,headSha,databaseId \
    --jq '.[0] | "\(.conclusion) \(.headSha) \(.databaseId)"')"
read -r conclusion sha id <<<"$run"

[ "$sha" = "$head" ] || { echo "FAIL: last run $id is for $sha, HEAD is $head" >&2; exit 1; }
[ "$conclusion" = "success" ] || { echo "FAIL: run $id concluded '$conclusion'" >&2; exit 1; }

echo "OK: run $id succeeded on $head"
