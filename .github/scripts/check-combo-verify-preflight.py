#!/usr/bin/env python3
"""Prove the combo-verify preflight can FAIL, and fails naming what to provision.

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

Usage:
    python3 .github/scripts/check-combo-verify-preflight.py            # run the scenarios
    python3 .github/scripts/check-combo-verify-preflight.py --self-test  # prove the harness detects
                                                                         # a gutted preflight
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - the CI step installs PyYAML; locally `pip install pyyaml`
    print("::error::check-combo-verify-preflight.py needs PyYAML (pip install pyyaml)")
    raise SystemExit(1)

WORKFLOW = ".github/workflows/combo-verify.yml"

# The sentinel proves we extracted the preflight's assertion block and not some neighbouring step.
SENTINEL = "missing=()"

FULLY_PROVISIONED = {
    "AZURE_CLIENT_ID": "cid",
    "AZURE_TENANT_ID": "tid",
    "AZURE_SUBSCRIPTION_ID": "sid",
    "MESHWEAVER_APP_ID": "app",
    "MESHWEAVER_APP_PRIVATE_KEY": "pem",
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
    """A gutted preflight — one that accepts anything — must be CAUGHT by the scenarios above."""
    gutted = 'echo "instances=[]" >>"$GITHUB_OUTPUT"; echo "count=0" >>"$GITHUB_OUTPUT"; exit 0'
    failures = check(gutted)
    if failures == 0:
        print("::error::--self-test: a preflight that asserts nothing passed every scenario, so "
              "this guard proves nothing about the real one.")
        return 1
    print(f"--self-test: a gutted preflight failed {failures} scenario(s) — the guard can fail.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".", help="repository root (default: cwd)")
    parser.add_argument("--self-test", action="store_true",
                        help="prove the guard detects a preflight that asserts nothing")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    script = read_preflight(Path(args.root).resolve())
    failures = check(script)
    if failures:
        print(f"::error::{failures} combo-verify preflight scenario(s) behaved wrongly.")
        return 1
    print(f"check-combo-verify-preflight: {len(SCENARIOS)} scenario(s), 0 violation(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
