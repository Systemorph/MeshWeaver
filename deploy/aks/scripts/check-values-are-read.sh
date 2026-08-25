#!/usr/bin/env bash
#
# Is every config key a values file sets actually READ by the chart?
#
#   deploy/aks/scripts/check-values-are-read.sh [values-file ...]
#   exit 0 = every `config.<component>.<KEY>` examined is consumed by that component's templates
#   exit 1 = at least one orphaned key, or nothing could be examined
#
# With no arguments it checks the values files THIS repo ships. Pass paths to check somebody
# else's — a per-env overlay in the private deployment repo is the case this was written for, and
# it needs no cluster, no secret and no network, so it can run as that repo's PR gate.
#
# 🚨 WHY THIS EXISTS — a rule the chart states in a comment, broken four times.
#
# deploy/helm/templates/memex-portal/config.yaml says it in as many words:
#
#     🚨 A values key this template omits reaches NO container, with no error anywhere: the
#     ConfigMap names every key explicitly, there is no catch-all range over config.memex_portal,
#     and the Deployment's only env path is envFrom on this ConfigMap.
#
# helm is perfectly happy either way. The values file is syntactically valid, the render succeeds,
# the deploy reports success, and the key is simply gone. The failure is always silent and always
# discovered somewhere else:
#
#   #1778  SelfUpdate__PollInterval          set in values, templated nowhere — read as a live
#                                            daily throttle in the chart for weeks; inert.
#   #1780  SelfUpdate__MinRollInterval       its replacement, same defect (#1925).
#   #1925  Modules__Root                     set in all three AKS values files, rendered by
#                                            nothing; Memex#53 was titled "Modules__Root belongs
#                                            in the chart — that is why it kept vanishing".
#   #2210  GitHub__App__{ClientId,           the OTHER direction, and the expensive one. After the
#          InstallationId,InstallationOwner} 2026-08-24 near-miss the keys WERE committed to the
#                                            overlay — one section off, under
#                                            `config.memex_migration`, which the portal ConfigMap
#                                            never reads. The overlay LOOKED safe (key present,
#                                            reassuring comment beside it) while every deploy
#                                            rendered the portal keys "" and helm's three-way
#                                            merge applied the blanking as a DELETION. GitSync and
#                                            plugin-registry pulls were down fleet-wide, 2026-08-25
#                                            11:13–11:24Z.
#
# The deploy-time blank-guard in Systemorph/Memex's helm-release.yml catches #2210's shape at the
# last possible moment: it diffs the render against the LIVE ConfigMap and refuses to empty a key
# that currently has a value. That guard is the safety net, and it needs a cluster to run. This is
# the same invariant asserted at AUTHORING time, from the files alone — so the mis-nesting is a red
# pull request instead of a production incident with a rollback.
#
# 🛡️ NO SKIP-TRAPDOOR (AGENTS.md → "A gate NEVER tests its own inputs"). Every input is a file
# named on the command line. There is no secret to be absent and no network to be down, so there is
# no condition under which this may decline to run and still exit 0: a values file that does not
# exist is a failure, a chart that cannot be read is a failure, and a run that examined zero keys
# is a failure — never "found no orphans".
set -uo pipefail

SELF_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd -- "$SELF_DIR/../../.." && pwd)"
CHART="${CHART_DIR:-$REPO/deploy/helm}"

# ---------------------------------------------------------------------------
# PREFLIGHT — assert the tools and files, and fail RED naming what is missing.
# ---------------------------------------------------------------------------
missing=()
if ! command -v python3 >/dev/null 2>&1; then
  missing+=("python3   not on PATH — needed to parse the values files")
# PyYAML is THIRD-PARTY, not stdlib. It happens to be present on GitHub's hosted runners today,
# which is exactly why it is asserted: an unstated dependency satisfied by luck of the runner
# image breaks on an image bump, as a traceback rather than as a sentence naming what to install.
elif ! python3 -c "import yaml" >/dev/null 2>&1; then
  missing+=("PyYAML    python3 has no 'yaml' module — pip install pyyaml (or apt-get install python3-yaml)")
fi
[ -d "$CHART/templates" ] || missing+=("chart     no templates/ under '$CHART' — set CHART_DIR if the chart lives elsewhere")
if [ ${#missing[@]} -gt 0 ]; then
  echo "::error::check-values-are-read cannot run — provide the following:"
  for m in "${missing[@]}"; do echo "  • $m"; done
  exit 1
fi

# ---------------------------------------------------------------------------
# The values files to check. Named ones win; otherwise the ones this repo ships.
# ---------------------------------------------------------------------------
if [ "$#" -gt 0 ]; then
  VALUES=( "$@" )
else
  VALUES=(
    "$REPO/deploy/helm/values.yaml"
    "$REPO/deploy/aks/values.aks.yaml"
    "$REPO/deploy/homebrew/share/values.local.defaults.yaml"
  )
fi

bad_input=0
for v in "${VALUES[@]}"; do
  [ -f "$v" ] || { echo "::error::values file does not exist: $v"; bad_input=1; }
done
[ "$bad_input" -eq 1 ] && exit 1

# ---------------------------------------------------------------------------
# The invariant this check RESTS on: the chart names every config key explicitly.
#
# If a template ever starts iterating a config section — `range .Values.config.memex_portal`,
# `toYaml .Values.config.memex_portal`, `index .Values.config.memex_portal "K"` — then a key
# absent from the grep is NOT necessarily unread, and every finding below becomes a guess. That
# would make this check confidently wrong, which is worse than absent. So assert the premise, and
# fail naming it rather than emitting nonsense findings against a chart that changed shape.
# ---------------------------------------------------------------------------
ITER="$(mktemp)"
trap 'rm -f "$ITER"' EXIT
grep -REn '(range|with|toYaml|index)[^\n]*\.Values\.config\.[A-Za-z_]' "$CHART/templates" \
     --include='*.yaml' --include='*.yml' --include='*.tpl' \
  | grep -vE '^[^:]+:[0-9]+: *#' > "$ITER" 2>/dev/null
if [ -s "$ITER" ]; then
  echo "::error::a template now iterates a config section, so 'this key is read by nothing' can"
  echo "  no longer be decided by naming: the premise of this check is that the chart names"
  echo "  every config key explicitly (see config.yaml's own comment). Either keep that idiom,"
  echo "  or teach this check the new one — do not leave it reporting findings it cannot stand"
  echo "  behind. Offending line(s):"
  sed 's/^/    /' "$ITER"
  exit 1
fi

python3 "$SELF_DIR/check-values-are-read.py" "$CHART" "${VALUES[@]}"
