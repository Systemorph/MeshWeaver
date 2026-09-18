#!/usr/bin/env bash
#
# Self-test for chart-drift-render.py — the DESIRED half of check-chart-drift.sh.
#
# WHY THIS EXISTS. `Chart Drift` ran 39 times and failed 39 times without ever producing a verdict
# (MeshWeaver#4640), and nothing in CI could have told anyone: the only thing that exercised the
# render was the nightly job itself, against a private cluster, behind two credentials. This file
# exercises the render — and, more importantly, the SAFETY PROPERTY the render now rests on — with
# no cluster, no credentials and no network, so chart-gate.yml runs it on every pull request.
#
# 🚨 THE PROPERTY UNDER TEST IS THE NEGATIVE ONE. chart-drift-render.py may render only by supplying
# a placeholder for a secret the check must not hold, and the whole design stands on the claim that
# no compared object depends on it. A test that only asserted "it renders" would pass just as
# happily on a version that had lost the ability to notice a leak — which is the failure class this
# repository keeps meeting. So case 2 POISONS the chart so the placeholder reaches a compared
# ConfigMap key, and REQUIRES the render to go red naming it. Case 3 poisons it one step further
# out, where the placeholder changes which objects exist at all. Case 4 is the subtlest: a render
# that is NOT empty but is missing an object the comparator refuses to run without, so the proof
# would be announced over a chart nothing can read. Case 5 asserts the script refuses rather than
# inventing a database host when the committed overlay has none — the #3780 rule applied to the
# checker itself.
#
# Case 1 is the control for cases 2–5: without a case that PASSES, a script that failed
# unconditionally would satisfy every other assertion here.
set -uo pipefail
SELF_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd -- "$SELF_DIR/../../.." && pwd)"
CHART="$REPO/deploy/helm"
RENDER="$SELF_DIR/chart-drift-render.py"
BASE="$CHART/values.yaml"
# The record-driven AKS shape with the Key Vault half absent — exactly what the nightly job faces.
FIXTURE="$SELF_DIR/testdata/values.record-render-no-vault-half.yaml"
fail=0

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

for required in "$RENDER" "$BASE" "$FIXTURE"; do
  if [ ! -f "$required" ]; then
    echo "::error::missing input '$required' — this self-test cannot run, and a self-test that"
    echo "    cannot run must never report a pass."
    exit 1
  fi
done

run_render() {  # run_render <chart-dir> <out> [extra values files...]
  local chart="$1" out="$2"; shift 2
  local args=( --chart "$chart" --namespace check --release rel --out "$out" -f "$BASE" -f "$FIXTURE" )
  local extra
  for extra in "$@"; do args+=( -f "$extra" ); done
  python3 "$RENDER" "${args[@]}" 2>&1
}

# A copy of the chart with one line appended to a template — the poison. Copying rather than
# committing a second chart keeps exactly one chart in the repository; a committed poisoned copy
# would rot the moment the real chart changed and would then be testing nothing.
poisoned_chart() {  # poisoned_chart <name> <template-relative-path> <line...>
  local name="$1" template="$2"; shift 2
  local dir="$WORK/$name"
  rm -rf "$dir"
  cp -R "$CHART" "$dir"
  printf '%s\n' "$@" >> "$dir/$template"
  echo "$dir"
}

# The other poison shape: a template that renders NOTHING, so an object simply disappears.
emptied_chart() {  # emptied_chart <name> <template-relative-path>
  local name="$1" template="$2"
  local dir="$WORK/$name"
  rm -rf "$dir"
  cp -R "$CHART" "$dir"
  : > "$dir/$template"
  echo "$dir"
}

expect_pass() {  # expect_pass <description> <output> <rc>
  if [ "$3" = "0" ]; then
    echo "  ok   $1"
  else
    echo "::error::$1 — expected exit 0, got $3. Output:"
    printf '%s\n' "$2" | sed 's/^/    /'
    fail=1
  fi
}

expect_red() {  # expect_red <description> <output> <rc> <phrase the failure must carry>
  if [ "$3" = "0" ]; then
    echo "::error::$1 — the render REPORTED SUCCESS where it had to fail. This is the only"
    echo "    outcome that matters here: the placeholder reached the comparison and nothing said"
    echo "    so, which is a drift report that describes the checker's own fiction. Output:"
    printf '%s\n' "$2" | sed 's/^/    /'
    fail=1
    return
  fi
  case "$2" in
    *"$4"*) echo "  ok   $1 (red, naming the cause)" ;;
    *)
      echo "::error::$1 — it failed, but not for the stated reason: '$4' is not in the output."
      echo "    A red for the wrong reason is not this control holding. Output:"
      printf '%s\n' "$2" | sed 's/^/    /'
      fail=1 ;;
  esac
}

# ---- 1. CONTROL: the real chart, the production shape, no vault half -------
echo "case: the record-driven shape renders, and the placeholder is proved inert"
out="$(run_render "$CHART" "$WORK/desired.yaml")"; rc=$?
expect_pass "exit code" "$out" "$rc"
case "$out" in
  *"Placeholder independence PROVED"*) echo "  ok   states the proof it made" ;;
  *) echo "::error::the render passed WITHOUT reporting the independence proof — so it either did"
     echo "    not make it or did not say so, and a proof nobody can read in the log is not one."
     printf '%s\n' "$out" | sed 's/^/    /'; fail=1 ;;
esac
# The render must actually contain the objects the comparison reads. Without this, a render that
# emitted nothing at all would satisfy every assertion above.
for object in "memex-portal-config" "memex-portal-deployment"; do
  if grep -q "name: \"\\?$object\"\\?$" "$WORK/desired.yaml" 2>/dev/null; then
    echo "  ok   the render carries $object"
  else
    echo "::error::the render does not carry $object — the comparison would have had no subject."
    fail=1
  fi
done
# And the placeholder must be nowhere near the objects that get compared. This is the WEAK form of
# the property (case 2 tests the strong one), kept because it is the form a human reads.
if grep -q "RENDER-ONLY-PLACEHOLDER" "$WORK/desired.yaml"; then
  if python3 - "$WORK/desired.yaml" <<'PYEOF'
import sys, yaml
compared = {("ConfigMap", "memex-portal-config"), ("Deployment", "memex-portal-deployment")}
leaked = []
for doc in yaml.safe_load_all(open(sys.argv[1])):
    if not doc:
        continue
    key = (doc.get("kind"), (doc.get("metadata") or {}).get("name"))
    if key in compared and "RENDER-ONLY-PLACEHOLDER" in yaml.dump(doc):
        leaked.append("/".join(str(k) for k in key))
if leaked:
    print("    leaked into: " + ", ".join(leaked))
    sys.exit(1)
PYEOF
  then
    echo "  ok   the placeholder appears only outside the compared objects"
  else
    echo "::error::the placeholder string appears INSIDE a compared object."
    fail=1
  fi
else
  echo "::error::no placeholder in the render at all — the fixture no longer exercises the"
  echo "    injection path, so cases 2–4 below would be proving nothing."
  fail=1
fi

# ---- 2. NEGATIVE CONTROL: the placeholder reaching a compared VALUE --------
# The realistic regression: somebody templates a connection string into memex-portal-config, and
# from then on the drift report compares the cluster against a value this check invented. The
# rendered ConfigMap key is a function of the placeholder, so the two renders disagree.
echo "case: a chart that leaks the placeholder into a compared ConfigMap key must go RED"
poison="$(poisoned_chart leak-into-configmap templates/memex-portal/config.yaml \
  '  DriftTestLeakedSecret: {{ .Values.secrets.memex_portal.ConnectionStrings__orleans | quote }}')"
out="$(run_render "$poison" "$WORK/poisoned.yaml")"; rc=$?
expect_red "a leaked ConfigMap value" "$out" "$rc" "DEPENDS ON the render-only placeholder"
case "$out" in
  *DriftTestLeakedSecret*) echo "  ok   names the leaking key" ;;
  *) echo "::error::the failure did not name the key that leaked, so nobody could act on it."
     printf '%s\n' "$out" | sed 's/^/    /'; fail=1 ;;
esac

# ---- 3. NEGATIVE CONTROL: the placeholder deciding what EXISTS -------------
# One step further out, and it would defeat a field-by-field comparison: the placeholder decides
# whether a compared object is rendered at all. A set-equality check catches it; a value diff over
# the intersection would not.
#
# 🚨 The condition must match placeholder A EXACTLY, and it is spelled out in full below for that
# reason. A condition neither placeholder satisfies would leave the object absent from BOTH renders,
# the object sets would match, and this case would pass having tested nothing — the vacuous shape
# this whole file exists to refuse.
echo "case: a chart where the placeholder decides whether a compared object exists must go RED"
poison="$(poisoned_chart leak-into-existence templates/memex-portal/pdb.yaml \
  '{{- if eq .Values.secrets.memex_portal.ConnectionStrings__orleans (printf "Host=%s;Port=5432;Database=%s;Username=%s;Password=%s" .Values.config.memex_portal.MEMEX_HOST "RENDER-ONLY-PLACEHOLDER-NOT-A-CREDENTIAL-A" "RENDER-ONLY-PLACEHOLDER-NOT-A-CREDENTIAL-A" "RENDER-ONLY-PLACEHOLDER-NOT-A-CREDENTIAL-A") }}' \
  '---' \
  'apiVersion: policy/v1' \
  'kind: PodDisruptionBudget' \
  'metadata:' \
  '  name: drift-test-conditional-pdb' \
  'spec:' \
  '  maxUnavailable: 1' \
  '  selector:' \
  '    matchLabels:' \
  '      app: drift-test' \
  '{{- end }}')"
out="$(run_render "$poison" "$WORK/poisoned-existence.yaml")"; rc=$?
expect_red "an object whose existence depends on the placeholder" "$out" "$rc" \
  "set of compared objects CHANGED"

# ---- 4. NEGATIVE CONTROL: a render that is non-empty but not COMPARABLE ----
# The vacuous-proof shape, and the subtlest of the three: the placeholder influences nothing, every
# surviving object matches across both renders, and the proof would be announced over a chart
# chart-drift-compare.py then refuses to read. Emptying config.yaml drops memex-portal-config while
# the Deployment and the PDB still render, so the "did it render anything at all?" test passes and
# only the per-object requirement can catch it. (Copilot review, #4683.)
echo "case: a render missing a REQUIRED compared object must go RED, not report a proof"
poison="$(emptied_chart drop-required-configmap templates/memex-portal/config.yaml)"
out="$(run_render "$poison" "$WORK/poisoned-missing.yaml")"; rc=$?
expect_red "a render with no memex-portal-config" "$out" "$rc" \
  "does not contain ConfigMap/memex-portal-config"
case "$out" in
  *"independence PROVED"*)
    echo "::error::it went red, but it ALSO announced the independence proof — the vacuous claim"
    echo "    is exactly what this case exists to stop being printed."
    fail=1 ;;
  *) echo "  ok   did not announce a proof it could not make" ;;
esac

# ---- 5. REFUSAL: no host in the values, and none invented ------------------
# The checker's own #3780 rule. Without config.<half>.MEMEX_HOST there is no secret-free source for
# the endpoint, and a checker that manufactured one would be committing the defect it guards.
echo "case: no MEMEX_HOST in the values — refuse, never invent a database host"
out="$(python3 "$RENDER" --chart "$CHART" --namespace check --release rel --out "$WORK/never.yaml" \
        -f "$BASE" -f "$SELF_DIR/testdata/values.adonet-external-db-no-connection-string.yaml" 2>&1)"; rc=$?
expect_red "a values set with no MEMEX_HOST" "$out" "$rc" "does NOT invent a database host"
if [ -s "$WORK/never.yaml" ]; then
  echo "::error::the refusal still wrote a render to disk — a later step could read it as the"
  echo "    desired side and compare the cluster against a chart nobody described."
  fail=1
else
  echo "  ok   wrote no render"
fi

echo
if [ "$fail" -eq 0 ]; then
  echo "chart-drift-render: the record-driven shape renders, the placeholder is provably inert, and"
  echo "all four controls hold — two leaks, the uncomparable render, and the no-host refusal."
else
  echo "::error::chart-drift-render self-test FAILED — see the findings above."
fi
exit "$fail"
