#!/usr/bin/env python3
"""compile-check.py — the COMPILING PR gate for the plugin node repos.

`validate-repos.py` checks JSON shape + Source presence but never compiles. That hole let
async-broken UWDeepfield source merge and park two production meshes (2026-07-21). This gate closes
it: it compiles every NodeType's C# `Source` against the framework assemblies EXACTLY as the mesh
does on import (all sources concatenated, `using` directives hoisted — reproduced here as a
GlobalUsings union + the framework's implicit global usings), and fails on any *regression*.

Faithful mesh reproduction (mirrors scratchpad/build-check.sh, proven against the live compile):
  * ref assemblies = every `<core>/src/*/bin/(Debug|Release)/net10.0/*.dll`, deduped by filename,
    preferring the assembly's OWN project dir + newest mtime;
  * a GlobalUsings.cs prelude = the UNION of `using X;` across the compiled files PLUS the implicit
    global usings the mesh injects (DynamicMeshNodeAttributeGenerator);
  * `Microsoft.Extensions.AI` is dropped from the union (not in the local dll set — the mesh
    supplies it); a set that genuinely needs it is reported UNVERIFIABLE rather than false-failed;
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
        "gate_fail": bool(gate_fail),
        "scope": "full" if scope is None else list(scope),
        "types": len(result),
        "elapsed_seconds": round(elapsed, 1),
        "clean": len(verdict["clean"]),
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
    """Return ({name.dll: path}, search_roots). If --refs is a dir, glob its *.dll. Else
       auto-discover the sibling core build (Debug preferred, else Release), deduped preferring the
       OWN project dir + newest. `search_roots` is the base dir(s) `discover_ai_refs` then scans for
       the Microsoft.Extensions.AI assemblies (which live under `test/*/bin`, not `src/*/bin`)."""
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
    return {n: os.path.abspath(p) for n, (p, _) in seen.items()}, search_roots


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

# 🚨 A using DIRECTIVE has THREE shapes, and the mesh keeps EVERYTHING after `using `
# verbatim (MeshWeaver.Compiler/DynamicMeshNodeAttributeGenerator.cs: "Only a plain namespace
# import can be promoted to `global using` verbatim; an alias/static form is emitted as-is").
# Capturing only the dotted name and re-emitting `global using X.Y;` diverges from the mesh in
# two ways, both of which were live in this repo's node source:
#   * `using static X.Y.Type;`  -> re-emitted plain, and CS0138 because Type is not a namespace.
#     This is a FALSE RED: the gate rejects source the mesh compiles (Cornerstone/Pricing).
#   * `using Alias = X.Y.Type;` -> dropped from the union entirely, so a sibling file or the
#     configuration lambda cannot see an alias the mesh DOES hoist. Latent, not yet fatal.
# The alternation below deliberately still refuses a `using var x = ...;` STATEMENT: neither the
# alias branch (no `=` directly after the identifier) nor the name branch (no `;`) can match it.
USING_DIRECTIVE_RE = re.compile(
    r"\s*using\s+"
    r"(?:(?P<static>static)\s+(?P<staticname>[A-Za-z_][\w.]*)"
    r"|(?P<alias>[A-Za-z_]\w*)\s*=\s*(?P<target>[A-Za-z_][\w.]*)"
    r"|(?P<name>[A-Za-z_][\w.]*))"
    r"\s*;"
)


def _directive_parts(m):
    """(namespace-or-type used for filtering, directive re-emitted after `global using `).

    Returns None for a directive that must NOT be hoisted.

    🚨 An ALIAS is never hoisted, because of an asymmetry between this checker and the mesh. The
    mesh CONCATENATES the source files and strips each file's own `using` lines
    (GenerateAttributeSource -> userCodeWithoutUsings), so its hoist is a MOVE. Here the files are
    compiled as-is and GlobalUsings.cs is added ALONGSIDE them, so a hoist is a COPY -- and while
    duplicate namespace and `using static` imports are legal C#, a duplicate using ALIAS is
    CS1537. Hoisting `using Snap = IssueDetectors.StatusSnapshot;` out of Hosting/Issue failed
    that NodeType with exactly that error.

    The cost is a narrower, KNOWN divergence: a sibling file or the configuration lambda cannot
    see an alias the mesh would hoist. Closing it needs the alias stripped from the source copy
    too -- the mesh's actual shape -- which is a larger change than this checker's contract.
    """
    if m.group("static"):
        return m.group("staticname"), f"static {m.group('staticname')}"
    if m.group("alias"):
        return None
    return m.group("name"), m.group("name")


def usings_union(cs_files, ai_available: bool) -> tuple:
    """Return (union_usings sorted, needs_external bool).

    `Microsoft.Extensions.AI` is never hoisted into the global-usings union (the source files keep
    their own `using` line). If the AI assemblies WERE located and added to the ref set
    (`ai_available`), that using resolves for real and the set is compiled normally. Only if the AI
    assemblies genuinely could not be found do we flag `needs_external` so the set can be reported
    UNVERIFIABLE — and even then only when EVERY diagnostic is attributable to the missing AI types
    (see `_is_ai_error`); any non-AI error still FAILS the set."""
    usings = set(IMPLICIT_USINGS)
    needs_external = False
    for f in cs_files:
        try:
            text = Path(f).read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for line in text.splitlines():
            m = USING_DIRECTIVE_RE.match(line)
            if m and (parts := _directive_parts(m)) is not None:
                ns, directive = parts
                if ns in EXTERNAL_USINGS or ns.startswith("Microsoft.Extensions.AI"):
                    if not ai_available:
                        needs_external = True
                    continue
                usings.add(directive)
    return sorted(usings), needs_external


def write_project(work: Path, cs_files, refs: dict):
    (work / "GlobalUsings.cs").write_text("", encoding="utf-8")  # placeholder, filled per-set
    ref_xml = "\n".join(
        f'    <Reference Include="{os.path.splitext(n)[0]}"><HintPath>{p}</HintPath></Reference>'
        for n, p in sorted(refs.items()))
    (work / "refs.txt").write_text(ref_xml, encoding="utf-8")


def build_csproj(work: Path, cs_files, ref_xml: str, with_config_check: bool = False,
                 impl_frameworks: bool = False) -> str:
    compiles = '    <Compile Include="GlobalUsings.cs" />\n'
    if with_config_check:
        compiles += '    <Compile Include="ConfigCheck.cs" />\n'
    compiles += "\n".join(
        f'    <Compile Include="{f}" />' for f in sorted(cs_files))
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
    framework_items = ("" if impl_frameworks else
                       '    <FrameworkReference Include="Microsoft.AspNetCore.App" />\n'
                       '    <PackageReference Include="System.Reactive" Version="6.1.0" />\n')
    return f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <!-- Disable the SDK's implicit usings: the MESH compiler injects ONLY the explicit prelude
         (IMPLICIT_USINGS, lifted from DynamicMeshNodeAttributeGenerator) plus the per-set union of
         authored `using`s. `ImplicitUsings=enable` would auto-add System.IO / System.Net.Http /
         System.Threading.Tasks / System.Text / … that the mesh does NOT inject — a source missing a
         `using System.IO;` would compile here but fail on the mesh (a false PASS). -->
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <NoWarn>$(NoWarn);CS1591;CS1998;CS8618;CS8602;CS8604;CS0618</NoWarn>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
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
    compiled ALONGSIDE the sources, in the same compilation, exactly as the mesh does — the sources
    define the types the lambda names, so the two only type-check together."""
    union, needs_external = usings_union(cs_files, ai_available)
    (work / "GlobalUsings.cs").write_text(
        "\n".join(f"global using {u};" for u in union) + "\n", encoding="utf-8")
    config_check = work / "ConfigCheck.cs"
    if lambda_src:
        config_check.write_text(config_check_source(lambda_src), encoding="utf-8")
    elif config_check.exists():
        config_check.unlink()       # the work dir is reused across sets — never leak a stale lambda
    (work / "check.csproj").write_text(
        build_csproj(work, cs_files, ref_xml, with_config_check=bool(lambda_src),
                     impl_frameworks=impl_frameworks), encoding="utf-8")
    cmd = ["dotnet", "build", "check.csproj", "--nologo", "-v", "q"]
    if restored:
        cmd.insert(2, "--no-restore")
    proc = subprocess.run(cmd, cwd=work, capture_output=True, text=True)
    out = proc.stdout + proc.stderr
    errors = []
    for line in out.splitlines():
        m = re.search(r"error (CS\d+): (.*?)(?: \[|$)", line)
        if m:
            errors.append(f"{m.group(1)}: {m.group(2).strip()}")
    # dedup preserving order
    seen = set()
    errors = [e for e in errors if not (e in seen or seen.add(e))]
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
    """The using-directive parser, against the shapes that actually occur in node source.

    This exists because the parser silently produced WRONG output rather than failing: a
    `using static` was re-emitted as a plain import (CS0138 — a false red on source the mesh
    compiles) and an alias was dropped from the scope entirely. Both are shape bugs no
    compile run can attribute back to the parser, so they get asserted here directly.
    """
    cases = [
        # line,                                        expected (ns, directive) or None
        ("using MeshWeaver.Data;",                     ("MeshWeaver.Data", "MeshWeaver.Data")),
        ("    using MeshWeaver.Layout;",               ("MeshWeaver.Layout", "MeshWeaver.Layout")),
        ("using static MeshWeaver.Layout.Controls;",   ("MeshWeaver.Layout.Controls",
                                                        "static MeshWeaver.Layout.Controls")),
        ("using static MeshWeaver.ContentCollections.ContentCollectionsExtensions;",
         ("MeshWeaver.ContentCollections.ContentCollectionsExtensions",
          "static MeshWeaver.ContentCollections.ContentCollectionsExtensions")),
        # An alias must NOT be hoisted: GlobalUsings.cs sits ALONGSIDE source that still declares
        # it, and a duplicate using alias is CS1537 (it failed Hosting/Issue).
        ("using Snap = IssueDetectors.StatusSnapshot;", None),
        ("using LayoutOption=MeshWeaver.Layout.Option;", None),
        # NOT directives — a `using` STATEMENT must never enter the global scope.
        ("using var stream = File.OpenRead(path);",    None),
        ("        using var scope = svc.CreateScope();", None),
        ("// using MeshWeaver.Fake;",                  None),
    ]
    failures = []
    for line, expected in cases:
        m = USING_DIRECTIVE_RE.match(line)
        actual = _directive_parts(m) if m else None
        if actual != expected:
            failures.append(f"  {line!r}\n    expected {expected!r}\n    actual   {actual!r}")

    # The end-to-end property: what lands in GlobalUsings.cs must be legal C#, i.e. a
    # `using static` on a TYPE keeps its modifier. This is the exact CS0138 regression.
    union, _ = usings_union([], ai_available=True)
    emitted = [f"global using {u};" for u in union]
    if any(u.startswith("global using static ") for u in emitted):
        failures.append("  IMPLICIT_USINGS unexpectedly contains a static import")

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
        refs, _ = discover_refs(flat)
        kept = set(refs)
        if kept != {"System.Text.Json.dll", "MeshWeaver.Data.dll"}:
            failures.append(f"  reference-set filter kept {sorted(kept)!r}; expected only System.Text.Json.dll + MeshWeaver.Data.dll")
        if not is_reference_assembly(os.path.join(flat, "System.Text.Json.dll")):
            failures.append("  is_reference_assembly refused the real System.Text.Json.dll")

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
        global ROOT
        saved_root = ROOT
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
        if got.get("scope") != "full" or got.get("types") != 3:
            failures.append(f"  report: scope/types wrong: {got.get('scope')!r}/{got.get('types')!r}")
        green = {"Pkg/Fine": ("ok", [])}
        write_report(rep, evaluate_gate(green, {}, {"Pkg/Fine": frozenset()}), green,
                     gate_fail=False, scope=["Pkg"], elapsed=1)
        got = json.loads(rep.read_text(encoding="utf-8"))
        if got.get("gate_fail") is not False or got.get("new_breaks") or got.get("scope") != ["Pkg"]:
            failures.append(f"  report: a clean gate must read gate_fail=false with no breaks, got {got!r}")

    if failures:
        print("✗ using-directive parser self-test FAILED:")
        print("\n".join(failures))
        return 1
    print(f"✓ using-directive parser: {len(cases)} shape(s) OK")
    print(f"✓ module-refs guard: detects a short reference set, collects exactly "
          f"{len(MODULE_REFS_NOT_IN_IMAGE)} registry-served assemblies, absolutized")
    print("✓ --modules scope: a subset selects only its packages, an unknown id and an empty list "
          "are refused, the ratchet judges the unit's allow entries only")
    print("✓ --report: the JSON verdict carries gate_fail in both colours, every new break with its "
          "first error, the known-debt list and the scope")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Compiling PR gate for the plugin node repos.")
    ap.add_argument("--refs", help="directory of framework ref assemblies (CI); "
                                    "else auto-discover ../MeshWeaver built src/*/bin")
    ap.add_argument("--image", action="store_true",
                    help="compile against the assemblies from the PLATFORM IMAGE (what CI uses and "
                         "what actually ships), cached per digest under ~/.cache/meshweaver/refs/ — see fetch-refs.py")
    ap.add_argument("--self-test", action="store_true",
                    help="check the using-directive parser against its known shapes and exit")
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

    if args.self_test:
        return _self_test()

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

    refs, ref_roots = discover_refs(refs_arg)
    _report_framework_provenance(refs, ref_roots)
    if not refs:
        # The image is the ZERO-BUILD path and the one CI uses; say so here rather than leaving
        # "build ../MeshWeaver" (11 minutes) as the only visible answer.
        print("error: no framework ref assemblies found.\n"
              "  python3 scripts/compile-check.py --image   # take them from the platform image "
              "(cached under ~/.cache/meshweaver/refs/, pulls once per pin)\n"
              "  …or pass --refs <dir>, or build a core checkout next to this repo.")
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
    (work / "GlobalUsings.cs").write_text("", encoding="utf-8")

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
