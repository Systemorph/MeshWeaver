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

🚨 It resolves each step BY ID into the parsed workflow (`jobs.preflight.steps[?id]`) and asserts a
sentinel is present, so if a step is renamed, reordered or moved into a script this fails LOUD
instead of silently testing nothing — the "a guard whose subject moved and whose roots did not"
failure mode. The step BETWEEN them is the derivation, which needs the network and has its own
falsification (`derive-combo-instances.py --self-test`, run beside this one).

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

Usage:
    python3 .github/scripts/check-combo-verify.py              # run both parts
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

FULLY_PROVISIONED = {
    "AZURE_CLIENT_ID": "cid",
    "AZURE_TENANT_ID": "tid",
    "AZURE_SUBSCRIPTION_ID": "sid",
    "FLEET_READER_APP_ID": "app",
    "FLEET_READER_APP_PRIVATE_KEY": "pem",
    "SOURCES": "Plugins=https://github.com/Systemorph/MeshWeaver.Plugins",
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
}
LANDER_FORBIDDEN = {
    "an mwi_ instance key": "INSTANCE_KEY",
    "a global admin's mw_ token": "ADMIN_TOKEN",
    "a raw mesh patch of Admin/UpdatePolicy": "/api/mesh/patch",
    "a client-side copy of the verdict merge": "--slurpfile p",
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
    roster = by_id.get("roster", {})
    derive = by_id.get("derive", {})
    outputs = doc["jobs"]["preflight"].get("outputs", {})
    if outputs.get("sources") != "${{ steps.roster.outputs.sources }}":
        raise SystemExit(
            f"::error::{WORKFLOW}: preflight must publish steps.roster.outputs.sources; "
            "otherwise the verifier can run without the deployment-derived source map.")
    if roster.get("env", {}).get("SOURCES") != "${{ steps.derive.outputs.sources }}":
        raise SystemExit(
            f"::error::{WORKFLOW}: the roster assertion must read SOURCES from "
            "steps.derive.outputs.sources, not an external variable.")
    verify_steps = doc["jobs"].get("verify", {}).get("steps", [])
    lander = next((step for step in verify_steps
                   if "bash .github/scripts/combo-verify-instance.sh" in step.get("run", "")), None)
    if lander is None or lander.get("env", {}).get("SOURCES") != "${{ needs.preflight.outputs.sources }}":
        raise SystemExit(
            f"::error::{WORKFLOW}: the verifier must consume needs.preflight.outputs.sources; "
            "otherwise it can run with a missing or stale source map.")
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
    return scripts


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
            if "instances=" not in gh_output or "count=" not in gh_output or "sources=" not in gh_output:
                failures += 1
                print("::error::the passing scenario did not emit the matrix, denominator and "
                      "derived source map — the verify job could otherwise skip or run without "
                      "its materialisation inputs")
            else:
                print("  " + gh_output.strip().replace("\n", " | "))
                expected_source = f"sources={overrides.get('SOURCES', '')}"
                if expected_source not in gh_output:
                    failures += 1
                    print("::error::the preflight did not preserve the derived source map in its output")
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
              "roster": ('echo "instances=[]" >>"$GITHUB_OUTPUT"; '
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
    source_failures = check_source_split(root)
    if source_failures:
        return source_failures
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

    failures = (check(read_preflight(root)) + check_lander(read_lander(root))
                + check_source_split(root))
    if failures:
        print(f"::error::{failures} combo-verify check(s) behaved wrongly.")
        return 1
    print(f"check-combo-verify: {len(SCENARIOS)} preflight scenario(s) over "
          f"{len(SENTINELS)} assertion step(s) + the lander's identity checks, "
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
