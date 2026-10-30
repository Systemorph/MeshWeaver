#!/usr/bin/env python3
"""check-build-stages.py — a repository's `.github/build-stages.json` claims CI equivalence exactly when it owes nothing.

The ONE declaration rule for every repository a standing build host may run (MeshWeaver.Plugins
`Hosting/StandingBuildHosts` §7 H7, `Hosting/BuildService` §5; platform reference
Doc/Architecture/BuildHosts → "Moving a repository's tests to the build server"). MeshWeaver.Plugins
holds the same rule as rule 7 of its own `check-build-queue-admission.py`; this file is that rule for
core and for every satellite node repo, which run it through `node-repo-validate.yml`.

A declaration is `{"stages": [...], "owes": [...], "ciEquivalent": true?, "restore": {...}?, "parallel": true?}`:

  * `stages` — what a host runs, in order (the runner, MeshWeaver.Deploy `build-run.py`, validates the
    same schema: one-segment name, kind build|test, a command, `timeoutMinutes` in (0, 45], `needs`
    drawn from plugins|outputs|nuget);
  * `restore` — optional: `{"command": "…", "timeoutMinutes": N}` (N in (0, 45], default 20), the
    restore the host runs ONCE per restore key from the DEFAULT BRANCH's tip to warm its shared NuGet
    cache (MeshWeaver.Deploy#68, build-run.py `validate_restore`); a run's own declaration is never
    executed;
  * `parallel` — optional, a bool: the repository declares its stages safe to run CONCURRENTLY on one
    host (MeshWeaver.Deploy build-run.py, fan-out: independent stages up to the host's
    `MW_BUILD_STAGE_SLOTS`, a `build` stage alone, each stage its own `$MW_BUILD_ARTIFACTS`);
  * `owes` — every thing those stages still check LESS than the Actions legs they would replace,
    one sentence each, naming the Actions leg;
  * `ciEquivalent` — the claim. `build-executor.py` routes a run's heavy legs to a host ONLY on
    `"ciEquivalent": true` AND an empty `owes`.

The rule (rule 7): the claim with an open debt is RED (a host green would check less than Actions);
a debt-free declaration that does not claim is RED (`owes` is how "not yet" says why); a malformed
`owes` or `ciEquivalent` is RED. Each PROFILE adds the parity a stage must keep — a substring of its
command — so a stage cannot quietly shed a check it has:

  core       `build` (Release, warnings as errors, the whole solution, every test project a member,
             a restore from the host's prepared package cache as fallback folder, the test pack) first; every
             `test-shard-<i>` and `doc-gate` run from a pack whose commit is the run's own commit; a
             `restore` that warms the build's own closure (the solution, Configuration=Release, CIRun=true,
             never a --source); the build stages the licence evidence from its own restore and hands it back
             with shard 0's plugin tester (host-build-outputs.py), the inputs of platform-compat and licence-gate.
  node-repo  the stages follow the lanes `.github/workflows/ci.yml` calls: `node-repo-compile-check.yml`
             ⇒ `compile-check` (core's compile-check.py, the repository's compile-check.allow, the
             host's reference set); `node-repo-gate.yml` ⇒ `gate` (the tester and dotnet through the
             host's own paths — never a hard-coded `/host/`, the gate's allow file, the warning baseline
             when ci.yml passes one, the gate seeded with its own bake, the Tests-area ratchet over the
             log, the log handed back as an H9 artifact); the gate is never narrowed to a `--shard`
             slice it does not complete and fold, and every check reaches the stage's exit code
             (`set -e` + pipefail, or each exit code captured and exited with) — #7456.

ABSENCE. A repository with no declaration is NOT MIGRATED: build-executor.py answers `actions` for it
and every heavy leg runs on GitHub as before, so nothing reads green that did not run. Without
`--require` that is said (a notice) and exits 0; with `--require` it is RED.

    check-build-stages.py --profile core|node-repo [--file F] [--workflow W] [--require]
    check-build-stages.py --self-test

Stdlib only. Exit 0 green, 1 red, 2 a usage error.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from typing import Optional

STAGES_FILE = ".github/build-stages.json"
CI_WORKFLOW = ".github/workflows/ci.yml"
STAGE_NAME = re.compile(r"^[a-z0-9][a-z0-9._-]*$")
KINDS = {"build", "test"}
NEEDS = {"plugins", "outputs", "nuget"}
MAX_TIMEOUT_MINUTES = 45
HARD_CODED_HOST = re.compile(r"(^|[\s\"'=])/host/")

# core — what each stage must keep saying, and why (dotnet-test.yml's job it mirrors).
CORE_BUILD_PARITY = [
    ("dotnet build MeshWeaver.slnx", "the whole solution, as the `build` job builds it"),
    ("-c Release", "Release, as CI builds"),
    ("-warnaserror", "warnings as errors (merge precondition (a))"),
    ("-p:CIRun=true", "the CI build switch the `build` job passes"),
    ("${NUGET_FALLBACK_PACKAGES:?", "the host's prepared package cache is bound (the fallback folder every package is read from)"),
    ("prepared package cache did not hold", "a package the cache lacks is NAMED, never fetched silently"),
    ("are not in MeshWeaver.slnx", "every test project is a solution member (the `build` job's step of that name)"),
    ("core-tests.py pack", "the shards' units packed by shard-assign.sh, the SAME assignment the matrix uses"),
    ("--sha \"${MW_BUILD_SHA", "the pack names the commit it was built from"),
    ("check-licenses.py --stage-evidence", "the licence evidence staged from THIS build's restore, as the `build` job "
     "stages it for licence-gate"),
    ("host-build-outputs.py stage", "shard 0's plugin tester, the licence evidence and the tested tree handed back for "
     "platform-compat and licence-gate (the `build` job's two artifacts)"),
]
# The host's default-branch restore must warm the closure the `build` stage's implicit restore reads — the same
# solution and the same global properties — or every run restores per run again (MWPREP001).
CORE_RESTORE_PARITY = [
    ("dotnet restore MeshWeaver.slnx", "the whole solution, the closure the `build` stage restores"),
    ("-p:Configuration=Release", "Release, as the build's `-c Release` restores"),
    ("-p:CIRun=true", "the CI build switch the build passes, which conditions package references"),
    ("dotnet restore .github/nuget-warm/ResolverFixturePackages.csproj",
     "the packages NuGetAssemblyResolver fetches at test time, which no project's closure holds"),
]
CORE_RUN_PARITY = [
    ("--require-sha \"${MW_BUILD_SHA", "the tests that run are the run's own commit's — never another set's image"),
    ("--root \"${MW_BUILD_OUTPUTS", "this run's own pack is a candidate root"),
]
CORE_SHARD_PARITY = [("core-tests.py run --shard", "one shard of the suite, the `test` matrix's shard loop — its markers, "
                      "the crash written into the trx, the results tree handed back, the untyped-content gate"),
                     ("--repo .", "the shard records and gates with the run's own checkout's scripts "
                      "(record-host-crash.py, check-untyped-content.sh), as the `test` job does")] + CORE_RUN_PARITY
CORE_DOC_GATE_PARITY = [("core-tests.py doc-gate", "the doc and samples gates, the `doc-gate` job")] + CORE_RUN_PARITY

# node-repo — the lane a satellite's ci.yml calls ⇒ the stage that must mirror it.
LANE_STAGE = {"node-repo-compile-check.yml": "compile-check", "node-repo-gate.yml": "gate"}
NODE_COMPILE_CHECK_PARITY = [
    ("compile-check.py", "core's canonical compile-check, as node-repo-compile-check.yml runs it"),
    ("${MW_BUILD_COMPILE_REFS", "the host's PREPARED reference set — the installed platform's app plus its shared frameworks, "
     "CI's refs/ shape (the floor); `${MeshWeaverRefs` alone is the app without System.Private.CoreLib, which compile-check.py refuses"),
    ("scripts/compile-check.allow", "the repository's compile-check debt (MW_ALLOW_FILE in the lane)"),
]
NODE_GATE_PARITY = [
    ("${MW_BUILD_GATE_HOST", "the tester through the host's own path"),
    ("${MW_BUILD_PORTAL_DOTNET", "the portal's own dotnet"),
    ("${MeshWeaverRefs", "the host's installed platform (the floor) as the portal app"),
    (" compile ", "the gate's compile step (bake-then-gate's first half)"),
    ("--seed", "the gate seeded with its OWN bake (bake-then-gate's second half)"),
    ("check-test-suites.py --gate-log", "the Tests-area ratchet over the gate log (ci.yml's tests-ratchet)"),
    ("${MW_BUILD_ARTIFACTS", "the gate log handed back as an H9 artifact"),
]


def _read(path: str) -> Optional[str]:
    try:
        with open(path, encoding="utf-8") as fh:
            return fh.read()
    except FileNotFoundError:
        return None


def schema_problems(doc: object) -> list[str]:
    """The runner's own schema (MeshWeaver.Deploy build-run.py `validate_stages`), so a declaration
    this check passes is one the host will load."""
    if not isinstance(doc, dict):
        return ["build-stages.json is not an object"]
    stages = doc.get("stages")
    if not isinstance(stages, list) or not stages:
        return ["build-stages.json must carry `stages`, a non-empty list — a run with nothing to run is not a green one"]
    out: list[str] = []
    seen: set[str] = set()
    for i, s in enumerate(stages):
        if not isinstance(s, dict):
            out.append(f"stage #{i} is not an object")
            continue
        name = s.get("name")
        if not isinstance(name, str) or not STAGE_NAME.match(name):
            out.append(f"stage #{i} name {name!r} is not one path segment [a-z0-9][a-z0-9._-]*")
            continue
        if name in seen:
            out.append(f"stage `{name}` is declared twice")
        seen.add(name)
        if s.get("kind") not in KINDS:
            out.append(f"stage `{name}` kind {s.get('kind')!r} must be one of {sorted(KINDS)}")
        if not isinstance(s.get("command"), str) or not s["command"].strip():
            out.append(f"stage `{name}` has no command")
        tmo = s.get("timeoutMinutes", MAX_TIMEOUT_MINUTES)
        if isinstance(tmo, bool) or not isinstance(tmo, (int, float)) or not (0 < tmo <= MAX_TIMEOUT_MINUTES):
            out.append(f"stage `{name}` timeoutMinutes {tmo!r} must be in (0, {MAX_TIMEOUT_MINUTES}] — a stage past it is stuck, not slow")
        needs = s.get("needs", [])
        if not isinstance(needs, list) or not all(isinstance(n, str) and n in NEEDS for n in needs):
            out.append(f"stage `{name}` needs {needs!r} must be a list drawn from {sorted(NEEDS)}")
    out += restore_schema_problems(doc)
    if "parallel" in doc and not isinstance(doc["parallel"], bool):
        out.append(f"build-stages.json `parallel` is {doc['parallel']!r} — true, false or absent (the runner refuses anything else)")
    return out


RESTORE_DEFAULT_TIMEOUT_MINUTES = 20


def restore_schema_problems(doc: dict) -> list[str]:
    """`restore`, when declared: the runner's `validate_restore` (MeshWeaver.Deploy#68). Absent is fine — the host
    then warms nothing from the default branch and says so. A present-but-`null` value is refused here (the runner
    reads it as absent): declare it or omit it."""
    if "restore" not in doc:
        return []
    r = doc["restore"]
    if not isinstance(r, dict) or not isinstance(r.get("command"), str) or not r["command"].strip():
        return ["build-stages.json `restore` must be {\"command\": \"…\", \"timeoutMinutes\": N} — the restore the host runs "
                "once per key from the default branch's tip to warm its shared NuGet cache"]
    tmo = r.get("timeoutMinutes", RESTORE_DEFAULT_TIMEOUT_MINUTES)
    if isinstance(tmo, bool) or not isinstance(tmo, (int, float)) or not (0 < tmo <= MAX_TIMEOUT_MINUTES):
        return [f"build-stages.json `restore` timeoutMinutes {tmo!r} must be in (0, {MAX_TIMEOUT_MINUTES}]"]
    unknown = sorted(set(r) - {"command", "timeoutMinutes"})
    if unknown:
        return [f"build-stages.json `restore` carries {unknown} — only `command` and `timeoutMinutes` are read"]
    return []


def debt_problems(doc: dict) -> list[str]:
    """Rule 7: claim ⟺ owes nothing."""
    out: list[str] = []
    owes = doc.get("owes", [])
    if not isinstance(owes, list) or not all(isinstance(o, str) and o.strip() for o in owes):
        out.append("build-stages.json `owes` must be a list of non-empty strings — each one thing the stages still check less than Actions")
        owes = ["(unreadable)"]
    claims = doc.get("ciEquivalent")
    # `in (None, True, False)` would let 0 and 1 through (1 == True in Python), so the type is checked.
    if claims is not None and not isinstance(claims, bool):
        out.append(f"build-stages.json `ciEquivalent` is {claims!r} — true, false or absent")
    if claims is True and owes:
        out.append(f"build-stages.json claims \"ciEquivalent\": true while it owes {len(owes)} item(s) — a host green would "
                   f"check less than the Actions legs it replaces (first: {owes[0][:120]})")
    if claims is not True and not owes:
        out.append("build-stages.json owes nothing but does not claim \"ciEquivalent\": true — say what is still owed in `owes`, or claim it")
    return out


def _stages(doc: dict) -> list[dict]:
    return [s for s in doc.get("stages") or [] if isinstance(s, dict) and isinstance(s.get("name"), str)]


def _markers(name: str, cmd: str, needles: list[tuple[str, str]]) -> list[str]:
    return [f"stage `{name}` no longer carries `{n}` — {why}; the host would check less than Actions"
            for n, why in needles if n not in cmd]


def core_problems(doc: dict) -> list[str]:
    stages = _stages(doc)
    names = [s["name"] for s in stages]
    out: list[str] = []
    if "build" not in names:
        return ["core declares no `build` stage — its shards would run a pack no stage of this run built"]
    if names[0] != "build":
        out.append(f"core's `build` stage must run FIRST (declared order {names[:3]}…) — every shard reads the pack it writes")
    for s in stages:
        cmd = str(s.get("command") or "")
        n = s["name"]
        if n == "build":
            out += _markers(n, cmd, CORE_BUILD_PARITY)
            if "nuget" not in (s.get("needs") or []):
                out.append("stage `build` does not need `nuget` — without the host's prepared package cache bound, every package is a download")
            if "--source" in cmd:
                out.append("stage `build` passes --source — under the repository's packageSourceMapping a command-line source "
                           "maps no package, so every restore is NU1100 even from a complete cache; restore through "
                           "nuget.config with the prepared cache as NUGET_FALLBACK_PACKAGES")
            if s.get("kind") != "build":
                out.append("stage `build` must be kind `build`")
        elif n.startswith("test-shard-"):
            out += _markers(n, cmd, CORE_SHARD_PARITY)
            idx = n[len("test-shard-"):]
            if f"--shard {idx} " not in cmd + " ":
                out.append(f"stage `{n}` does not run shard {idx}")
        elif n == "doc-gate":
            out += _markers(n, cmd, CORE_DOC_GATE_PARITY)
    if not any(n.startswith("test-shard-") for n in names):
        out.append("core declares no `test-shard-<i>` stage — the suite would run nowhere on a host")
    if "doc-gate" not in names:
        out.append("core declares no `doc-gate` stage — the doc and samples gates would run nowhere on a host")
    restore = doc.get("restore")
    if not isinstance(restore, dict):
        out.append("core declares no `restore` — the host's shared NuGet cache is then filled only by trunk builds, which "
                   "never reach it, so every run restores its whole closure (MWPREP001)")
    else:
        rcmd = str(restore.get("command") or "")
        out += [f"`restore` no longer carries `{n}` — {why}; the warm cache would miss what the build reads"
                for n, why in CORE_RESTORE_PARITY if n not in rcmd]
        if "--source" in rcmd:
            out.append("`restore` passes --source — under the repository's packageSourceMapping a command-line source maps "
                       "no package (NU1100); restore through nuget.config")
    return out


# A `uses:` value, plain or quoted — quoting a workflow reference must not hide it.
_USES = r"""uses:[ \t]*['"]?Systemorph/MeshWeaver/\.github/workflows/"""


def lanes_called(workflow_text: str) -> list[str]:
    """The shared heavy lanes a satellite's ci.yml calls (`uses: …/<lane>@…`), in LANE_STAGE order."""
    called = set(re.findall(_USES + r"([A-Za-z0-9._-]+\.yml)@", workflow_text))
    return [lane for lane in LANE_STAGE if lane in called]


def _job_blocks(workflow_text: str) -> list[str]:
    """Every job block under the top-level `jobs:` mapping, whatever indentation the file uses:
    the indent of the first key under `jobs:` IS the job indent, a job runs to the next key at
    that indent, and the mapping ends at the next top-level key."""
    lines = workflow_text.splitlines(keepends=True)
    blocks: list[str] = []
    in_jobs, indent, current = False, None, None
    for line in lines:
        bare = line.strip()
        meaningful = bare and not bare.startswith("#")
        lead = len(line) - len(line.lstrip(" "))
        if not in_jobs:
            if re.match(r"^jobs:[ \t]*(?:#.*)?$", line.rstrip("\r\n")):
                in_jobs = True
            continue
        if meaningful and lead == 0:
            break  # the next top-level key ends `jobs:`
        if meaningful and indent is None:
            indent = lead
        if meaningful and lead == indent and re.match(r"^[ ]*['\"]?[A-Za-z0-9_-]+['\"]?:[ \t]*(?:#.*)?$", line.rstrip("\r\n")):
            if current is not None:
                blocks.append("".join(current))
            current = [line]
        elif current is not None:
            current.append(line)
    if current is not None:
        blocks.append("".join(current))
    return blocks


def _calling_jobs(workflow_text: str, lane: str) -> list[str]:
    """The WHOLE job blocks that call `lane` — independent of whether `with:` comes before or after
    `uses:`, which YAML allows either way."""
    calls = re.compile(r"^\s+" + _USES + re.escape(lane) + r"@\S+", re.M)
    return [b for b in _job_blocks(workflow_text) if calls.search(b)]


def _block_input(block: str, key: str) -> Optional[str]:
    """`with: <key>:` of one calling job: None when that job does not pass it, and the empty string
    when it passes it EMPTY (`key: ''`) — which a lane reads as "none", not as its default, so the
    two must stay apart."""
    v = re.search(rf"^\s+(['\"]?){re.escape(key)}\1:[ \t]*(.*)$", block, re.M)
    if not v:
        return None
    value = re.sub(r"(^|\s)#.*$", "", v.group(2)).strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
        value = value[1:-1].strip()
    return value


# A YAML alias (`with: *gate_inputs`, `- *job`), a merge key (`<<: *base`) or an anchor (`&name`) makes a
# mapping's real content live somewhere this line reader does not look — the inputs (or a whole second
# calling job) would read as absent. Valid YAML, so it is REFUSED by name rather than read as "none".
_YAML_INDIRECTION = re.compile(r"(?:^\s*['\"]?<<['\"]?\s*:)|(?:(?::|^\s*-)[ \t]+[*&][A-Za-z0-9_.-]+[ \t]*(?:#.*)?$)", re.M)


def _jobs_text(workflow_text: str) -> str:
    return "".join(_job_blocks(workflow_text))


def _option_problems(name: str, cmd: str, opt: str, value: str, why: str) -> list[str]:
    """`opt value` must be passed, and EVERY `opt` must carry exactly `value` — a complete argument, never a
    prefix (`--allow plugin-gate.allow.expanded` is another file) and never a second, different occurrence
    (argparse keeps the last)."""
    args = [a.strip("'\"") for a in re.findall(rf"{re.escape(opt)}(?:[ \t]+|=)(\S+?)(?=$|[\s;|&)])", cmd)]
    bare = len(re.findall(rf"{re.escape(opt)}(?![A-Za-z0-9_-])", cmd))
    if not args:
        return [f"stage `{name}` no longer carries `{opt} {value}` — {why}; the host would check less than Actions"]
    if bare != len(args) or any(a != value for a in args):
        return [f"stage `{name}` passes `{opt}` {sorted(set(args)) or '(unreadable)'} where Actions passes exactly `{value}` — "
                f"{why}; a different file would tolerate what Actions rejects"]
    return []


def node_repo_problems(doc: dict, workflow_text: Optional[str]) -> list[str]:
    if workflow_text is None:
        return [f"{CI_WORKFLOW} is absent — the node-repo profile derives the stages a repository owes from the lanes it calls"]
    stages = {s["name"]: s for s in _stages(doc)}
    out: list[str] = []
    lanes = lanes_called(workflow_text)
    indirect = _YAML_INDIRECTION.search(_jobs_text(workflow_text))
    if indirect:
        return [f"{CI_WORKFLOW} uses a YAML anchor, alias or merge key in its jobs (`{indirect.group(0).strip()}`) — this guard "
                "reads only literal job blocks, so an aliased `with:` (or caller) would hide the baselines the lane enforces; "
                "write the inputs out"]
    if not lanes:
        out.append(f"{CI_WORKFLOW} calls none of {sorted(LANE_STAGE)} — there is no Actions leg a host stage could replace")
    for lane in lanes:
        want = LANE_STAGE[lane]
        st = stages.get(want)
        if st is None:
            out.append(f"{CI_WORKFLOW} calls {lane} but build-stages.json declares no `{want}` stage — a host route would follow a stage that never runs")
            continue
        cmd = str(st.get("command") or "")
        if want == "compile-check":
            out += _markers(want, cmd, NODE_COMPILE_CHECK_PARITY)
            out += compile_refs_problems(want, cmd)
        else:
            # FAIL CLOSED: the inputs that set the parity are read from the ONE job that calls the
            # lane. A call no job block could be found for (an unreadable layout), or a lane called
            # from several jobs (one host stage cannot mirror two callers' inputs), is red — never
            # read as "no inputs", which would drop the baselines from what the stage owes.
            blocks = _calling_jobs(workflow_text, lane)
            if len(blocks) != 1:
                out.append(f"{CI_WORKFLOW} calls {lane} from {len(blocks)} job block(s) this guard could read — "
                           "exactly one is required, so the stage's parity is derived from the inputs Actions really passes")
                continue
            block = blocks[0]
            # FAIL CLOSED on a `with:` this line reader cannot see into: a flow mapping
            # (`with: {warning-baseline-file: x}`) is valid YAML, but reading it as "no inputs" would drop
            # the baselines the Actions lane enforces from what the stage owes.
            if re.search(r"^\s+['\"]?with['\"]?:[ \t]*[{\[]", block, re.M):
                out.append(f"{CI_WORKFLOW} passes {lane} its `with:` as a flow mapping — this guard reads only block-style "
                           "`with:` inputs, so write them one per line (a flow mapping would hide the baselines the lane enforces)")
                continue
            # Omitted ⇒ the lane's default; passed EMPTY ⇒ no allowlist at all, and then a stage
            # that passes one would tolerate failures the Actions leg fails on.
            allow = _block_input(block, "allow-file")
            allow = "plugin-gate.allow" if allow is None else allow
            needles = list(NODE_GATE_PARITY)
            options: list[tuple[str, str, str]] = []
            if allow:
                options.append(("--allow", allow, "the gate's known-debt ratchet (the lane's allow-file)"))
            elif re.search(r"--allow(?![A-Za-z0-9_-])", cmd):
                out.append(f"stage `{want}` passes `--allow` while {CI_WORKFLOW} passes the lane an EMPTY allow-file — "
                           "the host would tolerate failures the Actions leg fails on")
            baseline = _block_input(block, "warning-baseline-file")
            if baseline:
                options.append(("--warning-baseline", baseline, "the in-mesh warning ratchet ci.yml passes the lane"))
            dynamic = _block_input(block, "dynamic-baseline-file")
            if dynamic:
                options.append(("--dynamic-baseline", dynamic, "the dynamic-edge ratchet ci.yml passes the lane"))
            out += _markers(want, cmd, needles)
            for opt, value, why in options:
                out += _option_problems(want, cmd, opt, value, why)
            out += gate_shard_problems(want, cmd)
            out += fail_fast_problems(want, cmd)
        if HARD_CODED_HOST.search(cmd):
            out.append(f"stage `{want}` hard-codes a `/host/…` path — the runner binds only $MW_BUILD_GATE_HOST / $MeshWeaverRefs into the sandbox")
    return out


REFS_ARG = re.compile(r"--refs[ =]+[\"']?\$\{?([A-Za-z_][A-Za-z0-9_]*)")
COMPILE_REFS_VAR = "MW_BUILD_COMPILE_REFS"


def compile_refs_problems(name: str, cmd: str) -> list[str]:
    """The marker alone is not enough: the variable must be what `--refs` actually receives — directly, or through a
    shell variable assigned from it (`refs="${MW_BUILD_COMPILE_REFS:?…}"; … --refs "$refs"`). Every `--refs` must."""
    args = REFS_ARG.findall(cmd)
    # compile-check's argparse keeps the LAST `--refs`, so EVERY occurrence must be one this rule can read: a second
    # `--refs /literal` (or `--refs=$(…)`) that REFS_ARG does not match would otherwise win unvalidated.
    every = re.findall(r"--refs(?![A-Za-z0-9_-])", cmd)
    if len(every) != len(args):
        return [f"stage `{name}` passes `--refs` {len(every)} time(s) but only {len(args)} receive a variable — compile-check "
                f"reads the LAST one, so each must be `${COMPILE_REFS_VAR}` (or a variable assigned from it)"]
    if not args:
        return [f"stage `{name}` passes no `--refs \"$VAR\"` — compile-check's reference set must be the host's prepared "
                f"`${COMPILE_REFS_VAR}`"]
    out = []
    for var in args:
        if var == COMPILE_REFS_VAR:
            continue
        if re.search(rf"(^|[;\s]){re.escape(var)}=[\"']?\$\{{{COMPILE_REFS_VAR}[:}}]", cmd):
            continue
        out.append(f"stage `{name}` passes `--refs ${var}`, which is not `${COMPILE_REFS_VAR}` (nor assigned from it) — "
                   "naming the prepared set elsewhere in the command does not make compile-check read it")
    return out


SHARD_ARG = re.compile(r"--shard(?![A-Za-z0-9_-])[\"']?(?:[ \t]+|=)[\"']?([^\s\"';|&)]*)")
MERGE_SHARDS = re.compile(r"gate-shard-merge\.py\b[^;|&]*--shards(?:[ \t]+|=)[\"']?(\d+)\b")


def gate_shard_problems(name: str, cmd: str) -> list[str]:
    """A gate stage that narrows the tester with `--shard k/n` gates ONE slice. The host route skips the Actions
    lane's gate-shard-merge.py, so nothing later notices the missing slices — one slice would stand in for the
    whole gate (#7456). Narrowing is green only when the stage runs EVERY slice 1..n with literal arguments AND
    folds them with gate-shard-merge.py `--shards n`; a slice this guard cannot read (a variable, a loop) is red."""
    slices = SHARD_ARG.findall(cmd)
    # FAIL CLOSED, as for `--refs`: every `--shard` token must be one SHARD_ARG reads. A form it cannot (an option
    # split across quotes it does not model, a line continuation) would otherwise narrow the gate unseen.
    every = re.findall(r"--shard(?![A-Za-z0-9_-])", cmd)
    if len(every) != len(slices):
        return [f"stage `{name}` passes `--shard` {len(every)} time(s) but only {len(slices)} read as a slice — a host "
                "stage must run the WHOLE gate (or every literal slice and fold them), and a slice this guard cannot "
                "read could be narrowing it"]
    if not slices:
        return []
    parsed = [re.fullmatch(r"(\d+)/(\d+)", s) for s in slices]
    if not all(parsed):
        return [f"stage `{name}` narrows the gate with `--shard` {sorted(set(slices))}, which this guard cannot read as "
                "literal `k/n` slices — a host stage must run the WHOLE gate (or every literal slice and fold them), "
                "since the host route runs no gate-shard-merge.py of its own"]
    totals = {int(m.group(2)) for m in parsed}
    if len(totals) != 1:
        return [f"stage `{name}` runs slices of different shard counts {sorted(totals)} — one gate has one count"]
    total = totals.pop()
    ran = {int(m.group(1)) for m in parsed}
    missing = sorted(set(range(1, total + 1)) - ran)
    out = []
    if missing or not ran <= set(range(1, total + 1)):
        out.append(f"stage `{name}` runs slice(s) {sorted(ran)} of {total} with `--shard` — slice(s) {missing or 'out of range'} "
                   "never run, so a host green would gate part of the tree the Actions lane gates whole")
    merged = MERGE_SHARDS.findall(cmd)
    if not merged or any(int(n) != total for n in merged):
        out.append(f"stage `{name}` narrows the gate with `--shard k/{total}` but does not fold the slices with "
                   f"gate-shard-merge.py `--shards {total}` — nothing would notice a slice that never produced a verdict")
    return out


def _statements(cmd: str) -> list[str]:
    return [s.strip() for s in re.split(r";|\n", cmd) if s.strip()]


def _set_change(stmt: str) -> dict[str, bool]:
    """What a `set` statement does to errexit / pipefail: {'e': True} for `set -e`, {'pipefail': False} for
    `set +o pipefail`, and so on. Anything else changes nothing."""
    m = re.fullmatch(r"set\s+(.*)", stmt)
    if not m:
        return {}
    out: dict[str, bool] = {}
    toks = m.group(1).split()
    i = 0
    while i < len(toks):
        t = toks[i]
        if t[:1] in "-+" and t not in ("-", "--"):
            on = t[0] == "-"
            if t[1:] == "o":
                name = toks[i + 1] if i + 1 < len(toks) else ""
                if name == "errexit":
                    out["e"] = on
                elif name == "pipefail":
                    out["pipefail"] = on
                i += 2
                continue
            letters = t[1:]
            if "e" in letters:
                out["e"] = on
            if "o" in letters and i + 1 < len(toks):
                # `-euo pipefail`: the `o` takes the next word as its option name
                name = toks[i + 1]
                if name == "errexit":
                    out["e"] = on
                elif name == "pipefail":
                    out["pipefail"] = on
                i += 2
                continue
        i += 1
    return out


def _states(stmts: list[str]) -> list[dict[str, bool]]:
    """The errexit / pipefail state IN FORCE when each statement runs (before it, so a `set` affects what follows)."""
    state = {"e": False, "pipefail": False}
    out = []
    for st in stmts:
        out.append(dict(state))
        state.update(_set_change(st))
    return out


def _captured_after(stmts: list[str], i: int, source: str) -> Optional[str]:
    """The variable statement i+1 assigns from `source` (`$?` or `${PIPESTATUS[0]}`), if any."""
    if i + 1 >= len(stmts):
        return None
    m = re.fullmatch(rf"([A-Za-z_][A-Za-z0-9_]*)=\"?{re.escape(source)}\"?", stmts[i + 1])
    return m.group(1) if m else None


def _exited_before_reassigned(stmts: list[str], at: int, var: str) -> bool:
    """A statement after `at` exits with `$var` before any statement assigns `var` again — so a later capture into
    the same name cannot overwrite a failure nobody looked at."""
    for st in stmts[at + 1:]:
        if re.search(rf"exit\s+\"?\$\{{?{re.escape(var)}\b", st):
            return True
        if re.match(rf"(?:then\s+|do\s+|else\s+)?{re.escape(var)}=", st):
            return False
    return False


_PIPED = re.compile(r"(?<![|])\|(?![|])")


def fail_fast_problems(name: str, cmd: str) -> list[str]:
    """Every check the gate stage runs must reach the stage's exit code. `bash -c` adds no failure propagation, so
    compile → gate → ratchet written as three plain statements exits with the RATCHET's code: a compile that exits 1
    followed by a green gate and ratchet is a green stage (#7456). The guard reads the stage IN ORDER and checks EVERY
    invocation of each check (the compile, each gate run, the shard merger, the Tests-area ratchet):

      * under `set -e` in force at that statement (with `-o pipefail` too when it is piped) it propagates — a `set`
        written after the checks, or a later `set +e` / `set +o pipefail`, is not in force there and does not count;
      * otherwise it needs `|| exit` (only on an unpiped check, or under pipefail — `gate | tee || exit` tests TEE),
        or its status captured right after it (`${PIPESTATUS[0]}` when piped, else `$?`) into a variable the stage
        exits with before anything assigns that variable again; the stage's LAST statement needs no capture.
    """
    stmts = _statements(cmd)
    states = _states(stmts)
    out: list[str] = []

    def check(marker: str, what: str) -> None:
        idx = [i for i, s in enumerate(stmts) if re.search(marker, s)]
        # a missing check is the parity markers' finding, not this one's
        for i in idx:
            s, st = stmts[i], states[i]
            piped = _PIPED.search(s) is not None
            if st["e"] and (not piped or st["pipefail"]):
                continue
            if re.search(r"\|\|\s*exit\b", s) and (not piped or st["pipefail"]):
                continue
            if piped:
                var = _captured_after(stmts, i, "${PIPESTATUS[0]}")
            else:
                var = _captured_after(stmts, i, "$?")
                if var is None and i == len(stmts) - 1:
                    continue
            if var is None or not _exited_before_reassigned(stmts, i + 1, var):
                how = "`${PIPESTATUS[0]}` (it is piped)" if piped else "`$?`"
                out.append(f"stage `{name}` does not propagate the {what}'s exit code (statement {i + 1}) — `set -e`"
                           f"{' with `-o pipefail`' if piped else ''} is not in force there, it has no "
                           f"{'pipefail-backed ' if piped else ''}`|| exit`, and it is not captured from {how} into a "
                           "variable the stage exits with; a failing earlier check followed by green later ones would exit 0")
                return

    check(r"[\"'}]\s+compile\s", "compile step")  # the tester invocation, never an `echo "… the compile …"`
    check(r"--seed(?![A-Za-z0-9_-])", "gate run")
    check(r"gate-shard-merge\.py", "shard merge")
    check(r"check-test-suites\.py --gate-log", "Tests-area ratchet")
    return out


def problems(text: Optional[str], profile: str, workflow_text: Optional[str] = None, require: bool = False) -> tuple[list[str], str]:
    """(problems, a one-line verdict)."""
    if text is None:
        if require:
            return [f"{STAGES_FILE} is absent — this repository must declare its build-host stages and what they still owe"], ""
        return [], (f"{STAGES_FILE} is absent: NOT MIGRATED — build-executor.py answers `actions`, so every heavy leg "
                    f"runs on GitHub Actions as before (nothing is skipped)")
    try:
        doc = json.loads(text)
    except ValueError as e:
        return [f"{STAGES_FILE} is not JSON: {e}"], ""
    found = schema_problems(doc)
    if found:
        return found, ""
    found = debt_problems(doc)
    if profile == "core":
        found += core_problems(doc)
    elif profile == "node-repo":
        found += node_repo_problems(doc, workflow_text)
    else:
        raise ValueError(f"unknown profile {profile!r}")
    owes = doc.get("owes") or []
    verdict = ("claims CI equivalence and owes nothing — a run may be routed to a host" if doc.get("ciEquivalent") is True
               else f"owes {len(owes)} item(s) — runs stay on GitHub Actions until each is paid")
    return found, f"{STAGES_FILE}: {len(_stages(doc))} stage(s); {verdict}"


# ───────────────────────────────── self-test ─────────────────────────────────

def _core_doc(total: int = 2) -> dict:
    build = ("set -euo pipefail; fb=\"${NUGET_FALLBACK_PACKAGES:?}\"; dotnet build MeshWeaver.slnx -c Release -p:CIRun=true "
             "-warnaserror; echo prepared package cache did not hold; echo are not in MeshWeaver.slnx; "
             "python3 deploy/core-tests/core-tests.py pack --repo . --out \"${MW_BUILD_OUTPUTS:?}/core-tests\" --sha \"${MW_BUILD_SHA:?}\"; "
             "python3 .github/scripts/check-licenses.py --stage-evidence e; python3 .github/scripts/host-build-outputs.py stage "
             "--repo . --sha \"${MW_BUILD_SHA:?}\" --evidence e --artifacts \"${MW_BUILD_ARTIFACTS:?}\"")
    roots = "--root \"${MW_BUILD_OUTPUTS:?}/core-tests\" --root /core-tests --require-sha \"${MW_BUILD_SHA:?}\""
    stages = [{"name": "build", "kind": "build", "command": build, "timeoutMinutes": 25, "needs": ["nuget"]}]
    stages += [{"name": f"test-shard-{i}", "kind": "test", "command": f"python3 deploy/core-tests/core-tests.py run --shard {i} --total {total} {roots} --repo .",
                "timeoutMinutes": 20} for i in range(total)]
    stages.append({"name": "doc-gate", "kind": "test", "command": f"python3 deploy/core-tests/core-tests.py doc-gate {roots} --repo .", "timeoutMinutes": 25})
    return {"owes": ["one thing"], "stages": stages,
            "restore": {"command": "dotnet restore MeshWeaver.slnx -p:Configuration=Release -p:CIRun=true"
                                   " && dotnet restore .github/nuget-warm/ResolverFixturePackages.csproj", "timeoutMinutes": 20}}


NODE_CI = """jobs:
  compile-check:
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-compile-check.yml@main
    with:
      executor: actions
  test-repos:
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@main
    with:
      executor: actions
  tests-ratchet:
    runs-on: x
"""


# The gate as this fixture once wrote it: three plain statements, so `bash -c` exits with the RATCHET's code and a
# failed compile reads green (#7456). Kept as the executing negative control.
_UNSAFE_GATE = ("t=\"${MW_BUILD_GATE_HOST:?}/mw-plugin-test.dll\"; d=\"${MW_BUILD_PORTAL_DOTNET:?}\"; a=\"${MeshWeaverRefs:?}\"; \"$d\" \"$t\" compile . "
                "--allow plugin-gate.allow --output b; \"$d\" \"$t\" . --allow plugin-gate.allow --seed b | tee \"${MW_BUILD_ARTIFACTS:?}/gate-log/gate.log\"; "
                "python3 scripts/check-test-suites.py --gate-log x")

# Every satellite's gate stage today (measured on each node repo's .github/build-stages.json): `set -uo pipefail`
# with each exit code captured by hand. It must stay green.
_SATELLITE_GATE = ('set -uo pipefail; tester="${MW_BUILD_GATE_HOST:?no tester}/mw-plugin-test.dll"; dn="${MW_BUILD_PORTAL_DOTNET:?no dotnet}"; '
                   'app="${MeshWeaverRefs:?no app}"; shared="$(dirname "$dn")/shared"; [ -f "$tester" ] || { echo "::error::no tester at $tester"; exit 2; }; '
                   'bake="${MW_BUILD_OUTPUTS:?}/gate-bake"; mkdir -p "$bake"; "$dn" "$tester" compile . --app "$app" --shared-frameworks "$shared" '
                   '--allow plugin-gate.allow --output "$bake" || exit $?; [ -s "$bake/framework-mvid.txt" ] || { echo "::error::the compile wrote no bake identity"; exit 1; }; '
                   'logdir="${MW_BUILD_ARTIFACTS:?}/gate-log"; mkdir -p "$logdir"; log="$logdir/gate.log"; '
                   '"$dn" "$tester" . --app "$app" --allow plugin-gate.allow --seed "$bake" 2>&1 | tee "$log"; gate=${PIPESTATUS[0]}; '
                   'python3 scripts/check-test-suites.py --gate-log "$log"; ratchet=$?; if [ "$gate" -ne 0 ]; then exit "$gate"; fi; exit "$ratchet"')


def _run_stage(command: str, compile_exit: int, fail_on: str = "") -> int:
    """EXECUTE a gate stage against stub tools: the tester's `compile` exits `compile_exit`, a tester invocation whose
    arguments contain `fail_on` exits 1, every other check is green."""
    import subprocess
    import tempfile
    with tempfile.TemporaryDirectory() as root:
        stubs = os.path.join(root, "bin")
        host = os.path.join(root, "host")
        for d in (stubs, host, os.path.join(root, "shared"), os.path.join(root, "artifacts", "gate-log")):
            os.makedirs(d)
        open(os.path.join(host, "mw-plugin-test.dll"), "w").close()
        dn = os.path.join(root, "dotnet")
        with open(dn, "w") as f:
            # A green compile writes its bake identity under --output, as the tester does.
            fail = f'case "$*" in *"{fail_on}"*) exit 1;; esac\n' if fail_on else ""
            f.write(f'#!/bin/sh\n{fail}if [ "$2" = compile ]; then\n  [ {compile_exit} -ne 0 ] && exit {compile_exit}\n'
                    '  prev=; for a in "$@"; do if [ "$prev" = --output ]; then mkdir -p "$a"; echo m > "$a/framework-mvid.txt"; fi; prev=$a; done\n'
                    'fi\nexit 0\n')
        py = os.path.join(stubs, "python3")
        with open(py, "w") as f:
            f.write("#!/bin/sh\nexit 0\n")
        for path in (dn, py):
            os.chmod(path, 0o755)
        env = dict(os.environ, PATH=stubs + os.pathsep + os.environ.get("PATH", ""), MW_BUILD_GATE_HOST=host,
                   MW_BUILD_PORTAL_DOTNET=dn, MeshWeaverRefs=root, MW_BUILD_OUTPUTS=os.path.join(root, "out"),
                   MW_BUILD_ARTIFACTS=os.path.join(root, "artifacts"))
        # The runner's shape: `bash -c <command>`, which adds no failure propagation of its own.
        return subprocess.run(["bash", "-c", command], cwd=root, env=env, stdout=subprocess.DEVNULL,
                              stderr=subprocess.DEVNULL).returncode


def _node_doc() -> dict:
    cc = "python3 \"${MW_PLATFORM_SCRIPTS:?}/compile-check.py\" --refs \"${MW_BUILD_COMPILE_REFS:?}\" # MW_ALLOW_FILE=scripts/compile-check.allow"
    gate = "set -euo pipefail; " + _UNSAFE_GATE
    return {"owes": ["one thing"], "stages": [{"name": "compile-check", "kind": "build", "command": cc, "timeoutMinutes": 40},
                                              {"name": "gate", "kind": "test", "command": gate, "timeoutMinutes": 45}]}


def _self_test() -> int:
    fails: list[str] = []

    def expect(cond: bool, what: str) -> None:
        print(("  ok   " if cond else "  FAIL ") + what)
        if not cond:
            fails.append(what)

    def red(doc: object, profile: str, needle: str, what: str, wf: Optional[str] = NODE_CI) -> None:
        found, _ = problems(json.dumps(doc), profile, wf)
        expect(any(needle in p for p in found), f"RED: {what}" + ("" if found else " (no problem reported at all)"))

    def mutate(base: dict, fn) -> dict:
        d = json.loads(json.dumps(base))
        fn(d)
        return d

    core, node = _core_doc(), _node_doc()
    print("positive controls:")
    expect(problems(json.dumps(core), "core")[0] == [], "core: a declaration with every parity marker and an open debt is green")
    expect(problems(json.dumps(node), "node-repo", NODE_CI)[0] == [], "node-repo: compile-check + gate with every marker and an open debt is green")
    expect(problems(json.dumps(mutate(node, lambda d: d.update(owes=[], ciEquivalent=True))), "node-repo", NODE_CI)[0] == [],
           "a debt-free CLAIM is green (the end state is reachable)")
    found, verdict = problems(None, "node-repo", NODE_CI)
    expect(found == [] and "NOT MIGRATED" in verdict, "an absent declaration without --require is said, not red (Actions keeps every leg)")
    gate_only = NODE_CI.replace("node-repo-compile-check.yml", "node-repo-validate.yml")
    expect(problems(json.dumps(mutate(node, lambda d: d["stages"].pop(0))), "node-repo", gate_only)[0] == [],
           "a repository that calls only the gate lane owes only a `gate` stage")
    print("negative controls — rule 7:")
    red(mutate(node, lambda d: d.update(ciEquivalent=True)), "node-repo", "while it owes", "ciEquivalent with an open debt")
    red(mutate(core, lambda d: d.update(ciEquivalent=True)), "core", "while it owes", "core: ciEquivalent with an open debt")
    red(mutate(node, lambda d: d.update(owes=[])), "node-repo", "owes nothing", "a debt-free declaration that does not claim")
    red(mutate(node, lambda d: d.pop("owes")), "node-repo", "owes nothing", "a declaration with no `owes` at all and no claim")
    red(mutate(node, lambda d: d.update(owes="later")), "node-repo", "`owes` must be", "a non-list owes")
    red(mutate(node, lambda d: d.update(owes=["  "])), "node-repo", "`owes` must be", "a blank owes entry")
    red(mutate(node, lambda d: d.update(ciEquivalent="yes")), "node-repo", "`ciEquivalent` is", "a string ciEquivalent")
    print("negative controls — schema:")
    red(mutate(node, lambda d: d.update(stages=[])), "node-repo", "non-empty list", "no stages")
    red(mutate(node, lambda d: d["stages"][0].update(timeoutMinutes=90)), "node-repo", "timeoutMinutes", "a stage cap past 45 minutes")
    red(mutate(node, lambda d: d["stages"][0].update(kind="deploy")), "node-repo", "kind", "an unknown kind")
    red(mutate(node, lambda d: d["stages"][0].update(needs=["secrets"])), "node-repo", "needs", "an unknown need")
    red(mutate(node, lambda d: d["stages"].append(dict(d["stages"][0]))), "node-repo", "declared twice", "a stage declared twice")
    red(mutate(node, lambda d: d["stages"][0].update(name="Build/x")), "node-repo", "path segment", "a stage name that is not one segment")
    print("parallel (fan-out on one host):")
    expect(problems(json.dumps(mutate(node, lambda d: d.update(parallel=True))), "node-repo", NODE_CI)[0] == [],
           "node-repo: `parallel: true` is green")
    red(mutate(node, lambda d: d.update(parallel="yes")), "node-repo", "`parallel`", "a `parallel` that is a string")
    red(mutate(node, lambda d: d.update(parallel=1)), "node-repo", "`parallel`", "a `parallel` that is a number")
    print("restore (the host's default-branch NuGet warm, MeshWeaver.Deploy#68):")
    expect(problems(json.dumps(mutate(node, lambda d: d.update(restore={"command": "dotnet restore x.slnx", "timeoutMinutes": 10}))),
                    "node-repo", NODE_CI)[0] == [], "node-repo: a well-formed `restore` is green")
    expect(problems(json.dumps(mutate(node, lambda d: d.update(restore={"command": "dotnet restore x.slnx"}))), "node-repo", NODE_CI)[0] == [],
           "node-repo: a `restore` without timeoutMinutes takes the runner's default and is green")
    red(mutate(node, lambda d: d.update(restore="dotnet restore")), "node-repo", "`restore` must be", "a `restore` that is a bare string")
    red(mutate(node, lambda d: d.update(restore=None)), "node-repo", "`restore` must be", "a `restore` that is null")
    red(mutate(node, lambda d: d.update(restore={"command": "  "})), "node-repo", "`restore` must be", "a `restore` with a blank command")
    red(mutate(node, lambda d: d.update(restore={"timeoutMinutes": 10})), "node-repo", "`restore` must be", "a `restore` with no command")
    red(mutate(node, lambda d: d.update(restore={"command": "dotnet restore", "timeoutMinutes": 90})), "node-repo",
        "`restore` timeoutMinutes", "a `restore` cap past 45 minutes")
    red(mutate(node, lambda d: d.update(restore={"command": "dotnet restore", "timeoutMinutes": 0})), "node-repo",
        "`restore` timeoutMinutes", "a zero `restore` cap")
    red(mutate(node, lambda d: d.update(restore={"command": "dotnet restore", "timeoutMinutes": True})), "node-repo",
        "`restore` timeoutMinutes", "a boolean `restore` cap (True == 1)")
    red(mutate(node, lambda d: d.update(restore={"command": "dotnet restore", "source": "https://x"})), "node-repo",
        "only `command` and `timeoutMinutes`", "a `restore` carrying a key the runner never reads")
    red(mutate(core, lambda d: d.pop("restore")), "core", "core declares no `restore`", "core without its default-branch restore")
    for needle, _ in CORE_RESTORE_PARITY:
        red(mutate(core, lambda d, n=needle: d["restore"].update(command=d["restore"]["command"].replace(n, "X"))), "core",
            f"`{needle}`", f"core's restore drops `{needle}`")
    red(mutate(core, lambda d: d["restore"].update(command=d["restore"]["command"] + " --source \"$NUGET_FALLBACK_PACKAGES\"")),
        "core", "`restore` passes --source", "core's restore from a --source (NU1100 under packageSourceMapping)")
    expect(problems("{", "core")[0] != [], "RED: a file that is not JSON")
    expect(problems(None, "core", require=True)[0] != [], "RED: an absent declaration under --require")
    print("negative controls — core parity:")
    for needle, _ in CORE_BUILD_PARITY:
        red(mutate(core, lambda d, n=needle: d["stages"][0].update(command=d["stages"][0]["command"].replace(n, "X"))), "core",
            f"`{needle}`", f"build drops `{needle}`")
    red(mutate(core, lambda d: d["stages"][0].update(needs=[])), "core", "does not need `nuget`", "build without the prepared package cache")
    red(mutate(core, lambda d: d["stages"][0].update(command=d["stages"][0]["command"] + " --source \"$NUGET_FALLBACK_PACKAGES\"")),
        "core", "passes --source", "a build that restores from a --source (NU1100 under packageSourceMapping)")
    for needle, _ in CORE_SHARD_PARITY:
        red(mutate(core, lambda d, n=needle: d["stages"][1].update(command=d["stages"][1]["command"].replace(n, "X"))), "core",
            f"`{needle}`", f"a shard drops `{needle}`")
    for needle, _ in CORE_DOC_GATE_PARITY:
        red(mutate(core, lambda d, n=needle: d["stages"][-1].update(command=d["stages"][-1]["command"].replace(n, "X"))), "core",
            f"`{needle}`", f"doc-gate drops `{needle}`")
    red(mutate(core, lambda d: d["stages"][2].update(command=d["stages"][2]["command"].replace("--shard 1 ", "--shard 0 "))), "core",
        "does not run shard 1", "test-shard-1 running shard 0")
    red(mutate(core, lambda d: d["stages"].insert(0, d["stages"].pop(1))), "core", "FIRST", "a shard before the build")
    red(mutate(core, lambda d: d.update(stages=d["stages"][1:])), "core", "no `build` stage", "no build stage")
    red(mutate(core, lambda d: d.update(stages=[d["stages"][0], d["stages"][-1]])), "core", "no `test-shard-", "no shard stage")
    red(mutate(core, lambda d: d.update(stages=d["stages"][:-1])), "core", "no `doc-gate`", "no doc-gate stage")
    print("negative controls — node-repo parity:")
    for needle, _ in NODE_COMPILE_CHECK_PARITY:
        red(mutate(node, lambda d, n=needle: d["stages"][0].update(command=d["stages"][0]["command"].replace(n, "X"))), "node-repo",
            f"`{needle}`", f"compile-check drops `{needle}`")
    for needle, _ in NODE_GATE_PARITY + [("--allow plugin-gate.allow", "")]:
        red(mutate(node, lambda d, n=needle: d["stages"][1].update(command=d["stages"][1]["command"].replace(n, "X"))), "node-repo",
            f"`{needle}`", f"gate drops `{needle}`")
    red(mutate(node, lambda d: d["stages"][0].update(command='test -n "${MW_BUILD_COMPILE_REFS:?}"; python3 "${MW_PLATFORM_SCRIPTS:?}/compile-check.py" '
                                                               '--refs "${MeshWeaverRefs:?}" # MW_ALLOW_FILE=scripts/compile-check.allow')),
        "node-repo", "which is not `$MW_BUILD_COMPILE_REFS`", "the prepared set is named but --refs receives the app alone")
    red(mutate(node, lambda d: d["stages"][0].update(command='r="${MeshWeaverRefs:?}"; x="${MW_BUILD_COMPILE_REFS:?}"; python3 "${MW_PLATFORM_SCRIPTS:?}/compile-check.py" '
                                                               '--refs "$r" # MW_ALLOW_FILE=scripts/compile-check.allow')),
        "node-repo", "`--refs $r`", "--refs receives a variable assigned from the app alone")
    expect(problems(json.dumps(mutate(node, lambda d: d["stages"][0].update(
        command='set -e; refs="${MW_BUILD_COMPILE_REFS:?no set}"; python3 "${MW_PLATFORM_SCRIPTS:?}/compile-check.py" --refs "$refs" '
                '# MW_ALLOW_FILE=scripts/compile-check.allow'))), "node-repo", NODE_CI)[0] == [],
           "--refs through a variable assigned from $MW_BUILD_COMPILE_REFS is green (the satellites' shape)")
    red(mutate(node, lambda d: d["stages"][0].update(command=d["stages"][0]["command"].replace(
            '--refs "${MW_BUILD_COMPILE_REFS:?}"', '--refs "${MW_BUILD_COMPILE_REFS:?}" --refs /previous-complete-refs'))),
        "node-repo", "compile-check reads the LAST one", "a second, literal --refs after the prepared set (argparse keeps the last)")
    red(mutate(node, lambda d: d["stages"][1].update(command=d["stages"][1]["command"] + "; dotnet /host/mw-plugin-test.dll .")), "node-repo",
        "hard-codes", "a hard-coded /host/ tester")
    red(mutate(node, lambda d: d["stages"].pop(1)), "node-repo", "no `gate` stage", "ci.yml calls the gate lane, no gate stage")
    red(mutate(node, lambda d: d["stages"].pop(0)), "node-repo", "no `compile-check` stage", "ci.yml calls the compile-check lane, no compile-check stage")
    wb = NODE_CI.replace("      executor: actions\n  tests-ratchet", "      executor: actions\n      warning-baseline-file: gate-warnings.allow\n  tests-ratchet")
    red(node, "node-repo", "--warning-baseline gate-warnings.allow", "ci.yml passes a warning baseline the gate stage drops", wb)
    af = NODE_CI.replace("      executor: actions\n  tests-ratchet", "      executor: actions\n      allow-file: other-gate.allow\n  tests-ratchet")
    red(node, "node-repo", "where Actions passes exactly `other-gate.allow`", "ci.yml passes another allow-file than the stage's", af)
    # `with:` BEFORE `uses:` is the same job — the inputs must still be read.
    wb_first = NODE_CI.replace(
        "  test-repos:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@main\n    with:\n      executor: actions\n",
        "  test-repos:\n    with:\n      executor: actions\n      warning-baseline-file: gate-warnings.allow\n"
        "    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@main\n")
    expect(wb_first != NODE_CI, "self-test fixture: the with-before-uses ci.yml was built")
    red(mutate(node, lambda d: d.update(owes=[], ciEquivalent=True)), "node-repo", "--warning-baseline gate-warnings.allow",
        "a debt-free claim whose gate drops the warning baseline ci.yml passes with `with:` BEFORE `uses:`", wb_first)
    db = NODE_CI.replace("      executor: actions\n  tests-ratchet", "      executor: actions\n      dynamic-baseline-file: dynamic.allow\n  tests-ratchet")
    red(mutate(node, lambda d: d.update(owes=[], ciEquivalent=True)), "node-repo", "--dynamic-baseline dynamic.allow",
        "a debt-free claim whose gate drops the dynamic baseline ci.yml passes", db)
    expect(problems(json.dumps(mutate(node, lambda d: d["stages"][1].update(command=d["stages"][1]["command"] + " --dynamic-baseline dynamic.allow"))),
                    "node-repo", db)[0] == [], "a gate that passes the dynamic baseline ci.yml passes is green")
    red(mutate(node, lambda d: d.update(ciEquivalent=1)), "node-repo", "`ciEquivalent` is", "a numeric ciEquivalent 1 (1 == True)")
    red(mutate(node, lambda d: d.update(ciEquivalent=0)), "node-repo", "`ciEquivalent` is", "a numeric ciEquivalent 0 (0 == False)")
    empty_allow = NODE_CI.replace("      executor: actions\n  tests-ratchet", "      executor: actions\n      allow-file: ''\n  tests-ratchet")
    no_allow = mutate(node, lambda d: d["stages"][1].update(command=d["stages"][1]["command"].replace("--allow plugin-gate.allow ", "")))
    expect("--allow" not in no_allow["stages"][1]["command"], "self-test fixture: the gate without an allowlist was built")
    expect(problems(json.dumps(no_allow), "node-repo", empty_allow)[0] == [],
           "an EMPTY allow-file in ci.yml and a gate stage with no --allow is green")
    red(node, "node-repo", "EMPTY allow-file", "a gate that passes --allow while ci.yml passes an EMPTY allow-file", empty_allow)
    red(no_allow, "node-repo", "--allow plugin-gate.allow", "an OMITTED allow-file still owes the lane's default allowlist")
    # Layout must not hide a caller's inputs: four-space indentation, a quoted `uses:`, a second caller.
    wb4 = "\n".join(("  " + l) if l.startswith("  ") else l for l in wb.split("\n"))
    expect(wb4 != wb and "\n    test-repos:\n" in wb4, "self-test fixture: the four-space ci.yml was built")
    red(mutate(node, lambda d: d.update(owes=[], ciEquivalent=True)), "node-repo", "--warning-baseline gate-warnings.allow",
        "a debt-free claim whose gate drops the warning baseline a FOUR-SPACE-indented ci.yml passes", wb4)
    expect(problems(json.dumps(node), "node-repo", "\n".join(("  " + l) if l.startswith("  ") else l for l in NODE_CI.split("\n")))[0] == [],
           "a FOUR-SPACE-indented ci.yml with an honest declaration is green")
    quoted = NODE_CI.replace("uses: Systemorph/MeshWeaver/.github/workflows/node-repo-compile-check.yml@main",
                             "uses: 'Systemorph/MeshWeaver/.github/workflows/node-repo-compile-check.yml@main'")
    expect(quoted != NODE_CI, "self-test fixture: the quoted-uses ci.yml was built")
    red(mutate(node, lambda d: (d.update(owes=[], ciEquivalent=True), d["stages"].pop(0))), "node-repo", "no `compile-check` stage",
        "a debt-free claim with only `gate` while a QUOTED `uses:` still calls compile-check", quoted)
    second = wb.replace("  tests-ratchet:", "  test-repos-strict:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@main\n"
                                            "    with:\n      warning-baseline-file: strict-warnings.allow\n  tests-ratchet:")
    red(mutate(node, lambda d: d.update(owes=[], ciEquivalent=True)), "node-repo", "from 2 job block(s)",
        "a lane called from a SECOND job whose inputs a single stage cannot mirror", second)
    # A flow-style `with:` is valid YAML the line reader cannot see into — red, never "no inputs".
    flow = NODE_CI.replace(
        "  test-repos:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@main\n    with:\n      executor: actions\n",
        "  test-repos:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@main\n"
        "    with: {executor: actions, warning-baseline-file: gate-warnings.allow, dynamic-baseline-file: dynamic.allow}\n")
    expect(flow != NODE_CI, "self-test fixture: the flow-style-with ci.yml was built")
    red(mutate(node, lambda d: d.update(owes=[], ciEquivalent=True)), "node-repo", "flow mapping",
        "a debt-free claim whose ci.yml passes the gate lane a FLOW-STYLE `with:`", flow)
    # A quoted key is the same input.
    qk = NODE_CI.replace("      executor: actions\n  tests-ratchet", "      executor: actions\n      \"warning-baseline-file\": gate-warnings.allow\n  tests-ratchet")
    expect(qk != NODE_CI, "self-test fixture: the quoted-key ci.yml was built")
    red(mutate(node, lambda d: d.update(owes=[], ciEquivalent=True)), "node-repo", "--warning-baseline gate-warnings.allow",
        "a debt-free claim whose gate drops the warning baseline ci.yml passes under a QUOTED key", qk)
    # An alias / merge key hides the inputs from a line reader — refused, never read as "no inputs".
    alias = NODE_CI.replace(
        "  test-repos:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@main\n    with:\n      executor: actions\n",
        "  test-repos:\n    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@main\n    with: *gate_inputs\n")
    expect(alias != NODE_CI, "self-test fixture: the aliased-with ci.yml was built")
    red(mutate(node, lambda d: d.update(owes=[], ciEquivalent=True)), "node-repo", "anchor, alias or merge key",
        "a debt-free claim whose ci.yml passes the gate lane `with: *gate_inputs` (an alias carrying the warning baseline)", alias)
    merge = NODE_CI.replace("    with:\n      executor: actions\n  tests-ratchet",
                            "    with:\n      <<: *gate_inputs\n      executor: actions\n  tests-ratchet")
    expect(merge != NODE_CI, "self-test fixture: the merge-key ci.yml was built")
    red(mutate(node, lambda d: d.update(owes=[], ciEquivalent=True)), "node-repo", "anchor, alias or merge key",
        "a debt-free claim whose ci.yml merges the gate inputs in with `<<: *gate_inputs`", merge)
    # A complete argument, never a prefix: another file would tolerate what Actions rejects.
    for opt, wf_input, value in (("--allow", None, "plugin-gate.allow"),
                                 ("--warning-baseline", "warning-baseline-file", "gate-warnings.allow"),
                                 ("--dynamic-baseline", "dynamic-baseline-file", "dynamic.allow")):
        wf = NODE_CI if wf_input is None else NODE_CI.replace(
            "      executor: actions\n  tests-ratchet", f"      executor: actions\n      {wf_input}: {value}\n  tests-ratchet")
        honest = mutate(node, lambda d, o=opt, v=value: d.update(owes=[], ciEquivalent=True) or d["stages"][1].update(
            command=d["stages"][1]["command"] if o == "--allow" else d["stages"][1]["command"] + f" {o} {v}"))
        expect(problems(json.dumps(honest), "node-repo", wf)[0] == [], f"a gate passing exactly `{opt} {value}` is green")
        suffixed = mutate(honest, lambda d, o=opt, v=value: d["stages"][1].update(
            command=d["stages"][1]["command"].replace(f"{o} {v}", f"{o} {v}.expanded")))
        red(suffixed, "node-repo", f"passes `{opt}`", f"a debt-free claim whose gate passes `{opt} {value}.expanded` (a SUFFIXED path)", wf)
        second = mutate(honest, lambda d, o=opt: d["stages"][1].update(command=d["stages"][1]["command"] + f" {o} other.allow"))
        red(second, "node-repo", f"passes `{opt}`", f"a debt-free claim whose gate adds a second `{opt} other.allow` (argparse keeps the last)", wf)
    print("fail-fast (#7456) — every check reaches the stage's exit code:")
    claim = lambda d: d.update(owes=[], ciEquivalent=True)  # noqa: E731
    sat = mutate(node, lambda d: (claim(d), d["stages"][1].update(command=_SATELLITE_GATE)))
    expect(problems(json.dumps(sat), "node-repo", NODE_CI)[0] == [],
           "the satellites' `set -uo pipefail` + hand-captured exit codes is green")
    unsafe = mutate(node, lambda d: (claim(d), d["stages"][1].update(command=_UNSAFE_GATE)))
    red(unsafe, "node-repo", "does not propagate the compile step's exit code", "compile; gate | tee; ratchet with no set -e")
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command="set -e; " + _UNSAFE_GATE))), "node-repo",
        "does not propagate the gate run's exit code", "`set -e` without pipefail over a gate piped into tee")
    for broken, needle, what in (
            ('--output "$bake" || exit $?;', '--output "$bake";', "compile step", ),
            ("gate=${PIPESTATUS[0]}", "gate=$?", "gate run"),
            ('if [ "$gate" -ne 0 ]; then exit "$gate"; fi; ', "", "gate run"),
            ("ratchet=$?; ", "", "Tests-area ratchet")):
        cmd = _SATELLITE_GATE.replace(broken, needle)
        expect(cmd != _SATELLITE_GATE, f"self-test fixture: the satellite gate without `{broken}` was built")
        red(mutate(node, lambda d, c=cmd: (claim(d), d["stages"][1].update(command=c))), "node-repo",
            f"does not propagate the {what}'s exit code", f"a satellite gate that drops `{broken}`")
    # EXECUTING controls: the runner's `bash -c`, the tester's compile exiting 1, the gate and ratchet green.
    expect(_run_stage(_UNSAFE_GATE, compile_exit=1) == 0,
           "EXECUTED: the unsafe shape exits 0 over a failed compile (the hazard is real, not hypothetical)")
    expect(_run_stage(node["stages"][1]["command"], compile_exit=1) != 0, "EXECUTED: the fixture's fail-fast gate exits non-zero over a failed compile")
    expect(_run_stage(_SATELLITE_GATE, compile_exit=1) != 0, "EXECUTED: the satellites' hand-captured gate exits non-zero over a failed compile")
    expect(_run_stage(node["stages"][1]["command"], compile_exit=0) == 0, "EXECUTED: the fail-fast gate is green when every check is green")
    expect(_run_stage(_SATELLITE_GATE, compile_exit=0) == 0, "EXECUTED: the satellites' gate is green when every check is green")
    print("narrowed gate (#7456) — one slice never stands in for the whole gate:")
    gate_cmd = node["stages"][1]["command"]
    one = gate_cmd.replace("--seed b", "--shard 1/4 --seed b")
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=one))), "node-repo", "never run",
        "a single `--shard 1/4` slice standing in for the gate")
    every = gate_cmd.replace("\"$d\" \"$t\" . --allow plugin-gate.allow --seed b",
                             "; ".join(f"\"$d\" \"$t\" . --allow plugin-gate.allow --shard {k}/4 --seed b" for k in range(1, 5)))
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=every))), "node-repo", "does not fold the slices",
        "every slice run but never folded with gate-shard-merge.py")
    folded = every + "; python3 gate-shard-merge.py --logs l --shards 4 --gate-result success --out o"
    expect(not any("--shard" in p for p in problems(json.dumps(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=folded)))),
                                                    "node-repo", NODE_CI)[0]),
           "every literal slice 1..4 folded with gate-shard-merge.py --shards 4 passes the shard rule")
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=folded.replace("--shards 4", "--shards 3")))), "node-repo",
        "does not fold the slices", "slices folded under the wrong --shards count")
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=gate_cmd.replace("--seed b", "--shard \"$i/4\" --seed b")))),
        "node-repo", "cannot read as literal", "a slice taken from a variable (a loop this guard cannot read)")
    for quote in ('"', "'"):
        quoted = gate_cmd.replace("--seed b", f"{quote}--shard{quote} 1/4 --seed b")
        red(mutate(node, lambda d, c=quoted: (claim(d), d["stages"][1].update(command=c))), "node-repo", "never run",
            f"a QUOTED {quote}--shard{quote} 1/4 slice standing in for the gate (review of #7525)")
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=gate_cmd.replace("--seed b", "--shard \\\n 1/4 --seed b")))),
        "node-repo", "cannot read", "a `--shard` token the guard cannot read as a slice is red, never skipped")
    print("fail-fast, read in order (review of #7525):")
    late = _UNSAFE_GATE + "; set -euo pipefail"
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=late))), "node-repo",
        "does not propagate the compile step's exit code", "a `set -euo pipefail` written AFTER the checks")
    expect(_run_stage(late, compile_exit=1) == 0, "EXECUTED: a `set -e` after the checks still exits 0 over a failed compile")
    undone = "set -euo pipefail; set +e; " + _UNSAFE_GATE
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=undone))), "node-repo",
        "does not propagate the compile step's exit code", "a `set +e` that undoes `set -e` before the checks")
    expect(_run_stage(undone, compile_exit=1) == 0, "EXECUTED: `set -e; set +e` exits 0 over a failed compile")
    tee_exit = _SATELLITE_GATE.replace("set -uo pipefail; ", "set -u; ").replace(
        '| tee "$log"; gate=${PIPESTATUS[0]}; ', '| tee "$log" || exit $?; gate=0; ')
    expect(tee_exit != _SATELLITE_GATE, "self-test fixture: the `gate | tee || exit` gate was built")
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=tee_exit))), "node-repo",
        "does not propagate the gate run's exit code", "`gate | tee || exit` with no pipefail (the exit tests TEE)")
    expect(_run_stage(tee_exit, compile_exit=0, fail_on="--seed") == 0,
           "EXECUTED: `gate | tee || exit` without pipefail exits 0 over a failed gate")
    tee_exit_pf = tee_exit.replace("set -u; ", "set -uo pipefail; ")
    expect(problems(json.dumps(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=tee_exit_pf)))),
                    "node-repo", NODE_CI)[0] == [], "`gate | tee || exit` UNDER pipefail is green")
    expect(_run_stage(tee_exit_pf, compile_exit=0, fail_on="--seed") != 0,
           "EXECUTED: `gate | tee || exit` under pipefail exits non-zero over a failed gate")
    one_run = '"$dn" "$tester" . --app "$app" --allow plugin-gate.allow --seed "$bake" 2>&1 | tee "$log"; gate=${PIPESTATUS[0]}; '
    run = lambda k, v: (f'"$dn" "$tester" . --app "$app" --allow plugin-gate.allow --shard {k}/2 --seed "$bake" 2>&1 '  # noqa: E731
                        f'| tee "$log.{k}"; {v}=${{PIPESTATUS[0]}}; ')
    merge = 'python3 gate-shard-merge.py --logs "$logdir" --shards 2 --out "$log"; merged=$?; if [ "$merged" -ne 0 ]; then exit "$merged"; fi; '
    overwritten = _SATELLITE_GATE.replace(one_run, run(1, "gate") + run(2, "gate") + merge)
    distinct = _SATELLITE_GATE.replace(
        one_run, run(1, "g1") + 'if [ "$g1" -ne 0 ]; then exit "$g1"; fi; ' + run(2, "gate") + merge)
    unmerged = distinct.replace('merged=$?; if [ "$merged" -ne 0 ]; then exit "$merged"; fi; ', "")
    expect(len({_SATELLITE_GATE, overwritten, distinct, unmerged}) == 4, "self-test fixture: the two-slice gates were built")
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=overwritten))), "node-repo",
        "does not propagate the gate run's exit code", "two slices captured into ONE variable (slice 1's failure overwritten)")
    expect(_run_stage(overwritten, compile_exit=0, fail_on="--shard 1/2") == 0,
           "EXECUTED: a slice-1 failure overwritten by slice 2's capture exits 0")
    expect(problems(json.dumps(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=distinct)))),
                    "node-repo", NODE_CI)[0] == [], "every slice's status exited before the next capture is green")
    expect(_run_stage(distinct, compile_exit=0, fail_on="--shard 1/2") != 0,
           "EXECUTED: …and it exits non-zero over a slice-1 failure")
    red(mutate(node, lambda d: (claim(d), d["stages"][1].update(command=unmerged))), "node-repo",
        "does not propagate the shard merge's exit code", "a gate-shard-merge.py whose failure never reaches the stage")
    red(node, "node-repo", "calls none of", "a ci.yml that calls no shared heavy lane", "jobs:\n  x:\n    runs-on: y\n")
    red(node, "node-repo", "is absent", "no ci.yml to derive the owed stages from", None)
    print(f"\n{'SELF-TEST FAILED' if fails else 'self-test: every breach is red, every honest declaration is green'}")
    return 1 if fails else 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return _self_test()
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--profile", required=True, choices=["core", "node-repo"])
    ap.add_argument("--file", default=STAGES_FILE)
    ap.add_argument("--workflow", default=CI_WORKFLOW)
    ap.add_argument("--require", action="store_true")
    a = ap.parse_args(argv)
    found, verdict = problems(_read(a.file), a.profile, _read(a.workflow) if a.profile == "node-repo" else None, a.require)
    for p in found:
        print(f"::error title=Build-host declaration::{p}")
    if not found:
        print(f"::notice title=Build-host declaration::{verdict}")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
