#!/usr/bin/env python3
"""compile-check.py — the COMPILING PR gate for the plugin node repos.

`validate-repos.py` checks JSON shape + Source presence but never compiles. That hole let
async-broken UWDeepfield source merge and park two production meshes (2026-07-21). This gate closes
it: it compiles every NodeType's C# `Source` against the framework assemblies EXACTLY as the mesh
does on import, and fails on any *regression*.

Faithful mesh reproduction (mirrors scratchpad/build-check.sh, proven against the live compile):
  * ref assemblies = every `<core>/src/*/bin/(Debug|Release)/net10.0/*.dll`, deduped by filename,
    preferring the assembly's OWN project dir + newest mtime;
  * ONE COMPILATION UNIT per NodeType (`UNIT_FILE`): every source concatenated in node-path order
    with the `using` directives hoisted and deduped, which is what
    `DynamicMeshNodeAttributeGenerator.ShapeAuthoredSource` produces and what the portal compiles.
    File-per-`<Compile>` is a DIFFERENT compilation — `#nullable`, `#pragma warning` and the using
    scope are per-FILE in C# — and the gate was blind to every diagnostic that difference creates
    (#4711). `ConcatenatedUnitParityTest` pins the two shapings against each other;
  * `Microsoft.Extensions.AI` is hoisted like any other import (the mesh hoists it too); a set that
    genuinely needs the absent assembly is reported UNVERIFIABLE rather than false-failed;
  * per-type source resolution replays the THREE source queries the mesh compile log shows: the
    type's own `Source` subtree (plus any DECLARED `sources` — never an implicit partition Source; the mesh resolves only what is declared) and the type's own `Test`
    subtree — honouring the `sources` field forms actually present in this repo;
  * …AND the type's `configuration` LAMBDA, wrapped in the same generated `ConfigureHub` method the
    mesh builds around it (`DynamicMeshNodeAttributeGenerator`). That field is C# living in JSON, so
    no compiler ever saw it: on 2026-08-09 `SocialMedia/Post`, `Profile` and `PostsHub` each called
    `AddTracking()` there after the framework deleted it, the sibling gate reported 22/22 clean, and
    all three production portals hit `REFUSING READINESS`. Sources and lambda compile TOGETHER (the
    lambda names types the sources define), so a set is keyed by both.

Ratcheting allowlist (`scripts/compile-check.allow`): the tree carries KNOWN UWDeepfield async-port
debt. An allowlisted type that still fails is reported as known-debt (no gate fail); one that now
compiles clean FAILS the gate ("remove it — it compiles now") so debt only ever shrinks. A
NON-allowlisted failure fails the gate — a real regression. Exit non-zero iff any gate-fail.

Scope (`--modules A,B,C`): compile ONLY the NodeTypes of the named packages (top-level folders).
The compile was always atomic — every type resolves its own declared source set and builds alone —
but the SELECTION was not: a pull request compiled all 92 NodeTypes of MeshWeaver.Plugins for a
one-package diff ("we wanted to disentangle in atomic units", maintainer, 2026-09-11). The caller
(the repo's CI) decides the unit from `affected-modules.py`; this flag is the per-unit mechanism.
An unknown id and an empty list are both RED — a scoped run that silently compiled nothing would
render exactly like a green gate. The allow-file ratchet is evaluated over the selected set ONLY:
an allowlisted type outside the scope was not compiled, so it is neither "now compiles" nor a ghost.
"""
import argparse
import glob
import json
import os
import re
import subprocess
import sys
import unicodedata
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
# 🚨 THE PLATFORM'S copy is the one implementation, every repo ("centralize scripts as best as
# possible", maintainer, 2026-09-01) — the reusable lane fetches THIS file at the pinned
# platform ref and runs it against the CALLER's tree, so the tree root and the allow-file
# (per-repo POLICY, the only piece that stays in the caller) arrive via the environment.
# Unset, both fall back to the script-beside-the-repo layout so a legacy per-repo copy keeps
# working during migration.
import os as _os
ROOT = Path(_os.environ["MW_REPO_ROOT"]).resolve() if _os.environ.get("MW_REPO_ROOT") else SCRIPTS.parent
ALLOW_FILE = (Path(_os.environ["MW_ALLOW_FILE"]).resolve() if _os.environ.get("MW_ALLOW_FILE")
              else (ROOT / "scripts" / "compile-check.allow" if _os.environ.get("MW_REPO_ROOT")
                    else SCRIPTS / "compile-check.allow"))

# Directories that are NOT node repos.
# "WhatsNew" is the satellite release-note lane (MeshWeaver#2539/#2608): a CHANGELOG, never a
# package and never module content. Must stay in step with the other scripts' SKIP sets.
SKIP = {"src", "test", "scripts", "e2e", "app", "WhatsNew", ".git", ".github", ".claude", ".worktrees"}

# The implicit global usings the mesh injects into every compiled NodeType source
# (MeshWeaver.Graph/Configuration/DynamicMeshNodeAttributeGenerator.cs). Faithful reproduction
# needs them because the authored .cs rely on them being present without an explicit `using`.
IMPLICIT_USINGS = [
    "System",
    "System.Collections.Generic",
    "System.ComponentModel",
    "System.ComponentModel.DataAnnotations",
    "System.Linq",
    "System.Reactive.Linq",
    "System.Text.Json.Serialization",
    "MeshWeaver.Mesh",
    "MeshWeaver.Messaging",
    "MeshWeaver.Data",
    "MeshWeaver.Domain",
    "MeshWeaver.Graph",
    "MeshWeaver.Graph.Configuration",
    "MeshWeaver.Layout",
    "MeshWeaver.Layout.Composition",
    "MeshWeaver.Layout.Domain",
    "MeshWeaver.Layout.Views",
    "MeshWeaver.Application.Styles",
    "MeshWeaver.ContentCollections",
    "MeshWeaver.Mesh.Services",
    "Microsoft.Extensions.DependencyInjection",
    "Microsoft.Extensions.Configuration",
]

# Not in the local dll set — the mesh provides it. Dropped from the union; a set that truly needs it
# is flagged UNVERIFIABLE instead of being counted as a compile failure.
EXTERNAL_USINGS = {"Microsoft.Extensions.AI"}


# ── discovery ────────────────────────────────────────────────────────────────────────────────────

def is_nodetype(node: object) -> bool:
    # Tolerate any JSON root — a checkout with clients/react/node_modules carries list-root files
    # (dayjs/locale.json …) and discovery must skim past them, not crash (2026-08-29).
    if not isinstance(node, dict):
        return False
    content = node.get("content") or {}
    return content.get("$type") == "NodeTypeDefinition" or node.get("nodeType") == "NodeType"


def node_dir(json_path: Path) -> Path:
    """The node's folder — a node with children exports as X/index.json, so its folder IS X/."""
    return json_path.parent if json_path.name == "index.json" else json_path.with_suffix("")


def discover_nodetypes(root: Path):
    repos = [d for d in root.iterdir() if d.is_dir() and d.name not in SKIP]
    out = []
    for repo in sorted(repos):
        for f in sorted(repo.rglob("*.json")):
            if "node_modules" in f.parts:
                continue
            try:
                node = json.loads(f.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                continue
            if is_nodetype(node):
                out.append((f, node))
    return out


# ── scope: the atomic unit the caller asks for ───────────────────────────────────────────────────

def known_packages(root: Path) -> list:
    """Every PACKAGE: a top-level folder carrying an `index.json` — the node-repo package
    convention (`node-repo-scope.py`, `affected-modules.py`), NOT every non-SKIP folder
    `discover_nodetypes` walks. A checkout also carries `clients/`, `tools/`, `devtools/` … and
    accepting one of those as "known" would let a caller name a non-package, compile zero
    NodeTypes and read a green verdict (Copilot on MeshWeaver#4052)."""
    return sorted(d.name for d in root.iterdir()
                  if d.is_dir() and d.name not in SKIP and (d / "index.json").is_file())


def parse_modules(arg: "str | None", known) -> tuple:
    """Turn `--modules A,B,C` into (sorted selected ids | None for full, error | None).

    🚨 Two refusals, both on purpose. An EMPTY list (`--modules ''`, `--modules ,`) is not "compile
    nothing" — a caller whose selection step produced no ids has a broken selector, and a green
    verdict over zero types is indistinguishable from a green verdict over the unit. An UNKNOWN id
    is refused by name: the ids come from another script's output, so a typo, a renamed package or
    a package deleted on the branch under test would otherwise silently narrow the unit to whatever
    else was in the list."""
    if arg is None:
        return None, None
    ids = [p.strip() for p in arg.split(",")]
    ids = [p for p in ids if p]
    if not ids:
        return None, ("--modules names no package — an empty selection is a broken selector, "
                      "not 'nothing to compile'. Pass the unit's package ids, or omit the flag "
                      "for a full run.")
    known_set = set(known)
    unknown = sorted(set(ids) - known_set)
    if unknown:
        return None, (f"--modules names {len(unknown)} unknown package(s): {', '.join(unknown)} "
                      f"— not a top-level node repo folder under {ROOT} "
                      f"(known: {', '.join(known) or '(none)'})")
    return sorted(set(ids)), None


def package_of(name: str) -> str:
    """The package (top-level folder) a node path or allow-file entry belongs to."""
    return name.split("/", 1)[0]


def in_scope(types, selected) -> list:
    """The discovered (json_path, node) pairs whose package is selected; all of them for None."""
    if selected is None:
        return list(types)
    sel = set(selected)
    return [(p, n) for p, n in types if package_of(p.relative_to(ROOT).as_posix()) in sel]


def scope_allow(allow: dict, selected) -> dict:
    """The allow-file entries the ratchet may judge on this run: only those inside the scope.

    An out-of-scope entry was NOT compiled here, so it can be neither reported as "now compiles —
    remove it" (it was never built) nor as a ghost (its type was never discovered). Judging it
    would make every narrowed leg red on debt that belongs to another unit."""
    if selected is None:
        return dict(allow)
    sel = set(selected)
    return {n: fp for n, fp in allow.items() if package_of(n) in sel}


def evaluate_gate(result: dict, allow: dict, node_set: dict) -> dict:
    """The ratchet, as a pure function of (what compiled, what is allowlisted, what was discovered).

    `result`   node-path -> (status, errors) for every type that was compiled on THIS run;
    `allow`    the allow-file entries ALREADY scoped to this run (see `scope_allow`);
    `node_set` node-path -> source set for every type discovered on this run."""
    clean = sorted(n for n, (st, _) in result.items() if st == "ok")
    unverifiable = sorted(n for n, (st, _) in result.items() if st == "unverifiable")
    failing = {n for n, (st, _) in result.items() if st == "fail"}
    allow_names = set(allow)
    new_breaks = sorted(failing - allow_names)
    # allowlisted-and-still-failing: split into genuine known-debt (fingerprint matches) vs.
    # fingerprint-drift (allowlisted, but a NEW/changed error the entry does not cover → gate FAIL).
    known_debt, fp_drift = [], []
    for n in sorted(failing & allow_names):
        cur_fp = failure_fingerprint(result[n][1])
        exp_fp = allow[n]
        if exp_fp is not None and cur_fp != exp_fp:
            fp_drift.append((n, exp_fp, cur_fp))
        else:
            known_debt.append(n)
    stale_allow = sorted(n for n in allow_names if result.get(n, (None,))[0] == "ok")
    # allowlisted types that vanished from discovery (renamed/removed) — warn, don't gate
    ghosts = sorted(n for n in allow if n not in node_set)
    return {"clean": clean, "unverifiable": unverifiable, "failing": sorted(failing),
            "new_breaks": new_breaks, "known_debt": known_debt, "fp_drift": fp_drift,
            "stale_allow": stale_allow, "ghosts": ghosts}


def write_report(path: Path, verdict: dict, result: dict, *, gate_fail: bool, scope, elapsed: float) -> None:
    """The verdict as MACHINE-READABLE JSON — for a caller that has to relay it, not read it.

    core's `satellite-compat` lane (MeshWeaver, 2026-09-12) runs this gate once per satellite on
    every platform build, inside a matrix of reusable-workflow calls. A matrix `uses:` job has no
    steps of its own and its outputs overwrite each other leg by leg, so the ONLY way the run's
    verdict and the `ci-failure` issue can name WHICH satellite broke and on WHICH NodeTypes is a
    per-leg artifact — and an artifact is worth exactly what the file inside it says. So the file
    carries the same verdict the console prints, keyed the same way, with the FIRST error line of
    every failing type: enough for a triage line, never a second judgement. Written before the
    exit code is decided so a red run has a report too; a run that dies before this point leaves
    no file, and the consumer treats "no report" as "not measured" rather than as clean."""
    first = lambda n: (result.get(n, (None, []))[1] or ["(no CS error captured)"])[0]
    payload = {
        # WHAT was judged, so the verdict is a fact about one tree and not "the satellite":
        # the lane sets these to the checked-out repository and the commit it resolved. A ref
        # like `main` moves; the sha in the report is what the verdict is about.
        "content": {"repository": os.environ.get("MW_CONTENT_REPOSITORY", ""),
                    "sha": os.environ.get("MW_CONTENT_SHA", "")},
        "gate_fail": bool(gate_fail),
        "scope": "full" if scope is None else list(scope),
        "types": len(result),
        "elapsed_seconds": round(elapsed, 1),
        "clean": len(verdict["clean"]),
        # By NAME as well as by count: compat-verdict.py asks "did this type compile against the
        # PREVIOUS image?", and a count cannot answer for one type.
        "ok": list(verdict["clean"]),
        "known_debt": [{"type": n, "error": first(n)} for n in verdict["known_debt"]],
        "new_breaks": [{"type": n, "error": first(n)} for n in verdict["new_breaks"]],
        "fp_drift": [{"type": n, "expected": e, "actual": a, "error": first(n)} for n, e, a in verdict["fp_drift"]],
        "stale_allow": list(verdict["stale_allow"]),
        "unverifiable": list(verdict["unverifiable"]),
        "ghosts": list(verdict["ghosts"]),
    }
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")


# ── source resolution (mirror the mesh's three source queries) ─────────────────────────────────────

def resolve_spec(spec: str, nd: Path, root: Path):
    """Resolve one `sources` entry to a directory (or None). Handles the forms in this repo:
       `namespace:Source scope:subtree`, `shared=namespace:UWDeepfield/Source scope:subtree`,
       `client=namespace:.../Source scope:subtree`, `shared=@Edu/CourseInvite/Source`, `@X/Y/Source`."""
    spec = spec.strip()
    # strip an `alias=` prefix (shared= / client= / catscen= …) in front of a namespace:/@ value
    m = re.match(r"^([A-Za-z][A-Za-z0-9]*)=(.*)$", spec)
    if m and (m.group(2).startswith("@") or m.group(2).startswith("namespace:")):
        spec = m.group(2)
    if spec.startswith("@"):
        target = root / spec[1:].strip()
        # The mesh resolves a single-node shorthand to BOTH an exact-path match and a namespace-
        # subtree match (NodeTypeDefinition.Sources), so `shared=@X/Source/OneFile` names ONE Code
        # node. On disk that node is `OneFile.cs`; a directory of that name is the subtree form.
        # Without this branch a single-file share resolved to a non-existent directory and the
        # consumer failed CS0246 locally while the mesh compiled it fine (Plugins#777's
        # Hosting/PlatformBuildInbox → shared=@Hosting/Deployment/Source/PlatformBuildInboxWatcher).
        if not target.is_dir() and target.with_suffix(".cs").is_file():
            return target.with_suffix(".cs")
        return target
    m = re.match(r"^namespace:(\S+)", spec)
    if m:
        val = m.group(1)
        absolute = root / val            # absolute node path (e.g. UWDeepfield/Source)
        if absolute.is_dir():
            return absolute
        return nd / val                  # relative to the node (e.g. `Source` → <node>/Source)
    return None


def resolve_sources(json_path: Path, node: dict, root: Path) -> frozenset:
    nd = node_dir(json_path)
    repo = json_path.relative_to(root).parts[0]
    content = node.get("content") or {}
    sources = content.get("sources")
    dirs = []
    if not sources:
        # default (sources == null): own Source subtree ONLY — this matches the mesh exactly.
        # 🚨 Do NOT re-add the partition-root Source here: the gate used to include it, which let
        # 28 NodeTypes compile green locally while failing on-mesh with CS0246 (2026-07-24) — the
        # mesh resolves nothing beyond the declared `sources`. A type that needs the partition
        # model must DECLARE it (e.g. "shared=@<Plugin>/Source").
        dirs.append(nd / "Source")
    else:
        for spec in sources:
            if not isinstance(spec, str):
                continue
            d = resolve_spec(spec, nd, root)
            if d is not None:
                dirs.append(d)
    dirs.append(nd / "Test")             # the mesh ALWAYS also queries the type's own Test subtree
    cs = set()
    for d in dirs:
        if d.is_dir():
            cs.update(str(p.resolve()) for p in d.rglob("*.cs"))
        elif d.is_file() and d.suffix == ".cs":
            cs.add(str(d.resolve()))
    return frozenset(cs)


# ── the configuration lambda (NOT a .cs file — and that is exactly why it broke prod) ──────────────

def config_lambda(node: dict):
    """The NodeType's hub-configuration lambda source, or None.

    🚨 THIS IS CODE, and until 2026-08-09 nothing on earth compiled it. It lives in the node JSON
    (`content.configuration`) rather than in a `.cs` file, so a repo-wide grep finds it but no
    compiler ever sees it: not `dotnet build` (node trees are `<None>` content), and not this gate,
    which resolved only `Source/`+`Test/` subtrees. The portal compiles it at RUNTIME —
    `MeshNodeCompilationService` passes `NodeTypeDefinition.Configuration` to
    `DynamicMeshNodeAttributeGenerator.GenerateAttributeSource`, which drops it into a generated
    `ConfigureHub` method.

    That hole took down all three production portals on 2026-08-09. MeshWeaver's `eafd353ed` deleted
    the `AddTracking()` extension on `MessageHubConfiguration`; `SocialMedia/Post`, `Profile` and
    `PostsHub` each still called it from this field. CI was green — the sibling repo's copy of this
    gate reported 22/22 clean on that very tree — and the break surfaced only on the next framework
    bump, which recompiles every dynamic NodeType at once: `SocialMedia/Post` → CompileError, its
    three dependents → UpstreamFailed, and `DynamicTypePreWarmer: REFUSING READINESS`. 39 of this
    repo's NodeTypes carry such a lambda; none of them had ever been compiled either.

    `Configuration` is the field the dynamic-compile path actually reads; `HubConfiguration` is its
    sibling on the same record, so it is honoured as a fallback rather than silently ignored.
    """
    content = node.get("content") or {}
    for field in ("configuration", "hubConfiguration"):
        value = content.get(field)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return None


def config_check_source(lambda_src: str) -> str:
    """Reproduce the `ConfigureHub` method `DynamicMeshNodeAttributeGenerator` generates around the
    lambda, so Roslyn type-checks it against the SAME surface the mesh will.

    Mirrors `GenerateAttributeSource`: `var result = config;` → `result.AddMeshDataSource();` → the
    user lambda bound to `Func<MessageHubConfiguration, MessageHubConfiguration>` → applied. The
    generated `MeshNodeProviderAttribute` subclass, its `Nodes` property and the optional
    `AddContentCollections(…)` block are deliberately NOT reproduced: all three are built from node
    METADATA (path/name/icon, the `contentCollections` list), never from authored code, so they
    cannot carry a user break — while constructing them here would add ref-set requirements that
    could only produce FALSE failures. The lambda is the authored part, and the lambda is what this
    compiles.

    Global namespace, matching the mesh: node `.cs` files declare no namespace, so their types are
    resolved by simple name from here exactly as they are in the generated file.

    The lambda is emitted VERBATIM at column 0 — deliberately never re-indented. C# raw string
    literals (`\"\"\"…\"\"\"`) strip indentation relative to their closing delimiter, so shifting the
    text would silently change the VALUE of any such string inside the lambda. The mesh drops it in
    unmodified too, so verbatim is both the safe choice and the faithful one."""
    return (
        "// Generated by compile-check.py — reproduces the ConfigureHub method that\n"
        "// DynamicMeshNodeAttributeGenerator.GenerateAttributeSource builds around the NodeType's\n"
        "// `configuration` lambda, so the lambda is type-checked before it can reach a portal.\n"
        "internal static class __MeshWeaverConfigurationCheck\n"
        "{\n"
        "    private static MessageHubConfiguration ConfigureHub(MessageHubConfiguration config)\n"
        "    {\n"
        "        var result = config;\n"
        "        result = result.AddMeshDataSource();\n"
        "        Func<MessageHubConfiguration, MessageHubConfiguration> userConfig =\n"
        f"{lambda_src};\n"
        "        result = userConfig(result);\n"
        "        return result;\n"
        "    }\n"
        "}\n"
    )


# ── reference assemblies ───────────────────────────────────────────────────────────────────────────

def _refuse_if_sibling_stale(core: "Path | str") -> None:
    """Refuse a verdict when the framework checkout is behind its remote.

    Every `src/` project compiles against this checkout, so a stale one silently changes the
    compiler's inputs — and CI checks core out FRESH while a developer machine does not.
    MEASURED 2026-08-29, twice within an hour by two independent sessions:

      * 20 commits behind -> phantom RED: `Ambiguous project name
        'MeshWeaver.Markdown.Collaboration'`, because the project core had DELETED was still on
        disk here as well as in the branch under test.
      * 22 commits behind -> false GREEN: `dotnet build` and this gate both passed while being
        structurally incapable of seeing a dangling ProjectReference to that same deleted
        project. It was reported as gate coverage. `main` then failed 24 of 39 jobs and NOTHING
        published to the registry for ANY package.

    A gate that answers confidently from the wrong inputs is worse than one that refuses.

    It never fetches — a silent network call inside a gate is its own trap, and an unreachable
    remote must not read as "current". When freshness cannot be determined (no git, no
    `origin/main`, path gone) it says so and continues: refusing on "cannot tell" would break
    every offline and container run, and CI is not exposed to this hazard anyway.
    """
    # CI is not exposed to this hazard — it checks the framework out FRESH for every run — and a
    # gate that can refuse there is a gate that can break the build for a reason unrelated to the
    # change under test. So it is a no-op on a runner, by construction rather than by argument.
    if os.environ.get("CI") or os.environ.get("GITHUB_ACTIONS"):
        return
    core = Path(core)
    try:
        r = subprocess.run(["git", "-C", str(core), "rev-list", "--count", "HEAD..origin/main"],
                           capture_output=True, text=True, timeout=15)
    except Exception as exc:
        print(f"  (framework freshness unchecked for {core}: {exc})", file=sys.stderr)
        return
    if r.returncode != 0:
        return                                   # not a git repo / no origin/main — CI's case
    n = r.stdout.strip()
    if n.isdigit() and int(n) > 0:
        sys.exit(
            f"\n\u2717 REFUSING TO RUN: the framework checkout is {n} commit(s) behind its remote.\n"
            f"    {core}\n\n"
            "  Every src/ project compiles against it, so this gate's verdict would be meaningless.\n"
            "  This exact staleness has produced BOTH a phantom failure and a false pass.\n\n"
            f"  Fix:  git -C {core} pull --ff-only origin main\n")



def _report_framework_provenance(refs: dict, ref_roots) -> None:
    """Say WHICH framework this verdict was measured against, on every run.

    🚨 A GATE'S VERDICT IS ONLY MEANINGFUL WITH ITS INPUT. This gate's answer depends on framework
    assemblies that move INDEPENDENTLY of the diff under test, so the same commit can be red and
    then green with nothing changed — and without the input printed, that is indistinguishable
    from "the diagnosis was wrong".

    MEASURED 2026-08-29, and it cost two sessions hours:

      * A core carve-out deleted `CommentsExtensions` from source, but the ACR image still carried
        it inside `MeshWeaver.Graph`. The gate reported `CS0433: exists in both …` — a duplicate
        that existed only in the stale INPUT. It went SUCCESS on the identical diff once a
        post-carve-out image published. Same diff, different input, opposite verdict.
      * A sibling core checkout 22 commits behind produced a false GREEN; one 20 behind produced a
        phantom RED. See `_refuse_if_sibling_stale`.

    The staleness REFUSAL above covers the local checkout, where freshness is knowable. A container
    image's contents cannot be compared to a remote the same way, so the honest guard there is
    provenance: print what was resolved, so a surprising verdict can be checked against its input
    in one line instead of being argued about.
    """
    roots = [str(r) for r in (ref_roots or [])]
    where = roots[0] if roots else "(unknown)"
    digest = os.environ.get("MW_IMAGE_DIGEST") or os.environ.get("MW_TEST_IMAGE") or ""
    line = f"framework: {len(refs)} ref assemblies from {where}"
    if digest:
        line += f"  image={digest}"
    print(line)

# Assemblies that are COMPILER-TIME TOOLING, never a reference: Roslyn source generators and analyzers
# ship beside the real assemblies (the tester image lays the SDK's generators out under
# `sdk-generators/`, MeshWeaver#2905) and they carry INTERNAL copies of the public types they
# generate code for — `System.Text.Json.SourceGeneration.dll` declares its own
# `JsonSerializerDefaults`/`JsonIgnoreCondition`. Handed to the compiler as a reference beside
# `System.Text.Json.dll`, every NodeType naming those types fails CS0433 "exists in both"
# (Reinsurance #145: Ifrs17/Engine, UWDeepfield/TreatyRenewalTracking; Manufacturing run 212:
# 12 of 15 types). A generator is consumed through the analyzer channel, never as a reference.
_TOOLING_DIR_PARTS = {"sdk-generators", "analyzers"}
_TOOLING_NAME_SUFFIXES = (".SourceGeneration.dll", ".Generators.dll", ".Generator.dll",
                          ".Analyzers.dll", ".Analyzer.dll", ".CodeFixes.dll")


def is_reference_assembly(path: str) -> bool:
    """False for a Roslyn generator/analyzer assembly — by its directory or by its name."""
    parts = set(Path(path).parts)
    if parts & _TOOLING_DIR_PARTS:
        return False
    return not os.path.basename(path).endswith(_TOOLING_NAME_SUFFIXES)


def discover_refs(refs_arg):
    """Return ({name.dll: path}, search_roots, layout). If --refs is a dir, glob its *.dll. Else
       auto-discover the sibling core build (Debug preferred, else Release), deduped preferring the
       OWN project dir + newest. `search_roots` is the base dir(s) `discover_ai_refs` then scans for
       the Microsoft.Extensions.AI assemblies (which live under `test/*/bin`, not `src/*/bin`).

       `layout` is which of the two documented shapes the set came from, and it decides what a
       COMPLETE set means (see `short_reference_set_refusal`): `"image"` — a FLAT directory of
       assemblies extracted from the platform image (`--image`'s cache, CI's `refs/`), whose
       contents are CONTAINER-built and therefore need the image's shared frameworks too; or
       `"source-build"` — the per-project `<proj>/bin/<cfg>/net10.0` layout of a core checkout
       built on this host with the SDK, which needs the reference pack and no shared frameworks."""
    layout = "source-build"
    if refs_arg:
        # Prefer the PRECISE per-project output glob (the same clean set auto-discovery uses below):
        # <refs>/<proj>/bin/(Debug|Release)/net10.0/*.dll. A bare recursive **/*.dll also sweeps
        # obj/ intermediates, bin/.../ref/ metadata-only reference assemblies and runtimes/<rid>/
        # copies; on some CI runners that larger set deduped by filename to a mix that OMITTED the
        # real framework assemblies, so every NodeType failed CS0246 'MeshWeaver' could not be found
        # (green locally, red in CI with the SAME ~100 ref count). Fall back to recursive when refs
        # points at a FLAT assemblies dir (no src/*/bin layout), the other documented --refs usage.
        dlls = []
        for cfg in ("Debug", "Release"):
            dlls = glob.glob(os.path.join(refs_arg, "*", "bin", cfg, "net10.0", "*.dll"))
            if dlls:
                break
        if not dlls:
            dlls = glob.glob(os.path.join(refs_arg, "**", "*.dll"), recursive=True)
            layout = "image"        # a flat assemblies dir — the platform-image extraction
        # `--refs <core>/src` is the documented form, so the checkout is its parent.
        _refuse_if_sibling_stale(Path(refs_arg).resolve().parent)
        search_roots = [refs_arg]
    else:
        # Auto-discover the sibling built core. In a worktree ROOT is under `.worktrees/<name>`, so
        # try the main checkout's sibling too, and the canonical ~/code/MeshWeaver.
        candidates = [
            ROOT.parent / "MeshWeaver",                       # standalone checkout sibling
            ROOT.parent.parent / "MeshWeaver",               # from .worktrees/<name>
            ROOT.parent.parent.parent / "MeshWeaver",        # deeper worktree nesting
            Path(os.path.expanduser("~/code/MeshWeaver")),
        ]
        dlls = []
        core_used = None
        for core in candidates:
            for cfg in ("Debug", "Release"):
                dlls = glob.glob(str(core / "src" / "*" / "bin" / cfg / "net10.0" / "*.dll"))
                if dlls:
                    core_used = core
                    break
            if dlls:
                break
        if core_used is not None:
            _refuse_if_sibling_stale(core_used)
        search_roots = [core_used] if core_used else list(candidates)
    excluded = [d for d in dlls if not is_reference_assembly(d)]
    if excluded:
        print(f"refs: excluded {len(excluded)} compiler-tooling assembl(ies) (generators/analyzers are not references): "
              + ", ".join(sorted({os.path.basename(d) for d in excluded})))
    dlls = [d for d in dlls if is_reference_assembly(d)]
    seen = {}
    for dll in dlls:
        n = os.path.basename(dll)
        proj = os.path.basename(os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(dll)))))
        own = (proj + ".dll") == n
        if n not in seen:
            seen[n] = (dll, own)
        else:
            cur_dll, cur_own = seen[n]
            if (own and not cur_own) or (own == cur_own and os.path.getmtime(dll) > os.path.getmtime(cur_dll)):
                seen[n] = (dll, own)
    # Absolutize: refs are written as <HintPath> into a check.csproj that `dotnet build` runs from a
    # TEMP dir, so a RELATIVE path (what a relative `--refs ../MeshWeaver/src` glob yields — the CI
    # invocation) would resolve against that temp dir, load nothing, and every NodeType would fail
    # CS0246 'MeshWeaver' could not be found (green locally with an ABSOLUTE --refs, red in CI). The
    # source .cs files are already resolve()d (see collect); do the same for refs.
    return {n: os.path.abspath(p) for n, (p, _) in seen.items()}, search_roots, layout


# 🚨 A SHORT REFERENCE SET IS REFUSED, NEVER FILLED WITH A GUESS (#4404; MeshWeaver.Plugins#1911),
# the same shape as MODULE_REFS_NOT_IN_IMAGE below, one layer down. Two holes used to be papered
# over silently:
#
#   * NO SHARED FRAMEWORKS. A refs dir extracted from `/app/.` alone never carries
#     System.Private.CoreLib.dll, so the run never entered implementation-framework mode. /app's
#     assemblies are container-built and reference System.Private.CoreLib's identity DIRECTLY,
#     which the SDK reference pack cannot resolve — so a pristine `main` reported 17 NodeTypes
#     broken with CS0012, naming the CONTENT.
#   * NO System.Reactive. The csproj filled that hole with a NuGet version — first a literal that
#     lagged the platform's Rx major (6.1.0 against 7.0.0: 43 of 89 NodeTypes "broken" with
#     CS1705), then a version read from Directory.Packages.props. Even a correctly derived version
#     is a GUESS about what the image carries, and a guess that compiles is a confident answer to a
#     question this gate cannot ask.
#
# Both produce a CONFIDENT WRONG ANSWER that accuses the content, and the natural response is to
# "fix" source that was never broken. A gate that cannot answer must say so. In CI the set is
# always complete (ci.yml copies /app/. AND /usr/share/dotnet/shared/.), so this never fires there;
# it exists for the hand-run that first found #1911.
def short_reference_set_refusal(refs, layout: str, where: str = "the reference set") -> str | None:
    """The message refusing a reference set that cannot answer the question, or None. Pure."""
    if layout == "image" and "System.Private.CoreLib.dll" not in refs:
        return (
            f"error: {where} came out of the platform image but carries NO shared frameworks —\n"
            "  System.Private.CoreLib.dll is missing, so this run would compile against the SDK's\n"
            "  reference pack instead of the assemblies the mesh actually loads.\n"
            "  The image's assemblies are CONTAINER-built and reference System.Private.CoreLib's\n"
            "  identity directly, which the ref pack cannot resolve: every NodeType binding one\n"
            "  fails CS0012 naming the CONTENT, which reads as the repository breaking when the real\n"
            "  fault is a short reference set (#1032, #1911). Refusing to report those as breaks.\n"
            "\n  Refill the cache — fetch-refs.py copies /usr/share/dotnet/shared out of the image\n"
            "  the way ci.yml does:\n"
            "    python3 scripts/fetch-refs.py --force\n"
            "  (a dir filled before #1911 is refilled by itself; --force refills one passed by hand.)")
    if "System.Reactive.dll" not in refs:
        return (
            f"error: {where} carries NO System.Reactive.dll.\n"
            "  Every MeshWeaver assembly binds Rx, so without it the NodeTypes that reach\n"
            "  MeshWeaver.Messaging.Hub / Mesh.Contract / Graph cannot be compiled at all.\n"
            "  This gate does NOT restore a NuGet version to fill the hole: a pin here lagged the\n"
            "  platform's Rx major and reported 43 of 89 NodeTypes broken with CS1705, naming the\n"
            "  CONTENT (#1911), and even a derived version is a guess about what the image carries.\n"
            "\n  Use a complete set instead:\n"
            "    python3 scripts/compile-check.py --image   # the platform image's own Rx")
    return None


# 🚨 THE MODULE ASSEMBLIES THAT LEFT THE PLATFORM IMAGE. These three are registry-served, so the
# pinned image's `/app` does NOT carry them any more (MeshWeaver#2276 moved MeshWeaver.AI out,
# #3175 made Markdown.Collaboration single-producer, and Maps followed). CI tops the reference set
# up from THIS run's module bundles and then asserts each one is present, because the failure mode
# is a misdirection: every NodeType binding a missing module fails with CS0246/CS0103 naming the
# CONTENT, which reads exactly like this repo breaking.
#
# Nothing did that locally, so `--image` — documented as "what CI uses and what actually ships" —
# reported 15 phantom breaks on a pristine checkout of a green `main` (AppleMaps/Gallery,
# Collaboration/Review, Cornerstone/Pricing, Edu/PromptCell, GoogleMaps/Gallery, Hosting/Backup,
# Hosting/Deployment, Hosting/FleetConsole, Hosting/InstanceAction, Hosting/InstanceRequest,
# Hosting/PlatformBuildInbox, MyAi/Panel, OpenStreetMap/Gallery, Providers/ProvidersApp,
# RolePlay/Story). A gate that is red on green main is a gate people learn to ignore.
MODULE_REFS_NOT_IN_IMAGE = ("MeshWeaver.AI", "MeshWeaver.Markdown.Collaboration", "MeshWeaver.Maps")


def discover_module_refs(search_roots, include_repo_build: bool = True):
    """Locate the MODULE assemblies that are no longer in the platform image. CI supplies them in
       the flat `--refs` dir (unpacked from the run's module bundles); locally they come from this
       repo's own `src/<Module>/bin/{Release,Debug}/net10.0/`, since all three are built HERE.
       Returns {name.dll: abspath} for whatever was found — the caller decides what a miss means.
       Prefers Release over Debug, and the newest file within a tier.

       `include_repo_build=False` restricts the search to `search_roots`, so the result is a
       function of the argument alone — what the self-test needs to prove the guard can fire on a
       short reference set even on a machine whose src/ HAS been built."""
    found = {}   # name -> (path, release, mtime)

    def consider(dll, release):
        n = os.path.basename(dll)
        if os.path.splitext(n)[0] not in MODULE_REFS_NOT_IN_IMAGE:
            return
        try:
            mt = os.path.getmtime(dll)
        except OSError:
            return
        cur = found.get(n)
        if cur is None or (release and not cur[1]) or (release == cur[1] and mt > cur[2]):
            found[n] = (dll, release, mt)

    # This repo's own build output first — the three projects live in src/ here.
    if include_repo_build:
        for name in MODULE_REFS_NOT_IN_IMAGE:
            for cfg in ("Release", "Debug"):
                for dll in glob.glob(str(ROOT / "src" / name / "bin" / cfg / "net10.0" / f"{name}.dll")):
                    consider(dll, cfg == "Release")
    # …then anything the reference roots already carry (CI's flat dir, a sibling checkout's bins).
    for root in search_roots:
        if not root:
            continue
        root = Path(root)
        for name in MODULE_REFS_NOT_IN_IMAGE:
            for dll in glob.glob(str(root / f"{name}.dll")):
                consider(dll, True)
            for cfg in ("Release", "Debug"):
                for dll in glob.glob(str(root / "**" / "bin" / cfg / "net10.0" / f"{name}.dll"),
                                     recursive=True):
                    consider(dll, cfg == "Release")
    # Absolutize — these become <HintPath>s in a csproj that `dotnet build` runs from a TEMP dir.
    return {n: os.path.abspath(p) for n, (p, _, _) in found.items()}


def discover_ai_refs(search_roots):
    """Locate the `Microsoft.Extensions.AI*` assemblies the mesh supplies at runtime but that are
       NOT in the core `src/*/bin` set discover_refs globs (they live under the core's `test/*/bin`
       output and in NuGet). Returns {name.dll: path} for net10.0, or {} if none can be found — in
       which case the AI-using sets stay UNVERIFIABLE. Prefers a build-output dll over a NuGet one,
       newest mtime within a tier."""
    found = {}   # name -> (path, from_build_output, mtime)

    def consider(dll, build_output):
        n = os.path.basename(dll)
        try:
            mt = os.path.getmtime(dll)
        except OSError:
            return
        cur = found.get(n)
        if cur is None or (build_output and not cur[1]) or (build_output == cur[1] and mt > cur[2]):
            found[n] = (dll, build_output, mt)

    for root in search_roots:
        if not root:
            continue
        root = Path(root)
        for cfg in ("Debug", "Release"):
            for dll in glob.glob(str(root / "**" / "bin" / cfg / "net10.0" /
                                    "Microsoft.Extensions.AI*.dll"), recursive=True):
                consider(dll, True)
        # a flat --refs directory
        for dll in glob.glob(str(root / "Microsoft.Extensions.AI*.dll")):
            consider(dll, True)
    # NuGet fallback (net10.0) only if nothing was found in the build output
    if not found:
        for dll in glob.glob(os.path.expanduser(
                "~/.nuget/packages/microsoft.extensions.ai*/**/lib/net10.0/"
                "Microsoft.Extensions.AI*.dll"), recursive=True):
            consider(dll, False)
    # Absolutize — same reason as discover_refs: these paths become <HintPath>s in a check.csproj
    # that `dotnet build` runs from a TEMP dir, so a RELATIVE path (what a relative `--refs ../refs`
    # glob yields — the CI invocation) resolves against that temp dir, RAR silently DROPS the
    # reference, and every AI-using set fails CS0234 on the Microsoft.Extensions.AI types
    # (green locally with an absolute --refs, red in CI — MeshWeaver.Reinsurance PR #33 hit
    # exactly this with the flat image ref layout).
    return {n: os.path.abspath(p) for n, (p, _, _) in found.items()}


# ── compile ─────────────────────────────────────────────────────────────────────────────────────
# 🚨 THE MESH COMPILES ONE UNIT PER NODETYPE, AND SO DOES THIS GATE (#4711).
#
# `DynamicMeshNodeAttributeGenerator.GenerateAttributeSource` takes the CONCATENATION of a
# NodeType's sources (`NodeCompileShaping.CombineSources` — joined with a blank line, in node-path
# order), strips every file's `using` lines and re-emits them, deduped, at the top of ONE generated
# file. This gate used to hand each source to MSBuild as its OWN `<Compile>` item with a
# `GlobalUsings.cs` alongside: legal C#, and not what ships.
#
# Under `<Nullable>annotations</Nullable>` — what `EmitPipeline.CreateCompilationOptions` sets and
# what this file mirrors when warnings are errors — that difference is not cosmetic. Nullable
# analysis runs only in text that opted in with `#nullable enable`, and in ONE concatenated unit a
# directive in the FIRST file is still in force in the LAST; file-per-`<Compile>` it reaches
# nothing past its own file. Measured on MeshWeaver.Plugins/BusinessRules/Scope, 2026-09-18, same
# tree both ways: this gate said `98 clean` while the bake said `CS8601 12× / CS8602 12×` — 24
# diagnostics in seven committed `Test/Generated/*Proxy.cs` that carry no `#nullable` of their own
# and inherit one from a file sorting ahead of them. The gate could not see them, and the warning
# ratchet then filed the pair under the NODETYPE's name, where nobody who owns it could reproduce
# it. A gate that is blind in one direction is worse than no gate: it is an alibi.
#
# So the unit is built here the way the mesh builds it, and `ConcatenatedUnitParityTest`
# (test/MeshWeaver.Compiler.Pipeline.Test) compares THIS file's output against
# `DynamicMeshNodeAttributeGenerator.ShapeAuthoredSource` directly. Two careful implementations of
# one fold either never converge or never fire, and both are silent — the same reason
# `check-module-platform-floor.py` is pinned to `NuGetVersionComparer`.
UNIT_FILE = "Combined.cs"


def read_source(path) -> str:
    """One node's `.cs` as the mesh holds it: text, with no byte-order mark.

    🚨 `utf-8-sig` is the FAITHFUL read here, not a convenience. .NET's `Encoding.UTF8` writes a
    BOM and `File.ReadAllText` strips it again, so `CodeConfiguration.Code` never carries one.
    Plain `utf-8` keeps U+FEFF as the file's first CHARACTER — harmless at the head of a
    compilation unit, which is why it never mattered while each file WAS a unit, and not harmless
    now that every file after the first lands in the MIDDLE of one. A BOM written mid-file is
    exactly what produced 25 spurious warnings in MeshWeaver.Plugins#2071."""
    return Path(path).read_text(encoding="utf-8-sig", errors="replace")


def node_path_of(path) -> str:
    """The MESH path of a source file: its path under the repo root, without the `.cs`.

    🚨 The join order is part of what gets compiled — a `#nullable enable` in the first file is in
    force in the last — so the gate orders the way `NodeCompileShaping.CollectCompileSources` does:
    by NODE PATH, ordinal. Sorting file paths instead agrees almost always and not always
    (`A/B.cs` vs `A/B.Extra.cs` sort one way as files and the other as node paths, because `.`
    precedes `c` while a prefix precedes its own extension), and "almost always" is not a
    reproduction."""
    p = Path(path)
    try:
        # BOTH sides resolved: `resolve_sources` hands back resolved paths, and on macOS `/tmp` and
        # `/var` are symlinks into `/private`, so comparing a resolved file against an UNRESOLVED
        # root raises and every file silently falls back to its absolute path — which changes the
        # concatenation ORDER, the one thing this function exists to pin.
        return p.resolve().relative_to(Path(ROOT).resolve()).with_suffix("").as_posix()
    except ValueError:
        return p.with_suffix("").as_posix()


def combined_lines(pieces):
    r"""`CombineSources` plus the `Split('\n')` / `TrimEnd('\r')` the extractor does — with
    provenance.

    `pieces` is [(node path, text)] in node-path order; the result is [(line, (node path, lineno))]
    — every line remembering which authored file and line produced it, so a diagnostic about the
    unit can name a FILE instead of only the NodeType. That is the other half of #4711: the bake
    reported `CS8602 14× / 1 site(s) / 3 type(s)`, named no file, and the count was then attributed
    to content that did not write it. The single blank line between two files is the `"\n\n"`
    join's, and belongs to neither."""
    out = []
    for i, (name, text) in enumerate(pieces):
        if i:
            out.append(("", None))
        for n, raw in enumerate(text.split("\n"), 1):
            out.append((raw.rstrip("\r"), (name, n)))
    return out


def collation_key(text: str) -> str:
    """`text` as .NET's CULTURE-SENSITIVE `StartsWith`/`EndsWith` sees it.

    🚨 This is not a tidy-up, it is the only way to agree with the mesh. `ExtractUsingStatements`
    tests `trimmed.StartsWith("using ")` and `trimmed.EndsWith(";")` through the parameterless
    overloads, which are **linguistic**, not ordinal — and a linguistic comparison gives Unicode
    FORMAT characters (category Cf: U+FEFF, U+200B–U+200F, U+00AD, U+2060 …) no collation weight at
    all. `Trim()` does NOT remove them (they are not whitespace to `char.IsWhiteSpace`), so they
    survive into `trimmed` and the comparison matches straight through them.

    Measured against the generator itself, 2026-09-18 — each of U+FEFF, U+200B, U+200E, U+00AD and
    U+2060 in front of a `using` line: `StartsWith("using ")` is **True** culture-sensitively and
    **False** ordinally, the directive IS hoisted, and it is emitted VERBATIM with the character
    still on it. Python's `str.startswith` is ordinal and has no such overload, so a faithful
    reproduction has to strip the ignorable characters FOR THE COMPARISON and keep them in the
    emitted text.

    This mattered immediately: seven committed scope proxies in MeshWeaver.Plugins carried a
    mid-file U+FEFF (their generator wrote one; #2071 fixed it), and an ordinal reproduction
    reported a CS1529 the mesh would never have produced — a FALSE RED invented by the gate, on
    content that was fine. `ConcatenatedUnitParityTest` caught it before this landed, which is what
    that test is for."""
    return "".join(c for c in text if unicodedata.category(c) != "Cf")


def extract_using_statements(lines):
    """`DynamicMeshNodeAttributeGenerator.ExtractUsingStatements`, line for line.

    Takes the provenance-carrying lines and returns (the using LINES as written, the remaining code
    lines with their provenance). The rules are the mesh's, including the ones that look odd: the
    block-comment flag runs over the WHOLE concatenation, a `using var` is a statement and stays,
    and a directive containing `(` stays. Reproducing them rather than writing a better parser is
    the point — a better parser would disagree with what ships.

    The prefix/suffix tests run on the `collation_key`, because the mesh's run on a linguistic
    comparison; `Contains("(")` does NOT, because .NET's `string.Contains` is ordinal. Getting that
    split wrong in either direction invents a diagnostic or hides one."""
    usings, code = [], []
    in_comment_block = False
    for pair in lines:
        text = pair[0]
        trimmed = text.strip()
        key = collation_key(trimmed)
        if key.startswith("/*"):
            in_comment_block = True
        if key.endswith("*/"):
            in_comment_block = False
            code.append(pair)
            continue
        if in_comment_block:
            code.append(pair)
            continue
        if (key.startswith("using ")
                and not key.startswith("using var ")
                and key.endswith(";")
                and "(" not in trimmed):
            usings.append(text)
        else:
            code.append(pair)
    return usings, code


def shape_authored_source(pieces):
    """This file's half of `DynamicMeshNodeAttributeGenerator.ShapeAuthoredSource`:
    (import lines, code lines with provenance).

    The import block is `IMPLICIT_USINGS` (the mesh's `StandardUsings`) followed by every authored
    directive, TRIMMED and deduped against the standard set and against each other — the mesh's own
    dedup, which is what keeps CS0105 off content that correctly imports the same namespace in
    three files.

    🚨 Note what the hoist now IS: a MOVE, not a copy. The old shape left each file's directives in
    place and added a `GlobalUsings.cs` beside them, so an ALIAS had to be dropped from the union
    (a duplicate `using X = Y;` is CS1537, and it failed Hosting/Issue) — leaving a sibling file
    and the configuration lambda unable to see an alias the mesh DOES hoist. With the directives
    actually removed from the code, an alias hoists exactly as the mesh hoists it: that known
    divergence is closed rather than documented."""
    usings, code = extract_using_statements(combined_lines(pieces))
    imports = [f"using {ns};" for ns in IMPLICIT_USINGS]
    already = set(imports)
    for directive in usings:
        trimmed = directive.strip()
        if trimmed and trimmed not in already:
            already.add(trimmed)
            imports.append(trimmed)
    return imports, code


def names_external(directive: str) -> bool:
    """True iff a hoisted `using` line imports a namespace the local reference set may not carry.

    The mesh hoists it either way, so the directive stays in the unit; this only decides whether a
    failure ENTIRELY attributable to the missing assembly may be reported UNVERIFIABLE instead of
    as a break (see `_is_ai_error`)."""
    body = collation_key(directive.strip())
    if not body.startswith("using "):
        return False
    body = body[len("using "):].strip().rstrip(";").strip()
    if body.startswith("static "):
        body = body[len("static "):].strip()
    if "=" in body:
        body = body.split("=", 1)[1].strip()
    return body in EXTERNAL_USINGS or body.startswith("Microsoft.Extensions.AI")


def build_unit(cs_files, lambda_src):
    """The ONE compilation unit for a source set, plus the line -> origin map.

    Returns (text, origins, imports), where `origins[i]` is the `(node path, line)` that produced
    unit line `i + 1`, or None for a line this file generated itself."""
    pieces = [(node_path_of(f), read_source(f))
              for f in sorted(cs_files, key=node_path_of)]
    imports, code = shape_authored_source(pieces)

    out = []                                        # (text, origin)

    def emit(text):
        out.append((text, None))

    emit("// Generated by compile-check.py — ONE compilation unit, shaped the way")
    emit("// DynamicMeshNodeAttributeGenerator.GenerateAttributeSource shapes the mesh's:")
    emit("// every source concatenated in node-path order, `using` directives hoisted and deduped.")
    emit("")
    for directive in imports:
        emit(directive)
    emit("")
    # The mesh emits `[assembly: …MeshNodeProvider]` and the generated provider class here. Both
    # are built from node METADATA (path/name/icon, the contentCollections list) and never from
    # authored code, so neither can carry a user break — while constructing them would add ref-set
    # requirements that could only produce FALSE failures. Their absence shifts line numbers
    # against the mesh's own generated file and nothing else: `origins` maps a diagnostic back to
    # authored text using THIS file's line numbering, never the mesh's.
    if any(text.strip() for text, _ in code):
        emit("// User-defined types")
        out.extend(code)
        emit("")
    if lambda_src:
        # The configuration lambda type-checks LAST, where the mesh's generated `ConfigureHub`
        # sits — so it inherits whatever nullable context the last authored file leaves in force,
        # which is what the mesh does to it too.
        for line in config_check_source(lambda_src).split("\n"):
            emit(line)
    return "\n".join(t for t, _ in out) + "\n", [o for _, o in out], imports


# 🚨 OPT-IN, and OFF is today's behaviour byte for byte. A repo turns this on only once its
# in-mesh warning debt is actually paid (MeshWeaver.Plugins, 2026-09-17: 87 sites fixed,
# plugin-gate-warnings.allow emptied of every code but the two centrally-suppressed families).
# Defaulting it on would red every satellite that has not, on a gate it cannot pass — which is the
# one thing a ratchet must never do.
WARNINGS_AS_ERRORS = False

# The in-mesh compile's NoWarn when warnings are errors — the PARITY list, and nothing else. It is
# CompileWarning.NotReported spelled for MSBuild, minus CS1701/CS1702 which the SDK's own
# Microsoft.NET.Sdk.CSharp.props already seeds into $(NoWarn) and which this file inherits by
# appending rather than replacing.
PARITY_NOWARN = "CS1591;CS1573;CS1712"

# 🚨 The LENIENT list is NOT a smaller parity list — it is the old behaviour, kept verbatim so that
# leaving the flag off cannot change a verdict. It silences CS1998 (an async method with no await)
# in the one tree where `async` is a hard architectural ban, CS0618 ([Obsolete] use) in the one tree
# whose cellSurface retirement policy depends on those being noticed, and three nullable codes. That
# is exactly why the flag exists.
LENIENT_NOWARN = "$(NoWarn);CS1591;CS1998;CS8618;CS8602;CS8604;CS0618"

ANALYZERS_OFF = """    <!-- 🚨 The MESH runs NO analyzers. `EmitPipeline` builds a bare CSharpCompilation — no
         analyzer references, no analysis level — so an analyzer diagnostic here is a property of
         THIS csproj and cannot exist in production. Measured on MeshWeaver.Plugins: with warnings
         as errors and analyzers left on, CA1416 (platform compatibility) failed 31 of 98 NodeTypes
         with "'HttpClient' is only supported on: 'linux'", because the reference set IS the Linux
         image's implementation assemblies and the analyzer reads their SupportedOSPlatform
         attributes. Exactly the CS1701 shape: a reference-set artefact filed under the content's
         name, unpayable by any author. -->
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <AnalysisLevel>none</AnalysisLevel>
    <RunAnalyzers>false</RunAnalyzers>
"""


def build_csproj(work: Path, ref_xml: str, impl_frameworks: bool = False) -> str:
    # 🚨 EXACTLY ONE `<Compile>`, and that is the fix for #4711 — see UNIT_FILE. A csproj with one
    # item per source file is a DIFFERENT COMPILATION from the one the mesh runs: `#nullable`,
    # `#pragma warning`, `#define` and the file-scoped `using` scope are all per-FILE in C#, and the
    # mesh has exactly one file. `build_unit` does the concatenation the generator does; this
    # declares the result.
    compiles = f'    <Compile Include="{UNIT_FILE}" />'
    # 🚨 IMPLEMENTATION-framework mode: when the refs dir carries the platform image's shared
    # frameworks (System.Private.CoreLib present), compile EXACTLY the way the mesh does — against
    # implementation assemblies, no SDK ref pack. The ref pack cannot resolve container-built
    # module assemblies (mw-plugin-test build-project compiles against /app, so its output
    # references System.Private.CoreLib's identity DIRECTLY, not System.Runtime's) — 11 NodeTypes
    # failed CS0012 'System.Private.CoreLib not referenced' on Plugins#1032 the moment the floor
    # bundles were container-built, while the portal compiled the same nodes fine. AspNetCore.App
    # and System.Reactive come from the same refs dir in this mode.
    impl_props = ("    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>\n"
                  if impl_frameworks else "")
    # 🚨 THIS FILE NEVER RESTORES System.Reactive FROM NuGet — in ANY mode (#4404, after
    # MeshWeaver.Plugins#1911). Rx comes from the reference set, full stop: `ref_xml` declares the
    # refs dir's own `System.Reactive.dll` with a HintPath, and that is the exact assembly the
    # platform image runs. A version literal written here was CS1705 BY CONSTRUCTION the day the
    # platform moved to Rx 7, on every NodeType whose set binds MeshWeaver.Messaging.Hub /
    # Mesh.Contract / Graph:
    #
    #   CS1705: Assembly 'MeshWeaver.Messaging.Hub' … uses 'System.Reactive, Version=7.0.0.0' which
    #           has a higher version than referenced assembly 'System.Reactive' … '6.1.0.0'
    #
    # and the message named THIS REPO'S CONTENT ("13 NodeTypes breaking") rather than the reference
    # set. So there is no fallback to restore: a refs dir WITHOUT `System.Reactive.dll` — or, in
    # image layout, without `System.Private.CoreLib.dll` — is REFUSED by
    # `short_reference_set_refusal` in `main` before any set is compiled, naming the refill command.
    # Do not reintroduce a version literal "for the case the refs dir has none": that case exits
    # before reaching this line, and a pin is a second place to forget that the platform moved.
    framework_items = ("" if impl_frameworks else
                       '    <FrameworkReference Include="Microsoft.AspNetCore.App" />\n')
    # 🚨 `annotations`, NOT `enable`, when warnings are errors — and this is the difference between
    # a gate and a false red. The MESH compiles with NullableContextOptions.Annotations
    # (EmitPipeline.CreateCompilationOptions), so nullable ANALYSIS runs only in files that opt in
    # with `#nullable enable`. `enable` here turns it on for every file, so the gate would report
    # CS8618/CS8602 sites that do not exist in production. The lenient path keeps `enable` because
    # it also NoWarns those three codes, which is how the divergence went unnoticed.
    nullable = "annotations" if WARNINGS_AS_ERRORS else "enable"
    wae = "true" if WARNINGS_AS_ERRORS else "false"
    analyzers = ANALYZERS_OFF if WARNINGS_AS_ERRORS else ""
    nowarn = f"$(NoWarn);{PARITY_NOWARN}" if WARNINGS_AS_ERRORS else LENIENT_NOWARN
    # Doc diagnostics are only PRODUCED with the documentation file on — the mesh parses with
    # DocumentationMode.Diagnose, so a gate that leaves this off cannot see a broken cref at all.
    docfile = "true" if WARNINGS_AS_ERRORS else "false"
    return f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <!-- Disable the SDK's implicit usings: the MESH compiler injects ONLY the explicit prelude
         (IMPLICIT_USINGS, lifted from DynamicMeshNodeAttributeGenerator) plus the authored
         `using`s the unit hoists. `ImplicitUsings=enable` would auto-add System.IO / System.Net.Http /
         System.Threading.Tasks / System.Text / … that the mesh does NOT inject — a source missing a
         `using System.IO;` would compile here but fail on the mesh (a false PASS). -->
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <Nullable>{nullable}</Nullable>
{analyzers}    <TreatWarningsAsErrors>{wae}</TreatWarningsAsErrors>
    <NoWarn>{nowarn}</NoWarn>
    <GenerateDocumentationFile>{docfile}</GenerateDocumentationFile>
    <RestoreSources>$(RestoreSources);https://api.nuget.org/v3/index.json</RestoreSources>
{impl_props}  </PropertyGroup>
  <ItemGroup>
{framework_items}{compiles}
  </ItemGroup>
  <ItemGroup>
{ref_xml}
  </ItemGroup>
</Project>'''


def compile_set(work: Path, cs_files, ref_xml: str, restored: bool, ai_available: bool,
                lambda_src: str | None = None, impl_frameworks: bool = False):
    """Return (errors, needs_external, infra_error).

    `infra_error` is set when `dotnet build` exits NONZERO but produced NO `CS` diagnostic — a
    restore/SDK/MSBuild failure (NU… / NETSDK… / MSB… codes). Those must FAIL the set, not pass it:
    an unavailable restore or broken ref set otherwise leaves `errors` empty and the set reads OK,
    silently letting anything through. We capture a representative non-CS line as the reason.

    `lambda_src` is the NodeType's `configuration` lambda (see `config_lambda`). When present it is
    compiled ALONGSIDE the sources, in the same compilation AND the same FILE, exactly as the mesh
    does — the sources define the types the lambda names, so the two only type-check together, and
    the lambda sits last so it inherits whatever `#nullable` context the sources leave in force."""
    unit, origins, imports = build_unit(cs_files, lambda_src)
    needs_external = (not ai_available) and any(names_external(d) for d in imports)
    # One file, rewritten per set — so no stale text from the previous set can survive, which the
    # old shape needed an explicit unlink() for.
    (work / UNIT_FILE).write_text(unit, encoding="utf-8")
    (work / "check.csproj").write_text(
        build_csproj(work, ref_xml, impl_frameworks=impl_frameworks), encoding="utf-8")
    cmd = ["dotnet", "build", "check.csproj", "--nologo", "-v", "q"]
    if restored:
        cmd.insert(2, "--no-restore")
    proc = subprocess.run(cmd, cwd=work, capture_output=True, text=True)
    out = proc.stdout + proc.stderr
    # One entry per DISTINCT diagnostic (code + message), carrying the AUTHORED FILE AND LINE of its
    # first site — the attribution #4711 is half about. MSBuild reports against `Combined.cs`, whose
    # line numbers are this file's; `origins` maps them back to the node path the text came from.
    #
    # 🚨 The dedup KEY deliberately excludes the origin. `failure_fingerprint` counts these entries,
    # and every allow-file line in the fleet pins a count taken under the old key — keying per site
    # would turn one CS8602 into twelve and red every baselined type at once, which is a ratchet
    # change wearing an attribution change's clothes.
    by_key = {}
    for line in out.splitlines():
        m = re.search(r"error (CS\d+): (.*?)(?: \[|$)", line)
        if not m:
            continue
        key = f"{m.group(1)}: {m.group(2).strip()}"
        if key in by_key:
            continue
        loc = re.search(r"\((\d+),\d+\): error CS", line)
        origin = None
        if loc:
            index = int(loc.group(1)) - 1
            if 0 <= index < len(origins):
                origin = origins[index]
        by_key[key] = f"{key}  [{origin[0]}.cs:{origin[1]}]" if origin else key
    errors = list(by_key.values())
    infra_error = None
    if proc.returncode != 0 and not errors:
        # nonzero exit with no CS diagnostic ⇒ restore/SDK/MSBuild failure. Capture the first
        # actionable non-CS diagnostic (NU/NETSDK/MSB/error) so the failure names its cause.
        for line in out.splitlines():
            m = re.search(r"error ((?:NU|NETSDK|MSB)\w*\d*|[A-Z]+\d+): (.*?)(?: \[|$)", line)
            if m:
                infra_error = f"{m.group(1)}: {m.group(2).strip()}"
                break
        if infra_error is None:
            for line in out.splitlines():
                if re.search(r"\berror\b", line, re.IGNORECASE):
                    infra_error = line.strip()[:200]
                    break
        if infra_error is None:
            infra_error = f"dotnet build exited {proc.returncode} with no diagnostic captured"
    return errors, needs_external, infra_error


# ── diagnostics classification ─────────────────────────────────────────────────────────────────

def _is_ai_error(err: str) -> bool:
    """True iff a diagnostic is attributable to the absent Microsoft.Extensions.AI assembly (used
       only when the AI dlls could not be located — see usings_union)."""
    return ("Microsoft.Extensions.AI" in err
            or "namespace name 'AI'" in err
            or "'AI' does not exist" in err)


# ── allowlist ────────────────────────────────────────────────────────────────────────────────────

def failure_fingerprint(errors) -> str:
    """A stable fingerprint of a set's failure: the sorted multiset of error codes (e.g.
       `CS0121:1` or `CS0246:2,CS8602:1`). Pins WHAT fails, so a NEW/changed error inside an
       already-allowlisted type is detected even though the type stays allowlisted."""
    from collections import Counter
    codes = Counter()
    for e in errors:
        if not e:
            continue
        head = e.split(":", 1)[0].strip()
        codes[head] += 1
    return ",".join(f"{c}:{n}" for c, n in sorted(codes.items()))


def read_allow():
    """Return {name: fingerprint-or-None}. Line format: `Type/Name  fp=<fingerprint>  # comment`.
       A missing `fp=` (legacy line) maps to None → fingerprint check is skipped for that entry."""
    if not ALLOW_FILE.exists():
        return {}
    out = {}
    for line in ALLOW_FILE.read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        parts = line.split()
        name = parts[0]
        fp = None
        for p in parts[1:]:
            if p.startswith("fp="):
                fp = p[len("fp="):]
        out[name] = fp
    return out


# ── main ───────────────────────────────────────────────────────────────────────────────────────

def _self_test() -> int:
    """The shaping of the ONE compilation unit, against what the mesh actually builds (#4711).

    Every assertion here is about a property that, when it was wrong, produced a CONFIDENT GREEN
    rather than an error: the gate compiled a different program from the portal and said the
    content was clean. The using-directive rules are asserted directly because a shape bug in them
    is invisible to any compile run — it reappears as a diagnostic on, or a missing diagnostic
    about, something else entirely.
    """
    import tempfile

    failures = []

    # ── the extractor, on the shapes that actually occur in node source ────────────────────────
    # A directive is MOVED to the top of the unit (it is deleted from the code); a `using`
    # STATEMENT and a commented-out line stay exactly where they are.
    hoist_cases = [
        # line                                                          hoisted?
        ("using MeshWeaver.Data;",                                      True),
        ("    using MeshWeaver.Layout;",                                True),
        ("using static MeshWeaver.Layout.Controls;",                    True),
        ("using static MeshWeaver.ContentCollections.ContentCollectionsExtensions;", True),
        # 🚨 An ALIAS now hoists, and that is the point of the move-not-copy shape: while
        # GlobalUsings.cs sat ALONGSIDE source that still declared it, hoisting one was CS1537 and
        # failed Hosting/Issue — so aliases were dropped and a sibling file could not see one the
        # mesh DOES hoist. The mesh hoists it; so does this.
        ("using Snap = IssueDetectors.StatusSnapshot;",                 True),
        ("using LayoutOption=MeshWeaver.Layout.Option;",                True),
        # NOT directives.
        ("using var stream = File.OpenRead(path);",                     False),
        ("        using var scope = svc.CreateScope();",                False),
        # 🚨 The `using var` STATEMENT with NO PARENTHESES — the one shape the `using var ` guard
        # is the ONLY thing standing between and a hoist. Every other statement here also carries a
        # `(`, so a table without this line passes with the guard DELETED: it asserts the paren
        # check twice and the guard not at all. Hoisting it yanks the variable out of its method
        # (CS8805/CS0103), which is the mesh's own recorded reason for the branch.
        ("            using var handle = state.Handle;",                False),
        ("// using MeshWeaver.Fake;",                                   False),
        ("using (var scope = svc.CreateScope()) { }",                   False),
    ]
    # 🚨 A Unicode FORMAT character in front of the directive — the byte-order mark a generator
    # writes mid-file, and its relatives. `Trim()` does not remove them on either side, but the
    # mesh's prefix test is LINGUISTIC and matches straight through them, so the directive IS
    # hoisted. An ordinal reproduction invents a CS1529 on content the portal compiles: measured on
    # seven committed scope proxies in MeshWeaver.Plugins before `collation_key` existed.
    hoist_cases += [(chr(c) + "using MeshWeaver.Data;", True)
                    for c in (0xFEFF, 0x200B, 0x200E, 0x00AD, 0x2060)]
    # …and a WHITESPACE character is trimmed away by both, which is a different mechanism with the
    # same outcome — listed so the two are not conflated.
    hoist_cases += [(chr(c) + "using MeshWeaver.Data;", True) for c in (0x0009, 0x0085, 0x00A0)]
    for line, hoisted in hoist_cases:
        usings, code = extract_using_statements([(line, ("F", 1))])
        got = bool(usings)
        if got != hoisted:
            failures.append(f"  {line!r}: hoisted={got!r}, expected {hoisted!r}")
        if got == bool(code):
            failures.append(f"  {line!r}: a line must land in EXACTLY one of usings/code")
    # 🚨 …and the character STAYS on the hoisted line. The mesh emits `userUsing.Trim()`, and
    # `Trim()` is not linguistic — so the import block carries a `<U+FEFF>using …;` line verbatim,
    # and the ordinal dedup therefore treats it as a DIFFERENT import from the clean spelling.
    # Normalising it here would be tidier and would disagree with what ships.
    marked = "\ufeffusing MeshWeaver.SomethingElse;"
    usings, _ = extract_using_statements([(marked, ("F", 1))])
    if usings != [marked]:
        failures.append(f"  a hoisted directive must be emitted VERBATIM, got {usings!r}")
    imports, _ = shape_authored_source([("F", marked)])
    if imports[-1] != marked:
        failures.append(f"  the import block must carry the mark verbatim, got {imports[-1]!r}")

    # A block comment carries a `using` line through verbatim — the mesh's flag, spanning files.
    usings, code = extract_using_statements(
        [(t, ("F", i)) for i, t in enumerate(["/*", "using MeshWeaver.Gone;", "*/"], 1)])
    if usings:
        failures.append(f"  a `using` inside a block comment was hoisted: {usings!r}")

    # ── THE CONTROL: one unit, so file 1's `#nullable enable` is in force in file N ────────────
    # This is the defect. Under file-per-`<Compile>` the directive reached nothing past its own
    # file, `<Nullable>annotations</Nullable>` therefore ran no nullable analysis on the others,
    # and the gate reported clean over source the bake then failed.
    with tempfile.TemporaryDirectory() as tmp:
        fixture = Path(tmp)
        srcdir = fixture / "Pkg" / "T" / "Source"
        srcdir.mkdir(parents=True)
        (srcdir / "A.cs").write_text(
            "#nullable enable\nusing MeshWeaver.Data;\npublic record A(string Name);\n",
            encoding="utf-8")
        # Sorts AFTER A by node path, declares no `#nullable` of its own — the generated-proxy
        # shape that produced the 24 unseen diagnostics on BusinessRules/Scope.
        (srcdir / "Z.cs").write_text(
            "public class Z\n{\n    public string? S;\n    public int L => S.Length;\n}\n",
            encoding="utf-8")
        # A BOM'd file, which is what .NET writes: concatenated, its U+FEFF would land MID-UNIT.
        (srcdir / "M.cs").write_text("public record M();\n", encoding="utf-8-sig")
        # `A.Extra` sorts before `A` as a FILE PATH ("A.Extra.cs" < "A.cs") and after it as a NODE
        # PATH ("A" < "A.Extra") — the case that makes file-path order not a reproduction.
        (srcdir / "A.Extra.cs").write_text("public record AExtra();\n", encoding="utf-8")

        global ROOT
        saved_root = ROOT
        ROOT = fixture
        try:
            files = [str(srcdir / n) for n in ("Z.cs", "A.cs", "M.cs", "A.Extra.cs")]
            unit, origins, imports = build_unit(files, None)
            unit_lines = unit.split("\n")

            def line_of(needle):
                return next((i for i, t in enumerate(unit_lines, 1) if needle in t), None)

            nullable_at, z_at, a_at, extra_at, m_at = (
                line_of("#nullable enable"), line_of("class Z"), line_of("record A(string Name)"),
                line_of("record AExtra"), line_of("record M("))
            if None in (nullable_at, z_at, a_at, extra_at, m_at):
                failures.append(f"  unit is missing a fixture declaration: "
                                f"nullable={nullable_at} A={a_at} AExtra={extra_at} "
                                f"M={m_at} Z={z_at}")
            else:
                # THE property: the directive precedes the file that never asked for it.
                if not nullable_at < z_at:
                    failures.append("  `#nullable enable` from the first file does not precede the "
                                    "last file's code — the unit is not concatenated")
                # …in NODE-PATH order, which is not file-path order.
                if not a_at < extra_at < m_at < z_at:
                    failures.append(f"  node-path order violated: A={a_at} A.Extra={extra_at} "
                                    f"M={m_at} Z={z_at}")
            if unit.count("#nullable enable") != 1:
                failures.append("  the directive was duplicated or dropped")
            if "﻿" in unit:
                failures.append("  a byte-order mark reached the unit — read_source must strip it")
            # The hoist is a MOVE: the directive is gone from the code and present once on top.
            if unit.count("using MeshWeaver.Data;") != 1:
                failures.append("  `using MeshWeaver.Data;` is duplicated: the hoist copied "
                                "instead of moving, or the dedup against IMPLICIT_USINGS broke")
            if imports[:len(IMPLICIT_USINGS)] != [f"using {ns};" for ns in IMPLICIT_USINGS]:
                failures.append("  the standard import block is not emitted first, verbatim")
            # …and a diagnostic on that line can be ATTRIBUTED — the half the bake could not do.
            if z_at is not None and origins[z_at - 1] != ("Pkg/T/Source/Z", 1):
                failures.append(f"  origin of the `class Z` line is {origins[z_at - 1]!r}, "
                                "expected ('Pkg/T/Source/Z', 1)")
            if nullable_at is not None and origins[nullable_at - 1] != ("Pkg/T/Source/A", 1):
                failures.append(f"  origin of the `#nullable` line is {origins[nullable_at - 1]!r}, "
                                "expected ('Pkg/T/Source/A', 1)")
            if any(o is not None for o in origins[:len(IMPLICIT_USINGS)]):
                failures.append("  a generated header line claims an authored origin")
            # The configuration lambda type-checks LAST, where the mesh's ConfigureHub sits.
            # `find`, never `index`: a missing substring must be a NAMED failure, not a traceback —
            # an assertion that crashes still stops the run, but it stops it saying nothing useful.
            with_lambda, _, _ = build_unit(files, "config => config")
            lambda_at = with_lambda.find("__MeshWeaverConfigurationCheck")
            sources_at = with_lambda.find("class Z")
            if lambda_at < 0 or sources_at < 0 or lambda_at < sources_at:
                failures.append("  the configuration lambda is emitted BEFORE the sources (or one "
                                f"of them is absent: lambda@{lambda_at}, sources@{sources_at}) — it "
                                "would not inherit the nullable context the mesh gives it")
        finally:
            ROOT = saved_root

    # 🚨 …and the csproj must declare EXACTLY ONE `<Compile>`. This is the assertion that fails on
    # the pre-#4711 shape by construction: that one emitted GlobalUsings.cs + one item per source.
    for impl in (False, True):
        proj = build_csproj(Path("/tmp"), "", impl_frameworks=impl)
        if proj.count("<Compile Include=") != 1 or f'<Compile Include="{UNIT_FILE}"' not in proj:
            failures.append(f"  the generated csproj (impl_frameworks={impl}) does not compile "
                            f"exactly one file — the mesh compiles one, so the gate must too")

    # The external-import classifier decides only whether a failure MAY read UNVERIFIABLE.
    for directive, external in (("using Microsoft.Extensions.AI;", True),
                                ("using Microsoft.Extensions.AI.Evaluation;", True),
                                ("using static Microsoft.Extensions.AI.ChatOptions;", True),
                                ("using Ai = Microsoft.Extensions.AI.ChatRole;", True),
                                ("using MeshWeaver.Data;", False),
                                ("using Microsoft.Extensions.Configuration;", False)):
        if names_external(directive) != external:
            failures.append(f"  names_external({directive!r}) != {external!r}")

    # The reference set must never carry a Roslyn generator/analyzer beside the assembly it generates
    # for (CS0433 "exists in both" on every NodeType naming a System.Text.Json type — Reinsurance #145).
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        flat = os.path.join(tmp, "app")
        os.makedirs(os.path.join(flat, "sdk-generators"))
        for name in ("System.Text.Json.dll", "System.Text.Json.SourceGeneration.dll", "MeshWeaver.Data.dll",
                     "Some.Analyzers.dll"):
            open(os.path.join(flat, name), "wb").close()
        open(os.path.join(flat, "sdk-generators", "Microsoft.Extensions.Logging.Generators.dll"), "wb").close()
        open(os.path.join(flat, "sdk-generators", "Innocent.Name.dll"), "wb").close()  # by DIRECTORY, not name
        refs, _, _ = discover_refs(flat)
        kept = set(refs)
        if kept != {"System.Text.Json.dll", "MeshWeaver.Data.dll"}:
            failures.append(f"  reference-set filter kept {sorted(kept)!r}; expected only System.Text.Json.dll + MeshWeaver.Data.dll")
        if not is_reference_assembly(os.path.join(flat, "System.Text.Json.dll")):
            failures.append("  is_reference_assembly refused the real System.Text.Json.dll")

    # 🚨 THE SHORT-REFERENCE-SET REFUSAL MUST BE ABLE TO FIRE (#4404, MeshWeaver.Plugins#1911). It
    # exists because the two holes it refuses do not LOOK like holes: they look like 17 NodeTypes
    # breaking with CS0012, or 43 with CS1705, naming source that was never touched. A refusal that
    # quietly finds nothing to refuse is indistinguishable from a complete reference set — so a
    # DELIBERATELY SHORT set is asserted to fail, and a complete one to pass, in both layouts.
    complete_image = {"MeshWeaver.Messaging.Hub.dll": "/x/MeshWeaver.Messaging.Hub.dll",
                      "System.Reactive.dll": "/x/System.Reactive.dll",
                      "System.Private.CoreLib.dll": "/x/shared-frameworks/System.Private.CoreLib.dll"}

    def refuses(label: str, refs_set, layout: str, want):
        got = short_reference_set_refusal(refs_set, layout, "the set")
        if want is None and got is not None:
            failures.append(f"  refusal {label}: expected a PASS, got:\n{got}")
        elif want is not None and (got is None or want not in got):
            failures.append(f"  refusal {label}: expected {want!r} in the refusal, got {got!r}")

    # The measured case: exactly what an /app-only extraction produced — a complete set with no
    # shared frameworks beside it.
    no_shared = {k: v for k, v in complete_image.items() if k != "System.Private.CoreLib.dll"}
    refuses("an image set with no shared frameworks", no_shared, "image",
            "System.Private.CoreLib.dll is missing")
    # …and it must name the one command that fixes it, or the reader is left where #1911 started.
    refuses("the refusal names the refill", no_shared, "image", "scripts/fetch-refs.py --force")
    # The other hole the csproj used to fill with a NuGet version.
    refuses("a set with no Rx",
            {k: v for k, v in complete_image.items() if k != "System.Reactive.dll"},
            "image", "NO System.Reactive.dll")
    refuses("a source build with no Rx",
            {"MeshWeaver.Messaging.Hub.dll": "/x/bin/Release/net10.0/MeshWeaver.Messaging.Hub.dll"},
            "source-build", "NO System.Reactive.dll")
    # 🚨 THE CONTROL ARM. A refusal that also fires on a GOOD set is a gate nobody can use: a complete
    # image cache, and the host-SDK core build that legitimately has no shared frameworks, must pass.
    refuses("a complete image cache", complete_image, "image", None)
    refuses("a core source build (the ref pack is correct there)",
            {"MeshWeaver.Messaging.Hub.dll": "/c/src/X/bin/Release/net10.0/MeshWeaver.Messaging.Hub.dll",
             "System.Reactive.dll": "/c/src/X/bin/Release/net10.0/System.Reactive.dll"},
            "source-build", None)
    # 🚨 …and NO version may come back. The generated csproj must never restore Rx from NuGet, in
    # either mode: that restore is what compiled the platform's Rx 7 assemblies against a 6.1.
    for impl in (False, True):
        if "System.Reactive" in build_csproj(Path("/tmp"), "", impl_frameworks=impl):
            failures.append(f"  the generated csproj names System.Reactive (impl_frameworks={impl}) "
                            "— a guessed Rx version is back")

    # 🚨 THE MODULE-REFS GUARD MUST BE ABLE TO FIRE. Its whole job is to refuse a run whose
    # reference set is short, so a guard that silently finds nothing to complain about reads
    # exactly like a complete reference set — the failure this gate exists to stop being
    # misreported. Assert both directions against a temp tree, with no build output anywhere.
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        empty = Path(tmp) / "empty"
        empty.mkdir()
        found = discover_module_refs([str(empty)], include_repo_build=False)
        if found:
            failures.append(f"  module-refs discovery found {sorted(found)} under an EMPTY root")
        stocked = Path(tmp) / "refs"
        stocked.mkdir()
        for name in MODULE_REFS_NOT_IN_IMAGE:
            (stocked / f"{name}.dll").write_bytes(b"")
        # A decoy that is NOT one of the three must never be collected.
        (stocked / "MeshWeaver.Layout.dll").write_bytes(b"")
        found = discover_module_refs([str(stocked)], include_repo_build=False)
        if sorted(found) != sorted(f"{m}.dll" for m in MODULE_REFS_NOT_IN_IMAGE):
            failures.append(f"  module-refs discovery returned {sorted(found)}, "
                            f"expected exactly {sorted(f'{m}.dll' for m in MODULE_REFS_NOT_IN_IMAGE)}")
        # Every name must be absolute — a relative HintPath resolves against dotnet's temp dir and
        # RAR drops the reference silently (the whole reason discover_refs absolutizes too).
        if any(not os.path.isabs(v) for v in found.values()):
            failures.append("  module-refs discovery returned a RELATIVE path")


    # 🚨 THE SCOPE MUST SELECT EXACTLY THE UNIT AND REFUSE EVERYTHING ELSE. `--modules` turns
    # one full run into one leg per atomic unit (Plugins CI, 2026-09-11), and every failure mode
    # of a narrowing is silent: a leg that compiled nothing, a typo that dropped a package, or a
    # ratchet that reds the leg on another unit's debt all render as verdicts. Asserted over a
    # fixture tree with no dotnet — pure selection and gate arithmetic.
    scope_failures = []
    with tempfile.TemporaryDirectory() as tmp:
        fixture = Path(tmp)
        for pkg, ntypes in (("Alpha", ("One", "Two")), ("Beta", ("Three",)), ("Gamma", ())):
            (fixture / pkg).mkdir()
            (fixture / pkg / "index.json").write_text('{"content": {"$type": "PluginContent"}}',
                                                     encoding="utf-8")
            for t in ntypes:
                (fixture / pkg / f"{t}.json").write_text(
                    '{"content": {"$type": "NodeTypeDefinition"}}', encoding="utf-8")
        (fixture / "scripts").mkdir()          # a SKIP dir is never a package
        # A non-package folder a checkout carries anyway (clients/, tools/, devtools/): it has no
        # index.json, so it is not a package even though discovery walks it — and naming it must
        # be refused, or an empty unit reads as a green verdict.
        (fixture / "clients").mkdir()
        (fixture / "clients" / "Stray.json").write_text(
            '{"content": {"$type": "NodeTypeDefinition"}}', encoding="utf-8")
        saved_root = ROOT          # `global ROOT` is declared once, above
        ROOT = fixture
        try:
            pkgs = known_packages(fixture)
            if pkgs != ["Alpha", "Beta", "Gamma"]:
                scope_failures.append(f"  known_packages = {pkgs!r}, expected Alpha/Beta/Gamma")
            types = discover_nodetypes(fixture)
            names = lambda ts: sorted(p.relative_to(fixture).as_posix() for p, _ in ts)
            # a subset selects ONLY those packages
            sel, err = parse_modules("Beta,Alpha", pkgs)
            if err or sel != ["Alpha", "Beta"]:
                scope_failures.append(f"  subset: parse_modules -> ({sel!r}, {err!r})")
            if names(in_scope(types, ["Alpha"])) != ["Alpha/One.json", "Alpha/Two.json"]:
                scope_failures.append(f"  subset: in_scope(Alpha) = {names(in_scope(types, ['Alpha']))!r}")
            if names(in_scope(types, ["Gamma"])) != []:
                scope_failures.append("  subset: a package with no NodeTypes must select zero, not error")
            if names(in_scope(types, None)) != names(types):
                scope_failures.append("  full: in_scope(None) must be every discovered type")
            # an unknown id is refused BY NAME
            sel, err = parse_modules("Alpha,Zeta", pkgs)
            if sel is not None or not err or "Zeta" not in err:
                scope_failures.append(f"  unknown id: expected a refusal naming Zeta, got ({sel!r}, {err!r})")
            sel, err = parse_modules("scripts", pkgs)
            if sel is not None or not err:
                scope_failures.append("  unknown id: a SKIP dir must not be selectable as a package")
            sel, err = parse_modules("clients", pkgs)
            if sel is not None or not err or "clients" not in err:
                scope_failures.append("  unknown id: a folder with no index.json is not a package, "
                                      f"even though discovery walks it — got ({sel!r}, {err!r})")
            # an empty list is refused — never 'compile nothing' silently
            for empty in ("", ",", " , "):
                sel, err = parse_modules(empty, pkgs)
                if sel is not None or not err:
                    scope_failures.append(f"  empty: {empty!r} must be refused, got ({sel!r}, {err!r})")
            if parse_modules(None, pkgs) != (None, None):
                scope_failures.append("  omitted flag must mean a full run")
            # the ratchet ignores out-of-scope allow entries in BOTH directions
            allow = {"Alpha/One": "CS0246:1", "Beta/Three": "CS0246:1", "Gamma/Gone": None}
            scoped = scope_allow(allow, ["Alpha"])
            if set(scoped) != {"Alpha/One"}:
                scope_failures.append(f"  scope_allow(Alpha) = {sorted(scoped)!r}, expected Alpha/One only")
            if scope_allow(allow, None) != allow:
                scope_failures.append("  scope_allow(None) must keep every entry")
            # Alpha's leg: Alpha/One now compiles → stale (ratchet reds it); Beta/Three's entry is
            # outside the scope → neither stale nor ghost; Gamma/Gone → not a ghost here either.
            result = {"Alpha/One": ("ok", []), "Alpha/Two": ("ok", [])}
            node_set = {"Alpha/One": frozenset(), "Alpha/Two": frozenset()}
            v = evaluate_gate(result, scoped, node_set)
            if v["stale_allow"] != ["Alpha/One"] or v["ghosts"] or v["new_breaks"] or v["known_debt"]:
                scope_failures.append(f"  ratchet in scope: {v!r}")
            # Beta's leg, still failing with the pinned fingerprint → known debt, nothing red.
            v = evaluate_gate({"Beta/Three": ("fail", ["CS0246: x"])}, scope_allow(allow, ["Beta"]),
                              {"Beta/Three": frozenset()})
            if v["known_debt"] != ["Beta/Three"] or v["stale_allow"] or v["ghosts"] or v["new_breaks"]:
                scope_failures.append(f"  ratchet known-debt: {v!r}")
            # …and a changed error inside an allowlisted type is drift, a new type is a break.
            v = evaluate_gate({"Beta/Three": ("fail", ["CS0246: x", "CS0103: y"]),
                               "Beta/Four": ("fail", ["CS0246: z"])},
                              scope_allow(allow, ["Beta"]), {"Beta/Three": frozenset(), "Beta/Four": frozenset()})
            if [d[0] for d in v["fp_drift"]] != ["Beta/Three"] or v["new_breaks"] != ["Beta/Four"]:
                scope_failures.append(f"  ratchet drift/break: {v!r}")
            # The FULL run still sees the whole allow-file — Gamma/Gone is a ghost there.
            v = evaluate_gate(result, scope_allow(allow, None), node_set)
            if v["ghosts"] != ["Beta/Three", "Gamma/Gone"]:
                scope_failures.append(f"  full-run ghosts: {v['ghosts']!r}")
        finally:
            ROOT = saved_root
    failures.extend(scope_failures)

    # 🚨 THE REPORT MUST CARRY THE VERDICT, IN BOTH COLOURS. A relayed verdict is read by a job
    # that cannot see this console (core's satellite-compat lane names the failing satellite and
    # its types from the file alone), so a report that says "green" for a red gate — or names no
    # type — would make that lane certify compatibility it never measured.
    with tempfile.TemporaryDirectory() as tmp:
        rep = Path(tmp) / "nested" / "verdict.json"
        result = {"Pkg/Broken": ("fail", ["CS0246: 'Gone' could not be found", "CS0103: second"]),
                  "Pkg/Fine": ("ok", []), "Pkg/Debt": ("fail", ["CS0246: x"])}
        allow = {"Pkg/Debt": failure_fingerprint(["CS0246: x"])}
        v = evaluate_gate(result, allow, {n: frozenset() for n in result})
        write_report(rep, v, result, gate_fail=bool(v["new_breaks"]), scope=None, elapsed=12.34)
        got = json.loads(rep.read_text(encoding="utf-8"))
        if got.get("gate_fail") is not True:
            failures.append(f"  report: a NEW break must read gate_fail=true, got {got.get('gate_fail')!r}")
        if [b.get("type") for b in got.get("new_breaks", [])] != ["Pkg/Broken"]:
            failures.append(f"  report: new_breaks must name Pkg/Broken, got {got.get('new_breaks')!r}")
        if got.get("new_breaks", [{}])[0].get("error") != "CS0246: 'Gone' could not be found":
            failures.append("  report: a break must carry its FIRST error line")
        if [d.get("type") for d in got.get("known_debt", [])] != ["Pkg/Debt"] or got.get("clean") != 1:
            failures.append(f"  report: known_debt/clean counts wrong: {got!r}")
        if got.get("ok") != ["Pkg/Fine"]:
            failures.append(f"  report: the clean types must be listed by NAME, got {got.get('ok')!r}")
        if got.get("scope") != "full" or got.get("types") != 3:
            failures.append(f"  report: scope/types wrong: {got.get('scope')!r}/{got.get('types')!r}")
        os.environ["MW_CONTENT_REPOSITORY"] = "Systemorph/Fixture"; os.environ["MW_CONTENT_SHA"] = "abc1234"
        try:
            write_report(rep, v, result, gate_fail=True, scope=None, elapsed=1)
            got = json.loads(rep.read_text(encoding="utf-8"))
            if got.get("content") != {"repository": "Systemorph/Fixture", "sha": "abc1234"}:
                failures.append(f"  report: content repository/sha not recorded: {got.get('content')!r}")
        finally:
            del os.environ["MW_CONTENT_REPOSITORY"]; del os.environ["MW_CONTENT_SHA"]
        green = {"Pkg/Fine": ("ok", [])}
        write_report(rep, evaluate_gate(green, {}, {"Pkg/Fine": frozenset()}), green,
                     gate_fail=False, scope=["Pkg"], elapsed=1)
        got = json.loads(rep.read_text(encoding="utf-8"))
        if got.get("gate_fail") is not False or got.get("new_breaks") or got.get("scope") != ["Pkg"]:
            failures.append(f"  report: a clean gate must read gate_fail=false with no breaks, got {got!r}")

    if failures:
        print("✗ compile-check self-test FAILED:")
        print("\n".join(failures))
        return 1
    print(f"✓ one concatenated unit (#4711): {len(hoist_cases)} using-directive shape(s) "
          "(incl. the format characters .NET's linguistic StartsWith matches through); a "
          "`#nullable enable` in the FIRST file reaches the LAST; node-path order, not file-path "
          "order; no BOM mid-unit; the hoist is a move; the lambda type-checks last; every "
          "diagnostic line maps back to its authored file; and the csproj compiles exactly one file")
    print("✓ short-reference-set refusal: fires on an image set with no shared frameworks and on any "
          "set with no System.Reactive, names the refill, stays silent on a complete image cache and "
          "on a core source build — and the generated csproj restores no Rx version (#4404)")
    print(f"✓ module-refs guard: detects a short reference set, collects exactly "
          f"{len(MODULE_REFS_NOT_IN_IMAGE)} registry-served assemblies, absolutized")
    print("✓ --modules scope: a subset selects only its packages, an unknown id and an empty list "
          "are refused, the ratchet judges the unit's allow entries only")
    print("✓ --report: the JSON verdict carries gate_fail in both colours, every new break with its "
          "first error, the known-debt list, the scope, and the content repository + sha it judged")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Compiling PR gate for the plugin node repos.")
    ap.add_argument("--refs", help="directory of framework ref assemblies (CI); "
                                    "else auto-discover ../MeshWeaver built src/*/bin")
    ap.add_argument("--image", action="store_true",
                    help="compile against the assemblies from the PLATFORM IMAGE (what CI uses and "
                         "what actually ships), cached per digest under ~/.cache/meshweaver/refs/ — see fetch-refs.py")
    ap.add_argument("--warnings-as-errors", action="store_true",
                    help="hold in-mesh C# to the same standard src/ is held to: warnings become "
                         "ERRORS, NoWarn narrows to the parity list (CS1591;CS1573;CS1712 plus the "
                         "SDK's own CS1701;CS1702), the documentation file is generated so doc "
                         "diagnostics are produced at all, and the nullable context becomes "
                         "`annotations` to match the mesh. OPT-IN: a repo turns it on once its "
                         "in-mesh warning debt is paid.")
    ap.add_argument("--self-test", action="store_true",
                    help="check the unit shaping against its known shapes and exit")
    # nargs="*", not "+": a source-LESS NodeType is a real case (its `configuration` lambda is
    # still code), and argparse refuses an empty "+" list outright — which would make the parity
    # test unable to compare the one shape that has no authored source at all.
    ap.add_argument("--emit-unit", nargs="*", metavar="FILE",
                    help="print, as JSON, the import block and the code this file would shape the "
                         "named sources into — the two halves of "
                         "DynamicMeshNodeAttributeGenerator.ShapeAuthoredSource. The files are "
                         "combined in the ORDER GIVEN (the caller owns the order; the gate itself "
                         "sorts by node path). Compiles nothing and reads no reference set — this "
                         "is what ConcatenatedUnitParityTest compares the C# generator against.")
    ap.add_argument("--gen-allow", action="store_true",
                    help="regenerate scripts/compile-check.allow from the current failures")
    ap.add_argument("--modules", metavar="A,B,C",
                    help="compile ONLY the NodeTypes of these packages (top-level folders) — the "
                         "atomic unit the caller selected; the allow-file ratchet is judged over "
                         "them alone. An unknown id or an empty list is RED. Omit for a full run.")
    ap.add_argument("--report", metavar="PATH",
                    help="also write the verdict as JSON to PATH (see write_report) — what a caller "
                         "that has to RELAY the result reads, e.g. core's per-satellite compat lane. "
                         "Never changes the exit code.")
    args = ap.parse_args()

    global WARNINGS_AS_ERRORS
    WARNINGS_AS_ERRORS = args.warnings_as_errors
    if WARNINGS_AS_ERRORS:
        print("mode: WARNINGS ARE ERRORS — NoWarn is the parity list "
              f"({PARITY_NOWARN} + the SDK's CS1701;CS1702), nullable context `annotations` "
              "(as the mesh compiles), documentation file ON")

    if args.self_test:
        return _self_test()

    if args.emit_unit is not None:        # [] means 'a source-less set', not 'not asked'
        imports, code = shape_authored_source(
            [(node_path_of(f), read_source(f)) for f in args.emit_unit])
        json.dump({"imports": imports, "code": "\n".join(text for text, _ in code)},
                  sys.stdout, indent=2)
        print()
        return 0

    # Resolve the scope BEFORE touching the reference set: a broken selection must be refused in
    # milliseconds and by name, not after an image pull.
    packages = known_packages(ROOT)
    selected, scope_error = parse_modules(args.modules, packages)
    if scope_error:
        print(f"error: {scope_error}")
        return 1
    if selected is None:
        print(f"scope: full — every NodeType in all {len(packages)} package(s)")
    else:
        print(f"scope: {len(selected)} of {len(packages)} package(s) — {', '.join(selected)}")

    refs_arg = args.refs
    if args.image:
        if args.refs:
            print("error: --image and --refs name two different reference sets — pass one")
            return 1
        import importlib.util
        spec = importlib.util.spec_from_file_location("fetch_refs", SCRIPTS / "fetch-refs.py")
        fetch_refs = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(fetch_refs)
        refs_arg = str(fetch_refs.resolve(quiet=False))

    refs, ref_roots, layout = discover_refs(refs_arg)
    _report_framework_provenance(refs, ref_roots)
    if not refs:
        # The image is the ZERO-BUILD path and the one CI uses; say so here rather than leaving
        # "build ../MeshWeaver" (11 minutes) as the only visible answer.
        print("error: no framework ref assemblies found.\n"
              "  python3 scripts/compile-check.py --image   # take them from the platform image "
              "(cached under ~/.cache/meshweaver/refs/, pulls once per pin)\n"
              "  …or pass --refs <dir>, or build a core checkout next to this repo.")
        return 1
    # 🚨 …and a set that is PRESENT can still be short. Refuse before compiling anything: the
    # breaks a short set produces name the CONTENT, and cost an hour of "fixing" source that was
    # never broken (#1911, #4404).
    if (refusal := short_reference_set_refusal(
            refs, layout, ref_roots[0] if ref_roots else "the reference set")) is not None:
        print("\n" + refusal)
        return 1
    # Supply the Microsoft.Extensions.AI assemblies (mesh-provided at runtime, not in src/*/bin) so
    # AI-using sets compile for real instead of being masked as UNVERIFIABLE. If they genuinely can't
    # be found, ai_available stays False and those sets fall back to UNVERIFIABLE (AI-only errors).
    ai_refs = discover_ai_refs(ref_roots)
    refs.update(ai_refs)            # AI names aren't in the core src set, so this never clobbers
    # 🚨 The registry-served modules the image no longer carries. CI unpacks them from this run's
    # bundles into the flat --refs dir; locally they come from src/<Module>/bin. Either way they
    # must be PRESENT, because their absence does not look like an absence — it looks like fifteen
    # NodeTypes breaking (see MODULE_REFS_NOT_IN_IMAGE). Refuse with the real cause instead.
    module_refs = discover_module_refs(ref_roots)
    for name, path in module_refs.items():
        refs.setdefault(name, path)   # never clobber a copy the reference set already carries
    missing_modules = [m for m in MODULE_REFS_NOT_IN_IMAGE if f"{m}.dll" not in refs]
    if missing_modules:
        print("\nerror: the reference set is missing "
              f"{len(missing_modules)} module assembl{'y' if len(missing_modules) == 1 else 'ies'} "
              "that the platform image no longer ships:\n"
              + "".join(f"    {m}.dll\n" for m in missing_modules)
              + "  These are registry-served (MeshWeaver#2276 / #3175), so `/app` does not carry\n"
                "  them and --image alone cannot supply them. Every NodeType that binds one fails\n"
                "  with CS0246/CS0103 naming the CONTENT — which reads as this repo breaking when\n"
                "  the real fault is a short reference set. Refusing to report those as breaks.\n"
                "\n  Build them once, then re-run this command:\n"
              + "".join(
                  f"    dotnet build src/{m} -c Release -p:MeshWeaverRoot=$HOME/code/MeshWeaver/\n"
                  for m in missing_modules)
              + "  (CI needs none of this: it unpacks the same assemblies from the run's module\n"
                "  bundles and asserts each one — .github/workflows/ci.yml, 'Compile-check every\n"
                "  NodeType'.)")
        return 1
    print(f"module refs: {len(MODULE_REFS_NOT_IN_IMAGE)} registry-served assembl(ies) present "
          + ", ".join(sorted(MODULE_REFS_NOT_IN_IMAGE)))
    ai_available = bool(ai_refs)
    ref_xml = "\n".join(
        f'    <Reference Include="{os.path.splitext(n)[0]}"><HintPath>{p}</HintPath></Reference>'
        for n, p in sorted(refs.items()))

    # The refs dir carrying the platform's shared frameworks (System.Private.CoreLib present)
    # switches every set to implementation-framework mode — see build_csproj.
    impl_frameworks = "System.Private.CoreLib.dll" in refs
    if impl_frameworks:
        print("mode: IMPLEMENTATION frameworks — compiling against the image's own assemblies "
              "(no SDK ref pack), exactly as the mesh compiles a NodeType")

    all_types = discover_nodetypes(ROOT)
    types = in_scope(all_types, selected)
    if selected is not None:
        print(f"scope: {len(types)} of {len(all_types)} discovered NodeType(s) are in the unit; "
              f"{len(all_types) - len(types)} outside it are not compiled here")
    # Group by (resolved source set, configuration lambda). The lambda is part of the KEY, not just
    # of the payload: two NodeTypes can share a source set while carrying different lambdas, and one
    # compile can only prove the lambda it actually contains. Types that share BOTH still collapse to
    # a single build, so the grouping keeps its speed-up.
    sets = {}                       # (frozenset, lambda|None) -> list of node-path str
    node_set = {}                   # node-path str -> frozenset
    empty_declared = {}             # node-path str -> reason (declares/expects sources but got 0 .cs)
    for json_path, node in types:
        s = resolve_sources(json_path, node, ROOT)
        nd = node_dir(json_path)
        name = nd.relative_to(ROOT).as_posix()
        repo = json_path.relative_to(ROOT).parts[0]
        node_set[name] = s
        lam = config_lambda(node)
        # A lambda is compiled even with no .cs of its own — the mesh generates ConfigureHub either
        # way, so a source-less NodeType's configuration is still code that has to type-check.
        if s or lam:
            sets.setdefault((s, lam), []).append(name)
        if not s:
            # No .cs resolved. Distinguish a genuinely source-less type (a pure empty-record
            # dimension: no `sources` field AND no Source folder — correctly skipped) from a type
            # that DECLARES/expects sources but resolves to nothing (a typo'd `shared=@…` path or a
            # missing/empty Source folder — a real hole the mesh compile would hit).
            content = node.get("content") or {}
            declared = content.get("sources")
            own_source = (nd / "Source").is_dir()
            root_source = (ROOT / repo / "Source").is_dir()
            if declared:
                empty_declared[name] = ("declares a `sources` list that resolves to no .cs files — "
                                        "check for a typo'd shared=@ path or a missing Source folder")
            elif own_source or root_source:
                empty_declared[name] = "has a Source/ folder that resolves to zero .cs files"
            # else: no sources declared and no Source folder → legitimately source-less; skip.

    import tempfile
    work = Path(tempfile.mkdtemp(prefix="compile-check-"))

    with_lambda = sum(1 for (_, lam) in sets if lam)
    print(f"compile-check: {len(types)} NodeType(s), {len(sets)} distinct source set(s) "
          f"({with_lambda} with a configuration lambda), {len(refs)} ref assemblies\n")

    # compile each distinct set; map result back to every node sharing it
    result = {}                     # node-path -> ("ok"|"fail"|"unverifiable", [errors])
    restored = False
    import time
    t0 = time.time()
    for i, ((s, lam), names) in enumerate(
            sorted(sets.items(), key=lambda kv: sorted(kv[1])[0]), 1):
        errors, needs_external, infra_error = compile_set(
            work, s, ref_xml, restored, ai_available, lambda_src=lam,
            impl_frameworks=impl_frameworks)
        restored = True
        if infra_error:
            # nonzero build with no CS diagnostic (restore/SDK/MSBuild) — a real, gating failure.
            status, errors = "fail", [infra_error]
        elif not errors:
            status = "ok"
        elif needs_external and all(_is_ai_error(e) for e in errors):
            # AI dlls not locatable AND every diagnostic is attributable to the missing AI types.
            status = "unverifiable"
        else:
            # any error (incl. a non-AI error in an AI-using set) is a genuine compile failure.
            status = "fail"
        for name in names:
            result[name] = (status, errors)
        tag = {"ok": "OK", "fail": "FAIL", "unverifiable": "UNVERIFIABLE"}[status]
        lead = sorted(names)[0]
        extra = f"  (+{len(names)-1} sharing)" if len(names) > 1 else ""
        print(f"  [{i}/{len(sets)}] {tag:12} {lead}{extra}"
              + (f"    {errors[0]}" if errors else ""))
    elapsed = time.time() - t0

    # NodeTypes that declare/expect sources but resolved to zero .cs — a real hole (never compiled),
    # so they FAIL the gate (subject to the same allowlist ratchet as compile failures).
    for name, reason in sorted(empty_declared.items()):
        result[name] = ("fail", [f"no resolvable source: {reason}"])
        print(f"  [--] {'FAIL':12} {name}    no resolvable source: {reason}")

    allow_all = read_allow()
    allow = scope_allow(allow_all, selected)
    if selected is not None and len(allow) != len(allow_all):
        print(f"\nallow: {len(allow)} of {len(allow_all)} entr(ies) are inside the unit; the other "
              f"{len(allow_all) - len(allow)} belong to packages not compiled here and are not judged")

    # ── gate evaluation ──
    failing = {n for n, (st, _) in result.items() if st == "fail"}

    if args.gen_allow:
        if selected is not None:
            # A regenerated allow-file describes the WHOLE tree; a scoped run has only seen part
            # of it and would silently drop every other unit's debt from the ratchet.
            print("error: --gen-allow needs a full run — it rewrites the allow-file for every "
                  "package, and a scoped run has not compiled the others. Drop --modules.")
            return 1
        lines = [
            "# compile-check.allow — the compile-debt backlog (RATCHET: must only ever SHRINK).",
            "#",
            "# Each NodeType listed here fails to compile against the current core. They are",
            "# grandfathered so the gate can go green on the existing tree, but the ratchet is",
            "# one-way: a NEW non-allowlisted compile failure FAILS the PR, and fixing an",
            "# allowlisted type means REMOVING its line here (the gate FAILS if an allowlisted",
            "# type now compiles CLEAN). Debt only shrinks, never silently lingers.",
            "#",
            "# The obligation these encode is AGENTS.md's 'Reactive, never async' rule plus the",
            "# node-native self-containment rule (Source must compile from core framework types",
            "# only). Two known debt categories seed this file:",
            "#   * UWDeepfield async-port debt — any UWDeepfield type whose Source still uses",
            "#     async/await/Task in hub- or view-reachable code (the 2026-07-21 two-mesh park).",
            "#   * vendored-framework symbol collision — a plugin that VENDORS a core framework's",
            "#     source (BusinessRules, awaiting the core injection seam, PR #54) collides with",
            "#     that framework's dll when compiled against the FULL local core; it compiles on",
            "#     the mesh (which does not load that assembly). Remove once core ships the seam.",
            "#",
            "# Each entry pins a FINGERPRINT of the expected failure (`fp=<codes>` — the sorted",
            "# multiset of error codes). A name-only allowlist would let NEW debt grow inside an",
            "# already-listed type; the fingerprint fails the gate if the type's CURRENT failure",
            "# differs from the recorded one (a new or changed error), even while it stays listed.",
            "#",
            "# Generated by: python3 scripts/compile-check.py --gen-allow",
            "#",
        ]
        for n in sorted(failing):
            errs = result[n][1]
            err = errs[0] if errs else "(no CS error captured)"
            fp = failure_fingerprint(errs)
            lines.append(f"{n}  fp={fp}  # {err}")
        ALLOW_FILE.write_text("\n".join(lines) + "\n", encoding="utf-8")
        print(f"\nwrote {ALLOW_FILE.relative_to(ROOT)} with {len(failing)} known-broken type(s).")
        return 0

    verdict = evaluate_gate(result, allow, node_set)
    clean, unverifiable = verdict["clean"], verdict["unverifiable"]
    new_breaks, fp_drift = verdict["new_breaks"], verdict["fp_drift"]
    known_debt, stale_allow, ghosts = verdict["known_debt"], verdict["stale_allow"], verdict["ghosts"]

    print(f"\n── summary ──  {len(clean)} clean · {len(known_debt)} known-debt · "
          f"{len(new_breaks)} NEW break(s) · {len(fp_drift)} fingerprint-drift · "
          f"{len(stale_allow)} stale-allow · {len(unverifiable)} unverifiable   ({elapsed:.0f}s)")

    gate_fail = False

    if new_breaks:
        gate_fail = True
        print(f"\n✗ {len(new_breaks)} NON-allowlisted NodeType(s) FAIL to compile (regression):")
        for n in new_breaks:
            print(f"  - {n}\n      {result[n][1][0] if result[n][1] else '(no CS error captured)'}")

    if fp_drift:
        gate_fail = True
        print(f"\n✗ {len(fp_drift)} allowlisted type(s) have a CHANGED failure fingerprint "
              f"(new/uncovered error — allowlist does not cover it):")
        for n, exp_fp, cur_fp in fp_drift:
            print(f"  - {n}: allow pins fp={exp_fp} but now fp={cur_fp}")
            print(f"      {result[n][1][0] if result[n][1] else ''}")
            print(f"      fix the new error, or re-baseline with --gen-allow if it is expected debt")

    if stale_allow:
        gate_fail = True
        print(f"\n✗ {len(stale_allow)} allowlisted type(s) now COMPILE CLEAN — remove from "
              f"{ALLOW_FILE.name} (ratchet):")
        for n in stale_allow:
            print(f"  - remove {n} from compile-check.allow — it compiles now")

    if known_debt:
        print(f"\n⚠ {len(known_debt)} known-debt type(s) still failing (allowlisted, not gating):")
        for n in known_debt:
            print(f"  - {n}: {result[n][1][0] if result[n][1] else ''}")

    if unverifiable:
        print(f"\n? {len(unverifiable)} UNVERIFIABLE (needs Microsoft.Extensions.AI, absent locally):")
        for n in unverifiable:
            print(f"  - {n}")

    if ghosts:
        print(f"\n⚠ {len(ghosts)} allowlisted type(s) no longer exist (clean up allow): "
              + ", ".join(ghosts))

    if not gate_fail:
        unit = "full tree" if selected is None else f"unit {', '.join(selected)}"
        print(f"\n✓ compile gate GREEN ({unit}): {len(clean)} clean, {len(known_debt)} known-debt "
              f"(shrinking), no new breaks.")
    if args.report:
        write_report(Path(args.report), verdict, result, gate_fail=gate_fail, scope=selected, elapsed=elapsed)
        print(f"verdict written to {args.report}")
    return 1 if gate_fail else 0


if __name__ == "__main__":
    sys.exit(main())
