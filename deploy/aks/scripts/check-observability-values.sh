#!/usr/bin/env bash
# Assert that every config key deploy/aks/scripts/values.observability.yaml sets actually arrives in
# the rendered configuration of the component it sits under.
#
# Usage: deploy/aks/scripts/check-observability-values.sh [values-file ...]
#
# 🚨 WHY THIS EXISTS. On 2026-09-09 loki-0 was drained with its node and every namespace's log
# history restarted from that moment (#3773) — the ingester's unflushed chunks went with it, and the
# lines lost were the first shutdown of the pair the incident (#3772) needed. `persistence.enabled`
# preserves FLUSHED chunks; what preserves the rest is the ingester WAL, which the values file now
# sets.
#
# 🚨 AND WHY IT IS A GATE RATHER THAN A NOTE. This same values file already documents, in prose, a
# key that is set-able and inert: `promtail.config.wal` produces ZERO occurrences of "wal" in the
# render, because the deprecated loki-stack chart does not template it. That was found by hand, once,
# and written down. Nothing re-checks it, and nothing was checking the Loki side either — so
# `loki.config.ingester.wal` was believed rather than known until it was rendered by hand on
# 2026-09-09 (it does arrive; measured, not assumed). A prose warning about silently-dropped keys is
# exactly the thing that should be an executable invariant: this script makes the next such key fail
# RED naming itself, in the pull request that adds it, instead of reading as coverage for weeks.
#
# It is the observability twin of check-values-are-read.sh — same defect class (#1778, #1780, #1925,
# #2210: a values key consumed by nothing, helm reporting success), different chart. That one reads
# the chart's TEMPLATES because the chart is in this repository. This one cannot: loki-stack is a
# third-party chart, so it asserts against the RENDER instead, which is the stronger test of the two
# — it sees an override as well as an omission.
#
# 🛡️ NO SKIP-TRAPDOOR (AGENTS.md → "A gate NEVER tests its own inputs"). Every failure mode is RED:
# helm missing, PyYAML missing, the chart unfetchable, the render empty, a component's rendered
# document absent, or fewer leaves examined than the file is known to set. There is no
# `continue-on-error:` and no input-shaped `if:` anywhere in its lane.
#
# 🌐 IT NEEDS THE NETWORK, and that is stated rather than worked around. loki-stack is fetched from
# grafana.github.io — the same dependency install-observability.sh has. A fetch failure fails the
# gate; it never skips it. This is why the check runs in its own job and NOT in `Chart invariants`,
# whose header asserts that it reads nothing outside this repository. That property is worth keeping
# true.
set -uo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# The chart version is PINNED, and the pin is the reason a render proves anything at all. An
# unpinned `helm upgrade --install grafana/loki-stack` (which is what this repo shipped until #3773)
# installs whatever upstream published most recently, so a chart bump could restructure `loki.config`
# and silently stop templating the WAL — the failure this gate exists to catch would arrive through
# the very mechanism the gate could not see. Keep it in step with install-observability.sh.
CHART_VERSION="${LOKI_STACK_VERSION:-2.10.3}"
CHART_REPO_URL="https://grafana.github.io/helm-charts"
CHART="${LOKI_STACK_CHART:-grafana/loki-stack}"

missing=()
command -v helm >/dev/null 2>&1 || missing+=("helm (install it, or use azure/setup-helm in CI)")
command -v python3 >/dev/null 2>&1 || missing+=("python3")
python3 -c 'import yaml' >/dev/null 2>&1 || missing+=("the PyYAML module (pip install pyyaml)")
command -v docker >/dev/null 2>&1 || missing+=("docker (the rendered Loki config is validated by the Loki binary itself)")
docker info >/dev/null 2>&1 || missing+=("a running container runtime — 'docker info' failed, so the Loki binary check could not run")

if [ ${#missing[@]} -gt 0 ]; then
  echo "::error::check-observability-values cannot run — provide the following:"
  for item in "${missing[@]}"; do echo "  - $item"; done
  echo "Nothing was checked. This is a failure, not a skip: a gate that declines to run must not"
  echo "render the same tick as one that passed."
  exit 1
fi

if [ $# -gt 0 ]; then
  VALUES=("$@")
else
  VALUES=("$SELF_DIR/values.observability.yaml")
fi

for file in "${VALUES[@]}"; do
  if [ ! -f "$file" ]; then
    echo "::error::values file not found: $file"
    exit 1
  fi
done

if ! helm repo add grafana "$CHART_REPO_URL" >/dev/null 2>&1; then
  # `helm repo add` fails when the repo already exists under a different URL — report it rather
  # than continuing against whatever that other URL serves.
  existing="$(helm repo list -o json 2>/dev/null | python3 -c 'import json,sys
try:
    print(next(r["url"] for r in json.load(sys.stdin) if r["name"] == "grafana"))
except Exception:
    print("<none>")' 2>/dev/null || echo "<none>")"
  if [ "$existing" != "$CHART_REPO_URL" ]; then
    echo "::error::a helm repo named 'grafana' already points at $existing, not $CHART_REPO_URL."
    exit 1
  fi
fi

if ! helm repo update grafana >/dev/null 2>&1; then
  echo "::error::could not refresh the 'grafana' helm repo from $CHART_REPO_URL — the chart could"
  echo "not be fetched, so NOTHING was checked. This is a failure, not a skip."
  exit 1
fi

echo "Rendering $CHART --version $CHART_VERSION with: ${VALUES[*]}"
python3 "$SELF_DIR/check-observability-values.py" "$CHART" "$CHART_VERSION" "${VALUES[@]}"
