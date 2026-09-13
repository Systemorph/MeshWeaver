#!/usr/bin/env python3
"""check-resolver-copy.py — a repository's vendored `scripts/resolve-platform.py` must not DRIFT from
the platform's canonical copy (MeshWeaver.Plugins#1565; the same shape as MeshWeaver#4027).

WHY. Every satellite carries a copy of the resolver and runs it in its own `platform-ref` job — the
job that decides WHICH sealed platform set the repository builds and tests against. The reusable
lanes (`node-repo-gate.yml`, `node-repo-publish-bake.yml`) fetch the platform's copy at
`scripts-ref` for their own resolution, so a stale vendored copy decides only what the satellite's
OWN jobs resolve — but that is the file whose answer a red or green depends on. Measured 2026-09-13
on every repository's `main`: seven copies at six distinct sizes (core 68,150 B; Crm 69,027;
Reinsurance 69,035; SocialMedia 69,035; Manufacturing 69,037; Plugins 60,365; Education 46,786), and
no guard compared any of them to the canonical. The last time one script was vendored per repo the
six copies drifted to five vintages and each fix landed in one of them (`gen-manifests.py`, #1426).

WHAT IT COMPARES. The CODE, never the prose: both files are parsed, every docstring is dropped, and
the two ASTs are unparsed and compared line by line — so a copy that differs only in comments,
docstrings or blank lines is NOT drift (measured: Crm/Reinsurance/SocialMedia/Manufacturing differ
from the canonical in ~167 raw lines and in 3 string literals of code, none of which changes an
answer). A difference in code is drift, and the report names the top-level functions and classes
that were added, removed or changed, so the reader can tell "lagging copy" from "deliberate fork".

WHEN IT REDS. Advisory first, red later, on a DATE this file carries (`RED_FROM`): a canonical
fetched at `@main` is live on merge for every caller (MeshWeaver#4027), so the day it lands every
repository sees the finding as a `::warning::` on its next run and has until the flip to re-copy —
or to delete its copy and let the lanes resolve. From the flip the same finding is `::error::` and
exit 1. A repository with NO copy passes with a notice: that is the end state, not a gap. A
canonical that cannot be read or parsed is RED — a guard that cannot read its subject must not pass.

WHAT IT DOES NOT TOUCH. The resolver's behaviour — including what an explicit `MW_PLATFORM_REF`
freeze does — is not read, run or changed here; this compares files.

  check-resolver-copy.py --root <repo> --canonical <path to the platform's resolve-platform.py>
  check-resolver-copy.py --self-test
"""
from __future__ import annotations

import argparse
import ast
import difflib
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

COPY_PATH = "scripts/resolve-platform.py"
# The flip: advisory before, red from this instant (UTC). One day after landing (2026-09-13).
RED_FROM = "2026-09-15T00:00:00Z"


def _strip_docstrings(tree: ast.AST) -> ast.AST:
    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            body = getattr(node, "body", None)
            if body and isinstance(body[0], ast.Expr) and isinstance(getattr(body[0], "value", None), ast.Constant) \
                    and isinstance(body[0].value.value, str):
                node.body = body[1:] or [ast.Pass()]
    return tree


def code_of(source: str) -> str:
    """The file's CODE — parsed, docstrings dropped, comments gone by construction, unparsed."""
    return ast.unparse(_strip_docstrings(ast.parse(source)))


def top_level_index(source: str) -> dict[str, str]:
    """name → code of every top-level def/class, for the function-level report."""
    tree = _strip_docstrings(ast.parse(source))
    out: dict[str, str] = {}
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            out[node.name] = ast.unparse(node)
    return out


def compare(copy_src: str, canonical_src: str) -> tuple[int, list[str]]:
    """(changed code lines, function-level notes)."""
    a = code_of(canonical_src).splitlines()
    b = code_of(copy_src).splitlines()
    changed = [l for l in difflib.unified_diff(a, b, lineterm="", n=0)
               if l.startswith(("+", "-")) and not l.startswith(("+++", "---"))]
    notes: list[str] = []
    if changed:
        ia, ib = top_level_index(canonical_src), top_level_index(copy_src)
        for name in sorted(set(ib) - set(ia)):
            notes.append(f"copy ADDS {name}")
        for name in sorted(set(ia) - set(ib)):
            notes.append(f"copy LACKS {name}")
        for name in sorted(set(ia) & set(ib)):
            if ia[name] != ib[name]:
                notes.append(f"{name} differs")
    return len(changed), notes


def now_utc() -> datetime:
    return datetime.now(timezone.utc)


def parse_when(text: str) -> datetime:
    return datetime.fromisoformat(text.replace("Z", "+00:00")).astimezone(timezone.utc)


def run(root: Path, canonical: Path, red_from: datetime, now: datetime) -> int:
    # 🚨 THE CANONICAL IS READ FIRST, BEFORE THE NO-COPY SHORTCUT (Copilot review, #4171). A
    # repository that has deleted its copy — the end state — would otherwise pass while the file it
    # is measured against is missing or unparsable, and the lane's own fetch only greps the body for
    # a marker string. "There is nothing to compare" and "the thing to compare against cannot be
    # read" are different sentences, and only the first is a pass.
    try:
        canonical_src = canonical.read_text(encoding="utf-8")
        code_of(canonical_src)
    except Exception as ex:  # noqa: BLE001 — the reason goes to the log; the verdict is RED
        print(f"::error::the canonical resolve-platform.py at {canonical} cannot be read or parsed "
              f"({type(ex).__name__}: {ex}) — a guard that cannot read its subject must not pass")
        return 1
    copy = root / COPY_PATH
    if not copy.is_file():
        print(f"resolver copy: none at {COPY_PATH} — this repository resolves the platform through the "
              "lanes' fetched canonical; nothing to compare")
        return 0
    try:
        copy_src = copy.read_text(encoding="utf-8")
        code_of(copy_src)
    except Exception as ex:  # noqa: BLE001
        print(f"::error::{COPY_PATH} cannot be read or parsed ({type(ex).__name__}: {ex})")
        return 1
    changed, notes = compare(copy_src, canonical_src)
    raw = sum(1 for l in difflib.unified_diff(canonical_src.splitlines(), copy_src.splitlines(),
                                              lineterm="", n=0)
              if l.startswith(("+", "-")) and not l.startswith(("+++", "---")))
    if changed == 0:
        print(f"resolver copy: {COPY_PATH} is CODE-IDENTICAL to the platform's canonical "
              f"({raw} raw line(s) differ — docstrings, comments or whitespace only)")
        return 0
    red = now >= red_from
    level = "error" if red else "warning"
    phase = ("RED since" if red else "advisory until") + f" {red_from.strftime('%Y-%m-%dT%H:%M:%SZ')}"
    print(f"::{level}::{COPY_PATH} has DRIFTED from the platform's canonical resolve-platform.py: "
          f"{changed} code line(s) differ ({raw} raw), {phase}. Re-copy the canonical "
          f"(.github/scripts/resolve-platform.py in Systemorph/MeshWeaver at this lane's scripts ref), "
          "or delete the copy and let the lanes resolve (MeshWeaver.Plugins#1565). A deliberate "
          "difference belongs in the canonical as an option, never in a fork.")
    for note in notes:
        print(f"  {note}")
    return 1 if red else 0


# ── self-test ───────────────────────────────────────────────────────────────────────────────
CANON = '''"""canonical docstring"""
import sys
LIMIT = 3

def choose(runs):
    """pick the newest sealed run"""
    # a comment
    for r in runs:
        if r.get("sealed"):
            return r
    return None

def main():
    return 0
'''


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, cond: bool, detail: str = "") -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {name}{(' — ' + detail) if detail and not cond else ''}")
        if not cond:
            failures.append(name)

    before = parse_when("2026-09-14T00:00:00Z")
    after = parse_when("2026-09-16T00:00:00Z")
    flip = parse_when(RED_FROM)
    with tempfile.TemporaryDirectory() as raw:
        tmp = Path(raw)
        canonical = tmp / "canonical.py"
        canonical.write_text(CANON, encoding="utf-8")

        def repo(name: str, copy: str | None) -> Path:
            r = tmp / name
            (r / "scripts").mkdir(parents=True)
            if copy is not None:
                (r / COPY_PATH).write_text(copy, encoding="utf-8")
            return r

        check("no copy passes with a notice", run(repo("none", None), canonical, flip, after) == 0)
        check("no copy + an ABSENT canonical is RED (the shortcut does not bypass the subject check)",
              run(repo("none-absent", None), tmp / "not-there.py", flip, after) == 1)
        check("an identical copy passes", run(repo("same", CANON), canonical, flip, after) == 0)
        prose = CANON.replace('"""pick the newest sealed run"""', '"""pick the newest SEALED run, differently worded"""') \
                     .replace("# a comment", "# a different comment\n\n")
        check("a copy differing only in docstrings/comments/blank lines passes (not drift)",
              run(repo("prose", prose), canonical, flip, after) == 0)
        code = CANON.replace('if r.get("sealed"):', 'if r.get("sealed") and r.get("green"):')
        check("a code difference before the flip is advisory (rc 0)",
              run(repo("early", code), canonical, flip, before) == 0)
        check("the same code difference from the flip is RED (rc 1)",
              run(repo("late", code), canonical, flip, after) == 1)
        changed, notes = compare(code, CANON)
        check("the report names the function that differs", "choose differs" in notes, str(notes))
        added = CANON + "\ndef ceiling_for(x):\n    return x\n"
        lacking = CANON.replace("def main():\n    return 0\n", "")
        check("an added function is reported as ADDS", "copy ADDS ceiling_for" in compare(added, CANON)[1])
        check("a missing function is reported as LACKS", "copy LACKS main" in compare(lacking, CANON)[1])
        check("an unparsable canonical is RED whatever the date",
              run(repo("x", CANON), tmp / "missing.py", flip, before) == 1)
        broken = tmp / "broken.py"
        broken.write_text("def (:\n", encoding="utf-8")
        check("a canonical that does not parse is RED", run(repo("y", CANON), broken, flip, before) == 1)
        check("no copy + an UNPARSABLE canonical is RED",
              run(repo("none-broken", None), broken, flip, before) == 1)
        check("an unparsable copy is RED", run(repo("z", "def (:\n"), canonical, flip, before) == 1)
        check("RED_FROM parses as an instant", flip.tzinfo is not None)
    if failures:
        print(f"::error::{len(failures)} self-test case(s) FAILED: " + "; ".join(failures))
        return 1
    print("check-resolver-copy.py self-test: all cases pass")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=".", help="the repository whose scripts/resolve-platform.py is checked")
    p.add_argument("--canonical", help="path to the platform's .github/scripts/resolve-platform.py")
    p.add_argument("--red-from", default=RED_FROM, help="ISO instant from which drift is an error (default: the file's constant)")
    p.add_argument("--now", help="ISO instant to evaluate at (tests); default: now")
    p.add_argument("--self-test", action="store_true")
    args = p.parse_args()
    if args.self_test:
        return self_test()
    if not args.canonical:
        p.error("--canonical is required (or --self-test)")
    return run(Path(args.root), Path(args.canonical), parse_when(args.red_from),
               parse_when(args.now) if args.now else now_utc())


if __name__ == "__main__":
    sys.exit(main())
