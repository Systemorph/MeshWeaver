#!/usr/bin/env python3
"""core-build-stages.py — core's `.github/build-stages.json` is GENERATED from CI_SHARD_TOTAL, and checked against it.

Core declares its suites as build-host stages, ONE declaration for both routes that run them:

  * a candidate DEPLOYMENT (Doc/Architecture/DeployTestSwitchLatest §2 step 3, phase P4) submits one
    pinned run per stage — `test-shard-<i>` and `doc-gate` — and reads the set's `mw-core-tests`
    image, staged on the host at /core-tests;
  * a pull request or trunk run (Doc/Architecture/BuildHosts → "Moving a repository's tests to the
    build server") runs `build` FIRST — the whole solution, Release, warnings as errors, as
    dotnet-test.yml's `build` job does, restored from the host's prepared package cache (the fallback folder), then
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
# The `build` job, as one stage. No restore STEP: the host comes prepared. The build restores through
# the repository's own nuget.config (its packageSourceMapping included) with the host's read-only
# prepared cache bound as the FALLBACK folder (`needs: nuget` -> NUGET_FALLBACK_PACKAGES): every package
# the cache holds is read from it and nothing is fetched. 🚨 Never `--source "$NUGET_FALLBACK_PACKAGES"`:
# with packageSourceMapping on, a source passed on the command line is a source no pattern maps to, so
# EVERY package is NU1100 even from a complete cache (measured: 435 x NU1100 restoring
# MeshWeaver.Hosting.Test's closure from a warm cache; 0 errors and 0 downloads through the fallback
# folder). A package the cache does not hold yet (a new or bumped PackageVersion) is restored into the
# run's own writable package folder and NAMED below; the host reports it (MWPREP001) and promotes it into
# the cache after a succeeded TRUNK build (MeshWeaver.Deploy#42), so the cache follows main.
# Where the build stages the licence evidence before handing it back (the run's writable checkout; never
# $MW_BUILD_OUTPUTS, which the host promotes after a trunk build).
EVIDENCE_DIR = ".core-tests/licence-evidence"
ARTIFACTS_ENV = '"${MW_BUILD_ARTIFACTS:?the host names no artifacts directory (MW_BUILD_ARTIFACTS)}"'
BUILD_COMMAND = (
    "set -euo pipefail; "
    ': "${NUGET_FALLBACK_PACKAGES:?the host bound no prepared package cache (needs: nuget)}"; '
    f'dotnet build {SOLUTION} -c Release -p:CIRun=true -warnaserror; '
    'fetched=$({ find "${NUGET_PACKAGES:?the host bound no writable package folder}" -mindepth 2 -maxdepth 2 -type d '
    "2>/dev/null || true; } | sed -E 's#.*/([^/]+)/([^/]+)$#\\1/\\2#' | sort | tr '\\n' ' '); "
    'if [ -n "$fetched" ]; then echo "::warning::the prepared package cache did not hold: $fetched- restored for '
    'this run; a succeeded trunk build promotes them"; else echo "every package came from the prepared cache"; fi; '
    "missing=$(comm -23 <(find test -name '*.csproj' ! -path '*/bin/*' | sort) "
    f"<(grep -oE 'test/[^\"]+\\.csproj' {SOLUTION} | sort)); "
    f'if [ -n "$missing" ]; then echo "::error::these test projects are not in {SOLUTION}, so they are never built '
    'and their tests never run: $missing"; exit 1; fi; '
    f'{RUNNER} pack --repo . --out {RUN_ROOT} --sha {SHA_ENV} --version "run-${{MW_BUILD_SHA}}"; '
    # THE DEPENDENT LEGS' INPUTS, handed back (host-build-outputs.py): on Actions the `build` job uploads shard 0's
    # tarball (platform-compat reads its plugin tester) and the licence evidence staged from its ONE restore
    # (licence-gate judges it), and both judge the tree the build compiled. Here the same evidence is staged from this
    # stage's restore — the writable folder AND the prepared cache bound as the fallback folder — and the tester, the
    # evidence and the tested tree are left under $MW_BUILD_ARTIFACTS/build-outputs/, which the host hands over as
    # receipts; dotnet-test.yml's host-build-outputs job fetches them back into the run under the Actions names.
    f'rm -rf {EVIDENCE_DIR}; python3 .github/scripts/check-licenses.py --stage-evidence {EVIDENCE_DIR}; '
    f'python3 .github/scripts/host-build-outputs.py stage --repo . --sha {SHA_ENV} --evidence {EVIDENCE_DIR} '
    f'--artifacts {ARTIFACTS_ENV}'
)
# The default-branch RESTORE a standing build host runs ONCE per restore key to warm its shared NuGet cache
# (MeshWeaver.Deploy#68, build-run.py `nuget_warm_plan`/`warm_nuget`): the host reads this declaration from the
# DEFAULT BRANCH's tip in its mirror — never from a run's own tree — runs it in the sandbox on a host-made
# checkout of that tip, and promotes every package it fetched into <cache>/nuget. It must restore exactly the
# closure BUILD_COMMAND's implicit restore needs: the same solution and the same global properties
# (Configuration=Release, as `-c Release` sets; CIRun=true), through the repository's nuget.config — never a
# `--source` (see above). A drifted command warms a closure the build does not read, and every run restores
# per run again (MWPREP001).
# It ALSO restores RESOLVER_FIXTURES: the packages NuGetAssemblyResolver fetches at TEST time (`#r "nuget:…"` in
# executable doc cells and samples, the resolver's own integration tests). They are in no project's closure, so
# without it every run downloaded them again; with it they are in <cache>/nuget, which the resolver reads as a
# NuGet fallback folder (NUGET_FALLBACK_PACKAGES) and downloads nothing it holds complete.
RESOLVER_FIXTURES = ".github/nuget-warm/ResolverFixturePackages.csproj"
RESTORE_COMMAND = (f"dotnet restore {SOLUTION} -p:Configuration=Release -p:CIRun=true"
                   f" && dotnet restore {RESOLVER_FIXTURES}")
RESTORE_TIMEOUT_MINUTES = 20
# What core's stages still check LESS than dotnet-test.yml's legs, or cannot yet deliver as a
# verdict. Remove an entry only in the change that pays it (check-build-stages.py refuses the
# ciEquivalent claim while any remains).
OWES = [
    "Consolidate test results: host-results (GitHub-hosted — core runs nothing on the self-hosted pool and reads no "
    "shared store) fetches each host shard's results tree over HTTPS by receipt (host-shard-results.py: the run's OIDC "
    "token, every file verified against its receipt) and uploads it as the `test` matrix's own artifact. Both ends "
    "exist: a host shard hands its tree back as receipts, and the build API serves a receipt's bytes at "
    "GET {api}/{repo}/{sha}/artifacts/{sha256} (MeshWeaver.Plugins' BuildHostEndpoints). What is unproven is the "
    "fetch itself: host-results runs only on the plain `host` route, never in host-forced shadow, so no run has yet "
    "fetched a tree that way with its OIDC token. Paid only when collect-results consolidates results fetched that "
    "way on a host run (Actions: collect-results over the testResults-shard<i>-attempt<a> artifacts)",
    "prepared packages: the build restores through the repository's nuget.config with the host's cache as the "
    "fallback folder, and a package the cache lacks is restored for the run and named; the host WARMS the cache from "
    "the default branch's tip by running the declared `restore` once per restore key (MeshWeaver.Deploy#68) - until "
    "that is on build-latest and has warmed core's key there, core's restore closure is not in the cache (Actions: "
    "the `build` job's NuGet-shelf hydration and restore)",
    "browser: the shards that run browser tests need the headless browser and the font set the `browser-fonts` job "
    "stages and each `test` shard provisions; the build-host image (MeshWeaver.Deploy deploy/build-host/Dockerfile) "
    "carries neither",
    "prepared mesh: on a host no test may install a mesh from scratch — a prepared mesh snapshot is cloned per run; "
    "which of core's fixtures that replaces is not established (owned by the prepared-server work)",
    "evidence: no stage has run on a host — a probe of every stage on build-latest must be green and agree with the "
    "Actions legs of the same commit",
]
COMMENT = ("GENERATED by .github/scripts/core-build-stages.py --write from CI_SHARD_TOTAL in "
           ".github/workflows/dotnet-test.yml; --check reds any drift. One declaration for both routes: a "
           "candidate DEPLOYMENT runs test-shard-<i> and doc-gate as pinned runs from the set's mw-core-tests "
           "image (/core-tests); a host PR/trunk run builds first and packs its own tree. Every shard reads the "
           "pack built from the run's own commit (--require-sha), never another set's. `restore` is what the host "
           "runs once per restore key, from the default branch's tip, to warm its shared NuGet cache with the "
           "build's closure. `parallel` lets a host run the stages after the build concurrently (fan-out: the 8 "
           "shards and doc-gate at once, as Actions' `test` matrix; the build runs alone). `owes` is what the stages "
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
                "command": f"{RUNNER} run --shard {i} --total {total} {ROOTS} --repo .",
                "timeoutMinutes": SHARD_TIMEOUT_MINUTES} for i in range(total)]
    stages.append({"name": "doc-gate", "kind": "test",
                   "command": f"{RUNNER} doc-gate {ROOTS} --repo .",
                   "timeoutMinutes": DOC_GATE_TIMEOUT_MINUTES})
    # FAN-OUT (MeshWeaver.Deploy build-run.py): every stage after `build` reads the run's pack read-only and writes
    # only its own work directory (`.core-tests/shard-<i>`, `.core-tests/doc-gate`), its own $TMPDIR and its own
    # $MW_BUILD_ARTIFACTS, so a host with stage slots runs the 8 shards and doc-gate CONCURRENTLY — Actions' 8-way
    # `test` matrix on one host — instead of one after another (measured on build-latest, #7308: 8 shards of 4-7 min
    # each, ~45 min serial). A host with one slot still runs them serially; nothing else changes.
    doc = {"$comment": COMMENT, "owes": list(OWES),
           "restore": {"command": RESTORE_COMMAND, "timeoutMinutes": RESTORE_TIMEOUT_MINUTES},
           "parallel": True,
           "stages": stages}
    if not OWES:
        doc["ciEquivalent"] = True  # the debt rule: owing nothing IS the claim, so the fully migrated file is reachable
    return doc


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


# A host stage's sandbox has no Docker daemon (Doc/Architecture/BuildHosts §2 step 5; its database is a
# prepared server, host-postgres.sh / sandbox-postgres.sh). Core's suites need none: the Postgres suites
# left core (#2847 and its successors) and the `test` job pulls no image. That is a FACT about the tree,
# so it is checked, not assumed — a suite that starts a container again reds here, and either moves to
# the host's prepared service or re-opens the debt (`containers:` in OWES) in the same change.
# The package check reads the MSBuild XML itself — PackageReference / PackageVersion /
# GlobalPackageReference elements, any quoting, comments ignored — so a `<Compile Include="Testcontainers…cs"/>`
# or a commented-out reference is not a dependency, and the legacy `DotNet.Testcontainers` package is one.
CONTAINER_PACKAGE_ITEMS = {"PackageReference", "PackageVersion", "GlobalPackageReference"}
CONTAINER_PACKAGE_NAME = re.compile(r"^\s*(Testcontainers|DotNet\.Testcontainers|Docker\.DotNet)(\.|\s*$)", re.I)
# Code: a using of either namespace, a ContainerBuilder, or a fully qualified (DotNet.)Testcontainers.<Type> use.
CONTAINER_CODE = re.compile(r"^\s*using\s+(DotNet\.Testcontainers|Testcontainers)\b|new\s+ContainerBuilder\b"
                            r"|(?<![\w.])(DotNet\.)?Testcontainers\.[A-Z]\w*\.", re.M)


def csharp_code_only(text: str) -> str:
    """`text` with every comment and the literal text of every string and char literal blanked (newlines kept,
    so a `^`-anchored match still sees line starts), while an interpolated string's holes stay code. The
    container scan reads only what EXECUTES: `// Previously used new ContainerBuilder()` and a fixture string
    that contains `using DotNet.Testcontainers.Builders;` start nothing."""
    out: list[str] = []
    n = len(text)

    def blank(chunk: str) -> None:
        out.append("".join(c if c == "\n" else " " for c in chunk))

    def code(i: int, close: bool) -> int:
        # Scans code from i; with `close`, stops at the `}` that ends an interpolation hole (depth 0).
        depth = 0
        while i < n:
            c = text[i]
            if text.startswith("//", i):
                j = text.find("\n", i)
                j = n if j < 0 else j
                blank(text[i:j]); i = j
            elif text.startswith("/*", i):
                j = text.find("*/", i + 2)
                j = n if j < 0 else j + 2
                blank(text[i:j]); i = j
            elif c == "'":
                j = i + 1
                while j < n and text[j] not in "'\n":
                    j += 2 if text[j] == "\\" else 1
                j = min(j + 1, n)
                blank(text[i:j]); i = j
            elif c in '$@"':
                j = i
                dollars = 0
                verbatim = False
                while j < n and text[j] in "$@":
                    dollars += text[j] == "$"; verbatim |= text[j] == "@"; j += 1
                if j >= n or text[j] != '"':  # `$`/`@` not opening a string (`@class`, a stray `$`)
                    out.append(text[i:j]); i = j; continue
                out.append(" " * (j - i)); i = string(j, dollars, verbatim)
            elif c == "{":
                depth += 1; out.append(c); i += 1
            elif c == "}":
                if close and depth == 0:
                    return i
                depth -= 1; out.append(c); i += 1
            else:
                out.append(c); i += 1
        return i

    def hole(i: int, braces: int) -> int:
        # i is at the first of `braces` opening braces; the hole's expression stays code.
        out.append(" " * braces)
        i = code(i + braces, close=True)
        k = 0
        while i < n and text[i] == "}" and k < braces:
            i += 1; k += 1
        out.append(" " * k)
        return i

    def string(i: int, dollars: int, verbatim: bool) -> int:
        q = 0
        while i + q < n and text[i + q] == '"':
            q += 1
        if q >= 3 and not verbatim:  # raw string literal: ends at the same run of quotes; holes open with `dollars` braces
            blank(text[i:i + q]); i += q
            while i < n:
                if text.startswith('"' * q, i):
                    blank(text[i:i + q]); return i + q
                if dollars and text.startswith("{" * dollars, i):
                    i = hole(i, dollars); continue
                blank(text[i]); i += 1
            return i
        if q == 2 and not verbatim:  # the empty string ""
            blank('""'); return i + 2
        blank('"'); i += 1
        while i < n:
            c = text[i]
            if verbatim and text.startswith('""', i):
                blank('""'); i += 2
            elif c == '"':
                blank(c); return i + 1
            elif not verbatim and c == "\\":
                blank(text[i:i + 2]); i += 2
            elif dollars and text.startswith("{{", i):
                blank("{{"); i += 2
            elif dollars and c == "{":
                i = hole(i, 1)
            elif c == "\n" and not verbatim:
                return i
            else:
                blank(c); i += 1
        return i

    code(0, close=False)
    return "".join(out)


def container_package_hit(text: str) -> str | None:
    """The first container package an MSBuild file references (None when it references none). Raises
    ValueError when the file is not XML — no verdict is ever read off a file that could not be parsed."""
    import xml.etree.ElementTree as ET
    try:
        root = ET.fromstring(text)
    except ET.ParseError as e:
        raise ValueError(f"not parseable as MSBuild XML: {e}") from e
    for el in root.iter():
        tag = el.tag.rsplit("}", 1)[-1] if isinstance(el.tag, str) else ""
        if tag not in CONTAINER_PACKAGE_ITEMS:
            continue
        for attr in ("Include", "Update"):
            value = el.get(attr) or ""
            for item in value.split(";"):
                if CONTAINER_PACKAGE_NAME.match(item):
                    return f'<{tag} {attr}="{item.strip()}">'
    return None


SKIP_DIRS = {"bin", "obj", "node_modules", ".git", ".core-tests"}


def container_debt_open(owes: list[str]) -> bool:
    """The `containers:` debt re-opened in OWES — then a Docker-dependent suite is DECLARED unmet, the
    ciEquivalent rule keeps every run on Actions, and the scan is not a second, contradicting gate."""
    return any(str(o).startswith("containers:") for o in owes)


def container_problems(root: str, owes: list[str] | None = None) -> list[str]:
    """Every place in core's build that would make a shard start a container (nothing while the
    `containers:` debt is declared open)."""
    if container_debt_open(OWES if owes is None else owes):
        return []
    out: list[str] = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        rel_dir = os.path.relpath(dirpath, root)
        for n in sorted(filenames):
            rel = os.path.normpath(os.path.join(rel_dir, n))
            is_msbuild = n.endswith((".csproj", ".props", ".targets"))
            if is_msbuild:
                what = "references a container package"
            elif n.endswith(".cs") and (rel.startswith("test" + os.sep) or rel == "test"):
                what = "starts a container"
            else:
                continue
            try:
                with open(os.path.join(dirpath, n), encoding="utf-8-sig", errors="replace") as fh:
                    text = fh.read()
                if is_msbuild:
                    hit = container_package_hit(text)
                else:
                    m = CONTAINER_CODE.search(csharp_code_only(text))
                    hit = m.group(0).strip() if m else None
            except (OSError, ValueError) as e:
                out.append(f"{rel}: unreadable ({e}) — the no-container check reached no verdict on it")
                continue
            if hit:
                out.append(f"{rel} {what} ({hit[:80]}) — a build host's sandbox has no Docker daemon; "
                           f"run the suite against the host's prepared service, or re-open the `containers:` debt in "
                           f"core-build-stages.py OWES in this change")
    return out


FOLLOW_MATRIX = re.compile(r"^  host-follow:\n(?:    .*\n|\n)*?        stage: \[([^\]]*)\]", re.M)
RESULTS_MATRIX = re.compile(r"^  host-results:\n(?:    .*\n|\n)*?        shard: \[([^\]]*)\]", re.M)
FOLLOW_INPUT = re.compile(r"^  host-follow:\n(?:    .*\n|\n)*?      stage: ([^\n]*)\n", re.M)


def _stage_of():
    """build-follow.py's own `--stage` parser — the route is checked against what the follower accepts."""
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "build-follow.py")
    spec = importlib.util.spec_from_file_location("build_follow", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod.stage_of, mod.Failure


def route_problems(workflow_text: str, total: int) -> list[str]:
    """dotnet-test.yml's host route follows EVERY declared stage and fetches EVERY shard's results —
    a stage the follow matrix omits would be a host verdict nobody reads."""
    out: list[str] = []
    want = [s["name"] for s in generate(total)["stages"]]
    m = FOLLOW_MATRIX.search(workflow_text)
    got = [x.strip() for x in m.group(1).split(",")] if m else None
    if got != want:
        out.append(f"{WORKFLOW} job host-follow's stage matrix is {got} but {STAGES} declares {want} — follow every declared stage")
    # Each matrix entry reaches build-follow.py as the `stage` input. A bare `build` is the AGGREGATE
    # check's name there and is refused before polling, so every entry must reach it as the stage's
    # qualified check name — held by running the follower's own parser over what the job passes.
    m = FOLLOW_INPUT.search(workflow_text)
    passed = m.group(1).strip().strip("'\"") if m else None
    if passed is None or "${{ matrix.stage }}" not in passed:
        out.append(f"{WORKFLOW} job host-follow passes no `stage: …${{{{ matrix.stage }}}}` input ({passed!r})")
    else:
        stage_of, failure = _stage_of()
        for s in want:
            value = passed.replace("${{ matrix.stage }}", s)
            try:
                if stage_of(value, "latest") != s:
                    out.append(f"{WORKFLOW} job host-follow passes --stage {value!r}, which build-follow.py reads as another stage than {s!r}")
            except failure as e:
                out.append(f"{WORKFLOW} job host-follow passes --stage {value!r}, which build-follow.py refuses: {e}")
    m = RESULTS_MATRIX.search(workflow_text)
    got = [x.strip() for x in m.group(1).split(",")] if m else None
    if got != [str(i) for i in range(total)]:
        out.append(f"{WORKFLOW} job host-results' shard matrix is {got} but CI_SHARD_TOTAL is {total} — fetch every shard's results")
    return out


# THE DEPENDENT LEGS on the host route: `platform-compat` reads the `build` job's shard-0 output (its plugin tester)
# and `licence-gate` its licence evidence, both judging the tree the build compiled. The host `build` stage hands them
# back (BUILD_COMMAND → host-build-outputs.py stage), `host-build-outputs` fetches them by receipt and re-uploads them
# under the Actions artifact names, and the two consumers accept either producer. Each line is a substring the named
# job must carry, and why — the wiring is TEXT, read without a YAML parser (main-cd's runner has none).
HANDBACK_JOB = "host-build-outputs"
HANDBACK_MAIN = "${{ needs.build.outputs.main-sha || needs.host-build-outputs.outputs.main-sha }}"
HANDBACK_RUNS = "needs.admission.outputs.executor == 'host' && needs.host-build-outputs.result == 'success'"
HANDBACK_WIRING = {
    HANDBACK_JOB: [
        ("if: ${{ !cancelled() && needs.admission.outputs.executor == 'host' }}", "it runs on the host route, whatever the follows concluded"),
        ("runs-on: ubuntu-latest", "GitHub-hosted: core runs nothing on the self-hosted pool"),
        ("id-token: write", "the run's own OIDC token is the build API's gate"),
        ("host-build-outputs.py fetch", "the outputs come back by verified receipt, never by trust"),
        ("main-sha: ${{ steps.fetch.outputs.main-sha }}", "the fresh merge onto the main the HOST compiled"),
        ("BUILT_TREE: ${{ steps.fetch.outputs.tree }}", "…held to the tree the host built"),
        ("--long=27", "shard 0's output in the Actions tarball shape (platform-compat extracts with --long=27)"),
        ("name: build-output-0", "under the Actions artifact name platform-compat downloads"),
        ("name: licence-evidence", "under the Actions artifact name licence-gate downloads"),
    ],
    "platform-compat": [
        ("host-build-outputs]", "it waits for the hand-back (a need)"),
        (HANDBACK_RUNS, "it runs on the host route once the hand-back is in"),
        (f"main-sha: {HANDBACK_MAIN}", "it judges the tree the build compiled, on either route"),
        ("name: build-output-0", "it reads shard 0's output by the one name both producers upload"),
    ],
    "licence-gate": [
        ("host-build-outputs]", "it waits for the hand-back (a need)"),
        (HANDBACK_RUNS, "it runs on the host route once the hand-back is in"),
        (f"main-sha: {HANDBACK_MAIN}", "it judges with the checker of the tree the evidence came from, on either route"),
        ("name: licence-evidence", "it reads the evidence by the one name both producers upload"),
    ],
    "collect-results": [
        ("host-build-outputs,", "the required check waits for the hand-back"),
        ("needs.host-build-outputs.result != 'success'", "…and is RED when it was not brought back"),
    ],
}


def _job_block(text: str, name: str) -> str | None:
    """One job's text: the `  <name>:` line's body up to the next line at two spaces or less."""
    m = re.search(rf"^  {re.escape(name)}:\n((?:    .*\n|\n)*)", text, re.M)
    return m.group(1) if m else None


def handback_problems(workflow_text: str) -> list[str]:
    """dotnet-test.yml hands the host build's outputs to platform-compat and licence-gate (HANDBACK_WIRING)."""
    out: list[str] = []
    for job, needles in HANDBACK_WIRING.items():
        block = _job_block(workflow_text, job)
        if block is None:
            out.append(f"{WORKFLOW} has no job {job} — the host build's outputs would reach neither platform-compat "
                       f"nor licence-gate")
            continue
        for needle, why in needles:
            if needle not in block:
                out.append(f"{WORKFLOW} job {job} does not carry `{needle}` — {why}")
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
    expect(all(s["command"].endswith(" --repo .") for s in doc["stages"][1:-1]),
           "every shard records and gates with the checkout's own scripts (record-host-crash.py, check-untyped-content.sh)")
    expect(doc["stages"][0]["needs"] == ["nuget"], "the build reads the host's prepared package cache, and nothing else")
    expect("--source" not in doc["stages"][0]["command"], "the build restores through the repository's source mapping, never a --source")
    expect(doc["restore"] == {"command": "dotnet restore MeshWeaver.slnx -p:Configuration=Release -p:CIRun=true"
                                         " && dotnet restore .github/nuget-warm/ResolverFixturePackages.csproj",
                              "timeoutMinutes": 20},
           "the host's default-branch restore is declared: the build's solution and global properties, then the "
           "resolver's test-time packages, within the cap")
    expect(os.path.isfile(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "nuget-warm",
                                       "ResolverFixturePackages.csproj")),
           "the resolver-fixture project the restore names exists")
    expect(f"dotnet build {SOLUTION} -c Release -p:CIRun=true" in doc["stages"][0]["command"],
           "the declared restore's properties are the build's own (-c Release = Configuration=Release, CIRun=true)")
    expect(doc["owes"] == OWES and doc.get("ciEquivalent") is (True if not OWES else None),
           "the debt is declared, and the claim is made exactly when nothing is owed")
    saved = list(OWES)
    try:
        OWES.clear()  # generate() reads the module list — emptied in place, restored below
        paid = generate(8)
        expect(paid["owes"] == [] and paid.get("ciEquivalent") is True and problems(render(paid), wf8) == [],
               "with OWES emptied the regenerated file claims ciEquivalent and passes the rule (the migrated end state is reachable)")
    finally:
        OWES[:] = saved
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
    d = json.loads(good); del d["restore"]
    expect(any("`restore`" in p for p in problems(render(d), wf8)), "the default-branch restore deleted by hand")
    d = json.loads(good); d["restore"]["command"] = "dotnet restore MeshWeaver.slnx -p:CIRun=true"
    expect(any("Configuration=Release" in p for p in problems(render(d), wf8)),
           "a restore that warms another closure than the build reads (Debug, not Release)")
    d = json.loads(good); d["restore"]["timeoutMinutes"] = 90
    expect(any("timeoutMinutes" in p for p in problems(render(d), wf8)), "a restore cap past the host's 45 minutes")
    expect(problems(None, wf8) != [], "an absent file")
    expect(problems("{", wf8) != [], "a file that is not JSON")
    try:
        problems(good, "env:\n  X: 1\n")
        expect(False, "a workflow with no CI_SHARD_TOTAL is refused")
    except SystemExit:
        expect(True, "a workflow with no CI_SHARD_TOTAL is refused")
    print("the host route follows every declared stage and fetches every shard:")
    stages2 = ", ".join(x["name"] for x in generate(2)["stages"])
    route = ("jobs:\n  host-follow:\n    strategy:\n      matrix:\n        stage: [" + stages2 + "]\n"
             "    with:\n      stage: build/${{ matrix.stage }}\n"
             "  host-results:\n    strategy:\n      matrix:\n        shard: [0, 1]\n")
    expect(route_problems(route, 2) == [], "both matrices name exactly the declaration")
    expect(any("host-follow" in p for p in route_problems(route.replace(", doc-gate", ""), 2)), "RED: a declared stage nobody follows")
    expect(any("host-results" in p for p in route_problems(route.replace("[0, 1]", "[0]"), 2)), "RED: a shard whose results are never fetched")
    expect(any("refuses" in p for p in route_problems(route.replace("build/${{ matrix.stage }}", "${{ matrix.stage }}"), 2)),
           "RED: the bare `build` entry reaches build-follow.py as the aggregate check, which it refuses")
    expect(len(route_problems("jobs:\n", 2)) == 3, "RED: no host route at all")
    real_wf = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "workflows", "dotnet-test.yml")
    if os.path.isfile(real_wf):
        wf_text = open(real_wf).read()
        expect(route_problems(wf_text, read_shard_total(wf_text)) == [], "the real dotnet-test.yml's route matches the declaration")
    print("the host build hands platform-compat and licence-gate their inputs back:")
    build_cmd = doc["stages"][0]["command"]
    expect("check-licenses.py --stage-evidence .core-tests/licence-evidence" in build_cmd
           and build_cmd.index("--stage-evidence") > build_cmd.index("dotnet build"),
           "the build stage stages the licence evidence from ITS OWN restore (after the build restored)")
    expect('host-build-outputs.py stage --repo . --sha "${MW_BUILD_SHA' in build_cmd
           and '--artifacts "${MW_BUILD_ARTIFACTS:?' in build_cmd,
           "…and hands the tester, the evidence and the tested tree back under $MW_BUILD_ARTIFACTS")
    expect(not any(str(o).startswith("dependent legs") for o in OWES), "the `dependent legs` debt is paid, so it is not owed")
    wired = "jobs:\n" + "".join(f"  {job}:\n" + "".join(f"    x: {n}\n" for n, _ in needles)
                                 for job, needles in HANDBACK_WIRING.items())
    expect(handback_problems(wired) == [], "a workflow carrying every hand-back marker is green")
    for job, needles in HANDBACK_WIRING.items():
        for needle, _ in needles:
            expect(any(f"job {job} does not carry" in p for p in handback_problems(wired.replace(f"    x: {needle}\n", ""))),
                   f"RED: {job} drops `{needle}`")
    expect(any("no job host-build-outputs" in p for p in handback_problems(wired.replace("  host-build-outputs:\n", "  other:\n"))),
           "RED: no hand-back job at all")
    if os.path.isfile(real_wf):
        expect(handback_problems(open(real_wf).read()) == [], "the real dotnet-test.yml hands the host build's outputs back")
    print("no container — core's suites need no Docker daemon:")
    import tempfile
    # The scan's controls run against an explicit DEBT-FREE list, never the live OWES: re-opening
    # `containers:` in OWES (the documented route) must not turn the self-test red.
    debt_free = [o for o in OWES if not str(o).startswith("containers:")]
    with tempfile.TemporaryDirectory() as t:
        os.makedirs(os.path.join(t, "test", "A"))
        open(os.path.join(t, "test", "A", "A.csproj"), "w").write('<Project><ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup></Project>')
        open(os.path.join(t, "test", "A", "T.cs"), "w").write("using Xunit;\n// mentions Testcontainers in a comment only\n")
        expect(container_problems(t, debt_free) == [], "a tree with no container package and no container code is green")
        open(os.path.join(t, "Directory.Packages.props"), "w").write('<PackageVersion Include="Testcontainers.PostgreSql" Version="4.0.0" />')
        expect(any("container package" in p for p in container_problems(t, debt_free)), "RED: Testcontainers.PostgreSql pinned centrally")
        open(os.path.join(t, "Directory.Packages.props"), "w").write("<PackageVersion Include = 'Testcontainers.PostgreSql' Version='4.0.0' />")
        expect(any("container package" in p for p in container_problems(t, debt_free)), "RED: the same pin single-quoted with spaces around =")
        expect(container_problems(t, debt_free + ["containers: a suite starts a database in Docker"]) == [],
               "a re-opened `containers:` debt declares it unmet — the scan does not contradict the declaration")
        expect(any("container package" in p for p in container_problems(t, debt_free)),
               "…and without that debt the same tree is RED again")
        os.remove(os.path.join(t, "Directory.Packages.props"))
        open(os.path.join(t, "test", "A", "A.csproj"), "w").write("<PackageReference Include='Docker.DotNet' />")
        expect(any("container package" in p for p in container_problems(t, debt_free)), "RED: a test project referencing Docker.DotNet")
        open(os.path.join(t, "test", "A", "A.csproj"), "w").write("<Project/>")
        open(os.path.join(t, "test", "A", "T.cs"), "w").write("using DotNet.Testcontainers.Builders;\n")
        expect(any("starts a container" in p for p in container_problems(t, debt_free)), "RED: a test that builds a container")
        os.makedirs(os.path.join(t, "test", "A", "bin"))
        open(os.path.join(t, "test", "A", "T.cs"), "w").write("using Xunit;\n")
        open(os.path.join(t, "test", "A", "bin", "X.props"), "w").write('<PackageReference Include="Testcontainers" />')
        expect(container_problems(t, debt_free) == [], "build output (bin/) is not the tree")
        open(os.path.join(t, "test", "A", "A.csproj"), "w").write(
            '<Project><ItemGroup><Compile Include="TestcontainersPolicyTest.cs" />'
            '<!-- <PackageReference Include="Docker.DotNet" /> --></ItemGroup></Project>')
        expect(container_problems(t, debt_free) == [],
               "a Compile item named Testcontainers… and a commented-out Docker.DotNet reference are no dependency")
        open(os.path.join(t, "test", "A", "A.csproj"), "w").write(
            '<Project><ItemGroup><PackageReference Include="DotNet.Testcontainers" Version="1.6.0" /></ItemGroup></Project>')
        expect(any("container package" in p for p in container_problems(t, debt_free)), "RED: the legacy DotNet.Testcontainers package")
        open(os.path.join(t, "test", "A", "A.csproj"), "w").write("<Project/>")
        open(os.path.join(t, "test", "A", "T.cs"), "w").write(
            "public class T { object B() => new DotNet.Testcontainers.Builders.TestcontainersBuilder<object>(); }\n")
        expect(any("starts a container" in p for p in container_problems(t, debt_free)),
               "RED: a fully qualified DotNet.Testcontainers builder with no using")
        print("  the C# scan reads only what executes — comments and literal text start nothing:")
        green_cs = {
            "a line comment naming a builder": "using Xunit;\n// Previously used new ContainerBuilder()\n",
            "a block comment naming the namespace": "/*\nusing DotNet.Testcontainers.Builders;\n*/\nusing Xunit;\n",
            "a raw-string fixture holding container code":
                'var src = """\nusing DotNet.Testcontainers.Builders;\nvar c = new ContainerBuilder();\n""";\n',
            "a regular string": 'var s = "new ContainerBuilder() and Testcontainers.Builders.X.Y";\n',
            "a verbatim string with an escaped quote": 'var s = @"say ""hi""\nusing Testcontainers;";\n',
            "an interpolated string's literal text": 'var s = $"{1} new ContainerBuilder() {{literal}}";\n',
        }
        for what, src in green_cs.items():
            open(os.path.join(t, "test", "A", "T.cs"), "w").write(src)
            expect(container_problems(t, debt_free) == [], f"green: {what}")
        red_cs = {
            "code after a string on the same line": 'var s = "x // y"; var b = new ContainerBuilder();\n',
            "code after a char literal": "var q = '\\''; var b = new ContainerBuilder();\n",
            "an interpolation hole that builds one": 'var s = $"{new ContainerBuilder().Build()}";\n',
            "a raw interpolation hole that builds one": 'var s = $$"""{ {{new ContainerBuilder()}} }""";\n',
            "code after a raw string": 'var s = """\nx\n""";\nusing Testcontainers;\n',
        }
        for what, src in red_cs.items():
            open(os.path.join(t, "test", "A", "T.cs"), "w").write(src)
            expect(any("starts a container" in p for p in container_problems(t, debt_free)), f"RED: {what}")
        open(os.path.join(t, "test", "A", "T.cs"), "w").write("using Xunit;\n")
        open(os.path.join(t, "test", "A", "A.csproj"), "w").write("<Project><ItemGroup>")
        expect(any("no verdict" in p for p in container_problems(t, debt_free)), "RED: an unparseable project file reaches no verdict")
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
        found = (problems(_read(os.path.join(root, STAGES)), workflow) + container_problems(root)
                 + route_problems(workflow, read_shard_total(workflow)) + handback_problems(workflow))
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
