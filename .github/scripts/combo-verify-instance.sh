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
#   INSTANCE_NAME   the instance's name, for the log lines and the summary (both are masked by the
#                   runner when the name is a private one)
#   ARTIFACT_TAG    what the files this script writes are named after — the workflow passes the
#                   matrix SLOT (`slot-3`). 🚨 Never the name: an artifact's name and the file names
#                   inside it are NOT masked, and this repository's artifacts are public (#3848).
#                   Defaults to INSTANCE_NAME for a local run.
#   BASE_URL        e.g. https://memex.systemorph.com  (no trailing slash)
#   ACTIONS_ID_TOKEN_REQUEST_URL / ACTIONS_ID_TOKEN_REQUEST_TOKEN
#                   set by the runner when the job holds `id-token: write`. The run's OWN identity
#                   is the only credential: a GitHub Actions OIDC token minted per call with
#                   audience = BASE_URL, which the instance verifies against GitHub's JWKS and
#                   resolves to its `Admin/_BuildPrincipal/systemorph--meshweaver` node (grant
#                   `verify:combo`). No key and no token is stored anywhere (#3848).
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

# ── 0. The run's own identity — minted per call, never stored ──────────────────────────────────
# 🚨 Minted FRESH for every request, never once per script. The token lives for minutes and the
# verification in step 3 can run for most of GATE_TIMEOUT, so a token minted up front would be
# expired by the time the verdict is landed — and the landing is the one call that must not fail.
#
# The AUDIENCE is the instance's own base URL. The portal accepts a build token only for an audience
# it declares (`Plugins:Registry:BuildPrincipalAudience`), so a token minted for one instance cannot
# be replayed at another.
[ -n "${ACTIONS_ID_TOKEN_REQUEST_URL:-}" ] && [ -n "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:-}" ] \
  || fail "no GitHub Actions OIDC token can be requested (ACTIONS_ID_TOKEN_REQUEST_URL/TOKEN unset). The job must hold 'permissions: id-token: write' — the run's own identity is the ONLY credential this lander uses."

mint_token() {
  local body value
  body=$(curl -sS --fail-with-body --connect-timeout 15 --max-time 60 \
    -H "Authorization: bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" \
    "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=$(jq -rn --arg a "$BASE_URL" '$a | @uri')") \
    || fail "the runner refused an OIDC token for audience $BASE_URL: $(head -c 300 <<<"${body:-}")"
  value=$(jq -r '.value // ""' <<<"$body")
  [ -n "$value" ] || fail "the runner's OIDC answer carried no token for audience $BASE_URL"
  echo "::add-mask::$value"
  TOKEN=$value
}

# What a 401 from the instance means, said once. The instance does not tell a refused caller WHICH
# half is missing, so both provisioning acts are named. Neither is a secret in this repository.
unauthorized_hint="The instance did not accept this run's identity. On $BASE_URL BOTH must hold: (1) the portal declares the audience — config Plugins:Registry:BuildPrincipalAudience = $BASE_URL (deployment record extraPortalConfig key Plugins__Registry__BuildPrincipalAudience); (2) a global admin of that instance has created Admin/_BuildPrincipal/systemorph--meshweaver granting verify:combo for workflow_run and workflow_dispatch on refs/heads/main (Doc/Architecture/ComboGateWiring → Provisioning an instance). There is no secret to set."

out_dir=${GITHUB_WORKSPACE:-$PWD}
tag=${ARTIFACT_TAG:-$INSTANCE_NAME}
summary=${GITHUB_STEP_SUMMARY:-/dev/null}

# ── 1. Which candidate would THIS instance roll to? ───────────────────────────────────────────
# Asked OF THE INSTANCE rather than derived here, because "the newest tag" is not the question the
# gate answers: ReleaseAvailabilityService already walks the completeness rule and names the release
# this environment would actually take. Re-deriving it here would be a second rule.
roll=$out_dir/combo-rolltarget-$tag.json
mint_token
roll_code=$(curl -sS -o "$roll" -w '%{http_code}' --connect-timeout 15 --max-time 120 \
  -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/plugins/roll-target") || roll_code=000
[ "$roll_code" != "401" ] || fail "GET $BASE_URL/api/plugins/roll-target -> HTTP 401. $unauthorized_hint"
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
combo=$out_dir/combo-$tag.json
mint_token
combo_code=$(curl -sS -o "$combo" -w '%{http_code}' --connect-timeout 15 --max-time 180 \
  -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/plugins/combo") || combo_code=000
[ "$combo_code" != "401" ] || fail "GET $BASE_URL/api/plugins/combo -> HTTP 401. $unauthorized_hint"
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
verdict=$out_dir/combo-verdict-$tag.json
src_args=()
# The source list is deployment-record data. Split its validated space-delimited pairs without
# pathname expansion, so a URL containing shell glob characters stays one literal argument.
IFS=' ' read -r -a source_pairs <<<"$SOURCES"
for s in "${source_pairs[@]}"; do src_args+=(--source "$s"); done

set +e
dotnet run --project tools/MeshWeaver.ComboVerifier/MeshWeaver.ComboVerifier.csproj \
  -c Release --no-build -- \
  "$combo" "$ACR/memex-portal-ai:$CANDIDATE" \
  --tag "$CANDIDATE" \
  --verdict "$verdict" \
  --work-root "$WORK_ROOT/$tag" \
  --platform "$PLATFORM" \
  --gate-timeout "$GATE_TIMEOUT" \
  "${src_args[@]}"
verify_exit=$?
set -e

[ -f "$verdict" ] || fail "mw-combo-verify exited $verify_exit and wrote no verdict file — nothing was verified, so there is nothing to land."
KIND=$(jq -r '.verdict' <"$verdict")
note "verdict=$KIND for $CANDIDATE (tool exit $verify_exit)"

# ── 4. LAND it — through the instance's own recording route, as the run's own identity. ──────
# POST /api/plugins/combo-verification takes the ComboVerification the tool wrote (serialized with
# the very options the endpoint reads it with) and records it through
# UpdatePolicyNodeType.RecordVerification — upsert by candidateTag, newest first, capped — so the
# merge rule exists ONCE, in the portal, and this script no longer re-implements it over a raw mesh
# patch with a global admin's token. 🚨 The route answers 200 only AFTER Admin/UpdatePolicy carries
# this exact verdict, so a 200 here IS the landing. The policy mode (including None) is preserved.
landed=$out_dir/combo-landed-$tag.json
mint_token
land_code=$(curl -sS -o "$landed" -w '%{http_code}' --connect-timeout 15 --max-time 120 \
  -X POST "$BASE_URL/api/plugins/combo-verification" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  --data-binary "@$verdict") || land_code=000
[ "$land_code" != "401" ] \
  || fail "POST $BASE_URL/api/plugins/combo-verification -> HTTP 401: the $KIND verdict for $CANDIDATE was NOT landed. $unauthorized_hint"
[ "$land_code" = "200" ] \
  || fail "POST $BASE_URL/api/plugins/combo-verification -> HTTP $land_code: the $KIND verdict for $CANDIDATE was NOT landed: $(head -c 400 "$landed")"
jq -e '.recorded == true' >/dev/null <"$landed" \
  || fail "the instance answered 200 but did not confirm the verdict was recorded: $(head -c 400 "$landed")"
note "landed: $(head -c 200 "$landed")"

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
