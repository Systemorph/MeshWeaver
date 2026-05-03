#!/usr/bin/env bash
# Deploy a MeshWeaver mode (prod/test) AND verify the db-migration container
# actually completed cleanly. `aspire deploy` itself happily exits 0 when the
# migration container app provisions — even if its Program.cs threw at v2 and
# the database is half-migrated. This wrapper closes that gap by polling
# admin.mesh_nodes.db_version until it reaches the expected value (or times
# out), which is the only authoritative signal that migration finished.
#
# Why we don't poll the container's `state=Terminated` + exit code:
# `db-migration` is deployed as a regular Container App, not a Container Apps
# Job. Container Apps treat *any* container exit (even `exit 0`) as a crash
# and immediately restart it — the container never reaches `Terminated`,
# `lastTerminationState.exitCode` flickers between null and 0 across restarts,
# and a successful migration looks identical to a CrashLoopBackOff. Aspire
# 13.2.x does not expose `PublishAsAzureContainerJob` (Wave 14), and adding a
# raw bicep override here is more code than it's worth — polling the DB is
# both simpler and an end-to-end check, not a proxy for it.
#
# Usage:
#   tools/deploy.sh prod
#   tools/deploy.sh test
#
# Prereqs: az CLI authenticated, aspire CLI installed, Docker running (for image build),
# AZURE_USER_PRINCIPAL_NAME exported (your AAD UPN), dotnet-script installed.
set -euo pipefail

MODE="${1:-}"
case "$MODE" in
  prod) RG="prod-memex" ;;
  test) RG="test-memex" ;;
  *) echo "Usage: $0 {prod|test}" >&2; exit 64 ;;
esac

if [ -z "${AZURE_USER_PRINCIPAL_NAME:-}" ]; then
  echo "❌ AZURE_USER_PRINCIPAL_NAME must be set (your AAD UPN, e.g. user@domain.com)." >&2
  exit 64
fi

echo "==> aspire deploy --mode $MODE (rg=$RG)"
aspire deploy \
  --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj \
  -- --mode "$MODE"

echo
echo "==> Discovering Postgres FQDN in $RG"
PG_HOST=$(az postgres flexible-server list -g "$RG" --query "[0].fullyQualifiedDomainName" -o tsv | tr -d '\r\n')
if [ -z "$PG_HOST" ]; then
  echo "❌ Could not resolve Postgres FQDN in resource group $RG. Is `az login` current?" >&2
  exit 2
fi
echo "    PG_HOST=$PG_HOST"

echo
echo "==> Polling admin.mesh_nodes.db_version until migration completes (deadline 10 min)…"
# `check-db-version.csx` exits 0 when db_version >= ExpectedVersion, non-zero
# otherwise. Loop until success or deadline. Print container logs on timeout
# so the failure mode is visible without a separate `az logs show`.
deadline=$(( $(date +%s) + 600 ))
attempt=0
while [ "$(date +%s)" -lt "$deadline" ]; do
  attempt=$((attempt + 1))
  if dotnet script tools/check-db-version.csx -- "$MODE" "$PG_HOST" 2>/dev/null; then
    echo
    echo "✅ Deploy complete and migration verified."
    exit 0
  fi
  printf "  …attempt %d: db_version not yet at expected value, retrying in 15s\n" "$attempt"
  sleep 15
done

echo "❌ db_version did not reach expected version within 10 min — migration failed." >&2
echo >&2
echo "Last 100 db-migration container log lines:" >&2
az containerapp logs show -n db-migration -g "$RG" --tail 100 >&2 || true
exit 1
