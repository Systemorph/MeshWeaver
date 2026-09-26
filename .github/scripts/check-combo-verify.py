#!/usr/bin/env python3
"""Prove the combo-verify lane's shell: the preflight can FAIL, and the verdict merge cannot lose data.

WHY THIS EXISTS
---------------
`combo-verify.yml` is the producer half of the combo gate (MeshWeaver#3544). Its `preflight` job is
the ONLY thing standing between "no credential was provisioned" and a lane that quietly verifies
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
— which cannot run before the derivation — asks whether every instance the fleet ACTUALLY has carries
both credentials and the derived source map arrived intact. Splitting the assertion split the
scenarios with it:

🚨 AND THE SPLIT IS BY WHETHER AN INPUT IS NEEDED TO *REACH* THE DERIVATION — not by what is knowable
yet, and NOT by whether provisioning it is reversible. The two `COMBO_VERIFY_*` credential maps are
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
                                              at "present"; production varies the credential maps to
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
  5. the key map absent entirely            → RED, naming secrets.COMBO_VERIFY_KEYS and carrying the
                                              issued-never-recovered guidance. Asserted here so the
                                              instruction to MINT arrives only once minting is useful.
  6. the token map absent entirely          → RED, naming secrets.COMBO_VERIFY_TOKENS.
  7. an instance with no admin token        → RED, naming the instance. Otherwise the shortfall
                                              surfaces deep inside the verify job as an HTTP 401
                                              that names no secret — the shape that made an absent
                                              MW_REGISTRY_KEY read as a script bug (Reinsurance#128).
  8. every derived instance credentialled   → GREEN, and it emits the matrix it promised.

🚨 It resolves each step BY ID into the parsed workflow (`jobs.preflight.steps[?id]`) and asserts a
sentinel is present, so if a step is renamed, reordered or moved into a script this fails LOUD
instead of silently testing nothing — the "a guard whose subject moved and whose roots did not"
failure mode. The step BETWEEN them is the derivation, which needs the network and has its own
falsification (`derive-combo-instances.py --self-test`, run beside this one).

PART TWO — THE VERDICT MERGE
----------------------------
`combo-verify-instance.sh` lands a verdict by read-merge-write, because an RFC 7396 merge patch
replaces an array WHOLESALE. So the whole list is re-sent on every landing, and a defect in the jq
that builds it does not fail — it silently DELETES an instance's recorded verdict history.

There is a live trap: `MeshOperations.Get` has TWO node shapes. Normally the body is the bare node;
when the node's NodeType carries a recorded compile error it is
`{"node": {...}, "compilationError": "..."}` instead. Reading `.content.comboVerifications` off the
wrapper yields null, null merges as an empty list, and the landing would replace up to eight
verdicts with one. This part extracts the jq program FROM the shipped script and runs it over both
shapes plus an instance that has no verdicts yet, asserting the rule
`UpdatePolicyNodeType.RecordVerification` applies in-process: upsert by `candidateTag`
(case-insensitive), newest first, capped at `MaxRecordedVerifications` = 8.

Usage:
    python3 .github/scripts/check-combo-verify.py              # run both parts
    python3 .github/scripts/check-combo-verify.py --self-test  # prove each part detects its defect
"""

from __future__ import annotations

import argparse
import json
import os
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
             "roster": "COMBO_VERIFY_KEYS has no mwi_ key"}

FULLY_PROVISIONED = {
    "AZURE_CLIENT_ID": "cid",
    "AZURE_TENANT_ID": "tid",
    "AZURE_SUBSCRIPTION_ID": "sid",
    "FLEET_READER_APP_ID": "app",
    "FLEET_READER_APP_PRIVATE_KEY": "pem",
    "COMBO_VERIFY_KEYS": '{"memex":"mwi_a","memex-cloud":"mwi_b"}',
    "COMBO_VERIFY_TOKENS": '{"memex":"mw_a","memex-cloud":"mw_b"}',
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
        # 🚨 THESE TWO MOVED HERE FROM `assert`, and that is the whole point of the reorder: the
        # instruction to MINT an irreversible credential is now emitted only once the roster the
        # credential is for has been derived. The message must still carry the provisioning
        # guidance — asserting later must not mean saying less — so the expected text is the part a
        # person acts on, not merely the secret's name.
        "roster",
        "the key map absent entirely",
        {**FULLY_PROVISIONED, "INSTANCES": DERIVED, "COMBO_VERIFY_KEYS": ""},
        1,
        "ISSUED, never recovered",
    ),
    (
        "roster",
        "the token map absent entirely",
        {**FULLY_PROVISIONED, "INSTANCES": DERIVED, "COMBO_VERIFY_TOKENS": ""},
        1,
        "secrets.COMBO_VERIFY_TOKENS",
    ),
    (
        "roster",
        "a derived instance has no admin token",
        {**FULLY_PROVISIONED, "INSTANCES": DERIVED,
         "COMBO_VERIFY_TOKENS": '{"memex":"mw_a"}'},
        1,
        "no mw_ admin token for instance 'memex-cloud'",
    ),
    (
        "roster",
        "every derived instance carries both credentials",
        {**FULLY_PROVISIONED, "INSTANCES": DERIVED},
        0,
        "2 instance(s) will be verified",
    ),
]


# The jq program that builds the patch body, delimited in the shipped script by these two markers.
MERGE_START = 'jq -n --slurpfile p "$policy" --slurpfile v "$verdict" \''
MERGE_END = "' >\"$request\""


def read_merge_program(root: Path) -> str:
    """The jq program the lander actually ships, read out of it rather than retyped."""
    path = root / LANDER
    if not path.is_file():
        raise SystemExit(f"::error::{LANDER} does not exist under {root}")
    text = path.read_text(encoding="utf-8")
    start = text.find(MERGE_START)
    if start < 0:
        raise SystemExit(
            f"::error::{LANDER}: could not find the verdict-merge jq invocation. It moved and this "
            "guard did not — it would otherwise pass having checked nothing."
        )
    start += len(MERGE_START)
    end = text.find(MERGE_END, start)
    if end < 0:
        raise SystemExit(
            f"::error::{LANDER}: the verdict-merge jq program is not terminated by {MERGE_END!r}")
    program = text[start:end]
    if "comboVerifications" not in program or "ascii_downcase" not in program:
        raise SystemExit(
            f"::error::{LANDER}: the extracted jq program does not look like the verdict merge "
            f"(no comboVerifications / no case-insensitive tag test):\n{program}"
        )
    return program


def existing_verdicts() -> list:
    rows = [
        {"candidateTag": f"3.0.0-ci.{7900 + i}", "verdict": "Green",
         "verifiedAt": f"2026-08-0{1 + i}T00:00:00+00:00"}
        for i in range(9)
    ]
    # Same tag as the incoming verdict, different case: the upsert must REPLACE it, not duplicate it.
    rows.append({"candidateTag": "3.0.0-CI.7999", "verdict": "Red",
                 "verifiedAt": "2026-07-01T00:00:00+00:00"})
    return rows


INCOMING = {"candidateTag": "3.0.0-ci.7999", "verdict": "Green",
            "verifiedAt": "2026-09-07T12:00:00+00:00"}

BARE_NODE = {"id": "UpdatePolicy",
             "content": {"comboVerifications": existing_verdicts(), "mode": "Auto"}}

# (label, the body /api/mesh/get returns, expected list length)
NODE_SHAPES = [
    ("the bare node", BARE_NODE, 8),
    ("the compile-error wrapper {node, compilationError}",
     {"node": BARE_NODE, "compilationError": "boom"}, 8),
    ("an instance with no verdicts yet", {"id": "UpdatePolicy", "content": {"mode": "Auto"}}, 1),
]


def run_merge(program: str, policy: dict) -> dict:
    with tempfile.TemporaryDirectory() as tmp:
        pol = Path(tmp) / "policy.json"
        ver = Path(tmp) / "verdict.json"
        pol.write_text(json.dumps(policy))
        ver.write_text(json.dumps(INCOMING))
        proc = subprocess.run(
            ["jq", "-n", "--slurpfile", "p", str(pol), "--slurpfile", "v", str(ver), program],
            capture_output=True, text=True, check=False)
        if proc.returncode != 0:
            raise SystemExit(f"::error::the verdict-merge jq failed: {proc.stderr}")
        return json.loads(proc.stdout)


def check_merge(program: str) -> int:
    failures = 0
    for label, policy, want_len in NODE_SHAPES:
        body = run_merge(program, policy)
        rows = json.loads(body["fields"])["content"]["comboVerifications"]
        tags = [r["candidateTag"] for r in rows]
        lowered = [t.lower() for t in tags]
        stamps = [r["verifiedAt"] for r in rows]
        problems = []
        if body.get("path") != "Admin/UpdatePolicy":
            problems.append(f"patched {body.get('path')!r}, not Admin/UpdatePolicy")
        if len(rows) != want_len:
            problems.append(f"{len(rows)} verdict(s), want {want_len}")
        if len(lowered) != len(set(lowered)):
            problems.append(f"duplicate candidateTag after the upsert: {tags}")
        if INCOMING["candidateTag"] not in tags:
            problems.append("the incoming verdict is not in the list it would land")
        if stamps != sorted(stamps, reverse=True):
            problems.append(f"not newest-first: {stamps}")
        if len(rows) > 8:
            problems.append("over MaxRecordedVerifications = 8")
        if problems:
            failures += 1
            print(f"[FAIL] merge over {label}: " + "; ".join(problems))
            print(f"::error::the verdict merge would corrupt Admin/UpdatePolicy for {label}")
        else:
            print(f"[PASS] merge over {label}: {len(rows)} verdict(s), newest {tags[0]}")
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

    # The un-hardened merge: reads `.content` off the body without unwrapping `{node, ...}`. It is
    # correct for the bare node and DELETES the history for the wrapper — the exact defect the
    # shipped program's `(.node // .)` exists to prevent.
    naive = ('($v[0].candidateTag // "" | ascii_downcase) as $tag '
             '| (($p[0].content.comboVerifications // []) '
             '| map(select((.candidateTag // "" | ascii_downcase) != $tag))) + [$v[0]] '
             '| sort_by(.verifiedAt) | reverse | .[0:8] '
             '| { path: "Admin/UpdatePolicy", '
             'fields: ({ content: { comboVerifications: . } } | tojson) }')
    merge_failures = check_merge(naive)
    if merge_failures == 0:
        print("::error::--self-test: a merge that never unwraps {node, compilationError} passed "
              "every shape, so part two proves nothing about the real one.")
        return 1
    print(f"--self-test: the un-hardened merge failed {merge_failures} shape(s) — part two can fail.")
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

    failures = (check(read_preflight(root)) + check_merge(read_merge_program(root))
                + check_source_split(root))
    if failures:
        print(f"::error::{failures} combo-verify check(s) behaved wrongly.")
        return 1
    print(f"check-combo-verify: {len(SCENARIOS)} preflight scenario(s) over "
          f"{len(SENTINELS)} assertion step(s) + {len(NODE_SHAPES)} verdict-merge shape(s), "
          "0 violation(s).")
    # 🚨 WHAT THIS GREEN DOES NOT COVER, said by the gate rather than left to a reader.
    # Every `roster` scenario feeds a SUCCESSFUL derivation (`INSTANCES=DERIVED`), because that is
    # the only state in which the step it exercises is reachable. This green proves the credential
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
