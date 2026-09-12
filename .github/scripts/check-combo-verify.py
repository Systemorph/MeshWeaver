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
text — extracted from the shipped YAML by path, never retyped — under five input scenarios and
asserts the exit code AND that the message names the specific shortfall:

  1. nothing provisioned                    → RED, naming vars.COMBO_VERIFY_INSTANCES
  2. the instance list set, a secret absent → RED, naming that secret
  3. the list present but an EMPTY array    → RED. This is the one that matters most: an empty
                                              array yields an empty matrix, an empty matrix SKIPS
                                              the verify job, and GitHub paints a skipped job the
                                              same colour as a passed one. "The gate never ran" and
                                              "the gate passed" must never be the same pixel.
  4. an instance with no admin token        → RED, naming the instance. Otherwise the shortfall
                                              surfaces deep inside the verify job as an HTTP 401
                                              that names no secret — the shape that made an absent
                                              MW_REGISTRY_KEY read as a script bug (Reinsurance#128).
  5. everything provisioned                 → GREEN, and it emits the matrix it promised.

🚨 It resolves the step BY PATH into the parsed workflow (`jobs.preflight.steps[0].run`) and
asserts a sentinel is present, so if the preflight is renamed, reordered or moved into a script
this fails LOUD instead of silently testing nothing — the "a guard whose subject moved and whose
roots did not" failure mode.

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

# The sentinel proves we extracted the preflight's assertion block and not some neighbouring step.
SENTINEL = "missing=()"

FULLY_PROVISIONED = {
    "AZURE_CLIENT_ID": "cid",
    "AZURE_TENANT_ID": "tid",
    "AZURE_SUBSCRIPTION_ID": "sid",
    "FLEET_READER_APP_ID": "app",
    "FLEET_READER_APP_PRIVATE_KEY": "pem",
    "COMBO_VERIFY_INSTANCES": (
        '[{"name":"memex","baseUrl":"https://memex.systemorph.com"},'
        '{"name":"memex-cloud","baseUrl":"https://memex.meshweaver.cloud"}]'
    ),
    "COMBO_VERIFY_SOURCES": "plugins=https://github.com/Systemorph/MeshWeaver.Plugins",
    "COMBO_VERIFY_KEYS": '{"memex":"mwi_a","memex-cloud":"mwi_b"}',
    "COMBO_VERIFY_TOKENS": '{"memex":"mw_a","memex-cloud":"mw_b"}',
}

# (label, env overrides, expected exit code, text the output MUST contain)
SCENARIOS = [
    (
        "nothing provisioned",
        {name: "" for name in FULLY_PROVISIONED},
        1,
        "vars.COMBO_VERIFY_INSTANCES",
    ),
    (
        "instance list present, the keys secret absent",
        {**FULLY_PROVISIONED, "COMBO_VERIFY_KEYS": ""},
        1,
        "secrets.COMBO_VERIFY_KEYS",
    ),
    (
        "list present but an EMPTY array (the vacuous-green shape)",
        {**FULLY_PROVISIONED, "COMBO_VERIFY_INSTANCES": "[]"},
        1,
        "not a non-empty JSON array",
    ),
    (
        "an instance in the list has no admin token",
        {**FULLY_PROVISIONED, "COMBO_VERIFY_TOKENS": '{"memex":"mw_a"}'},
        1,
        "no mw_ admin token for instance 'memex-cloud'",
    ),
    (
        "everything provisioned",
        FULLY_PROVISIONED,
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


def read_preflight(root: Path) -> str:
    path = root / WORKFLOW
    if not path.is_file():
        raise SystemExit(f"::error::{WORKFLOW} does not exist under {root}")
    doc = yaml.safe_load(path.read_text(encoding="utf-8"))
    try:
        script = doc["jobs"]["preflight"]["steps"][0]["run"]
    except (KeyError, IndexError, TypeError) as exc:
        raise SystemExit(
            f"::error::{WORKFLOW}: could not resolve jobs.preflight.steps[0].run ({exc}). "
            "The preflight moved and this guard did not — it would otherwise pass having "
            "checked nothing."
        ) from exc
    if SENTINEL not in script:
        raise SystemExit(
            f"::error::{WORKFLOW}: jobs.preflight.steps[0].run does not contain '{SENTINEL}', so "
            "it is not the assertion block this guard exercises. Point the guard at the step that "
            "asserts the inputs, or restore the assertion."
        )
    return script


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


def check(script: str) -> int:
    failures = 0
    for label, overrides, want_code, want_text in SCENARIOS:
        code, output, gh_output = run_scenario(script, overrides)
        ok = code == want_code and want_text in output
        print(f"[{'PASS' if ok else 'FAIL'}] {label}: exit={code} (want {want_code})")
        if not ok:
            failures += 1
            print(f"::error::combo-verify preflight scenario '{label}' behaved wrongly — "
                  f"exit {code}, want {want_code}; message {'contains' if want_text in output else 'DOES NOT contain'} "
                  f"{want_text!r}")
            print("  " + output.replace("\n", "\n  "))
        elif want_code == 0:
            if "instances=" not in gh_output or "count=" not in gh_output:
                failures += 1
                print("::error::the passing scenario emitted no matrix — an empty matrix skips the "
                      "verify job, and a skipped job is painted green")
            else:
                print("  " + gh_output.strip().replace("\n", " | "))
    return failures


def self_test() -> int:
    """Each part must be shown to FIRE on its own defect. An unproven guard is no guard."""
    gutted = 'echo "instances=[]" >>"$GITHUB_OUTPUT"; echo "count=0" >>"$GITHUB_OUTPUT"; exit 0'
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
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".", help="repository root (default: cwd)")
    parser.add_argument("--self-test", action="store_true",
                        help="prove the guard detects a preflight that asserts nothing")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    root = Path(args.root).resolve()
    failures = check(read_preflight(root)) + check_merge(read_merge_program(root))
    if failures:
        print(f"::error::{failures} combo-verify check(s) behaved wrongly.")
        return 1
    print(f"check-combo-verify: {len(SCENARIOS)} preflight scenario(s) + "
          f"{len(NODE_SHAPES)} verdict-merge shape(s), 0 violation(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
