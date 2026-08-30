#!/usr/bin/env python3
"""EXECUTE main-cd's pure-shell decision steps against fixtures. No Azure, no cluster, no secret.

WHY
---
`main-cd.yml` runs on `workflow_run`, `schedule` and `workflow_dispatch` — never on
`pull_request`. So the first execution of an edit to it is in production, on a schedule, and
its decision steps are exactly the ones whose verdict everyone downstream believes.

The release-version recovery step is the worked example, and it is the reason this file exists:
it had NEVER ONCE SUCCEEDED in its life. Nine consecutive scheduled reconciles (2026-08-28
22:12Z → 08-29 05:26Z, ~33 h) died in it, and every one of them accused promote — a component
that was working perfectly. MeshWeaver#2642 fixed it; this executes it.

HOW IT STAYS HONEST
-------------------
* The step is EXTRACTED FROM THE WORKFLOW by its `id:`, never copied here. A copy passes while
  the real thing rots.
* If the step cannot be found, or has grown a `${{ }}` expression this harness cannot supply, or
  has stopped calling the command being stubbed, the harness FAILS RED. It never reports a pass
  for a step it did not run — that is the same "a skipped gate ticks like a passed one" defect the
  thing being tested was full of.
* `az` is stubbed with a script that answers from a fixture using REAL jmespath — the same engine
  the azure-cli uses — so the null-throw is reproduced rather than asserted about.

THE FIXTURE is the live registry's shape on the day of the incident: 2101 manifests in
`memex-portal-ai`, 16 of them UNTAGGED (`tags: null` — the orphaned per-platform children an
index push leaves behind), and one digest carrying `aaf95af`, `main` and `3.0.0-rc8.ci.6360`.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

WORKFLOW = ".github/workflows/main-cd.yml"
STEP_ID = "release"
DECIDE_STEP_ID = "decide"

SHORT_SHA = "aaf95af"
VERSION = "3.0.0-rc8.ci.6360"

# ── the stub `az` ───────────────────────────────────────────────────────────────────────────
# Reproduces the three behaviours that matter: a successful jmespath query, a query that THROWS
# on a null field (az's own message, exit 1), and a call that fails for an unrelated reason
# (auth). Nothing else about `az` is modelled, and the harness asserts the step still calls the
# subcommand being stubbed, so a step that grew a second az call cannot pass on this one.
AZ_STUB = r'''#!/usr/bin/env python3
import json, os, sys
import jmespath
from jmespath.exceptions import JMESPathError

if os.environ.get("AZ_FAIL"):
    sys.stderr.write("ERROR: Please run 'az login' to setup account.\n")
    sys.exit(1)

argv = sys.argv[1:]
if argv[:3] != ["acr", "manifest", "list-metadata"]:
    sys.stderr.write(f"stub az: unmodelled invocation {argv!r}\n")
    sys.exit(97)

query = None
for i, a in enumerate(argv):
    if a == "--query":
        query = argv[i + 1]
if query is None:
    sys.stderr.write("stub az: no --query\n")
    sys.exit(97)

data = json.load(open(os.environ["AZ_FIXTURE"]))
try:
    result = jmespath.compile(query).search(data)
except JMESPathError as e:
    # az surfaces a jmespath failure verbatim on stderr and exits non-zero.
    sys.stderr.write(f"{e}\n")
    sys.exit(1)

for row in result or []:
    print(row)
'''


def fixture(tagged: bool) -> list[dict]:
    """memex-portal-ai as it stood on 2026-08-29: 2101 manifests, 16 with `tags: null`."""
    rows: list[dict] = [{"digest": f"sha256:{i:064x}", "tags": None} for i in range(16)]
    rows += [{"digest": f"sha256:{i:064x}", "tags": [f"ci.{i}"]} for i in range(16, 2100)]
    rows.append(
        {
            "digest": "sha256:6c3abd508033db59865e8bedf68076bdb75b4c044e3fa1159c01eff98b2a1089",
            # Phase A tags the short sha; Phase C arms the release on the SAME digest.
            "tags": [SHORT_SHA, "main", VERSION] if tagged else [SHORT_SHA, "main"],
        }
    )
    return rows


# ── extraction ──────────────────────────────────────────────────────────────────────────────
# ── the stub `gh` ───────────────────────────────────────────────────────────────────────────
# 🚨 THIS IS A SAFETY DEVICE, not a convenience. The `decide` step posts a heal COMMENT to the
# CD-failure issue on a reconcile attempt, and `run_step` inherits the caller's environment —
# so on a developer machine with a live `gh` login, running this harness POSTS REAL COMMENTS TO
# REAL ISSUES. That is not hypothetical: it happened while these cases were being written, three
# times, to Systemorph/MeshWeaver#2810, and the comments had to be deleted by hand.
#
# A test harness must not be able to mutate anything outside its temp directory. The stub records
# what was asked and answers nothing, so the step's control flow is unchanged and its side effect
# is not. The credentials are cleared as well (below) — either alone would do, and one of them
# will still be there after someone edits the other.
GH_STUB = """#!/usr/bin/env bash
echo "gh $*" >> "$GH_CALLS"
exit 0
"""


def extract_step(root: Path, step_id: str) -> str:
    import yaml

    doc = yaml.safe_load((root / WORKFLOW).read_text())
    for job in (doc.get("jobs") or {}).values():
        for step in job.get("steps") or []:
            if isinstance(step, dict) and step.get("id") == step_id:
                body = step.get("run")
                if not body:
                    die(f"step id `{step_id}` in {WORKFLOW} has no `run:` — nothing to execute.")
                if "${{" in body:
                    die(
                        f"step id `{step_id}`'s `run:` now contains a ${{{{ }}}} expression, which "
                        "this harness cannot supply. It is refusing to execute a body it would "
                        "have to rewrite — pass the value through `env:` instead (every input it "
                        "takes today already is)."
                    )
                return body
    die(
        f"no step with `id: {step_id}` in {WORKFLOW}. It was renamed, moved or deleted — and this "
        "harness will not report a pass for a step it could not find. Re-point STEP_ID."
    )


def die(msg: str):
    print(f"::error::{msg}")
    sys.exit(1)


# ── running one case ────────────────────────────────────────────────────────────────────────
def run_step(body: str, env: dict[str, str], rows: list[dict] | None, az_fail: bool = False):
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        binp = tmp / "bin"
        binp.mkdir()
        (binp / "az").write_text(AZ_STUB)
        (binp / "az").chmod(0o755)
        (binp / "gh").write_text(GH_STUB)
        (binp / "gh").chmod(0o755)
        calls = tmp / "gh_calls"
        calls.touch()
        fx = tmp / "fixture.json"
        fx.write_text(json.dumps(rows if rows is not None else []))
        out = tmp / "github_output"
        out.touch()
        # The runner provides these to every step; a step that writes its decision to the job
        # summary (the `decide` step does) dies on `set -u` without them. Supplying them here is
        # not indulgence — omitting them would make the harness fail for a reason that has nothing
        # to do with the logic under test, which is how a harness gets disabled.
        summary = tmp / "github_step_summary"
        summary.touch()

        e = dict(os.environ)
        e["PATH"] = f"{binp}:{e['PATH']}"
        e["AZ_FIXTURE"] = str(fx)
        e["GITHUB_OUTPUT"] = str(out)
        e["GITHUB_STEP_SUMMARY"] = str(summary)
        e.setdefault("GITHUB_REPOSITORY", "Systemorph/MeshWeaver")
        e["GH_CALLS"] = str(calls)
        # Belt AND braces: the stub above shadows `gh` on PATH, and these leave a real `gh` — if one
        # is ever reached another way — with no credential to write with.
        for cred in ("GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN"):
            e[cred] = ""
        e["GH_CONFIG_DIR"] = str(tmp / "gh-config")
        if az_fail:
            e["AZ_FAIL"] = "1"
        else:
            e.pop("AZ_FAIL", None)
        e.update(env)

        p = subprocess.run(["bash", "-c", body], env=e, capture_output=True, text=True)
        # The job summary is part of what a decision step SAYS, so a case asserting on the
        # decision's wording must be able to see it.
        return p.returncode, p.stdout + p.stderr + summary.read_text(), out.read_text()



# ── the DECIDE step: the path that only runs when main goes quiet ────────────────────────────
def run_decide_cases(root, case) -> None:
    """
    🚨 <b>#2643 — a code path gated on INACTIVITY gets no coverage from ordinary traffic.</b>

    `decide` chooses between building an image set and re-asserting the content bake
    (`bake_only`). The bake-only branch is reached only on a reconcile that finds a COMPLETE image
    set and nothing to build — i.e. only when nobody is pushing. On a busy trunk it never runs; its
    first execution was in production at 22:12 on a Friday, and it then failed nine times out of
    nine over 33 h before anyone noticed, because every fix-verifying push took the other branch.

    #2642 fixed the specific defect and this harness executes THAT step. What it did not do is
    remove the shape: the branch is still only exercised by silence. These cases exercise it on
    every run instead — the deliberate exercise the issue asks for, without waiting for the trunk
    to fall quiet or adding a dispatch input nobody remembers to use.

    The inputs are the ones the step reads from `env:`, so this drives the real decision logic
    rather than a restatement of it.
    """
    body = extract_step(root, DECIDE_STEP_ID)

    # A stub is only evidence about what it stubs; the same rule as the release step. If the
    # branch under test stops writing this output, every case below would pass vacuously.
    if "bake_only=true" not in body:
        die(
            f"step `{DECIDE_STEP_ID}` no longer writes `bake_only=true` — the branch these cases "
            "exercise is gone or renamed, so they would pass having tested nothing. Update the "
            "harness with the step."
        )

    # A RECONCILE (not a push): no workflow_run payload, so the step takes the reconcile path.
    reconcile = {
        "GREEN": "true", "PENDING": "false", "CONCL": "success",
        "SHORT": "abc1234", "REASON": "reconcile", "RELEVANT": "true",
        "AGE_MIN": "120", "FRESH_AGE_MIN": "120", "BATCH_WINDOW": "",
        "MAX_ATTEMPTS": "3", "RUN_URL": "https://example.invalid/run",
        "GH_TOKEN": "", "COMPLETE": "true",
    }

    rc, log, outputs = run_step(body, reconcile, None)
    case("a reconcile with a COMPLETE image set takes the bake-only branch",
         rc == 0 and "bake_only=true" in outputs, f"rc={rc} out={outputs!r} log={log}")
    case("...and it does NOT also publish an image set",
         "publish=true" not in outputs.replace("publish=true\n", "publish=true\n") or "publish=false" in outputs,
         f"out={outputs!r}")
    case("...and it says WHY, in the decision log",
         "complete image set" in log, f"log={log}")

    # The neighbour that must NOT be confused with it: an INCOMPLETE set on the same event is a
    # real build. If these two ever collapse into one, delivery either stops or doubles.
    rc, log, outputs = run_step(body, {**reconcile, "COMPLETE": "false"}, None)
    case("an INCOMPLETE image set on the same event still builds",
         rc == 0 and "bake_only=true" not in outputs, f"rc={rc} out={outputs!r} log={log}")


def main() -> int:
    root = Path(os.environ.get("GITHUB_WORKSPACE", ".")).resolve()
    try:
        import jmespath  # noqa: F401
        import yaml  # noqa: F401
    except ImportError as exc:
        die(f"this harness cannot run — {exc}. pip install jmespath pyyaml.")

    body = extract_step(root, STEP_ID)
    # A stub is only evidence about the call it stubs. If the step stopped making that call, the
    # cases below would all pass while testing nothing.
    if "az acr manifest list-metadata" not in body:
        die(
            f"step `{STEP_ID}` no longer calls `az acr manifest list-metadata` — the stub these "
            "cases rely on is no longer the seam under test, so every case below would pass "
            "vacuously. Update the harness with the step."
        )

    failures: list[str] = []

    def case(name: str, ok: bool, detail: str = ""):
        print(f"  {'PASS' if ok else 'FAIL'}  {name}")
        if not ok:
            print(f"        {detail}")
            failures.append(name)

    base = {"RELEASE_VERSION": "", "BAKE_ONLY": "true", "SHORT_SHA": SHORT_SHA}

    # 1 ── A full run: portal-image minted the version, so nothing is read back.
    rc, log, outputs = run_step(body, {**base, "RELEASE_VERSION": "3.0.0-rc9.ci.1"}, fixture(True))
    case("a minted version passes straight through",
         rc == 0 and "version=3.0.0-rc9.ci.1" in outputs, f"rc={rc} out={outputs!r} log={log}")

    # 2 ── THE CASE THAT HAD NEVER SUCCEEDED. Bake-only reconcile, and the repository holds 16
    #      untagged manifests whose `tags` is null. Before #2642 the query threw on the first of
    #      them, jmespath aborted the WHOLE query, az exited non-zero, `2>/dev/null || true`
    #      turned that into an empty tag list, and the step blamed promote.
    rc, log, outputs = run_step(body, base, fixture(True))
    case("a bake-only reconcile recovers the version DESPITE 16 untagged manifests",
         rc == 0 and f"version={VERSION}" in outputs, f"rc={rc} out={outputs!r} log={log}")

    # 3 ── The genuine "promote never armed it" case must still be loud, and must still say so.
    rc, log, outputs = run_step(body, base, fixture(False))
    case("a digest with no version tag is still a loud, accurate stop",
         rc != 0 and "carries no version tag" in log, f"rc={rc} log={log}")

    # 4 ── 🚨 THE REGRESSION GUARD. When az itself fails, the step must fail with az's message —
    #      it must NOT convert the failure into "there is no version tag" and blame promote.
    #      Reintroduce `2>/dev/null || true` on that read and this case goes red: `tags` becomes
    #      empty, `version` becomes empty, and the step prints the promote accusation.
    rc, log, outputs = run_step(body, base, fixture(True), az_fail=True)
    case("a FAILING az fails the step", rc != 0, f"rc={rc} log={log}")
    case("a FAILING az is never reported as promote's fault",
         "Fix promote" not in log and "carries no version tag" not in log,
         f"the step blamed a healthy component for its own failed call:\n{log}")
    case("a FAILING az surfaces az's own message", "az login" in log, f"log={log}")

    # 5 ── A non-bake-only run with no version is a refusal, not a read.
    rc, log, outputs = run_step(body, {**base, "BAKE_ONLY": "false"}, fixture(True))
    case("no version on a non-bake-only run refuses rather than guessing",
         rc != 0 and "unknown release" in log, f"rc={rc} log={log}")

    print()
    print(f"── step `{DECIDE_STEP_ID}` ──")
    run_decide_cases(root, case)

    print()
    if failures:
        print(f"::error::{len(failures)} case(s) failed: {', '.join(failures)}")
        return 1
    print(f"all cases passed against {WORKFLOW} steps `{STEP_ID}` + `{DECIDE_STEP_ID}` (extracted, not copied)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
