#!/usr/bin/env python3
"""abi-key.py — ABI-keyed rebuild: a dependent is rebuilt and re-tested only when a dependency's
PUBLIC SURFACE changed, not whenever its source did.

(The name on this first line is load-bearing, like module-build-key.py's: a lane that fetches this
file may refuse a body whose first 400 bytes do not name it.)

WHY (maintainer, 2026-11-07): "can we reduce amount we rebuild? do we need to rebuild always all
dependencies? actually only when api changes?!"  Until now a change to project D selected D's whole
transitive dependent closure, and module-build-key.py's `closure` hashed every in-repo dependency
by SOURCE TREE — so an edit to a private method body in a project forty others reference rebuilt and
re-tested all forty. A dependent's COMPILATION is a function of D's REFERENCE ASSEMBLY (the public
surface Roslyn emits with ProduceReferenceAssembly — `obj/<cfg>/<tfm>/ref/<Asm>.dll`), not of D's
method bodies. This script keys dependents on that.

THE POLICY (owner-facing, kept exactly)
---------------------------------------
  * internal-only change (D's public SURFACE unchanged at base and head) — D and D's own suite (its
    sibling `<D>.Test`) compile and test; every other dependent is SKIPPED with the reason
    `public API unchanged (surface <hash>)`.
  * public-API change (the surface differs — this also catches the optional-constructor-parameter
    BINARY break AGENTS.md warns about, because the ctor signature and its defaults are surface) —
    dependents rebuild and run their tests as before.
  * full runs (schedule, platform releases, trunk publication) never consult this: the CALLER only
    applies a verdict on a pull request / merge-group diff.

HOW THE PROBE MEASURES THE SURFACE — Roslyn's syntax tree, no build (owner: "analyze the Roslyn tree
rather than lots of builds")
--------------------------------------------------------------------------------------------------
  The comparer `abi-syntax/` (a small console on Microsoft.CodeAnalysis.CSharp) parses each changed file at
  base and at head and reduces it to a SKELETON: every declaration with its modifiers, attributes, type
  parameters, constraints, base list, parameter defaults, enum values and const values — private and internal
  ones included — with every member BODY, constructor initializer and non-const initializer removed (a body
  leaves a marker: `{}`, `__iterator__` when it yields, `__field__` when an accessor uses `field`), all trivia
  dropped except preprocessor directives and disabled text, kept verbatim. Equal skeletons for EVERY changed file
  of D ⇒ unchanged. The reference assembly is a function of exactly that declaration set (plus what rule 1
  excludes), so a body-only edit cannot move it; the replay of merged pull requests against the old build-based
  verdicts is in Doc/Architecture/ModuleBuildArchitecture → "ABI-keyed rebuild". Surfaces are cached by git blob
  sha + the comparer's own content hash + PARSE_OPTIONS (`--cache-dir`, the fleet's file store in CI): a hit
  counts only under its full key, and an unreachable cache only means parsing.

WHAT MAKES A SKIP SOUND — every rule below errs toward REBUILD
-------------------------------------------------------------
  1. ELIGIBLE ONLY FOR C# EDITS OF EXISTING FILES. D is eligible only when EVERY changed file under D is
     an existing `*.cs` (an added or deleted file is a change), D cannot embed `.cs` as payload (a Compile
     Remove / resource include — `payload_risk`), and D switches on no generator driven by method BODIES
     (interceptors). A changed csproj, resx, json, md, razor, wwwroot asset or content file makes D
     "changed": those reach a dependent through paths the surface does not describe. (The build hosts'
     `key` keeps the ABI identity `sha256(ref asm || non-C# tree hash)`.)
  2. INTERNALSVISIBLETO. A dependent X that sees D's internals (D declares InternalsVisibleTo X)
     keys on D's FULL implementation, never the surface: a unit is never skipped when ANY project in
     its closure is a friend of D. Friends are the UNION of csproj `<InternalsVisibleTo Include=…>` items,
     `[assembly: InternalsVisibleTo("…")]` in D's sources and, for the build hosts' manifest, the compiled
     assembly's attribute blobs — over-approximation is the safe direction.
  3. FAIL SAFE IN THE COMPARER. A file that does not parse, top-level statements, an `#if` edit (directives are
     surface), and a file that feeds a source generator (a partial member declared without a body, or a known
     generator attribute such as `[GeneratedRegex]`, compared TOKEN for token) all read as changed; a struct with
     a primary constructor keeps its member bodies (a captured parameter is a private field — layout).
  4. CONTENT READERS. A unit whose declared inputs or linked `../` includes READ one of D's changed
     files as text (a census ratchet over `src/**/*.cs`) is never skipped — it reads source, not
     surface.
  5. ANYTHING ELSE IN THE DIFF. The verdict speaks only for D's files. The CALLER must re-select on
     the diff WITHOUT them (`decide --reselected`): a unit any other changed path still reaches stays.
  6. A comparer that cannot be built or cannot answer makes the whole verdict `unavailable`. A probe that
     cannot answer narrows nothing.

WHAT MAKES A PROBE WORTH RUNNING — it never burns its budget on a range it cannot narrow
-------------------------------------------------------------------------------------
  7. SHORT-CIRCUIT. A range that changes an input EVERY project's build imports moves every reference
     assembly, and the caller re-selects every unit on that path anyway (rule 5) — so the probe answers
     `{"unavailable": "<input> changed — every reference assembly moves; nothing narrowed"}` BEFORE it
     builds anything. The set is module-build-key.py's own input model, widened to err wide: every
     GLOBAL_INPUTS basename (global.json, nuget.config, …) and every `Directory.*.props|targets`
     (directory_imports' rule) at ANY depth, `scripts/platform-floor.json` (the platform the caller
     resolves), and an added or removed `<TargetFramework(s)>` line in any .csproj/.props/.targets.
     Measured on MeshWeaver.Plugins#5421 (net11 retarget, package versions moved): the probe built
     every changed project at head and base and its job was killed by its own 25-minute limit.
  8. A DEADLINE. With `--deadline <epoch>` the probe starts nothing it cannot finish by then: when the
     comparer's build (TOOL_BUILD_SECONDS, cached by content hash) + one parse per file version exceed the
     time left it answers `unavailable` without starting.

THE KEY (the interface the build hosts rely on)
-----------------------------------------------
    identity(D, as seen by unit U) =
        "tree:" + tree_hash(D)      D is U's OWN dir (U's entry dir or its sibling <dir>.Test), or D
                                    has no built entry in the manifest
        "full:" + sha256(D.dll)     some project in U's closure is a friend of D (InternalsVisibleTo)
        "abi:"  + sha256(sha256(ref asm) "\n" non-C# tree hash of D)      otherwise

    ABI test key(U) = sha256( canonical JSON of {
        recipe:   ABI_RECIPE
        unit:     U's entry project (repo-relative csproj path)
        closure:  {dir → identity(dir, U)}  over module-build-key.py's closure_dirs(U)
        globals:  module-build-key.py GLOBAL_INPUTS {path → sha256 | null}
        imports:  module-build-key.py directory_imports(closure)
        platform: the caller's opaque platform identity (digests / platform ref)
    } )

  🚨 Cross-commit equality needs a commit-independent reference assembly: the .NET SDK writes
  `<version>+<git sha>` into AssemblyInformationalVersion by default and the reference assembly
  carries that attribute (measured 2026-11-07). Build with
  `-p:IncludeSourceRevisionInInformationalVersion=false` (the probe does) or a fixed
  InformationalVersion (MeshWeaver.Plugins' src/Directory.Build.props pins it to AssemblyVersion).

  Two runs whose ABI test keys for U are equal compiled U's suite against the same public surface
  of every dependency, the same own sources, the same build inputs and the same platform — so U's
  recorded verdict may be reused. `decide` (the GitHub Actions path) is that comparison between the
  BASE and the HEAD of one diff, where an unchanged-tree dependency is `tree:` on both sides.

CLI
---
  abi-key.py manifest --root R --project P [--project P …] [--configuration Release] [--msbuild-arg A …] [--glob]
      → JSON {projectDir: {assembly, refasm, full, noncode, abi, friends[]}} of BUILT projects.
        Default asks MSBuild (`dotnet msbuild -getProperty:TargetRefPath,TargetPath,AssemblyName`);
        `--glob` reads obj/<cfg>/*/ref/*.dll + bin/<cfg>/*/<Asm>.dll instead (no SDK needed).
  abi-key.py probe --root R (--range A...B | --base SHA) [--deadline EPOCH] [--cache-dir DIR] [--no-strip]
                   [--out verdict.json]
      → the VERDICT: compares each eligible changed project's syntax surface at base and head (git blobs —
        the working tree is never touched, nothing is built): {schema, method: "syntax", base, head,
        projects: {dir: {assembly, eligible, unchanged, refasm (= the head SURFACE hash), baseRefasm, files[],
        friends[], reason}}, deterministic, cache: {hits, misses}} — or, short-circuited (rule 7) or out of
        time (rule 8), the same envelope with `unavailable` set and no projects.
  abi-key.py --self-test-syntax        builds the real comparer on this host's SDK and runs its cases.
  abi-key.py decide --root R --verdict V --units U.json [--reselected ids.json] [--out decision.json]
      U.json = [{id, project, reads?: [prefix|glob …], closure?: [dir …]}] (closure: the caller's own graph; [] means "no dependencies"); ids.json = ids still selected when the
      verdict's unchanged files are removed from the diff (the caller's own selector). REQUIRED for any
      skip: without it every unit is kept (nothing outside the verdict's files is known unchanged).
      → {id: {decision: "skip"|"keep", reason, dependencies[]}}; skip reasons read exactly
        `public API unchanged (ref asm <hash12>)`.
  abi-key.py key --root R --project P --manifest M.json [--platform STR] [--read GLOB …] [--json]
      → the ABI test key of unit P (see THE KEY), or {key, inputs} with --json.
  abi-key.py --self-test

Exit codes: 0 answered (an "everything changed" verdict is an answer); 2 bad invocation or input.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path
from typing import Callable

ABI_RECIPE = "1"
# What the compiler reads: C# and Razor sources (a `.razor`/`.cshtml` markup edit compiles into a method
# BODY — BuildRenderTree — unless it adds a `[Parameter]`/public member, which the reference assembly shows).
CODE_EXTS = (".cs", ".razor", ".cshtml")
SCHEMA = "abi-verdict/1"
SKIP_PREFIX = "public API unchanged"
_HERE = Path(__file__).resolve().parent


def _load(name: str, module: str):
    spec = importlib.util.spec_from_file_location(module, _HERE / name)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load {_HERE / name}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# Loaded, never copied: the dependency walk, the tree hash and the build-input sets are the ones
# the content-addressed build key uses, so ABI key and build key cannot disagree about a closure.
_MBK = _load("module-build-key.py", "module_build_key")
_PSCOPE = _load("node-repo-project-scope.py", "node_repo_project_scope")


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# ───────────────────────────── the project graph ─────────────────────────────

SKIP_DIRS = {"bin", "obj", ".git", ".vs", ".worktrees", "node_modules", "TestResults", "meshweaver"}


def project_dirs(root: Path) -> list[str]:
    """Every directory holding a *.csproj (repo-relative), build outputs and nested checkouts excluded."""
    out: set[str] = set()
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS and not d.startswith(".")]
        if any(f.endswith(".csproj") for f in filenames):
            out.add(Path(dirpath).relative_to(root).as_posix())
    return sorted(out)


def owner_of(path: str, dirs: list[str]) -> str | None:
    hits = [d for d in dirs if path == d or path.startswith(d + "/")]
    return max(hits, key=len) if hits else None


UNKNOWN_ASSEMBLY = "?unresolved"


def assembly_names(root: Path, rel_dir: str) -> set[str]:
    """The project's assembly name(s), or UNKNOWN_ASSEMBLY when it cannot be read from the project text:
    an `<AssemblyName>` built from a property, or one an ancestor Directory.Build.props/.targets sets
    (review of core #7986 — an InternalsVisibleTo grant to the REAL name must never be missed)."""
    names: set[str] = set()
    inherited = False
    here = (root / rel_dir).resolve()
    while True:
        for f in here.glob("Directory.Build.*"):
            if f.suffix in (".props", ".targets") and "<AssemblyName" in f.read_text(encoding="utf-8", errors="replace"):
                inherited = True
        if here == root.resolve() or here.parent == here:
            break
        here = here.parent
    for csproj in sorted((root / rel_dir).glob("*.csproj")):
        text = csproj.read_text(encoding="utf-8", errors="replace")
        m = re.search(r"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>", text)
        if m and "$(" not in m.group(1):
            names.add(m.group(1))
        elif m or inherited:
            names.add(UNKNOWN_ASSEMBLY)
        else:
            names.add(csproj.stem)
    return names


def own_dirs(root: Path, project: str) -> set[str]:
    """The unit's own directories: its entry project's dir and its sibling — `<dir>.Test` for a
    production entry, the production `<dir>` for a `.Test` entry (review of core #7986: a test-project
    unit must keep its production sibling's changes its own, or an internal edit would skip it)."""
    d = (root / project).parent
    out = {d.relative_to(root).as_posix()}
    sibling = d.with_name(d.name[: -len(".Test")]) if d.name.endswith(".Test") else d.with_name(d.name + ".Test")
    if sibling.is_dir() and any(sibling.glob("*.csproj")):
        out.add(sibling.relative_to(root).as_posix())
    return out


_PAYLOAD_RISK = re.compile(r"<Compile\s[^>]*Remove=|EnableDefaultCompileItems|"
                           r"<(?:EmbeddedResource|Content|None)\s[^>]*(?:Include|Update)=[\"'][^\"']*(?:\.cs\b|\*)")


def payload_risk(root: Path, rel_dir: str) -> bool:
    """Could a `.cs` file under rel_dir be PAYLOAD rather than compiler input? (a Compile Remove, default
    compile items off, or a resource/content include that matches .cs or a wildcard). Read from the
    project text — the fallback when MSBuild's evaluated Compile items are not at hand."""
    return any(_PAYLOAD_RISK.search(c.read_text(encoding="utf-8", errors="replace"))
               for c in (root / rel_dir).glob("*.csproj"))


def is_code(root: Path, rel_path: str, rel_dir: str, code: set[str] | None) -> bool:
    """Is this file compiler input of the project at rel_dir? `code` = MSBuild's evaluated Compile items
    under rel_dir (minus resources/content) when known; otherwise a `.cs` with no payload risk."""
    if code is not None:
        return rel_path in code
    # Unevaluated (`--glob`, no SDK): C# only — a `.razor`/`.cshtml` is compiler input only when MSBuild
    # SAYS so (review of core #7995: a literal EmbeddedResource of a Page.cshtml is payload).
    return rel_path.endswith(".cs") and not payload_risk(root, rel_dir)


def noncode_hash(root: Path, rel_dir: str, code: set[str] | None = None) -> str:
    """Tree hash over every file under rel_dir that is NOT compiler input — what a reference assembly
    cannot describe (project file, resources, content, assets, C# payload a project embeds)."""
    base = root / rel_dir
    entries: list[str] = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in _MBK.SKIP_DIRS)
        for name in sorted(filenames):
            p = Path(dirpath) / name
            if is_code(root, p.relative_to(root).as_posix(), rel_dir, code):
                continue
            entries.append(f"{p.relative_to(base).as_posix()}={_MBK.file_hash(p)}")
    return sha256("\n".join(entries).encode("utf-8"))


# ───────────────────────────── InternalsVisibleTo ─────────────────────────────

_IVT_ITEM = re.compile(r"<InternalsVisibleTo\s[^>]*Include=(?:\"([^\"]+)\"|'([^']+)')")
_IVT_ATTR = re.compile(r"InternalsVisibleTo(?:Attribute)?\s*\(\s*\"([^\"]+)\"")
# A custom-attribute blob with ONE string argument: prolog 0x0001, an ECMA-335 COMPRESSED length
# (1 byte < 0x80, 2 bytes 10xxxxxx… < 0x4000 — a full strong-name PublicKey makes an IVT string ~330
# bytes, review of core #7986), UTF-8 bytes, zero named arguments 0x0000.
_PROLOG = b"\x01\x00"


def _compressed(data: bytes, at: int) -> tuple[int, int] | None:
    """(value, bytes used) of the ECMA-335 compressed unsigned integer at `at`, or None."""
    if at >= len(data):
        return None
    b0 = data[at]
    if b0 & 0x80 == 0:
        return b0, 1
    if b0 & 0xC0 == 0x80 and at + 1 < len(data):
        return ((b0 & 0x3F) << 8) | data[at + 1], 2
    if b0 & 0xE0 == 0xC0 and at + 3 < len(data):
        return ((b0 & 0x1F) << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3], 4
    return None


def _name(raw: str) -> str:
    return raw.split(",", 1)[0].strip()


def friends_from_assembly(data: bytes) -> set[str]:
    """Assembly names an implementation (or reference) assembly grants InternalsVisibleTo.

    Over-approximates on purpose: when the metadata names InternalsVisibleToAttribute at all, EVERY
    single-string attribute argument is a candidate (AssemblyTitle, AssemblyProduct, …). A spurious
    friend only costs a rebuild; a missed one would skip a dependent that reads internals."""
    if b"InternalsVisibleToAttribute" not in data:
        return set()
    out: set[str] = set()
    at = data.find(_PROLOG)
    while at != -1:
        got = _compressed(data, at + 2)
        nxt = data.find(_PROLOG, at + 1)
        if got is None or got[0] == 0 or got[0] > 4096:
            at = nxt
            continue
        n, used = got
        start = at + 2 + used
        blob = data[start:start + n]
        at = nxt
        if len(blob) == n and data[start + n:start + n + 2] == b"\x00\x00":
            try:
                text = blob.decode("utf-8")
            except UnicodeDecodeError:
                continue
            if text.isprintable() and text.strip():
                out.add(_name(text))
    return out


UNKNOWN_FRIEND = "?unresolved-friend"
_IVT_ASSEMBLY_ATTRIBUTE = re.compile(r"<AssemblyAttribute\s[^>]*Include=[\"'][^\"']*InternalsVisibleTo[^\"']*[\"'][^>]*?(?:/>|>(.*?)</AssemblyAttribute>)",
                                     re.DOTALL)


def friends_from_source(root: Path, rel_dir: str) -> set[str]:
    """InternalsVisibleTo grants readable WITHOUT a build: csproj and every Directory.Build.* above it (items and
    AssemblyAttribute items), and `[assembly:]` attributes in the sources. A grant whose name cannot be read
    (a property other than the project's own name, an attribute item without a literal) is UNKNOWN_FRIEND —
    every dependent then keeps (the probe no longer has the compiled attribute blobs to read it from)."""
    out: set[str] = set()
    base = root / rel_dir
    own = assembly_names(root, rel_dir)
    texts = [c.read_text(encoding="utf-8", errors="replace") for c in base.glob("*.csproj")]
    here = base.resolve()
    while here != root.resolve() and here.parent != here:
        here = here.parent
        texts += [f.read_text(encoding="utf-8", errors="replace") for f in here.glob("Directory.Build.*") if f.is_file()]
    for text in texts:
        for a, b in _IVT_ITEM.findall(text):
            name = _name(a or b)
            if "$(" in name:
                expanded = {re.sub(r"\$\((?:AssemblyName|MSBuildProjectName|TargetName)\)", n, name) for n in own}
                out.update(UNKNOWN_FRIEND if "$(" in e or UNKNOWN_ASSEMBLY in e else e for e in expanded)
            else:
                out.add(name)
        for body in _IVT_ASSEMBLY_ATTRIBUTE.findall(text):
            m = re.search(r"<_Parameter1>\s*([^<$]+?)\s*</_Parameter1>", body or "")
            out.add(_name(m.group(1)) if m else UNKNOWN_FRIEND)
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in _MBK.SKIP_DIRS]
        for name in filenames:
            if name.endswith(".cs"):
                text = (Path(dirpath) / name).read_text(encoding="utf-8", errors="replace")
                if "InternalsVisibleTo" in text:
                    out.update(_name(x) for x in _IVT_ATTR.findall(text))
    # `$(AssemblyName).Test`-style items: the caller's own .Test sibling is an own dir anyway, but
    # an unexpanded property must never read as "no friend" — keep the literal for the record.
    return {f for f in out if f}


# ───────────────────────────── manifest: what a BUILT project exposes ─────────────────────────────

def _msbuild_paths(root: Path, csproj: Path, configuration: str, msbuild_args: list[str]) -> dict:
    cmd = ["dotnet", "msbuild", str(csproj), "-nologo", f"-p:Configuration={configuration}",
           "-getProperty:TargetRefPath", "-getProperty:TargetPath", "-getProperty:AssemblyName",
           "-getProperty:UsingMicrosoftNETSdkRazor", "-getProperty:EnableDefaultRazorComponentItems",
           "-getItem:Compile", "-getItem:EmbeddedResource", "-getItem:Content", *msbuild_args]
    # A fresh MSBuild process per evaluation, never the MSBuild server: the server keeps the project
    # files it loaded, so an item or import deleted since it last evaluated this tree is still listed
    # (see msbuild-evaluate.py `_FRESH_MSBUILD`) — and a probe evaluates two checkouts in turn.
    proc = subprocess.run(cmd, cwd=root, capture_output=True, text=True, timeout=600,
                          env={**os.environ, "DOTNET_CLI_USE_MSBUILD_SERVER": "0"})
    if proc.returncode != 0:
        raise RuntimeError(f"msbuild evaluation of {csproj} failed: {proc.stderr.strip()[-400:] or proc.stdout.strip()[-400:]}")
    doc = json.loads(proc.stdout)
    props = doc.get("Properties") or {}
    return {"ref": props.get("TargetRefPath") or "", "full": props.get("TargetPath") or "",
            "assembly": props.get("AssemblyName") or csproj.stem,
            "code": sorted(code_from_evaluation(root, csproj.parent, props, doc.get("Items") or {}))}


def code_from_evaluation(root: Path, project_dir: Path, props: dict, items: dict) -> set[str]:
    """The project's compiler input under its directory, from `dotnet msbuild -getProperty/-getItem` JSON.

    Compile items, plus Razor markup — a `Content` item the Razor SDK compiles (RazorComponent is only filled
    in by targets) — but only in a project the Razor SDK compiles (a plain Microsoft.NET.Sdk project may carry
    a .razor Content item nobody compiles; default component items can be switched off), and never one
    copied to output or publish (an MVC/Razor Pages `.cshtml` with PreserveNewest) or embedded. Everything
    else — the copied `.cshtml` included — stays payload, hashed into the non-code identity (review of #7995).
    Self-tested on synthetic JSON in the shape `dotnet msbuild` answers (this script's self-test runs where no SDK
    is installed); verified by hand against a real Razor project. A drift in the SDK's names or metadata can
    only DEGRADE markup to payload — lost narrowing, never a wrong skip."""
    def rel(kind: str, keep=lambda it: True) -> set[str]:
        out = set()
        for it in items.get(kind) or []:
            if not keep(it):
                continue
            try:
                out.add(Path(it.get("FullPath") or "").resolve().relative_to(root.resolve()).as_posix())
            except ValueError:
                continue
        return out
    prefix = project_dir.resolve().relative_to(root.resolve()).as_posix() + "/"
    razor_sdk = (str(props.get("UsingMicrosoftNETSdkRazor") or "").lower() == "true"
                 and str(props.get("EnableDefaultRazorComponentItems") or "true").lower() != "false")

    def razor(it: dict) -> bool:
        return razor_sdk and (str(it.get("FullPath") or "").endswith((".razor", ".cshtml"))
                and (it.get("CopyToOutputDirectory") or "Never") == "Never"
                and (it.get("CopyToPublishDirectory") or "Never") == "Never")
    copied = rel("Content", lambda it: not razor(it))
    return {f for f in (rel("Compile") | rel("Content", razor)) - rel("EmbeddedResource") - copied if f.startswith(prefix)}


def _glob_paths(root: Path, csproj: Path, configuration: str) -> dict:
    assembly = sorted(assembly_names(root, csproj.parent.relative_to(root).as_posix()))[0]
    d = csproj.parent
    refs = sorted(d.glob(f"obj/{configuration}/*/ref/{assembly}.dll"))
    fulls = sorted(d.glob(f"bin/{configuration}/*/{assembly}.dll"))
    if len(refs) > 1 or len(fulls) > 1:
        raise RuntimeError(f"{csproj}: more than one target framework built — the ABI key covers one")
    return {"ref": str(refs[0]) if refs else "", "full": str(fulls[0]) if fulls else "", "assembly": assembly}


def manifest_entry(root: Path, rel_dir: str, paths: dict) -> dict:
    ref = Path(paths["ref"]) if paths.get("ref") else None
    full = Path(paths["full"]) if paths.get("full") else None
    if ref is None or not ref.is_file():
        raise RuntimeError(f"{rel_dir}: no reference assembly at {paths.get('ref') or '<unset>'} — build with "
                           "ProduceReferenceAssembly=true (the SDK default for net5+) before reading the ABI")
    ref_bytes = ref.read_bytes()
    full_bytes = full.read_bytes() if full and full.is_file() else b""
    refasm = sha256(ref_bytes)
    code = set(paths["code"]) if paths.get("code") is not None else None
    nc = noncode_hash(root, rel_dir, code)
    friends = friends_from_assembly(full_bytes) | friends_from_assembly(ref_bytes) | friends_from_source(root, rel_dir)
    return {"assembly": paths.get("assembly") or "", "refasm": refasm,
            "full": sha256(full_bytes) if full_bytes else "", "noncode": nc,
            "abi": sha256(f"{refasm}\n{nc}".encode()), "friends": sorted(friends),
            "code": sorted(code) if code is not None else None}


def build_manifest(root: Path, projects: list[str], configuration: str = "Release",
                   msbuild_args: list[str] | None = None, use_glob: bool = False) -> dict:
    out: dict[str, dict] = {}
    for project in projects:
        csproj = (root / project).resolve()
        if csproj.is_dir():
            found = sorted(csproj.glob("*.csproj"))
            if len(found) != 1:
                raise RuntimeError(f"{project}: expected exactly one *.csproj")
            csproj = found[0]
        rel_dir = csproj.parent.relative_to(root.resolve()).as_posix()
        paths = (_glob_paths(root.resolve(), csproj, configuration) if use_glob
                 else _msbuild_paths(root, csproj, configuration, msbuild_args or []))
        out[rel_dir] = manifest_entry(root.resolve(), rel_dir, paths)
    return out


# ───────────────────────────── probe: base vs head, in place ─────────────────────────────

def git(root: Path, *args: str, check: bool = True) -> str:
    proc = subprocess.run(["git", *args], cwd=root, capture_output=True, text=True, timeout=300)
    if check and proc.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)}: {proc.stderr.strip()}")
    return proc.stdout


def resolve_range(root: Path, diff_range: str | None, base: str | None) -> tuple[str, str]:
    head = git(root, "rev-parse", "HEAD").strip()
    if base:
        return git(root, "rev-parse", base).strip(), head
    if not diff_range:
        raise ValueError("probe needs --range A...B or --base SHA")
    if "..." in diff_range:
        a, b = diff_range.split("...", 1)
        return git(root, "merge-base", a or "HEAD", b or "HEAD").strip(), head
    if ".." in diff_range:
        a, _ = diff_range.split("..", 1)
        return git(root, "rev-parse", a).strip(), head
    return git(root, "rev-parse", diff_range).strip(), head


def changed_files(root: Path, base: str) -> list[str]:
    out = git(root, "diff", "-z", "--no-renames", "--name-only", base, "HEAD")
    return sorted(p for p in out.split("\0") if p)


# ── rule 7: inputs every project's build imports ──
GLOBAL_PATHS = ("scripts/platform-floor.json",)
GLOBAL_NAMES = frozenset(Path(p).name.lower() for p in _MBK.GLOBAL_INPUTS if not Path(p).name.startswith("Directory."))
_DIRECTORY_IMPORT = re.compile(r"(?:^|/)Directory\.[^/]+\.(?:props|targets)$", re.IGNORECASE)
_TFM_LINE = re.compile(r"^[+-](?![+-]{2} ).*<TargetFrameworks?\b", re.IGNORECASE)
MSBUILD_EXTS = (".csproj", ".props", ".targets")
def global_input(files: list[str], diff_text: str) -> str | None:
    """Rule 7: the first changed input every project's build imports, named — None when there is none.
    `diff_text` is `git diff -U0` over the range's MSBuild files (for the TargetFramework lines)."""
    for f in sorted(files):
        if f in GLOBAL_PATHS or f.rsplit("/", 1)[-1].lower() in GLOBAL_NAMES or _DIRECTORY_IMPORT.search(f):
            return f
    current = None
    for line in diff_text.splitlines():
        if line.startswith("diff --git "):
            current = line.split(" b/", 1)[-1].strip()
            continue
        if line.startswith(("+++ ", "--- ")):
            continue
        if current and current.lower().endswith(MSBUILD_EXTS) and _TFM_LINE.match(line):
            return f"the target framework in {current}"
    return None


def short_circuit(root: Path, base: str, files: list[str]) -> str | None:
    msbuild = [f for f in files if f.lower().endswith(MSBUILD_EXTS)]
    diff_text = git(root, "diff", "--no-renames", "-U0", base, "HEAD", "--", *msbuild) if msbuild else ""
    hit = global_input(files, diff_text)
    return None if hit is None else f"{hit} changed — every reference assembly moves; nothing narrowed"


def unavailable(base: str, head: str, why: str) -> dict:
    """A verdict that narrows nothing: no project is `unchanged`, so `decide` keeps every unit."""
    return {"schema": SCHEMA, "base": base, "head": head, "deterministic": True, "projects": {}, "unavailable": why,
            "method": METHOD}


# ───────────────────────────── the surface: Roslyn's syntax tree, never a build ─────────────────────────────

SYNTAX_DIR = _HERE / "abi-syntax"
# The parse options every surface is computed under — part of every cache key. The tool parses at the newest
# language version (a superset of every LangVersion) with NO preprocessor symbol, and keeps directives and
# disabled text verbatim, so no project's symbols are needed and a `#if` edit is always a surface change.
PARSE_OPTIONS = "lang=preview;symbols=none;directives=verbatim"
METHOD = "syntax"
# Rule 8's per-file prior: one Roslyn parse + skeleton is milliseconds; this bounds the tool's whole call.
PARSE_SECONDS = 0.05
TOOL_BUILD_SECONDS = 90.0
# The project text that switches on a generator driven by method BODIES (interceptors): a body edit there can
# change generated code, so its projects are never eligible.
_BODY_GENERATORS = re.compile(r"Interceptors(?:Preview)?Namespaces|EnableConfigurationBindingGenerator|"
                              r"EnableRequestDelegateGenerator", re.IGNORECASE)

SurfaceFn = Callable[[dict], dict]   # {blob sha: bytes} → {blob sha: {surface: str|None, mode, reason}}


def tool_hash(tool_dir: Path = SYNTAX_DIR) -> str:
    """The comparer's own content address: every source file of its directory (bin/obj excluded)."""
    entries = []
    for p in sorted(tool_dir.rglob("*")):
        rel = p.relative_to(tool_dir)
        if p.is_file() and not ({"bin", "obj"} & set(rel.parts)):
            entries.append(f"{rel.as_posix()}={sha256(p.read_bytes())}")
    return sha256("\n".join(entries).encode())


def surface_key(blob: str, strip: bool = True, tool: str | None = None) -> str:
    """A cached surface is authoritative only under its FULL key: the comparer's content, the parse options and
    the blob — git's own content address of the file."""
    return sha256(f"abi-surface/1\n{tool or tool_hash()}\n{PARSE_OPTIONS}\nstrip={strip}\n{blob}".encode())


class SurfaceCache:
    """Content-addressed surfaces under a directory (a runner-local one, or the fleet's file store — the share a
    runner pod mounts). A hit counts only when the entry names its full key; anything unreadable or unwritable is a
    MISS — the file is parsed, never a wrong verdict."""

    def __init__(self, directory: Path | None):
        self.dir = directory
        self.hits = self.misses = 0

    def _path(self, key: str) -> Path | None:
        return self.dir / "surfaces" / key[:2] / f"{key}.json" if self.dir else None

    def get(self, key: str) -> dict | None:
        p = self._path(key)
        try:
            doc = json.loads(p.read_text(encoding="utf-8")) if p and p.is_file() else None
        except (OSError, ValueError):
            doc = None
        if isinstance(doc, dict) and doc.get("key") == key and "surface" in doc:
            self.hits += 1
            return doc
        self.misses += 1
        return None

    def put(self, key: str, doc: dict) -> None:
        p = self._path(key)
        if p is None:
            return
        try:
            p.parent.mkdir(parents=True, exist_ok=True)
            tmp = p.with_suffix(f".{os.getpid()}.tmp")
            tmp.write_text(json.dumps({**doc, "key": key}, sort_keys=True), encoding="utf-8")
            os.replace(tmp, p)
        except OSError:
            pass


def syntax_tool(cache_dir: Path | None, dotnet: str = "dotnet") -> Path:
    """The comparer's built entry point, cached by its content hash + the SDK + the runner — built once per run
    at most. Raises RuntimeError when it cannot be built (the probe then answers `unavailable`)."""
    sdk = subprocess.run([dotnet, "--version"], capture_output=True, text=True, timeout=120)
    if sdk.returncode != 0:
        raise RuntimeError(f"no .NET SDK to build the syntax comparer: {sdk.stderr.strip()[-200:]}")
    version = sdk.stdout.strip()
    major = version.split(".", 1)[0]
    ident = sha256(f"{tool_hash()}\n{version}\n{sys.platform}\n{os.uname().machine}".encode())[:24]
    base = (cache_dir or Path(os.environ.get("RUNNER_TEMP") or "/tmp") / "abi-cache") / "tool"
    done = base / ident / "abi-syntax.dll"
    if (base / ident / ".complete").is_file() and done.is_file():
        return done
    staging = base / f"{ident}.{os.getpid()}.tmp"
    proc = subprocess.run([dotnet, "build", str(SYNTAX_DIR / "AbiSyntax.csproj"), "-nologo", "-v:q", "-c", "Release",
                           f"-p:AbiSyntaxTfm=net{major}.0", "-o", str(staging),
                           f"-p:BaseIntermediateOutputPath={staging}.obj/"],
                          capture_output=True, text=True, timeout=900,
                          env={**os.environ, "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "DOTNET_NOLOGO": "1"})
    if proc.returncode != 0 or not (staging / "abi-syntax.dll").is_file():
        tail = "\n".join((proc.stdout + proc.stderr).strip().splitlines()[-15:])
        raise RuntimeError(f"the syntax comparer did not build:\n{tail}")
    import shutil
    shutil.rmtree(f"{staging}.obj", ignore_errors=True)
    (staging / ".complete").write_text(ident)
    try:
        os.replace(staging, base / ident)
    except OSError:
        if not (base / ident / ".complete").is_file():   # another run won the rename: theirs is identical
            raise
    return done


def roslyn_surfaces(cache: SurfaceCache, cache_dir: Path | None, strip: bool = True, dotnet: str = "dotnet") -> SurfaceFn:
    """The production SurfaceFn: cache first, the Roslyn comparer for every miss, written back."""
    tool = tool_hash()

    def surfaces(blobs: dict) -> dict:
        import tempfile
        out: dict[str, dict] = {}
        misses: dict[str, bytes] = {}
        for blob, data in blobs.items():
            hit = cache.get(surface_key(blob, strip, tool))
            if hit is not None:
                out[blob] = {k: hit.get(k) for k in ("surface", "mode", "reason")}
            else:
                misses[blob] = data
        if misses:
            entry = syntax_tool(cache_dir, dotnet)
            with tempfile.TemporaryDirectory() as tmp:
                req = {"strip": strip, "blobs": []}
                for blob, data in misses.items():
                    (Path(tmp) / blob).write_bytes(data)
                    req["blobs"].append({"id": blob, "file": str(Path(tmp) / blob)})
                (Path(tmp) / "request.json").write_text(json.dumps(req))
                proc = subprocess.run([dotnet, str(entry), str(Path(tmp) / "request.json")], capture_output=True,
                                      text=True, timeout=600)
            if proc.returncode != 0:
                raise RuntimeError(f"the syntax comparer failed: {proc.stderr.strip()[-400:]}")
            got = json.loads(proc.stdout)
            for blob in misses:
                doc = got.get(blob)
                if not isinstance(doc, dict):
                    raise RuntimeError(f"the syntax comparer gave no answer for blob {blob}")
                out[blob] = doc
                cache.put(surface_key(blob, strip, tool), doc)
        return out
    return surfaces


def _blob(root: Path, rev: str, path: str) -> str | None:
    proc = subprocess.run(["git", "rev-parse", "--verify", "-q", f"{rev}:{path}"], cwd=root, capture_output=True, text=True)
    return proc.stdout.strip() if proc.returncode == 0 and proc.stdout.strip() else None


def _blob_bytes(root: Path, blob: str) -> bytes:
    return subprocess.run(["git", "cat-file", "blob", blob], cwd=root, capture_output=True, check=True, timeout=120).stdout


_ITEM_PATTERN = re.compile(r"<(Compile|EmbeddedResource|Content|None|AdditionalFiles)\s[^>]*?\b(Include|Update|Remove)=(?:\"([^\"]*)\"|'([^']*)')")


def _glob_regex(pattern: str) -> str:
    out, i = [], 0
    while i < len(pattern):
        if pattern.startswith("$(", i):
            j = pattern.find(")", i)
            out.append(".*")                # an unexpanded property: anything (over-approximates coverage)
            i = len(pattern) if j < 0 else j + 1
        elif pattern.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif pattern.startswith("**", i):
            out.append(".*")
            i += 2
        elif pattern[i] == "*":
            out.append("[^/]*")
            i += 1
        elif pattern[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(pattern[i]))
            i += 1
    return "".join(out)


def payload_item(root: Path, rel_dir: str, rel_file: str) -> str | None:
    """Why the changed `.cs` file may NOT be plain compiler input of the project at rel_dir — the item that
    covers it (a `Compile Remove`, or an EmbeddedResource/Content/None/AdditionalFiles include of it), or default
    compile items switched off — read from the project file and every Directory.Build.* above it. None ⇒ the
    SDK's default `**/*.cs` compiles it. Patterns with unexpanded properties match ANYTHING their literal parts
    allow (the safe direction); the build hosts' manifest still asks MSBuild itself (`code_from_evaluation`)."""
    text = _project_text(root, rel_dir)
    if re.search(r"<EnableDefaultCompileItems>\s*false", text, re.IGNORECASE):
        return "EnableDefaultCompileItems=false"
    for kind, attr, a, b in _ITEM_PATTERN.findall(text):
        if kind == "Compile" and attr != "Remove":
            continue
        for piece in (a or b).split(";"):
            piece = piece.strip().replace("\\", "/")
            if not piece:
                continue
            if "$(" in piece:
                rx = "(?:.*/)?" + _glob_regex(piece.lstrip("/"))
            else:
                rx = _glob_regex(os.path.normpath(f"{rel_dir}/{piece}").replace("\\", "/"))
            if re.fullmatch(rx, rel_file):
                return f"<{kind} {attr}=\"{piece}\">"
    return None


def _project_text(root: Path, rel_dir: str) -> str:
    """The project file and every Directory.Build.* above it — where a body-driven generator is switched on."""
    texts = [c.read_text(encoding="utf-8", errors="replace") for c in (root / rel_dir).glob("*.csproj")]
    here = (root / rel_dir).resolve()
    while True:
        texts += [f.read_text(encoding="utf-8", errors="replace") for f in here.glob("Directory.Build.*") if f.is_file()]
        if here == root.resolve() or here.parent == here:
            break
        here = here.parent
    return "\n".join(texts)


def probe(root: Path, base: str, head: str, files: list[str], surfaces: SurfaceFn,
          deadline: float | None = None, clock: Callable[[], float] = time.time) -> dict:
    """The verdict for every project owning a changed file (see the module docstring)."""
    root = root.resolve()
    why = short_circuit(root, base, files)
    if why:
        return unavailable(base, head, why)
    dirs = project_dirs(root)
    owned: dict[str, list[str]] = {}
    for f in files:
        d = owner_of(f, dirs)
        if d is not None:
            owned.setdefault(d, []).append(f)
    verdict: dict = {"schema": SCHEMA, "base": base, "head": head, "deterministic": True, "projects": {}, "method": METHOD}
    projects = verdict["projects"]
    pairs: dict[str, list[tuple[str, str | None, str | None]]] = {}
    for d, fs in sorted(owned.items()):
        names = sorted(assembly_names(root, d))
        entry = {"assembly": names[0] if names else Path(d).name, "files": sorted(fs), "eligible": False,
                 "unchanged": False, "refasm": "", "baseRefasm": "", "friends": sorted(friends_from_source(root, d))}
        projects[d] = entry
        other = [f for f in fs if not f.endswith(".cs")]
        if other:
            entry["reason"] = f"a non-C# input changed ({other[0]}{f' +{len(other) - 1}' if len(other) > 1 else ''}) — not described by its syntax surface"
            continue
        payload = next(((f, why) for f in sorted(fs) if (why := payload_item(root, d, f))), None)
        if payload:
            entry["reason"] = f"{payload[0]} may not be compiler input ({payload[1]}) — not judged by syntax"
            continue
        if _BODY_GENERATORS.search(_project_text(root, d)):
            entry["reason"] = "a source generator driven by method bodies (interceptors) is switched on — a body edit can move generated code"
            continue
        trio = [(f, _blob(root, base, f), _blob(root, head, f)) for f in sorted(fs)]
        added = [f for f, b, h in trio if b is None or h is None]
        if added:
            entry["reason"] = f"a file was added or deleted ({added[0]}) — treated as an API change"
            continue
        entry["eligible"] = True
        pairs[d] = trio
    blobs = {b for trio in pairs.values() for _, bb, hb in trio for b in (bb, hb)}
    if blobs and deadline is not None and clock() + TOOL_BUILD_SECONDS + len(blobs) * PARSE_SECONDS > deadline:
        return unavailable(base, head, f"{len(blobs)} file version(s) to parse (+ the comparer's build) exceed the "
                                       f"{max(0, int(deadline - clock()))} s left before the deadline — nothing narrowed")
    try:
        got = surfaces({b: _blob_bytes(root, b) for b in sorted(blobs)}) if blobs else {}
    except (RuntimeError, OSError, ValueError, subprocess.SubprocessError) as exc:
        return unavailable(base, head, f"the syntax comparer could not answer: {str(exc)[-300:]} — nothing narrowed")
    for d, trio in pairs.items():
        entry = projects[d]
        head_s, base_s, moved = [], [], None
        for f, bb, hb in trio:
            b, h = got.get(bb) or {}, got.get(hb) or {}
            if not b.get("surface") or not h.get("surface"):
                moved = moved or f"{f}: {(h.get('reason') or b.get('reason') or 'no surface')} — treated as an API change"
                continue
            base_s.append(f"{f}={b['surface']}")
            head_s.append(f"{f}={h['surface']}")
            if b["surface"] != h["surface"]:
                moved = moved or f"public API changed ({f}: surface {b['surface'][:12]} → {h['surface'][:12]})"
        entry["refasm"] = sha256("\n".join(head_s).encode()) if head_s else ""
        entry["baseRefasm"] = sha256("\n".join(base_s).encode()) if base_s else ""
        if moved:
            entry["reason"] = moved
        else:
            entry["unchanged"] = True
            entry["reason"] = f"{SKIP_PREFIX} (surface {entry['refasm'][:12]})"
    return verdict


# ───────────────────────────── decide: which selected units may skip ─────────────────────────────

def unit_reads(root: Path, project: str, closure: list[str], declared: list[str]) -> list[str]:
    reads = list(declared)
    for d in closure:
        for csproj in (root / d).glob("*.csproj"):
            text = csproj.read_text(encoding="utf-8", errors="replace")
            for attr in re.findall(r'<(?:Compile|Content|EmbeddedResource|None)\s[^>]*Include=["\']([^"\']+)', text):
                for raw in attr.split(";"):
                    pattern = _PSCOPE.linked_pattern(root, csproj, raw)
                    if pattern is not None:
                        reads.append(pattern)
    return reads


def decide(root: Path, verdict: dict, units: list[dict], reselected: set[str] | None = None) -> dict:
    root = root.resolve()
    if verdict.get("schema") != SCHEMA:
        raise ValueError(f"verdict schema {verdict.get('schema')!r} is not {SCHEMA!r}")
    projects = verdict.get("projects") or {}
    out: dict[str, dict] = {}
    for unit in units:
        uid, project = unit["id"], unit["project"]
        if not (root / project).is_file():
            raise ValueError(f"unit {uid}: project {project} does not exist")
        # A caller whose selector owns a WIDER graph (MeshWeaver.Plugins' project-closure.py: the build+test
        # closure, Condition-guarded references included) hands it in as `closure`, so `decide` judges the
        # same edges the selector reached the unit by; absent, module-build-key.py's closure.
        closure = list(unit["closure"]) if unit.get("closure") is not None else _MBK.closure_dirs(root, project)
        own = own_dirs(root, project)
        hit = [d for d in closure if d in projects]
        if reselected is None:
            # No re-selection evidence ⇒ nothing outside the verdict's files is known to be unchanged
            # (a Directory.Build.props beside the body edit), so nothing may skip (review of core #7986).
            out[uid] = {"decision": "keep", "reason": "no re-selection evidence (--reselected) — skipping needs it",
                        "dependencies": hit}
            continue
        if uid in reselected:
            out[uid] = {"decision": "keep", "reason": "another changed path still reaches it", "dependencies": hit}
            continue
        reads = unit_reads(root, project, closure, list(unit.get("reads") or []))
        # A linked or declared read reaches files OUTSIDE the ProjectReference closure too (a `../Shared/A.cs`
        # compile include with no reference to Shared — review of core #7986): any probed file it covers keeps.
        read_hit = next((f for e in projects.values() for f in e.get("files") or []
                         if any(_PSCOPE.input_covers(f, r) for r in reads)), None)
        if read_hit:
            out[uid] = {"decision": "keep", "reason": f"reads {read_hit} as content or compiler input (declared input / linked include)",
                        "dependencies": hit}
            continue
        if not hit:
            out[uid] = {"decision": "keep", "reason": "no probed project in its closure — selected for another reason",
                        "dependencies": []}
            continue
        assemblies = set().union(*(assembly_names(root, d) for d in closure))
        keep = None
        for d in hit:
            entry = projects[d]
            if d in own:
                keep = f"{d} is its own project — the changed module compiles and tests"
            elif not entry.get("unchanged"):
                keep = f"{entry.get('assembly') or d}: {entry.get('reason') or 'public API changed'}"
            else:
                friends = set(entry.get("friends") or []) | friends_from_source(root, d)
                others = set().union(*(assembly_names(root, x) for x in closure if x != d))
                seen = sorted(others & friends)
                if UNKNOWN_FRIEND in friends:
                    keep = (f"{entry.get('assembly') or d} grants InternalsVisibleTo to an assembly whose name cannot be "
                            "read without a build — a friend cannot be ruled out")
                elif UNKNOWN_ASSEMBLY in others and friends:
                    keep = (f"an assembly name in its closure cannot be read from the project text, and "
                            f"{entry.get('assembly') or d} grants InternalsVisibleTo — a friend cannot be ruled out")
                elif seen:
                    keep = (f"{seen[0]} sees {entry.get('assembly') or d}'s internals (InternalsVisibleTo) — "
                            "keyed on the full assembly")
                else:
                    readers = [f for f in entry.get("files") or [] if any(_PSCOPE.input_covers(f, r) for r in reads)]
                    if readers:
                        keep = f"reads {readers[0]} as content (declared input / linked include)"
            if keep:
                break
        if keep:
            out[uid] = {"decision": "keep", "reason": keep, "dependencies": hit}
        else:
            hashes = ", ".join(projects[d]["refasm"][:12] for d in hit)
            out[uid] = {"decision": "skip", "reason": f"{SKIP_PREFIX} (surface {hashes})",
                        "dependencies": [projects[d].get("assembly") or d for d in hit]}
    return out


# ───────────────────────────── key: the ABI test key ─────────────────────────────

def files_matching(root: Path, pattern: str) -> list[str]:
    """Repo-relative files a read pattern (a prefix, `.`, or a glob) covers, sorted."""
    wild = re.search(r"[*?]", pattern)
    literal = pattern if wild is None else pattern[: pattern.rfind("/", 0, wild.start()) + 1]
    base = root / literal.rstrip("/") if literal not in ("", ".") else root
    if base.is_file():
        return [base.relative_to(root).as_posix()] if _PSCOPE.input_covers(base.relative_to(root).as_posix(), pattern) else []
    out: list[str] = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        for name in filenames:
            rel = (Path(dirpath) / name).relative_to(root).as_posix()
            if _PSCOPE.input_covers(rel, pattern):
                out.append(rel)
    return sorted(out)


def abi_key(root: Path, project: str, manifest: dict, platform: str = "", reads: list[str] | None = None) -> dict:
    root = root.resolve()
    closure = _MBK.closure_dirs(root, project)
    own = own_dirs(root, project)
    assemblies = {d: assembly_names(root, d) for d in closure}
    # A unit that READS a dependency's files as text (a linked `../Lib/A.cs` compile include, a declared
    # input, a census) depends on those bytes, not on a surface: key that dependency by its tree
    # (review of core #7986 — the same safeguard `decide` applies).
    read_patterns = unit_reads(root, project, closure, list(reads or []))
    identities: dict[str, str] = {}
    for d in closure:
        entry = manifest.get(d)
        read_here = d not in own and any(
            _PSCOPE.input_covers(str(p.relative_to(root).as_posix()), r)
            for p in (root / d).rglob("*") if p.is_file() and not (set(p.relative_to(root / d).parts) & _MBK.SKIP_DIRS)
            for r in read_patterns)
        if d in own or not entry or read_here:
            identities[d] = "tree:" + _MBK.tree_hash(root, d)
            continue
        friends = set(entry.get("friends") or []) | friends_from_source(root, d)
        others = set().union(*(a for x, a in assemblies.items() if x != d))
        if friends & others or UNKNOWN_FRIEND in friends or (friends and UNKNOWN_ASSEMBLY in others):
            if not entry.get("full"):
                raise ValueError(f"{d}: a friend in {project}'s closure needs the full assembly hash, the manifest has none")
            # The full assembly AND the non-compiled files (review of core #7986): a content file copied to
            # output can change while the implementation DLL stays byte-identical.
            identities[d] = "full:" + sha256(f"{entry['full']}\n{entry.get('noncode', '')}".encode())
        else:
            identities[d] = "abi:" + entry["abi"]
    globals_ = {rel: (_MBK.file_hash(root / rel) if (root / rel).is_file() else None) for rel in _MBK.GLOBAL_INPUTS}
    # Every read is hashed on its own, inside the closure or not (review of core #7986): a linked
    # `MeshWeaver.Fixture/RepoPaths.cs` with no ProjectReference is compiler input all the same — an edit,
    # an addition or a deletion under the pattern moves the key.
    read_hashes = {r: sha256("\n".join(f"{f}={_MBK.file_hash(root / f)}" for f in files_matching(root, r)).encode())
                   for r in sorted(set(read_patterns))}
    inputs = {"recipe": ABI_RECIPE, "unit": Path(project).as_posix(), "closure": identities, "reads": read_hashes,
              "globals": globals_, "imports": _MBK.directory_imports(root, closure), "platform": platform}
    canonical = json.dumps(inputs, sort_keys=True, separators=(",", ":"))
    return {"key": sha256(canonical.encode()), "inputs": inputs}


# ───────────────────────────── self-test ─────────────────────────────

def _fixture(root: Path) -> None:
    def proj(d: str, refs: list[str] = (), extra: str = "") -> None:
        (root / d).mkdir(parents=True, exist_ok=True)
        items = "".join(f'<ProjectReference Include="../{Path(r).name}/{Path(r).name}.csproj" />' for r in refs)
        (root / d / f"{Path(d).name}.csproj").write_text(f'<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{items}{extra}</ItemGroup></Project>')
    proj("src/Lib", extra='<InternalsVisibleTo Include="Lib.Test" /><InternalsVisibleTo Include="Friend" />')
    proj("src/Lib.Test", ["src/Lib"])
    proj("src/App", ["src/Lib"])
    proj("src/App.Test", ["src/App"])
    proj("src/Friend", ["src/Lib"])
    proj("src/Friend.Test", ["src/Friend"])
    proj("src/Census.Test", ["src/App"])
    (root / "src/Lib/A.cs").write_text("public class A { public int F() => 1; }\n")
    (root / "src/App/B.cs").write_text("public class B { public int G() => new A().F(); }\n")
    (root / "src/Lib/data.json").write_text("{}\n")
    (root / ".gitignore").write_text("bin/\nobj/\n")


def _fake_builder(root: Path, log: list, nondeterministic: set[str] | None = None):
    """A 'compiler' whose reference assembly is the sorted public lines of the project's *.cs files
    (plus an InternalsVisibleTo blob from the csproj), and whose implementation is all of the C#."""
    counter = {"n": 0}

    def build(d: str, with_refs: bool) -> bool:
        log.append((d, with_refs))
        src = sorted(p for ext in ("*.cs", "*.razor") for p in (root / d).rglob(ext))
        text = "".join(p.read_text() for p in src)
        if "SYNTAX ERROR" in text:
            return False
        public = sorted(set(re.findall(r"public (?:class \w+|[\w<>?]+ \w+\([^)]*\)|[\w<>?]+ \w+ \{)", text)))
        friends = sorted(friends_from_source(root, d))
        blob = b"InternalsVisibleToAttribute" + b"".join(b"\x01\x00" + bytes([len(f)]) + f.encode() + b"\x00\x00" for f in friends)
        counter["n"] += 1
        salt = str(counter["n"]).encode() if nondeterministic and d in nondeterministic else b""
        out = root / d / "obj/Release/net11.0/ref"
        out.mkdir(parents=True, exist_ok=True)
        name = Path(d).name
        (out / f"{name}.dll").write_bytes("\n".join(public).encode() + blob + salt)
        binp = root / d / "bin/Release/net11.0"
        binp.mkdir(parents=True, exist_ok=True)
        (binp / f"{name}.dll").write_bytes(text.encode() + blob)
        return True

    def read(d: str) -> dict:
        csproj = sorted((root / d).glob("*.csproj"))[0]
        paths = _glob_paths(root, csproj, "Release")
        # Explicit compiler membership, as MSBuild's evaluation would hand it: C# and Razor sources of a
        # project with no payload risk (the fake compiler reads exactly these).
        if not payload_risk(root, d):
            paths["code"] = sorted(p.relative_to(root).as_posix() for ext in ("*.cs", "*.razor") for p in (root / d).rglob(ext)
                                   if not (set(p.relative_to(root / d).parts) & {"bin", "obj"}))
        return manifest_entry(root, d, paths)

    return build, read


def _fake_surfaces(log: list) -> SurfaceFn:
    """A stand-in for the Roslyn comparer (this self-test runs where no SDK is installed): the surface of a file is
    its sorted public declaration lines; "SYNTAX ERROR" is a parse error. `--self-test-syntax` runs the real one."""
    def surfaces(blobs: dict) -> dict:
        log.append(sorted(blobs))
        out = {}
        for blob, data in blobs.items():
            text = data.decode("utf-8", "replace")
            if "SYNTAX ERROR" in text:
                out[blob] = {"surface": None, "mode": "error", "reason": "parse error CS1002 at line 1"}
                continue
            public = sorted(set(re.findall(r"public (?:class \w+|[\w<>?]+ \w+\([^)]*\)|[\w<>?]+ \w+ \{)", text)))
            out[blob] = {"surface": sha256("\n".join(public).encode()), "mode": "skeleton", "reason": ""}
        return out
    return surfaces


def self_test() -> int:
    import tempfile
    checks = 0

    def check(cond: bool, what: str) -> None:
        nonlocal checks
        checks += 1
        if not cond:
            raise AssertionError(what)

    # IVT extraction from metadata bytes — the attribute blob shape, and the over-approximation.
    blob = b"xxInternalsVisibleToAttribute\x00..\x01\x00\x08Lib.Test\x00\x00..\x01\x00\x1aFriend, PublicKey=00240000\x00\x00"
    check(friends_from_assembly(blob) == {"Lib.Test", "Friend"}, "IVT names (public key stripped)")
    check(friends_from_assembly(b"\x01\x00\x08Lib.Test\x00\x00") == set(), "no IVT attribute type ⇒ no friend")
    check(friends_from_assembly(b"InternalsVisibleToAttribute\x01\x00\x09Lib.Test\x00\x00") == set(), "a length that does not close is no blob")
    strong = "Friend.Strong, PublicKey=" + "0024000004800000940000000602000000240000525341310004000001000100" * 5
    n = len(strong.encode())
    check(n >= 0x80, "the fixture's strong name needs a TWO-byte compressed length")
    blob2 = b"InternalsVisibleToAttribute..\x01\x00" + bytes([0x80 | (n >> 8), n & 0xFF]) + strong.encode() + b"\x00\x00"
    check(friends_from_assembly(blob2) == {"Friend.Strong"}, "an IVT with a full PublicKey (2-byte SerString length) is read")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        _fixture(root)
        gitc = ["git", "-c", "user.email=t@t", "-c", "user.name=t"]
        subprocess.run(["git", "init", "-q", "-b", "main"], cwd=root, check=True)
        subprocess.run([*gitc, "add", "-A"], cwd=root, check=True)
        subprocess.run([*gitc, "commit", "-qm", "base"], cwd=root, check=True)
        base = git(root, "rev-parse", "HEAD").strip()
        check(friends_from_source(root, "src/Lib") == {"Lib.Test", "Friend"}, "csproj IVT items read")
        (root / "src/Directory.Build.targets").write_text('<Project><ItemGroup><InternalsVisibleTo Include="$(AssemblyName).Bench" />'
                                                         '</ItemGroup></Project>')
        check("Lib.Bench" in friends_from_source(root, "src/Lib"), "a Directory.Build.* grant naming the project's own name is expanded")
        (root / "src/Directory.Build.targets").write_text('<Project><ItemGroup><InternalsVisibleTo Include="$(Vendor).Tests" />'
                                                         '<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">'
                                                         '<_Parameter1>Lib.Probe</_Parameter1></AssemblyAttribute></ItemGroup></Project>')
        got_f = friends_from_source(root, "src/Lib")
        check(UNKNOWN_FRIEND in got_f and "Lib.Probe" in got_f, f"an unreadable grant is UNKNOWN; an AssemblyAttribute grant is read: {got_f}")
        (root / "src/Directory.Build.targets").unlink()
        check(own_dirs(root, "src/App/App.csproj") == {"src/App", "src/App.Test"}, "own dirs = entry + sibling .Test")

        units = [{"id": u, "project": f"src/{u}/{u}.csproj"} for u in ("Lib", "Lib.Test", "App", "App.Test", "Friend", "Friend.Test")]
        units.append({"id": "Census.Test", "project": "src/Census.Test/Census.Test.csproj", "reads": ["src/**/*.cs"]})

        def commit(msg: str) -> None:
            subprocess.run([*gitc, "add", "-A"], cwd=root, check=True)
            subprocess.run([*gitc, "commit", "-qm", msg], cwd=root, check=True)

        def run_probe(**kw) -> tuple[dict, list]:
            log: list = []
            v = probe(root, base, git(root, "rev-parse", "HEAD").strip(), changed_files(root, base), _fake_surfaces(log), **kw)
            return v, log

        # ── BRANCH 1: an internal-only change (a method body) — no build, one parse per file version ──
        (root / "src/Lib/A.cs").write_text("public class A { public int F() => 2; private int H() => 3; }\n")
        commit("internal")
        v, log = run_probe()
        lib = v["projects"]["src/Lib"]
        check(v["method"] == "syntax" and lib["eligible"] and lib["unchanged"], f"internal-only ⇒ unchanged: {lib}")
        check(lib["reason"].startswith("public API unchanged (surface ") and lib["refasm"] == lib["baseRefasm"], lib["reason"])
        check(len(log) == 1 and len(log[0]) == 2, f"ONE comparer call over the two versions of the one file — nothing built: {log}")
        d = decide(root, v, units, set())
        check(d["App"]["decision"] == "skip" and d["App.Test"]["decision"] == "skip", f"dependents skipped: {d}")
        check(d["App.Test"]["reason"] == f"public API unchanged (surface {lib['refasm'][:12]})", d["App.Test"]["reason"])
        check(d["Lib"]["decision"] == "keep" and d["Lib.Test"]["decision"] == "keep", "the changed module and its own suite run")
        check(d["Friend"]["decision"] == "keep" and "InternalsVisibleTo" in d["Friend"]["reason"], f"an IVT friend keys on the full assembly: {d['Friend']}")
        check(d["Friend.Test"]["decision"] == "keep" and "Friend sees" in d["Friend.Test"]["reason"], "…and so does every unit whose closure holds the friend")
        check(d["Census.Test"]["decision"] == "keep" and "as content" in d["Census.Test"]["reason"], "a census reader of src/**/*.cs is never skipped")
        dn = decide(root, v, units)
        check(all(x["decision"] == "keep" and "re-selection" in x["reason"] for x in dn.values()),
              "no --reselected evidence ⇒ nothing skips (a Directory.Build.props beside the edit is invisible here)")
        check("own project" in decide(root, v, [{"id": "LT", "project": "src/Lib.Test/Lib.Test.csproj"}], set())["LT"]["reason"],
              "a .Test entry owns its production sibling: Lib.Test keeps on an internal Lib change even without IVT")
        d2 = decide(root, v, units, reselected={"App.Test"})
        check(d2["App.Test"]["decision"] == "keep" and d2["App"]["decision"] == "skip", "a unit another changed path reaches stays")
        # The KEY (build hosts): equal across the internal change for a non-friend dependent, `full:` for a friend.
        build0, read0 = _fake_builder(root, [])
        build0("src/Lib", True)
        mh = {"src/Lib": read0("src/Lib")}
        ka = abi_key(root, "src/App.Test/App.Test.csproj", mh)
        check(ka["inputs"]["closure"]["src/Lib"].startswith("abi:"), "a non-friend dependency keys on abi:")
        check(ka["inputs"]["closure"]["src/App.Test"].startswith("tree:"), "own dirs key on their tree")
        kf = abi_key(root, "src/Friend.Test/Friend.Test.csproj", mh)
        check(kf["inputs"]["closure"]["src/Lib"].startswith("full:"), "a friend's closure keys D on full:")
        (root / "src/Lib/data.json").write_text('{"copied": "to output"}\n')
        kf2 = abi_key(root, "src/Friend.Test/Friend.Test.csproj", {"src/Lib": read0("src/Lib")})
        check(kf2["key"] != kf["key"], "full: carries the non-C# tree too — a content file moves a friend's key")
        subprocess.run(["git", "checkout", "-q", "--", "src/Lib/data.json"], cwd=root, check=True)
        (root / "src/Linker").mkdir()
        (root / "src/Linker/Linker.csproj").write_text('<Project><ItemGroup><ProjectReference Include="../Lib/Lib.csproj" />'
                                                       '<Compile Include="../Lib/A.cs" /></ItemGroup></Project>')
        kl = abi_key(root, "src/Linker/Linker.csproj", mh)
        check(kl["inputs"]["closure"]["src/Lib"].startswith("tree:"), "a unit LINKING a dependency's source keys it by tree")
        check(abi_key(root, "src/App.Test/App.Test.csproj", mh, reads=["src/Lib/**"])["inputs"]["closure"]["src/Lib"].startswith("tree:"),
              "…and so does a caller-declared read")
        (root / "src/Shared").mkdir()
        (root / "src/Shared/S.cs").write_text("class S { }\n")
        (root / "src/Linker/Linker.csproj").write_text('<Project><ItemGroup><ProjectReference Include="../App/App.csproj" />'
                                                       '<Compile Include="../Shared/S.cs" /></ItemGroup></Project>')
        k1 = abi_key(root, "src/Linker/Linker.csproj", mh)
        (root / "src/Shared/S.cs").write_text("class S { int x; }\n")
        k2 = abi_key(root, "src/Linker/Linker.csproj", mh)
        check(k1["key"] != k2["key"] and "../Shared/S.cs" not in str(k1["inputs"]["closure"]),
              "a linked file OUTSIDE the ProjectReference closure is hashed into the key on its own")
        fake = {"schema": SCHEMA, "projects": {"src/Lib": {"unchanged": True, "files": ["src/Lib/A.cs"], "refasm": "a" * 64,
                                                          "friends": []},
                                               "src/Shared": {"unchanged": True, "files": ["src/Shared/S.cs"], "refasm": "b" * 64,
                                                              "friends": []}}}
        wide = decide(root, fake, [{"id": "W", "project": "src/App.Test/App.Test.csproj", "closure": ["src/App.Test", "src/App", "src/Lib", "src/Shared"]}], set())["W"]
        check(wide["decision"] == "skip" and set(wide["dependencies"]) == {"src/Lib", "src/Shared"},
              f"a caller-supplied closure is the one decide judges: {wide}")
        dl = decide(root, fake, [{"id": "L", "project": "src/Linker/Linker.csproj"}], set())["L"]
        check(dl["decision"] == "keep" and "src/Shared/S.cs" in dl["reason"],
              f"decide keeps a unit that links a probed file outside its closure: {dl}")
        for p in ("src/Linker/Linker.csproj", "src/Shared/S.cs"):
            (root / p).unlink()
        (root / "src/Linker").rmdir()
        (root / "src/Shared").rmdir()
        app_csproj = (root / "src/App/App.csproj").read_text()
        (root / "src/App/App.csproj").write_text(app_csproj.replace("<ItemGroup>", "<PropertyGroup><AssemblyName>$(Prefix)App</AssemblyName></PropertyGroup><ItemGroup>", 1))
        check(assembly_names(root, "src/App") == {UNKNOWN_ASSEMBLY}, "a property-built AssemblyName is UNRESOLVED, never the file name")
        du = decide(root, {"schema": SCHEMA, "projects": {"src/Lib": {"unchanged": True, "files": ["src/Lib/A.cs"],
                                                                      "refasm": "a" * 64, "friends": ["Friend"]}}},
                    [{"id": "AT", "project": "src/App.Test/App.Test.csproj"}], set())["AT"]
        check(du["decision"] == "keep" and "cannot be ruled out" in du["reason"],
              f"an unresolved assembly name beside an IVT grant keeps the unit: {du}")
        check(abi_key(root, "src/App.Test/App.Test.csproj", mh)["inputs"]["closure"]["src/Lib"].startswith("full:"),
              "…and keys the IVT-granting dependency on full:")
        dk = decide(root, {"schema": SCHEMA, "projects": {"src/Lib": {"unchanged": True, "files": ["src/Lib/A.cs"],
                                                                      "refasm": "a" * 64, "friends": [UNKNOWN_FRIEND]}}},
                    [{"id": "AT", "project": "src/App.Test/App.Test.csproj"}], set())["AT"]
        check(dk["decision"] == "keep" and "cannot be read without a build" in dk["reason"],
              f"an unresolved InternalsVisibleTo grant keeps every dependent: {dk}")
        (root / "src/App/App.csproj").write_text(app_csproj)

        # ── BRANCH 2: a public-API change ──
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Lib/A.cs").write_text("public class A { public int F() => 2; public int F2() => 4; }\n")
        commit("api")
        v, _ = run_probe()
        lib = v["projects"]["src/Lib"]
        check(lib["eligible"] and not lib["unchanged"] and lib["reason"].startswith("public API changed"), f"API change ⇒ changed: {lib}")
        check(all(x["decision"] == "keep" for x in decide(root, v, units, set()).values()), "API change ⇒ every dependent rebuilds and tests")

        # ── non-C# input: never eligible, nothing parsed ──
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Lib/data.json").write_text('{"x": 1}\n')
        commit("content")
        v, log = run_probe()
        check(not v["projects"]["src/Lib"]["eligible"] and log == [] and "non-C#" in v["projects"]["src/Lib"]["reason"],
              f"a content edit is not a surface question: {v['projects']['src/Lib']}")
        check(decide(root, v, units, set())["App.Test"]["decision"] == "keep", "…so dependents rebuild")

        # ── Razor markup: not judged by C# syntax — never eligible (a [Parameter] there IS surface) ──
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Lib/V.razor").write_text("<div>hello</div>\n@code { private int n = 1; }\n")
        commit("a razor view")
        v, log = run_probe()
        check(not v["projects"]["src/Lib"]["eligible"] and log == [], f"a .razor edit is never eligible: {v['projects']['src/Lib']}")

        # ── an ADDED (or deleted) .cs file: an API change, nothing parsed for it ──
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Lib/New.cs").write_text("class Hidden { }\n")
        commit("new file")
        v, _ = run_probe()
        check(not v["projects"]["src/Lib"]["unchanged"] and "added or deleted" in v["projects"]["src/Lib"]["reason"],
              f"a new file is a change: {v['projects']['src/Lib']}")

        # ── MSBuild's evaluation → compiler input (the build hosts' manifest still reads it) ──
        def item(rel_path: str, **meta) -> dict:
            return {"FullPath": str(root / rel_path), **meta}
        evaluation = {"Compile": [item("src/Lib/A.cs")],
                      "Content": [item("src/Lib/V.razor"), item("src/Lib/Page.cshtml", CopyToPublishDirectory="PreserveNewest"),
                                  item("src/Lib/data.json", CopyToOutputDirectory="PreserveNewest")],
                      "EmbeddedResource": [item("src/Lib/Embedded.cshtml")]}
        razor_props = {"UsingMicrosoftNETSdkRazor": "true"}
        got = code_from_evaluation(root, root / "src/Lib", razor_props, evaluation)
        check(got == {"src/Lib/A.cs", "src/Lib/V.razor"},
              f"Razor SDK: Compile + uncopied .razor are code; a PreserveNewest .cshtml, an embedded one and content are payload: {got}")
        check(code_from_evaluation(root, root / "src/Lib", {}, evaluation) == {"src/Lib/A.cs"},
              "a plain Microsoft.NET.Sdk project: its .razor Content is payload")

        # ── C# that may be PAYLOAD, not compiler input: never eligible ──
        (root / "src/Doc/Data").mkdir(parents=True)
        (root / "src/Doc/Doc.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Remove="Data/**" />'
                                                 '<EmbeddedResource Include="Data/**" /></ItemGroup></Project>')
        (root / "src/Doc/D.cs").write_text("public class D { }\n")
        (root / "src/Doc/Data/Node.cs").write_text("public class N { }\n")
        commit("doc project")
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Doc/Data/Node.cs").write_text("// an embedded C# node\npublic class N { }\n")
        commit("embedded node")
        v, log = run_probe()
        doc = v["projects"]["src/Doc"]
        check(not doc["eligible"] and "may not be compiler input (<Compile Remove=" in doc["reason"] and log == [],
              f"embedded C# payload is not a surface question: {doc}")
        check(payload_item(root, "src/Doc", "src/Doc/D.cs") is None, "…while a .cs the item does not cover is plain compiler input")
        (root / "src/Fx").mkdir()
        (root / "src/Fx/Fx.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><None Include="Fixtures\\**" CopyToOutputDirectory="Always" />'
                                                '<EmbeddedResource Include="$(SomeRoot)Hosting/Console.json" /></ItemGroup></Project>')
        check(payload_item(root, "src/Fx", "src/Fx/F.cs") is None, "a fixture/resource include that cannot cover the file leaves it eligible")
        check(payload_item(root, "src/Fx", "src/Fx/Fixtures/Sample.cs") == '<None Include="Fixtures/**">', "…and one that covers it does not")
        (root / "src/Fx/Fx.csproj").write_text('<Project><ItemGroup><Content Include="$(Root)/**/*" /></ItemGroup></Project>')
        check(payload_item(root, "src/Fx", "src/Fx/F.cs") is not None, "an unexpanded property in a wildcard include over-approximates: payload")
        (root / "src/Fx/Fx.csproj").write_text('<Project><PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>')
        check(payload_item(root, "src/Fx", "src/Fx/F.cs") == "EnableDefaultCompileItems=false", "default compile items off: not judged")
        (root / "src/Fx/Fx.csproj").unlink()
        (root / "src/Fx").rmdir()

        # ── a body-driven generator (interceptors) switched on: never eligible ──
        (root / "src/Gen").mkdir()
        (root / "src/Gen/Gen.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                                                 '<InterceptorsNamespaces>$(InterceptorsNamespaces);X</InterceptorsNamespaces>'
                                                 '</PropertyGroup></Project>')
        (root / "src/Gen/G.cs").write_text("public class G { public int F() => 1; }\n")
        commit("generator project")
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Gen/G.cs").write_text("public class G { public int F() => 2; }\n")
        commit("generator body")
        v, log = run_probe()
        check(not v["projects"]["src/Gen"]["eligible"] and "interceptors" in v["projects"]["src/Gen"]["reason"] and log == [],
              f"an interceptor-enabled project is not judged by syntax: {v['projects']['src/Gen']}")

        # ── a head that does not parse: changed, its dependents keep ──
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Lib/A.cs").write_text("public class A { public int F() => 10; public int F2() => 4; } // SYNTAX ERROR\n")
        commit("broken head")
        v, _ = run_probe()
        check(not v["projects"]["src/Lib"]["unchanged"] and "parse error" in v["projects"]["src/Lib"]["reason"],
              f"a file that does not parse narrows nothing: {v['projects']['src/Lib']}")
        check(decide(root, v, units, set())["App"]["decision"] == "keep", "…its dependents keep")
        # …and a comparer that cannot answer at all makes the whole verdict `unavailable`.
        def broken(_: dict) -> dict:
            raise RuntimeError("the syntax comparer did not build")
        vb = probe(root, base, git(root, "rev-parse", "HEAD").strip(), changed_files(root, base), broken)
        check("could not answer" in (vb.get("unavailable") or "") and vb["projects"] == {}, f"a comparer failure is unavailable: {vb}")

        # ── rule 7: an input every build imports short-circuits BEFORE anything is parsed ──
        (root / "src/Lib/A.cs").write_text("public class A { public int F() => 10; public int F2() => 4; }\n")
        commit("repair the head for the short-circuit cases")

        def sc_case(label: str, writes: dict[str, str], expect: str | None) -> dict:
            nonlocal base
            subprocess.run(["git", "checkout", "-q", "--", "."], cwd=root, check=True)
            base = git(root, "rev-parse", "HEAD").strip()
            (root / "src/Lib/A.cs").write_text(f"public class A {{ public int F() => {len(label)}; public int F2() => 4; }}\n")
            for rel, text in writes.items():
                (root / rel).parent.mkdir(parents=True, exist_ok=True)
                (root / rel).write_text(text)
            commit(label)
            v, log = run_probe()
            if expect is None:
                check("unavailable" not in v and log, f"{label}: probes (parses): {v.get('unavailable')} {log}")
            else:
                check(expect in (v.get("unavailable") or "") and "nothing narrowed" in v["unavailable"] and log == []
                      and v["projects"] == {} and v["schema"] == SCHEMA, f"{label}: short-circuited, nothing parsed: {v} {log}")
            return v
        sc_case("a .cs-only change still probes", {}, None)
        v = sc_case("Directory.Build.props beside a .cs edit", {"src/Directory.Build.props": "<Project />\n"}, "src/Directory.Build.props changed")
        check(all(x["decision"] == "keep" for x in decide(root, v, units, set()).values()),
              "an unavailable verdict narrows nothing: every unit keeps")
        sc_case("a split pin file at any depth", {"src/Lib/Directory.Packages.Extra.props": "<Project />\n"}, "src/Lib/Directory.Packages.Extra.props")
        sc_case("global.json", {"global.json": "{}\n"}, "global.json changed")
        sc_case("NuGet.Config, any case", {"NuGet.Config": "<configuration />\n"}, "NuGet.Config changed")
        sc_case("the platform floor", {"scripts/platform-floor.json": '{"major": 4}\n'}, "scripts/platform-floor.json changed")
        sc_case("a TargetFramework retarget", {"src/App/App.csproj": (root / "src/App/App.csproj").read_text().replace(
            "<ItemGroup>", "<PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup>", 1)},
            "the target framework in src/App/App.csproj")
        # Negative control: the same range WITHOUT the csproj — the old behaviour — is probed.
        nlog: list = []
        probe(root, base, git(root, "rev-parse", "HEAD").strip(),
              [f for f in changed_files(root, base) if not f.endswith(".csproj")], _fake_surfaces(nlog))
        check(nlog != [], f"negative control — without the short-circuit the same .cs edit is probed: {nlog}")

        # ── rule 8: a deadline the work cannot meet starts nothing ──
        sc_case("rebase the budget case on a plain .cs edit", {}, None)
        v, log = run_probe(deadline=1000.0 + 10, clock=lambda: 1000.0)
        check("exceed the 10 s left" in (v.get("unavailable") or "") and log == [],
              f"the comparer's build + parses > 10 s ⇒ unavailable, NOTHING started: {v.get('unavailable')} {log}")
        v, log = run_probe(deadline=1000.0 + 600, clock=lambda: 1000.0)
        check("unavailable" not in v and log, "negative control — with time left the same range is probed")

        # ── the surface cache: content-addressed, authoritative only under its full key, never load-bearing ──
        cache = SurfaceCache(root / "cache")
        key = surface_key("b" * 40, True, "t" * 64)
        check(cache.get(key) is None and cache.misses == 1, "an empty cache misses")
        cache.put(key, {"surface": "s" * 64, "mode": "skeleton", "reason": ""})
        check((cache.get(key) or {}).get("surface") == "s" * 64 and cache.hits == 1, "a stored surface is a hit under its key")
        check(surface_key("b" * 40, True, "u" * 64) != key and surface_key("b" * 40, False, "t" * 64) != key
              and surface_key("c" * 40, True, "t" * 64) != key, "the comparer's content, strip and the blob each move the key")
        path = root / "cache/surfaces" / key[:2] / f"{key}.json"
        path.write_text(json.dumps({"key": "x" * 64, "surface": "forged", "mode": "skeleton", "reason": ""}))
        check(cache.get(key) is None, "an entry that does not name its full key is a MISS, never a verdict")
        path.write_text("{not json")
        check(cache.get(key) is None, "an unreadable entry is a miss")
        dead = SurfaceCache(root / "src/Lib/A.cs/not-a-dir")
        dead.put(key, {"surface": "s"})
        check(dead.get(key) is None, "an unwritable/unreachable cache only means parsing")
    print(f"abi-key: {checks} self-test assertions passed")
    return 0


# Real-Roslyn cases: (label, base source, head source, expect unchanged?). Run by --self-test-syntax (needs an SDK).
SYNTAX_CASES: list[tuple[str, str, str, bool]] = [
    ("a method body edit", "public class A { public int F() { return 1; } }", "public class A { public int F() { return 2 + 3; } }", True),
    ("an expression body edit", "public class A { public int F() => 1; }", "public class A { public int F() => 2; }", True),
    ("an accessor body edit", "public class A { int _x; public int P { get { return _x; } set { _x = value; } } }",
     "public class A { int _x; public int P { get { return _x + 1; } set { } } }", True),
    ("a field / property initializer edit", "public class A { private int _x = 1; public int P { get; } = 2; }",
     "public class A { private int _x = 5; public int P { get; } = 9; }", True),
    ("a constructor initializer and body edit", "public class A : B { public A(int x) : base(x) { } }",
     "public class A : B { public A(int x) : base(x + 1) { Log(); } }", True),
    ("comments and whitespace", "public class A { public int F() => 1; }", "// note\npublic   class A {\n  /// doc\n  public int F() => 1;\n}", True),
    ("a local function inside a body", "public class A { public int F() { return 1; } }",
     "public class A { public int F() { int G() => 2; return G(); } }", True),
    ("a signature change", "public class A { public int F(int a) => a; }", "public class A { public int F(long a) => 1; }", False),
    ("a modifier change", "public class A { public int F() => 1; }", "public class A { public virtual int F() => 1; }", False),
    ("private to public", "public class A { private int F() => 1; }", "public class A { public int F() => 1; }", False),
    ("an attribute change", "public class A { public int F() => 1; }", "public class A { [Obsolete] public int F() => 1; }", False),
    ("a default parameter value", "public class A { public int F(int a = 1) => a; }", "public class A { public int F(int a = 2) => a; }", False),
    ("a const value", "public class A { public const int C = 1; }", "public class A { public const int C = 2; }", False),
    ("an enum value", "public enum E { X = 1 }", "public enum E { X = 2 }", False),
    ("a new private field (struct layout)", "public struct S { public int A; }", "public struct S { public int A; private int b; }", False),
    ("an auto-property becomes computed (a backing field goes)", "public struct S { public int P { get; } }",
     "public struct S { public int P => 1; }", False),
    ("a body becomes an iterator", "public class A { public System.Collections.Generic.IEnumerable<int> F() { return new int[0]; } }",
     "public class A { public System.Collections.Generic.IEnumerable<int> F() { yield return 1; } }", False),
    ("an #if edit", "public class A {\n#if DEBUG\n public void D() { }\n#endif\n}", "public class A {\n#if TRACE\n public void D() { }\n#endif\n}", False),
    ("an edit inside a disabled region", "public class A {\n#if NEVER\n public void D() { }\n#endif\n}",
     "public class A {\n#if NEVER\n public void D(int x) { }\n#endif\n}", False),
    ("a partial type: a body edit in one part", "public partial class A { public int F() => 1; }", "public partial class A { public int F() => 2; }", True),
    ("a partial type: a declaration change in one part", "public partial class A { public int F() => 1; }",
     "public partial class A { public int F() => 1; public int G() => 2; }", False),
    ("a generator input ([GeneratedRegex]): any edit", "public partial class A { [GeneratedRegex(\"a\")] private static partial Regex R(); public int F() => 1; }",
     "public partial class A { [GeneratedRegex(\"a\")] private static partial Regex R(); public int F() => 2; }", False),
    ("a struct with a primary constructor keeps its bodies", "public struct S(int x) { public int F() => 1; }",
     "public struct S(int x) { public int F() => x; }", False),
    ("a record struct: a body edit (positional parameters are properties, never captured)",
     "public readonly record struct D(int Start) { public static D Of(int a) { return new D(a); } }",
     "public readonly record struct D(int Start) { public static D Of(int a) { if (a > 0) a--; return new D(a); } }", True),
    ("a plain using added at the top of the file", "using System;\npublic class A { public int F() => 1; }",
     "using System;\nusing System.Linq;\npublic class A { public int F() => 1; }", True),
    ("a using added INSIDE a namespace block (an inner scope can shadow)", "namespace N { public class A { public Foo F() => null; } }",
     "namespace N { using B; public class A { public Foo F() => null; } }", False),
    ("a using alias", "public class A { public Foo F() => null; }", "using Foo = B.Bar;\npublic class A { public Foo F() => null; }", False),
    ("a using static", "public class A { public int F(int a = 1) => a; }", "using static B.C;\npublic class A { public int F(int a = 1) => a; }", False),
    ("a parse error", "public class A { public int F() => 1; }", "public class A { public int F() => ; ", False),
    ("top-level statements", "System.Console.WriteLine(1);", "System.Console.WriteLine(2);", False),
]


def syntax_self_test() -> int:
    """The REAL comparer (built from abi-syntax/ on this host's SDK) over SYNTAX_CASES, the cache's hit path, and a
    negative control: with stripping off, a body-only edit reads as a change — so `unchanged` comes from the
    stripping, never from a comparer that always says equal."""
    import tempfile
    checks = 0
    with tempfile.TemporaryDirectory() as tmp:
        cache_dir = Path(tmp) / "cache"
        blobs: dict[str, bytes] = {}
        for _, b, h, _ in SYNTAX_CASES:
            for src in (b, h):
                data = src.encode()
                blobs[sha256(data)[:40]] = data
        cache = SurfaceCache(cache_dir)
        got = roslyn_surfaces(cache, cache_dir)(blobs)
        failed = []
        for label, b, h, same in SYNTAX_CASES:
            bs, hs = got[sha256(b.encode())[:40]], got[sha256(h.encode())[:40]]
            unchanged = bool(bs.get("surface")) and bs.get("surface") == hs.get("surface")
            checks += 1
            if unchanged != same:
                failed.append(f"{label}: expected {'unchanged' if same else 'changed'}, got {bs} / {hs}")
        if cache.misses != len(blobs) or cache.hits != 0:
            failed.append(f"a cold cache misses every blob: {cache.hits} hit(s), {cache.misses} miss(es)")
        warm = SurfaceCache(cache_dir)
        again = roslyn_surfaces(warm, cache_dir)(blobs)
        checks += 2
        if warm.hits != len(blobs) or warm.misses != 0 or again != got:
            failed.append(f"a warm cache answers every blob, identically: {warm.hits}/{len(blobs)} hit(s)")
        body = next(c for c in SYNTAX_CASES if c[0] == "a method body edit")
        raw = roslyn_surfaces(SurfaceCache(None), cache_dir, strip=False)({"b" * 40: body[1].encode(), "h" * 40: body[2].encode()})
        checks += 1
        if raw["b" * 40]["surface"] == raw["h" * 40]["surface"]:
            failed.append("negative control — with stripping OFF a body edit must read as a change")
    if failed:
        print("abi-key syntax self-test FAILED:\n  " + "\n  ".join(failed))
        return 1
    print(f"abi-key: {checks} syntax self-test assertions passed (real Roslyn comparer, {len(SYNTAX_CASES)} cases)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", nargs="?", choices=["manifest", "probe", "decide", "key"])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--self-test-syntax", action="store_true", help="build the real Roslyn comparer and run its cases")
    ap.add_argument("--root", default=".")
    ap.add_argument("--project", action="append", default=[])
    ap.add_argument("--configuration", default="Release")
    ap.add_argument("--msbuild-arg", action="append", default=[])
    ap.add_argument("--glob", action="store_true")
    ap.add_argument("--range", dest="diff_range")
    ap.add_argument("--base")
    ap.add_argument("--verdict")
    ap.add_argument("--units")
    ap.add_argument("--reselected")
    ap.add_argument("--manifest")
    ap.add_argument("--platform", default="")
    ap.add_argument("--read", action="append", default=[], help="key: a caller-declared read (prefix or glob) of the unit")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--deadline", type=float, help="probe: epoch seconds by which it must answer (rule 8)")
    ap.add_argument("--cache-dir", help="probe: the content-addressed surface + comparer cache (a shared store's directory)")
    ap.add_argument("--no-strip", action="store_true", help="probe: compare full token streams (a negative control)")
    ap.add_argument("--out")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if a.self_test_syntax:
        return syntax_self_test()
    if not a.command:
        ap.error("a command (manifest | probe | decide | key) or --self-test is required")
    root = Path(a.root).resolve()

    def emit(obj) -> None:
        text = json.dumps(obj, indent=2, sort_keys=True)
        if a.out:
            Path(a.out).write_text(text + "\n")
        print(text)

    try:
        if a.command == "manifest":
            if not a.project:
                ap.error("manifest needs --project")
            emit(build_manifest(root, a.project, a.configuration, a.msbuild_arg, a.glob))
        elif a.command == "probe":
            base, head = resolve_range(root, a.diff_range, a.base)
            cache_dir = Path(a.cache_dir) if a.cache_dir else None
            cache = SurfaceCache(cache_dir)
            verdict = probe(root, base, head, changed_files(root, base),
                            roslyn_surfaces(cache, cache_dir, strip=not a.no_strip), a.deadline)
            verdict["cache"] = {"hits": cache.hits, "misses": cache.misses}
            print(f"abi-key: surface cache {cache.hits} hit(s), {cache.misses} miss(es)", file=sys.stderr)
            emit(verdict)
        elif a.command == "decide":
            if not a.verdict or not a.units:
                ap.error("decide needs --verdict and --units")
            re_ids = set(json.loads(Path(a.reselected).read_text())) if a.reselected else None
            emit(decide(root, json.loads(Path(a.verdict).read_text()), json.loads(Path(a.units).read_text()), re_ids))
        else:
            if len(a.project) != 1 or not a.manifest:
                ap.error("key needs exactly one --project and --manifest")
            got = abi_key(root, a.project[0], json.loads(Path(a.manifest).read_text()), a.platform, a.read)
            print(json.dumps(got, indent=2, sort_keys=True) if a.json else got["key"])
    except (ValueError, RuntimeError, OSError) as exc:
        print(f"abi-key: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
