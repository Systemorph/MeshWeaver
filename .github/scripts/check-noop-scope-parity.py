#!/usr/bin/env python3
"""check-noop-scope-parity.py — a node repo's own no-op set agrees with the platform's.

WHY THIS EXISTS
═══════════════
`node-repo-scope.py` decides which module bundles a pull request rebuilds and which module suites
it runs. Half of that decision reads a set the CALLER repo owns — `NOOP_DIRS` in its
`scripts/affected-modules.py`, the top-level directories a change in which reaches no build. The
platform carries its own copy, and the two are HAND-WRITTEN in two repositories.

That copy drifts, and until 2026-09-07 the drift was invisible from inside the repo paying for it:

  * MeshWeaver.SocialMedia run 34122662676 —
        scope: full — this script's NOOP_DIRS has drifted from scripts/affected-modules.py's —
        only theirs: (none); only ours: app.
    …on EVERY pull request since `app` was added to the platform's set. The lane narrowed nothing,
    for months, and the only trace was one line in the log of the job it disabled.
  * MeshWeaver.Manufacturing and MeshWeaver.Reinsurance carry the same divergence today.

`node-repo-scope.py` no longer answers a divergence with a blanket full run — it narrows on the
INTERSECTION, so a divergent dir is over-built rather than the whole run being. That makes the
drift SAFE. It does not make it FREE, and it does not cover the shape that is not a divergence at
all: a caller whose `NOOP_DIRS` literal has been RENAMED, reformatted or moved, which the platform
reads as "cannot verify" and answers — correctly, and permanently — with the full set.

So this guard asserts the two things the scope script cannot assert about itself:

  1. THE SUBJECT IS STILL THERE. A caller that ships `scripts/affected-modules.py` must expose a
     single-line `NOOP_DIRS = {…}` literal. If it does not, narrowing is OFF in that repo and
     nothing anywhere goes red — the "a guard whose subject moved while its roots did not" shape
     this fleet keeps paying for.
  2. THE DIVERGENCE COSTS NOTHING. Every top-level directory THE CALLER'S TREE ACTUALLY HAS must
     be classified identically by both sets. A name only one side carries for a directory that
     does not exist cannot classify a changed file and is therefore not a finding — which is why
     SocialMedia's missing `app` is reported as harmless rather than as a red.

It reads only the two files and the caller's top-level directory listing: no network, no
credential, no secret, so it runs on fork pull requests exactly as it does anywhere else.

🚨 IT DOES NOT RE-DECLARE EITHER SET. The platform's `NOOP_DIRS`/`NOOP_FILES` and the parser for
the caller's are IMPORTED from `node-repo-scope.py` — a third hand-copy is the defect this guard
exists to find.

USAGE
    check-noop-scope-parity.py --root <caller checkout> [--platform-scope <node-repo-scope.py>]
    check-noop-scope-parity.py --self-test
"""
from __future__ import annotations

import argparse
import importlib.util
import sys
from pathlib import Path


def load_scope(path: Path):
    """The platform's node-repo-scope module, or a hard exit naming what is missing.

    🚨 NO FALLBACK. "The platform's set could not be loaded" and "the sets agree" must never look
    alike: without the module there is nothing to compare against, and a guard that passes on a
    missing input is the trapdoor this file's docstring is about.
    """
    if not path.is_file():
        sys.exit(f"✗ cannot load the platform's no-op sets: {path} does not exist. This guard "
                 "compares the caller's scripts/affected-modules.py against node-repo-scope.py, "
                 "so fetch BOTH at the same platform-ref and pass --platform-scope.")
    spec = importlib.util.spec_from_file_location("_mw_node_repo_scope", path)
    if spec is None or spec.loader is None:
        sys.exit(f"✗ {path} could not be loaded as a Python module.")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    for attr in ("NOOP_DIRS", "NOOP_FILES", "SELECTOR", "caller_noop_dirs"):
        if not hasattr(module, attr):
            sys.exit(f"✗ {path} carries no `{attr}` — this guard imports the platform's sets "
                     "rather than re-declaring them, and the name it imports has moved. Update "
                     "this guard in the same change that moved it.")
    return module


def check(root: Path, scope, say) -> int:
    selector = root / scope.SELECTOR
    say(f"platform NOOP_DIRS  : {', '.join(sorted(scope.NOOP_DIRS))}")
    say(f"platform NOOP_FILES : {', '.join(sorted(scope.NOOP_FILES))}")

    if not selector.is_file():
        # Not a finding: a repo without the selector cannot narrow at all, and node-repo-scope.py
        # already answers that with a FULL run and a named reason on every event. Say so with the
        # cost attached so nobody reads the green tick as "this repo narrows".
        say(f"\nthis repo ships no {scope.SELECTOR}, so its module lane CANNOT narrow: every "
            "event packs and tests the full matrix, by design and out loud. Nothing to compare.")
        return 0

    theirs, unreadable = scope.caller_noop_dirs(root)
    if theirs is None:
        print(f"::error title=The no-op set this repo owns cannot be read::{unreadable}. "
              f"{scope.SELECTOR} must expose a SINGLE-LINE `NOOP_DIRS = {{…}}` literal: the "
              "platform parses it out of the source (importing it would run its argparse), and "
              "when the parse fails node-repo-scope.py answers 'cannot verify' with a FULL run on "
              "every pull request — silently, forever, visible only inside the job it disables. "
              "Put the literal back on one line, or move this guard in the same change that "
              "moved it.")
        return 1

    say(f"this repo's NOOP_DIRS: {', '.join(sorted(theirs))}")

    # 🚨 SCOPED TO WHAT THE TREE ACTUALLY HAS. A name only one side carries, for a directory this
    # repo does not have, can never classify a changed file — reporting it would be a red about
    # nothing, and a gate that reds about nothing is one people learn to ignore.
    present = {d.name for d in root.iterdir() if d.is_dir()}
    only_theirs = sorted((theirs - scope.NOOP_DIRS) & present)
    only_ours = sorted((scope.NOOP_DIRS - theirs) & present)
    dormant = sorted((theirs ^ scope.NOOP_DIRS) - present)
    if dormant:
        say(f"\ndivergent but DORMANT (no such directory here, so nothing can be classified by "
            f"it): {', '.join(dormant)}")

    if not only_theirs and not only_ours:
        say(f"\n✓ the two sets agree about every top-level directory this repo has "
            f"({len(present)} checked).")
        return 0

    lines = []
    if only_ours:
        lines.append(f"the platform calls {', '.join(only_ours)} inert and this repo does not")
    if only_theirs:
        lines.append(f"this repo calls {', '.join(only_theirs)} inert and the platform does not")
    print(f"::error title=This repo's no-op set diverges from the platform's::"
          f"{'; '.join(lines)}. Every directory named there EXISTS here, so each one is scoped "
          f"OUT of the narrowing intersection and rebuilt on every pull request that touches it. "
          f"Nothing is under-built — the intersection is safe in both directions — this is runner "
          f"time you are paying for. Fix: make {scope.SELECTOR}'s single-line NOOP_DIRS literal "
          f"equal to the platform's, listed above.")
    return 1


# ── self-test ────────────────────────────────────────────────────────────────────────────────
# 🚨 A GUARD OWES ITS OWN PROOF THAT IT CAN SAY NO. Every branch above is exercised against a
# fixture, INCLUDING the two that must stay green — a guard that reds on everything is as useless
# as one that reds on nothing.

def self_test(scope_path: Path) -> int:
    import io
    import tempfile
    from contextlib import redirect_stdout

    scope = load_scope(scope_path)
    failures: list[str] = []
    ran = 0

    def case(name: str, ok: bool, detail: str = "") -> None:
        nonlocal ran
        ran += 1
        print(f"  {'✓' if ok else '✗'} {name}{'' if ok else ': ' + detail}")
        if not ok:
            failures.append(name)

    def run(root: Path) -> tuple[int, str]:
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = check(root, scope, lambda *a: print(*a))
        return rc, buf.getvalue()

    literal = "NOOP_DIRS = {%s}\n" % ", ".join(f'"{d}"' for d in sorted(scope.NOOP_DIRS))

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "scripts").mkdir()
        selector = root / scope.SELECTOR
        for d in sorted(scope.NOOP_DIRS):
            (root / d).mkdir(exist_ok=True)
        (root / "Alpha").mkdir()

        selector.write_text(literal + "print('stub')\n", encoding="utf-8")
        rc, out = run(root)
        case("identical sets ⇒ green, stating how many dirs were checked",
             rc == 0 and "agree about every top-level directory" in out, f"rc={rc} {out[-120:]}")

        # ── the finding that costs runner time: a divergence over a dir that EXISTS ──
        short = {d for d in scope.NOOP_DIRS if d != "docs"}
        selector.write_text("NOOP_DIRS = {%s}\nprint('stub')\n"
                            % ", ".join(f'"{d}"' for d in sorted(short)), encoding="utf-8")
        rc, out = run(root)
        case("a dir the platform calls inert and this repo does not ⇒ RED, naming it",
             rc == 1 and "docs" in out, f"rc={rc} {out[-160:]}")

        selector.write_text('NOOP_DIRS = {%s, "Alpha"}\nprint("stub")\n'
                            % ", ".join(f'"{d}"' for d in sorted(scope.NOOP_DIRS)),
                            encoding="utf-8")
        rc, out = run(root)
        case("…and the OTHER direction (this repo calls a real dir inert) ⇒ RED too",
             rc == 1 and "Alpha" in out, f"rc={rc} {out[-160:]}")

        # ── the divergence that costs NOTHING: SocialMedia's `app`, a dir it does not have ──
        no_such = root / "nosuch-dormant"
        selector.write_text('NOOP_DIRS = {%s, "%s"}\nprint("stub")\n'
                            % (", ".join(f'"{d}"' for d in sorted(scope.NOOP_DIRS)),
                               no_such.name), encoding="utf-8")
        rc, out = run(root)
        case("a divergence over a dir this repo DOES NOT HAVE is reported dormant, not red",
             rc == 0 and "DORMANT" in out and no_such.name in out, f"rc={rc} {out[-160:]}")

        # ── the subject moving: the literal is no longer parseable ──
        selector.write_text(literal.replace("NOOP_DIRS", "NOOP_TOPS"), encoding="utf-8")
        rc, out = run(root)
        case("a RENAMED NOOP_DIRS literal ⇒ RED (narrowing is off and nothing else says so)",
             rc == 1 and "SINGLE-LINE" in out, f"rc={rc} {out[-160:]}")
        selector.write_text("NOOP_DIRS = {\n  'legacy',\n}\nprint('stub')\n", encoding="utf-8")
        rc, out = run(root)
        case("…and a literal reformatted onto several lines is a divergence, not a parse failure",
             rc == 1, f"rc={rc} {out[-160:]}")

        # ── a repo with no selector: not a finding, but it must SAY it cannot narrow ──
        selector.unlink()
        rc, out = run(root)
        case("no affected-modules.py ⇒ green, and says the lane CANNOT narrow",
             rc == 0 and "CANNOT narrow" in out, f"rc={rc} {out[-160:]}")

    if failures:
        print(f"\n::error title=check-noop-scope-parity self-test failed::{len(failures)} case(s).")
        return 1
    print(f"\n✓ check-noop-scope-parity self-test: {ran} cases green — the two REAL divergences "
          "(one per direction), the dormant one that must NOT red, the renamed literal, and the "
          "two green shapes.")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    p.add_argument("--root", default=".", help="the caller repo checkout")
    p.add_argument("--platform-scope", default="", dest="platform_scope",
                   help="the platform's node-repo-scope.py (default: beside this script)")
    p.add_argument("--self-test", action="store_true", dest="self_test")
    args = p.parse_args()

    scope_path = (Path(args.platform_scope) if args.platform_scope
                  else Path(__file__).resolve().parent / "node-repo-scope.py")
    if args.self_test:
        return self_test(scope_path)
    return check(Path(args.root).resolve(), load_scope(scope_path), print)


if __name__ == "__main__":
    sys.exit(main())
