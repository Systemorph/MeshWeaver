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
#
# THREE SIDES. Each case carries a release-manifest.yaml as well: D = desired.yaml (what the chart
# renders), L = the live-*.json (what the cluster runs), M = the manifest (what helm previously
# owned). Sections 1 and 2 exercise D vs L; section 3 exercises D vs M, which is where the ONE
# deletion hazard lives — and section 4 asserts the comparator refuses to run at all without M,
# because an unread manifest makes every finding look like the harmless half.
set -uo pipefail
SELF_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DATA="$SELF_DIR/testdata/chart-drift"
CMP="$SELF_DIR/chart-drift-compare.py"
fail=0

run_case_with_manifest() {  # run_case_with_manifest <case> <release-manifest-path>
  local case="$1"
  python3 "$CMP" \
    "$DATA/desired.yaml" \
    "$DATA/$case/live-configmap.json" \
    "$DATA/$case/live-deployment.json" \
    "$DATA/$case/expect-patch.json" \
    "$DATA/$case/live-poddisruptionbudgets.json" \
    "$DATA/$case/live-scaledobjects.json" \
    "$DATA/$case/envfrom-source-keys.txt" \
    "$2" 2>&1
}

run_case() {
  run_case_with_manifest "$1" "$DATA/$1/release-manifest.yaml"
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

# ...nor a PROVENANCE. Reading a key NAME says nothing about where that key's value came from. The
# message asserted "the Key Vault credential went unused" until 2026-09-04, when re-measuring memex
# showed the shadowed secret was helm-rendered, that its namespace's CSI-backed secret does not carry
# the key, and that the vault holds no such entry at all. A checker that narrates an unmeasured
# provenance teaches its reader a wrong fix.
if echo "$out" | grep -A1 'SHADOWS *inline env PluginCatalog__RegistryToken' | grep -qi 'key vault'; then
  echo "::error::the secret-backed shadow claims a Key Vault provenance. Only the key NAME is read"
  echo "         from that source — where its value came from is not observable here."
  fail=1
else
  echo "  ok   a secret-backed shadow claims no provenance"
fi

# ...and it MUST warn that a credential shadow can be two PRINCIPALS, not two values of one setting.
# On memex both sides were valid registry instance keys of DIFFERENT registered instances, so the
# reflex cleanup would have re-identified the portal and widened its entitlement. "Establish which
# value is live" is not sufficient advice for a credential, and that is the whole lesson of #3201.
if echo "$out" | grep -A1 'SHADOWS *inline env PluginCatalog__RegistryToken' \
     | grep -q 'DIFFERENT registered instances'; then
  echo "  ok   a secret-backed shadow warns the two sides may be different principals"
else
  echo "::error::the secret-backed shadow does not warn that the two sides may be different"
  echo "         PRINCIPALS. Deleting the inline entry can re-identify the deployment, not merely"
  echo "         change a value — see MeshWeaver#3201."
  fail=1
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

# An inline env the CHART renders and the pod also carries, with a different value. Until
# 2026-09-03 this shape was invisible: the comparator matched the two by NAME and skipped, having
# counted the comparison, so `kubectl set env` over one of the chart's own entries reported nothing.
expect_class "DIFFERS"      "inline env AZURE_CLIENT_ID"
# ...and it must not print either value — an inline env can hold a token.
if echo "$out" | grep -A1 'DIFFERS *inline env AZURE_CLIENT_ID' | grep -qE '00000000|99999999'; then
  echo "::error::the inline-env DIFFERS finding PRINTS the values it compared. Inline env entries"
  echo "         hold tokens; the finding may say that they differ and nothing more."
  fail=1
else
  echo "  ok   an inline-env DIFFERS withholds both values"
fi
# The CONTROL for that comparison: an entry identical on both sides must NOT be reported. Without
# this, a comparator that flagged every both-sides entry would pass the assertion above.
if echo "$out" | grep -q 'inline env DOTNET_DbgMiniDumpType'; then
  echo "::error::DOTNET_DbgMiniDumpType is IDENTICAL on both sides and was reported anyway — the"
  echo "         both-sides comparison flags everything, which is not a comparison."
  fail=1
else
  echo "  ok   an inline env identical on both sides is not reported"
fi
# A same-name entry whose SHAPE changed (chart literal vs live valueFrom) is not the same setting.
expect_class "DIFFERS"      "inline env DOTNET_DbgMiniDumpName"
# An inline env over a key from a SECOND envFrom ConfigMap. `.Values.extraEnvFrom` takes
# `{configMapRef: …}` as well as `{secretRef: …}`, so enumerating only secrets would leave exactly
# the blind spot #3204 closed, one source-kind over.
expect_class "SHADOWS"      "inline env Commerce__BaseUrl"
if echo "$out" | grep -A1 'SHADOWS *inline env Commerce__BaseUrl' | grep -q 'configmap/portal-extra'; then
  echo "  ok   a shadow over a second envFrom ConfigMap names that ConfigMap"
else
  echo "::error::the Commerce__BaseUrl finding does not name the envFrom ConfigMap supplying it."
  fail=1
fi
if echo "$out" | grep -A1 'SHADOWS *inline env Commerce__BaseUrl' | grep -q 'THE TWO DISAGREE'; then
  echo "::error::the second-ConfigMap shadow asserts a value verdict it cannot have — only that"
  echo "         source's key NAMES are read, so agreement is unknown and must not be stated."
  fail=1
else
  echo "  ok   a shadow over a key-names-only source states no value verdict"
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

# ---- 3. PENDING DELETIONS — the third side, D vs M -------------------------
# D = the chart as CI renders it, L = the live objects, M = the last-deployed release manifest.
# Every assertion above compares D against L. The deletion hazard is not in that comparison: a key
# that is in L and in M but NOT in D is a chart RETIREMENT that has not landed — helm owned it, so
# the three-way merge removes it on the next upgrade. That is the ONE cluster-only shape a deploy
# destroys, and until the comparator computed it, all 36 findings of 2026-09-04 were reported as
# the harmless half and the 13 owned ones were split out BY HAND, once, by one person.
expect_detail() {  # expect_detail <finding-regex> <substring> <why it matters>
  if echo "$out" | grep -A1 -e "$1" | grep -qF -- "$2"; then
    echo "  ok   $1 → says '$2'"
  else
    echo "::error::the finding matching '$1' does not say '$2' — $3. Actual:"
    echo "$out" | grep -A1 -e "$1" | sed 's/^/      /'
    fail=1
  fi
}
refute_detail() {  # refute_detail <finding-regex> <substring> <why it matters>
  if echo "$out" | grep -A1 -e "$1" | grep -qF -- "$2"; then
    echo "::error::the finding matching '$1' says '$2' and must not — $3. Actual:"
    echo "$out" | grep -A1 -e "$1" | sed 's/^/      /'
    fail=1
  else
    echo "  ok   $1 → does not say '$2'"
  fi
}

# POSITIVE: live + in the manifest + no longer rendered, with a NON-EMPTY value. A real removal.
# (Its CLASS is asserted in section 2 — the class does not change, the verdict inside it does.)
expect_detail 'CLUSTER-ONLY *ConfigMap LogWatch__DefaultRepository' 'PENDING DELETION' \
  "helm owns this key and the chart no longer renders it, so the next upgrade removes it"
expect_detail 'CLUSTER-ONLY *ConfigMap LogWatch__DefaultRepository' 'NON-EMPTY' \
  "the VALUE decides whether the deletion takes anything away, and this one does"
# ...and the value itself is never printed, only its weight.
refute_detail 'CLUSTER-ONLY *ConfigMap LogWatch__DefaultRepository' 'NO-OP' \
  "a non-empty value is not a no-op deletion"

# ZERO-LENGTH: the same shape over an empty value. All 13 owned-but-retired keys measured on
# 2026-09-04 were zero-length, so reading only the key NAMES would have raised an alarm about
# deleting nothing at all. The two cases must be distinguishable in the report.
expect_class  "CLUSTER-ONLY" "ConfigMap Authentication__DevAdminUsers"
expect_detail 'CLUSTER-ONLY *ConfigMap Authentication__DevAdminUsers' 'PENDING DELETION' \
  "it is in the manifest and unrendered, exactly like the case above"
expect_detail 'CLUSTER-ONLY *ConfigMap Authentication__DevAdminUsers' 'NO-OP' \
  "an empty value means the removal changes nothing the pod reads, and saying so is the point"

# NEVER-OWNED: live, unrendered, and NOT in the manifest. It survives every deploy — the migration
# backlog, not a hazard. If this ever reads as a pending deletion the split has collapsed one way.
expect_class  "CLUSTER-ONLY" "ConfigMap Ops__Contact"
refute_detail 'CLUSTER-ONLY *ConfigMap Ops__Contact' 'PENDING DELETION' \
  "helm never owned it, so no upgrade removes it"
expect_detail 'CLUSTER-ONLY *ConfigMap Ops__Contact' 'PRESERVES' \
  "the never-owned half must still say a deploy leaves it alone"

# The same split over an inline env entry, both halves. (Copilot's class is asserted in section 2.)
expect_detail 'CLUSTER-ONLY *inline env Features__Ai__Clis__Copilot' 'PENDING DELETION' \
  "the manifest carries this entry, so helm owns it"
expect_detail 'CLUSTER-ONLY *inline env Features__Ai__Clis__Copilot' 'NON-EMPTY' \
  "an inline env's emptiness is readable and decides what the removal costs"
expect_class  "CLUSTER-ONLY" "inline env Legacy__Flag"
expect_detail 'CLUSTER-ONLY *inline env Legacy__Flag' 'NO-OP' \
  "an empty inline env deletes to nothing"
expect_class  "CLUSTER-ONLY" "inline env Tok"
refute_detail 'CLUSTER-ONLY *inline env Tok' 'PENDING DELETION' \
  "it is not in the manifest, so helm never owned it"

# ...and no inline-env pending deletion may print the value it weighed — they hold tokens.
if echo "$out" | grep -A1 'CLUSTER-ONLY *inline env Features__Ai__Clis__Copilot' | grep -qE "'true'"; then
  echo "::error::the inline-env pending deletion PRINTS the value it weighed. Inline env entries"
  echo "         hold tokens; the finding may state emptiness and length and nothing more."
  fail=1
else
  echo "  ok   an inline-env pending deletion withholds the value"
fi

# 🚨 THE NEGATIVE CONTROL. Retention__Days is live AND in the manifest AND still rendered, with the
# same value on both sides. It must not be reported at ALL. Without this the whole check could pass
# by flagging every key the manifest happens to carry, which is not a comparison.
if echo "$out" | grep -q 'Retention__Days'; then
  echo "::error::Retention__Days is live, helm-owned AND still rendered — it is not drift of any"
  echo "         kind, and it was reported anyway. The manifest comparison flags everything."
  fail=1
else
  echo "  ok   a key that is live, helm-owned and still rendered is not reported"
fi

# The count the report now carries so nobody re-derives it by hand next run.
if echo "$out" | grep -q '4 of the CLUSTER-ONLY finding(s) are PENDING DELETIONS'; then
  echo "  ok   the summary counts the pending deletions"
else
  echo "::error::the summary does not count the 4 seeded pending deletions. That count was a hand"
  echo "         measurement (13 of 36 on 2026-09-04) that nothing recomputed on the next run."
  echo "$out" | grep -i 'pending deletion' | sed 's/^/      /'
  fail=1
fi

# ---- 4. FAIL CLOSED on the manifest ---------------------------------------
# 🚨 AGENTS.md → "A gate NEVER tests its own inputs". An unreadable manifest makes every live-only
# key look never-owned — i.e. harmless — so treating it as optional would turn "could not check"
# into "nothing to worry about", silently. Each of the three ways it can be absent must be RED and
# must name the command that produces it.
echo "case: fail-closed (the release manifest is a required input)"
assert_fails_naming_manifest() {  # assert_fails_naming_manifest <description> <manifest-arg>
  local o rc
  o="$(run_case_with_manifest clean "$2")"; rc=$?
  if [ "$rc" -eq 0 ]; then
    echo "::error::$1 — the comparator exited 0. A manifest it could not read must be a FAILURE,"
    echo "         never a silent 'no pending deletions'."
    fail=1
  elif ! echo "$o" | grep -q 'helm get manifest'; then
    echo "::error::$1 — it failed, but without naming \`helm get manifest\`, so the reader is not"
    echo "         told what to provide. Output:"
    echo "$o" | sed 's/^/      /'
    fail=1
  else
    echo "  ok   $1 → fails RED and names \`helm get manifest\`"
  fi
}
assert_fails_naming_manifest "an EMPTY manifest argument" ""
MISSING="$DATA/clean/this-manifest-does-not-exist.yaml"
assert_fails_naming_manifest "a manifest path that does not exist" "$MISSING"
EMPTY_MANIFEST="$(mktemp)"
: > "$EMPTY_MANIFEST"
assert_fails_naming_manifest "a manifest file with no objects in it" "$EMPTY_MANIFEST"
rm -f "$EMPTY_MANIFEST"

if [ "$fail" -eq 0 ]; then
  echo ""
  echo "chart-drift comparator: all classification assertions passed."
else
  echo ""
  echo "::error::chart-drift comparator self-test FAILED"
fi
exit "$fail"
