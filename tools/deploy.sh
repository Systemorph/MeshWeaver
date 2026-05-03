#!/usr/bin/env bash
# Deploy a MeshWeaver mode (prod/test) AND verify the db-migration container
# actually completed cleanly. `aspire deploy` itself happily exits 0 when the
# migration container app provisions — even if its Program.cs threw at v2 and
# the database is half-migrated. This wrapper closes that gap by polling the
# db-migration ACA revision until it reaches a terminal state and exiting
# non-zero on Failed / non-zero exit code.
#
# Usage:
#   tools/deploy.sh prod
#   tools/deploy.sh test
#
# Prereqs: az CLI authenticated, aspire CLI installed, Docker running (for image build).
set -euo pipefail

MODE="${1:-}"
case "$MODE" in
  prod) RG="prod-memex" ;;
  test) RG="test-memex" ;;
  *) echo "Usage: $0 {prod|test}" >&2; exit 64 ;;
esac

echo "==> aspire deploy --mode $MODE (rg=$RG)"
aspire deploy \
  --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj \
  -- --mode "$MODE"

echo
echo "==> Waiting for db-migration container to reach a terminal state…"
# Poll the latest revision of the db-migration container app. ACA reports
# `Provisioned` while the container app definition is up, but the actual
# migration container's exit code shows up under `properties.template.containers[0].lastTerminationState`
# once it finishes running. We poll until either:
#   - replica completed successfully (exit code 0) → keep going
#   - replica failed (non-zero exit) → fail the deploy
#   - 10 min timeout → fail the deploy

deadline=$(( $(date +%s) + 600 ))
exit_code=""
state=""
while [ "$(date +%s)" -lt "$deadline" ]; do
  # ACA exposes per-replica state. Pick the most recent one.
  read -r state exit_code reason < <(
    az containerapp replica list \
      -n db-migration -g "$RG" \
      --revision "$(az containerapp revision list -n db-migration -g "$RG" \
        --query "sort_by([], &properties.createdTime)[-1].name" -o tsv)" \
      --query "sort_by([], &properties.createdTime)[-1].properties.containers[0].{state: state, exitCode: lastTerminationState.exitCode, reason: lastTerminationState.reason}" \
      -o tsv 2>/dev/null || echo "Unknown 0 -"
  )

  case "$state" in
    Terminated)
      if [ "$exit_code" = "0" ]; then
        echo "✅ db-migration completed cleanly (exit 0, reason=$reason)"
        break
      else
        echo "❌ db-migration FAILED: exit=$exit_code reason=$reason" >&2
        echo
        echo "Last 100 log lines:" >&2
        az containerapp logs show -n db-migration -g "$RG" --tail 100 >&2 || true
        exit 1
      fi
      ;;
    Running|Waiting)
      printf "  …state=%s, waiting 10s\n" "$state"
      sleep 10
      ;;
    *)
      printf "  …state=%s (unknown), waiting 10s\n" "$state"
      sleep 10
      ;;
  esac
done

if [ -z "$exit_code" ] || [ "$state" != "Terminated" ]; then
  echo "❌ db-migration didn't reach Terminated state within 10 min — failing deploy." >&2
  exit 2
fi

echo
echo "==> Verifying admin.mesh_nodes.db_version landed"
# The portal's DbVersionGate checks the same thing at startup, but verifying
# here gives us a clear deploy-time signal even if the portal is slow to start.
dotnet script tools/check-db-version.csx -- "$MODE" || {
  echo "❌ db_version check failed against the deployed DB" >&2
  exit 3
}

echo
echo "✅ Deploy complete and migration verified."
