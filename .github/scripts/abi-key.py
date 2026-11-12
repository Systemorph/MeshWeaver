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
  * internal-only change (D's reference assembly byte-identical at base and head) — D and D's own
    suite (its sibling `<D>.Test`) compile and test; every other dependent is SKIPPED with the reason
    `public API unchanged (ref asm <hash>)`.
  * public-API change (reference assembly differs — this also catches the optional-constructor-
    parameter BINARY break AGENTS.md warns about, because the ctor signature is public surface) —
    dependents rebuild and run their tests as before.
  * full runs (schedule, platform releases, trunk publication) never consult this: the CALLER only
    applies a verdict on a pull request / merge-group diff.

WHAT MAKES A SKIP SOUND — every rule below errs toward REBUILD
-------------------------------------------------------------
  1. ELIGIBLE ONLY FOR C# BODY EDITS. D is eligible only when EVERY changed file under D is `*.cs`.
     A changed csproj, resx, json, md, razor, wwwroot asset, embedded resource or content file makes
     D "changed" without building anything: those reach a dependent through paths the reference
     assembly does not describe (content copied to output, a resource stream, a package reference
     that flows transitively). Hence the ABI identity is `sha256(ref asm || non-C# tree hash)`.
  2. INTERNALSVISIBLETO. A dependent X that sees D's internals (D declares InternalsVisibleTo X)
     keys on D's FULL implementation assembly, never the reference assembly: a unit is never skipped
     when ANY project in its closure is a friend of D. Friends are the UNION of (a) the compiled
     assembly's InternalsVisibleTo attribute blobs (base and head), (b) csproj
     `<InternalsVisibleTo Include=…>` items and (c) `[assembly: InternalsVisibleTo("…")]` in D's
     sources — over-approximation is the safe direction.
  3. DETERMINISM IS MEASURED, NOT ASSUMED. The probe builds D at head, swaps D's changed files to
     the base, rebuilds D, restores head and rebuilds D AGAIN; if the two head reference assemblies
     differ the toolchain is not deterministic for D and EVERY project is reported changed. (Roslyn
     emits a deterministic reference assembly whose MVID is derived from its content when
     Deterministic=true, the SDK default; both builds run in the same directory, so absolute paths
     — which DO enter the implementation assembly's MVID — cannot differ.)
  4. CONTENT READERS. A unit whose declared inputs or linked `../` includes READ one of D's changed
     files as text (a census ratchet over `src/**/*.cs`) is never skipped — it reads source, not
     surface.
  5. ANYTHING ELSE IN THE DIFF. The verdict speaks only for D's files. The CALLER must re-select on
     the diff WITHOUT them (`decide --reselected`): a unit any other changed path still reaches stays.
  6. A base that does not build against the head's dependencies, a head that does not build, an
     unreadable reference assembly — all mean "changed". A probe that cannot answer narrows nothing.

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
  abi-key.py probe --root R (--range A...B | --base SHA) [--configuration Release] [--msbuild-arg A …]
                   [--out verdict.json]
      → the VERDICT: builds each eligible changed project at head and base (swapping only that
        project's changed files in place, restoring them after) and compares reference assemblies:
        {schema, base, head, projects: {dir: {assembly, eligible, unchanged, refasm, baseRefasm,
        files[], friends[], reason}}, deterministic}. Leaves every probed project built at HEAD.
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


def friends_from_source(root: Path, rel_dir: str) -> set[str]:
    out: set[str] = set()
    base = root / rel_dir
    for csproj in base.glob("*.csproj"):
        for a, b in _IVT_ITEM.findall(csproj.read_text(encoding="utf-8", errors="replace")):
            out.add(_name(a or b))
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
    proc = subprocess.run(cmd, cwd=root, capture_output=True, text=True, timeout=600)
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


Builder = Callable[[str, bool], bool]   # (project dir, with_references) → built?
Reader = Callable[[str], dict]          # project dir → manifest entry


def dotnet_builder(root: Path, configuration: str, msbuild_args: list[str]) -> Builder:
    def build(rel_dir: str, with_references: bool) -> bool:
        csproj = sorted((root / rel_dir).glob("*.csproj"))[0]
        cmd = ["dotnet", "build", str(csproj), "-nologo", "-v:q", "-c", configuration,
               "-p:ProduceReferenceAssembly=true", "-p:Deterministic=true",
               # The SDK stamps the git commit into AssemblyInformationalVersion, which the REFERENCE
               # assembly carries too (measured: `1.0.0+<sha>` in obj/…/ref/<Asm>.dll). The probe builds
               # base and head at one HEAD, but a key compared ACROSS commits must not move with it.
               "-p:IncludeSourceRevisionInInformationalVersion=false", *msbuild_args]
        if not with_references:
            cmd.append("-p:BuildProjectReferences=false")
        proc = subprocess.run(cmd, cwd=root, capture_output=True, text=True, timeout=1800)
        if proc.returncode != 0:
            tail = "\n".join((proc.stdout + proc.stderr).strip().splitlines()[-15:])
            print(f"abi-key: dotnet build {rel_dir} ({'with' if with_references else 'without'} references) failed:\n{tail}",
                  file=sys.stderr)
        return proc.returncode == 0
    return build


def probe(root: Path, base: str, head: str, files: list[str], build: Builder, read: Reader) -> dict:
    """The verdict for every project owning a changed file (see the module docstring, rules 1–6)."""
    root = root.resolve()
    dirs = project_dirs(root)
    owned: dict[str, list[str]] = {}
    for f in files:
        d = owner_of(f, dirs)
        if d is not None:
            owned.setdefault(d, []).append(f)
    verdict: dict = {"schema": SCHEMA, "base": base, "head": head, "deterministic": True, "projects": {}}
    projects = verdict["projects"]
    eligible: list[str] = []
    for d, fs in sorted(owned.items()):
        names = sorted(assembly_names(root, d))
        entry = {"assembly": names[0] if names else Path(d).name, "files": sorted(fs), "eligible": False,
                 "unchanged": False, "refasm": "", "baseRefasm": "", "friends": sorted(friends_from_source(root, d))}
        other = [f for f in fs if not f.endswith(CODE_EXTS)]
        if other:
            entry["reason"] = f"a non-code input changed ({other[0]}{f' +{len(other) - 1}' if len(other) > 1 else ''}) — not described by the reference assembly"
        else:
            entry["eligible"] = True
            eligible.append(d)
        projects[d] = entry
    head_entries: dict[str, dict] = {}
    for d in eligible:
        if not build(d, True):
            projects[d].update(eligible=False, reason="did not build at head — the regular legs report why")
            continue
        try:
            head_entry = read(d)
        except (RuntimeError, OSError, ValueError, IndexError) as exc:
            projects[d].update(eligible=False, reason=f"head reference assembly unreadable: {exc}")
            continue
        # A `.cs` extension is not compiler input (review of core #7986): a project can embed C# as a
        # resource (`<Compile Remove="Data/**"/><EmbeddedResource Include="Data/**"/>`). Every changed
        # file must be an evaluated Compile item at head — a deleted one, absent a payload risk.
        code = set(head_entry["code"]) if head_entry.get("code") is not None else None
        payload = [f for f in projects[d]["files"]
                   if not (is_code(root, f, d, code) if (root / f).is_file() else not payload_risk(root, d))]
        if payload:
            projects[d].update(eligible=False, reason=f"{payload[0]} is not compiler input (resource/content payload) — "
                                                     "not described by the reference assembly")
            continue
        head_entries[d] = head_entry
    for d, head_entry in head_entries.items():
        entry = projects[d]
        saved: dict[str, bytes | None] = {}
        for f in entry["files"]:
            p = root / f
            saved[f] = p.read_bytes() if p.is_file() else None
        base_entry = None
        try:
            for f in entry["files"]:
                p = root / f
                show = subprocess.run(["git", "show", f"{base}:{f}"], cwd=root, capture_output=True, timeout=120)
                if show.returncode == 0:
                    p.parent.mkdir(parents=True, exist_ok=True)
                    p.write_bytes(show.stdout)
                elif p.exists():
                    p.unlink()   # added by the change: absent at base
            if build(d, False):
                try:
                    base_entry = read(d)
                except (RuntimeError, OSError, ValueError, IndexError):
                    base_entry = None
        finally:
            for f, data in saved.items():
                p = root / f
                if data is None:
                    if p.exists():
                        p.unlink()
                else:
                    p.parent.mkdir(parents=True, exist_ok=True)
                    p.write_bytes(data)
        again = None
        if build(d, False):
            try:
                again = read(d)
            except (RuntimeError, OSError, ValueError, IndexError):
                again = None
        entry["refasm"] = head_entry["refasm"]
        entry["friends"] = sorted(set(entry["friends"]) | set(head_entry.get("friends") or [])
                                  | set((base_entry or {}).get("friends") or []))
        if again is None or again["refasm"] != head_entry["refasm"]:
            verdict["deterministic"] = False
            entry["reason"] = "the head's reference assembly did not rebuild byte-identical — not deterministic"
            continue
        if base_entry is None:
            entry["reason"] = "the base did not build against the head's dependencies — treated as an API change"
            continue
        entry["baseRefasm"] = base_entry["refasm"]
        if base_entry["abi"] == head_entry["abi"]:
            entry["unchanged"] = True
            entry["reason"] = f"{SKIP_PREFIX} (ref asm {head_entry['refasm'][:12]})"
        else:
            entry["reason"] = f"public API changed (ref asm {base_entry['refasm'][:12]} → {head_entry['refasm'][:12]})"
    if not verdict["deterministic"]:
        for entry in projects.values():
            if entry["unchanged"]:
                entry["unchanged"] = False
                entry["reason"] = "withdrawn: another project's reference assembly was not deterministic in this run"
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
                if UNKNOWN_ASSEMBLY in others and friends:
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
            out[uid] = {"decision": "skip", "reason": f"{SKIP_PREFIX} (ref asm {hashes})",
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
        if friends & others or (friends and UNKNOWN_ASSEMBLY in others):
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
        subprocess.run(["git", "init", "-q", "-b", "main"], cwd=root, check=True)
        subprocess.run(["git", "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A"], cwd=root, check=True)
        subprocess.run(["git", "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "base"], cwd=root, check=True)
        base = git(root, "rev-parse", "HEAD").strip()
        check(friends_from_source(root, "src/Lib") == {"Lib.Test", "Friend"}, "csproj IVT items read")
        check(own_dirs(root, "src/App/App.csproj") == {"src/App", "src/App.Test"}, "own dirs = entry + sibling .Test")

        units = [{"id": u, "project": f"src/{u}/{u}.csproj"} for u in ("Lib", "Lib.Test", "App", "App.Test", "Friend", "Friend.Test")]
        units.append({"id": "Census.Test", "project": "src/Census.Test/Census.Test.csproj", "reads": ["src/**/*.cs"]})

        def commit(msg: str) -> None:
            subprocess.run(["git", "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qam", msg], cwd=root, check=True)

        def run_probe(nondet: set[str] | None = None) -> tuple[dict, list]:
            log: list = []
            build, read = _fake_builder(root, log, nondet)
            v = probe(root, base, git(root, "rev-parse", "HEAD").strip(), changed_files(root, base), build, read)
            return v, log

        # ── BRANCH 1: an internal-only change (a method body) ──
        (root / "src/Lib/A.cs").write_text("public class A { public int F() => 2; private int H() => 3; }\n")
        commit("internal")
        head_text = (root / "src/Lib/A.cs").read_text()
        v, log = run_probe()
        lib = v["projects"]["src/Lib"]
        check(v["deterministic"] and lib["eligible"] and lib["unchanged"], f"internal-only ⇒ unchanged: {lib}")
        check(lib["reason"].startswith("public API unchanged (ref asm ") and lib["refasm"] == lib["baseRefasm"], lib["reason"])
        check((root / "src/Lib/A.cs").read_text() == head_text, "the probe restores the head's files")
        check(log[0] == ("src/Lib", True) and log[1:] == [("src/Lib", False), ("src/Lib", False)],
              f"head with references, then base and head-again without: {log}")
        d = decide(root, v, units, set())
        check(d["App"]["decision"] == "skip" and d["App.Test"]["decision"] == "skip", f"dependents skipped: {d}")
        check(d["App.Test"]["reason"] == f"public API unchanged (ref asm {lib['refasm'][:12]})", d["App.Test"]["reason"])
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
        # The KEY: equal across the internal change for a non-friend dependent, `full:` for a friend.
        mh = {"src/Lib": _fake_builder(root, [])[1]("src/Lib")}
        ka = abi_key(root, "src/App.Test/App.Test.csproj", mh)
        check(ka["inputs"]["closure"]["src/Lib"].startswith("abi:"), "a non-friend dependency keys on abi:")
        check(ka["inputs"]["closure"]["src/App.Test"].startswith("tree:"), "own dirs key on their tree")
        kf = abi_key(root, "src/Friend.Test/Friend.Test.csproj", mh)
        check(kf["inputs"]["closure"]["src/Lib"].startswith("full:"), "a friend's closure keys D on full:")
        (root / "src/Lib/data.json").write_text('{"copied": "to output"}\n')
        kf2 = abi_key(root, "src/Friend.Test/Friend.Test.csproj", {"src/Lib": _fake_builder(root, [])[1]("src/Lib")})
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
        (root / "src/App/App.csproj").write_text(app_csproj)

        # ── BRANCH 2: a public-API change ──
        (root / "src/Lib/A.cs").write_text("public class A { public int F() => 2; public int F2() => 4; }\n")
        commit("api")
        v, _ = run_probe()
        lib = v["projects"]["src/Lib"]
        check(lib["eligible"] and not lib["unchanged"] and lib["reason"].startswith("public API changed"), f"API change ⇒ changed: {lib}")
        d = decide(root, v, units, set())
        check(all(x["decision"] == "keep" for x in d.values()), f"API change ⇒ every dependent rebuilds and tests: {d}")
        kb = abi_key(root, "src/App.Test/App.Test.csproj", {"src/Lib": _fake_builder(root, [])[1]("src/Lib")})
        check(kb["key"] != ka["key"], "the ABI key moves with the public surface")

        # ── internal again on top: the key returns to the internal-only shape relative to ITS base ──
        base = git(root, "rev-parse", "HEAD").strip()
        k_before = abi_key(root, "src/App.Test/App.Test.csproj", {"src/Lib": _fake_builder(root, [])[1]("src/Lib")})
        (root / "src/Lib/A.cs").write_text("public class A { public int F() => 9; public int F2() => 4; }\n")
        commit("internal 2")
        rebuild, reread = _fake_builder(root, [])
        rebuild("src/Lib", False)
        k_after = abi_key(root, "src/App.Test/App.Test.csproj", {"src/Lib": reread("src/Lib")})
        check(k_before["key"] == k_after["key"], "an internal-only change leaves a dependent's ABI key EQUAL")
        kl_before = abi_key(root, "src/Lib/Lib.csproj", {})
        check(kl_before["inputs"]["closure"]["src/Lib"].startswith("tree:"), "the changed project keys on its own tree")

        # ── non-C# input: never eligible, nothing built ──
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Lib/data.json").write_text('{"x": 1}\n')
        commit("content")
        v, log = run_probe()
        check(not v["projects"]["src/Lib"]["eligible"] and log == [] and "non-code" in v["projects"]["src/Lib"]["reason"],
              f"a content edit is not a reference-assembly question: {v['projects']['src/Lib']}")
        check(decide(root, v, units, set())["App.Test"]["decision"] == "keep", "…so dependents rebuild")

        # ── Razor markup: a body edit keeps the surface, a [Parameter] is surface ──
        (root / "src/Lib/V.razor").write_text("<div>hello</div>\n@code { private int n = 1; }\n")
        subprocess.run(["git", "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A"], cwd=root, check=True)
        commit("a razor view")
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Lib/V.razor").write_text("<section class=\"x\">hello again</section>\n@code { private int n = 2; }\n")
        commit("razor markup")
        v, _ = run_probe()
        check(v["projects"]["src/Lib"]["eligible"] and v["projects"]["src/Lib"]["unchanged"],
              f"a .razor markup/body edit is eligible and keeps the public surface: {v['projects']['src/Lib']}")
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Lib/V.razor").write_text("<section>@Title</section>\n@code { [Parameter] public string? Title { get; set; } }\n")
        commit("razor parameter")
        v, _ = run_probe()
        check(v["projects"]["src/Lib"]["eligible"] and not v["projects"]["src/Lib"]["unchanged"],
              f"a new [Parameter] in .razor is a public-API change: {v['projects']['src/Lib']}")
        (root / "src/Lib/V.razor.css").write_text(".x { color: red; }\n")
        subprocess.run(["git", "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A"], cwd=root, check=True)
        base = git(root, "rev-parse", "HEAD").strip()
        commit("scoped css")
        v, log = run_probe()
        check(not v["projects"]["src/Lib"]["eligible"] and log == [], "scoped CSS (.razor.css) is an asset, never eligible")
        check(not is_code(root, "src/Lib/V.razor", "src/Lib", None) and is_code(root, "src/Lib/A.cs", "src/Lib", None),
              "unevaluated (--glob): C# only — a .razor needs MSBuild's word that the Razor SDK compiles it")

        # ── MSBuild's evaluation → compiler input (the JSON `dotnet msbuild -getProperty -getItem` answers) ──
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
        check(code_from_evaluation(root, root / "src/Lib", {**razor_props, "EnableDefaultRazorComponentItems": "false"}, evaluation)
              == {"src/Lib/A.cs"}, "default Razor component items switched off: payload")
        # …and END TO END through probe(): a copied .cshtml edit (MVC/Razor Pages, PreserveNewest) is never
        # `unchanged` — the evaluated membership marks the project ineligible after the head build.
        (root / "src/Web").mkdir()
        (root / "src/Web/Web.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk.Razor"></Project>')
        (root / "src/Web/W.cs").write_text("public class W { }\n")
        (root / "src/Web/Page.cshtml").write_text("<p>one</p>\n")
        subprocess.run(["git", "-c", "user.email=t@t", "-c", "user.name=t", "add", "src/Web"], cwd=root, check=True)
        commit("web project")
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Web/Page.cshtml").write_text("<p>two</p>\n")
        commit("page edit")
        wlog: list = []
        wbuild, wread = _fake_builder(root, wlog)

        def web_read(d: str) -> dict:
            if d != "src/Web":
                return wread(d)
            paths = _glob_paths(root, sorted((root / d).glob("*.csproj"))[0], "Release")
            paths["code"] = sorted(code_from_evaluation(root, root / d, {"UsingMicrosoftNETSdkRazor": "true"}, {
                "Compile": [{"FullPath": str(root / "src/Web/W.cs")}],
                "Content": [{"FullPath": str(root / "src/Web/Page.cshtml"), "CopyToPublishDirectory": "PreserveNewest"}]}))
            return manifest_entry(root, d, paths)
        v = probe(root, base, git(root, "rev-parse", "HEAD").strip(), changed_files(root, base), wbuild, web_read)
        web = v["projects"]["src/Web"]
        check(not web["eligible"] and not web["unchanged"] and "not compiler input" in web["reason"],
              f"a copied .cshtml edit is payload end to end, never `unchanged`: {web}")

        # ── C# that is PAYLOAD, not compiler input: never eligible ──
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Doc/Data").mkdir(parents=True)
        (root / "src/Doc/Doc.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Remove="Data/**" />'
                                                 '<EmbeddedResource Include="Data/**" /></ItemGroup></Project>')
        (root / "src/Doc/D.cs").write_text("public class D { }\n")
        subprocess.run(["git", "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A"], cwd=root, check=True)
        commit("doc project")
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Doc/Data/Node.cs").write_text("// an embedded C# node\npublic class N { }\n")
        subprocess.run(["git", "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A"], cwd=root, check=True)
        commit("embedded node")
        v, _ = run_probe()
        doc = v["projects"]["src/Doc"]
        check(not doc["eligible"] and "not compiler input" in doc["reason"], f"embedded C# payload is not a surface question: {doc}")
        check(noncode_hash(root, "src/Doc") != noncode_hash(root, "src/Lib") and payload_risk(root, "src/Doc")
              and not payload_risk(root, "src/Lib"), "a payload-risk project hashes its .cs into the non-C# identity")

        # ── non-determinism withdraws every skip ──
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Lib/A.cs").write_text("public class A { public int F() => 10; public int F2() => 4; }\n")
        (root / "src/App/B.cs").write_text("public class B { public int G() => new A().F() + 1; }\n")
        commit("two")
        v, _ = run_probe(nondet={"src/App"})
        check(not v["deterministic"] and not v["projects"]["src/Lib"]["unchanged"], f"one non-deterministic project withdraws every skip: {v}")

        # ── a base that does not build: treated as an API change ──
        base = git(root, "rev-parse", "HEAD").strip()
        (root / "src/Lib/A.cs").write_text("public class A { public int F() => 10; public int F2() => 4; } // SYNTAX ERROR\n")
        commit("broken head")
        v, _ = run_probe()
        check(not v["projects"]["src/Lib"]["eligible"], "a head that does not build narrows nothing")
        check(decide(root, v, units, set())["App"]["decision"] == "keep", "…its dependents keep")
        try:
            decide(root, {"schema": "nope"}, units)
        except ValueError:
            checks += 1
        else:
            raise AssertionError("an unknown verdict schema must be refused")
    print(f"abi-key: {checks} self-test assertions passed")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", nargs="?", choices=["manifest", "probe", "decide", "key"])
    ap.add_argument("--self-test", action="store_true")
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
    ap.add_argument("--out")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
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
            build = dotnet_builder(root, a.configuration, a.msbuild_arg)

            def read(d: str) -> dict:
                return build_manifest(root, [d], a.configuration, a.msbuild_arg, a.glob)[d]

            emit(probe(root, base, head, changed_files(root, base), build, read))
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
