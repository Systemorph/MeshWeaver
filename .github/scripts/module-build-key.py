#!/usr/bin/env python3
"""module-build-key.py — the CONTENT ADDRESS of one module build (Plugins#889, #931).

(The name on this first line is load-bearing: the module-pack lane fetches this file at the
platform pin and refuses a body whose first 400 bytes do not name it — the same shape as
check-workflow-timeouts.py's check.)

WHY THIS EXISTS (maintainer, 2026-09-02: "if Plugin X was built against Platform version Y, we don't
have to rebuild this" · "we should not start the same build multiple times")
-----------------------------------------------------------------------------------------------------
A module bundle's bytes and its test verdict are a FUNCTION of a finite set of inputs. Name them,
hash them, and two runs that share the hash share the result — the second run can reuse the first
run's bundle, verdict and publication instead of paying 7–17 minutes to reproduce them. That
requires the hash to cover EVERYTHING the lane feeds the compiler and the test host, and nothing
else: an input left out makes two different builds collide (a silent under-build — the failure mode
`node-repo-scope.py` is biased against); an input that is really noise (a timestamp, a run id) makes
every build unique and the ledger useless.

THE KEY
-------
    K = sha256( canonical JSON of {
          recipe:          RECIPE_VERSION                (the lane's build recipe; bump on a byte-changing lane edit)
          package, module: the matrix entry's identity
          entry:           {build: sdk|container, accept: sorted tokens}
          moduleVersion:   <package>/manifest.lock → moduleVersion   (the package's own content hash, Plugins#878)
          closure:         {project dir → tree hash}  for the entry project, its sibling <dir>.Test
                           project (the lane RUNS it) and every in-repo ProjectReference either
                           reaches, transitively — module-owned MeshWeaver.* siblings RIDE the bundle
                           (module-owned-platform.sh), so their bytes are the bundle's bytes
          packages:        {package → moduleVersion}  for every package whose module project is in
                           that closure, and every package the entry package `requires`, transitively
          globals:         {path → sha256 | null}  for the repo-level build inputs that change what
                           EVERY project compiles (src/Directory.Build.props, platform-shipped.txt, …);
                           null records ABSENCE, which is an input too
          testerDigest, platformDigest, platformRef:  the compiler, the reference set, and the
                           platform source the tests build core from
          shelf:           ONLY when the caller supplies its own module-libraries shelf
                           (`--module-libraries-project`): {project, closure: {dir → tree hash} of
                           the shelf project and every in-repo project it reaches, imports:
                           {path → sha256 | null} of the MSBuild files it inherits from its own
                           directory up to the root}. The shelf is a compiler input — its deps.json
                           decides what a module binds — so a shelf edit must be a new build. Omitted,
                           the key is byte-identical to a key computed before the shelf existed.
        } )

A `$(MeshWeaverRoot)`-relative ProjectReference is the PLATFORM's and is never an edge here: the
platform enters the key through platformDigest (what the module compiles against) and platformRef
(what `dotnet test` builds from source) — not through a walk of another repository.

🚨 platformRef is in the key ON PURPOSE, and it is the lever a satellite pulls to get reuse: a caller
whose MW_PLATFORM_REF tracks `main` gets a new key on every core commit, because its test host really
does build core from that commit. Pin the ref to the promoted set's commit and identical trees key
identically across runs.

USAGE
-----
  module-build-key.py --root REPO --entry '<matrix entry JSON>' --tester-digest T --platform-digest D --platform-ref R
  module-build-key.py --root REPO --modules @matrix.json …      # every entry → JSON list on stdout
  … --module-libraries-project tools/Acme.ModuleLibraries/Acme.ModuleLibraries.csproj   # + the shelf
  module-build-key.py --self-test

Output: `--entry` prints the key alone (or `--json` for {key, inputs}); `--modules` prints a JSON
list of {package, module, key, inputs}. Exit 2 on a package with no manifest.lock — a package that
cannot be keyed must not be silently keyed on nothing.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path

# 🚨 Bump this when the LANE changes in a way that alters the bytes it packs or the verdict it
# records for the SAME source (a packer flag, a new closure rule, a different test invocation).
# Never for a cosmetic lane edit — that would throw away every recorded build for nothing.
# "1" → "2" (MeshWeaver#3211): the pack step now passes `--graph-dll <platform /app anchor>`, so a
# bundle's manifest carries `frameworkMvid` where it used to omit it. The bytes for the SAME source
# genuinely differ — and a REUSED pre-#3211 bundle would be refused at the publish step, which is the
# right verdict for those bytes and the wrong thing to spend a run discovering. One rebuild wave, and
# every recorded key afterwards attests bytes that state what they were built against.
# 3: the from-source arm STATES the framework identity as the platform commit (`g<sha>`)
# instead of deriving an MVID from a compiler DLL built per matrix job (#3308). Bundles packed
# under recipe 2 state an identity no consumer can match, so they must be REPACKED rather than
# reused — which is exactly what changing this constant forces.
# 4: the pack now MEASURES what the platform ships (`--platform-app`, the pinned image's extracted
# /app: its app closure, its surface manifest and its seeded modules/<Name>/ lane) and DROPS every
# MeshWeaver.* sibling that host already has, instead of trusting the repo's declared
# src/platform-shipped.txt (#3732). Bundles packed under recipe 3 carry those duplicates — 14 of
# MeshWeaver.Plugins' 37 carried 27 of them, measured 2026-09-08 — and a duplicate is exactly what
# makes one assembly name reach a process at two builds, so they must be REPACKED, never reused.
RECIPE_VERSION = "4"

# Repo-level files that change what EVERY project compiles or tests. Presence and content both
# count; a file the repo does not have hashes as null so adding it later changes the key.
GLOBAL_INPUTS = (
    "src/Directory.Build.props",
    "src/Directory.Build.targets",
    "src/platform-shipped.txt",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "global.json",
    "nuget.config",
    "NuGet.config",
    "NuGet.Config",
)

# Directories a tree hash never descends into: build outputs and editor state are not source.
SKIP_DIRS = {"bin", "obj", ".vs", "node_modules", "TestResults", "test-logs"}

_INCLUDE = r"<ProjectReference\s[^>]*Include=(?:\"([^\"]+)\"|'([^']+)')"
_COMPILE = r"<Compile\s[^>]*Include=(?:\"([^\"]+)\"|'([^']+)')"
# The project-relative MSBuild properties a linked item may start with; any other $(…) names a
# location outside this repository (the platform's), whose bytes the platform digest already keys.
_PROJECT_DIR_PROPS = ("$(MSBuildThisFileDirectory)", "$(MSBuildProjectDirectory)")


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_hash(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def tree_hash(root: Path, rel_dir: str) -> str:
    """sha256 over (relative path, content hash) of every source file under rel_dir, sorted."""
    base = root / rel_dir
    entries: list[str] = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        for name in sorted(filenames):
            p = Path(dirpath) / name
            rel = p.relative_to(base).as_posix()
            entries.append(f"{rel}={file_hash(p)}")
    return sha256_bytes("\n".join(entries).encode("utf-8"))


def project_references(root: Path, csproj: Path) -> list[Path]:
    """In-repo ProjectReference targets of csproj, resolved; $(…)-relative ones are the platform's."""
    text = csproj.read_text(encoding="utf-8", errors="replace")
    found: list[Path] = []
    for a, b in re.findall(_INCLUDE, text):
        include = (a or b).strip()
        if "$(" in include:
            continue
        target = (csproj.parent / include.replace("\\", "/")).resolve()
        try:
            target.relative_to(root.resolve())
        except ValueError:
            continue  # resolves outside this checkout — not this repo's edge
        if target.is_file():
            found.append(target)
    return found


def compile_includes(root: Path, csproj: Path) -> list[Path]:
    """In-repo files a project's explicit `<Compile Include>` items name, wildcards expanded.

    A project compiles more than its own directory: `<Compile Include="../Shared/Emitter.cs" />`
    links a file from elsewhere, and a directory tree hash never sees it."""
    text = csproj.read_text(encoding="utf-8", errors="replace")
    rroot = root.resolve()
    found: set[Path] = set()
    for a, b in re.findall(_COMPILE, text):
        for include in (a or b).split(";"):
            include = include.strip().replace("\\", "/")
            if not include:
                continue
            for prop in _PROJECT_DIR_PROPS:
                if include.startswith(prop):
                    include = include[len(prop):].lstrip("/")
                    break
            if "$(" in include or "@(" in include or "%(" in include:
                continue
            if any(c in include for c in "*?["):
                base = csproj.parent
                parts = Path(include).parts
                # walk the literal prefix (it may climb with ..) before handing the rest to glob
                i = 0
                while i < len(parts) and not any(c in parts[i] for c in "*?["):
                    base = base / parts[i]
                    i += 1
                rest = "/".join(parts[i:])
                candidates = list(base.resolve().glob(rest)) if base.is_dir() else []
            else:
                candidates = [csproj.parent / include]
            for c in candidates:
                c = c.resolve()
                try:
                    rel = c.relative_to(rroot)
                except ValueError:
                    continue  # outside this checkout — not this repo's bytes
                if c.is_file() and not any(part in SKIP_DIRS for part in rel.parts):
                    found.add(c)
    return sorted(found)


def closure_dirs(root: Path, entry_project: str) -> list[str]:
    """The entry project's dir, its sibling <dir>.Test, and every in-repo project either reaches."""
    root = root.resolve()
    start = [root / entry_project]
    test_dir = (root / entry_project).parent.with_name((root / entry_project).parent.name + ".Test")
    if test_dir.is_dir():
        start += sorted(test_dir.glob("*.csproj"))
    seen: set[Path] = set()
    queue = [p.resolve() for p in start if p.is_file()]
    while queue:
        p = queue.pop()
        if p in seen:
            continue
        seen.add(p)
        queue.extend(project_references(root, p))
    return sorted({p.parent.relative_to(root).as_posix() for p in seen})


# The MSBuild files a project inherits implicitly from its own directory and every ancestor up to
# the repository root (the first one found wins in MSBuild; hashing all of them over-keys, never
# under-keys). Absence of these names is recorded too. Every other `Directory.*.props|targets`
# beside them is hashed when present, because the implicit ones import split pin files such as
# `Directory.Packages.Plugins.props`, and a pin there is a shelf input as much as one inline.
SHELF_IMPORTS = ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props")


def shelf_inputs(root: Path, project: str, kind: str = "module-libraries-project") -> dict:
    """A caller-supplied compiler-input project as key inputs: its reference closure and its imports.

    Used for the module-libraries shelf and for a build extension (`build-extensions-project`):
    both are published per run and change what every module compiles against."""
    root = root.resolve()
    csproj = (root / project).resolve()
    if not csproj.is_file():
        raise ValueError(f"{kind} {project} does not exist under {root} — a compiler input "
                         "that cannot be keyed must not be silently keyed on nothing")
    seen: set[Path] = set()
    queue = [csproj]
    while queue:
        p = queue.pop()
        if p in seen:
            continue
        seen.add(p)
        queue.extend(project_references(root, p))
    dirs = sorted({p.parent.relative_to(root).as_posix() for p in seen})
    imports: dict[str, str | None] = {}
    for d in dirs:
        here = root / d
        while True:
            names = set(SHELF_IMPORTS) | {f.name for pattern in ("Directory.*.props", "Directory.*.targets")
                                          for f in here.glob(pattern) if f.is_file()}
            for name in sorted(names):
                rel = (here / name).relative_to(root).as_posix()
                if rel not in imports:
                    imports[rel] = file_hash(here / name) if (here / name).is_file() else None
            if here == root:
                break
            here = here.parent
    # Linked sources: a `<Compile Include>` naming a file outside every closure directory is
    # compiled (and published) with the project, so its bytes are an input too. Recorded only when
    # present, so a project that links nothing keys exactly as it did before this input existed.
    linked: dict[str, str] = {}
    for p in sorted(seen):
        for f in compile_includes(root, p):
            rel = f.relative_to(root).as_posix()
            if any(rel.startswith(d + "/") for d in dirs if d != "."):
                continue  # already inside a hashed closure directory
            linked[rel] = file_hash(f)
    result = {"project": Path(project).as_posix(),
              "closure": {d: tree_hash(root, d) for d in dirs},
              "imports": dict(sorted(imports.items()))}
    if linked:
        result["linked"] = dict(sorted(linked.items()))
    return result


def read_json(path: Path) -> dict | None:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def node_packages(root: Path) -> dict[str, dict]:
    """Top-level dirs carrying an index.json → their index (the caller's node packages)."""
    skip = {".git", ".github", ".claude", ".worktrees", "scripts", "src", "test", "docs", "e2e",
            "app", "clients", "legacy", "meshweaver", "tools"}
    out: dict[str, dict] = {}
    for d in sorted(root.iterdir()):
        if d.is_dir() and d.name not in skip and (d / "index.json").is_file():
            idx = read_json(d / "index.json")
            if isinstance(idx, dict):
                out[d.name] = idx
    return out


def module_version(root: Path, package: str) -> str | None:
    lock = read_json(root / package / "manifest.lock")
    if not isinstance(lock, dict):
        return None
    mv = lock.get("moduleVersion")
    return mv if isinstance(mv, str) and mv else None


def requires_of(index: dict) -> list[str]:
    """`content.requires` as package ids — strings, or objects naming id/package."""
    content = index.get("content") if isinstance(index.get("content"), dict) else {}
    req = content.get("requires") or index.get("requires") or []
    ids: list[str] = []
    for r in req if isinstance(req, list) else []:
        if isinstance(r, str):
            ids.append(r.split("@")[0].split("/")[-1])
        elif isinstance(r, dict):
            v = r.get("id") or r.get("package") or r.get("name")
            if isinstance(v, str):
                ids.append(v.split("/")[-1])
    return ids


def compute(root: Path, entry: dict, tester_digest: str, platform_digest: str, platform_ref: str,
            recipe: str = RECIPE_VERSION, shelf_project: str = "",
            extensions_project: str = "", extensions_sdk: str = "") -> dict:
    """{key, inputs} for one matrix entry. Raises KeyError-shaped ValueError on an unkeyable entry."""
    package = entry.get("package") or ""
    module = entry.get("module") or ""
    project = entry.get("project") or ""
    if not package or not module or not project:
        raise ValueError(f"matrix entry lacks package/module/project: {entry}")
    if not (root / project).is_file():
        raise ValueError(f"{module}: project {project} does not exist under {root}")
    own = module_version(root, package)
    if own is None:
        raise ValueError(f"{package}/manifest.lock is missing or carries no moduleVersion — the "
                         "package cannot be keyed (run scripts/gen-manifests.py)")

    dirs = closure_dirs(root, project)
    closure = {d: tree_hash(root, d) for d in dirs}

    packages = node_packages(root)
    reached: dict[str, str] = {}
    # packages whose module project lives in the closure — their moduleVersion covers their content
    for pid, idx in packages.items():
        content = idx.get("content") if isinstance(idx.get("content"), dict) else {}
        mod = content.get("module")
        if not isinstance(mod, str):
            continue
        if any(Path(d).name == mod for d in dirs) and pid != package:
            mv = module_version(root, pid)
            reached[pid] = mv or "<no manifest.lock>"
    # the requires chain, transitively
    queue = list(requires_of(packages.get(package, {})))
    seen: set[str] = set()
    while queue:
        pid = queue.pop()
        if pid in seen or pid == package:
            continue
        seen.add(pid)
        reached[pid] = module_version(root, pid) or "<no manifest.lock>"
        queue.extend(requires_of(packages.get(pid, {})))

    globals_: dict[str, str | None] = {}
    for rel in GLOBAL_INPUTS:
        p = root / rel
        globals_[rel] = file_hash(p) if p.is_file() else None

    accept = " ".join(sorted((entry.get("accept") or "").split()))
    inputs = {
        "recipe": recipe,
        "package": package,
        "module": module,
        "entry": {"build": entry.get("build") or "sdk", "accept": accept},
        "moduleVersion": own,
        "closure": closure,
        "packages": dict(sorted(reached.items())),
        "globals": globals_,
        "testerDigest": tester_digest or "",
        "platformDigest": platform_digest or "",
        "platformRef": platform_ref or "",
    }
    # Only when set: a caller without its own shelf keys exactly as before, so no recorded build
    # is thrown away by this input's arrival.
    if shelf_project:
        inputs["shelf"] = shelf_inputs(root, shelf_project)
    # The same for a build extension (e.g. the Razor one): it decides what the compiler GENERATES,
    # so an edit to it is a new build. Absent ⇒ the key is byte-identical to one computed before.
    if extensions_project:
        inputs["buildExtension"] = shelf_inputs(root, extensions_project, "build-extensions-project")
        # 🚨 The extension's publish takes compiler bytes from OUTSIDE the repository too: the Razor
        # extension stages its source generator from the installed .NET SDK. So the SDK the lane
        # publishes it with is a key input — a new SDK is a new compiler, and a ledger keyed on the
        # repository alone would reuse bundles (and recorded failures) built by the old one.
        if not extensions_sdk:
            raise ValueError("build-extensions-project is set but no --build-extensions-sdk was given — "
                             "the SDK the extension is published with decides its generator bytes, so an "
                             "unkeyed SDK lets the ledger reuse a bundle built by a different compiler")
        inputs["buildExtension"]["sdk"] = extensions_sdk
    canonical = json.dumps(inputs, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
    return {"key": sha256_bytes(canonical.encode("utf-8")), "inputs": inputs}


# ── self-test ────────────────────────────────────────────────────────────────────────────────

def _csproj(refs: list[str]) -> str:
    return "<Project><ItemGroup>%s</ItemGroup></Project>\n" % "".join(
        f'<ProjectReference Include="{r}" />' for r in refs)


def _fixture(root: Path) -> None:
    for pkg, module, requires in (("Alpha", "Acme.Alpha", ["Beta"]), ("Beta", "Acme.Beta", []),
                                  ("Gamma", None, [])):
        (root / pkg).mkdir(parents=True, exist_ok=True)
        content = {"requires": requires}
        if module:
            content["module"] = module
        (root / pkg / "index.json").write_text(json.dumps({"id": pkg, "content": content}),
                                               encoding="utf-8")
        (root / pkg / "manifest.lock").write_text(
            json.dumps({"moduleVersion": f"mv-{pkg}", "version": "1.0.0"}), encoding="utf-8")
    for name, refs in (
            ("Acme.Alpha", ["../Acme.Shared/Acme.Shared.csproj",
                            "$(MeshWeaverRoot)/src/MeshWeaver.Graph/MeshWeaver.Graph.csproj"]),
            ("Acme.Alpha.Test", ["../Acme.Alpha/Acme.Alpha.csproj", "../Acme.Kit/Acme.Kit.csproj"]),
            ("Acme.Beta", []), ("Acme.Shared", []), ("Acme.Kit", []), ("Acme.Other", [])):
        d = root / "src" / name
        d.mkdir(parents=True, exist_ok=True)
        (d / f"{name}.csproj").write_text(_csproj(refs), encoding="utf-8")
        (d / "Code.cs").write_text(f"// {name}\n", encoding="utf-8")
        (d / "bin").mkdir(exist_ok=True)
        (d / "bin" / "out.dll").write_bytes(b"\x00")
    (root / "src" / "Directory.Build.props").write_text("<Project />\n", encoding="utf-8")
    for name, refs in (("Acme.ModuleLibraries", ["../Acme.ShelfKit/Acme.ShelfKit.csproj"]),
                       ("Acme.ShelfKit", [])):
        d = root / "tools" / name
        d.mkdir(parents=True, exist_ok=True)
        (d / f"{name}.csproj").write_text(_csproj(refs), encoding="utf-8")
        (d / "Code.cs").write_text(f"// {name}\n", encoding="utf-8")
    (root / "docs").mkdir(exist_ok=True)
    (root / "docs" / "guide.md").write_text("x\n", encoding="utf-8")


_ENTRY = {"package": "Alpha", "module": "Acme.Alpha",
          "project": "src/Acme.Alpha/Acme.Alpha.csproj", "build": "container", "accept": "targets"}


def self_test() -> int:
    import tempfile
    failures: list[str] = []
    ran = 0

    def check(name: str, ok: bool, detail: str = "") -> None:
        nonlocal ran
        ran += 1
        print(f"  {'✓' if ok else '✗'} {name}{'' if ok else ': ' + detail}")
        if not ok:
            failures.append(name)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        _fixture(root)
        base = compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123")
        k0 = base["key"]
        check("the key is 64 hex characters", re.fullmatch(r"[0-9a-f]{64}", k0) is not None, k0)
        check("the key is deterministic",
              compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123")["key"] == k0)
        check("the closure is the entry, its .Test sibling and what they reach — not the platform",
              sorted(base["inputs"]["closure"]) == ["src/Acme.Alpha", "src/Acme.Alpha.Test",
                                                    "src/Acme.Kit", "src/Acme.Shared"],
              str(sorted(base["inputs"]["closure"])))
        check("the requires chain's moduleVersions are in the key",
              base["inputs"]["packages"] == {"Beta": "mv-Beta"}, str(base["inputs"]["packages"]))
        check("absent global inputs are recorded as null (absence is an input)",
              base["inputs"]["globals"]["global.json"] is None
              and base["inputs"]["globals"]["src/Directory.Build.props"] is not None)

        def flips(label: str, mutate, restore) -> None:
            mutate()
            try:
                k = compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123")["key"]
            finally:
                restore()
            check(f"{label} changes the key", k != k0)
            check(f"…and restoring it restores the key",
                  compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123")["key"] == k0)

        def edit(rel: str, text: str):
            p = root / rel
            old = p.read_text(encoding="utf-8")
            return (lambda: p.write_text(text, encoding="utf-8")), (lambda: p.write_text(old, encoding="utf-8"))

        print("every input flips the key:")
        flips("the entry project's source", *edit("src/Acme.Alpha/Code.cs", "// changed\n"))
        flips("a referenced (riding) project's source", *edit("src/Acme.Shared/Code.cs", "// changed\n"))
        flips("the .Test sibling's source", *edit("src/Acme.Alpha.Test/Code.cs", "// changed\n"))
        flips("a project reached only through the .Test project", *edit("src/Acme.Kit/Code.cs", "// c\n"))
        flips("the package's own moduleVersion",
              *edit("Alpha/manifest.lock", json.dumps({"moduleVersion": "mv-Alpha2", "version": "1.0.1"})))
        flips("a required package's moduleVersion",
              *edit("Beta/manifest.lock", json.dumps({"moduleVersion": "mv-Beta2", "version": "1.0.0"})))
        flips("src/Directory.Build.props", *edit("src/Directory.Build.props", "<Project><PropertyGroup/></Project>\n"))
        flips("a new file in the entry project",
              (lambda: (root / "src/Acme.Alpha/New.cs").write_text("//\n", encoding="utf-8")),
              (lambda: (root / "src/Acme.Alpha/New.cs").unlink()))
        flips("a new global input appearing (global.json)",
              (lambda: (root / "global.json").write_text("{}\n", encoding="utf-8")),
              (lambda: (root / "global.json").unlink()))
        for label, kwargs in (("the tester digest", dict(tester_digest="sha256:t2")),
                              ("the platform digest", dict(platform_digest="sha256:p2")),
                              ("the platform ref", dict(platform_ref="def456")),
                              # 🚨 DERIVED from the live default, never a literal: this case read
                              # `recipe="2"` and silently became a no-op assertion the moment the
                              # lane bumped RECIPE_VERSION to "2" (MeshWeaver#3211) — a guard whose
                              # subject moved and whose root did not passes having checked nothing.
                              ("the recipe version", dict(recipe=RECIPE_VERSION + "-other"))):
            args = dict(tester_digest="sha256:t", platform_digest="sha256:p", platform_ref="abc123")
            args.update(kwargs)
            check(f"{label} changes the key", compute(root, _ENTRY, **args)["key"] != k0)
        check("the build mode changes the key",
              compute(root, {**_ENTRY, "build": "sdk"}, "sha256:t", "sha256:p", "abc123")["key"] != k0)
        check("the accept set changes the key",
              compute(root, {**_ENTRY, "accept": "targets embedded-resource"}, "sha256:t", "sha256:p",
                      "abc123")["key"] != k0)
        check("accept token ORDER does not change the key (it is a set)",
              compute(root, {**_ENTRY, "accept": "b a"}, "sha256:t", "sha256:p", "abc123")["key"]
              == compute(root, {**_ENTRY, "accept": "a b"}, "sha256:t", "sha256:p", "abc123")["key"])

        print("noise does NOT flip the key:")
        for label, rel in (("an unrelated project", "src/Acme.Other/Code.cs"),
                           ("a docs file", "docs/guide.md"),
                           ("a package with no module (Gamma)", "Gamma/index.json")):
            mutate, restore = edit(rel, "// noise\n")
            mutate()
            try:
                k = compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123")["key"]
            finally:
                restore()
            check(f"{label} leaves the key unchanged", k == k0)
        (root / "src/Acme.Alpha/bin/out.dll").write_bytes(b"\x01\x02")
        check("build output (bin/) leaves the key unchanged",
              compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123")["key"] == k0)

        print("the caller's module-libraries shelf:")
        shelf = "tools/Acme.ModuleLibraries/Acme.ModuleLibraries.csproj"

        def with_shelf() -> dict:
            return compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123", shelf_project=shelf)

        s0 = with_shelf()
        check("no shelf named: the inputs carry no shelf (keys recorded before it stay valid)",
              "shelf" not in base["inputs"])
        check("a named shelf changes the key", s0["key"] != k0)
        check("the shelf closure is the shelf project and what it reaches",
              sorted(s0["inputs"]["shelf"]["closure"]) == ["tools/Acme.ModuleLibraries", "tools/Acme.ShelfKit"],
              str(sorted(s0["inputs"]["shelf"]["closure"])))
        for label, rel, text in (
                ("the shelf project (a PackageReference added)",
                 "tools/Acme.ModuleLibraries/Acme.ModuleLibraries.csproj",
                 '<Project><ItemGroup><PackageReference Include="Radzen.Blazor" /></ItemGroup></Project>\n'),
                ("a project the shelf reaches", "tools/Acme.ShelfKit/Code.cs", "// changed\n")):
            mutate, restore = edit(rel, text)
            mutate()
            try:
                k = with_shelf()["key"]
            finally:
                restore()
            check(f"{label} changes the shelf key", k != s0["key"])
            check(f"{label}: the build key WITHOUT a shelf ignores it",
                  compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123")["key"] == k0)
        pins = root / "tools" / "Directory.Packages.props"
        pins.write_text("<Project><ItemGroup><PackageVersion Include=\"Radzen.Blazor\" Version=\"9.0.0\" /></ItemGroup></Project>\n",
                        encoding="utf-8")
        try:
            k = with_shelf()["key"]
        finally:
            pins.unlink()
        check("a package pin the shelf inherits (tools/Directory.Packages.props) changes the shelf key",
              k != s0["key"])
        check("…and removing it restores the shelf key", with_shelf()["key"] == s0["key"])
        split = root / "tools" / "Directory.Packages.Plugins.props"
        split.write_text("<Project />\n", encoding="utf-8")
        try:
            k = with_shelf()["key"]
        finally:
            split.unlink()
        check("a split pin file the shelf's imports pull in (Directory.Packages.Plugins.props) changes the shelf key",
              k != s0["key"])
        try:
            compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123", shelf_project="tools/Nope/Nope.csproj")
            check("a named shelf that does not exist is refused", False, "no error raised")
        except ValueError as exc:
            check("a named shelf that does not exist is refused", "does not exist" in str(exc))

        print("the caller's build extension:")
        sdk = "10.0.200"

        def with_ext(shelf_too: bool = False) -> dict:
            return compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123",
                           shelf_project=shelf if shelf_too else "", extensions_project=shelf,
                           extensions_sdk=sdk)

        e0 = with_ext()
        check("no extension named: the inputs carry no buildExtension (keys recorded before stay valid)",
              "buildExtension" not in base["inputs"] and "buildExtension" not in s0["inputs"])
        check("a named extension changes the key", e0["key"] != k0)
        check("the extension is keyed under its own name, never as the shelf",
              "shelf" not in e0["inputs"] and e0["key"] != s0["key"])
        check("shelf AND extension together key differently from either alone",
              with_ext(shelf_too=True)["key"] not in (e0["key"], s0["key"]))
        mutate, restore = edit("tools/Acme.ShelfKit/Code.cs", "// extension changed\n")
        mutate()
        try:
            k = with_ext()["key"]
            k_plain = compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123")["key"]
        finally:
            restore()
        check("a project the extension reaches changes the extension key", k != e0["key"])
        check("…while the key WITHOUT an extension ignores it (negative control)", k_plain == k0)
        check("…and restoring it restores the extension key", with_ext()["key"] == e0["key"])
        check("the publishing SDK is keyed with the extension", e0["inputs"]["buildExtension"].get("sdk") == sdk)
        sdk = "10.0.201"
        check("a different publishing SDK changes the extension key", with_ext()["key"] != e0["key"])
        sdk = "10.0.200"
        check("…and the same SDK restores it (negative control)", with_ext()["key"] == e0["key"])
        # A linked source: the extension compiles `../Shared/Emitter.cs`, outside every closure dir.
        ext_proj = root / "tools" / "Acme.ModuleLibraries" / "Acme.ModuleLibraries.csproj"
        ext_old = ext_proj.read_text(encoding="utf-8")
        shared = root / "tools" / "Shared"
        shared.mkdir(exist_ok=True)
        (shared / "Emitter.cs").write_text("// emitter v1\n", encoding="utf-8")
        (shared / "Unlinked.cs").write_text("// not compiled\n", encoding="utf-8")
        ext_proj.write_text('<Project><ItemGroup><ProjectReference Include="../Acme.ShelfKit/Acme.ShelfKit.csproj" />'
                            '<Compile Include="../Shared/Emitter.cs" Link="Emitter.cs" /></ItemGroup></Project>\n',
                            encoding="utf-8")
        try:
            l0 = with_ext()
            check("a linked <Compile Include> outside the extension's folder is a key input",
                  list(l0["inputs"]["buildExtension"].get("linked", {})) == ["tools/Shared/Emitter.cs"],
                  str(l0["inputs"]["buildExtension"].get("linked")))
            (shared / "Emitter.cs").write_text("// emitter v2\n", encoding="utf-8")
            check("editing ONLY the linked source changes the extension key", with_ext()["key"] != l0["key"])
            (shared / "Emitter.cs").write_text("// emitter v1\n", encoding="utf-8")
            check("…and restoring it restores the extension key", with_ext()["key"] == l0["key"])
            (shared / "Unlinked.cs").write_text("// changed, still not compiled\n", encoding="utf-8")
            check("a file beside the linked one that no Compile item names leaves the key unchanged "
                  "(negative control)", with_ext()["key"] == l0["key"])
            ext_proj.write_text('<Project><ItemGroup><ProjectReference Include="../Acme.ShelfKit/Acme.ShelfKit.csproj" />'
                                '<Compile Include="$(MSBuildThisFileDirectory)../Shared/*.cs" /></ItemGroup></Project>\n',
                                encoding="utf-8")
            check("a wildcard linked include keys every file it matches",
                  sorted(with_ext()["inputs"]["buildExtension"].get("linked", {}))
                  == ["tools/Shared/Emitter.cs", "tools/Shared/Unlinked.cs"],
                  str(with_ext()["inputs"]["buildExtension"].get("linked")))
        finally:
            ext_proj.write_text(ext_old, encoding="utf-8")
            for f in shared.iterdir():
                f.unlink()
            shared.rmdir()
        check("a project that links nothing carries no `linked` input (keys recorded before stay valid)",
              "linked" not in with_ext()["inputs"]["buildExtension"] and with_ext()["key"] == e0["key"])
        try:
            compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123", extensions_project=shelf)
            check("an extension without its publishing SDK is refused", False, "no error raised")
        except ValueError as exc:
            check("an extension without its publishing SDK is refused, by name", "--build-extensions-sdk" in str(exc))
        try:
            compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123", extensions_project="tools/Nope/Nope.csproj",
                    extensions_sdk=sdk)
            check("a named extension that does not exist is refused", False, "no error raised")
        except ValueError as exc:
            check("a named extension that does not exist is refused, by its own input name",
                  "build-extensions-project" in str(exc) and "does not exist" in str(exc))

        print("refusals:")
        (root / "Alpha" / "manifest.lock").unlink()
        try:
            compute(root, _ENTRY, "sha256:t", "sha256:p", "abc123")
            check("a package without manifest.lock is refused", False, "no error raised")
        except ValueError as exc:
            check("a package without manifest.lock is refused", "manifest.lock" in str(exc))
        (root / "Alpha" / "manifest.lock").write_text(
            json.dumps({"moduleVersion": "mv-Alpha", "version": "1.0.0"}), encoding="utf-8")
        try:
            compute(root, {**_ENTRY, "project": "src/Nope/Nope.csproj"}, "sha256:t", "sha256:p", "abc123")
            check("a missing project is refused", False, "no error raised")
        except ValueError as exc:
            check("a missing project is refused", "does not exist" in str(exc))

    if failures:
        print(f"\n::error title=module-build-key self-test failed::{len(failures)} case(s) — this script "
              "decides when two builds are THE SAME build; a collision is a silent under-build.")
        return 1
    print(f"\n✓ module-build-key self-test: {ran} cases green — determinism, every input flips the key, "
          "noise does not, and the two refusals.")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--root", default=".")
    p.add_argument("--entry", help="one matrix entry as JSON")
    p.add_argument("--modules", help="a JSON array of matrix entries, or @file")
    p.add_argument("--tester-digest", default="")
    p.add_argument("--platform-digest", default="")
    p.add_argument("--platform-ref", default="")
    p.add_argument("--recipe-version", default=RECIPE_VERSION, help=argparse.SUPPRESS)
    p.add_argument("--module-libraries-project", default="",
                   help="the caller's module-libraries shelf csproj (relative to --root); keyed when set")
    p.add_argument("--build-extensions-project", default="",
                   help="the caller's build-extension csproj (relative to --root); keyed when set")
    p.add_argument("--build-extensions-sdk", default="",
                   help="the exact .NET SDK version the extension is published with; required with it")
    p.add_argument("--json", action="store_true", help="with --entry: print {key, inputs}")
    p.add_argument("--self-test", action="store_true", dest="self_test")
    a = p.parse_args()
    if a.self_test:
        return self_test()
    root = Path(a.root)
    if not root.is_dir():
        print(f"::error::--root {a.root} is not a directory", file=sys.stderr)
        return 2
    try:
        if a.entry:
            got = compute(root, json.loads(a.entry), a.tester_digest, a.platform_digest, a.platform_ref,
                          a.recipe_version, a.module_libraries_project, a.build_extensions_project,
                          a.build_extensions_sdk)
            print(json.dumps(got, indent=2) if a.json else got["key"])
            return 0
        if a.modules:
            raw = a.modules
            if raw.startswith("@"):
                raw = Path(raw[1:]).read_text(encoding="utf-8")
            entries = json.loads(raw)
            out = []
            for e in entries:
                got = compute(root, e, a.tester_digest, a.platform_digest, a.platform_ref, a.recipe_version,
                              a.module_libraries_project, a.build_extensions_project,
                              a.build_extensions_sdk)
                out.append({"package": e.get("package"), "module": e.get("module"),
                            "key": got["key"], "inputs": got["inputs"]})
                print(f"  {e.get('module')}: {got['key']} (moduleVersion {got['inputs']['moduleVersion']}, "
                      f"{len(got['inputs']['closure'])} project dir(s), "
                      f"{len(got['inputs']['packages'])} reached package(s))", file=sys.stderr)
            print(json.dumps(out))
            return 0
    except ValueError as exc:
        print(f"::error title=module build key::{exc}", file=sys.stderr)
        return 2
    p.error("one of --entry, --modules or --self-test is required")
    return 2


if __name__ == "__main__":
    sys.exit(main())
