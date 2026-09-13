#!/usr/bin/env python3
"""EXECUTE `node-repo-gate.yml`'s shard-planning step against fixtures — the step that decides how
many runners the gate fans out across (MeshWeaver#4175).

WHY. The plan used to read the caller's `shards` input alone, while the step immediately above it
narrowed the run to the packages a pull request actually affects. A diff touching fewer packages
than `shards` therefore created a shard with NOTHING in it — and an empty shard is not a harmless
no-op: it installs no package, the tester's bake-consumption postcondition fires ("the bake in
'/seed' declares assemblies for N NodeType(s), NONE of which this run installed"), and the fold
fails the required context. Measured on MeshWeaver.Plugins#1768, run 34749441237.

🚨 THE POSTCONDITION IS NOT WHAT CHANGED. Letting an empty shard pass would make a leg that judged
nothing indistinguishable from one that judged the bytes. The fan-out is clamped instead, and these
cases pin BOTH directions: a narrowed run never plans more shards than it has packages, and a full
run's fan-out is untouched.

The step is EXTRACTED FROM THE WORKFLOW by its `id:` and executed — never copied here, because a
copy passes while the lane rots.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

WORKFLOW = ".github/workflows/node-repo-gate.yml"
STEP_ID = "plan"
FAILURES: list[str] = []


def die(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


def fail(name: str, detail: str) -> None:
    FAILURES.append(name)
    print(f"  FAIL {name} — {detail}")


def ok(name: str, detail: str = "") -> None:
    print(f"  ok   {name}{(' — ' + detail) if detail else ''}")


def extract_step(root: Path, step_id: str) -> str:
    import yaml

    doc = yaml.safe_load((root / WORKFLOW).read_text(encoding="utf-8"))
    for job in (doc.get("jobs") or {}).values():
        for step in job.get("steps") or []:
            if isinstance(step, dict) and step.get("id") == step_id:
                body = step.get("run")
                if not body:
                    die(f"step id `{step_id}` in {WORKFLOW} has no `run:` — nothing to execute.")
                if "${{" in body:
                    die(f"step id `{step_id}`'s `run:` grew a ${{{{ }}}} expression this harness "
                        "cannot supply — pass it through `env:` instead.")
                return body
    die(f"no step with id `{step_id}` in {WORKFLOW}. This harness EXTRACTS the lane's own shell by "
        "id; a renamed or id-less step would leave it testing nothing.")
    raise AssertionError("unreachable")


def run_plan(body: str, shards: str, mount: str, run_all: str) -> tuple[int, dict[str, str], str]:
    with tempfile.TemporaryDirectory() as raw:
        tmp = Path(raw)
        out = tmp / "outputs"
        out.touch()
        script = tmp / "plan.sh"
        script.write_text(body, encoding="utf-8")
        env = dict(os.environ)
        env.update({"SHARDS": shards, "MOUNT": mount, "RUN_ALL": run_all,
                    "GITHUB_OUTPUT": str(out)})
        result = subprocess.run(["bash", str(script)], capture_output=True, text=True, env=env)
        rows: dict[str, str] = {}
        for line in out.read_text(encoding="utf-8").splitlines():
            if "=" in line:
                key, value = line.split("=", 1)
                rows[key] = value
        return result.returncode, rows, result.stdout + result.stderr


def case(name: str, body: str, *, shards: str, mount: str, run_all: str,
         expect_rc: int, expect_shards: str | None, expect_says: str = "") -> None:
    rc, rows, log = run_plan(body, shards, mount, run_all)
    if rc != expect_rc:
        fail(name, f"rc={rc} (expected {expect_rc}); log tail: {log.strip()[-300:]}")
        return
    if expect_shards is not None:
        if rows.get("shards") != expect_shards:
            fail(name, f"shards={rows.get('shards')!r}, expected {expect_shards!r}")
            return
        try:
            matrix = json.loads(rows.get("matrix", "null"))
        except json.JSONDecodeError:
            fail(name, f"matrix is not JSON: {rows.get('matrix')!r}")
            return
        if matrix != list(range(1, int(expect_shards) + 1)):
            fail(name, f"matrix={matrix!r} does not enumerate 1..{expect_shards}")
            return
    if expect_says and expect_says not in log:
        fail(name, f"the log does not say {expect_says!r}; got: {log.strip()[-300:]}")
        return
    ok(name, f"shards={rows.get('shards')} matrix={rows.get('matrix')}")


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    body = extract_step(root, STEP_ID)
    print(f"executing `{STEP_ID}` from {WORKFLOW}")

    # THE DEFECT'S OWN INPUTS: MeshWeaver.Plugins#1768, run 34749441237 — shards: 4, narrowed to
    # three packages. Before the clamp this planned four shards and the fourth gated `0 of 3`.
    case("a narrowed run with 3 packages and shards:4 plans THREE shards, not four", body,
         shards="4", mount="Store AI Hosting", run_all="false",
         expect_rc=0, expect_shards="3",
         expect_says="fanning across 3 shard(s), not the caller's 4")
    case("…one package narrows to ONE shard", body,
         shards="4", mount="Hosting", run_all="false", expect_rc=0, expect_shards="1")
    case("a narrowed run with MORE packages than shards keeps the caller's fan-out", body,
         shards="4", mount="A B C D E F", run_all="false", expect_rc=0, expect_shards="4")
    case("a narrowed run with exactly `shards` packages is unchanged", body,
         shards="4", mount="A B C D", run_all="false", expect_rc=0, expect_shards="4")
    # A FULL run: `mount` is empty because nothing was narrowed, and the shards divide a population
    # this step cannot count — clamping there would collapse every push run to one shard.
    case("a FULL run (run_all=true, empty mount) keeps the caller's fan-out", body,
         shards="4", mount="", run_all="true", expect_rc=0, expect_shards="4")
    case("an unsharded caller is unchanged by the clamp", body,
         shards="1", mount="Store AI Hosting", run_all="false", expect_rc=0, expect_shards="1")
    # Nothing affected and not a full run: the step exits EARLY with nothing_to_gate, before the
    # clamp — the pre-existing contract, pinned here so the clamp cannot have moved it.
    rc, rows, _ = run_plan(body, "4", "", "false")
    if rc == 0 and rows.get("nothing_to_gate") == "true" and rows.get("shards") == "0" \
            and rows.get("matrix") == "[]":
        ok("nothing affected still exits with nothing_to_gate=true, shards=0, matrix=[]")
    else:
        fail("nothing affected still exits with nothing_to_gate", f"rc={rc} rows={rows}")
    # The input's own refusals, unchanged.
    case("a non-numeric `shards` is still refused", body,
         shards="four", mount="A", run_all="false", expect_rc=1, expect_shards=None,
         expect_says="shards must be a positive integer")
    case("`shards: 0` is still refused", body,
         shards="0", mount="A", run_all="false", expect_rc=1, expect_shards=None,
         expect_says="shards must be at least 1")
    case("`shards: 13` is still refused as a typo", body,
         shards="13", mount="A", run_all="false", expect_rc=1, expect_shards=None,
         expect_says="is a typo, not a plan")

    # 🚨 FALSIFICATION: with the clamp removed, the defect's own inputs plan FOUR shards again —
    # so the positive case above could have failed, and this harness is not measuring a constant.
    stripped = body.replace('echo "shards=$EFFECTIVE"', 'echo "shards=$SHARDS"') \
                   .replace('echo "matrix=$(seq 1 "$EFFECTIVE"', 'echo "matrix=$(seq 1 "$SHARDS"')
    if stripped == body:
        die("the falsification arm rewrote nothing — the clamp's own output lines are not in the "
            "step, so every case above would pass having checked a constant")
    rc, rows, _ = run_plan(stripped, "4", "Store AI Hosting", "false")
    if rc == 0 and rows.get("shards") == "4":
        ok("falsification arm — without the clamp the same inputs plan 4 shards over 3 packages")
    else:
        fail("falsification arm", f"rc={rc} shards={rows.get('shards')!r}; the unclamped step did "
                                 "not reproduce the defect, so the cases above prove nothing")

    if FAILURES:
        print(f"\n::error::{len(FAILURES)} case(s) FAILED: " + "; ".join(FAILURES))
        return 1
    print("\nPASS — the fan-out never exceeds the number of affected packages, a full run is "
          "unchanged, every pre-existing refusal still fires, and the falsification arm reproduces "
          "the defect.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
