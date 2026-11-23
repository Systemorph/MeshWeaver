#!/usr/bin/env python3
"""Prove the combo-verify lane's shell: the preflight can FAIL, and the lander holds no credential.

WHY THIS EXISTS
---------------
`combo-verify.yml` is the producer half of the combo gate (MeshWeaver#3544). Its `preflight` job is
the ONLY thing standing between "nothing was derived" and a lane that quietly verifies
nothing — and the whole file fires on `workflow_run` / `workflow_dispatch`, never on
`pull_request`, so an edit to it merges on the strength of being valid YAML and first executes in
production. That is the same blind spot `check-workflow-shell.py` was written for (#2642): CI's own
shell, unopened by anything.

A gate you have never seen fail is not a gate. This script EXECUTES the preflight's real `run:`
text — extracted from the shipped YAML by path, never retyped — under the input scenarios listed
below, and asserts the exit code AND that the message names the specific shortfall. 🚨 No total is
stated here or in the prose that describes this script: a count in a comment has no mechanism keeping
it true, and adding a scenario has now invalidated such a sentence twice. The script's own summary
line carries the number, and `--self-test` carries how many of them a gutted preflight must fail.

🚨 THE ROSTER AND SOURCE MAP ARE DERIVED, SO THE PREFLIGHT ASSERTS IN TWO STEPS (#3848), and this
drives both. `derive-combo-instances.py` reads the fleet's deployment overlays and records between
them. So `assert` asks whether the inputs that come from outside the tree exist at all, and `roster`
— which cannot run before the derivation — asks whether the derivation produced a real roster and
the derived source map arrived intact. Splitting the assertion split the
scenarios with it:

🚨 AND THE SPLIT IS BY WHETHER AN INPUT IS NEEDED TO *REACH* THE DERIVATION — not by what is knowable
yet, and NOT by whether provisioning it is reversible. The two `COMBO_VERIFY_*` credential maps (since removed, #3848) were
asserted in `roster`, after the derivation; `SOURCES` is derived from `DeploymentContent.PluginRepos`.
`assert` carries only `AZURE_*` (the login) and `FLEET_READER_*` (the token the derivation reads the
records with). The workflow's own history is the argument: every run
in it died in `assert`, so the derivation had NEVER ONCE executed in CI and the lane's red named
absent inputs while the state that must change first was invisible to every reader.

🚨 AN EARLIER VERSION OF THIS SPLIT GOT THE RULE WRONG: it kept `vars.COMBO_VERIFY_SOURCES` in
`assert` because it was plain data, free to provision and free to correct. That input is now removed;
the source map is derived from deployment records. The spend argument for the two credential maps is
still why their minting guidance must not be emitted early; it is simply not the criterion for
whether an input belongs in `assert`. Any external input needed to reach the derivation belongs
there, and nothing else.

  assert:  (only what is needed to REACH the derivation)
  1. nothing provisioned                    → RED, naming secrets.FLEET_READER_APP_ID — the input
                                              without which the derivation cannot even be attempted.
  2. ONLY what this repository actually      → GREEN, and this is the scenario that would have caught
     has provisioned                          the earlier mistake. Every other case starts from
                                              FULLY_PROVISIONED, which holds external inputs CONSTANT
                                              at "present"; production had the credential maps
                                              ABSENT. So the guard was green over a preflight that
                                              reddened one step above the derivation in the only
                                              configuration that matters. Spelled as production's own
                                              input state, it goes RED if an unprovisioned input is
                                              ever added to this step again.
  3. everything provisioned                 → GREEN

  roster:  (what is only answerable, or only worth answering, once the installations are known)
  3a. the derived source map absent         → RED, naming the missing deployment-record output.
  4. the derivation emitted NOTHING         → RED. This is the one that matters most: an empty or
     (and the same for an EMPTY array)        absent roster yields an empty matrix, an empty matrix
                                              SKIPS the verify job, and GitHub paints a skipped job
                                              the same colour as a passed one. "The gate never ran"
                                              and "the gate passed" must never be the same pixel.
                                              🚨 A DERIVED zero paints exactly the green a DECLARED
                                              zero did, which is why this scenario did not move
                                              with the input it used to be about.
  5. a derived roster and source map        → GREEN, and it emits the matrix it promised. There is no
                                              credential to assert (#3848): each instance is reached
                                              as the run's own OIDC identity, so a missing grant is a
                                              401 in the verify job naming both provisioning halves.
                                              🚨 And what it emits carries NO NAME — see part three.
  5a. the derivation emitted no digest      → RED. Each verify job re-derives the roster and is held
                                              to the preflight's digest; a hand-over without one
                                              could land a verdict on another installation.

🚨 It resolves each step BY ID into the parsed workflow (`jobs.preflight.steps[?id]`) and asserts a
sentinel is present, so if a step is renamed, reordered or moved into a script this fails LOUD
instead of silently testing nothing — the "a guard whose subject moved and whose roots did not"
failure mode. The step BETWEEN them is the derivation, which needs the network and has its own
falsification (`derive-combo-instances.py --self-test`, run beside this one).

PART THREE — THE ROSTER NEVER CROSSES A JOB BOUNDARY, AND THE VERDICT COUNTS JOBS (#3848)
-----------------------------------------------------------------------------------------
(Listed here because it is about the preflight's hand-over; it runs after part two.)

Measured 2026-10-10: the preflight published the roster as the job output `instances`. The roster
names installations the private roster masks, and the runner DROPS a job output that contains a
masked value — "Skip output 'instances' since it may contain secret", a WARNING inside a job that
concludes success. `fromJSON('')` then could not expand the verify matrix, so no per-instance job
was ever created: eight post-CD runs in a row read preflight=success verify=failure with no
instance contacted and no reason printed. Two things are asserted so that cannot recur:

  * STRUCTURE — the preflight's job outputs are exactly `slots`, `count`, `digest`; the verify matrix
    is built from `slots`; nothing in the verify job reads a name out of the matrix or out of a
    preflight output (a job NAME and an artifact NAME are not masked); and the verify job resolves
    its slot by re-running the derivation with the preflight's count and digest, BEFORE the lander.
  * BEHAVIOUR — the roster step's real shell is executed over a derived roster and every value it
    writes to `$GITHUB_OUTPUT` is put through the runner's own rule: a value that CONTAINS any
    installation name or host is one the runner would drop. And the `verdict` step's real shell is
    executed over the outcomes below, with the per-instance job counts as inputs:

      preflight ok, 6 instances, 0 jobs created     → RED naming the unexpanded matrix (the measured defect)
      preflight ok, 6 instances, 5 jobs created     → RED: a partial matrix is not a verification
      preflight ok, 6 of 6 created, 5 succeeded     → RED
      preflight ok, 6 of 6 created and succeeded    → GREEN, `verified=true`
      the job count itself missing                  → RED: a verdict with no numerator is not one
      preflight skipped, CD cancelled               → GREEN "no candidate", `verified=false`
      preflight skipped, CD succeeded               → RED (the skip-trapdoor)
      preflight failed                              → RED

`--self-test` re-publishes the roster as a job output, names the job after the instance, and guts the
verdict — and requires each to be refused.

PART TWO — THE LANDER HOLDS NOTHING (#3848)
-------------------------------------------
`combo-verify-instance.sh` used to read roll-target and combo with a stored `mwi_` instance key and
land the verdict with a global admin's `mw_` token over `POST /api/mesh/patch`, re-implementing
`UpdatePolicyNodeType.RecordVerification`'s merge in jq. Neither credential can be read back, so the
lane sat red for a month waiting for twelve hand-issued values. It now mints this run's own GitHub
Actions OIDC token per call (audience = the instance's base URL) and lands through
`POST /api/plugins/combo-verification`, where the merge exists once, in the portal. This part
asserts that shape — the token request, the audience, the recording route, the confirmed landing,
the 401 guidance, a fresh mint before EVERY instance call — and the absence of each removed shape,
and executes the lander once with no OIDC request URL to prove it stops red naming
`id-token: write`. `--self-test` feeds it the old credential-holding lander and requires it to fail.

PART FOUR — DECIDE BEFORE PAYING, AND BUILD THE VERIFIER ONCE (saving R2 of CiProfile2026-11)
------------------------------------------------------------------------------------------------
Each slot used to build the verifier itself, and pulled two images, composed the gate host and
fetched the sealed modules BEFORE the verifier found out the combo could not even be assembled.
Now one `build-verifier` job publishes the verifier for every slot, and the lander runs
`mw-combo-verify --assemble-only` before anything the gate needs. This part asserts the structure
(the slots need the build and never build; the verdict names a failed build as its own red) and
EXECUTES the lander's up-front block with a stand-in verifier over every exit it can see:

      assembly decides NotVerifiable (exit 1 + verdict)  → landed, and the gate is NOT reached
      only the gate can answer (exit 0, no verdict)      → the gate IS reached (the negative control)
      exit 1 claiming Green or Red                       → RED, nothing landed: no pass without a gate
      exit 0 with a verdict file / exit 1 without one    → RED, nothing landed: the phases disagree
      any other exit                                     → RED, nothing landed: a broken instrument

Usage:
    python3 .github/scripts/check-combo-verify.py              # run every part
    python3 .github/scripts/check-combo-verify.py --self-test  # prove each part detects its defect
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - the CI step installs PyYAML; locally `pip install pyyaml`
    print("::error::check-combo-verify.py needs PyYAML (pip install pyyaml)")
    raise SystemExit(1)

WORKFLOW = ".github/workflows/combo-verify.yml"
LANDER = ".github/scripts/combo-verify-instance.sh"
SOURCE_READ_LINE = "IFS=' ' read -r -a source_pairs <<<\"$SOURCES\""
SOURCE_LOOP_LINE = 'for s in "${source_pairs[@]}"; do src_args+=(--source "$s"); done'

# One sentinel per assertion step, proving we extracted THAT block and not a neighbouring step.
SENTINELS = {"assert": "missing=()",
             "roster": "the derived instance roster is not a non-empty JSON array"}
# The verdict step lives in its own job; same rule — by id, with a sentinel.
VERDICT_SENTINEL = "the preflight was SKIPPED although the triggering CD concluded"
VERIFY_JOB_NAME = "Verify instance slot ${{ matrix.slot }} against its roll target"
# The roster digest's HMAC key: a secret the preflight asserts, identical in both jobs.
DIGEST_KEY = "${{ secrets.FLEET_READER_APP_PRIVATE_KEY }}"
# The ONLY condition under which a verify job may upload anything (see handover_problems).
# `!cancelled()`, not `failure()` (#4413): a LANDED Red or NotVerifiable leaves the job green (the
# verdict is advisory), and those are the slots whose combo and verdict a reader wants.
UPLOAD_CONDITION = "${{ !cancelled() && env.INSTANCE_PRIVATE == 'false' }}"
# …and what it may be called and may contain: the slot, never a name. An artifact's name and the
# file names inside it are not masked.
UPLOAD_NAME = "combo-work-slot-${{ matrix.slot }}"
UPLOAD_PATHS = ["combo-slot-${{ matrix.slot }}.json", "combo-verdict-slot-${{ matrix.slot }}.json"]
# What the preflight may hand to another job. Anything else is a value that can carry a name.
PREFLIGHT_OUTPUTS = {"slots": "${{ steps.roster.outputs.slots }}",
                     "count": "${{ steps.roster.outputs.count }}",
                     "digest": "${{ steps.roster.outputs.digest }}"}

FULLY_PROVISIONED = {
    "AZURE_CLIENT_ID": "cid",
    "AZURE_TENANT_ID": "tid",
    "AZURE_SUBSCRIPTION_ID": "sid",
    "FLEET_READER_APP_ID": "app",
    "FLEET_READER_APP_PRIVATE_KEY": "pem",
    "SOURCES": "Plugins=https://github.com/Systemorph/MeshWeaver.Plugins",
    "DIGEST": "_".join("5" * 78),   # the derivation's digest: 78 single digits, `_`-joined
}

# What the derivation step hands the roster step on a healthy fleet.
DERIVED = ('[{"name":"memex","baseUrl":"https://memex.systemorph.com"},'
           '{"name":"memex-cloud","baseUrl":"https://memex.meshweaver.cloud"}]')

# (step id, label, env overrides, expected exit code, text the output MUST contain)
SCENARIOS = [
    (
        "assert",
        "nothing provisioned",
        {name: "" for name in FULLY_PROVISIONED},
        1,
        # The input without which the derivation cannot even be ATTEMPTED, so it is the one this
        # scenario pins. Naming a credential map here would be asserting the old order.
        "secrets.FLEET_READER_APP_ID",
    ),
    (
        # 🚨 THE SCENARIO THAT WOULD HAVE CAUGHT THE FIRST ATTEMPT AT THIS REORDER, and the reason it
        # is spelled as PRODUCTION'S OWN INPUT STATE rather than as one absent name. Every other
        # scenario starts from FULLY_PROVISIONED, which holds external inputs constant at "present"
        # while production has the credential maps absent. This asserts the property the reorder
        # exists for: with exactly the inputs needed to reach derivation, `assert` PASSES. If a
        # future unprovisioned input is added to that step, this goes red.
        "assert",
        "ONLY what this repository actually has provisioned ⇒ the derivation is reached",
        {**{k: "" for k in FULLY_PROVISIONED}, "AZURE_CLIENT_ID": "cid", "AZURE_TENANT_ID": "tid",
         "AZURE_SUBSCRIPTION_ID": "sid", "FLEET_READER_APP_ID": "app",
         "FLEET_READER_APP_PRIVATE_KEY": "pem"},
        0,
        "Every external input is present",
    ),
    (
        "roster",
        "the derived source map absent",
        {**FULLY_PROVISIONED, "INSTANCES": DERIVED, "SOURCES": ""},
        1,
        "deployment records derived no registry sources",
    ),
    (
        "assert",
        "everything provisioned",
        FULLY_PROVISIONED,
        0,
        "Every external input is present",
    ),
    (
        # 🚨 THE VACUOUS-GREEN CASE, and it did not move with the input it used to be about: the
        # roster is derived now, and a DERIVED zero paints exactly the green a DECLARED zero did.
        "roster",
        "the derivation emitted NOTHING (the vacuous-green shape)",
        {**FULLY_PROVISIONED, "INSTANCES": ""},
        1,
        "not a non-empty JSON array",
    ),
    (
        "roster",
        "the derivation emitted an EMPTY array (the vacuous-green shape)",
        {**FULLY_PROVISIONED, "INSTANCES": "[]"},
        1,
        "not a non-empty JSON array",
    ),
    (
        "roster",
        "a derived roster and source map ⇒ the matrix is emitted",
        {**FULLY_PROVISIONED, "INSTANCES": DERIVED},
        0,
        "2 instance(s) will be verified",
    ),
    (
        "roster",
        "the derivation emitted no roster digest ⇒ nothing is handed over",
        {**FULLY_PROVISIONED, "INSTANCES": DERIVED, "DIGEST": ""},
        1,
        "emitted no roster digest",
    ),
]

# What the runner treats as secret in the scenario above: every installation name and host of the
# derived roster, and every source. In production only the PRIVATE ones are masked — but which
# those are is not knowable here, and the property worth holding is the stronger one: nothing that
# identifies ANY installation is in a value handed to another job.
MASKED = [value for row in json.loads(DERIVED)
          for value in (row["name"], row["baseUrl"], row["baseUrl"].split("//", 1)[1])]
MASKED += FULLY_PROVISIONED["SOURCES"].split()

# (label, env, expected exit code, text the output MUST contain, expected `verified=` or None)
VERDICT_OK = {"PREFLIGHT": "success", "BUILD": "success", "VERIFY": "success", "COUNT": "6",
              "TRIGGER": "success", "STARTED": "6", "PASSED": "6"}
VERDICT_SCENARIOS = [
    (
        # 🚨 THE MEASURED DEFECT (#3848, 2026-10-10): the matrix input was dropped, no job exists.
        "6 instances derived, NO per-instance job created (the dropped-output shape)",
        {**VERDICT_OK, "VERIFY": "failure", "STARTED": "0", "PASSED": "0"},
        1, "created NO per-instance verify job", None,
    ),
    (
        "6 instances derived, 5 per-instance jobs created",
        {**VERDICT_OK, "STARTED": "5", "PASSED": "5"},
        1, "The matrix does not cover the roster", None,
    ),
    (
        "6 of 6 created, one failed",
        {**VERDICT_OK, "VERIFY": "failure", "PASSED": "5"},
        1, "5 of 6 per-instance job(s) produced and landed a verdict", None,
    ),
    (
        "6 of 6 created, the matrix reads success, only 5 concluded success",
        {**VERDICT_OK, "PASSED": "5"},
        1, "only 5 of 6 per-instance job(s) concluded success", None,
    ),
    (
        "6 of 6 created and succeeded",
        VERDICT_OK,
        0, "Every derived instance (6 of 6)", "true",
    ),
    (
        "the per-instance job count is missing",
        {**VERDICT_OK, "STARTED": "", "PASSED": ""},
        1, "the per-instance job count is missing", None,
    ),
    (
        "the preflight passed and counted zero",
        {**VERDICT_OK, "COUNT": "0", "STARTED": "0", "PASSED": "0"},
        1, "named ZERO instances", None,
    ),
    (
        "no candidate: the triggering CD was cancelled",
        {**VERDICT_OK, "PREFLIGHT": "skipped", "BUILD": "skipped", "VERIFY": "skipped",
         "COUNT": "", "TRIGGER": "cancelled", "STARTED": "0", "PASSED": "0"},
        0, "No candidate", "false",
    ),
    (
        "the preflight was skipped although CD succeeded (the skip-trapdoor)",
        {**VERDICT_OK, "PREFLIGHT": "skipped", "BUILD": "skipped", "VERIFY": "skipped",
         "COUNT": "", "STARTED": "0", "PASSED": "0"},
        1, "the preflight was SKIPPED", None,
    ),
    (
        "the preflight failed",
        {**VERDICT_OK, "PREFLIGHT": "failure", "BUILD": "skipped", "VERIFY": "skipped",
         "COUNT": "", "STARTED": "0", "PASSED": "0"},
        1, "the preflight FAILED", None,
    ),
    (
        # The ONE build every slot needs: a failed compile skips the whole matrix, and must be
        # named as what it is rather than as an unexpanded matrix.
        "the verifier build failed, so every slot was skipped",
        {**VERDICT_OK, "BUILD": "failure", "VERIFY": "skipped", "STARTED": "0", "PASSED": "0"},
        1, "the verifier build", None,
    ),
    (
        "the verifier build was cancelled, so every slot was skipped",
        {**VERDICT_OK, "BUILD": "cancelled", "VERIFY": "skipped", "STARTED": "0", "PASSED": "0"},
        1, "the verifier build", None,
    ),
]


# ── PART TWO: the lander reaches each instance as THIS RUN, and holds nothing (#3848) ──────────
# What the lander must contain (each proves one property) and must NOT contain (each is the shape
# this change removed: a stored instance key, a global admin's token, and a client-side re-
# implementation of RecordVerification's merge over a raw mesh patch).
LANDER_REQUIRED = {
    "the run's OIDC token is requested from the runner": "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=",
    "the audience is the instance's own base URL": '--arg a "$BASE_URL"',
    "the verdict is landed through the recording route": '-X POST "$BASE_URL/api/plugins/combo-verification"',
    "the landing is confirmed, not assumed": "jq -e '.recorded == true'",
    "a 401 names both provisioning halves": "Admin/_BuildPrincipal/systemorph--meshweaver",
    "a row is private unless told otherwise (fails closed)":
        'PRIVATE=true\n[ "${INSTANCE_PRIVATE:-}" = "false" ] && PRIVATE=false',
    "a private row's verifier output is withheld": '>"$WORK_ROOT/verifier-$tag.log" 2>&1',
    "a private row's summary carries no module id": "Module ids, failures and caveat text are withheld",
    "the 401 guidance names a VALUE to configure, never a description of the row":
        "config Plugins:Registry:BuildPrincipalAudience = $audience_value ",
    # #3848: the portal image ships no tester, so the gate runs on the COMPOSED host (#3022).
    "the gate host is composed from the candidate portal's /app and its tester CLI":
        'bash .github/scripts/compose-gate-host.sh "$host_root/portal-app" "$host_root/tester-app"',
    "the tester and the portal are asserted to be one build before the composition is trusted":
        'framework-identity /portal --expect "$tester_identity"',
    "the verifier is handed the composed host": '--host "$host_root/host"',
    "a missing instrument is refused and never landed": '[ "$verify_exit" -ne "$TESTER_MISSING_EXIT" ]',
    # #4413: the selected version is a tag in the repository the instance's poller lists.
    "the candidate image comes from the repository the instance rolls": 'PORTAL_IMAGE="$ACR/$REPO:$CANDIDATE"',
    "that repository is read from the instance's own roll-target answer": "REPO=$(jq -r '.repository // \"\"' <\"$roll\")",
    # Saving R2 of Doc/Architecture/CiProfile2026-11: the verifier is the one the run built once.
    "the verifier is the one the run built once, asserted before any instance call":
        ': "${COMBO_VERIFIER:?',
    "the combo is assembled BEFORE any candidate image is pulled": 'dotnet "$COMBO_VERIFIER" --assemble-only',
    "the full run reuses the root the up-front phase materialised": 'verifier_cmd=(dotnet "$COMBO_VERIFIER" --assembled',
    "only NotVerifiable may be decided before the gate": '[ "$KIND" = "NotVerifiable" ]',
}
LANDER_FORBIDDEN = {
    "a per-slot build or run of the verifier project (it is built once per run)":
        "dotnet run --project tools/MeshWeaver.ComboVerifier",
    "an mwi_ instance key": "INSTANCE_KEY",
    "a global admin's mw_ token": "ADMIN_TOKEN",
    "a raw mesh patch of Admin/UpdatePolicy": "/api/mesh/patch",
    "a client-side copy of the verdict merge": "--slurpfile p",
    "an unconditional print of a response body (a private row's names its modules)": '$(head -c',
    "401 guidance that tells an operator to set the audience to a description":
        "BuildPrincipalAudience = $where",
    "a candidate pulled from memex-portal-ai whatever the instance rolls (#4413: control rolls memex-control)":
        'PORTAL_IMAGE="$ACR/memex-portal-ai:',
}


def read_lander(root: Path) -> str:
    path = root / LANDER
    if not path.is_file():
        raise SystemExit(f"::error::{LANDER} does not exist under {root}")
    return path.read_text(encoding="utf-8")


def check_lander(text: str, run_it: bool = True) -> int:
    """Static shape plus ONE executed refusal: with no OIDC request URL the lander must stop red,
    naming `id-token: write`, before it calls anything."""
    failures = 0
    for label, needle in LANDER_REQUIRED.items():
        ok = needle in text
        print(f"[{'PASS' if ok else 'FAIL'}] lander: {label}")
        if not ok:
            failures += 1
            print(f"::error::{LANDER} lost {needle!r} — {label} is no longer true")
    for label, needle in LANDER_FORBIDDEN.items():
        ok = needle not in text
        print(f"[{'PASS' if ok else 'FAIL'}] lander holds no {label}")
        if not ok:
            failures += 1
            print(f"::error::{LANDER} contains {needle!r} — {label} is back, which #3848 removed")
    # Every request to the instance mints a FRESH token first: the verification between the reads
    # and the landing can outlive a token minted once.
    calls = len(re.findall(r"^\w+_code=\$\(curl", text, flags=re.M))
    mints = len(re.findall(r"^mint_token\n\w+_code=\$\(curl", text, flags=re.M))
    ok = calls >= 3 and mints == calls
    print(f"[{'PASS' if ok else 'FAIL'}] lander mints a fresh token before each of its {calls} instance call(s)")
    if not ok:
        failures += 1
        print(f"::error::{LANDER}: {mints} of {calls} instance call(s) are preceded by mint_token")
    # …and SENDS it. A minted token that no request carries is an unauthenticated lane that passes
    # every other check here. Each curl block (from its `_code=$(curl` line to the `|| x_code=000`
    # that closes it) must carry the bearer header.
    blocks = re.findall(r"^\w+_code=\$\(curl.*?\|\| \w+_code=000", text, flags=re.M | re.S)
    sent = sum(1 for b in blocks if '-H "Authorization: Bearer $TOKEN"' in b)
    ok = len(blocks) == calls and calls >= 3 and sent == calls
    print(f"[{'PASS' if ok else 'FAIL'}] lander sends the minted token on each of its {calls} instance call(s)")
    if not ok:
        failures += 1
        print(f"::error::{LANDER}: {sent} of {calls} instance call(s) carry 'Authorization: Bearer $TOKEN'")
    if run_it:
        with tempfile.TemporaryDirectory() as tmp:
            script = Path(tmp) / "lander.sh"
            script.write_text(text, encoding="utf-8")
            env = {k: v for k, v in os.environ.items()
                   if k not in ("ACTIONS_ID_TOKEN_REQUEST_URL", "ACTIONS_ID_TOKEN_REQUEST_TOKEN")}
            env.update({"INSTANCE_NAME": "probe", "BASE_URL": "http://127.0.0.1:9",
                        "GITHUB_WORKSPACE": tmp, "GITHUB_STEP_SUMMARY": str(Path(tmp) / "s")})
            proc = subprocess.run(["bash", str(script)], env=env, capture_output=True,
                                  text=True, check=False)
        out = proc.stdout + proc.stderr
        ok = proc.returncode != 0 and "id-token: write" in out
        print(f"[{'PASS' if ok else 'FAIL'}] lander without an OIDC request URL stops red naming id-token: write")
        # …and that run had INSTANCE_PRIVATE unset, i.e. a PRIVATE row: nothing it printed may
        # carry the installation's name or URL.
        quiet = "probe" not in out and "127.0.0.1" not in out
        print(f"[{'PASS' if quiet else 'FAIL'}] lander prints neither name nor URL when the row is not declared public")
        if not quiet:
            failures += 1
            print(f"::error::the lander printed a private row's name or URL: {out}")
        if not ok:
            failures += 1
            print(f"::error::the lander did not refuse a run with no OIDC identity: exit={proc.returncode}\n  {out}")
    return failures


def read_preflight(root: Path) -> dict[str, str]:
    """The preflight's two assertion blocks, resolved BY ID out of the shipped YAML.

    🚨 Never by index. The derivation step sits BETWEEN them, so a position is a coincidence — and
    a guard that silently reads the wrong step is the failure mode this whole file exists to name."""
    path = root / WORKFLOW
    if not path.is_file():
        raise SystemExit(f"::error::{WORKFLOW} does not exist under {root}")
    doc = yaml.safe_load(path.read_text(encoding="utf-8"))
    try:
        steps = doc["jobs"]["preflight"]["steps"]
    except (KeyError, TypeError) as exc:
        raise SystemExit(
            f"::error::{WORKFLOW}: could not resolve jobs.preflight.steps ({exc}). The preflight "
            "moved and this guard did not — it would otherwise pass having checked nothing."
        ) from exc
    by_id = {step.get("id"): step for step in steps if isinstance(step, dict)}
    problems = handover_problems(doc)
    if problems:
        raise SystemExit("\n".join(f"::error::{WORKFLOW}: {problem}" for problem in problems))
    scripts: dict[str, str] = {}
    for step_id, sentinel in SENTINELS.items():
        step = by_id.get(step_id)
        if step is None or "run" not in step:
            raise SystemExit(
                f"::error::{WORKFLOW}: jobs.preflight has no `run` step with id `{step_id}`. The "
                "assertion moved, was renamed or became a script, and this guard did not follow — "
                "it would otherwise pass having checked nothing."
            )
        if sentinel not in step["run"]:
            raise SystemExit(
                f"::error::{WORKFLOW}: the `{step_id}` step does not contain {sentinel!r}, so it is "
                "not the assertion block this guard exercises. Point the guard at the step that "
                "asserts, or restore the assertion."
            )
        scripts[step_id] = step["run"]
    verdict = next((step for step in doc["jobs"].get("verdict", {}).get("steps", [])
                    if isinstance(step, dict) and step.get("id") == "verdict"), None)
    if verdict is None or VERDICT_SENTINEL not in verdict.get("run", ""):
        raise SystemExit(
            f"::error::{WORKFLOW}: jobs.verdict has no `run` step with id `verdict` containing "
            f"{VERDICT_SENTINEL!r}. The verdict moved and this guard did not follow — it would "
            "otherwise pass having checked nothing.")
    scripts["verdict"] = verdict["run"]
    return scripts


def handover_problems(doc: dict) -> list[str]:
    """Why the roster could not reach the verify jobs, or would reach them wearing a name (#3848).

    Structural, over the parsed workflow. Each arm is one way the measured defect — a job output
    the runner dropped because it contained a masked name, so the matrix never expanded — or its
    twin, a name printed where nothing masks it, comes back."""
    problems: list[str] = []
    jobs = doc.get("jobs", {})
    preflight = jobs.get("preflight", {})
    verify = jobs.get("verify", {})
    verdict = jobs.get("verdict", {})

    outputs = preflight.get("outputs", {})
    if outputs != PREFLIGHT_OUTPUTS:
        extra = sorted(set(outputs) - set(PREFLIGHT_OUTPUTS))
        problems.append(
            "jobs.preflight.outputs must be exactly slots/count/digest from the roster step"
            + (f"; it also publishes {extra}" if extra else f"; it is {outputs}")
            + ". The roster and the source map contain names the private roster masks, and the "
            "runner DROPS a job output containing a masked value — the verify matrix then "
            "receives an empty string and no per-instance job is created.")
    by_id = {step.get("id"): step for step in preflight.get("steps", []) if isinstance(step, dict)}
    roster_env = by_id.get("roster", {}).get("env", {})
    for name in ("INSTANCES", "SOURCES", "DIGEST"):
        want = "${{ steps.derive.outputs.%s }}" % name.lower()
        if roster_env.get(name) != want:
            problems.append(f"the roster assertion must read {name} from {want}, not an external "
                            "variable.")

    # The digest is an HMAC: the preflight's derivation and every slot step must hold the SAME key,
    # or no slot would ever match — and neither may run without one (an unkeyed digest of
    # low-entropy names confirms a guess).
    if by_id.get("derive", {}).get("env", {}).get("MW_COMBO_DIGEST_KEY") != DIGEST_KEY:
        problems.append(f"the preflight's derivation must set MW_COMBO_DIGEST_KEY to {DIGEST_KEY}.")
    matrix = verify.get("strategy", {}).get("matrix", {})
    if matrix != {"slot": "${{ fromJSON(needs.preflight.outputs.slots) }}"}:
        problems.append(
            f"jobs.verify's matrix must be exactly `slot: fromJSON(needs.preflight.outputs.slots)`; "
            f"it is {matrix}. A matrix built from anything that carries a name is the dropped "
            "output again.")
    if verify.get("name") != VERIFY_JOB_NAME:
        problems.append(
            f"jobs.verify.name must be {VERIFY_JOB_NAME!r}; it is {verify.get('name')!r}. A job "
            "name is NOT masked (so it must not carry an installation's name), and the verdict "
            "job counts per-instance jobs by this exact name.")
    if "if" in verify:
        problems.append("jobs.verify carries an `if:` — it must be gated by `needs: preflight` "
                        "and nothing else.")
    verify_text = json.dumps(verify)
    for needle, why in (
        ("matrix.instance", "reads an installation out of the matrix"),
        ("needs.preflight.outputs.instances", "reads the roster out of a job output"),
        ("needs.preflight.outputs.sources", "reads the source map out of a job output"),
    ):
        if needle in verify_text:
            problems.append(f"jobs.verify {why} (`{needle}`), which the runner drops whenever it "
                            "contains a masked name.")
    verify_steps = [step for step in verify.get("steps", []) if isinstance(step, dict)]
    resolve_at = next((i for i, step in enumerate(verify_steps)
                       if "derive-combo-instances.py --discover" in step.get("run", "")
                       and "--slot" in step.get("run", "")), None)
    lander_at = next((i for i, step in enumerate(verify_steps)
                      if "bash .github/scripts/combo-verify-instance.sh" in step.get("run", "")),
                     None)
    if resolve_at is None or lander_at is None or resolve_at > lander_at:
        problems.append(
            "jobs.verify must resolve its slot with `derive-combo-instances.py --discover --slot` "
            "BEFORE it runs the lander; otherwise the lander has no installation to verify.")
    else:
        resolve = verify_steps[resolve_at]
        env = resolve.get("env", {})
        for key, want in (("SLOT", "${{ matrix.slot }}"),
                          ("EXPECT_COUNT", "${{ needs.preflight.outputs.count }}"),
                          ("EXPECT_DIGEST", "${{ needs.preflight.outputs.digest }}"),
                          ("MW_ACR_PRIVATE_ROSTER", "${{ secrets.ACR_RETENTION_PRIVATE_ROSTER }}"),
                          ("MW_COMBO_DIGEST_KEY", DIGEST_KEY)):
            if env.get(key) != want:
                problems.append(f"the slot step must set {key} to {want}; it has {env.get(key)!r}.")
        for flag in ('--slot "$SLOT"', '--expect-count "$EXPECT_COUNT"',
                     '--expect-digest "$EXPECT_DIGEST"'):
            if flag not in resolve.get("run", ""):
                problems.append(f"the slot step must pass {flag}: a re-derived roster that is not "
                                "held to the preflight's can land a verdict on another "
                                "installation.")
        if "if" in resolve or resolve.get("continue-on-error"):
            problems.append("the slot step is conditional or continue-on-error — a slot that "
                            "cannot be resolved must be a red verify job.")
        lander = verify_steps[lander_at]
        if lander.get("env", {}).get("ARTIFACT_TAG") != "slot-${{ matrix.slot }}":
            problems.append("the lander must name its files by slot (ARTIFACT_TAG: slot-N): an "
                            "artifact's name and file names are not masked.")
        for needle in ("${INSTANCE_NAME:?", "${BASE_URL:?", "${SOURCES:?", "${INSTANCE_PRIVATE:?"):
            if needle not in lander.get("run", ""):
                problems.append(f"the lander step must assert {needle}…}} before it runs, so a "
                                "slot step that stops writing one is a red naming it.")

    # 🚨 A private row's module list never leaves the run: this repository is public, and
    # `combo.json` is a client estate's inventory. Every upload in the verify job must be
    # conditional on INSTANCE_PRIVATE being literally 'false' (fail closed).
    uploads = [step for step in verify_steps if "upload-artifact" in str(step.get("uses", ""))]
    for step in uploads:
        if step.get("if") != UPLOAD_CONDITION:
            problems.append(
                f"the verify job uploads an artifact under `if: {step.get('if')}`; it must be "
                f"exactly `{UPLOAD_CONDITION}`. Anything looser uploads a client estate's module "
                "list (combo.json) from a public repository.")
        with_ = step.get("with") or {}
        if with_.get("name") != UPLOAD_NAME:
            problems.append(
                f"the verify job's artifact is named {with_.get('name')!r}; it must be exactly "
                f"{UPLOAD_NAME!r}. An artifact's NAME is not masked, so it carries the slot and "
                "nothing an installation could be recognised by.")
        paths = [line.strip() for line in str(with_.get("path", "")).splitlines() if line.strip()]
        if paths != UPLOAD_PATHS:
            problems.append(
                f"the verify job uploads {paths}; it must upload exactly {UPLOAD_PATHS}. A file "
                "name inside an artifact is not masked either, and a wider path (a directory, a "
                "glob) can sweep in files this guard never looked at.")
    if len(uploads) > 1:
        problems.append(f"the verify job has {len(uploads)} upload steps; this guard knows one.")
    verdict_steps = [step for step in verdict.get("steps", []) if isinstance(step, dict)]
    counter = next((step for step in verdict_steps if step.get("id") == "jobs"), None)
    verdict_step = next((step for step in verdict_steps if step.get("id") == "verdict"), {})
    if (counter is None or "/jobs?filter=latest" not in counter.get("run", "")
            or "^Verify instance slot [0-9]+ against its roll target$" not in counter.get("run", "")):
        problems.append("jobs.verdict must count this run's per-instance jobs (step id `jobs`) by "
                        "the verify job's exact name.")
    elif "if" in counter or counter.get("continue-on-error"):
        problems.append("the job-count step is conditional or continue-on-error — a verdict "
                        "whose numerator could not be read must be red.")
    elif (counter.get("env", {}).get("EXPECT_COUNT") != "${{ needs.preflight.outputs.count }}"
          or '-ge "$expect"' not in counter.get("run", "") or "sleep" not in counter.get("run", "")):
        problems.append("the job-count step must re-read the run's jobs until it lists the "
                        "preflight's COUNT (bounded): a re-run copies passed jobs into the new "
                        "attempt lazily, and a single read counted 5 of 7 on run 45066775860.")
    venv = verdict_step.get("env", {})
    if (venv.get("STARTED") != "${{ steps.jobs.outputs.started }}"
            or venv.get("PASSED") != "${{ steps.jobs.outputs.passed }}"):
        problems.append("the verdict step must read STARTED and PASSED from the job-count step.")
    if verdict.get("if") != "always()" or verdict.get("needs") != VERDICT_NEEDS:
        problems.append(f"jobs.verdict must run `if: always()` and need {VERDICT_NEEDS}.")
    problems.extend(build_once_problems(doc))
    if verdict.get("permissions") != {"actions": "read"}:
        problems.append("jobs.verdict needs exactly `permissions: {actions: read}` to list the "
                        "run's jobs.")
    problems.extend(ledger_problems(doc))
    return problems


# ── PART FOUR: the verifier is built ONCE per run (saving R2 of Doc/Architecture/CiProfile2026-11)
BUILD_JOB = "build-verifier"
VERIFY_NEEDS = ["preflight", BUILD_JOB]
VERDICT_NEEDS = ["preflight", BUILD_JOB, "verify"]
VERIFIER_ARTIFACT = "combo-verifier"
COMBO_VERIFIER_ENV = "${{ runner.temp }}/combo-verifier/mw-combo-verify.dll"


def build_once_problems(doc: dict) -> list[str]:
    """One job builds the verifier; every slot downloads it and none builds it again."""
    problems: list[str] = []
    jobs = doc.get("jobs", {})
    build = jobs.get(BUILD_JOB)
    verify = jobs.get("verify", {})
    if not isinstance(build, dict):
        return [f"jobs.{BUILD_JOB} is missing — the verifier must be built ONCE per run, not in "
                "every slot."]
    if build.get("needs") != "preflight":
        problems.append(f"jobs.{BUILD_JOB} must need `preflight` (so a run with no candidate builds "
                        f"nothing); it needs {build.get('needs')!r}.")
    if "if" in build:
        problems.append(f"jobs.{BUILD_JOB} carries an `if:` — it is gated by the preflight and "
                        "nothing else.")
    build_steps = [step for step in build.get("steps", []) if isinstance(step, dict)]
    if any(step.get("continue-on-error") or "if" in step for step in build_steps):
        problems.append(f"a step of jobs.{BUILD_JOB} is conditional or continue-on-error — a "
                        "verifier that did not build must be a red build job.")
    if not any("dotnet publish tools/MeshWeaver.ComboVerifier/" in str(step.get("run", ""))
               for step in build_steps):
        problems.append(f"jobs.{BUILD_JOB} no longer publishes tools/MeshWeaver.ComboVerifier.")
    uploads = [step for step in build_steps if "upload-artifact" in str(step.get("uses", ""))]
    if (len(uploads) != 1 or (uploads[0].get("with") or {}).get("name") != VERIFIER_ARTIFACT
            or (uploads[0].get("with") or {}).get("if-no-files-found") != "error"):
        problems.append(f"jobs.{BUILD_JOB} must upload exactly one artifact named "
                        f"{VERIFIER_ARTIFACT!r} with `if-no-files-found: error`.")
    if verify.get("needs") != VERIFY_NEEDS:
        problems.append(f"jobs.verify must need exactly {VERIFY_NEEDS}; it needs "
                        f"{verify.get('needs')!r}. A slot that does not wait for the one build "
                        "has no verifier to run.")
    verify_steps = [step for step in verify.get("steps", []) if isinstance(step, dict)]
    for step in verify_steps:
        text = str(step.get("run", ""))
        if "dotnet build" in text or "dotnet publish" in text or "dotnet run --project" in text:
            problems.append("a verify step builds or runs a project — the verifier is built once in "
                            f"jobs.{BUILD_JOB} and downloaded; seven slots compiling the same commit "
                            "is the waste R2 removed.")
    downloads = [step for step in verify_steps
                 if "download-artifact" in str(step.get("uses", ""))
                 and (step.get("with") or {}).get("name") == VERIFIER_ARTIFACT]
    if len(downloads) != 1 or "if" in downloads[0] or downloads[0].get("continue-on-error"):
        problems.append(f"jobs.verify must download the {VERIFIER_ARTIFACT!r} artifact in exactly one "
                        "unconditional step.")
    lander = next((step for step in verify_steps
                   if "bash .github/scripts/combo-verify-instance.sh" in str(step.get("run", ""))), {})
    if (lander.get("env") or {}).get("COMBO_VERIFIER") != COMBO_VERIFIER_ENV:
        problems.append(f"the lander step must set COMBO_VERIFIER to {COMBO_VERIFIER_ENV}.")
    # 🚨 THE FLEET IS READ ONCE PER RUN (fleet-reader installation budget): the preflight saves the scans,
    # ENCRYPTS them before the only upload that carries them (a public repository's artifacts are
    # downloadable and the scans name a client estate's installations), and each slot loads them.
    pre_steps = [step for step in (doc.get("jobs", {}).get("preflight", {}) or {}).get("steps", [])
                 if isinstance(step, dict)]
    saves = [i for i, step in enumerate(pre_steps) if "--save-scans" in str(step.get("run", ""))]
    encrypts = [i for i, step in enumerate(pre_steps)
                if "openssl enc -aes-256-cbc" in str(step.get("run", ""))
                and "rm -f" in str(step.get("run", ""))]
    scan_uploads = [i for i, step in enumerate(pre_steps)
                    if "upload-artifact" in str(step.get("uses", ""))
                    and (step.get("with") or {}).get("name") == "combo-scans"]
    if not (saves and encrypts and scan_uploads and saves[0] < encrypts[0] < scan_uploads[0]):
        problems.append("jobs.preflight must save the scans, encrypt them (deleting the plaintext) and "
                        "only then upload the `combo-scans` artifact, in that order.")
    elif not str((pre_steps[scan_uploads[0]].get("with") or {}).get("path", "")).endswith(".enc"):
        problems.append("the `combo-scans` artifact must be the ENCRYPTED file (*.enc); a plaintext scan "
                        "names a client estate's installations in a downloadable artifact.")
    slot = next((step for step in verify_steps if "--slot" in str(step.get("run", ""))), {})
    if "--load-scans" not in str(slot.get("run", "")) or not any(
            "download-artifact" in str(step.get("uses", ""))
            and (step.get("with") or {}).get("name") == "combo-scans" for step in verify_steps):
        problems.append("jobs.verify must download `combo-scans` and resolve its slot with --load-scans — "
                        "eight fleet reads per run is what spent the installation's budget.")
    return problems


# ── PART FOUR, BEHAVIOUR: the lander's up-front block, executed ─────────────────────────────────
UPFRONT_START = 'rm -f "$verdict"\n'
UPFRONT_END = "# ── 3. Verify"
# (label, stand-in exit, verdict the stand-in writes or None, expected exit, text the output must
#  contain, whether the gate must be reached, whether a verdict must be landed)
UPFRONT_SCENARIOS = [
    # A landed NotVerifiable ends the slot GREEN: the verdict is advisory (#4413, policy
    # `platform-rolls-independently-of-packages`); the job is red only when the check broke.
    ("the assembly decides NotVerifiable ⇒ landed, gate NOT reached, slot green",
     1, "NotVerifiable", 0, "DECIDED BEFORE THE GATE", False, True),
    ("NEGATIVE CONTROL: only the gate can answer ⇒ the gate IS reached",
     0, None, 0, "REACHED-THE-GATE", True, False),
    ("an up-front Green is refused — no pass without a gate",
     1, "Green", 1, "Only NotVerifiable can be decided before the gate", False, False),
    ("an up-front Red is refused — no verdict about the candidate without a gate",
     1, "Red", 1, "Only NotVerifiable can be decided before the gate", False, False),
    ("exit 0 with a verdict file ⇒ the phases disagree",
     0, "NotVerifiable", 1, "the two phases disagree", False, False),
    ("exit 1 with no verdict file ⇒ nothing to land",
     1, None, 1, "wrote no verdict file", False, False),
    ("a broken instrument (exit 3) ⇒ red, nothing landed",
     3, None, 1, "could not run (exit 3)", False, False),
]


def read_upfront(text: str) -> str:
    start = text.find(UPFRONT_START)
    end = text.find(UPFRONT_END, start if start >= 0 else 0)
    if start < 0 or end < 0:
        raise SystemExit(f"::error::{LANDER}: the up-front block (from {UPFRONT_START.strip()!r} to "
                         f"{UPFRONT_END!r}) moved, and this guard did not follow — it would "
                         "otherwise pass having checked nothing.")
    return text[start:end]


def check_upfront(block: str) -> int:
    """Executes the lander's up-front block with a stand-in `dotnet` over each exit it can see."""
    failures = 0
    for label, stand_exit, writes, want_code, want_text, want_gate, want_landed in UPFRONT_SCENARIOS:
        with tempfile.TemporaryDirectory() as tmp:
            prelude = f"""set -euo pipefail
fail() {{ echo "FAIL: $*"; exit 1; }}
note() {{ echo "NOTE: $*"; }}
land_verdict() {{ echo "LANDED $KIND"; exit 0; }}
dotnet() {{
  local out="" prev=""
  for a in "$@"; do [ "$prev" = "--verdict" ] && out=$a; prev=$a; done
  echo "stand-in verifier: $*"
  if [ -n "${{STAND_VERDICT:-}}" ]; then printf '{{"verdict":"%s"}}' "$STAND_VERDICT" >"$out"; fi
  return "$STAND_EXIT"
}}
PRIVATE=false
COMBO_VERIFIER=/stand-in/mw-combo-verify.dll
CANDIDATE=9.9.9
tag=slot-0
WORK_ROOT={tmp}/work
verdict={tmp}/verdict.json
assembly_args=(combo.json image:9.9.9 --verdict "$verdict")
"""
            script = prelude + block + '\necho "REACHED-THE-GATE"\n'
            env = dict(os.environ)
            env["STAND_EXIT"] = str(stand_exit)
            env["STAND_VERDICT"] = writes or ""
            proc = subprocess.run(["bash", "-c", script], env=env, capture_output=True,
                                  text=True, check=False)
        out = proc.stdout + proc.stderr
        reached = "REACHED-THE-GATE" in out
        landed = "LANDED " in out
        ok = (proc.returncode == want_code and want_text in out
              and reached == want_gate and landed == want_landed)
        print(f"[{'PASS' if ok else 'FAIL'}] up-front: {label}: exit={proc.returncode} "
              f"(want {want_code}), gate {'reached' if reached else 'not reached'}, "
              f"{'landed' if landed else 'nothing landed'}")
        if not ok:
            failures += 1
            print(f"::error::the lander's up-front block behaved wrongly for '{label}'")
            print("  " + out.replace("\n", "\n  "))
    return failures


# ── The landing decides the slot's colour (#4413) ─────────────────────────────────────────────
# Under policy `platform-rolls-independently-of-packages` the combo verdict is ADVISORY, so a
# per-instance job is red ONLY when the check broke — no verdict produced, or it did not land on its
# instance. A Red or NotVerifiable that LANDED is reported (summary + warning) and the job is green;
# filing it as a red main would ledger the fleet's content state as a broken build. The real
# `land_verdict` function is executed with a stand-in `curl` over both directions.
LANDING_START = "land_verdict() {\n"
LANDING_END = "\n}\n"

# (label, verdict kind written, HTTP code, response body, want exit, text the output must contain,
#  text the summary must contain or None)
LANDING_SCENARIOS = [
    ("a Red verdict that LANDED ⇒ green, reported as a warning and in the summary",
     "Red", "200", '{"recorded":true}', 0, "::warning::", "→ **Red**"),
    ("a NotVerifiable verdict that LANDED ⇒ green, reported",
     "NotVerifiable", "200", '{"recorded":true}', 0, "verdict is NotVerifiable", "→ **NotVerifiable**"),
    ("a Green verdict that LANDED ⇒ green",
     "Green", "200", '{"recorded":true}', 0, "GREEN", "→ **Green**"),
    ("a verdict the instance refused (HTTP 500) ⇒ RED, not landed",
     "Red", "500", "boom", 1, "was NOT landed", None),
    ("a verdict the instance did not authenticate (HTTP 401) ⇒ RED",
     "Green", "401", "", 1, "HTTP 401", None),
    ("a 200 that does not confirm the recording ⇒ RED",
     "Red", "200", '{"recorded":false}', 1, "did not confirm", None),
    ("an unreachable instance (curl failed) ⇒ RED",
     "Green", "CURLFAIL", "", 1, "HTTP 000", None),
    ("a confirm-phase 504 then a landed 200 ⇒ green after one re-post",
     "Red", "504c,200", '{"recorded":true}', 0, "re-posting", "→ **Red**"),
    ("a confirm-phase 504 on every attempt ⇒ RED naming NOT CONFIRMED after 3 re-posts",
     "Red", "504c,504c,504c,504c", "", 1, "NOT CONFIRMED after 3 re-posts", None),
    ("a 400 (even with a confirm-shaped body) ⇒ RED at once, never re-posted (negative control)",
     "Red", "400c,200", '{"recorded":true}', 1, "HTTP 400", None),
    ("a 504 that is not the confirm phase ⇒ RED at once, never re-posted",
     "Red", "504,200", '{"recorded":true}', 1, "HTTP 504", None),
    ("a verdict kind this lane cannot classify ⇒ RED even though it landed",
     "Purple", "200", '{"recorded":true}', 1, "unknown verdict kind", None),
]


def read_landing(text: str) -> str:
    start = text.find(LANDING_START)
    end = text.find(LANDING_END, start if start >= 0 else 0)
    if start < 0 or end < 0:
        raise SystemExit(f"::error::{LANDER}: the land_verdict function moved, and this guard did "
                         "not follow — it would otherwise pass having checked nothing.")
    return text[start:end + len(LANDING_END)]


def check_landing(function: str) -> int:
    """Executes the real `land_verdict` over each landing outcome with a stand-in `curl`."""
    failures = 0
    for label, kind, code, body, want_code, want_text, want_summary in LANDING_SCENARIOS:
        with tempfile.TemporaryDirectory() as tmp:
            verdict = Path(tmp) / "verdict.json"
            verdict.write_text(json.dumps({"verdict": kind, "modules": [], "caveats": []}),
                               encoding="utf-8")
            summary = Path(tmp) / "summary"
            summary.touch()
            prelude = f"""set -euo pipefail
fail() {{ echo "::error::[slot-0] $*"; exit 1; }}
note() {{ echo "[slot-0] $*"; }}
body_of() {{ head -c "$1" "$2" 2>/dev/null || true; }}
mint_token() {{ TOKEN=stand-in; }}
sleep() {{ :; }}
POSTS={tmp}/posts
: >"$POSTS"
curl() {{
  local out="" prev=""
  for a in "$@"; do [ "$prev" = "-o" ] && out=$a; prev=$a; done
  [ "$STAND_CODE" != "CURLFAIL" ] || return 7
  # STAND_CODE is a comma list answered in order (the last one repeats); "504c" is a 504 whose body
  # names the confirm phase, exactly as the portal's own timeout answers.
  local n; n=$(($(wc -l <"$POSTS") + 1)); echo x >>"$POSTS"
  local code; code=$(printf '%s' "$STAND_CODE" | cut -d, -f"$n")
  [ -n "$code" ] || code=$(printf '%s' "$STAND_CODE" | awk -F, '{{print $NF}}')
  if [ "${{code%c}}" != "$code" ]; then code=${{code%c}}
    printf '%s' '{{"error":"Timed out","phase":"confirm","candidateTag":"9.9.9"}}' >"$out"
  else
    printf '%s' "$STAND_BODY" >"$out"
  fi
  printf '%s' "$code"
}}
shown=slot-0
where="this instance"
PRIVATE=false
CANDIDATE=9.9.9
tag=slot-0
out_dir={tmp}
summary={summary}
BASE_URL=http://127.0.0.1:9
unauthorized_hint="provision the build principal"
verdict={verdict}
KIND={kind}
"""
            script = prelude + function + '\nland_verdict\necho "RETURNED"; exit 99\n'
            env = dict(os.environ)
            env["STAND_CODE"] = code
            env["STAND_BODY"] = body
            proc = subprocess.run(["bash", "-c", script], env=env, capture_output=True,
                                  text=True, check=False)
            summary_text = summary.read_text(encoding="utf-8")
        out = proc.stdout + proc.stderr
        ok = (proc.returncode == want_code and want_text in out
              and (want_summary is None or want_summary in summary_text))
        print(f"[{'PASS' if ok else 'FAIL'}] landing: {label}: exit={proc.returncode} (want {want_code})")
        if not ok:
            failures += 1
            print(f"::error::the lander's land_verdict behaved wrongly for '{label}'")
            print("  " + out.replace("\n", "\n  "))
    return failures


# The ledger pair's severity (#4413). Under policy `platform-rolls-independently-of-packages` the
# combo verdict is ADVISORY: a Red is named on the roll and the platform still rolls, and an
# unverified instance rolls UNVERIFIED. So an unverified fleet is never a primary path broken, and
# its ledger must not carry a release-blocking severity. `node-repo-ci-failure.yml` defaults
# `severity` to sev:H (a red MAIN build), so a caller that omits it files a release blocker.
LEDGER_JOBS = ("combo-red", "combo-green")
ADVISORY_SEVERITIES = ("sev:M", "sev:L")


def ledger_problems(doc: dict) -> list[str]:
    """Each ledger job passes an advisory severity, never the reporter's sev:H default (#4413)."""
    problems: list[str] = []
    jobs = doc.get("jobs", {})
    for name in LEDGER_JOBS:
        job = jobs.get(name)
        if not isinstance(job, dict) or "node-repo-ci-failure.yml" not in str(job.get("uses", "")):
            problems.append(f"jobs.{name} must call node-repo-ci-failure.yml — the ledger pair moved "
                            "and this guard did not follow.")
            continue
        severity = (job.get("with") or {}).get("severity")
        if severity not in ADVISORY_SEVERITIES:
            problems.append(
                f"jobs.{name} passes severity {severity!r}; it must pass one of "
                f"{', '.join(ADVISORY_SEVERITIES)}. The combo verdict is advisory (policy "
                "`platform-rolls-independently-of-packages`), and an omitted severity becomes the "
                "reporter's sev:H default — a release blocker for a condition that holds no roll.")
    return problems


def check_verdict(script: str) -> int:
    """The verdict step's real shell, over the per-instance job counts (#3848)."""
    failures = 0
    for label, overrides, want_code, want_text, want_verified in VERDICT_SCENARIOS:
        with tempfile.TemporaryDirectory() as tmp:
            summary = Path(tmp) / "summary"
            summary.touch()
            code, output, gh_output = run_scenario(
                script, {**overrides, "GITHUB_STEP_SUMMARY": str(summary)})
        ok = code == want_code and want_text in output
        if ok and want_verified is not None:
            ok = f"verified={want_verified}" in gh_output
        if ok and want_code != 0:
            # A red verdict must never have already told the reporter it verified something.
            ok = "verified=true" not in gh_output
        print(f"[{'PASS' if ok else 'FAIL'}] verdict: {label}: exit={code} (want {want_code})")
        if not ok:
            failures += 1
            print(f"::error::combo-verify verdict scenario '{label}' behaved wrongly — exit {code}, "
                  f"want {want_code}; message "
                  f"{'contains' if want_text in output else 'DOES NOT contain'} {want_text!r}; "
                  f"outputs {gh_output.strip()!r}")
            print("  " + output.replace("\n", "\n  "))
    return failures


def run_scenario(script: str, env_overrides: dict[str, str]) -> tuple[int, str, str]:
    with tempfile.TemporaryDirectory() as tmp:
        github_output = os.path.join(tmp, "github_output")
        Path(github_output).touch()
        env = dict(os.environ)
        env.update(env_overrides)
        env["GITHUB_OUTPUT"] = github_output
        proc = subprocess.run(
            ["bash", "-c", script], env=env, capture_output=True, text=True, check=False
        )
        return proc.returncode, proc.stdout + proc.stderr, Path(github_output).read_text()


def check(scripts: dict[str, str]) -> int:
    failures = 0
    for step_id, label, overrides, want_code, want_text in SCENARIOS:
        code, output, gh_output = run_scenario(scripts[step_id], overrides)
        ok = code == want_code and want_text in output
        print(f"[{'PASS' if ok else 'FAIL'}] {step_id}: {label}: exit={code} (want {want_code})")
        if not ok:
            failures += 1
            print(f"::error::combo-verify preflight scenario '{label}' ({step_id}) behaved wrongly — "
                  f"exit {code}, want {want_code}; message {'contains' if want_text in output else 'DOES NOT contain'} "
                  f"{want_text!r}")
            print("  " + output.replace("\n", "\n  "))
        elif want_code == 0 and step_id == "roster":
            # Only the roster step emits the matrix, and a matrix that is never emitted skips the
            # verify job exactly as an empty one does.
            emitted = dict(line.split("=", 1) for line in gh_output.splitlines() if "=" in line)
            count = len(json.loads(overrides["INSTANCES"]))
            want = {"slots": json.dumps(list(range(count)), separators=(",", ":")),
                    "count": str(count), "digest": overrides["DIGEST"]}
            if emitted != want:
                failures += 1
                print("::error::the passing scenario must hand over exactly one slot per instance, "
                      f"the count and the digest ({want}); it emitted {emitted}")
            else:
                print("  " + gh_output.strip().replace("\n", " | "))
            # 🚨 THE RUNNER'S OWN RULE, APPLIED HERE (#3848): a job output whose value CONTAINS a
            # masked string is dropped. Every name, host and source of the roster is treated as
            # masked, so any value that would be dropped in production is a failure on this PR.
            for key, value in emitted.items():
                leaked = [secret for secret in MASKED if secret in value]
                if leaked:
                    failures += 1
                    print(f"::error::the roster step writes output `{key}` containing "
                          f"{len(leaked)} value(s) of the roster itself. The runner drops a job "
                          "output that contains a masked value (\"Skip output since it may contain "
                          "secret\"), so the verify matrix would receive nothing and no "
                          "per-instance job would be created.")
    return failures


def check_source_split(root: Path) -> int:
    """Assert the shipped source parser preserves glob characters from record URLs."""
    path = root / LANDER
    if not path.is_file():
        print(f"::error::{LANDER} does not exist under {root}")
        return 1
    text = path.read_text(encoding="utf-8")
    read_at = text.find(SOURCE_READ_LINE)
    loop_at = text.find(SOURCE_LOOP_LINE)
    if read_at < 0 or loop_at < read_at:
        print("::error::the verifier must split derived SOURCES with read -a and pass each literal "
              "pair to --source; unquoted word splitting can pathname-expand deployment data")
        return 1
    split_lines = SOURCE_READ_LINE + "\n" + SOURCE_LOOP_LINE
    with tempfile.TemporaryDirectory() as tmp:
        temp_root = Path(tmp)
        for parent, leaf in (("Plugins=https:", "repo-shadow"),
                             ("Education=https:", "sourceX")):
            folder = temp_root / parent / "example.org"
            folder.mkdir(parents=True, exist_ok=True)
            (folder / leaf).touch()
        sources = "Plugins=https://example.org/repo* Education=https://example.org/source?"
        expected = ("--source\nPlugins=https://example.org/repo*\n"
                    "--source\nEducation=https://example.org/source?\n")
        script = ("src_args=()\n" + split_lines + "\n"
                  + "printf '%s\\n' \"${src_args[@]}\"\n")
        env = dict(os.environ)
        env["SOURCES"] = sources
        proc = subprocess.run(["bash", "-c", script], cwd=temp_root, env=env,
                              capture_output=True, text=True, check=False)
    ok = proc.returncode == 0 and proc.stdout == expected
    print(f"[{'PASS' if ok else 'FAIL'}] verifier source arguments remain literal; no pathname expansion")
    if not ok:
        print(f"::error::the verifier source split changed derived inputs: exit={proc.returncode}; "
              f"stdout={proc.stdout!r}; stderr={proc.stderr!r}")
        return 1
    return 0


def self_test(root: Path) -> int:
    """Each part must be shown to FIRE on its own defect. An unproven guard is no guard."""
    gutted = {"assert": 'echo "Every external input is present"; exit 0',
              "roster": ('echo "slots=[]" >>"$GITHUB_OUTPUT"; '
                         'echo "count=0" >>"$GITHUB_OUTPUT"; '
                         'echo "0 instance(s) will be verified"; exit 0')}
    preflight_failures = check(gutted)
    if preflight_failures == 0:
        print("::error::--self-test: a preflight that asserts nothing passed every scenario, so "
              "this guard proves nothing about the real one.")
        return 1
    print(f"--self-test: a gutted preflight failed {preflight_failures} scenario(s) "
          "— part one can fail.")

    # The lander this change replaced: a stored key, an admin token and a raw mesh patch. Part two
    # must reject it on its static shape alone.
    old_lander = ('roll_code=$(curl -H "Authorization: Bearer $INSTANCE_KEY" x)\n'
                  'patch_code=$(curl -X POST "$BASE_URL/api/mesh/patch" '
                  '-H "Authorization: Bearer $ADMIN_TOKEN" -d x)\n'
                  'jq -n --slurpfile p policy\n\n')
    lander_failures = check_lander(old_lander, run_it=False)
    if lander_failures == 0:
        print("::error::--self-test: the credential-holding lander passed part two, so part two "
              "proves nothing about the real one.")
        return 1
    print(f"--self-test: the credential-holding lander failed {lander_failures} check(s) — part two can fail.")
    # The real lander with the bearer header stripped: it still mints, so only the send check can
    # catch it.
    unsent = read_lander(root).replace('-H "Authorization: Bearer $TOKEN" ', "")
    unsent_failures = check_lander(unsent, run_it=False)
    if unsent_failures == 0:
        print("::error::--self-test: a lander that mints a token and never sends it passed part two.")
        return 1
    print(f"--self-test: the lander that never sends its token failed {unsent_failures} check(s).")
    source_failures = check_source_split(root)
    if source_failures:
        return source_failures

    # ── Part three (#3848): each way the roster's hand-over broke, or could break again ─────────
    real = read_preflight(root)
    # The roster step as it shipped until 2026-10-10: the roster itself, as an output.
    leaking = real["roster"] + '\necho "instances=$(jq -c . <<<"$INSTANCES")" >>"$GITHUB_OUTPUT"\n'
    leak_failures = check({**real, "roster": leaking})
    if leak_failures == 0:
        print("::error::--self-test: a roster step that publishes the roster as an output passed, "
              "so the masked-output check proves nothing.")
        return 1
    print(f"--self-test: a roster step that publishes the roster failed {leak_failures} "
          "scenario(s) — the dropped-output shape is detected.")

    gutted_verdict = 'echo "verified=true" >>"$GITHUB_OUTPUT"; echo "Every derived instance"; exit 0'
    verdict_failures = check_verdict(gutted_verdict)
    if verdict_failures == 0:
        print("::error::--self-test: a verdict that always says verified passed every scenario.")
        return 1
    print(f"--self-test: a verdict that always says verified failed {verdict_failures} scenario(s).")
    # The verdict as it shipped until 2026-10-10: it trusted `needs.verify.result` and never
    # counted a job. Strip the count arms and the measured defect must stop being NAMED.
    # (The parsed `run:` text is dedented, so each arm opens and closes at column 0.)
    uncounted = re.sub(r'^if \[ "\$STARTED" -ne "\$\{COUNT:-0\}" \]; then\n.*?^fi\n',
                       "", real["verdict"], count=1, flags=re.S | re.M)
    uncounted = re.sub(r'^if \[ "\$PASSED" -ne "\$COUNT" \]; then\n.*?^fi\n',
                       "", uncounted, count=1, flags=re.S | re.M)
    if uncounted == real["verdict"] or check_verdict(uncounted) == 0:
        print("::error::--self-test: a verdict that never counts the per-instance jobs passed "
              "every scenario (or the count arms could not be located to remove).")
        return 1
    print("--self-test: a verdict that never counts the per-instance jobs fails its scenarios.")

    doc = yaml.safe_load((root / WORKFLOW).read_text(encoding="utf-8"))
    if handover_problems(doc):
        print("::error::--self-test: the shipped workflow fails the structural hand-over check; "
              "run without --self-test for the list.")
        return 1

    def mutated(change) -> list[str]:
        copy = json.loads(json.dumps(doc, default=str))
        change(copy["jobs"])
        return handover_problems(copy)

    mutations = {
        "the job count read once, with no settle loop (run 45066775860)":
            lambda jobs: [step.update({"run": "gh api repos/x/actions/runs/1/jobs?filter=latest "
                                              "'^Verify instance slot [0-9]+ against its roll target$'"})
                          for step in jobs["verdict"]["steps"] if step.get("id") == "jobs"],
        "the roster re-published as a job output":
            lambda jobs: jobs["preflight"]["outputs"].update(
                instances="${{ steps.roster.outputs.instances }}"),
        "the matrix built from the roster":
            lambda jobs: jobs["verify"]["strategy"].update(
                matrix={"instance": "${{ fromJSON(needs.preflight.outputs.instances) }}"}),
        "the job named after the installation":
            lambda jobs: jobs["verify"].update(
                name="Verify ${{ matrix.instance.name }} against its roll target"),
        "the slot step removed":
            lambda jobs: jobs["verify"].update(steps=[
                step for step in jobs["verify"]["steps"]
                if "--slot" not in str(step.get("run", ""))]),
        "the slot step not held to the preflight's digest":
            lambda jobs: [step["env"].pop("EXPECT_DIGEST") for step in jobs["verify"]["steps"]
                          if "--slot" in str(step.get("run", ""))],
        "the artifact named after the installation":
            lambda jobs: [step["env"].pop("ARTIFACT_TAG") for step in jobs["verify"]["steps"]
                          if "combo-verify-instance.sh" in str(step.get("run", ""))],
        "the failed-verification upload re-enabled for a private row":
            lambda jobs: [step.update({"if": "${{ !cancelled() }}"}) for step in jobs["verify"]["steps"]
                          if "upload-artifact" in str(step.get("uses", ""))],
        "the artifact itself named after the installation":
            lambda jobs: [step["with"].update(name="combo-work-${{ env.INSTANCE_NAME }}")
                          for step in jobs["verify"]["steps"]
                          if "upload-artifact" in str(step.get("uses", ""))],
        "the artifact's files named after the installation":
            lambda jobs: [step["with"].update(
                path="combo-${{ env.INSTANCE_NAME }}.json\ncombo-verdict-${{ env.INSTANCE_NAME }}.json\n")
                          for step in jobs["verify"]["steps"]
                          if "upload-artifact" in str(step.get("uses", ""))],
        "the artifact widened to a glob":
            lambda jobs: [step["with"].update(path="combo-*.json\n")
                          for step in jobs["verify"]["steps"]
                          if "upload-artifact" in str(step.get("uses", ""))],
        "a second upload step added":
            lambda jobs: jobs["verify"]["steps"].append(
                {"uses": "actions/upload-artifact@v7", "if": UPLOAD_CONDITION,
                 "with": {"name": UPLOAD_NAME, "path": "\n".join(UPLOAD_PATHS)}}),
        "the upload made unconditional":
            lambda jobs: [step.pop("if") for step in jobs["verify"]["steps"]
                          if "upload-artifact" in str(step.get("uses", ""))],
        "the lander no longer told whether the row is private":
            lambda jobs: [step.update(run=step["run"].replace("${INSTANCE_PRIVATE:?", "${X:-"))
                          for step in jobs["verify"]["steps"]
                          if "combo-verify-instance.sh" in str(step.get("run", ""))],
        "the digest key dropped from the preflight":
            lambda jobs: [step["env"].pop("MW_COMBO_DIGEST_KEY") for step in jobs["preflight"]["steps"]
                          if step.get("id") == "derive"],
        "the digest key different in the verify job":
            lambda jobs: [step["env"].update(MW_COMBO_DIGEST_KEY="${{ secrets.OTHER }}")
                          for step in jobs["verify"]["steps"] if "--slot" in str(step.get("run", ""))],
        "the red ledger's severity dropped (reporter default sev:H)":
            lambda jobs: jobs["combo-red"]["with"].pop("severity"),
        "the red ledger filed as a release blocker":
            lambda jobs: jobs["combo-red"]["with"].update(severity="sev:H"),
        "the green ledger's severity dropped":
            lambda jobs: jobs["combo-green"]["with"].pop("severity"),
        "the verdict no longer counting jobs":
            lambda jobs: jobs["verdict"].update(steps=[
                step for step in jobs["verdict"]["steps"] if step.get("id") != "jobs"]),
        "the job count made continue-on-error":
            lambda jobs: [step.update({"continue-on-error": True})
                          for step in jobs["verdict"]["steps"] if step.get("id") == "jobs"],
        "the verify job made conditional":
            lambda jobs: jobs["verify"].update({"if": "needs.preflight.outputs.count != '0'"}),
        "the verifier built in every slot again":
            lambda jobs: jobs["verify"]["steps"].insert(0, {
                "name": "Build the verifier",
                "run": "dotnet build tools/MeshWeaver.ComboVerifier/MeshWeaver.ComboVerifier.csproj -c Release"}),
        "the slots no longer waiting for the one build":
            lambda jobs: jobs["verify"].update(needs="preflight"),
        "the one build removed":
            lambda jobs: jobs.pop(BUILD_JOB),
        "the one build made conditional":
            lambda jobs: jobs[BUILD_JOB].update({"if": "github.event_name == 'workflow_dispatch'"}),
        "the build's upload allowed to find nothing":
            lambda jobs: [step["with"].update({"if-no-files-found": "ignore"})
                          for step in jobs[BUILD_JOB]["steps"]
                          if "upload-artifact" in str(step.get("uses", ""))],
        "the verdict no longer reading the build":
            lambda jobs: jobs["verdict"].update(needs=["preflight", "verify"]),
    }
    for label, change in mutations.items():
        if not mutated(change):
            print(f"::error::--self-test: {label} passed the structural hand-over check.")
            return 1
    print(f"--self-test: {len(mutations)} hand-over mutation(s) each refused.")

    # ── Part four, behaviour: each way the up-front block could hide a doomed slot or skip a gate.
    upfront = read_upfront(read_lander(root))
    if check_upfront(upfront):
        print("::error::--self-test: the shipped up-front block fails its own scenarios.")
        return 1
    upfront_mutations = {
        "an up-front verdict landed whatever it says (no NotVerifiable guard)":
            upfront.replace('[ "$KIND" = "NotVerifiable" ] \\\n', 'true \\\n', 1),
        "a verifiable slot that stops instead of running the gate":
            upfront.replace('composing it."\n', 'composing it."\n    exit 0\n', 1),
        "a broken instrument read as 'only the gate can answer'":
            upfront.replace("  0)\n", "  0|3)\n", 1),
    }
    for label, block in upfront_mutations.items():
        if block == upfront:
            print(f"::error::--self-test: could not build the mutation '{label}' — the up-front "
                  "block changed shape and this self-test did not follow.")
            return 1
        if check_upfront(block) == 0:
            print(f"::error::--self-test: {label} passed every up-front scenario.")
            return 1
    print(f"--self-test: {len(upfront_mutations)} up-front mutation(s) each fail a scenario.")

    # ── The landing, behaviour: both directions must be able to fail (#4413).
    landing = read_landing(read_lander(root))
    if check_landing(landing):
        print("::error::--self-test: the shipped land_verdict fails its own scenarios.")
        return 1
    landing_mutations = {
        "the old rule back: a landed Red or NotVerifiable reds the job (an advisory verdict filed as a red main)":
            landing.replace('  Red|NotVerifiable)\n', '  Red|NotVerifiable) fail "verdict is $KIND";\n', 1),
        "an unconfirmed recording accepted as landed":
            landing.replace("jq -e '.recorded == true' >/dev/null <\"$landed\"", "true", 1),
        "a refused POST accepted as landed":
            landing.replace('[ "$land_code" = "200" ] \\\n', 'true \\\n', 1),
        "a confirm-phase 504 never re-posted (#4413)":
            landing.replace('-ge "${LAND_RETRIES:-3}"', '-ge 0', 1),
        "a 4xx re-posted blindly (#4413)":
            landing.replace('[ "$land_code" = "504" ] && jq', '[ "$land_code" != "200" ] && jq', 1),
        "an unknown verdict kind accepted":
            landing.replace('  *) fail "the verifier wrote an unknown verdict kind',
                            '  *) note "the verifier wrote an unknown verdict kind', 1),
    }
    for label, block in landing_mutations.items():
        if block == landing:
            print(f"::error::--self-test: could not build the mutation '{label}' — land_verdict "
                  "changed shape and this self-test did not follow.")
            return 1
        if check_landing(block) == 0:
            print(f"::error::--self-test: {label} passed every landing scenario.")
            return 1
    print(f"--self-test: {len(landing_mutations)} landing mutation(s) each fail a scenario.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".", help="repository root (default: cwd)")
    parser.add_argument("--self-test", action="store_true",
                        help="prove the guard detects a preflight that asserts nothing")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    if args.self_test:
        return self_test(root)

    scripts = read_preflight(root)
    failures = (check(scripts) + check_lander(read_lander(root))
                + check_source_split(root) + check_verdict(scripts["verdict"])
                + check_upfront(read_upfront(read_lander(root)))
                + check_landing(read_landing(read_lander(root))))
    if failures:
        print(f"::error::{failures} combo-verify check(s) behaved wrongly.")
        return 1
    print(f"check-combo-verify: {len(SCENARIOS)} preflight scenario(s) over "
          f"{len(SENTINELS)} assertion step(s) + the lander's identity checks + "
          f"{len(VERDICT_SCENARIOS)} verdict scenario(s) + {len(UPFRONT_SCENARIOS)} up-front "
          f"scenario(s) + {len(LANDING_SCENARIOS)} landing "
          "scenario(s) + the roster hand-over's and the one build's structure, "
          "0 violation(s).")
    # 🚨 WHAT THIS GREEN DOES NOT COVER, said by the gate rather than left to a reader.
    # Every `roster` scenario feeds a SUCCESSFUL derivation (`INSTANCES=DERIVED`), because that is
    # the only state in which the step it exercises is reachable. This green proves the roster
    # assertion behaves GIVEN derived inputs; it says nothing about whether the live repositories
    # produce those inputs. A gate that holds a dimension constant must not let its green be read as
    # coverage of that dimension.
    print("  NOT COVERED by the above: whether the live fleet derives its roster and sources. Every "
          "`roster` scenario assumes derived inputs (INSTANCES=DERIVED). That question belongs to "
          "derive-combo-instances.py — run its --self-test beside this, and read the LANE's own "
          "run for the live answer.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
