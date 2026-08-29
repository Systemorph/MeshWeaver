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
        fx = tmp / "fixture.json"
        fx.write_text(json.dumps(rows if rows is not None else []))
        out = tmp / "github_output"
        out.touch()

        e = dict(os.environ)
        e["PATH"] = f"{binp}:{e['PATH']}"
        e["AZ_FIXTURE"] = str(fx)
        e["GITHUB_OUTPUT"] = str(out)
        if az_fail:
            e["AZ_FAIL"] = "1"
        else:
            e.pop("AZ_FAIL", None)
        e.update(env)

        p = subprocess.run(["bash", "-c", body], env=e, capture_output=True, text=True)
        return p.returncode, p.stdout + p.stderr, out.read_text()


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
    if failures:
        print(f"::error::{len(failures)} case(s) failed: {', '.join(failures)}")
        return 1
    print(f"all cases passed against {WORKFLOW} step `{STEP_ID}` (extracted, not copied)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
