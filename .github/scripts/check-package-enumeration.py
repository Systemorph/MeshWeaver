#!/usr/bin/env python3
"""check-package-enumeration.py — a node repo's two package enumerators return the SAME packages.

WHY THIS EXISTS (MeshWeaver#4774)
═════════════════════════════════
A node repo answers "which top-level directories are packages?" TWICE:

  * the platform's canonical `gen-manifests.py` (`plugin_dirs`) — what gets a manifest.lock, a
    version and a bundle. The lane fetches it at its scripts ref; it is the VERDICT.
  * the caller's own `scripts/validate-repos.py` — what gets walked for node JSON.

A directory in one and not the other is either validated as nodes it does not contain, or has a
manifest.lock demanded for a package it is not. Each satellite guarded that with its own
`check-skip-sets.py`, which compared the two DECLARED skip sets — and `plugin_dirs` applies a rule
that is in no declaration: a top-level DOT-directory is never a package. Measured on the satellites'
`main`: five `validate-repos.py` enumerators had no dot rule, so an undeclared `.foo` (tooling
writes `.example-check/`, `.platform-scripts/`, `.agents/`) was skipped by the canonical and walked
by the validator while the declared sets were equal and the guard was green. MeshWeaver.Plugins
also carried `dist` in both of its SKIP sets and NOT in the config the canonical reads — invisible
to a guard comparing the two SKIPs, because the verdict enumerator reads neither.

So this compares what the two enumerators RETURN, never what they declare:

  1. ON THE CALLER'S TREE — both enumerations of the checkout, name for name.
  2. ON A FIXTURE — an empty tree holding every name either side declares, every top-level
     directory the checkout has, and a probe dot-directory and probe package. A disagreement that
     needs a directory the checkout happens not to contain today (the dot rule, a skip name one
     side forgot) is latent on the real tree and shows up here, which is the whole point: the next
     implicit rule cannot hide by being unexercised.

THE CONTRACT with the caller: `scripts/validate-repos.py` exposes `package_dirs(root) -> list[Path]`,
a pure function of the top-level directory NAMES under `root`, and its own `main()` enumerates
through it. That is the one thing the guard can call without knowing how each repo spells its walk
(a comprehension in five, `is_course_dir` in Education).

🚨 A caller that does not expose it YET is NAMED — `NOT compared` on a `::warning::` line — and does
not fail. Adoption is one PR per satellite; red-on-absence before they land would red every
satellite's required `validate` context for a condition none of them can fix without that PR (the
fleet-wide-red shape). The flip to red is a change to THIS file once every caller exposes the
function, never a date. Everything else is RED: a missing or unloadable validate-repos.py, a
`package_dirs` that raises or returns a non-list, a `main()` that does not call `package_dirs` or
also walks the tree itself (`iterdir()`), an unloadable canonical, and any disagreement.

No network, no credential: two files and directory listings, so it runs on fork PRs too.

USAGE
    check-package-enumeration.py --root <caller checkout> [--canonical <gen-manifests.py>]
    check-package-enumeration.py --self-test [--canonical <gen-manifests.py>]
"""
from __future__ import annotations

import argparse
import ast
import importlib.util
import shutil
import sys
import tempfile
from pathlib import Path

# Probe names for the fixture. The dot-directory is the rule this guard was written for; the
# plain name proves the fixture enumerates SOMETHING, so an enumerator that returns nothing for
# every tree cannot agree with an equally empty canonical by accident.
PROBE_DOT = ".mw-enumeration-probe"
PROBE_PACKAGE = "MwEnumerationProbe"


class Refusal(Exception):
    """A reason the comparison could not be made. Always RED — never read as agreement."""


def load_module(path: Path, name: str):
    if not path.is_file():
        raise Refusal(f"{path} does not exist")
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise Refusal(f"{path} cannot be loaded as a module")
    module = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(module)
    except SystemExit as ex:                      # a script that exits at import time
        raise Refusal(f"{path} exited while being imported ({ex.code})") from None
    except Exception as ex:                       # noqa: BLE001 — name it, never swallow it
        raise Refusal(f"{path} raised while being imported: {type(ex).__name__}: {ex}") from None
    return module


def names_of(fn, root: Path, who: str) -> list[str]:
    try:
        dirs = fn(root)
    except SystemExit as ex:
        raise Refusal(f"{who}({root}) exited ({ex.code})") from None
    except Exception as ex:                       # noqa: BLE001
        raise Refusal(f"{who}({root}) raised {type(ex).__name__}: {ex}") from None
    if not isinstance(dirs, list):
        raise Refusal(f"{who} must return a list of directories, got {type(dirs).__name__}")
    return sorted(Path(d).name for d in dirs)


def main_delegates(path: Path) -> str | None:
    """None when validate-repos.py's main() enumerates THROUGH package_dirs, else why not.

    🚨 Calling package_dirs proves only that the helper agrees with the canonical. The gate walks
    what main() walks, so a helper added beside an untouched `root.iterdir()` comprehension would
    pass this guard while the effective validator still disagreed. Static on purpose: main() runs
    the whole gate and cannot be called here."""
    try:
        tree = ast.parse(path.read_text(encoding="utf-8"))
    except (OSError, SyntaxError) as ex:
        return f"{path.name} cannot be parsed: {ex}"
    mains = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "main"]
    if len(mains) != 1:
        return f"{path.name} defines {len(mains)} top-level main() — expected exactly one"
    calls = [n for n in ast.walk(mains[0]) if isinstance(n, ast.Call)]
    if not any(isinstance(c.func, ast.Name) and c.func.id == "package_dirs" for c in calls):
        return f"{path.name}'s main() never calls package_dirs(root)"
    private = [c.lineno for c in calls
               if isinstance(c.func, ast.Attribute) and c.func.attr == "iterdir"]
    if private:
        return (f"{path.name}'s main() also walks the tree itself (iterdir() at line(s) "
                f"{', '.join(map(str, private))}) beside package_dirs")
    return None


def diff(where: str, validator: list[str], canonical: list[str], say) -> bool:
    only_v = sorted(set(validator) - set(canonical))
    only_c = sorted(set(canonical) - set(validator))
    if not only_v and not only_c:
        return True
    say(f"::error::the two package enumerators disagree {where}")
    if only_v:
        say(f"  walked by validate-repos.py, NOT a package to gen-manifests.py: {', '.join(only_v)}")
        say("    -> validate-repos.py validates these as node packages; the canonical gives them no "
            "manifest.lock, no version and no bundle")
    if only_c:
        say(f"  a package to gen-manifests.py, NOT walked by validate-repos.py: {', '.join(only_c)}")
        say("    -> the canonical demands a manifest.lock for these; validate-repos.py never "
            "checks their nodes")
    return False


def check(root: Path, canonical_path: Path, say) -> int:
    try:
        canonical = load_module(canonical_path, "_mw_canonical_gen_manifests")
        if not callable(getattr(canonical, "plugin_dirs", None)) or \
                not callable(getattr(canonical, "skip_set", None)):
            raise Refusal(f"{canonical_path} has no plugin_dirs/skip_set — not the manifest generator")
        validator = load_module(root / "scripts" / "validate-repos.py", "_mw_caller_validate_repos")
    except Refusal as r:
        say(f"::error::package enumeration cannot be compared: {r}")
        return 1

    package_dirs = getattr(validator, "package_dirs", None)
    if not callable(package_dirs):
        say("::warning::package enumeration NOT compared: scripts/validate-repos.py exposes no "
            "package_dirs(root), so this repo's second enumerator cannot be called "
            "(MeshWeaver#4774). Adopt: define package_dirs(root) — SKIP plus the dot-directory rule "
            "— and enumerate through it in main().")
        return 0

    delegation = main_delegates(root / "scripts" / "validate-repos.py")
    if delegation:
        say(f"::error::package enumeration cannot be trusted: {delegation}")
        say("  package_dirs is only the contract if the gate's own walk goes through it — route "
            "main()'s enumeration through package_dirs(root) and drop the private iterdir().")
        return 1

    try:
        tree_v = names_of(package_dirs, root, "validate-repos.package_dirs")
        tree_c = names_of(canonical.plugin_dirs, root, "gen-manifests.plugin_dirs")
        declared = set(canonical.skip_set(root)) | set(getattr(validator, "SKIP", set()) or set())
    except Refusal as r:
        say(f"::error::package enumeration cannot be compared: {r}")
        return 1
    except SystemExit as ex:                       # the canonical refuses a missing/garbled config
        say(f"::error::package enumeration cannot be compared: gen-manifests.py refused ({ex.code})")
        return 1

    ok = diff("on this checkout", tree_v, tree_c, say)

    top = {d.name for d in root.iterdir() if d.is_dir()}
    names = sorted(top | declared | {PROBE_DOT, PROBE_PACKAGE, "scripts"})
    with tempfile.TemporaryDirectory() as d:
        fx = Path(d)
        for n in names:
            (fx / n).mkdir(parents=True, exist_ok=True)
        # The canonical reads its declared policy from the fixture's own scripts/, exactly as it
        # reads the caller's — so the fixture answers under the SAME config, not a guessed one.
        shutil.copyfile(canonical.config_path(root), fx / "scripts" / canonical.CONFIG_NAME)
        try:
            fx_v = names_of(package_dirs, fx, "validate-repos.package_dirs")
            fx_c = names_of(canonical.plugin_dirs, fx, "gen-manifests.plugin_dirs")
        except Refusal as r:
            say(f"::error::package enumeration cannot be compared on the fixture: {r}")
            return 1
    ok = diff(f"on a fixture of {len(names)} top-level name(s) (every declared skip, every "
              f"directory in this checkout, {PROBE_DOT}/ and {PROBE_PACKAGE}/)",
              fx_v, fx_c, say) and ok
    if PROBE_PACKAGE not in fx_c:
        say(f"::error::the canonical did not enumerate the probe package {PROBE_PACKAGE}/ — the "
            "fixture proved nothing, so agreement on it is not evidence")
        ok = False

    if not ok:
        say("  Fix the enumerator that is wrong — usually validate-repos.py's package_dirs (the "
            "dot-directory rule, or a skip name) or scripts/gen-manifests.config.json's skip list. "
            "Both must return the same packages for every tree.")
        return 1
    say(f"✓ both package enumerators return the same {len(tree_c)} package(s) on this checkout "
        f"and the same {len(fx_c)} on a {len(names)}-name fixture (dot-directory probe included)")
    return 0


VALIDATE_AGREEING = '''
from pathlib import Path
SKIP = {"scripts", "src"}
def package_dirs(root):
    return sorted((d for d in Path(root).iterdir()
                   if d.is_dir() and d.name not in SKIP and not d.name.startswith(".")),
                  key=lambda d: d.name)
'''
MAIN_OK = '''
def main():
    return len(package_dirs(Path(".")))
'''
VALIDATE_AGREEING += MAIN_OK


def self_test(canonical_path: Path) -> int:
    failures: list[str] = []

    def run(files: dict[str, str], dirs: list[str], config: str | None) -> tuple[int, str]:
        lines: list[str] = []
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            (root / "scripts").mkdir()
            for n in dirs:
                (root / n).mkdir(parents=True, exist_ok=True)
            for rel, text in files.items():
                (root / rel).write_text(text, encoding="utf-8")
            if config is not None:
                (root / "scripts" / "gen-manifests.config.json").write_text(config, encoding="utf-8")
            rc = check(root, canonical_path, lines.append)
        return rc, "\n".join(lines)

    cfg = '{"skip": ["scripts", "src"], "hashModuleSources": false}\n'
    vr = "scripts/validate-repos.py"

    def case(name: str, want_rc: int, got: tuple[int, str], must_say: str | None = None) -> None:
        rc, out = got
        good = rc == want_rc and (must_say is None or must_say in out)
        print(f"  {'ok  ' if good else 'FAIL'} {name}")
        if not good:
            failures.append(f"{name}: exit {rc} (want {want_rc})\n{out}")

    case("agreeing enumerators pass", 0,
         run({vr: VALIDATE_AGREEING}, ["Alpha", "Beta", "src"], cfg), "same 2 package(s)")
    # The #4774 shape exactly: equal declared sets, no dot rule, and NO dot-directory on the tree —
    # the real-tree comparison agrees and only the fixture can see it.
    case("a validator without the dot rule FAILS on the fixture even with no dot-dir on the tree", 1,
         run({vr: VALIDATE_AGREEING.replace(' and not d.name.startswith(".")', "")},
             ["Alpha", "src"], cfg), PROBE_DOT)
    case("a validator without the dot rule FAILS on a tree that has one", 1,
         run({vr: VALIDATE_AGREEING.replace(' and not d.name.startswith(".")', "")},
             ["Alpha", ".agents"], cfg), ".agents")
    # The Plugins `dist` shape: the validator skips a name the canonical's config does not.
    case("a skip name only the validator carries FAILS although the tree lacks it", 1,
         run({vr: VALIDATE_AGREEING.replace('{"scripts", "src"}', '{"scripts", "src", "dist"}')},
             ["Alpha"], cfg), "dist")
    case("a skip name only the config carries FAILS", 1,
         run({vr: VALIDATE_AGREEING},
             ["Alpha"], '{"skip": ["scripts", "src", "legacy"], "hashModuleSources": false}\n'),
         "legacy")
    case("a caller with no package_dirs is NAMED, not failed", 0,
         run({vr: 'SKIP = {"scripts"}\n'}, ["Alpha"], cfg), "NOT compared")
    case("a missing validate-repos.py FAILS", 1, run({}, ["Alpha"], cfg), "does not exist")
    case("a package_dirs that raises FAILS", 1,
         run({vr: "def package_dirs(root):\n    raise RuntimeError('boom')\n" + MAIN_OK},
             ["Alpha"], cfg),
         "boom")
    case("a package_dirs that returns a non-list FAILS", 1,
         run({vr: "def package_dirs(root):\n    return None\n" + MAIN_OK}, ["Alpha"], cfg),
         "NoneType")
    case("a package_dirs that returns a TUPLE FAILS (the contract is a list)", 1,
         run({vr: "def package_dirs(root):\n    return ()\n" + MAIN_OK}, ["Alpha"], cfg),
         "tuple")
    case("a main() that never calls package_dirs FAILS", 1,
         run({vr: VALIDATE_AGREEING.replace(MAIN_OK, "\ndef main():\n    return 0\n")},
             ["Alpha"], cfg), "never calls package_dirs")
    case("a main() that walks the tree itself beside package_dirs FAILS", 1,
         run({vr: VALIDATE_AGREEING.replace(
             MAIN_OK, "\ndef main():\n    package_dirs(Path('.'))\n"
                      "    return [d for d in Path('.').iterdir()]\n")},
             ["Alpha"], cfg), "walks the tree itself")
    case("a missing gen-manifests.config.json FAILS (the canonical never guesses)", 1,
         run({vr: VALIDATE_AGREEING}, ["Alpha"], None), "not found")
    # The canonical input is itself a precondition: an unreadable one is red, never "agree".
    lines: list[str] = []
    with tempfile.TemporaryDirectory() as d:
        (Path(d) / "scripts").mkdir()
        (Path(d) / "scripts" / "validate-repos.py").write_text(VALIDATE_AGREEING, encoding="utf-8")
        rc = check(Path(d), Path(d) / "absent-gen-manifests.py", lines.append)
    case("a missing canonical FAILS", 1, (rc, "\n".join(lines)), "does not exist")

    if failures:
        print(f"\n✗ check-package-enumeration self-test: {len(failures)} failure(s)")
        for f in failures:
            print(f"--- {f}")
        return 1
    print("\n✓ check-package-enumeration self-test: 14/14 — both directions of a disagreement are "
          "red on the tree AND on the fixture, main() must walk THROUGH package_dirs, the dot rule is caught with no dot-directory "
          "present, and every unreadable input is red rather than agreement")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    p.add_argument("--root", type=Path, help="the caller's checkout")
    p.add_argument("--canonical", type=Path,
                   default=Path(__file__).resolve().parent / "gen-manifests.py",
                   help="the platform's gen-manifests.py (default: beside this script)")
    p.add_argument("--self-test", action="store_true")
    a = p.parse_args()
    if a.self_test:
        return self_test(a.canonical.resolve())
    if a.root is None:
        p.error("--root is required")
    return check(a.root.resolve(), a.canonical.resolve(), print)


if __name__ == "__main__":
    sys.exit(main())
