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
#   INSTANCE_PRIVATE  `false` for an installation of this estate, anything else for a client estate's
#                   (a row the private roster names). 🚨 FAILS CLOSED: unset means private. For a
#                   private row this script prints the VERDICT AND COUNTS ONLY — no name, no host,
#                   no response body, no module id, and the verifier's own output is withheld —
#                   because this repository's logs and summaries are public and an installation's
#                   module list is the client's inventory. The verdict is still LANDED on the
#                   instance, in full, where its owner can read it.
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

PRIVATE=true
[ "${INSTANCE_PRIVATE:-}" = "false" ] && PRIVATE=false
if [ "$PRIVATE" = false ]; then
  shown=$INSTANCE_NAME; where=$BASE_URL
else
  shown=${ARTIFACT_TAG:-private}; where="this instance (private roster row)"
fi
# What the audience must be SET TO. `$where` is a description for a private row, never a value to
# configure — so the 401 guidance says where the real value is recorded instead of printing it.
if [ "$PRIVATE" = false ]; then
  audience_value=$BASE_URL
else
  audience_value="the instance's own externally reachable base URL (https:// + the ingress.host of its deployment overlay; withheld here)"
fi
fail() { echo "::error::[$shown] $*"; exit 1; }
note() { echo "[$shown] $*"; }
# A response body, or the fact that one is withheld. Bodies of a private row can name its modules.
body_of() {  # body_of <bytes> <file>
  if [ "$PRIVATE" = false ]; then head -c "$1" "$2"; else printf '(response body withheld: private roster row)'; fi
}

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
    || fail "the runner refused an OIDC token for audience $where: ${body:0:300}"
  value=$(jq -r '.value // ""' <<<"$body")
  [ -n "$value" ] || fail "the runner's OIDC answer carried no token for audience $where"
  echo "::add-mask::$value"
  TOKEN=$value
}

# What a 401 from the instance means, said once. The instance does not tell a refused caller WHICH
# half is missing, so both provisioning acts are named. Neither is a secret in this repository.
unauthorized_hint="The instance did not accept this run's identity. On $where BOTH must hold: (1) the portal declares the audience — config Plugins:Registry:BuildPrincipalAudience = $audience_value (deployment record extraPortalConfig key Plugins__Registry__BuildPrincipalAudience); (2) a global admin of that instance has created Admin/_BuildPrincipal/systemorph--meshweaver granting verify:combo for workflow_run and workflow_dispatch on refs/heads/main (Doc/Architecture/ComboGateWiring → Provisioning an instance). There is no secret to set."

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
[ "$roll_code" != "401" ] || fail "GET $where/api/plugins/roll-target -> HTTP 401. $unauthorized_hint"
[ "$roll_code" = "200" ] \
  || fail "GET $where/api/plugins/roll-target -> HTTP $roll_code. $(body_of 400 "$roll")"

CANDIDATE=$(jq -r '.selected // ""' <"$roll")
CURRENT=$(jq -r '.current // "?"' <"$roll")
# 🚨 The selection is the POLLER's own (#4413): the registry's portal tags under this instance's
# update policy, so a named candidate has a portal image by construction. An instance on an image
# from before that fix still selects over the published root's `_releases` markers, which name a
# set the fleet promoted but has not armed (no `memex-portal-ai:<version>` yet); the pull below then
# goes red naming the image, which is the truth about that instance until it rolls.
#
# An INDETERMINATE selection is "the instance could not look", never "nothing to roll to" — passing
# it as "nothing to verify" would green a slot on no evidence while the poller may still roll.
if [ "$(jq -r '.indeterminate // false' <"$roll")" = "true" ]; then
  if [ "$PRIVATE" = false ]; then
    fail "the instance could not select a roll target (indeterminate): $(jq -r '.environment // ""' <"$roll") — nothing was verified."
  else
    fail "the instance could not select a roll target (indeterminate; text withheld: private roster row) — nothing was verified."
  fi
fi
if [ -z "$CANDIDATE" ] || [ "$CANDIDATE" = "null" ]; then
  # A real, reportable outcome — and deliberately NOT silence. The instance has nothing to roll to,
  # so there is nothing to verify; the summary says so by name.
  note "NOTHING TO VERIFY — the instance selects no roll target (current=$CURRENT)."
  # shellcheck disable=SC2016  # the backticks are MARKDOWN for the step summary, not a subshell
  printf '### %s — nothing to verify\n\nThe instance selects no roll target (currently on `%s`).\n' \
    "$shown" "$CURRENT" >>"$summary"
  exit 0
fi
note "candidate=$CANDIDATE (currently on $CURRENT)"

# 🚨 WHICH REPOSITORY `selected` names (#4413). The version is a tag in the repository THIS
# instance's poller lists, and that is not always memex-portal-ai: the control instance rolls
# memex-control, whose version tag exists before memex-portal-ai's is armed — and for builds that
# never arm it. Pulling memex-portal-ai:<selected> for control failed "not found" on every run whose
# candidate was not armed yet (runs 41020010827, 41017110305), so the slot was red having verified
# nothing. The instance names the repository on the same answer; an image from before it did is
# read as the poller's own default, which is what that image lists.
REPO=$(jq -r '.repository // ""' <"$roll")
if [ -z "$REPO" ]; then
  REPO=memex-portal-ai
  note "the roll target names no repository (the instance runs an image from before #4413 added it) — assuming the poller's default, $REPO."
fi
[[ "$REPO" =~ ^[a-z0-9]+([._/-][a-z0-9]+)*$ ]] \
  || fail "the roll target names a repository that is not a registry path, so no image can be pulled for it — nothing was verified."

# ── 2. What does the instance actually run? ────────────────────────────────────────────────────
combo=$out_dir/combo-$tag.json
mint_token
combo_code=$(curl -sS -o "$combo" -w '%{http_code}' --connect-timeout 15 --max-time 180 \
  -H "Authorization: Bearer $TOKEN" "$BASE_URL/api/plugins/combo") || combo_code=000
[ "$combo_code" != "401" ] || fail "GET $where/api/plugins/combo -> HTTP 401. $unauthorized_hint"
if [ "$combo_code" != "200" ]; then
  fail "GET $where/api/plugins/combo -> HTTP $combo_code. $(body_of 400 "$combo") ::: A 404 here means the instance runs a portal image from before #3544 added the route — roll it to an image that serves /api/plugins/combo first. There is deliberately NO fallback: the only other source (Hosting/ModuleInventory) drops readAt, isComplete, caveats and the per-module sync detail, so a verdict derived from it would be about something other than this instance's real module set."
fi
jq -e 'has("modules")' >/dev/null <"$combo" \
  || fail "the combo read from $where is not an InstanceCombo: $(body_of 400 "$combo")"
if [ "$(jq -r '.isComplete' <"$combo")" != "true" ]; then
  # Stated, never swallowed. The reader folds an unreadable source into caveats rather than
  # faulting, and the verifier can then only answer NotVerifiable — which fails this script below.
  if [ "$PRIVATE" = false ]; then
    note "the instance reports an INCOMPLETE combo: $(jq -c '.caveats' <"$combo")"
  else
    note "the instance reports an INCOMPLETE combo: $(jq -r '.caveats | length' <"$combo") caveat(s), text withheld (private roster row)"
  fi
fi
note "combo: $(jq -r '.modules | length' <"$combo") module(s), readAt=$(jq -r '.readAt' <"$combo")"

# The sources whose sealed module sets the gate needs (MeshWeaver#4413; composed below, once the
# tester's framework identity is known). The publication share is asserted HERE — right after the
# combo read and BEFORE either image is pulled — so a missing `vars.BAKE_PUBLISH_TARGETS` is a red
# naming it, not one that arrives after two image pulls. It is not a `preflight` input: that step
# carries only what is needed to REACH the roster derivation (check-combo-verify.py, part one), and
# only a combo that declares a compiled module needs it.
module_sources=$(jq -r '[.modules[].package | select(. != null and ((.compiledModule // "") | length) > 0)
  | (.sourceName // "") | ascii_downcase] | unique | .[]' <"$combo")
if [ -n "$module_sources" ]; then
  : "${BAKE_PUBLISH_TARGETS:?vars.BAKE_PUBLISH_TARGETS is empty — the sealed module publications cannot be read, so the gate would run without the modules this instance lands}"
fi

# ── 3. Verify ─────────────────────────────────────────────────────────────────────────────────
verdict=$out_dir/combo-verdict-$tag.json
src_args=()
# The source list is deployment-record data. Split its validated space-delimited pairs without
# pathname expansion, so a URL containing shell glob characters stays one literal argument.
IFS=' ' read -r -a source_pairs <<<"$SOURCES"
for s in "${source_pairs[@]}"; do src_args+=(--source "$s"); done

# 🚨 THE GATE HOST — the candidate PORTAL's /app with the candidate set's tester CLI beside it,
# started by the portal image's own `dotnet`: the host every node-repo lane runs since #3022
# (compose-gate-host.sh's header has the argument). Neither image alone is the gate. The portal image
# ships NO /app/mw-plugin-test, so running the tester "inside the candidate image" died in tini with
# exit 127, wrote no report, and landed a NotVerifiable on every live instance as though the candidate
# had been examined (#3848, run 38688576426). The tester image alone is a strict subset of the portal
# (it lacks the AI, Maps, Blazor halves…), so a gate run there reds content that binds them.
# `mw-plugin-test:<version>` is written by the SAME CD run as the portal's `<version>`, in promote's
# phase A — before phase C writes the portal tag a roll-target can name — so it exists whenever the
# candidate does (Doc/Architecture/ImageTagContract). Every docker call is pinned to $PLATFORM.
PORTAL_IMAGE="$ACR/$REPO:$CANDIDATE"
TESTER_IMAGE="$ACR/mw-plugin-test:$CANDIDATE"
host_root="$WORK_ROOT/host-$tag"
rm -rf "$host_root"
mkdir -p "$host_root"
extract_app() {  # extract_app <image> <dest>
  local cid
  docker pull -q --platform "$PLATFORM" "$1" >/dev/null \
    || fail "docker pull $1 ($PLATFORM) failed — the candidate set's image is not available here, so nothing can be verified and nothing is landed."
  cid=$(docker create --platform "$PLATFORM" "$1") \
    || fail "docker create $1 ($PLATFORM) failed, so its /app cannot be read."
  docker cp -q "$cid:/app" "$2" || { docker rm "$cid" >/dev/null; fail "could not copy /app out of $1 ($PLATFORM)."; }
  docker rm "$cid" >/dev/null
}
extract_app "$PORTAL_IMAGE" "$host_root/portal-app"
extract_app "$TESTER_IMAGE" "$host_root/tester-app"
# 🚨 ONE BUILD, asserted — the same assertion node-repo-gate.yml makes before it trusts a composition:
# the tester's `framework-identity` verb reads the portal's /app and refuses unless both resolve one
# framework identity, naming the canonical assemblies each side lacks.
id_line=$(docker run --rm --init --platform "$PLATFORM" --entrypoint /app/mw-plugin-test "$TESTER_IMAGE" --print-framework-identity) \
  || fail "the tester image $TESTER_IMAGE ($PLATFORM) could not print its framework identity — the instrument does not start, so nothing was verified and nothing is landed."
tester_identity="${id_line#*identity=}"; tester_identity="${tester_identity%% *}"
[ -n "$tester_identity" ] || fail "could not read a framework identity from the tester image $TESTER_IMAGE (got: '$id_line')."
docker run --rm --init --platform "$PLATFORM" -v "$host_root/portal-app:/portal:ro" \
  --entrypoint /app/mw-plugin-test "$TESTER_IMAGE" framework-identity /portal --expect "$tester_identity" \
  || fail "$TESTER_IMAGE and $PORTAL_IMAGE do NOT resolve one framework identity (the verb's output above names what each side lacks) — they are not one build, so a gate composed from them would judge neither. Nothing was verified and nothing is landed."
bash .github/scripts/compose-gate-host.sh "$host_root/portal-app" "$host_root/tester-app" "$host_root/host"
note "gate host: $PORTAL_IMAGE /app + the $TESTER_IMAGE CLI, framework identity $tester_identity ($PLATFORM)"

# ───── THE MODULE HALF OF THE COMBO (MeshWeaver#4413) ─────
# 🚨 An instance runs every installed package's COMPILED MODULE (the combo names it per package as
# `package.compiledModule`): the portal image ships only its boot modules, and AI, Chat,
# Collaboration and the rest are LANDED per package. The tester image ships no module at all. So a
# gate handed only the CONTENT registers no `Skill`/`Agent` type, `Store` (first in install order)
# fails "NodeType(s) not registered: Skill", and every later package "NodeType 'Store/Plugin' is not
# registered" — measured as 54 of 54 red on every slot that reached the gate, a verdict about the
# CONTENT whose cause was the INSTRUMENT.
#
# The bytes come from where every gate in the fleet takes them: the publication SEALED for this
# candidate's framework identity, per source (compose-sealed-modules.sh — the unit of consistency,
# no registry fallback), read with the same OIDC login the ACR pull above used, exactly as main-cd's
# `published-modules` job reads it. `--packages '*'` takes each source's whole sealed set; the
# verifier then loads ONLY the modules this combo declares (ComboModuleSet) and names the declared
# ones no seal carries. A source with no seal for this identity is RED here — never a gate run
# without the modules the instance lands.
module_args=()
if [ -n "$module_sources" ]; then
  storage_target="${BAKE_PUBLISH_TARGETS%% *}"
  bundles_root="$host_root/module-bundles"
  rm -rf "$bundles_root"
  mkdir -p "$bundles_root"
  while IFS= read -r src; do
    [ -n "$src" ] || fail "a package in the combo read from $where declares a compiled module but names no source, so there is no sealed publication to take its module from — nothing was verified and nothing is landed."
    mkdir -p "$bundles_root/$src"
    set +e
    if [ "$PRIVATE" = false ]; then
      bash .github/scripts/compose-sealed-modules.sh --identity "$tester_identity" --packages '*' \
        --upstreams "$src" --out "$bundles_root/$src" --storage-target "$storage_target"
    else
      mkdir -p "$WORK_ROOT"
      bash .github/scripts/compose-sealed-modules.sh --identity "$tester_identity" --packages '*' \
        --upstreams "$src" --out "$bundles_root/$src" --storage-target "$storage_target" \
        >>"$WORK_ROOT/modules-$tag.log" 2>&1
    fi
    compose_exit=$?
    set -e
    if [ "$compose_exit" -ne 0 ]; then
      if [ "$PRIVATE" = false ]; then
        fail "the sealed module set of source '$src' for framework identity $tester_identity could not be composed (exit $compose_exit, above) — the gate would run without modules this instance lands, so nothing was verified and nothing is landed."
      else
        fail "a source's sealed module set for framework identity $tester_identity could not be composed (exit $compose_exit; source and output withheld: private roster row) — nothing was verified and nothing is landed."
      fi
    fi
  done <<<"$module_sources"
  module_args=(--module-bundles "$bundles_root")
  note "module half: the sealed module set of $(printf '%s\n' "$module_sources" | grep -c .) source(s) composed for identity $tester_identity"
fi

# 🚨 A private row's verifier output names every module it materialises, so it goes to a file that
# is never uploaded and only its line count is printed. The verdict below carries the counts.
verifier_cmd=(dotnet run --project tools/MeshWeaver.ComboVerifier/MeshWeaver.ComboVerifier.csproj
  -c Release --no-build --
  "$combo" "$PORTAL_IMAGE"
  --host "$host_root/host"
  --tag "$CANDIDATE"
  --verdict "$verdict"
  --work-root "$WORK_ROOT/$tag"
  --platform "$PLATFORM"
  --gate-timeout "$GATE_TIMEOUT"
  "${src_args[@]}"
  ${module_args[@]+"${module_args[@]}"})
set +e
if [ "$PRIVATE" = false ]; then
  "${verifier_cmd[@]}"
else
  mkdir -p "$WORK_ROOT"
  "${verifier_cmd[@]}" >"$WORK_ROOT/verifier-$tag.log" 2>&1
fi
verify_exit=$?
set -e
[ "$PRIVATE" = false ] || note "verifier output withheld (private roster row): $(awk 'END { print NR + 0 }' "$WORK_ROOT/verifier-$tag.log") line(s)"

# 🚨 A missing tester is a broken INSTRUMENT, not a verdict about the candidate: the verifier asserts
# its gate host before assembling anything, writes no verdict, and exits TESTER_MISSING_EXIT
# (DockerImageGate.TesterMissingExitCode). Nothing is landed — an instance must never read "the
# instrument was absent" as "the candidate was examined and could not be verified".
TESTER_MISSING_EXIT=3
[ "$verify_exit" -ne "$TESTER_MISSING_EXIT" ] \
  || fail "the composed gate for $CANDIDATE ($PLATFORM) is not runnable — its host lacks the tester CLI, its runtimeconfig or the portal's manifest, or the combo's compiled modules could not be composed (MeshWeaver#4413) — so there is no instrument to run: nothing was verified and NOTHING was landed. The verifier's message names what is missing (above, or in its withheld log for a private row)."
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
  || fail "POST $where/api/plugins/combo-verification -> HTTP 401: the $KIND verdict for $CANDIDATE was NOT landed. $unauthorized_hint"
[ "$land_code" = "200" ] \
  || fail "POST $where/api/plugins/combo-verification -> HTTP $land_code: the $KIND verdict for $CANDIDATE was NOT landed: $(body_of 400 "$landed")"
jq -e '.recorded == true' >/dev/null <"$landed" \
  || fail "the instance answered 200 but did not confirm the verdict was recorded: $(body_of 400 "$landed")"
note "landed: $(body_of 200 "$landed")"

{
  # shellcheck disable=SC2016  # the backticks are MARKDOWN for the step summary, not a subshell
  printf '### %s — `%s` → **%s**\n\n' "$shown" "$CANDIDATE" "$KIND"
  jq -r '"- modules: \(.modules | length), passed: \([.modules[] | select(.outcome == "Passed")] | length), failed: \([.modules[] | select(.outcome == "Failed")] | length)"' <"$verdict"
  if [ "$PRIVATE" = false ]; then
    jq -r '.modules[] | select(.outcome != "Passed") | "  - `\(.moduleId)` \(.outcome): \(.failures | join("; "))"' <"$verdict"
    jq -r '.caveats[]? | "  - caveat: \(.)"' <"$verdict"
  else
    jq -r '"- caveats: \(.caveats // [] | length). Module ids, failures and caveat text are withheld for a private roster row; the full verdict is landed on the instance."' <"$verdict"
  fi
} >>"$summary"

# 🚨 Exit AFTER landing, and Green is the ONLY pass. A Red says this candidate cannot serve the
# modules this instance runs — delivery promoted something a live instance must refuse. A
# NotVerifiable says nothing was checked at all, which is the one outcome a gate must never paint
# green: it is "the gate never ran" wearing the colour of "the gate passed".
[ "$KIND" = "Green" ] \
  || fail "verdict is $KIND for $CANDIDATE. It IS landed on $where, so the instance's own gate will act on it; this run is red because a candidate a live instance cannot take has not been delivered."
note "GREEN"
