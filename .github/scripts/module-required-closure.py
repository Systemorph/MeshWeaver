#!/usr/bin/env python3
"""Drop from a module bundle what a module its package REQUIRES already serves.

    module-required-closure.py --repo-root "$GITHUB_WORKSPACE" --package Google \\
        --module MeshWeaver.Mail.Gmail --packdir "$PACKDIR" --pack-args "$RUNNER_TEMP/mods/<M>/pack-args"
    exit 0 = pack-args rewritten (or left untouched when no required module is in reach), every
             decision printed
    exit 1 = an input is missing or unreadable — named; pack-args untouched

🚨 WHY (Systemorph/MeshWeaver.Plugins, main run 42641639266, 2026-11-09). The registry refused
`MeshWeaver.Mail.Gmail@1.14.49` (33,470,132 bytes) and `MeshWeaver.Mail.MicrosoftGraph@1.28.17`
(33,400,061 bytes) with 413 against its 30,000,000-byte request bound, so no Mail update reached any
instance. Measured over the two bundles' own files:

    Gmail 33.1 MB = 19.0 MB  MeshWeaver.AI + MeshWeaver.Markdown.Collaboration and their closure
                             (libSkiaSharp.so 4.8, OpenXml 1.9, FluentUI icons 3.7, PdfPig 1.9, …)
                  + 13.5 MB  MeshWeaver.Mail.MicrosoftGraph and its closure (Microsoft.Graph 9.3, …)
                  +  0.1 MB  MeshWeaver.Mail.Gmail itself

Every byte but the last belongs to a module the package already REQUIRES (Google → Mail → AI), and
the loader never reads them: `ModuleLoadContext` serves a name that is ANOTHER installed module's
entry assembly from that module's own context (step 2, before the module's own directory), so the
bundled copy of `MeshWeaver.AI.dll` inside the Mail bundle is dead weight wherever AI is installed —
and AI is installed wherever Mail is, because Mail requires it. What that entry needs in turn is
resolved by THAT context, from THAT module's directory. The pack lane predates per-module contexts
(2026-10-05) and carried every module-owned sibling with its whole closure, so each dependent bundle
re-shipped its dependencies, and the largest crossed the registry bound.

🚨 THE RULE, and why it is a walk over METADATA rather than over packages. The dependent's own load
context is asked for exactly the names ITS code references, transitively, until a reference lands on
another module's entry. So:

    rides = the AssemblyRef closure of the module's entry DLL over the files the builder named
            (--with), stopping at the ENTRY of every module whose package this package REQUIRES
            (transitively, through `content.requires` of each in-repo `<Package>/index.json`).

Everything the builder named that this walk does not reach is not carried. A file the module's OWN
code references keeps riding even when a required module also carries it — exactly as before, so
nothing the dependent binds by itself changes. Only the closure reached THROUGH a required module's
entry is dropped, and that closure is the required module's to carry (its own bundle keeps it, by
the same rule). A native payload goes with its managed owner (`libSkiaSharp.so` with
`SkiaSharp.dll`); one with no attributable owner keeps riding and says so.

🚨 WHAT IT DOES NOT TOUCH. A module whose package requires no in-repo module, or whose closure
reaches none, keeps its pack-args byte-for-byte — the helper is not even run. A required package
from ANOTHER repository has no `index.json` here, so its module is not known and nothing is dropped
for it (conservative: the dependent keeps carrying it, as before). The SDK path (`--deps-closure`)
is the packer's own walk and is not rewritten here.

Design of record: `Doc/Architecture/ModuleClosureAccounting` → "A module the package REQUIRES is
served, not carried".
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
from collections import deque
from pathlib import Path

VALUE_FLAGS = ("--with", "--with-native")
HELPER = Path(__file__).with_name("assembly-references.cs")


# ───────────────────────────── the package graph ─────────────────────────────

def package_id(requirement: str) -> str:
    """`Mail@^1.27.0` → `Mail`. Every reader splits on the `@` (PackageDependencyGraph.DependencyId)."""
    return requirement.split("@", 1)[0].strip()


def read_content(repo_root: Path, package: str) -> dict | None:
    index = repo_root / package / "index.json"
    if not index.is_file():
        return None
    try:
        node = json.loads(index.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise SystemExit(f"::error::cannot read {index}: {error}")
    content = node.get("content")
    return content if isinstance(content, dict) else None


def required_modules(repo_root: Path, package: str) -> dict[str, str]:
    """Module entry name → the requires chain that reaches it, over every package `package`
    requires, transitively. A package with no `index.json` in this repository (another repository's)
    is followed no further and contributes no module — conservative by construction."""
    found: dict[str, str] = {}
    seen = {package}
    queue: deque[tuple[str, str]] = deque()
    own = read_content(repo_root, package)
    for requirement in (own or {}).get("requires") or []:
        queue.append((package_id(requirement), f"{package} → {package_id(requirement)}"))
    while queue:
        current, chain = queue.popleft()
        if current in seen:
            continue
        seen.add(current)
        content = read_content(repo_root, current)
        if content is None:
            continue
        module = content.get("module")
        if isinstance(module, str) and module:
            found.setdefault(module, chain)
        for requirement in content.get("requires") or []:
            queue.append((package_id(requirement), f"{chain} → {package_id(requirement)}"))
    own_module = (own or {}).get("module")
    found.pop(own_module, None)
    return found


# ───────────────────────────── pack-args ─────────────────────────────

def parse_pack_args(text: str) -> list[tuple[str, str | None]]:
    lines = [line for line in text.splitlines() if line != ""]
    pairs: list[tuple[str, str | None]] = []
    i = 0
    while i < len(lines):
        flag = lines[i]
        if flag in VALUE_FLAGS:
            if i + 1 >= len(lines):
                raise SystemExit(f"::error::pack-args ends with '{flag}' and no value")
            pairs.append((flag, lines[i + 1]))
            i += 2
        else:
            pairs.append((flag, None))
            i += 1
    return pairs


def render_pack_args(pairs: list[tuple[str, str | None]]) -> str:
    out: list[str] = []
    for flag, value in pairs:
        out.append(flag)
        if value is not None:
            out.append(value)
    return "".join(f"{line}\n" for line in out)


def stem(file_name: str) -> str:
    name = os.path.basename(file_name)
    for suffix in (".dll", ".so", ".dylib"):
        if name.endswith(suffix):
            return name[: -len(suffix)]
    return name


def native_owner(native: str, managed: set[str]) -> str | None:
    """`runtimes/linux-x64/native/libSkiaSharp.so` → `SkiaSharp` when SkiaSharp.dll is named."""
    base = stem(native)
    for candidate in (base, base[3:] if base.startswith("lib") else None):
        if candidate and candidate in managed:
            return candidate
    return None


# ───────────────────────────── the walk ─────────────────────────────

def parse_refs(tsv: str) -> dict[str, set[str]]:
    refs: dict[str, set[str]] = {}
    for line in tsv.splitlines():
        if not line.strip():
            continue
        file, _, name = line.partition("\t")
        refs.setdefault(stem(file.strip()), set())
        if name.strip():
            refs[stem(file.strip())].add(name.strip())
    return refs


def walk(entry: str, refs: dict[str, set[str]], carried: set[str], stop: set[str]) -> tuple[set[str], set[str]]:
    """The names reached from `entry` over `carried`, never crossing a `stop` name. Also returns
    the stop names actually reached (what the module binds a required module through)."""
    reached = {entry}
    through: set[str] = set()
    queue = deque([entry])
    while queue:
        for name in sorted(refs.get(queue.popleft(), ())):
            if name in stop:
                through.add(name)
            elif name in carried and name not in reached:
                reached.add(name)
                queue.append(name)
    return reached, through


def is_satellite(value: str) -> bool:
    """A culture-satellite resource assembly. ResourceManager PROBES for it — no AssemblyRef ever
    names it, and a missing one fails no load — so the walk cannot vouch for it either way."""
    return value.endswith(".resources.dll")


def walked(value: str) -> bool:
    """A `--with` entry the AssemblyRef walk decides: a managed assembly that is not a satellite."""
    return value.endswith(".dll") and not is_satellite(value)


def prune(entry: str, pairs: list[tuple[str, str | None]], refs: dict[str, set[str]], stop: set[str]):
    """Returns (kept pairs, dropped pairs with reasons, the stop names the walk crossed into)."""
    for flag, value in pairs:
        # 🚨 Everything here is keyed by the file's STEM, which is sound only because `--with`
        # names FLAT files (module-pack refuses a path component there). Assert it rather than
        # let two same-named files in different directories be kept or dropped together.
        if flag == "--with" and value is not None and ("/" in value or "\\" in value):
            raise SystemExit(f"::error::--with '{value}' carries a path; the closure walk keys files by name and "
                             "module-pack accepts only flat --with entries")
    managed = {stem(v) for f, v in pairs if f == "--with" and v and walked(v)}
    reached, through = walk(entry, refs, managed, stop)
    kept: list[tuple[str, str | None]] = []
    dropped: list[tuple[str, str, str]] = []
    for flag, value in pairs:
        if flag == "--with" and value is not None and walked(value):
            name = stem(value)
            if name in reached:
                kept.append((flag, value))
            elif name in stop:
                dropped.append((flag, value, "the ENTRY of a required module — served from that module's own context"))
            else:
                dropped.append((flag, value, "not referenced by this module's own code — reached only through a required module's entry, whose bundle carries it"))
        elif flag == "--with-native" and value is not None:
            owner = native_owner(value, managed)
            if owner is not None and owner not in reached:
                dropped.append((flag, value, f"native payload of {owner}.dll, which does not ride"))
            else:
                if owner is None:
                    print(f"required-closure: {value} keeps riding — no managed owner among the named assemblies, so nothing proves it unused")
                kept.append((flag, value))
        else:
            kept.append((flag, value))
    return kept, dropped, through


def run_helper(paths: list[Path]) -> str:
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        raise SystemExit("::error::no `dotnet` on PATH — the AssemblyRef walk cannot run, and guessing which files ride is not an option")
    with tempfile.TemporaryDirectory(prefix="assembly-references-") as work:
        # An EMPTY directory: a file-based app inherits every Directory.Build.props above it.
        script = Path(work) / "assembly-references.cs"
        shutil.copyfile(HELPER, script)
        result = subprocess.run(
            # ABSOLUTE paths: the helper runs in the empty directory, not where the caller stands.
            [dotnet, "run", "--file", str(script), "--", *(str(p.resolve()) for p in paths)],
            cwd=work, capture_output=True, text=True,
            env={**os.environ, "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
    if result.returncode != 0:
        raise SystemExit(f"::error::the AssemblyRef helper failed (exit {result.returncode}):\n{result.stdout}\n{result.stderr}")
    return result.stdout


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repo-root", required=True, type=Path)
    parser.add_argument("--package", required=True)
    parser.add_argument("--module", required=True)
    parser.add_argument("--packdir", required=True, type=Path)
    parser.add_argument("--pack-args", required=True, type=Path)
    parser.add_argument("--refs-tsv", type=Path, help="pre-computed AssemblyRef table (tests); default: run the helper")
    args = parser.parse_args(argv)

    stop_chains = required_modules(args.repo_root, args.package)
    pairs = parse_pack_args(args.pack_args.read_text(encoding="utf-8"))
    named = [v for f, v in pairs if f == "--with" and v and walked(v)]
    if not stop_chains:
        print(f"required-closure: {args.package} requires no module of this repository — {args.module}'s closure unchanged")
        return 0
    stop = set(stop_chains)
    if not stop.intersection(stem(v) for v in named):
        # The entry may still reference a required module directly (it then binds it from that
        # module's context) — but nothing in the closure belongs to one, so nothing can be dropped.
        print(f"required-closure: none of {', '.join(sorted(stop))} rides {args.module}'s closure — unchanged")
        return 0

    entry_dll = args.packdir / f"{args.module}.dll"
    files = [entry_dll, *(args.packdir / v for v in named)]
    missing = [str(p) for p in files if not p.is_file()]
    if missing:
        print(f"::error::cannot walk {args.module}'s references: {', '.join(missing)} not under {args.packdir}")
        return 1
    tsv = args.refs_tsv.read_text(encoding="utf-8") if args.refs_tsv else run_helper(files)
    kept, dropped, through = prune(args.module, pairs, parse_refs(tsv), stop)

    for module in sorted(stop):
        print(f"required-closure: {module} is served by a required module ({stop_chains[module]})"
              + ("" if module in through else " — not referenced by this module's code"))
    saved = 0
    for flag, value, why in dropped:
        path = args.packdir / value
        size = path.stat().st_size if path.is_file() else 0
        saved += size
        print(f"closure: {value} does NOT ride — {why} ({size:,} bytes)")
    print(f"required-closure: {args.module} — {len(dropped)} of {len(pairs)} closure entr(y|ies) dropped, "
          f"{saved:,} uncompressed bytes no longer carried")
    args.pack_args.write_text(render_pack_args(kept), encoding="utf-8")
    return 0


# ───────────────────────────── self-test ─────────────────────────────

def self_test() -> int:
    failures = 0

    def check(label: str, condition: bool, detail: object = "") -> None:
        nonlocal failures
        print(f"  {'ok  ' if condition else 'FAIL'} {label}" + ("" if condition else f" — {detail}"))
        if not condition:
            failures += 1

    def package(root: Path, name: str, module: str | None, requires: list[str]) -> None:
        (root / name).mkdir(parents=True, exist_ok=True)
        content: dict = {"requires": requires}
        if module:
            content["module"] = module
        (root / name / "index.json").write_text(json.dumps({"id": name, "content": content}))

    print("== the package graph: transitive, '@' split, cycle-safe, foreign packages contribute nothing")
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        package(root, "Google", "MeshWeaver.Mail.Gmail", ["Store@^1.0.0", "Mail@^1.27.0"])
        package(root, "Mail", "MeshWeaver.Mail.MicrosoftGraph", ["Store@^1.0.0", "AI@^1.46.0"])
        package(root, "AI", "MeshWeaver.AI", ["Store@^1.43.0", "Google"])  # a cycle back
        package(root, "Store", None, [])
        found = required_modules(root, "Google")
        check("Google reaches Mail and, through it, AI", set(found) == {"MeshWeaver.Mail.MicrosoftGraph", "MeshWeaver.AI"}, found)
        check("the chain is stated", found.get("MeshWeaver.AI") == "Google → Mail → AI", found)
        check("its own module is never a stop, even through a cycle", "MeshWeaver.Mail.Gmail" not in found, found)
        check("a package requiring nothing in-repo has no stops", required_modules(root, "Store") == {}, required_modules(root, "Store"))
        check("a package with no index.json here (another repo's) has no stops", required_modules(root, "Elsewhere") == {})

    print("== pack-args round-trip")
    text = "--with\nA.dll\n--with-native\nruntimes/linux-x64/native/libB.so\n--deps-closure\n"
    check("parse → render is the identity", render_pack_args(parse_pack_args(text)) == text, render_pack_args(parse_pack_args(text)))

    print("== the walk (Gmail's measured shape)")
    refs = parse_refs("\n".join([
        "MeshWeaver.Mail.Gmail.dll\tSystem.Runtime",
        "MeshWeaver.Mail.Gmail.dll\tMeshWeaver.Mail.MicrosoftGraph",
        "MeshWeaver.Mail.Gmail.dll\tMeshWeaver.AI",
        "MeshWeaver.Mail.Gmail.dll\tSystem.Reactive",
        "System.Reactive.dll\tSystem.Runtime",
        "MeshWeaver.Mail.MicrosoftGraph.dll\tMicrosoft.Graph",
        "MeshWeaver.Mail.MicrosoftGraph.dll\tMeshWeaver.AI",
        "Microsoft.Graph.dll\tMicrosoft.Kiota.Abstractions",
        "Microsoft.Kiota.Abstractions.dll\t",
        "MeshWeaver.AI.dll\tSkiaSharp",
        "MeshWeaver.AI.dll\tMeshWeaver.Markdown.Collaboration",
        "SkiaSharp.dll\t",
        "MeshWeaver.Markdown.Collaboration.dll\t",
    ]))
    pairs = [("--with", n) for n in ("MeshWeaver.Mail.MicrosoftGraph.dll", "MeshWeaver.AI.dll", "MeshWeaver.Markdown.Collaboration.dll",
                                     "Microsoft.Graph.dll", "Microsoft.Kiota.Abstractions.dll", "SkiaSharp.dll", "System.Reactive.dll")]
    pairs.append(("--with-native", "runtimes/linux-x64/native/libSkiaSharp.so"))
    pairs.append(("--with-native", "runtimes/linux-x64/native/libunowned.so"))
    kept, dropped, through = prune("MeshWeaver.Mail.Gmail", pairs, refs, {"MeshWeaver.Mail.MicrosoftGraph", "MeshWeaver.AI"})
    kept_values = [v for _, v in kept]
    check("what the module's own code references keeps riding (System.Reactive)", "System.Reactive.dll" in kept_values, kept_values)
    check("required modules' entries are dropped",
          {"MeshWeaver.Mail.MicrosoftGraph.dll", "MeshWeaver.AI.dll"} <= {v for _, v, _ in dropped}, dropped)
    check("their closures are dropped (Graph, Kiota, Markdown.Collaboration, SkiaSharp)",
          {"Microsoft.Graph.dll", "Microsoft.Kiota.Abstractions.dll", "MeshWeaver.Markdown.Collaboration.dll", "SkiaSharp.dll"}
          <= {v for _, v, _ in dropped}, dropped)
    check("a native goes with its managed owner", "runtimes/linux-x64/native/libSkiaSharp.so" in {v for _, v, _ in dropped}, dropped)
    check("a native with no owner keeps riding", "runtimes/linux-x64/native/libunowned.so" in kept_values, kept_values)
    check("the crossings are reported", through == {"MeshWeaver.Mail.MicrosoftGraph", "MeshWeaver.AI"}, through)

    print("== the walk (Mail's shape): a file the module binds ITSELF rides even if the dependency carries it too")
    refs2 = parse_refs("Mail.dll\tMeshWeaver.AI\nMail.dll\tShared\nMeshWeaver.AI.dll\tShared\nMeshWeaver.AI.dll\tOnlyAi\nShared.dll\t\nOnlyAi.dll\t\n")
    kept2, dropped2, _ = prune("Mail", [("--with", "MeshWeaver.AI.dll"), ("--with", "Shared.dll"), ("--with", "OnlyAi.dll")], refs2, {"MeshWeaver.AI"})
    check("Shared.dll rides (the module references it directly)", ("--with", "Shared.dll") in kept2, kept2)
    check("OnlyAi.dll does not", "OnlyAi.dll" in {v for _, v, _ in dropped2}, dropped2)
    check("a transitive reference of a kept file rides too",
          ("--with", "Deep.dll") in prune("M", [("--with", "A.dll"), ("--with", "Deep.dll")],
                                         parse_refs("M.dll\tA\nA.dll\tDeep\nDeep.dll\t\n"), {"X"})[0])

    print("== satellites and paths: never decided by the walk")
    kept3, dropped3, _ = prune("M", [("--with", "M.resources.dll"), ("--with", "Dep.dll")],
                               parse_refs("M.dll\tDep\nDep.dll\t\n"), {"Dep"})
    check("a culture-satellite resource assembly keeps riding (probed, never referenced)",
          ("--with", "M.resources.dll") in kept3, (kept3, dropped3))
    try:
        prune("M", [("--with", "de/M.resources.dll")], {}, {"X"})
        check("a --with carrying a path is refused", False, "no refusal")
    except SystemExit as refusal:
        check("a --with carrying a path is refused", "carries a path" in str(refusal), refusal)

    print("== the CLI: untouched when nothing required is in reach; rewritten when it is; refuses missing files")
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp) / "repo"
        package(root, "Google", "MeshWeaver.Mail.Gmail", ["Mail@^1.27.0"])
        package(root, "Mail", "MeshWeaver.Mail.MicrosoftGraph", ["AI"])
        package(root, "AI", "MeshWeaver.AI", [])
        package(root, "Solo", "Solo", ["Store"])
        packdir = Path(tmp) / "pack"
        packdir.mkdir()
        for name in ("MeshWeaver.Mail.Gmail.dll", "MeshWeaver.Mail.MicrosoftGraph.dll", "MeshWeaver.AI.dll", "System.Reactive.dll", "Solo.dll", "Big.dll"):
            (packdir / name).write_bytes(b"x" * 100)
        refs_file = Path(tmp) / "refs.tsv"
        refs_file.write_text("MeshWeaver.Mail.Gmail.dll\tMeshWeaver.Mail.MicrosoftGraph\nMeshWeaver.Mail.Gmail.dll\tSystem.Reactive\n"
                             "MeshWeaver.Mail.MicrosoftGraph.dll\tMeshWeaver.AI\nSystem.Reactive.dll\t\n")
        args_file = Path(tmp) / "pack-args"
        original = "--with\nMeshWeaver.Mail.MicrosoftGraph.dll\n--with\nMeshWeaver.AI.dll\n--with\nSystem.Reactive.dll\n"
        args_file.write_text(original)
        rc = main(["--repo-root", str(root), "--package", "Google", "--module", "MeshWeaver.Mail.Gmail",
                   "--packdir", str(packdir), "--pack-args", str(args_file), "--refs-tsv", str(refs_file)])
        check("exit 0", rc == 0, rc)
        check("pack-args keeps only what Gmail binds itself", args_file.read_text() == "--with\nSystem.Reactive.dll\n", args_file.read_text())

        solo_args = "--with\nBig.dll\n"
        args_file.write_text(solo_args)
        rc = main(["--repo-root", str(root), "--package", "Solo", "--module", "Solo",
                   "--packdir", str(packdir), "--pack-args", str(args_file), "--refs-tsv", str(Path(tmp) / "absent.tsv")])
        check("no required in-repo module: exit 0, byte-identical, no refs read", rc == 0 and args_file.read_text() == solo_args, (rc, args_file.read_text()))

        args_file.write_text(original)
        (packdir / "MeshWeaver.AI.dll").unlink()
        rc = main(["--repo-root", str(root), "--package", "Google", "--module", "MeshWeaver.Mail.Gmail",
                   "--packdir", str(packdir), "--pack-args", str(args_file), "--refs-tsv", str(refs_file)])
        check("a named file absent from the packdir: exit 1, pack-args untouched", rc == 1 and args_file.read_text() == original, (rc, args_file.read_text()))

    print(f"{'PASS' if failures == 0 else 'FAIL'}: module-required-closure self-test ({failures} failure(s))")
    return 1 if failures else 0


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        sys.exit(self_test())
    sys.exit(main(sys.argv[1:]))
