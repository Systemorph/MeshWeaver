#!/usr/bin/env python3
"""Refuse a binary-breaking change to a public record's primary constructor.

WHY THIS EXISTS
---------------
Adding a parameter to a record's primary constructor REPLACES the signature rather than
extending it — even when the new parameter has a default. C# compiles the call site against
the arity it saw, so a module built against the old shape calls a constructor the new
assembly does not have, and vice versa:

    new module + old platform -> MissingMethodException -> TypeInitializationException -> abort
    old module + new platform -> the identical abort, from the other side

There is therefore NO image that can serve a mixed set of module builds, and the failure is a
host abort at boot rather than a degraded module. `minMeshVersion` guards the first direction
only; nothing guards the second.

This is not hypothetical and it is not rare. Inside 24 hours (2026-08-25/26):

  * `LanguageModelCatalogSource` gained a 9th parameter. memex crashlooped on the roll and was
    reverted; its sibling survived the same bump only because the bundle/image ordering
    happened by accident.
  * `BurstReport.HeaderOnly` lost its default (`ImmutableList<T>? x = null` -> `ImmutableList<T> x`).
    Source-compatible inside the platform, binary-breaking for every caller that omitted it.
    It landed while the first was still an open incident.

Both passed review because both are source-compatible IN THE REPO MAKING THE CHANGE. The break
only appears in a consumer that was compiled earlier — which is precisely what a repo-local
test suite cannot see. Hence a gate rather than more care.

WHAT IT CHECKS
--------------
For every `public`/`protected` record (class or struct) with a primary constructor, comparing
the merge base against the working tree:

  * parameter COUNT changed            -> binary-breaking
  * a parameter LOST its default value -> binary-breaking
  * a parameter's TYPE changed         -> binary-breaking
  * a parameter was RENAMED            -> breaks named arguments and `with` positional callers

Reordering shows up as a type or name change at a position, so it is covered.

ADDING a parameter WITH a default is still a failure. That is the whole point: it is the exact
shape of the first incident, and it is the one people believe is safe.

THE ESCAPE HATCH
----------------
Sometimes the change is right and the fleet is moved deliberately. `scripts/record-signatures.allow`
takes one fully-qualified type per line with a trailing reason. An entry is a statement that the
atomic move is planned, not a way to make the gate quiet — so it must be deleted once the change
has shipped, and a stale entry (listed, but the record no longer differs) FAILS, exactly like the
repo's other ratchets.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

ALLOW_FILE = "scripts/record-signatures.allow"

# `public sealed record Foo(` / `public record struct Bar<T>(` / `protected record class Baz(`
RECORD_RE = re.compile(
    r"^\s*(?:public|protected(?:\s+internal)?)\s+"
    r"(?:(?:sealed|abstract|partial|readonly|unsafe)\s+)*"
    r"record\s+(?:(?:class|struct)\s+)?"
    r"(?P<name>[A-Za-z_]\w*)\s*"
    r"(?P<generics><[^(){}]*>)?\s*"
    r"\(",
    re.MULTILINE,
)


@dataclass(frozen=True)
class Param:
    type: str
    name: str
    has_default: bool

    def describe(self) -> str:
        return f"{self.type} {self.name}" + (" = …" if self.has_default else "")


@dataclass(frozen=True)
class Finding:
    file: str
    record: str
    kind: str
    detail: str


def split_params(text: str) -> list[str]:
    """Split a parameter list on top-level commas.

    Generic arguments, arrays, tuples and attributes all contain commas that are NOT
    separators — `ImmutableDictionary<string, int> map` is ONE parameter. Depth counting is
    what keeps `<`/`(`/`[` from being read as boundaries.
    """
    out, depth, current, in_str = [], 0, [], None
    for ch in text:
        if in_str:
            current.append(ch)
            if ch == in_str:
                in_str = None
            continue
        if ch in "\"'":
            in_str, _ = ch, current.append(ch)
            continue
        if ch in "<([{":
            depth += 1
        elif ch in ">)]}":
            depth -= 1
        if ch == "," and depth == 0:
            out.append("".join(current))
            current = []
            continue
        current.append(ch)
    if "".join(current).strip():
        out.append("".join(current))
    return [p.strip() for p in out if p.strip()]


def parse_param(raw: str) -> Param | None:
    """One parameter -> (type, name, has_default). Attributes and modifiers are stripped."""
    text = re.sub(r"\[[^\]]*\]", " ", raw).strip()          # [Attr] …
    has_default = False
    # A default may itself contain '=' (lambdas, ==) — split on the FIRST top-level '='.
    depth = 0
    for i, ch in enumerate(text):
        if ch in "<([{":
            depth += 1
        elif ch in ">)]}":
            depth -= 1
        elif ch == "=" and depth == 0 and text[i : i + 2] != "==" and (i == 0 or text[i - 1] != "="):
            text, has_default = text[:i].strip(), True
            break
    text = re.sub(r"^(?:params|ref|out|in|scoped|this)\s+", "", text).strip()
    if not text:
        return None
    parts = text.rsplit(" ", 1)
    if len(parts) != 2:
        return None
    type_, name = parts[0].strip(), parts[1].strip()
    if not name or not re.match(r"^[A-Za-z_@]\w*$", name):
        return None
    return Param(re.sub(r"\s+", " ", type_), name, has_default)


def records_in(source: str) -> dict[str, list[Param]]:
    """Every public record's primary-constructor parameter list, by record name."""
    found: dict[str, list[Param]] = {}
    for m in RECORD_RE.finditer(source):
        open_idx = source.index("(", m.end() - 1)
        depth, close = 0, None
        for i in range(open_idx, len(source)):
            if source[i] in "<([{":
                depth += 1
            elif source[i] in ">)]}":
                depth -= 1
                if depth == 0:
                    close = i
                    break
        if close is None:
            continue
        params = [p for p in (parse_param(r) for r in split_params(source[open_idx + 1 : close])) if p]
        found[m.group("name")] = params
    return found


def compare(name: str, before: list[Param], after: list[Param], path: str) -> list[Finding]:
    if len(before) != len(after):
        return [
            Finding(
                path, name, "arity",
                f"primary constructor went from {len(before)} to {len(after)} parameter(s). "
                f"Adding one — even WITH a default — replaces the signature: every assembly "
                f"compiled against the old arity calls a constructor this one no longer has.\n"
                f"        before: ({', '.join(p.describe() for p in before)})\n"
                f"        after:  ({', '.join(p.describe() for p in after)})",
            )
        ]
    out: list[Finding] = []
    for i, (b, a) in enumerate(zip(before, after)):
        if b.has_default and not a.has_default:
            out.append(Finding(path, name, "default-removed",
                               f"parameter {i} `{a.name}` lost its default value — source-compatible "
                               f"here, binary-breaking for every caller that omitted it"))
        if b.type != a.type:
            out.append(Finding(path, name, "type-changed",
                               f"parameter {i} changed type: `{b.type}` -> `{a.type}`"))
        elif b.name != a.name:
            out.append(Finding(path, name, "renamed",
                               f"parameter {i} renamed: `{b.name}` -> `{a.name}` — breaks named "
                               f"arguments and positional `with` callers"))
    return out


def read_allow(root: Path) -> dict[str, str]:
    path = root / ALLOW_FILE
    if not path.exists():
        return {}
    entries: dict[str, str] = {}
    for line in path.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        name, _, reason = line.partition(" ")
        entries[name.strip()] = reason.strip()
    return entries


def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(root), *args],
                          capture_output=True, text=True).stdout


def run(root: Path, base: str) -> int:
    changed = [f for f in git(root, "diff", "--name-only", f"{base}...HEAD").splitlines()
               if f.endswith(".cs") and "/obj/" not in f and "/bin/" not in f]
    if not changed:
        print("no changed C# files — nothing to check")
        return 0

    allow = read_allow(root)
    findings: list[Finding] = []
    hit_allow: set[str] = set()

    for rel in changed:
        after_path = root / rel
        after_src = after_path.read_text(errors="replace") if after_path.exists() else ""
        before_src = git(root, "show", f"{base}:{rel}")
        if not before_src:
            continue                                   # new file: nothing shipped yet
        before, after = records_in(before_src), records_in(after_src)
        for name, params in before.items():
            if name not in after:
                continue                               # deletion is a different question
            for f in compare(name, params, after[name], rel):
                if name in allow:
                    hit_allow.add(name)
                else:
                    findings.append(f)

    stale = sorted(set(allow) - hit_allow)

    for f in findings:
        print(f"\n✗ {f.file}\n    record {f.record} — {f.kind}\n        {f.detail}")
    for name in stale:
        print(f"\n✗ {ALLOW_FILE} lists `{name}`, but its primary constructor no longer differs.\n"
              f"    An allow entry says an atomic move is PLANNED. Once it has shipped the entry is\n"
              f"    a lie that hides the next break — delete the line.")

    if findings or stale:
        print(f"\n🚨 {len(findings)} binary-breaking record change(s), {len(stale)} stale allow entr(ies).")
        print("   A module and the platform it loads into must agree on this signature EXACTLY.")
        print("   There is no image that can serve a mixed set — the host aborts at boot, in both")
        print("   directions. If the change is deliberate, plan the atomic move and add the record")
        print(f"   to {ALLOW_FILE} with a reason.")
        return 1

    print(f"✓ {len(changed)} changed C# file(s): no binary-breaking primary-constructor changes"
          + (f" ({len(allow)} allow entr(ies), all still needed)" if allow else ""))
    return 0


def self_test() -> int:
    """Both real incidents, plus the parses that would make the gate lie."""
    failures: list[str] = []

    def check(label: str, cond: bool) -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {label}")
        if not cond:
            failures.append(label)

    # ── the two real incidents ────────────────────────────────────────────────────────────
    before = "public sealed record LanguageModelCatalogSource(string A, string B, int C, string D, string E, ImmutableArray<string> F, bool G, ProviderKind H);"
    after = before.replace("bool G, ProviderKind H)", "bool G, ProviderKind H, string I)")
    f = compare("LanguageModelCatalogSource", records_in(before)["LanguageModelCatalogSource"],
                records_in(after)["LanguageModelCatalogSource"], "x.cs")
    check("the 9th-parameter incident is caught", any(x.kind == "arity" for x in f))

    before = "public sealed record BurstReport(string Key, ImmutableList<HeaderOnlyBurst>? HeaderOnly = null);"
    after = "public sealed record BurstReport(string Key, ImmutableList<HeaderOnlyBurst> HeaderOnly);"
    f = compare("BurstReport", records_in(before)["BurstReport"],
                records_in(after)["BurstReport"], "x.cs")
    check("a lost default is caught", any(x.kind == "default-removed" for x in f))

    # ── adding WITH a default is still a break — the belief that makes this ship ──────────
    before = "public record R(int A);"
    after = "public record R(int A, int B = 0);"
    f = compare("R", records_in(before)["R"], records_in(after)["R"], "x.cs")
    check("adding a DEFAULTED parameter still fails", any(x.kind == "arity" for x in f))

    # ── things that must NOT fire ────────────────────────────────────────────────────────
    same = "public record S(ImmutableDictionary<string, int> Map, (int X, int Y) Point);"
    check("an unchanged record is clean",
          not compare("S", records_in(same)["S"], records_in(same)["S"], "x.cs"))
    check("a generic comma is not a separator", len(records_in(same)["S"]) == 2)

    check("a tuple comma is not a separator",
          records_in(same)["S"][1].name == "Point")

    body = "public record T(int A) { public int B { get; init; } }"
    check("a property is not a constructor parameter", len(records_in(body)["T"]) == 1)

    check("a non-record is ignored", "C" not in records_in("public class C(int a) { }"))
    check("a private record is ignored", "P" not in records_in("private record P(int a);"))

    # ── defaults that contain '=' or commas ──────────────────────────────────────────────
    d = records_in('public record D(string S = "a,b", int N = 1);')["D"]
    check("a default containing a comma stays one parameter", len(d) == 2)
    check("a defaulted parameter is recognised", all(p.has_default for p in d))

    # ── rename and type change ───────────────────────────────────────────────────────────
    f = compare("R", records_in("public record R(int A);")["R"],
                records_in("public record R(int B);")["R"], "x.cs")
    check("a rename is caught", any(x.kind == "renamed" for x in f))
    f = compare("R", records_in("public record R(int A);")["R"],
                records_in("public record R(long A);")["R"], "x.cs")
    check("a type change is caught", any(x.kind == "type-changed" for x in f))

    # ── attributes and modifiers ─────────────────────────────────────────────────────────
    a = records_in("public record A([property: JsonPropertyName(\"x\")] string X, params int[] Rest);")["A"]
    check("an attribute is stripped, not counted", len(a) == 2 and a[0].name == "X")
    check("a params modifier is stripped", a[1].name == "Rest")

    print("\nself-test: " + ("PASSED" if not failures else f"{len(failures)} FAILURE(S)"))
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", default="origin/main", help="merge base to compare against")
    ap.add_argument("--root", default=".", help="repository root")
    ap.add_argument("--self-test", action="store_true", help="prove the gate catches the real cases")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    return run(Path(args.root).resolve(), args.base)


if __name__ == "__main__":
    sys.exit(main())
