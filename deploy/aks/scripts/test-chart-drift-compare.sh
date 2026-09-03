#!/usr/bin/env bash
#
# Self-test for chart-drift-compare.py — the classification half of check-chart-drift.sh.
#
# WHY THIS EXISTS. That comparator decides which divergences are live-wrong (COLLIDES, SHADOWS)
# and which are hygiene (CLUSTER-ONLY, CHART-ONLY, DIFFERS). Until this file, that logic only ever
# executed against a PRIVATE production cluster on a nightly schedule — so a regression in it could
# only be discovered by someone noticing the report had gone quiet, which is exactly the failure
# the drift checker was built to prevent, one level up. It needs no cluster and no credentials, so
# chart-gate.yml runs it on every pull request.
#
# The CLEAN case is the control: if the comparator ever stopped classifying at all, or started
# reporting drift unconditionally, one of the two cases below would fail. A test with only the
# drifted case would pass on a comparator that flagged everything.
set -uo pipefail
SELF_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DATA="$SELF_DIR/testdata/chart-drift"
CMP="$SELF_DIR/chart-drift-compare.py"
fail=0

run_case() {
  local case="$1"
  python3 "$CMP" \
    "$DATA/desired.yaml" \
    "$DATA/$case/live-configmap.json" \
    "$DATA/$case/live-deployment.json" \
    "$DATA/$case/expect-patch.json" \
    "$DATA/$case/live-poddisruptionbudgets.json" \
    "$DATA/$case/live-scaledobjects.json" \
    "$DATA/$case/envfrom-secret-keys.txt" 2>&1
}

check() {  # check <description> <expected> <actual>
  if [ "$2" = "$3" ]; then
    echo "  ok   $1"
  else
    echo "::error::$1 — expected '$2', got '$3'"
    fail=1
  fi
}

# ---- 1. CLEAN: identical objects must report NO drift, and exit 0 ----------
echo "case: clean (no drift)"
out="$(run_case clean)"; rc=$?
check "exit code" "0" "$rc"
case "$out" in
  *"No drift"*) echo "  ok   reports 'No drift'" ;;
  *) echo "::error::clean case did not report 'No drift'. Output:"; echo "$out"; fail=1 ;;
esac

# ---- 2. DRIFTED: one of every class, each landing in the right one ---------
echo "case: drifted (one of every class)"
out="$(run_case drifted)"; rc=$?
check "exit code" "1" "$rc"

# The exact class each seeded divergence must be reported as. Getting any of these WRONG is the
# regression this test exists to catch — a SHADOWS demoted to CLUSTER-ONLY is precisely how the
# live-wrong entries stayed invisible in MeshWeaver#2355 for a week.
expect_class() {  # expect_class <class> <label>
  if echo "$out" | grep -qE "^::error::$1 +$2\$"; then
    echo "  ok   $2 → $1"
  else
    echo "::error::expected '$2' to be classified $1; it was not. Actual classes:"
    echo "$out" | grep '^::error::' | sed 's/^/      /'
    fail=1
  fi
}
expect_class "COLLIDES"     "inline env EMAIL__CLIENTID"
expect_class "SHADOWS"      "inline env PreWarm__GateReadiness"
expect_class "SHADOWS"      "inline env Grpc__TrustedPort"
expect_class "CLUSTER-ONLY" "inline env Features__Ai__Clis__Copilot"
expect_class "CLUSTER-ONLY" "ConfigMap LogWatch__DefaultRepository"
expect_class "CHART-ONLY"   "ConfigMap PluginCatalog__RegistryUrl"
expect_class "DIFFERS"      "livenessProbe"
# An inline env over an envFrom SECRET key — the shape the ConfigMap-only comparison could not see,
# and the one that is live on memex today (MeshWeaver#3201).
expect_class "SHADOWS"      "inline env PluginCatalog__RegistryToken"
if echo "$out" | grep -A1 'SHADOWS *inline env PluginCatalog__RegistryToken' | grep -q 'secret/portal-secrets'; then
  echo "  ok   a secret-backed shadow names the secret"
else
  echo "::error::the PluginCatalog__RegistryToken finding does not name the secret supplying it."
  fail=1
fi
# A secret key differing from the inline name ONLY IN CASE is a COLLISION, not a shadow: the pod
# carries both variables and .NET picks per start. Routing secret twins past the case check was the
# first cut of this feature and Copilot caught it on #3204.
expect_class "COLLIDES"     "inline env Speech__ApiKey"
if echo "$out" | grep -A1 'COLLIDES *inline env Speech__ApiKey' | grep -q 'secret/portal-secrets'; then
  echo "  ok   a case-collision against a SECRET names the secret"
else
  echo "::error::the Speech__ApiKey collision does not name the secret it collides with."
  fail=1
fi

# ...and must NOT claim to know whether the values agree — this script never reads a secret value.
if echo "$out" | grep -A1 'SHADOWS *inline env PluginCatalog__RegistryToken' | grep -q 'THE TWO DISAGREE'; then
  echo "::error::the secret-backed shadow asserts a value verdict it cannot have — no secret value"
  echo "         is ever read, so agreement is unknown and must not be stated."
  fail=1
else
  echo "  ok   a secret-backed shadow states no value verdict"
fi

# A SHADOWS whose two values disagree must SAY so — that verdict is the whole point of the class.
if echo "$out" | grep -A1 'SHADOWS *inline env PreWarm__GateReadiness' | grep -q 'THE TWO DISAGREE'; then
  echo "  ok   disagreeing SHADOWS is called out as such"
else
  echo "::error::PreWarm__GateReadiness shadows a ConfigMap key with a DIFFERENT value, but the"
  echo "         finding does not say the two disagree."
  fail=1
fi
# ...and one whose values agree must NOT claim they disagree.
if echo "$out" | grep -A1 'SHADOWS *inline env Grpc__TrustedPort' | grep -q 'THE TWO DISAGREE'; then
  echo "::error::Grpc__TrustedPort shadows a ConfigMap key with the SAME value, but the finding"
  echo "         claims the two disagree."
  fail=1
else
  echo "  ok   agreeing SHADOWS is not reported as a disagreement"
fi

# An inline entry sourced with valueFrom carries no literal — it must not be compared as if it did.
if echo "$out" | grep -q 'Traceback'; then
  echo "::error::the comparator threw on a valueFrom env entry:"; echo "$out"; fail=1
else
  echo "  ok   a valueFrom env entry does not crash the comparison"
fi

# The inert-probe note: a liveness/readiness initialDelaySeconds under a startupProbe.
if echo "$out" | grep -A1 'DIFFERS *livenessProbe' | grep -q 'startupProbe'; then
  echo "  ok   an initialDelaySeconds-only probe diff is explained as inert"
else
  echo "::error::the livenessProbe DIFFERS finding does not explain that a startupProbe makes"
  echo "         initialDelaySeconds inert — the note that stops the wrong fix."
  fail=1
fi

if [ "$fail" -eq 0 ]; then
  echo ""
  echo "chart-drift comparator: all classification assertions passed."
else
  echo ""
  echo "::error::chart-drift comparator self-test FAILED"
fi
exit "$fail"
