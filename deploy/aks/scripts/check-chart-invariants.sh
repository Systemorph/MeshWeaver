#!/usr/bin/env bash
#
# Does the chart DESCRIBE a shape that can actually work?
#
#   deploy/aks/scripts/check-chart-invariants.sh
#   exit 0 = every values combination in this repo renders a self-consistent deployment
#   exit 1 = a combination renders a contradiction, or nothing could be checked
#
# 🚨 WHY THIS EXISTS — the companion to check-chart-drift.sh, and the half that can actually run
# on every pull request.
#
# check-chart-drift.sh answers "does the CLUSTER run what the chart describes?" It needs cluster
# credentials and the private per-env values, so it cannot be a PR gate. But the memex-cloud 503 of
# 2026-08-14 did not need a cluster to detect: the CHART ITSELF described an impossibility, in git,
# for a month, and no build ever looked.
#
#   deploy/aks/values.aks.yaml         keda.enabled: true, keda.minReplicas: 2, replicas.portal: 2
#   templates/…/scaledobject.yaml      minReplicaCount: 2          ← wants two pods
#   templates/…/pdb.yaml               maxUnavailable: 1           ← budgets for two pods
#   templates/…/deployment.yaml        replicas: 1   HARD-CODED    ← renders one pod
#
# Three files asking for HA and one line vetoing it. `replicas.portal: 2` was consumed by nothing.
# The rendered manifest set was internally contradictory and `helm template` was perfectly happy to
# emit it, because helm validates syntax, not sense.
#
# WHAT IT CHECKS (rendered objects — no cluster, no secrets, no network):
#   1. spec.replicas is ABSENT when a ScaledObject exists     helm and the HPA must not both own it
#   2. a replica floor > 1 implies AdoNet/AzureTables         Localhost clustering cannot span pods
#   3. a replica floor > 1 implies RWX on every portal claim  /data is shared state
#   4. AdoNet implies ConnectionStrings__orleans is emitted   the silo throws at startup without it
#   5. a PDB uses maxUnavailable, never minAvailable          minAvailable is not scale-invariant
#   6. a PDB implies a replica floor > 1                      over one pod it blocks all or evicts all
#   7. a ScaledObject implies strategy.maxUnavailable: 0      surge-first, or the roll drops traffic
#
# NO SKIP-TRAPDOOR (AGENTS.md → "A gate NEVER tests its own inputs"). Every input is IN THIS REPO:
# the chart and the tracked values files. There is no secret to be absent, so there is no condition
# under which this check may decline to run — and it asserts a minimum number of rendered
# combinations before it is allowed to report success, so "checked nothing" can never read as
# "found nothing wrong".
set -uo pipefail

SELF_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd -- "$SELF_DIR/../../.." && pwd)"
CHART="$REPO/deploy/helm"
PVCS="$REPO/deploy/aks/manifests/portal-pvcs.yaml"

fail=0
summary() { [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "$1" >> "$GITHUB_STEP_SUMMARY"; return 0; }
report()  { echo "::error::$1"; summary "- ❌ $1"; fail=1; }
ok()      { echo "$1";          summary "- ✅ $1"; }

# ---------------------------------------------------------------------------
# PREFLIGHT — assert the tools and files, and fail RED naming what is missing.
# ---------------------------------------------------------------------------
missing=()
command -v helm    >/dev/null 2>&1 || missing+=("helm      not on PATH — brew install helm / azure/setup-helm")
if ! command -v python3 >/dev/null 2>&1; then
  missing+=("python3   not on PATH — needed to parse the rendered manifests")
# PyYAML is a THIRD-PARTY module, not part of the standard library. It happens to be present on
# GitHub's hosted runners today, which is exactly why it is asserted here: an unstated dependency
# that works by luck breaks on a runner-image bump, and it would break as a Python traceback rather
# than as a preflight naming what to install. A gate's dependencies are inputs like any other.
elif ! python3 -c "import yaml" >/dev/null 2>&1; then
  missing+=("PyYAML    python3 has no 'yaml' module — pip install pyyaml (or apt-get install python3-yaml)")
fi
[ -d "$CHART" ] || missing+=("chart     not found at '$CHART'")
[ -f "$PVCS" ]  || missing+=("pvcs      not found at '$PVCS' — the RWX assertion reads it")
if [ ${#missing[@]} -gt 0 ]; then
  echo "::error::check-chart-invariants cannot run — provide the following:"
  for m in "${missing[@]}"; do echo "  • $m"; done
  exit 1
fi

# ---------------------------------------------------------------------------
# The values combinations this repo ships. Each is a real deployment shape someone installs, so
# each must render a coherent one. Per-env overlays (Entra ids, hosts, connection strings) live in
# the PRIVATE Systemorph/Memex repo and are NOT here — they can still break a namespace, which is
# what check-chart-drift.sh is for. These are the shapes git can prove on its own.
#
# name|values files (colon-separated, relative to the repo root)
# ---------------------------------------------------------------------------
COMBOS=(
  "self-host (neutral chart defaults)|deploy/helm/values.yaml"
  "AKS overlay (memex / memex-cloud / atioz shared layer)|deploy/helm/values.yaml:deploy/aks/values.aks.yaml"
  "memex-local (Colima k3s)|deploy/helm/values.yaml:deploy/homebrew/share/values.local.defaults.yaml"
)

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

rendered=0
for combo in "${COMBOS[@]}"; do
  name="${combo%%|*}"
  files="${combo#*|}"
  args=( template release "$CHART" --namespace check )
  bad_input=0
  IFS=':' read -r -a paths <<< "$files"
  for p in "${paths[@]}"; do
    if [ ! -f "$REPO/$p" ]; then
      report "combination '$name' names a values file that does not exist: $p"
      bad_input=1
    fi
    args+=( -f "$REPO/$p" )
  done
  [ "$bad_input" -eq 1 ] && continue

  out="$WORK/$(echo "$name" | tr -c 'a-zA-Z0-9' '-').yaml"
  if ! helm "${args[@]}" > "$out" 2> "$out.err"; then
    report "combination '$name' does not render at all — helm template FAILED:"
    sed 's/^/    /' "$out.err"
    continue
  fi
  rendered=$((rendered + 1))

  if python3 "$SELF_DIR/check-chart-invariants.py" "$name" "$out" "$PVCS"; then
    ok "$name — the rendered deployment is self-consistent"
  else
    fail=1
  fi
done

# The evidence assertion: a run that rendered (almost) nothing must not read as a pass.
if [ "$rendered" -lt "${#COMBOS[@]}" ]; then
  report "only $rendered of ${#COMBOS[@]} values combinations rendered — treating as FAILURE rather than reporting 'no contradictions' on partial evidence"
fi

if [ "$fail" -eq 0 ]; then
  echo
  echo "All $rendered values combinations render a self-consistent deployment."
fi
exit "$fail"
