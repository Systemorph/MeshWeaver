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
takes one fully-qualified type per line, then the PULL REQUEST the entry travels with, then a
reason:

    Rail  #3414 — the two order-losing buckets become one ordered sequence …

An entry is a statement that the atomic move is planned, not a way to make the gate quiet, and it
is TRANSITIONAL: `scripts/transitional_allow.py` scopes it to the diff that introduces it and
resolves the pull request it names, so it expires on merge by mechanism rather than by a comment
asking a future reader to delete it (#3422 — that comment was ignored and reddened every
C#-touching pull request in the fleet for ~40 minutes). A LIVE entry that matches nothing in the
diff carrying it still FAILS, exactly like the repo's other ratchets; an entry the merge base
already carries is INERT — it allows nothing and fails nothing.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

import transitional_allow as ta

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


# 🚨 A UTF-8 BOM is not whitespace and it is not a line start. `RECORD_RE` anchors on `^` and
# opens with `\s*`, and Python's `\s` does NOT match U+FEFF (measured: `re.match(r"\s", "\ufeff")`
# is None, `"\ufeff".isspace()` is False). So in a file whose BOM sits immediately before a public
# record — a record on LINE 1 — the declaration matches nothing and its primary constructor can
# change unchallenged, which is the one thing this gate exists to refuse.
#
# 🚨 READING A FILE WITHOUT ERROR IS NOT READING IT CORRECTLY. `run()` reads with
# `errors="replace"`, which guarantees no exception — a DIFFERENT property, and an easy one to
# mistake for robustness. It neither throws nor corrupts; it silently hands back U+FEFF as the
# first character. `encoding="utf-8-sig"` would strip it, but the BEFORE side of every comparison
# comes from `git show`, not from `read_text`, so a fix at the read site would cover only half the
# diff. The strip therefore lives HERE, where both sides pass through.
#
# Measured on `main`, 2026-09-06: 1281 scanned files under `src/`, 310 carrying a BOM, 522 public
# records — and ZERO files where the BOM currently hides one. The hole is LATENT, not live: no
# record is on line 1 of a BOM'd file today. It is fixed anyway, because its sibling gate
# (`check-type-forwards.py`, where the same blindness WAS live — 16 public types invisible, 128
# miskeyed) learned about BOMs in the same change, and one gate knowing what its neighbour does
# not is exactly how this survived unnoticed in both.
def _strip_bom(text: str) -> str:
    return text.lstrip("\ufeff")


def records_in(source: str) -> dict[str, list[Param]]:
    """Every public record's primary-constructor parameter list, by record name."""
    source = _strip_bom(source)
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


def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(root), *args],
                          capture_output=True, text=True).stdout


def run(root: Path, base: str, resolve, this_pr: int | None = None) -> int:
    # 🚨 The allow file is judged BEFORE the early return on "no changed C# files". A pull request
    # that adds an entry while changing no C# would otherwise slip past having declared an
    # allowance nothing checked — the same shape as a gate that skips on a missing input.
    judgements, landed = ta.judge(root, ALLOW_FILE, base, resolve, this_pr=this_pr)
    allow = ta.in_force(judgements)
    allow_lines = ta.report(ALLOW_FILE, judgements, landed)
    allow_failures = ta.failures(judgements)

    changed = [f for f in git(root, "diff", "--name-only", f"{base}...HEAD").splitlines()
               if f.endswith(".cs") and "/obj/" not in f and "/bin/" not in f]

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

    # An entry this diff INTRODUCES and then matches nothing with is the classic stale entry —
    # and it now fires only on the branch that carries the line, which is its author's own pull
    # request. An entry the merge base already carried is inert and is never counted here: that
    # is #3422's fleet-wide red, and it is gone by construction rather than by a reminder.
    unmatched = sorted(set(allow) - hit_allow)

    for line in allow_lines:
        print(line)
    for f in findings:
        print(f"\n✗ {f.file}\n    record {f.record} — {f.kind}\n        {f.detail}")
    for name in unmatched:
        print(f"\n✗ {ALLOW_FILE} entry `{name}` is added by this diff, but that record's primary\n"
              f"    constructor does not differ from the merge base. An allow entry says an atomic\n"
              f"    move is PLANNED — one that permits nothing is a lie that hides the next break.\n"
              f"    Delete the line, or make the change it was written for.")

    if findings or unmatched or allow_failures:
        print(f"\n🚨 {len(findings)} binary-breaking record change(s), {len(unmatched)} allow "
              f"entr(ies) permitting nothing, {len(allow_failures)} unusable allow entr(ies).")
        print("   A module and the platform it loads into must agree on this signature EXACTLY.")
        print("   There is no image that can serve a mixed set — the host aborts at boot, in both")
        print("   directions. If the change is deliberate, plan the atomic move and add the record")
        print(f"   to {ALLOW_FILE} as `<Record>  #<this pull request> — <reason>`.")
        return 1

    if not changed:
        print("no changed C# files — nothing to check")
        return 0
    print(f"✓ {len(changed)} changed C# file(s): no binary-breaking primary-constructor changes"
          + (f" ({len(allow)} allow entr(ies) in force)" if allow else ""))
    return 0


def self_test() -> int:
    """Both real incidents, plus the parses that would make the gate lie."""
    failures: list[str] = []

    def check(label: str, cond: bool) -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {label}")
        if not cond:
            failures.append(label)

    # 🚨 THE CANARY, and it runs FIRST. Every case below indexes `records_in(...)` by record name,
    # so a `RECORD_RE` that has stopped matching does not FAIL them — it raises `KeyError` and
    # buries the verdict in a traceback, which is harder to read than the defect it found. One
    # cheap assertion up front turns "the self-test crashed" into "the classifier sees nothing",
    # which is the sentence a reader needs. It is the same lesson this file learned about reading a
    # file without error: not throwing and being correct are different properties.
    if "Canary" not in records_in("public sealed record Canary(int A);"):
        check("RECORD_RE still matches a plain public record — nothing below means anything "
              "if it does not", False)
        print("\nself-test: 1 FAILURE(S)")
        return 1

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

    # ── the transitional allowance, end to end through run() ─────────────────────────
    #
    # 🚨 These call run() on a real repository rather than the classifier in isolation. A guard
    # whose subject moved and whose roots did not passes having checked nothing: if run() ever
    # stopped consulting transitional_allow — or went back to reading the allow file whole — the
    # INERT case below would go red and the LIVE case would go green, so neither can drift quietly.
    for label, cond, evidence in _allowance_cases():
        check(label, cond)
        if not cond:
            print("       --- what the gate actually printed ---\n       "
                  + evidence.strip().replace("\n", "\n       "))

    # ── the BOM, and the DENOMINATOR that would have caught it without anyone looking ──
    bom = "﻿"
    on_line_one = "public sealed record Solo(int A);\n"
    check("a BOM does not hide a record declared on LINE 1",
          "Solo" in records_in(bom + on_line_one))
    check("…and a record on a later line was never at risk",
          "Bar" in records_in(bom + "namespace N;\npublic sealed record Bar(int A);\n"))
    # 🚨 Both sides are looked up DEFENSIVELY. If the strip regresses, this case must report a
    # clean FAIL naming itself — not blow up with a KeyError, which would bury the verdict in a
    # traceback and make the self-test's own failure harder to read than the defect it found.
    solo_before = records_in(bom + "public sealed record Solo(int A);\n").get("Solo")
    solo_after = records_in(bom + "public sealed record Solo(int A, int B);\n").get("Solo")
    check("the BOM is stripped on BOTH sides, so a BOM'd file still COMPARES",
          solo_before is not None and solo_after is not None
          and [x.kind for x in compare("Solo", solo_before, solo_after, "x.cs")] == ["arity"])

    for label, cond in _denominator_case():
        check(label, cond)

    print("\nself-test: " + ("PASSED" if not failures else f"{len(failures)} FAILURE(S)"))
    return 1 if failures else 0


# 🚨 THE DENOMINATOR. The BOM blindness survived in two gates at once because nothing anywhere
# stated how much either of them was seeing: a matcher that stops matching reports a clean tree
# forever, and "the codebase shrank" and "the parser broke" produce the identical output. This gate
# is worse off than its sibling for that, because it scans only the files a diff CHANGED — so its
# per-run count is legitimately zero on most pull requests and can never be a floor.
#
# So the floor is asserted against the WHOLE tree, in the self-test that runs first in CI: index
# every `src/**/*.cs` and refuse to certify the gate if the public-record count has collapsed.
# MEASURED on `main`, 2026-09-06: 522 public records across 1281 scanned files. The floor sits far
# below that and far above zero, so it survives a carve-out wave and still fails outright on a
# `RECORD_RE` that has stopped matching.
MIN_PUBLIC_RECORDS_IN_TREE = 200


def _denominator_case() -> list[tuple[str, bool]]:
    root = Path(__file__).resolve().parent.parent
    src = root / "src"
    if not src.is_dir():
        # Refuse to report a pass having found no tree to count. Absent is not clean.
        return [(f"the tree denominator could not be read ({src} is not a directory)", False)]

    files = records = 0
    for f in src.rglob("*.cs"):
        rel = f.relative_to(root).as_posix()
        if "/bin/" in rel or "/obj/" in rel:
            continue
        files += 1
        records += len(records_in(f.read_text(encoding="utf-8", errors="replace")))

    return [(
        f"the tree declares {records} public record(s) across {files} scanned file(s) — at or "
        f"above the floor of {MIN_PUBLIC_RECORDS_IN_TREE}",
        records >= MIN_PUBLIC_RECORDS_IN_TREE,
    )]


_RAIL_BEFORE = "namespace N;\npublic sealed record Rail(int A);\n"
_RAIL_AFTER = "namespace N;\npublic sealed record Rail(int A, int B);\n"
_OTHER = "namespace N;\npublic sealed record Other(int A);\n"
_ENTRY = "Rail  #3414 — the two order-losing buckets become one ordered sequence\n"


def _allowance_cases() -> list[tuple[str, bool, str]]:
    """Replay #3422 against the gate itself: an entry lives for one diff and dies with the merge.

    Every case is a real commit on a real repository, because that is the only way the SCOPE half
    can be exercised at all — "already in the merge base" is a fact about history, not about text.
    """
    import contextlib
    import io
    import tempfile

    env = {**os.environ, "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
           "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t"}
    out: list[tuple[str, bool, str]] = []

    def quiet(*args, **kwargs) -> int:
        """run(), with its report captured — and replayed only when the case FAILS."""
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = run(*args, **kwargs)
        quiet.last = buffer.getvalue()  # type: ignore[attr-defined]
        return code

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "scripts").mkdir()
        allow = root / ALLOW_FILE

        def g(*args: str) -> None:
            done = subprocess.run(["git", "-C", str(root), *args], capture_output=True,
                                  text=True, env=env)
            if done.returncode != 0:
                raise RuntimeError(f"git {' '.join(args)}: {done.stderr}")

        def rev() -> str:
            return subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"],
                                  capture_output=True, text=True).stdout.strip()

        def commit(message: str) -> str:
            g("add", "-A")
            g("commit", "-qm", message)
            return rev()

        g("init", "-q", "-b", "main")
        (root / "Rail.cs").write_text(_RAIL_BEFORE, encoding="utf-8")
        allow.write_text("# header only\n", encoding="utf-8")
        base = commit("base")

        # ── the pull request that breaks the signature ────────────────────────────────────
        g("checkout", "-q", "-b", "the-change")
        (root / "Rail.cs").write_text(_RAIL_AFTER, encoding="utf-8")
        commit("break the primary constructor")
        # THE CONTROL ARM. Without it, a gate that allowed everything would score identically.
        out.append(("a break with no allow entry FAILS",
                    quiet(root, base, ta._resolver(ta._state())) == 1, quiet.last))

        allow.write_text("# header only\n" + _ENTRY, encoding="utf-8")
        commit("declare the allowance")
        out.append(("…with an entry naming an OPEN pull request it PASSES",
                    quiet(root, base, ta._resolver(ta._state())) == 0, quiet.last))
        # \U0001f6a8 An entry written for a merge that has already happened is stale by definition.
        out.append(("…naming a MERGED pull request it FAILS as stale",
                    quiet(root, base, ta._resolver(ta._state(merged=True, state="closed"))) == 1, quiet.last))
        out.append(("…naming a CLOSED-unmerged pull request it FAILS",
                    quiet(root, base, ta._resolver(ta._state(state="closed"))) == 1, quiet.last))
        # What #3414's entry actually carried: #3406, the ISSUE.
        out.append(("…naming an ISSUE it FAILS",
                    quiet(root, base, ta._resolver(ta._state(isPullRequest=False))) == 1, quiet.last))
        # \U0001f6a8 The one thing it must never do is pass because it could not tell.
        out.append(("…with an UNRESOLVABLE reference it FAILS rather than passing",
                    quiet(root, base, ta._raising("could not reach api.github.com")) == 1, quiet.last))
        out.append(("…naming a DIFFERENT pull request than the one under test it FAILS",
                    quiet(root, base, ta._resolver(ta._state()), this_pr=3415) == 1, quiet.last))
        out.append(("…and naming THIS pull request it PASSES",
                    quiet(root, base, ta._resolver(ta._state()), this_pr=3414) == 0, quiet.last))

        # ── it merges. Every later pull request now carries the line in its merge base ────
        g("checkout", "-q", "main")
        g("merge", "-q", "--no-ff", "-m", "merge the change", "the-change")
        merged = rev()

        # THE INCIDENT. Before #3422 this printed "1 stale allow entr(ies)" and went RED on every
        # C#-touching pull request in the fleet for ~40 minutes, while main itself read green.
        g("checkout", "-q", "-b", "somebody-else", merged)
        (root / "Other.cs").write_text(_OTHER, encoding="utf-8")
        commit("an unrelated change")
        out.append(("a LATER pull request is NOT reddened by the landed entry",
                    quiet(root, merged, ta._raising("must not be called")) == 0, quiet.last))

        # \U0001f6a8 …and the landed entry allows NOTHING, so a second break on the same record still
        # fails. That is the property the old stale ratchet existed to protect, kept without the red.
        g("checkout", "-q", "-b", "the-next-break", merged)
        (root / "Rail.cs").write_text(
            "namespace N;\npublic sealed record Rail(int A, int B, int C);\n", encoding="utf-8")
        commit("break it again")
        out.append(("…and the landed entry does NOT hide the next break on the same record",
                    quiet(root, merged, ta._raising("must not be called")) == 1, quiet.last))

    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", default="origin/main", help="merge base to compare against")
    ap.add_argument("--root", default=".", help="repository root")
    ap.add_argument("--pr", type=int, default=None,
                    help="the pull request under test. When given, an allow entry must name IT — "
                         "an entry is transitional for ONE change, the one it travels with. A "
                         "merge_group build has no single pull request, so it is omitted there "
                         "and the weaker rule (the entry must name an OPEN pull request) applies; "
                         "nothing reaches the queue without the pull_request run being green.")
    ap.add_argument("--repo", default=None,
                    help="owner/name whose pull requests an allow entry may name "
                         "(default: $GITHUB_REPOSITORY, else Systemorph/MeshWeaver)")
    ap.add_argument("--self-test", action="store_true", help="prove the gate catches the real cases")
    args = ap.parse_args()
    if args.self_test:
        return max(ta.self_test(), self_test())
    return run(Path(args.root).resolve(), args.base,
               ta.resolver_from_env(args.repo), args.pr)


if __name__ == "__main__":
    sys.exit(main())
