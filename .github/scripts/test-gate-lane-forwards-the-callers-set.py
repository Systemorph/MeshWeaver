#!/usr/bin/env python3
"""test-gate-lane-forwards-the-callers-set.py — node-repo-gate.yml carries a PINNED caller's set name
all the way to resolve-gate-platform.sh's --set.

WHY. A caller that pins its digests has ALREADY resolved the set (every satellite's preflight emits
`set`), and a shard on a volume runner needs that NAME to tell a set the runner volume has not
reached yet — waited out, bounded — from one it has moved past (#4281). The lane dropped it on the
pinned path (`platform-set=` echoed empty in `plan`, not emitted at all in the shard), so a set
sealed minutes before the run was refused at once on every volume shard until the refresh's next
tick: MeshWeaver.Crm#108 on 2026-09-15 (3.0.0-ci.8687 sealed 17:43:27Z, shard at 17:50Z). The
workflow is YAML driving bash, so this reads the steps' text and asserts each hop of the chain; each
assertion names the hop it guards so a red says which link broke.

    test-gate-lane-forwards-the-callers-set.py [--workflow <path>]
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

DEFAULT = Path(__file__).resolve().parents[1] / "workflows" / "node-repo-gate.yml"


def step(text: str, name_fragment: str) -> str:
    """The text of the one step whose `- name:` contains the fragment, up to the next step."""
    starts = [m.start() for m in re.finditer(r"^\s+- name: ", text, re.M)]
    for i, start in enumerate(starts):
        line_end = text.index("\n", start)
        if name_fragment in text[start:line_end]:
            end = starts[i + 1] if i + 1 < len(starts) else len(text)
            return text[start:end]
    raise SystemExit(f"FAIL no step named like '{name_fragment}' — the guard lost its subject")


def run_block(step_text: str) -> str:
    """The step's `run: |` script, de-indented — executed below, not just read."""
    lines = step_text.split("\n")
    start = next(i for i, l in enumerate(lines) if l.strip() == "run: |") + 1
    body = [l for l in lines[start:]]
    indent = min(len(l) - len(l.lstrip()) for l in body if l.strip())
    return "\n".join(l[indent:] for l in body)


def execute(script: str, env: dict[str, str]) -> tuple[int, dict[str, str], str]:
    """Run the step's bash with `env`; returns (rc, GITHUB_OUTPUT as a dict, stdout)."""
    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp) / "out"
        out.write_text("")
        proc = subprocess.run(["bash", "-c", script], capture_output=True, text=True,
                              env={**os.environ, **env, "GITHUB_OUTPUT": str(out), "RUNNER_TEMP": tmp})
        rows = dict(l.split("=", 1) for l in out.read_text().splitlines() if "=" in l)
        return proc.returncode, rows, proc.stdout + proc.stderr


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--workflow", default=str(DEFAULT))
    text = Path(ap.parse_args().workflow).read_text(encoding="utf-8")
    failures: list[str] = []

    def check(label: str, ok: bool) -> None:
        print(f"  {'ok  ' if ok else 'FAIL'} {label}")
        if not ok:
            failures.append(label)

    inputs = text[text.index("    inputs:"):]
    check("1 the lane declares an optional `platform-set` INPUT",
          re.search(r"^      platform-set:\n(?:.*\n)*?        required: false", inputs, re.M) is not None)

    plan = step(text, "The platform: the caller's pins, or the newest sealed set resolved here")
    check("2 plan reads the input into its step", "PINNED_SET: ${{ inputs.platform-set }}" in plan)
    pinned = plan[plan.index('if [ -n "${IMAGE_DIGEST:-}" ] && [ -n "${PLATFORM_DIGEST:-}" ]; then'):]
    pinned = pinned[:pinned.index("exit 0")]
    check("3 plan's PINNED branch emits the caller's set (not an empty `platform-set=`)",
          'echo "platform-set=${PINNED_SET:-}"' in pinned and 'echo "platform-set="\n' not in pinned)
    check("4 a malformed set is refused by shape", "is not a set name" in plan)
    check("5 a set without a pin is refused", "names the set of PINNED digests, and none are pinned" in plan)
    check("6 plan exposes it as its job output",
          "platform-set: ${{ steps.digests.outputs.platform-set }}" in text)

    # ── the plan step EXECUTED on its pinned path (it exits before any resolver or registry call) ──
    pins = {"IMAGE_DIGEST": "sha256:" + "1" * 64, "PLATFORM_DIGEST": "sha256:" + "2" * 64,
            "ALLOW_UNPINNED": "false"}
    script = run_block(plan)
    rc, rows, said = execute(script, {**pins, "PINNED_SET": "3.0.0-ci.8687"})
    check("10 EXECUTED: a pinned pair with a set → rc 0, platform-set=3.0.0-ci.8687, resolved=false",
          rc == 0 and rows.get("platform-set") == "3.0.0-ci.8687" and rows.get("resolved") == "false")
    rc, rows, said = execute(script, {**pins, "PINNED_SET": ""})
    check("11 EXECUTED: a pinned pair with no set → rc 0 and an empty platform-set (as before)",
          rc == 0 and rows.get("platform-set") == "")
    rc, rows, said = execute(script, {**pins, "PINNED_SET": "8687"})
    check("12 EXECUTED: a malformed set → rc != 0, named", rc != 0 and "is not a set name" in said)
    rc, rows, said = execute(script, {"IMAGE_DIGEST": "", "PLATFORM_DIGEST": "", "ALLOW_UNPINNED": "false",
                                      "PINNED_SET": "3.0.0-ci.8687"})
    check("13 EXECUTED: a set with no pin → rc != 0, named", rc != 0 and "none are pinned" in said)

    shard = step(text, "The platform for this shard: the caller's pins, or plan's set re-checked")
    check("7 the shard reads plan's set", "PLAN_SET: ${{ needs.plan.outputs.platform-set }}" in shard)
    as_called = shard[shard.index('if [ "${RESOLVED:-}" != "true" ]; then'):]
    as_called = as_called[:as_called.index("exit 0")]
    check("8 the shard's AS-CALLED branch emits `platform-set`", 'echo "platform-set=${PLAN_SET:-}"' in as_called)
    rc, rows, said = execute(run_block(shard), {"RESOLVED": "false", "IMAGE_DIGEST": pins["IMAGE_DIGEST"],
                                                "PLATFORM_DIGEST": pins["PLATFORM_DIGEST"],
                                                "PLAN_SET": "3.0.0-ci.8687"})
    check("14 EXECUTED: the shard's as-called path hands 3.0.0-ci.8687 on as platform-set",
          rc == 0 and rows.get("platform-set") == "3.0.0-ci.8687")

    source = step(text, "Where this shard takes the platform from")
    check("9 the volume lookup takes the shard's `platform-set` (not `set`) as --set",
          "PLATFORM_SET: ${{ steps.platform.outputs.platform-set }}" in source
          and '--set "${PLATFORM_SET:-}"' in source)

    if failures:
        print(f"::error::node-repo-gate.yml drops a pinned caller's set name at: {'; '.join(failures)}")
        return 1
    print("node-repo-gate.yml carries a pinned caller's set to --set: every hop present")
    return 0


if __name__ == "__main__":
    sys.exit(main())
