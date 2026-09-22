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

A SECOND SUBJECT: `scripts/gen-manifests.py` (MeshWeaver#4777). The precedent this header cites —
six copies, five vintages, each fix landing in one of them — still had no guard when #4775 fixed
three ways `--resolve` reported success over an unanswered question: four copies were re-copied
with it, two (Education, Plugins) could not be and still carry all three, and three days later the
four were already three canonical commits behind again. Same comparison, same function-level
report, ONE difference: this subject is ADVISORY and carries no flip date, on purpose. 🚨 A red on
drift is a red on every canonical change for as long as `node-repo-resolve-locks.yml` REQUIRES the
vendored copy (`[ -f scripts/gen-manifests.py ] || exit 1` — the lane runs the CALLER's copy for
`--resolve` and the platform's only for the post-check), and the canonical moved four times in the
three days after #4775: a dated flip here would red the whole fleet on a timer for a file the fleet
is not allowed to delete. The flip's precondition is therefore a change, not a date: the resolve
lane resolving with the platform's canonical, so a copy becomes optional and the no-copy notice is
the end state — exactly as it is for the resolver. Until then this subject's job is to make "did
the fix reach every copy?" a question every run ANSWERS, in its annotations, instead of one a
person has to remember to ask.

  check-resolver-copy.py --root <repo> --canonical <path to the platform's resolve-platform.py>
  check-resolver-copy.py --subject gen-manifests --root <repo> --canonical <path to the platform's gen-manifests.py>
  check-resolver-copy.py --self-test
"""
from __future__ import annotations

import argparse
import ast
import contextlib
import difflib
import io
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

COPY_PATH = "scripts/resolve-platform.py"
# The flip: advisory before, red from this instant (UTC). One day after landing (2026-09-13).
RED_FROM = "2026-09-15T00:00:00Z"


class Subject:
    """One vendored file this guard compares: where the copy lives, what the canonical is called,
    the remedy the finding names, and whether drift can ever be RED (`red_from` None = advisory
    with no flip scheduled — the header says why for gen-manifests)."""

    def __init__(self, key: str, copy_path: str, canonical_name: str, remedy: str,
                 red_from: str | None, what_it_decides: str) -> None:
        self.key = key
        self.copy_path = copy_path
        self.canonical_name = canonical_name
        self.remedy = remedy
        self.red_from = red_from
        self.what_it_decides = what_it_decides


SUBJECTS: dict[str, Subject] = {
    "resolver": Subject(
        key="resolver",
        copy_path=COPY_PATH,
        canonical_name="resolve-platform.py",
        remedy="Re-copy the canonical (.github/scripts/resolve-platform.py in Systemorph/MeshWeaver at "
               "this lane's scripts ref), or delete the copy and let the lanes resolve "
               "(MeshWeaver.Plugins#1565). A deliberate difference belongs in the canonical as an "
               "option, never in a fork.",
        red_from=RED_FROM,
        what_it_decides="which sealed platform set this repository builds and tests against"),
    "gen-manifests": Subject(
        key="gen-manifests",
        copy_path="scripts/gen-manifests.py",
        canonical_name="gen-manifests.py",
        remedy="Re-copy the canonical (.github/scripts/gen-manifests.py in Systemorph/MeshWeaver at "
               "this lane's scripts ref) — it REQUIRES scripts/gen-manifests.config.json, so a repo "
               "still carrying a module-level SKIP declares that config first, equal to "
               "validate-repos.py's SKIP (MeshWeaver#4777). No flip is scheduled: the resolve lane "
               "still runs THIS copy for --resolve and refuses a repo without one, so drift cannot "
               "be red until the lane resolves with the platform's canonical.",
        red_from=None,
        what_it_decides="every manifest.lock's content hash, the moduleVersion derived from it, and "
                        "whether --resolve reports a merge as resolvable"),
}


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
    """The resolver subject — the original entry point, unchanged in behaviour and wording."""
    return run_subject(SUBJECTS["resolver"], root, canonical, red_from, now)


def run_subject(subject: Subject, root: Path, canonical: Path, red_from: datetime | None,
                now: datetime) -> int:
    copy_path, name = subject.copy_path, subject.canonical_name
    # 🚨 THE CANONICAL IS READ FIRST, BEFORE THE NO-COPY SHORTCUT (Copilot review, #4171). A
    # repository that has deleted its copy — the end state — would otherwise pass while the file it
    # is measured against is missing or unparsable, and the lane's own fetch only greps the body for
    # a marker string. "There is nothing to compare" and "the thing to compare against cannot be
    # read" are different sentences, and only the first is a pass.
    try:
        canonical_src = canonical.read_text(encoding="utf-8")
        code_of(canonical_src)
    except Exception as ex:  # noqa: BLE001 — the reason goes to the log; the verdict is RED
        print(f"::error::the canonical {name} at {canonical} cannot be read or parsed "
              f"({type(ex).__name__}: {ex}) — a guard that cannot read its subject must not pass")
        return 1
    copy = root / copy_path
    if not copy.is_file():
        if subject.key == "resolver":
            print(f"resolver copy: none at {copy_path} — this repository resolves the platform through the "
                  "lanes' fetched canonical; nothing to compare")
        else:
            print(f"{subject.key} copy: none at {copy_path} — nothing to compare")
        return 0
    try:
        copy_src = copy.read_text(encoding="utf-8")
        code_of(copy_src)
    except Exception as ex:  # noqa: BLE001
        print(f"::error::{copy_path} cannot be read or parsed ({type(ex).__name__}: {ex})")
        return 1
    changed, notes = compare(copy_src, canonical_src)
    raw = sum(1 for l in difflib.unified_diff(canonical_src.splitlines(), copy_src.splitlines(),
                                              lineterm="", n=0)
              if l.startswith(("+", "-")) and not l.startswith(("+++", "---")))
    if changed == 0:
        print(f"{subject.key} copy: {copy_path} is CODE-IDENTICAL to the platform's canonical "
              f"({raw} raw line(s) differ — docstrings, comments or whitespace only)")
        return 0
    # 🚨 A subject with no flip is advisory BY DECLARATION (red_from None), never by a date that
    # has not arrived yet: the two print different phases so a reader can tell "not yet" from
    # "not until the precondition in the header".
    red = red_from is not None and now >= red_from
    level = "error" if red else "warning"
    if red_from is None:
        phase = "advisory — no flip is scheduled (see the guard's header for the precondition)"
    else:
        phase = ("RED since" if red else "advisory until") + f" {red_from.strftime('%Y-%m-%dT%H:%M:%SZ')}"
    print(f"::{level}::{copy_path} has DRIFTED from the platform's canonical {name}: "
          f"{changed} code line(s) differ ({raw} raw), {phase}. This copy decides "
          f"{subject.what_it_decides}. {subject.remedy}")
    for note in notes:
        print(f"  {note}")
    return 1 if red else 0


# ── self-test ───────────────────────────────────────────────────────────────────────────────
# A miniature of the canonical gen-manifests.py AFTER #4775: the git read reports failure as None
# and the caller refuses on it.
GM_CANON = '''"""gen-manifests canonical (miniature)"""
import sys

def git(root, args):
    return None

def _unmerged_paths(root):
    out = git(root, ["diff", "--name-only", "--diff-filter=U"])
    if out is None:
        return None
    return out.splitlines()

def resolve(root):
    still = _unmerged_paths(root)
    if still is None:
        return 2
    return 0 if not still else 1

def main():
    return resolve(".")
'''

# The vintage BEFORE #4775, as two repositories still carry it: no `_unmerged_paths`, and a failed
# git read coerced to "" so "nothing left to resolve" is reported over a question never answered.
GM_PRE_4775 = '''"""gen-manifests (older vintage)"""
import sys

def git(root, args):
    return None

def resolve(root):
    still = (git(root, ["diff", "--name-only", "--diff-filter=U"]) or "").strip()
    return 0 if not still else 1

def main():
    return resolve(".")
'''

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

        # ── the second subject: gen-manifests, advisory by declaration ─────────────────────
        gm = SUBJECTS["gen-manifests"]
        gm_canonical = tmp / "gen-manifests.canonical.py"
        gm_canonical.write_text(GM_CANON, encoding="utf-8")

        def gm_repo(name: str, copy: str | None) -> Path:
            r = tmp / name
            (r / "scripts").mkdir(parents=True)
            if copy is not None:
                (r / gm.copy_path).write_text(copy, encoding="utf-8")
            return r

        def captured(subject: Subject, root: Path, canon: Path, now: datetime) -> tuple[int, str]:
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                rc = run_subject(subject, root, canon, parse_when(subject.red_from) if subject.red_from else None, now)
            return rc, buf.getvalue()

        check("gen-manifests: the subject declares NO flip (red_from is None, not a date)", gm.red_from is None)
        check("gen-manifests: no copy passes with a notice",
              captured(gm, gm_repo("gm-none", None), gm_canonical, after)[0] == 0)
        check("gen-manifests: no copy + an ABSENT canonical is still RED (advisory never bypasses the subject check)",
              captured(gm, gm_repo("gm-none-absent", None), tmp / "gm-not-there.py", after)[0] == 1)
        check("gen-manifests: an identical copy passes",
              captured(gm, gm_repo("gm-same", GM_CANON), gm_canonical, after)[0] == 0)
        # 🚨 PRODUCTION'S ACTUAL STATE, not a normalised fixture (MeshWeaver#4777): a copy of the
        # vintage BEFORE #4775 — it LACKS `_unmerged_paths` and still coerces a failed git read to
        # "" — measured on two repositories' main. The guard must NAME both, and it must not red.
        rc, out = captured(gm, gm_repo("gm-education-vintage", GM_PRE_4775), gm_canonical, after)
        check("gen-manifests: a pre-#4775 vintage is reported, ADVISORY, long after any resolver flip (rc 0)", rc == 0, out)
        check("gen-manifests: the finding is a ::warning::, never ::error::", "::warning::" in out and "::error::" not in out, out)
        check("gen-manifests: the phase says no flip is scheduled, not 'advisory until <date>'",
              "no flip is scheduled" in out and "advisory until" not in out, out)
        check("gen-manifests: the report names the function the vintage LACKS", "copy LACKS _unmerged_paths" in out, out)
        check("gen-manifests: the report names the function whose code differs", "resolve differs" in out, out)
        check("gen-manifests: the remedy names the config the canonical requires", "gen-manifests.config.json" in out, out)
        check("gen-manifests: a canonical that does not parse is RED whatever the subject",
              captured(gm, gm_repo("gm-y", GM_CANON), broken, after)[0] == 1)
        check("gen-manifests: an unparsable copy is RED",
              captured(gm, gm_repo("gm-z", "def (:\n"), gm_canonical, after)[0] == 1)
        # And the resolver subject is untouched by the generalisation: same code drift, same flip.
        check("resolver: a code difference from the flip is still RED after the generalisation",
              captured(SUBJECTS["resolver"], repo("late-2", code), canonical, after)[0] == 1)
    if failures:
        print(f"::error::{len(failures)} self-test case(s) FAILED: " + "; ".join(failures))
        return 1
    print("check-resolver-copy.py self-test: all cases pass")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=".", help="the repository whose vendored copy is checked")
    p.add_argument("--canonical", help="path to the platform's canonical copy of the subject")
    p.add_argument("--subject", choices=sorted(SUBJECTS), default="resolver",
                   help="which vendored file to compare (default: the resolver)")
    p.add_argument("--red-from", default=None,
                   help="ISO instant from which drift is an error; resolver only (default: the file's "
                        "constant). Refused for a subject that declares no flip.")
    p.add_argument("--now", help="ISO instant to evaluate at (tests); default: now")
    p.add_argument("--self-test", action="store_true")
    args = p.parse_args()
    if args.self_test:
        return self_test()
    if not args.canonical:
        p.error("--canonical is required (or --self-test)")
    subject = SUBJECTS[args.subject]
    if subject.red_from is None:
        if args.red_from:
            # 🚨 Not a knob. The header says why this subject has no flip; the flip is a change to
            # the resolve lane, and a workflow argument must not be able to schedule it by accident.
            p.error(f"--red-from is refused for --subject {subject.key}: it is advisory by declaration")
        red_from = None
    else:
        red_from = parse_when(args.red_from or subject.red_from)
    return run_subject(subject, Path(args.root), Path(args.canonical), red_from,
                       parse_when(args.now) if args.now else now_utc())


if __name__ == "__main__":
    sys.exit(main())
