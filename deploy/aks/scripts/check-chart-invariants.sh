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
# credentials and the private per-env values, so it cannot be a PR gate. But the production 503 of
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
#   8. every values key the platform must let a deploy set is actually RENDERED, and never blank
#   9. a key whose consumer parses it must never render BLANK   absent is fine; blank throws at bind
#  10. readiness and liveness probe DIFFERENT paths           one path cannot answer two questions
#  11. the platform pull secret is on BOTH pods or neither the migration and the portal pull one image
#  12. a chart-created PVC is sized, classed, kept, mounted    or the claim exists and nothing uses it
#  13. the bundle-fetch shelf is the pre-warm root, pod cred   or the portal reads a shelf nobody filled
#  14. a replica floor > 1 implies a PDB                     or one node drain evicts every replica at once
#  15. a replica floor > 1 implies anti-affinity / spread    or every replica shares one node
#  16. wait-for-postgres probes EVERY host the pod's connection strings name  or Init:1/1 proves
#                                                             nothing about the connection that fails
#  17. no wait-for-postgres probes memex-postgres-service unless the chart renders it  or the gate
#                                                             spins forever on a name that never resolves
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
  "AKS overlay (the layer every AKS install shares)|deploy/helm/values.yaml:deploy/aks/values.aks.yaml"
  "memex-local (Colima k3s)|deploy/helm/values.yaml:deploy/homebrew/share/values.local.defaults.yaml"
  # A record-driven Provision of a MIRROR-CONSUMING instance (MeshWeaver#3353): the only combination
  # that switches on the pull secret, SelfUpdate__Registry and the chart-created PVCs. A fixture, not
  # an environment — without it those template branches render on nothing in this repo and a
  # regression in any of them is invisible until a real Provision fails.
  "mirror consumer (record-driven provision fixture)|deploy/helm/values.yaml:deploy/aks/scripts/testdata/values.mirror-consumer.yaml"
  # The fleet registry (templates/registry/) renders NOTHING under the three shapes above — it is
  # off by default — so without this combination a broken registry template is invisible to every
  # pull request and surfaces as a failed `helm upgrade` in the deployment repo. The example
  # overlay sets every required key (object NAMES and a bcrypt HASH, no credential).
  "AKS overlay + the fleet registry (cr.meshweaver.cloud)|deploy/helm/values.yaml:deploy/aks/values.aks.yaml:deploy/helm/values.registry.example.yaml"
  # A mirror consumer that ALSO materialises its plugin bundles from the fleet registry before the
  # portal starts (bundles.*, Doc/Architecture/PluginBundlesInTheRegistry): the only combination
  # that renders the `bundle-fetch` init container, its shelf, its script and the projected pull
  # secret. Invariant 13 asserts the shelf is the pre-warm's root and the credential is the pod's.
  "mirror consumer + bundles from the fleet registry (fixture)|deploy/helm/values.yaml:deploy/aks/scripts/testdata/values.mirror-consumer.yaml:deploy/aks/scripts/testdata/values.bundle-fetch.yaml"
  # The memex.systemorph.com shape (MeshWeaver#3772): TWO PLAIN REPLICAS with KEDA OFF. Its lane
  # renders the vault values plus its own overlay — never values.aks.yaml — so this is the only
  # combination here with a replica floor above one and no ScaledObject. Until 2026-09-09 it
  # rendered NO PodDisruptionBudget (pdb.yaml was gated on keda.enabled) and invariants 14/15 did
  # not exist: an AKS node drain evicted both pods in the same second, 503 for ~90 s.
  "two plain replicas, KEDA off (the memex shape)|deploy/helm/values.yaml:deploy/aks/scripts/testdata/values.two-replicas-no-keda.yaml"
  # 🚨 A DEDICATED orleans server (MeshWeaver#4173, #3780): the mesh database on one host, cluster
  # membership on another. Nothing else here renders that shape, and it is the one the start-up
  # gate was blind to — the probe read config.MEMEX_HOST while the boot opened two SECRET
  # connection strings naming neither. Invariant 16 asserts the probe covers both.
  "a dedicated orleans server (fixture)|deploy/helm/values.yaml:deploy/aks/scripts/testdata/values.dedicated-orleans-host.yaml"
  # 🚨 The Key Vault case (pearl, 2026-09-15): an external database whose connection string comes
  # from a CSI SecretProviderClass, not from the values — the shape of EVERY record-driven Provision,
  # and the one no combination here rendered. #4173 derived the probe from the values' string, which
  # here is the chart's in-cluster default, and pearl's pods waited forever for memex-postgres-service.
  # Invariants 16 and 17 assert the probe is the record-rendered MEMEX_HOST and never that Service.
  "a Key Vault connection string, record-driven (the pearl shape, fixture)|deploy/helm/values.yaml:deploy/aks/scripts/testdata/values.keyvault-connection-string.yaml"
  # 🚨 The instance's OWN database release (Doc/Architecture/InClusterDatabases): a CloudNativePG
  # Cluster beside the portal release, named by database.release. The only combination whose
  # connection strings are COMPOSED in the containers' env from a Secret the chart does not render.
  # Invariant 19 asserts the credentials are defined before the strings that expand them, and that
  # the gate probes the host those strings name.
  "the instance's own database release (the pearl shape after 2026-09-15, fixture)|deploy/helm/values.yaml:deploy/aks/scripts/testdata/values.incluster-db-release.yaml"
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

# ---------------------------------------------------------------------------
# REFUSALS — shapes the chart must NOT render, and must name why.
#
# 🚨 The opposite assertion from the loop above, and the one #3780 needed. A values combination
# can be internally consistent and still describe a deployment that dies at boot, because a
# template `default` manufactured a plausible value for an input nobody supplied: two replicas on
# an external database with NO connection string in values rendered `ConnectionStrings__orleans`
# pointing at the chart's in-cluster Service, which that release does not render — and every new
# pod on the control instance failed at silo start, twice (revisions 44 and 55), with `helm
# template` perfectly happy both times. The invariant checker cannot see it: the rendered Secret
# carries a key (invariant 4 is satisfied) naming a host (invariant 16 is satisfied) that simply
# does not exist. So the chart now REFUSES that render, and this is the control that proves the
# refusal is still there: each entry must FAIL `helm template` AND mention the phrase. A render
# that succeeds here is the regression.
#
# name|values files (colon-separated)|phrase the refusal must carry
# ---------------------------------------------------------------------------
REFUSALS=(
  "AdoNet on an external database with no connection string in values (the #3780 render)|deploy/helm/values.yaml:deploy/aks/scripts/testdata/values.adonet-external-db-no-connection-string.yaml|MeshWeaver#3780"
  "an external database with neither a values connection string nor a MEMEX_HOST (the pearl refusal)|deploy/helm/values.yaml:deploy/aks/scripts/testdata/values.external-db-no-host.yaml|names no external database host"
  "an external database whose values string names the in-cluster Service (the explicit-placeholder refusal)|deploy/helm/values.yaml:deploy/aks/scripts/testdata/values.external-db-explicit-in-cluster-host.yaml|names the in-cluster Service memex-postgres-service"
  "a database release AND the bundled Postgres (two answers to which database)|deploy/helm/values.yaml:deploy/aks/scripts/testdata/values.db-release-with-bundled-postgres.yaml|exclusive with postgres.enabled"
)
refused=0
for entry in "${REFUSALS[@]}"; do
  name="${entry%%|*}"
  rest="${entry#*|}"
  files="${rest%%|*}"
  phrase="${rest#*|}"
  args=( template release "$CHART" --namespace check )
  bad_input=0
  IFS=':' read -r -a paths <<< "$files"
  for p in "${paths[@]}"; do
    if [ ! -f "$REPO/$p" ]; then
      report "refusal '$name' names a values file that does not exist: $p"
      bad_input=1
    fi
    args+=( -f "$REPO/$p" )
  done
  [ "$bad_input" -eq 1 ] && continue

  out="$WORK/refusal-$(echo "$name" | tr -c 'a-zA-Z0-9' '-').yaml"
  if helm "${args[@]}" > "$out" 2> "$out.err"; then
    report "refusal '$name' RENDERED — the chart manufactured a value for an input nobody supplied instead of refusing (the #3780 shape is back)"
    continue
  fi
  if ! grep -q -- "$phrase" "$out.err"; then
    report "refusal '$name' failed to render, but not for the stated reason — '$phrase' is not in helm's error:"
    sed 's/^/    /' "$out.err"
    continue
  fi
  refused=$((refused + 1))
  ok "$name — refused to render, naming the missing input"
done
if [ "$refused" -lt "${#REFUSALS[@]}" ]; then
  report "only $refused of ${#REFUSALS[@]} refusal controls held — treating as FAILURE"
fi

# ---------------------------------------------------------------------------
# THE DATABASE RELEASE CHART (deploy/helm-db, Doc/Architecture/InClusterDatabases) — a second chart,
# installed per instance beside the portal release. It must render its CloudNativePG Cluster where
# the platform layer puts it (the `db` pool, one instance per zone), and refuse a release with no
# database name rather than bootstrap one called ''.
# ---------------------------------------------------------------------------
DB_CHART="$REPO/deploy/helm-db"
if [ -d "$DB_CHART" ]; then
  db_out="$WORK/db-release.yaml"
  if helm template pearl-db "$DB_CHART" --namespace pearl --set database=pearl > "$db_out" 2> "$db_out.err"; then
    db_ok=1
    for want in 'kind: Cluster' 'instances: 2' 'podAntiAffinityType: "required"' 'topologyKey: topology.kubernetes.io/zone' \
                'workload: db' 'effect: NoSchedule' 'CREATE EXTENSION IF NOT EXISTS vector' 'storageClass: "memex-db-premiumv2"'; do
      if ! grep -qF -- "$want" "$db_out"; then
        report "the database release chart does not render '$want' — the Cluster would not land one instance per zone on the db pool"
        db_ok=0
      fi
    done
    [ "$db_ok" -eq 1 ] && ok "the database release chart — a two-zone Cluster on the db pool with the vector extension"
  else
    report "the database release chart does not render at all:"
    sed 's/^/    /' "$db_out.err"
  fi
  if helm template pearl-db "$DB_CHART" --namespace pearl > "$db_out" 2> "$db_out.err"; then
    report "the database release chart RENDERED with no database name — it must refuse instead"
  elif grep -q "must be a plain lower-case PostgreSQL identifier" "$db_out.err"; then
    ok "the database release chart — refuses a release with no database name"
  else
    report "the database release chart refused, but not for the stated reason:"
    sed 's/^/    /' "$db_out.err"
  fi
fi

if [ "$fail" -eq 0 ]; then
  echo
  echo "All $rendered values combinations render a self-consistent deployment, and all $refused refusal controls hold."
fi
exit "$fail"
