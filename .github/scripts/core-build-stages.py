#!/usr/bin/env python3
"""core-build-stages.py — core's `.github/build-stages.json` is GENERATED from CI_SHARD_TOTAL, and checked against it.

Core declares its suites as build-host stages, ONE declaration for both routes that run them:

  * a candidate DEPLOYMENT (Doc/Architecture/DeployTestSwitchLatest §2 step 3, phase P4) submits one
    pinned run per stage — `test-shard-<i>` and `doc-gate` — and reads the set's `mw-core-tests`
    image, staged on the host at /core-tests;
  * a pull request or trunk run (Doc/Architecture/BuildHosts → "Moving a repository's tests to the
    build server") runs `build` FIRST — the whole solution, Release, warnings as errors, as
    dotnet-test.yml's `build` job does, restored OFFLINE from the host's prepared package cache, then
    `core-tests.py pack` of this run's tree into $MW_BUILD_OUTPUTS/core-tests — and then the same
    shard and doc-gate stages.

Each shard and the doc gate name BOTH roots and `--require-sha "$MW_BUILD_SHA"`: core-tests.py reads
the first pack built from the run's own commit and is RED when none is, so a run never tests the
image of whatever set the host has installed in place of its own tree.

The shard count is declared ONCE, in `dotnet-test.yml` (`CI_SHARD_TOTAL`). This file's stage list is
derived from it, so the two cannot drift: `--check` reds when the committed file differs in ANY way
from what `--write` would produce. Change CI_SHARD_TOTAL (or OWES below), run `--write`, commit both.

🚨 `owes` / `ciEquivalent` follow the fleet's ONE rule (check-build-stages.py, profile `core`; the
same rule MeshWeaver.Plugins holds as rule 7 of its admission check): the claim is refused while
anything is owed, and build-executor.py routes nothing to a host without it. OWES below is that
debt, one sentence per gap, each naming the Actions leg it is measured against. Remove an entry
only in the change that pays it.

    core-build-stages.py --write        regenerate .github/build-stages.json
    core-build-stages.py --check        red when the committed file is not the generated one, or breaks the rule
    core-build-stages.py --self-test    prove --check fires on every drift, and passes on none
"""
from __future__ import annotations

import importlib.util
import json
import os
import re
import sys

WORKFLOW = ".github/workflows/dotnet-test.yml"
STAGES = ".github/build-stages.json"
SHARD_TOTAL_RE = re.compile(r"^\s*CI_SHARD_TOTAL:\s*(\d+)\s*(?:#.*)?$", re.M)
STAGE_NAME = re.compile(r"^[a-z0-9][a-z0-9._-]*$")
# Where the build host stages the mw-core-tests image's tree (read-only), beside /portal/app and
# /host — the composition MeshWeaver.Deploy's build-host chart owns. A deployment reads it.
IMAGE_ROOT = "/core-tests"
# Where a host run's own `build` stage packs this run's tree.
RUN_ROOT = '"${MW_BUILD_OUTPUTS:?the host names no run outputs (MW_BUILD_OUTPUTS)}/core-tests"'
RUNNER = "python3 deploy/core-tests/core-tests.py"
SHA_ENV = '"${MW_BUILD_SHA:?the host names no commit (MW_BUILD_SHA)}"'
ROOTS = f"--root {RUN_ROOT} --root {IMAGE_ROOT} --require-sha {SHA_ENV}"
# dotnet-test.yml's `build` job cap.
BUILD_TIMEOUT_MINUTES = 25
# dotnet-test.yml's shard job cap: the same budget a shard has on a runner.
SHARD_TIMEOUT_MINUTES = 20
# dotnet-test.yml's doc-gate job cap is 12 on a runner; a host also stages the gate's trees.
DOC_GATE_TIMEOUT_MINUTES = 25
SOLUTION = "MeshWeaver.slnx"
# The `build` job, as one stage. No restore STEP: the host comes prepared, and the build resolves
# every package from the host's read-only prepared cache (bound by `needs: nuget` as
# NUGET_FALLBACK_PACKAGES) with that cache as its ONLY source — a package the host was not prepared
# with is a red restore, never a download.
BUILD_COMMAND = (
    "set -euo pipefail; "
    ': "${NUGET_FALLBACK_PACKAGES:?the host bound no prepared package cache (needs: nuget)}"; '
    f'dotnet build {SOLUTION} -c Release -p:CIRun=true -warnaserror --source "${{NUGET_FALLBACK_PACKAGES}}"; '
    "missing=$(comm -23 <(find test -name '*.csproj' ! -path '*/bin/*' | sort) "
    f"<(grep -oE 'test/[^\"]+\\.csproj' {SOLUTION} | sort)); "
    f'if [ -n "$missing" ]; then echo "::error::these test projects are not in {SOLUTION}, so they are never built '
    'and their tests never run: $missing"; exit 1; fi; '
    f'{RUNNER} pack --repo . --out {RUN_ROOT} --sha {SHA_ENV} --version "run-${{MW_BUILD_SHA}}"'
)
# What core's stages still check LESS than dotnet-test.yml's legs, or cannot yet deliver as a
# verdict. Remove an entry only in the change that pays it (check-build-stages.py refuses the
# ciEquivalent claim while any remains).
OWES = [
    "route: dotnet-test.yml has no admission — no job calls build-executor.py and no follow job (build-follow.yml) "
    "stands in for the `build`, `test` and `doc-gate` jobs, so even an equivalent declaration would route nothing "
    "(the satellites' shape: node-repo-build-executor.yml + build-follow.yml + a host-verdict input)",
    "Consolidate test results: collect-results consolidates the testResults-shard<i> trx artifacts of the `test` matrix; "
    "a host shard hands no trx back (nothing is written under $MW_BUILD_ARTIFACTS), so the consolidated verdict, the "
    "failure summary and the per-project MISSING_BIN evidence cannot be built from a host run",
    "fan-out: a host runs one run's stages serially — build 25 + 8 shards x 20 + doc-gate 25 minutes is past the run "
    "deadline; the deployment route already submits one pinned run per stage, but such runs do not share one "
    "$MW_BUILD_OUTPUTS, so the shards need this run's pack through the shared cache (`needs: outputs`), which today "
    "promotes only a trunk run's content-keyed output (Actions: one `build`, then the 8-way `test` matrix)",
    "prepared packages: the build restores ONLY from the host's prepared cache (NUGET_FALLBACK_PACKAGES); that the "
    "cache holds core's whole restore closure, refreshed when Directory.Packages.props changes, is not established "
    "(Actions: the `build` job's NuGet-shelf hydration and restore)",
    "browser: the shards that run browser tests need the headless browser and the font set the `browser-fonts` job "
    "stages and each `test` shard provisions; the build-host image (MeshWeaver.Deploy deploy/build-host/Dockerfile) "
    "carries neither",
    "containers: the shards' Testcontainers projects start a database in Docker; a host's sandbox has no Docker "
    "daemon (its database is sandbox-postgres.sh), so those projects cannot run there as they run in the `test` job",
    "shard post-gates: the `test` job runs check-untyped-content.sh (no content degraded to an untyped JsonElement) "
    "over each shard's logs; core-tests.py run does not",
    "dependent legs: platform-compat and licence-gate read the `build` job's artifacts (shard 0's output, the licence "
    "evidence); with the build on a host they need its outputs handed back",
    "prepared mesh: on a host no test may install a mesh from scratch — a prepared mesh snapshot is cloned per run; "
    "which of core's fixtures that replaces is not established (owned by the prepared-server work)",
    "evidence: no stage has run on a host — a probe of every stage on build-latest must be green and agree with the "
    "Actions legs of the same commit",
]
COMMENT = ("GENERATED by .github/scripts/core-build-stages.py --write from CI_SHARD_TOTAL in "
           ".github/workflows/dotnet-test.yml; --check reds any drift. One declaration for both routes: a "
           "candidate DEPLOYMENT runs test-shard-<i> and doc-gate as pinned runs from the set's mw-core-tests "
           "image (/core-tests); a host PR/trunk run builds first and packs its own tree. Every shard reads the "
           "pack built from the run's own commit (--require-sha), never another set's. `owes` is what the stages "
           "still check less than dotnet-test.yml; ciEquivalent is refused while it is non-empty "
           "(check-build-stages.py, profile core).")


def shard_stage_name(i: int) -> str:
    return f"test-shard-{i}"


def read_shard_total(text: str) -> int:
    found = SHARD_TOTAL_RE.findall(text)
    if len(found) != 1:
        raise SystemExit(f"::error::{WORKFLOW} must declare CI_SHARD_TOTAL exactly once (found {len(found)})")
    return int(found[0])


def generate(total: int) -> dict:
    stages = [{"name": "build", "kind": "build", "command": BUILD_COMMAND,
               "timeoutMinutes": BUILD_TIMEOUT_MINUTES, "needs": ["nuget"]}]
    stages += [{"name": shard_stage_name(i), "kind": "test",
                "command": f"{RUNNER} run --shard {i} --total {total} {ROOTS}",
                "timeoutMinutes": SHARD_TIMEOUT_MINUTES} for i in range(total)]
    stages.append({"name": "doc-gate", "kind": "test",
                   "command": f"{RUNNER} doc-gate {ROOTS} --repo .",
                   "timeoutMinutes": DOC_GATE_TIMEOUT_MINUTES})
    return {"$comment": COMMENT, "owes": list(OWES), "stages": stages}


def _rule():
    """check-build-stages.py — the fleet's ONE declaration rule, beside this file."""
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "check-build-stages.py")
    spec = importlib.util.spec_from_file_location("check_build_stages", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def render(doc: dict) -> str:
    return json.dumps(doc, indent=2) + "\n"


def problems(committed_text: str | None, workflow_text: str) -> list[str]:
    total = read_shard_total(workflow_text)
    if committed_text is None:
        return [f"{STAGES} is absent — run core-build-stages.py --write"]
    try:
        doc = json.loads(committed_text)
    except ValueError as e:
        return [f"{STAGES} is not JSON: {e}"]
    rule_problems, _ = _rule().problems(committed_text, "core")
    out: list[str] = list(rule_problems)
    stages = doc.get("stages") if isinstance(doc, dict) else None
    names = [s.get("name") for s in stages if isinstance(s, dict)] if isinstance(stages, list) else []
    shards = sorted(n for n in names if isinstance(n, str) and n.startswith("test-shard-"))
    want = [shard_stage_name(i) for i in range(total)]
    if sorted(shards) != sorted(want):
        out.append(f"{STAGES} declares {len(shards)} shard stage(s) {shards} but CI_SHARD_TOTAL is {total} "
                   f"(expected {want})")
    bad = [n for n in names if not (isinstance(n, str) and STAGE_NAME.match(n))]
    if bad:
        out.append(f"stage name(s) {bad} are not one path segment [a-z0-9][a-z0-9._-]*")
    if committed_text != render(generate(total)):
        out.append(f"{STAGES} is not what core-build-stages.py --write generates for CI_SHARD_TOTAL={total} "
                   f"— regenerate it, never hand-edit")
    return out


def _read(path: str) -> str | None:
    try:
        with open(path) as fh:
            return fh.read()
    except FileNotFoundError:
        return None


def _self_test() -> int:
    fails: list[str] = []

    def expect(cond: bool, what: str) -> None:
        print(("  ok   " if cond else "  FAIL ") + what)
        if not cond:
            fails.append(what)

    wf8 = "env:\n  CI_SHARD_TOTAL: 8\n"
    good = render(generate(8))
    print("positive control:")
    expect(problems(good, wf8) == [], "the generated file passes against the total it was generated for (and the fleet's declaration rule)")
    doc = json.loads(good)
    expect([s["name"] for s in doc["stages"]] == ["build"] + [f"test-shard-{i}" for i in range(8)] + ["doc-gate"],
           "build first, then eight shard stages test-shard-0..7, then doc-gate")
    expect(all(s["timeoutMinutes"] <= 45 for s in doc["stages"]), "every stage cap is within the host's 45-minute limit")
    expect(f"run --shard 3 --total 8 " in doc["stages"][4]["command"], "stage test-shard-i runs shard i of N")
    expect(all("--require-sha" in s["command"] for s in doc["stages"][1:]), "every shard and the doc gate read only a pack of the run's own commit")
    expect(doc["stages"][0]["needs"] == ["nuget"], "the build reads the host's prepared package cache, and nothing else")
    expect(doc["owes"] == OWES and "ciEquivalent" not in doc, "the debt is declared and nothing is claimed")
    print("negative controls — each must be RED:")
    expect(any("CI_SHARD_TOTAL is 9" in p for p in problems(good, "env:\n  CI_SHARD_TOTAL: 9\n")),
           "CI_SHARD_TOTAL raised to 9 without regenerating")
    expect(any("CI_SHARD_TOTAL is 7" in p for p in problems(good, "env:\n  CI_SHARD_TOTAL: 7\n")),
           "CI_SHARD_TOTAL lowered to 7 without regenerating")
    d = json.loads(good); del d["stages"][5]
    expect(problems(render(d), wf8) != [], "a shard stage deleted by hand")
    d = json.loads(good); d["stages"][1]["command"] = "true"
    expect(problems(render(d), wf8) != [], "a shard command replaced by one that tests nothing")
    d = json.loads(good); d["ciEquivalent"] = True
    expect(any("while it owes" in p for p in problems(render(d), wf8)), "a ciEquivalent claim with the debt still open")
    d = json.loads(good); d["owes"] = []
    expect(any("owes nothing" in p for p in problems(render(d), wf8)), "the debt deleted by hand without the claim")
    d = json.loads(good); d["stages"][0]["command"] = d["stages"][0]["command"].replace(" -warnaserror", "")
    expect(any("-warnaserror" in p for p in problems(render(d), wf8)), "the build stage sheds warnings-as-errors")
    d = json.loads(good); d["stages"][2]["command"] = d["stages"][2]["command"].replace("--require-sha", "--sha")
    expect(any("--require-sha" in p for p in problems(render(d), wf8)), "a shard that would read whatever pack the host has")
    expect(problems(None, wf8) != [], "an absent file")
    expect(problems("{", wf8) != [], "a file that is not JSON")
    try:
        problems(good, "env:\n  X: 1\n")
        expect(False, "a workflow with no CI_SHARD_TOTAL is refused")
    except SystemExit:
        expect(True, "a workflow with no CI_SHARD_TOTAL is refused")
    print(f"\n{'SELF-TEST FAILED' if fails else 'self-test: every drift is red, the generated file is green'}")
    return 1 if fails else 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return _self_test()
    root = os.getcwd()
    workflow = _read(os.path.join(root, WORKFLOW))
    if workflow is None:
        print(f"::error::{WORKFLOW} not found — run from the repository root")
        return 2
    if argv == ["--write"]:
        with open(os.path.join(root, STAGES), "w") as fh:
            fh.write(render(generate(read_shard_total(workflow))))
        print(f"wrote {STAGES} for CI_SHARD_TOTAL={read_shard_total(workflow)}")
        return 0
    if argv == ["--check"]:
        found = problems(_read(os.path.join(root, STAGES)), workflow)
        for p in found:
            print(f"::error::{p}")
        if not found:
            print(f"{STAGES}: build + {read_shard_total(workflow)} shard stages + doc-gate, generated from CI_SHARD_TOTAL — in step; "
                  f"owes {len(OWES)} item(s), so runs stay on GitHub Actions")
        return 1 if found else 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
