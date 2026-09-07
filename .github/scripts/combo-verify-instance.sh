#!/usr/bin/env bash
# Verify ONE candidate against ONE live instance, and LAND the verdict on that instance.
#
# The producer half of the combo gate (issue #3544). `ComboVerificationGate` on every portal
# consults `Admin/UpdatePolicy` → `content.comboVerifications` before it applies a self-update, and
# until this script existed nothing in the fleet ever wrote one: `mw-combo-verify` was built, was
# documented in Doc/Architecture/CandidateReleaseProtocol, and was invoked by no workflow — so every
# roll ran UNVERIFIED and the gate that exists to notice could not.
#
# 🚨 The verdict is LANDED BEFORE the exit code is decided. A Red that never reaches the instance is
# worse than no verdict at all: the instance's gate would clear a candidate this run proved cannot
# serve it.
#
# 🚨 Every failure here is LOUD and names what to do. There is no `|| true` and no `2>/dev/null` on
# anything whose value is then read — the shape that turns "the call failed" into "the answer is
# empty" (MeshWeaver#2642) and leaves no trace in the log or the exit code.
#
# Inputs, all environment. A missing one is a PREFLIGHT failure in the workflow, never a skip here.
#   INSTANCE_NAME   the instance's name, for the summary and the artifact names
#   BASE_URL        e.g. https://memex.systemorph.com  (no trailing slash)
#   INSTANCE_KEY    mwi_… — the instance-registry key. Reads roll-target + combo.
#   ADMIN_TOKEN     mw_…  — an API token of a GLOBAL ADMIN on that instance. Lands the verdict.
#   ACR             e.g. meshweaver.azurecr.io
#   SOURCES         space-separated name=url pairs for --source
#   GITHUB_TOKEN    read access to the module repositories
#   GATE_TIMEOUT    seconds; comfortably under the job's timeout-minutes so the TOOL's budget
#                   fires first and says what did not complete
#   PLATFORM        docker platform to verify, e.g. linux/amd64
#   WORK_ROOT       materialisation root
set -euo pipefail

fail() { echo "::error::[$INSTANCE_NAME] $*"; exit 1; }
note() { echo "[$INSTANCE_NAME] $*"; }

out_dir=${GITHUB_WORKSPACE:-$PWD}
summary=${GITHUB_STEP_SUMMARY:-/dev/null}

# ── 1. Which candidate would THIS instance roll to? ───────────────────────────────────────────
# Asked OF THE INSTANCE rather than derived here, because "the newest tag" is not the question the
# gate answers: ReleaseAvailabilityService already walks the completeness rule and names the release
# this environment would actually take. Re-deriving it here would be a second rule.
roll=$out_dir/combo-rolltarget-$INSTANCE_NAME.json
roll_code=$(curl -sS -o "$roll" -w '%{http_code}' --connect-timeout 15 --max-time 120 \
  -H "Authorization: Bearer $INSTANCE_KEY" "$BASE_URL/api/plugins/roll-target") || roll_code=000
[ "$roll_code" = "200" ] \
  || fail "GET $BASE_URL/api/plugins/roll-target -> HTTP $roll_code. $(head -c 400 "$roll")"

CANDIDATE=$(jq -r '.selected // ""' <"$roll")
CURRENT=$(jq -r '.current // "?"' <"$roll")
if [ -z "$CANDIDATE" ] || [ "$CANDIDATE" = "null" ]; then
  # A real, reportable outcome — and deliberately NOT silence. The instance has nothing to roll to,
  # so there is nothing to verify; the summary says so by name.
  note "NOTHING TO VERIFY — the instance selects no roll target (current=$CURRENT)."
  # shellcheck disable=SC2016  # the backticks are MARKDOWN for the step summary, not a subshell
  printf '### %s — nothing to verify\n\nThe instance selects no roll target (currently on `%s`).\n' \
    "$INSTANCE_NAME" "$CURRENT" >>"$summary"
  exit 0
fi
note "candidate=$CANDIDATE (currently on $CURRENT)"

# ── 2. What does the instance actually run? ────────────────────────────────────────────────────
combo=$out_dir/combo-$INSTANCE_NAME.json
combo_code=$(curl -sS -o "$combo" -w '%{http_code}' --connect-timeout 15 --max-time 180 \
  -H "Authorization: Bearer $INSTANCE_KEY" "$BASE_URL/api/plugins/combo") || combo_code=000
if [ "$combo_code" != "200" ]; then
  fail "GET $BASE_URL/api/plugins/combo -> HTTP $combo_code. $(head -c 400 "$combo") ::: A 404 here means the instance runs a portal image from before #3544 added the route — roll it to an image that serves /api/plugins/combo first. There is deliberately NO fallback: the only other source (Hosting/ModuleInventory) drops readAt, isComplete, caveats and the per-module sync detail, so a verdict derived from it would be about something other than this instance's real module set."
fi
jq -e 'has("modules")' >/dev/null <"$combo" \
  || fail "the combo read from $BASE_URL is not an InstanceCombo: $(head -c 400 "$combo")"
if [ "$(jq -r '.isComplete' <"$combo")" != "true" ]; then
  # Stated, never swallowed. The reader folds an unreadable source into caveats rather than
  # faulting, and the verifier can then only answer NotVerifiable — which fails this script below.
  note "the instance reports an INCOMPLETE combo: $(jq -c '.caveats' <"$combo")"
fi
note "combo: $(jq -r '.modules | length' <"$combo") module(s), readAt=$(jq -r '.readAt' <"$combo")"

# ── 3. Verify ─────────────────────────────────────────────────────────────────────────────────
verdict=$out_dir/combo-verdict-$INSTANCE_NAME.json
src_args=()
for s in $SOURCES; do src_args+=(--source "$s"); done

set +e
dotnet run --project tools/MeshWeaver.ComboVerifier/MeshWeaver.ComboVerifier.csproj \
  -c Release --no-build -- \
  "$combo" "$ACR/memex-portal-ai:$CANDIDATE" \
  --tag "$CANDIDATE" \
  --verdict "$verdict" \
  --work-root "$WORK_ROOT/$INSTANCE_NAME" \
  --platform "$PLATFORM" \
  --gate-timeout "$GATE_TIMEOUT" \
  "${src_args[@]}"
verify_exit=$?
set -e

[ -f "$verdict" ] || fail "mw-combo-verify exited $verify_exit and wrote no verdict file — nothing was verified, so there is nothing to land."
KIND=$(jq -r '.verdict' <"$verdict")
note "verdict=$KIND for $CANDIDATE (tool exit $verify_exit)"

# ── 4. LAND it. Read-merge-write, mirroring UpdatePolicyNodeType.RecordVerification exactly. ───
# 🚨 An RFC 7396 merge patch REPLACES an array wholesale, so the whole list has to be sent. The
# merge rule is not invented here — upsert by candidateTag (case-insensitive), newest first, capped
# at MaxRecordedVerifications = 8 — it is the one RecordVerification applies in-process.
policy=$out_dir/combo-policy-$INSTANCE_NAME.json
get_code=$(curl -sS -o "$policy" -w '%{http_code}' --connect-timeout 15 --max-time 120 \
  -X POST "$BASE_URL/api/mesh/get" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d '{"path":"Admin/UpdatePolicy"}') || get_code=000
[ "$get_code" = "200" ] \
  || fail "POST $BASE_URL/api/mesh/get Admin/UpdatePolicy -> HTTP $get_code. $(head -c 400 "$policy")"
# 🚨 The mesh API ships its OWN failures with HTTP 200 — the body is the verdict, not the status.
if ! jq -e 'type == "object"' >/dev/null <"$policy"; then
  fail "reading Admin/UpdatePolicy did not return a node: $(head -c 400 "$policy")"
fi

request=$out_dir/combo-patch-$INSTANCE_NAME.json
jq -n --slurpfile p "$policy" --slurpfile v "$verdict" '
  ($v[0].candidateTag // "" | ascii_downcase) as $tag
  | (($p[0].content.comboVerifications // [])
      | map(select((.candidateTag // "" | ascii_downcase) != $tag)))
    + [$v[0]]
  | sort_by(.verifiedAt) | reverse | .[0:8]
  | { path: "Admin/UpdatePolicy", fields: ({ content: { comboVerifications: . } } | tojson) }
' >"$request"

patched=$out_dir/combo-patched-$INSTANCE_NAME.txt
patch_code=$(curl -sS -o "$patched" -w '%{http_code}' --connect-timeout 15 --max-time 120 \
  -X POST "$BASE_URL/api/mesh/patch" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d "@$request") || patch_code=000
[ "$patch_code" = "200" ] \
  || fail "POST $BASE_URL/api/mesh/patch -> HTTP $patch_code. $(head -c 400 "$patched")"
grep -q 'Patched:' "$patched" \
  || fail "the verdict did not land on Admin/UpdatePolicy: $(head -c 400 "$patched")"
note "landed: $(head -c 200 "$patched")"

{
  # shellcheck disable=SC2016  # the backticks are MARKDOWN for the step summary, not a subshell
  printf '### %s — `%s` → **%s**\n\n' "$INSTANCE_NAME" "$CANDIDATE" "$KIND"
  jq -r '"- modules: \(.modules | length), passed: \([.modules[] | select(.outcome == "Passed")] | length), failed: \([.modules[] | select(.outcome == "Failed")] | length)"' <"$verdict"
  jq -r '.modules[] | select(.outcome != "Passed") | "  - `\(.moduleId)` \(.outcome): \(.failures | join("; "))"' <"$verdict"
  jq -r '.caveats[]? | "  - caveat: \(.)"' <"$verdict"
} >>"$summary"

# 🚨 Exit AFTER landing, and Green is the ONLY pass. A Red says this candidate cannot serve the
# modules this instance runs — delivery promoted something a live instance must refuse. A
# NotVerifiable says nothing was checked at all, which is the one outcome a gate must never paint
# green: it is "the gate never ran" wearing the colour of "the gate passed".
[ "$KIND" = "Green" ] \
  || fail "verdict is $KIND for $CANDIDATE. It IS landed on $BASE_URL, so the instance's own gate will act on it; this run is red because a candidate a live instance cannot take has not been delivered."
note "GREEN"
