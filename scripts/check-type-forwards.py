#!/usr/bin/env python3
"""Refuse moving a PUBLIC type between platform assemblies without leaving a type forwarder.

WHY THIS EXISTS
---------------
A module is a plain assembly that binds platform types BY SIMPLE ASSEMBLY NAME, and the module
lane's only gate is a SEMVER FLOOR — never MVID equality — precisely so that "a landed module
keeps loading across ordinary platform updates" (Doc/Architecture/Modules -> the skip rules).
That sentence is a promise about BINARY compatibility.

A module's IL does not hold "MeshOperations". It holds

    TypeRef  MeshWeaver.AI.MeshOperations     scope: AssemblyRef MeshWeaver.AI

so the FULL TYPE NAME **and** the assembly that carries it are both part of the contract. Move
the type to another assembly, or rename its namespace, and every module compiled earlier dies
the moment the platform rolls:

    System.TypeLoadException: Could not load type 'MeshWeaver.AI.MeshOperations'
        from assembly 'MeshWeaver.AI, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null'

That is #2370, and it took down the WHOLE /mcp surface of a production deployment: the MCP SDK
constructs its tool target per invocation, so every get / search / create / update / render_area
call from every external client (Claude Code, Copilot) failed identically until the platform
carried the type again.

WHY REVIEW AND THE OTHER GATES CANNOT CATCH IT
----------------------------------------------
The change that caused #2370 was verified by BUILDING the plugins repo's MeshWeaver.Mcp against
the branch, and it compiled cleanly — the module's source carries `using` directives for both
namespaces, so SOURCE compatibility survived a break that BINARY compatibility did not. The same
blind spot is structural in CI:

  * `landed-modules-gate` used to compile the plugins repo's module SOURCE against the PR. It
    answered "will the module still COMPILE", never "will the module ALREADY PUBLISHED still
    BIND" — and it is GONE besides (see the note above `clients-gate` in dotnet-test.yml: core
    builds the image and runs its own tests; plugins are built by the repo that owns them). So
    nothing in this workflow compiles a single line of module source any more.
  * `check-record-signatures.py` (#2298) covers the other half of the same class — a public
    record's primary constructor — and stops at constructors.
  * The semver floor cannot see a type at all.

WHAT IT CHECKS
--------------
Comparing the merge base against the working tree, for every `public` top-level type declared
under `src/<Assembly>/`:

    the type is no longer declared in <Assembly>, but a type of the same SIMPLE NAME is now
    declared in a DIFFERENT src/ assembly

...i.e. it MOVED. That is binary-breaking unless the old assembly leaves a forwarder, so the gate
requires `src/<OldAssembly>/**.cs` to contain

    [assembly: TypeForwardedTo(typeof(<OldNamespace>.<Name>))]

A forwarder CANNOT rename, so a move that also changes the namespace fails until the original
full name is restored in the new assembly (which is exactly the #2370 fix: the moved types keep
`namespace MeshWeaver.AI` inside MeshWeaver.Mesh.Operations, and MeshWeaver.AI forwards).

…AND THE DEPARTURES, WHICH USED TO BE A SILENT `continue` (#2398)
-----------------------------------------------------------------
A type can also leave an assembly WITHOUT landing anywhere in this repo. The original scoping
decision waved that through — "a genuine deletion reads AS a deletion in review, whereas a move
reads as a refactor" — and #2276 destroyed the premise. Since the module assemblies moved to
MeshWeaver.Plugins, a public type that moves from a core assembly INTO one of them deletes files
from core and adds none, so in core it reads EXACTLY like a deletion, to this gate and to a
reviewer alike. Measured on 2026-08-29:

    check-type-forwards.py --base v3.0.0-rc7 --head origin/main   ->  "OK"

...while that window contains the seven #2398 types (`MeshWeaver.GitSync.AiContentSyncArea` and
friends) that left GitSync/Hosting for `MeshWeaver.AI`, which is now built in MeshWeaver.Plugins.
The gate reported green on the exact class it exists for, and could not have reported anything
else: `MeshWeaver.AI` is not in this repo's `src/`, so there was no landing site to find.

So a DEPARTURE — the type is gone from `src/` while the assembly it left is STILL BUILT HERE — is
now its own reported, counted category, and it FAILS. A module holding
`OldAssembly!Namespace.Type` throws the identical `TypeLoadException` whether the type moved to a
sibling repo or was deleted outright, so failing on both is the conservative reading of the same
contract; deletions cost nothing in practice (MEASURED: zero departures across `main~5`, `~10`,
`~25`, `~50` and `~100` -> `main`, versus eight across `~400`).

If the OLD assembly itself left this repo, that is a different thing and is still skipped — there
is nowhere here to put a forwarder, the assembly keeps its name and its consumers wherever it is
built now, and 537 of its types would otherwise be reported at once.

RESOLVING A DEPARTURE: `--sibling <checkout>`
--------------------------------------------
Nothing inside this repo can tell a cross-repo move from a deletion — both are "the file is
gone". Point the gate at a sibling checkout (repeatable) and it can:

    check-type-forwards.py --base v3.0.0-rc7 --head origin/main --sibling ~/code/MeshWeaver.Plugins

A departed type found in a sibling's `src/` is named as a PROVEN CROSS-REPO MOVE, with the repo
and assembly it landed in. One found in NO sibling given is a PROVEN DELETION, reported but not
failed — which restores the original scoping decision on the one footing that can carry it,
evidence. `--sibling` therefore only ever RELAXES the verdict, so it cannot manufacture a red,
and omitting it is the conservative mode.

CI does NOT pass `--sibling`, deliberately. This repo is PUBLIC and MeshWeaver.Plugins is PRIVATE,
so the checkout would need a secret on a `pull_request` gate — in a workflow that today has no
secret and no preflight job at all (both deliberate; see the "No preflight job right now" note in
dotnet-test.yml) — and it would make a required gate's verdict depend on ANOTHER repo's moving
HEAD. A binary-compatibility gate whose answer changes without its own diff changing is one people
stop believing.

THE ESCAPE HATCH
----------------
`scripts/type-forwards.allow` takes one `Assembly:Namespace.Type` per line with a reason. An
entry is a statement that no shipped module can be holding that TypeRef — not a way to make the
gate quiet. A STALE entry (listed, but the type did not move in this diff) FAILS, exactly like
the repo's other ratchets, so it cannot outlive its change and hide the next break.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

ALLOW_FILE = "scripts/type-forwards.allow"

# `public sealed record Foo(` / `public partial class Bar<T> :` / `public enum Baz` / `public
# delegate void Qux(`. The leading indent is CAPTURED rather than forbidden: 38 files under `src/`
# still use a block-scoped namespace, which indents every top-level type by four spaces
# (`MeshWeaver.Data/TypeDescription.cs`), and anchoring at column 0 skipped all of them — a gate
# that quietly covers less is the exact failure mode this change exists to stop. Nesting is then
# excluded by comparing that indent against the enclosing namespace's form (see TOP_LEVEL_INDENT).
TYPE_RE = re.compile(
    r"^(?P<indent>[ \t]*)public\s+"
    r"(?:(?:sealed|abstract|partial|readonly|ref|unsafe|static)\s+)*"
    r"(?:class|record|struct|interface|enum|delegate)\s+"
    r"(?:(?:class|struct)\s+)?"          # `record class` / `record struct`
    r"(?P<name>[A-Za-z_]\w*)",
    re.MULTILINE,
)

# `namespace Foo.Bar;` (file-scoped), `namespace Foo.Bar {`, or `namespace Foo.Bar` with the brace
# on the next line. The terminator is captured because it decides where a TOP-LEVEL type sits.
NAMESPACE_RE = re.compile(
    r"^namespace\s+(?P<ns>[A-Za-z_][\w.]*)[ \t]*(?P<form>[;{]|$)", re.MULTILINE
)

# Column at which a top-level type is declared, by namespace form. A DEEPER indent is a nested
# type, which no module binds by its own simple name and which is therefore out of scope.
TOP_LEVEL_INDENT = {";": 0, "{": 4, "": 4}

# 🚨 The attribute may be written BARE (with `using System.Runtime.CompilerServices;`) or FULLY
# QUALIFIED — both compile to the same ExportedType row, so both must be recognised. The optional
# `(?:[\w.]*\.)?` is what makes that true. Without it the gate refused a correct forwarder and
# demanded one that was already there, which is exactly the shape this file's own header warns
# about: "a gate that only understood one of the two spellings would demand a forwarder that is
# already there" — and a gate that rejects correct work is how people learn to allow-list past it.
# `\s*` already spans newlines, so a split declaration was never the problem.
FORWARD_RE = re.compile(
    r"\[assembly:\s*(?:[\w.]*\.)?TypeForwardedTo\s*\(\s*typeof\(\s*(?P<type>[A-Za-z_][\w.]*)\s*\)\s*\)\s*\]"
)

# Paths under src/ that are NOT compiled into their assembly. `MeshWeaver.Documentation/Data` is
# in-mesh node source shipped as content (AGENTS.md: compiled at RUNTIME, never by any build), so
# a type "moving" between two doc samples is not a binary event at all.
EXCLUDED_PREFIXES = ("src/MeshWeaver.Documentation/Data/",)


@dataclass(frozen=True)
class Decl:
    assembly: str
    namespace: str
    name: str

    @property
    def full_name(self) -> str:
        return f"{self.namespace}.{self.name}" if self.namespace else self.name

    @property
    def key(self) -> str:
        return f"{self.assembly}:{self.full_name}"


def _is_scanned(path: str) -> bool:
    if not path.startswith("src/") or not path.endswith(".cs"):
        return False
    if "/bin/" in path or "/obj/" in path:
        return False
    return not path.startswith(EXCLUDED_PREFIXES)


def _assembly_of(path: str) -> str:
    # src/<Assembly>/... — the project directory IS the assembly name throughout this repo.
    return path.split("/")[1]


def _indent_width(raw: str) -> int:
    return sum(4 if ch == "\t" else 1 for ch in raw)


def parse_declarations(path: str, text: str) -> list[Decl]:
    """Public TOP-LEVEL types declared by one file, with the namespace in force."""
    assembly = _assembly_of(path)
    namespaces = [
        (m.start(), m.group("ns"), TOP_LEVEL_INDENT[m.group("form") or ""])
        for m in NAMESPACE_RE.finditer(text)
    ]
    out: list[Decl] = []
    for m in TYPE_RE.finditer(text):
        ns, expected = "", 0
        for start, candidate, indent in namespaces:
            if start < m.start():
                ns, expected = candidate, indent
            else:
                break
        if _indent_width(m.group("indent")) != expected:
            continue  # nested inside another type — not independently bindable by its simple name
        out.append(Decl(assembly, ns, m.group("name")))
    return out


def parse_forwards(text: str) -> set[str]:
    # 🚨 A COMMENTED-OUT forwarder must not count. FORWARD_RE is a plain regex over the file text,
    # so before this line `// [assembly: TypeForwardedTo(typeof(X))]` satisfied the gate — the exact
    # false-PASS shape this gate exists to prevent, and the likeliest way a forwarder gets disabled
    # during a refactor (comment first, delete later). Found by running the gate's own negative
    # control while fixing #2398: commenting the six new lines out left it GREEN, deleting them
    # turned it red, so "the forwarder is disabled" and "the forwarder is there" read identically.
    #
    # Line comments only. A forwarder buried in a /* ... */ block would still count; closing that
    # needs a real C# tokenizer rather than a regex, which is out of proportion for a CI script and
    # a far less likely way to disable one line. Truncating at `//` can also cut a string literal
    # containing a URL; harmless here, because the two regexes match declarations and assembly
    # attributes, neither of which lives inside a string.
    # 🚨 Strip line comments FIRST, then match over the whole text — not per line. Matching per
    # line silently defeated FORWARD_RE's `\s*`, so a forwarder wrapped across two lines did not
    # count, which is how a long type name is naturally written:
    #     [assembly: TypeForwardedTo(
    #         typeof(MeshWeaver.ContentCollections.Indexing.DocumentPaths))]
    # That is the same false-NEGATIVE class as the qualifier above: the gate demands a forwarder
    # that is already there, and the way out people find is the allow file. Comment stripping keeps
    # its own line granularity, so the #2398 negative control still holds — a commented-out
    # forwarder contributes nothing.
    stripped = "\n".join(line.split("//", 1)[0] for line in text.splitlines())
    return {m.group("type") for m in FORWARD_RE.finditer(stripped)}


# ─────────────────────────────── tree readers ───────────────────────────────


def _run(args: list[str]) -> str:
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout


def read_base_tree(base: str) -> tuple[dict[str, Decl], set[str], set[str]]:
    # ONE `git cat-file --batch` for the whole base tree rather than a `git show` per file: the
    # scanned set is ~1500 files, and a subprocess each turns a 2-second gate into a 25-second one.
    wanted: list[tuple[str, str]] = []
    for line in _run(["git", "ls-tree", "-r", base]).splitlines():
        meta, _, path = line.partition("\t")
        if not _is_scanned(path):
            continue
        wanted.append((meta.split()[2], path))

    decls: dict[str, Decl] = {}
    forwards: set[str] = set()
    assemblies = {_assembly_of(path) for _, path in wanted}
    if not wanted:
        return decls, forwards, assemblies

    proc = subprocess.run(
        ["git", "cat-file", "--batch"],
        input=("\n".join(oid for oid, _ in wanted) + "\n").encode(),
        check=True,
        capture_output=True,
    )
    blob = proc.stdout
    cursor = 0
    for _, path in wanted:
        header_end = blob.index(b"\n", cursor)
        size = int(blob[cursor:header_end].split()[2])
        body = blob[header_end + 1 : header_end + 1 + size]
        cursor = header_end + 1 + size + 1  # trailing newline after each object
        text = body.decode("utf-8", errors="replace")
        for d in parse_declarations(path, text):
            decls[d.key] = d
        forwards |= {f"{_assembly_of(path)}:{t}" for t in parse_forwards(text)}
    return decls, forwards, assemblies


def read_work_tree(root: Path) -> tuple[dict[str, Decl], set[str], set[str]]:
    decls: dict[str, Decl] = {}
    forwards: set[str] = set()
    assemblies: set[str] = set()
    for file in sorted((root / "src").rglob("*.cs")):
        path = file.relative_to(root).as_posix()
        if not _is_scanned(path):
            continue
        text = file.read_text(encoding="utf-8", errors="replace")
        assemblies.add(_assembly_of(path))
        for d in parse_declarations(path, text):
            decls[d.key] = d
        forwards |= {f"{_assembly_of(path)}:{t}" for t in parse_forwards(text)}
    return decls, forwards, assemblies


def read_sibling_tree(path: Path) -> dict[str, list[Decl]]:
    """Public top-level types declared under `<sibling>/src/`, indexed by SIMPLE NAME.

    Matching is by simple name — never full name — for the same reason `find_moves` uses it in
    this repo: a move that also renames the namespace is precisely the case that CANNOT be
    forwarded, so keying on the full name would hide the worst one. The destination's full name is
    printed instead, which makes the rename visible."""
    by_simple_name: dict[str, list[Decl]] = {}
    src = path / "src"
    if not src.is_dir():
        return by_simple_name
    for file in sorted(src.rglob("*.cs")):
        rel = file.relative_to(path).as_posix()
        if not _is_scanned(rel):
            continue
        text = file.read_text(encoding="utf-8", errors="replace")
        for d in parse_declarations(rel, text):
            by_simple_name.setdefault(d.name, []).append(d)
    return by_simple_name


def read_allow(root: Path) -> dict[str, str]:
    file = root / ALLOW_FILE
    if not file.exists():
        return {}
    entries: dict[str, str] = {}
    for line in file.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        key, _, reason = line.partition(" ")
        entries[key.strip()] = reason.strip()
    return entries


# ─────────────────────────────── the check ───────────────────────────────


def _departure(
    key: str, old: Decl, siblings: dict[str, dict[str, list[Decl]]] | None
) -> tuple[str, str, bool]:
    """One departed type, resolved against the sibling checkouts if any were given."""
    elsewhere = [
        f"{label}/src/{d.assembly} ({d.full_name})"
        for label, index in sorted((siblings or {}).items())
        for d in index.get(old.name, [])
    ]
    if elsewhere:
        return (
            key,
            f"  {old.full_name}\n"
            f"      left     {old.assembly}  (still built here — so a module binds "
            f"{old.assembly}!{old.full_name})\n"
            f"      now in   {', '.join(sorted(elsewhere))}\n"
            f"      CROSS-REPO MOVE. A forwarder is usually impossible: it would have to live in\n"
            f"      src/{old.assembly} and reference an assembly this repo does not build.",
            False,
        )
    if siblings:
        return (
            key,
            f"  {old.full_name}\n"
            f"      left     {old.assembly}  (still built here — so a module binds "
            f"{old.assembly}!{old.full_name})\n"
            f"      in none of {', '.join(sorted(siblings))} — a PROVEN DELETION.",
            True,
        )
    return (
        key,
        f"  {old.full_name}\n"
        f"      left     {old.assembly}  (still built here — so a module binds "
        f"{old.assembly}!{old.full_name})\n"
        f"      now in   nothing under this repo's src/",
        False,
    )


def find_moves(
    before: dict[str, Decl],
    after: dict[str, Decl],
    forwards: set[str],
    head_assemblies: set[str] | None = None,
    siblings: dict[str, dict[str, list[Decl]]] | None = None,
) -> tuple[list[tuple[str, str]], set[str], list[tuple[str, str, bool]]]:
    """Returns ([(key, message)] failures, keys accounted for, [(key, message, is_deletion)]).

    The third list is the DEPARTURES (#2398): the type is gone from this repo's `src/` while the
    assembly it left is still built here. `is_deletion` is True only when `siblings` was supplied
    and the type is in none of them — i.e. the deletion is PROVEN rather than merely assumed.

    `siblings` maps a checkout label to that checkout's declarations by simple name; it is the ONLY
    thing that can tell a cross-repo move from a deletion, since inside this repo both are "the
    file is gone".

    The key is what `scripts/type-forwards.allow` names, so `check` can match an allow entry
    EXACTLY — a substring match on the message would let one entry silence a same-named type in a
    different assembly."""
    by_simple_name: dict[str, list[Decl]] = {}
    for d in after.values():
        by_simple_name.setdefault(d.name, []).append(d)

    failures: list[tuple[str, str]] = []
    moved: set[str] = set()
    departed: list[tuple[str, str, bool]] = []
    for key, old in sorted(before.items()):
        if key in after:
            continue
        if head_assemblies is not None and old.assembly not in head_assemblies:
            # The WHOLE assembly left this repo (the #2276 wave moves several to MeshWeaver.Plugins).
            # There is nowhere here to put a forwarder, and the assembly keeps its name and its
            # consumers wherever it is now built — so a same-simple-named type elsewhere in src/ is a
            # coincidence, not a move. Skipping this is what keeps the gate honest: without it, the
            # Blazor exit alone produces 15 false positives that would train people to allow-list.
            continue
        landed = [d for d in by_simple_name.get(old.name, []) if d.assembly != old.assembly]
        if not landed:
            # 🚨 #2398: this used to be a silent `continue`, and that is how the gate went green on
            # the seven types that left GitSync/Hosting for MeshWeaver.AI. Post-#2276, MeshWeaver.AI
            # is built in MeshWeaver.Plugins, so the move left NO landing site here and read as a
            # deletion — the one shape the gate treated as out of scope. It is reported now.
            departed.append(_departure(key, old, siblings))
            if not departed[-1][2]:
                moved.add(key)  # allow-listable, and the entry stays honest via the stale ratchet
            continue
        moved.add(key)
        # The old assembly forwards the ORIGINAL full name: binary-compatible. Accept the
        # unqualified `typeof(Foo)` spelling too — C# resolves it against that file's usings, and a
        # gate that only understood one of the two spellings would demand a forwarder that is
        # already there.
        if key in forwards or f"{old.assembly}:{old.name}" in forwards:
            continue
        destinations = ", ".join(sorted(f"{d.assembly} ({d.full_name})" for d in landed))
        renamed = all(d.full_name != old.full_name for d in landed)
        remedy = (
            f"      A forwarder cannot rename. Restore `namespace {old.namespace};` on the type in "
            f"its new assembly, then add the forwarder."
            if renamed
            else f"      Add to src/{old.assembly}: "
            f"[assembly: TypeForwardedTo(typeof({old.full_name}))]"
        )
        failures.append((
            key,
            f"  {old.full_name}\n"
            f"      left  {old.assembly}\n"
            f"      now in {destinations}\n"
            f"      No [assembly: TypeForwardedTo(typeof({old.full_name}))] in src/{old.assembly}, so "
            f"every module compiled\n"
            f"      against the old platform binds a TypeRef that no longer resolves (#2370).\n"
            f"{remedy}",
        ))
    return failures, moved, departed


def check(
    root: Path, base: str, head: str | None = None, sibling_paths: list[str] | None = None
) -> int:
    before, _, _ = read_base_tree(base)
    after, forwards, head_assemblies = (
        read_base_tree(head) if head else read_work_tree(root)
    )
    allow = read_allow(root)
    siblings = {Path(p).resolve().name: read_sibling_tree(Path(p)) for p in (sibling_paths or [])}
    for label, index in siblings.items():
        print(f"Sibling checkout {label}: {sum(len(v) for v in index.values())} public types.")

    failures, moved, departed = find_moves(
        before, after, forwards, head_assemblies, siblings or None
    )

    unmatched = [key for key in allow if key not in moved]
    remaining = [message for key, message in failures if key not in allow]
    # A PROVEN deletion is reported and passes; anything else in this category fails, and the
    # allow file is the same escape hatch it is for a move.
    departures_failing = [
        (key, message) for key, message, is_deletion in departed if not is_deletion and key not in allow
    ]
    deletions = [message for _, message, is_deletion in departed if is_deletion]

    if deletions:
        print(
            f"\n{len(deletions)} public type(s) left src/ with no forwarder and were found in NONE of "
            f"the sibling checkouts given — a proven deletion, reported but not failed:\n"
        )
        print("\n\n".join(deletions))

    if not remaining and not unmatched and not departures_failing:
        print(f"OK — no unguarded public-type move against {base}.")
        return 0

    if remaining:
        print(
            "\nA PUBLIC TYPE MOVED ASSEMBLIES WITHOUT A TYPE FORWARDER — binary-breaking for every\n"
            "module compiled against an earlier platform (#2370):\n"
        )
        print("\n\n".join(remaining))
        print(
            f"\nIf no shipped module can hold this TypeRef, record that in {ALLOW_FILE} with a reason.\n"
        )
    if departures_failing:
        print(
            f"\n{len(departures_failing)} PUBLIC TYPE(S) LEFT src/ WITH NO FORWARDER AND NO LANDING SITE\n"
            f"IN THIS REPO — verify they did not move to a sibling repo (#2398). Post-#2276 the\n"
            f"module assemblies are built in MeshWeaver.Plugins, so a move into one of them deletes\n"
            f"files here and adds none: indistinguishable from a deletion, and binary-breaking\n"
            f"either way for a module compiled against an earlier platform (#2370).\n"
        )
        print("\n\n".join(message for _, message in departures_failing))
        print(
            f"\nRe-run with --sibling <checkout> (e.g. ../MeshWeaver.Plugins) to say which of these\n"
            f"moved and which were deleted — a proven deletion is reported and passes. If no shipped\n"
            f"module can hold the TypeRef, record that in {ALLOW_FILE} with a reason.\n"
        )
    for key in sorted(unmatched):
        print(
            f"STALE ALLOW ENTRY: {ALLOW_FILE} lists `{key}`, but nothing of that name moved in this\n"
            f"  diff. Delete the line — an entry that outlives its change hides the next break."
        )
    return 1


# ─────────────────────────────── self-test ───────────────────────────────
#
# Every FAILING case keeps a second file in the OLD assembly ("Keep"): an assembly with no files
# left at HEAD has left the repo, and the gate skips it deliberately (see find_moves).

AI_OPS_OLD = "namespace MeshWeaver.AI;\npublic class MeshOperations\n{\n}\n"
OPS_NEW_NS = "namespace MeshWeaver.Mesh;\npublic class MeshOperations\n{\n}\n"
AI_KEEP = "namespace MeshWeaver.AI;\npublic class MeshPlugin\n{\n}\n"
FOO_N = "namespace N;\npublic sealed record Foo(int X);\n"
FOO_GENERIC = "namespace N;\npublic sealed record class Foo<T> where T : notnull\n{\n}\n"
FOO_BLOCK_NS = "namespace N\n{\n    public record Foo(int X);\n}\n"
FOO_BLOCK_NS_BRACE = "namespace N {\n    public record Foo(int X);\n}\n"
FOO_BLOCK_NS_NESTED = (
    "namespace N\n{\n    public class Foo\n    {\n        public class Inner\n        {\n"
    "        }\n    }\n}\n"
)
INNER_MOVED = "namespace M;\npublic class Inner\n{\n}\n"
KEEP_A_BLOCK = "namespace N\n{\n    public class Keep\n    {\n    }\n}\n"
KEEP_A = "namespace N;\npublic class Keep\n{\n}\n"
DOC_KEEP = "namespace MeshWeaver.Documentation;\npublic class Doc\n{\n}\n"
BLAZOR_PORTAL_APP = "namespace MeshWeaver.Blazor.Infrastructure;\npublic class PortalApplication\n{\n}\n"
ASPNET_PORTAL_APP = "namespace MeshWeaver.Hosting.AspNetCore.Portal;\npublic class PortalApplication\n{\n}\n"
ASPNET_KEEP = "namespace MeshWeaver.Hosting.AspNetCore;\npublic class Other\n{\n}\n"
ENUM_E = "namespace N;\npublic enum E\n{\n    A,\n}\n"
DELEGATE_D = "namespace N;\npublic delegate void D(int x);\n"
# The #2398 seven, in miniature: the type keeps its simple name and CHANGES namespace, which is
# why the sibling lookup keys on the simple name and prints the destination's full name.
GITSYNC_SYNC_AREA = "namespace MeshWeaver.GitSync;\npublic static class AiContentSyncArea\n{\n}\n"
GITSYNC_KEEP = "namespace MeshWeaver.GitSync;\npublic class GitSyncPlugin\n{\n}\n"
AI_SYNC_AREA = "namespace MeshWeaver.AI;\npublic static class AiContentSyncArea\n{\n}\n"

# (label, base files, head files, should_pass[, sibling checkouts][, substrings the output must
# contain]). The last two are optional so the rows that predate --sibling stay as they were.
SELF_TESTS: list[tuple] = [
    (
        "the #2370 move: assembly AND namespace changed, no forwarder",
        {
            "src/MeshWeaver.AI/MeshOperations.cs": AI_OPS_OLD,
            "src/MeshWeaver.AI/MeshPlugin.cs": AI_KEEP,
        },
        {
            "src/MeshWeaver.Mesh.Operations/MeshOperations.cs": OPS_NEW_NS,
            "src/MeshWeaver.AI/MeshPlugin.cs": AI_KEEP,
        },
        False,
    ),
    (
        "the #2370 fix: original full name kept, forwarder left behind",
        {
            "src/MeshWeaver.AI/MeshOperations.cs": AI_OPS_OLD,
            "src/MeshWeaver.AI/MeshPlugin.cs": AI_KEEP,
        },
        {
            "src/MeshWeaver.Mesh.Operations/MeshOperations.cs": AI_OPS_OLD,
            "src/MeshWeaver.AI/MeshPlugin.cs": AI_KEEP,
            "src/MeshWeaver.AI/TypeForwards.cs":
                "[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.MeshOperations))]\n",
        },
        True,
    ),
    (
        "the forwarder is FULLY QUALIFIED — same ExportedType row, must be accepted",
        {
            "src/MeshWeaver.AI/MeshOperations.cs": AI_OPS_OLD,
            "src/MeshWeaver.AI/MeshPlugin.cs": AI_KEEP,
        },
        {
            "src/MeshWeaver.Mesh.Operations/MeshOperations.cs": AI_OPS_OLD,
            "src/MeshWeaver.AI/MeshPlugin.cs": AI_KEEP,
            # No `using System.Runtime.CompilerServices;` — so the attribute is written out in
            # full, which the C# compiler accepts and emits identically. The gate refused exactly
            # this shape until the qualifier was made optional, demanding a forwarder that was
            # already there. Pinned so a narrowing of FORWARD_RE cannot quietly bring that back.
            "src/MeshWeaver.AI/TypeForwards.cs":
                "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(\n"
                "    typeof(MeshWeaver.AI.MeshOperations))]\n",
        },
        True,
    ),
    (
        "assembly move only (namespace unchanged), but the forwarder is missing",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {"src/B/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        False,
    ),
    (
        "assembly move only, forwarder present",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {
            "src/B/Foo.cs": FOO_N,
            "src/A/Keep.cs": KEEP_A,
            "src/A/Fwd.cs": "[assembly: TypeForwardedTo(typeof(N.Foo))]\n",
        },
        True,
    ),
    (
        "an UNQUALIFIED forwarder counts — `typeof(Foo)` resolves through the file's usings",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {
            "src/B/Foo.cs": FOO_N,
            "src/A/Keep.cs": KEEP_A,
            "src/A/Fwd.cs": "using N;\n[assembly: TypeForwardedTo(typeof(Foo))]\n",
        },
        True,
    ),
    (
        # The gate's own negative control found this hole (#2398): commenting the forwarders out
        # left the gate GREEN, so a disabled forwarder and a present one read the same.
        "a COMMENTED-OUT forwarder does not count",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {
            "src/B/Foo.cs": FOO_N,
            "src/A/Keep.cs": KEEP_A,
            "src/A/Fwd.cs": "// [assembly: TypeForwardedTo(typeof(N.Foo))]\n",
        },
        False,
    ),
    (
        "...nor one left behind mid-line in a note about its removal",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {
            "src/B/Foo.cs": FOO_N,
            "src/A/Keep.cs": KEEP_A,
            "src/A/Fwd.cs": "// dropped in #9999: [assembly: TypeForwardedTo(typeof(N.Foo))]\n",
        },
        False,
    ),
    (
        # ...but a REAL forwarder carrying a trailing comment is still a forwarder. Without this
        # row, "strip everything after //" would look correct while silencing live forwarders.
        "a forwarder followed by an explanatory comment still counts",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {
            "src/B/Foo.cs": FOO_N,
            "src/A/Keep.cs": KEEP_A,
            "src/A/Fwd.cs": "[assembly: TypeForwardedTo(typeof(N.Foo))]  // moved in #1234\n",
        },
        True,
    ),
    (
        "a forwarder in the WRONG assembly does not count",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {
            "src/B/Foo.cs": FOO_N,
            "src/A/Keep.cs": KEEP_A,
            "src/C/Fwd.cs": "[assembly: TypeForwardedTo(typeof(N.Foo))]\n",
        },
        False,
    ),
    (
        "a forwarder for a DIFFERENT full name does not count (a forwarder cannot rename)",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {
            "src/B/Foo.cs": "namespace M;\npublic class Foo\n{\n}\n",
            "src/A/Keep.cs": KEEP_A,
            "src/A/Fwd.cs": "[assembly: TypeForwardedTo(typeof(M.Foo))]\n",
        },
        False,
    ),
    (
        # 🚨 POLICY CHANGE (#2398). This row used to assert `True` — "a deletion reads AS a
        # deletion in review". Post-#2276 it does not: a type moving into an assembly built in
        # MeshWeaver.Plugins deletes files here and adds none, so a cross-repo move and a deletion
        # are the SAME diff in this repo. The gate reports both and lets --sibling separate them.
        "a type gone from src/ while its assembly is still here is a DEPARTURE, not silence",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {"src/A/Keep.cs": KEEP_A},
        False,
    ),
    (
        # THE #2398 SHAPE, exactly: MeshWeaver.GitSync stays in core, AiContentSyncArea leaves it
        # for MeshWeaver.AI — which is built in MeshWeaver.Plugins, so there is no landing site
        # here. Before this change the gate hit `if not landed: continue` and printed OK.
        "the #2398 shape: a type leaves a core assembly for one built in a SIBLING repo",
        {
            "src/MeshWeaver.GitSync/AiContentSyncArea.cs": GITSYNC_SYNC_AREA,
            "src/MeshWeaver.GitSync/Keep.cs": GITSYNC_KEEP,
        },
        {"src/MeshWeaver.GitSync/Keep.cs": GITSYNC_KEEP},
        False,
    ),
    (
        "…and with --sibling it is named as a CROSS-REPO MOVE, with repo, assembly and new name",
        {
            "src/MeshWeaver.GitSync/AiContentSyncArea.cs": GITSYNC_SYNC_AREA,
            "src/MeshWeaver.GitSync/Keep.cs": GITSYNC_KEEP,
        },
        {"src/MeshWeaver.GitSync/Keep.cs": GITSYNC_KEEP},
        False,
        {"MeshWeaver.Plugins": {"src/MeshWeaver.AI/AiContentSyncArea.cs": AI_SYNC_AREA}},
        ("CROSS-REPO MOVE", "MeshWeaver.Plugins/src/MeshWeaver.AI", "MeshWeaver.AI.AiContentSyncArea"),
    ),
    (
        # The other half, and the reason --sibling can only RELAX: with the sibling in hand the
        # deletion is PROVEN, so the original scoping decision is restored — on evidence this time.
        "…while a departed type in NO sibling given is a proven deletion and passes",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {"src/A/Keep.cs": KEEP_A},
        True,
        {"MeshWeaver.Plugins": {"src/MeshWeaver.AI/Unrelated.cs": "namespace Q;\npublic class Unrelated\n{\n}\n"}},
    ),
    (
        # A sibling can only answer for types it declares. A same-named type in a sibling's OWN
        # source is what makes the move provable; one in its bin/obj output is not source at all.
        "a sibling's bin/obj output is not a landing site",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {"src/A/Keep.cs": KEEP_A},
        True,
        {"MeshWeaver.Plugins": {"src/B/obj/Generated/Foo.cs": FOO_N}},
    ),
    (
        # The whole-assembly-left skip still wins over the departure report: the assembly is not
        # built here any more, so there is no forwarder to demand and no departure to report.
        "an assembly that LEFT the repo produces no departures either",
        {
            "src/MeshWeaver.Blazor/Infrastructure/PortalApplication.cs": BLAZOR_PORTAL_APP,
            "src/MeshWeaver.Hosting.AspNetCore/Other.cs": ASPNET_KEEP,
        },
        {"src/MeshWeaver.Hosting.AspNetCore/Other.cs": ASPNET_KEEP},
        True,
    ),
    (
        # The Blazor exit (#2276) is exactly this, 15 times over. Without the whole-assembly-left
        # rule the gate reports every one and teaches people to allow-list.
        "an assembly LEAVES the repo while a same-named type exists elsewhere — must not fire",
        {
            "src/MeshWeaver.Blazor/Infrastructure/PortalApplication.cs": BLAZOR_PORTAL_APP,
            "src/MeshWeaver.Hosting.AspNetCore/Other.cs": ASPNET_KEEP,
        },
        {
            "src/MeshWeaver.Hosting.AspNetCore/Portal/PortalApplication.cs": ASPNET_PORTAL_APP,
            # Kept at HEAD so the row tests ONLY the whole-assembly-left rule. Dropping it made
            # `Other` depart from an assembly that is still here, which the departure report
            # (#2398) correctly fires on — a second finding that has nothing to do with Blazor.
            "src/MeshWeaver.Hosting.AspNetCore/Other.cs": ASPNET_KEEP,
        },
        True,
    ),
    (
        "...but a move out of an assembly that IS still here still fires",
        {
            "src/MeshWeaver.Blazor/Infrastructure/PortalApplication.cs": BLAZOR_PORTAL_APP,
            "src/MeshWeaver.Blazor/Keep.cs": "namespace MeshWeaver.Blazor;\npublic class Keep\n{\n}\n",
        },
        {
            "src/MeshWeaver.Blazor/Keep.cs": "namespace MeshWeaver.Blazor;\npublic class Keep\n{\n}\n",
            "src/MeshWeaver.Hosting.AspNetCore/Portal/PortalApplication.cs": ASPNET_PORTAL_APP,
        },
        False,
    ),
    (
        "moving a file WITHIN one assembly is not a move at all",
        {"src/A/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        {"src/A/Sub/Foo.cs": FOO_N, "src/A/Keep.cs": KEEP_A},
        True,
    ),
    (
        "an INTERNAL type moving is not a binary contract",
        {"src/A/Foo.cs": "namespace N;\ninternal class Foo\n{\n}\n", "src/A/Keep.cs": KEEP_A},
        {"src/B/Foo.cs": "namespace M;\ninternal class Foo\n{\n}\n", "src/A/Keep.cs": KEEP_A},
        True,
    ),
    (
        "a NESTED public type is not independently bindable — indented, so out of scope",
        {
            "src/A/Foo.cs": "namespace N;\npublic class Foo\n{\n    public class Inner\n    {\n    }\n}\n",
            "src/A/Keep.cs": KEEP_A,
        },
        {
            "src/A/Foo.cs": FOO_N,
            "src/A/Keep.cs": KEEP_A,
            "src/B/Inner.cs": "namespace M;\npublic class Inner\n{\n}\n",
        },
        True,
    ),
    (
        "`public record class` / generics parse (they are the shapes that make a matcher lie)",
        {"src/A/Foo.cs": FOO_GENERIC, "src/A/Keep.cs": KEEP_A},
        {"src/B/Foo.cs": FOO_GENERIC, "src/A/Keep.cs": KEEP_A},
        False,
    ),
    (
        # 38 files under src/ still declare a block-scoped namespace, which indents every top-level
        # type by four. An earlier revision anchored the matcher at column 0 and skipped all of
        # them — covering less while still reporting green.
        "a block-scoped namespace indents its TOP-LEVEL types, and they are still in scope",
        {"src/A/Foo.cs": FOO_BLOCK_NS, "src/A/Keep.cs": KEEP_A_BLOCK},
        {"src/B/Foo.cs": FOO_BLOCK_NS, "src/A/Keep.cs": KEEP_A_BLOCK},
        False,
    ),
    (
        "…with the brace on the namespace's own line, too",
        {"src/A/Foo.cs": FOO_BLOCK_NS_BRACE, "src/A/Keep.cs": KEEP_A_BLOCK},
        {"src/B/Foo.cs": FOO_BLOCK_NS_BRACE, "src/A/Keep.cs": KEEP_A_BLOCK},
        False,
    ),
    (
        "…but a type NESTED inside a block-scoped namespace's type stays out of scope",
        {"src/A/Foo.cs": FOO_BLOCK_NS_NESTED, "src/A/Keep.cs": KEEP_A_BLOCK},
        {
            "src/A/Foo.cs": "namespace N\n{\n    public class Foo\n    {\n    }\n}\n",
            "src/A/Keep.cs": KEEP_A_BLOCK,
            "src/B/Inner.cs": INNER_MOVED,
        },
        True,
    ),
    (
        "an in-mesh doc sample is not compiled, so it is not a binary move",
        {
            "src/MeshWeaver.Documentation/Data/X/Source/Foo.cs": FOO_N,
            "src/MeshWeaver.Documentation/Doc.cs": DOC_KEEP,
        },
        {
            "src/MeshWeaver.Documentation/Data/Y/Source/Foo.cs": FOO_N,
            "src/MeshWeaver.Documentation/Doc.cs": DOC_KEEP,
        },
        True,
    ),
    (
        "a public enum is a type too",
        {"src/A/E.cs": ENUM_E, "src/A/Keep.cs": KEEP_A},
        {"src/B/E.cs": ENUM_E, "src/A/Keep.cs": KEEP_A},
        False,
    ),
    (
        "a public delegate is a type too",
        {"src/A/D.cs": DELEGATE_D, "src/A/Keep.cs": KEEP_A},
        {"src/B/D.cs": DELEGATE_D, "src/A/Keep.cs": KEEP_A},
        False,
    ),
]


def self_test() -> int:
    failed = 0
    for entry in SELF_TESTS:
        label, base_files, head_files, should_pass = entry[:4]
        sibling_files: dict[str, dict[str, str]] = entry[4] if len(entry) > 4 else {}
        expected_substrings: tuple[str, ...] = entry[5] if len(entry) > 5 else ()
        before: dict[str, Decl] = {}
        for path, text in base_files.items():
            if _is_scanned(path):
                for d in parse_declarations(path, text):
                    before[d.key] = d
        after: dict[str, Decl] = {}
        forwards: set[str] = set()
        head_assemblies: set[str] = set()
        for path, text in head_files.items():
            if not _is_scanned(path):
                continue
            head_assemblies.add(_assembly_of(path))
            for d in parse_declarations(path, text):
                after[d.key] = d
            forwards |= {f"{_assembly_of(path)}:{t}" for t in parse_forwards(text)}
        siblings: dict[str, dict[str, list[Decl]]] = {}
        for sibling_label, files in sibling_files.items():
            index: dict[str, list[Decl]] = {}
            for path, text in files.items():
                if not _is_scanned(path):
                    continue
                for d in parse_declarations(path, text):
                    index.setdefault(d.name, []).append(d)
            siblings[sibling_label] = index
        failures, _, departed = find_moves(
            before, after, forwards, head_assemblies, siblings or None
        )
        # A departure that is not a PROVEN deletion is a failure of the run, exactly as `check`
        # treats it — so `should_pass` means the same thing for both categories.
        reported = [m for _, m in failures] + [m for _, m, deleted in departed if not deleted]
        passed = not reported
        missing = [s for s in expected_substrings if not any(s in m for m in reported)]
        if passed != should_pass or missing:
            failed += 1
            print(f"SELF-TEST FAILED: {label}")
            if passed != should_pass:
                print(
                    f"  expected {'pass' if should_pass else 'FAIL'}, "
                    f"got {'pass' if passed else 'FAIL'}"
                )
            for s in missing:
                print(f"  output never mentioned {s!r}")
            for message in reported:
                print(message)
        else:
            print(f"ok: {label}")
    if failed:
        print(f"\n{failed} self-test(s) failed — the gate cannot be trusted.")
        return 1
    print(f"\nAll {len(SELF_TESTS)} self-tests passed.")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--base", help="commit-ish to compare against (e.g. origin/main)")
    ap.add_argument(
        "--head",
        help="commit-ish to check INSTEAD of the working tree — for replaying real history "
        "(e.g. --base b53aed0aa^1 --head b53aed0aa reproduces the #2370 break)",
    )
    ap.add_argument(
        "--sibling",
        action="append",
        default=[],
        metavar="CHECKOUT",
        help="path to a sibling repo checkout that builds platform assemblies (repeatable, e.g. "
        "../MeshWeaver.Plugins). Resolves a DEPARTED type — gone from this repo's src/ while the "
        "assembly it left is still built here — into a proven CROSS-REPO MOVE or a proven "
        "DELETION. It only ever relaxes the verdict, so omitting it is the conservative mode; CI "
        "omits it deliberately (see the module docstring).",
    )
    ap.add_argument("--self-test", action="store_true", help="prove the matcher is not vacuous")
    args = ap.parse_args()

    if args.self_test:
        return self_test()
    if not args.base:
        ap.error("--base is required unless --self-test is given")

    root = Path(
        subprocess.run(
            ["git", "rev-parse", "--show-toplevel"], check=True, capture_output=True, text=True
        ).stdout.strip()
    )
    for path in args.sibling:
        if not (Path(path) / "src").is_dir():
            # A typo'd sibling path would silently turn every cross-repo move into a "proven
            # deletion" — the flag's one dangerous failure mode, since it RELAXES the verdict.
            ap.error(f"--sibling {path}: no src/ directory there")
    return check(root, args.base, args.head, args.sibling)


if __name__ == "__main__":
    sys.exit(main())
