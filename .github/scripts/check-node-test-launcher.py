#!/usr/bin/env python3
"""check-node-test-launcher.py — a node repo's `scripts/run-node-tests.py` must LAUNCH the platform's
canonical harness, never carry a copy of it (MeshWeaver#4785).

WHY. `run-node-tests.py` is the local loop every node repo's AGENTS.md tells an author to run before
pushing, and it imports `compile-check.py` — a canonical whose surface moves. For a year it existed
only as vendored copies, three of them, with nothing in core to compare them against. When core
`ce104c872e` replaced the compile model and removed `usings_union`, all three broke at once and
DIFFERENTLY, and not one CI lane in the fleet went red:

  * MeshWeaver.Reinsurance — `AttributeError: module 'compile_check' has no attribute
    'usings_union'`, raised before a single test ran, while its own `--self-test` printed
    `✓ self-test green` (it exercised only the collision detector);
  * MeshWeaver.Crm — patched locally with a re-derived global-usings union, so it kept RUNNING
    against a compile model the gate had abandoned: 163 tests passing under a shaping the mesh
    does not use;
  * MeshWeaver.Plugins — kept working only because it loads a VENDORED `compile-check.py` fork that
    still carried the removed helper.

That is the `gen-manifests.py` shape (#1426, five vintages) and the `resolve-platform.py` shape
(#4787, 70 re-copy commits across six repos). The canonical now lives in
`.github/scripts/run-node-tests.py`; a satellite keeps a LAUNCHER that fetches it at the same ref the
repo's compile-check lane is pinned to, so the harness and the gate are one vintage by construction.

WHAT IT ASSERTS. A launcher is defined by what it does NOT contain. Given a repo root:

  * NO copy is a PASS with a notice — that is a valid end state (the repo invokes
    `scripts/platform-script.py run-node-tests.py` directly).
  * a file that is present must NAME the canonical (`run-node-tests.py`), or it launches nothing;
  * it must define none of the harness's own functions (`HARNESS_DEFS`) — that is a re-vendor;
  * it must carry no template literal (a `CSPROJ`/`RUNNER` block is thousands of characters) —
    the tell that survives a rename of every function;
  * it must not exceed `MAX_DEFS` top-level definitions;
  * a file that cannot be read or parsed is RED. A guard that cannot read its subject must not pass.

WHAT IT DOES NOT DO. It never fetches, runs or compares BEHAVIOUR — `run-node-tests.py --self-test`
is what proves the harness reproduces the gate, and core's own CI runs it on every platform PR. This
one only refuses the copy coming back.

  check-node-test-launcher.py --root <repo>
  check-node-test-launcher.py --self-test
"""
from __future__ import annotations

import argparse
import ast
import sys
import tempfile
from pathlib import Path

CANONICAL = "run-node-tests.py"
# 🚨 EVERY copy, WHEREVER IT SITS — never one hard-coded path. The three vendored copies did not
# agree on a location: MeshWeaver.Crm and MeshWeaver.Reinsurance kept theirs at `scripts/`,
# MeshWeaver.Plugins at `devtools/` (a declared no-op dir). A guard that looked only under `scripts/`
# would have reported "no copy — nothing to check" for the repo whose copy was the one still running
# against the abandoned model, which is a FALSE PASS wearing a clean notice. So the subject is
# discovered, and moving the file cannot evade it.
SEARCH_EXCLUDED = {".git", ".platform-scripts", ".worktrees", "node_modules", "bin", "obj",
                   "__pycache__", ".cache", ".venv"}

# The harness's own names. A launcher that defines any of these is not a launcher.
HARNESS_DEFS = {
    "run_set", "unit_for", "compile_items", "collisions_within", "declarations",
    "discover_module_nodetypes", "usings_union", "build_unit", "sibling_plugin_refs",
    "find_generator", "shared_sources", "hoist_self_test",
}
# A csproj or C# runner template. The shortest plausible one is far longer than this; the longest
# plausible launcher literal is a multi-line refusal message, which is far shorter.
MAX_LITERAL = 900
MAX_DEFS = 4


def _strip_docstrings(tree: ast.AST) -> ast.AST:
    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            body = getattr(node, "body", None)
            if (body and isinstance(body[0], ast.Expr)
                    and isinstance(getattr(body[0], "value", None), ast.Constant)
                    and isinstance(body[0].value.value, str)):
                node.body = body[1:] or [ast.Pass()]
    return tree


def findings(source: str) -> list[str]:
    """Every reason this file is not a launcher. Empty list = it is one. Pure."""
    out: list[str] = []
    tree = _strip_docstrings(ast.parse(source))
    defs = [n for n in tree.body if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef))]
    vendored = sorted({n.name for n in defs} & HARNESS_DEFS)
    if vendored:
        out.append("defines the HARNESS, so it is a copy and not a launcher: "
                   + ", ".join(vendored))
    if len(defs) > MAX_DEFS:
        out.append(f"{len(defs)} top-level definitions (a launcher needs at most {MAX_DEFS}): "
                   + ", ".join(n.name for n in defs[:8]))
    # Docstrings are already gone, so a surviving long literal is a template, not prose.
    for node in ast.walk(tree):
        if isinstance(node, ast.Constant) and isinstance(node.value, str) and len(node.value) > MAX_LITERAL:
            head = node.value.strip().splitlines()[0][:60] if node.value.strip() else ""
            out.append(f"carries a {len(node.value)}-character template literal at line "
                       f"{getattr(node, 'lineno', '?')} (starts: {head!r}) — a csproj or C# runner "
                       "block belongs in the canonical")
            break
    if CANONICAL not in source:
        out.append(f"never names {CANONICAL}, so it launches nothing")
    return out


def copies(root: Path) -> list[Path]:
    """Every `run-node-tests.py` in the tree, sorted, excluding caches and build output."""
    out = []
    for path in root.rglob(CANONICAL):
        rel = path.relative_to(root)
        if SEARCH_EXCLUDED & set(rel.parts[:-1]) or not path.is_file():
            continue
        out.append(path)
    return sorted(out)


def run(root: Path) -> int:
    found = copies(root)
    if not found:
        print(f"node-test harness: no {CANONICAL} anywhere in the tree — this repository invokes "
              "the platform's canonical directly; nothing to check")
        return 0
    failed = 0
    for launcher in found:
        rel = launcher.relative_to(root).as_posix()
        try:
            source = launcher.read_text(encoding="utf-8")
            problems = findings(source)
        except Exception as ex:  # noqa: BLE001 — the reason goes to the log; the verdict is RED
            print(f"::error::{rel} cannot be read or parsed ({type(ex).__name__}: {ex}) — "
                  "a guard that cannot read its subject must not pass")
            failed += 1
            continue
        if not problems:
            print(f"node-test harness: {rel} launches the platform's canonical "
                  f"{CANONICAL} ({len(source.splitlines())} lines, no harness of its own)")
            continue
        print(f"::error::{rel} is a VENDORED COPY of the platform's node-test harness, not a "
              "launcher. Three copies of it broke silently and differently the day core changed the "
              "compile model (MeshWeaver#4785): the harness imports compile-check.py, whose surface "
              "moves, and no CI lane runs the caller. Replace it with a launcher that fetches "
              f".github/scripts/{CANONICAL} from Systemorph/MeshWeaver at the ref this repo's "
              "compile-check lane uses, so the harness and the compile gate are always one vintage. "
              "A behaviour this repository genuinely needs belongs in the canonical as an option, "
              "never in a fork.")
        for problem in problems:
            print(f"  {problem}")
        failed += 1
    return 1 if failed else 0


# ── self-test ───────────────────────────────────────────────────────────────────────────────────

LAUNCHER = '''#!/usr/bin/env python3
"""Launch the platform's canonical run-node-tests.py."""
import importlib.util, os, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def main(argv):
    spec = importlib.util.spec_from_file_location("platform_script", ROOT / "scripts" / "platform-script.py")
    loader = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loader)
    return loader.main(["run-node-tests.py", *argv])


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
'''


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, cond: bool, detail: str = "") -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {name}{(' — ' + detail) if detail and not cond else ''}")
        if not cond:
            failures.append(name)

    with tempfile.TemporaryDirectory() as raw:
        tmp = Path(raw)

        def repo(name: str, text: str | None, where: str = "scripts") -> Path:
            r = tmp / name
            (r / where).mkdir(parents=True)
            if text is not None:
                (r / where / CANONICAL).write_text(text, encoding="utf-8")
            return r

        check("no copy at all passes with a notice", run(repo("none", None), ) == 0)
        check("a launcher passes", run(repo("shim", LAUNCHER)) == 0)

        # 🚨 THE LOCATION IS DISCOVERED, NOT ASSUMED. MeshWeaver.Plugins keeps its copy under
        # devtools/, so a guard hard-coded to scripts/ would have printed "nothing to check" for the
        # one repo whose copy was still RUNNING against the abandoned model.
        copy_anywhere = LAUNCHER + "\n\ndef run_set(a, b):\n    return None\n"
        check("a copy under devtools/ is found and RED",
              run(repo("devtools-copy", copy_anywhere, "devtools")) == 1)
        check("a copy under a nested module dir is found and RED",
              run(repo("nested", copy_anywhere, "Pkg/tools")) == 1)
        check("a launcher under devtools/ passes", run(repo("devtools-shim", LAUNCHER, "devtools")) == 0)
        check("CONTROL: a copy inside the .platform-scripts CACHE is NOT the repo's own",
              run(repo("cached", copy_anywhere, ".platform-scripts/abc123")) == 0)
        two = repo("two", LAUNCHER)
        (two / "devtools").mkdir()
        (two / "devtools" / CANONICAL).write_text(copy_anywhere, encoding="utf-8")
        check("a launcher in one place does not excuse a copy in another", run(two) == 1)
        check("…and the copies are counted per file",
              len(copies(two)) == 2, f"{[p.name for p in copies(two)]}")

        # Each of the four tells, alone, must red — otherwise a copy that avoided the others walks.
        copy_defs = LAUNCHER + "\n\ndef run_set(a, b):\n    return None\n"
        check("a file that defines run_set is RED", run(repo("defs", copy_defs)) == 1)
        check("…and the report names the function", "run_set" in "".join(findings(copy_defs)),
              str(findings(copy_defs)))

        template = LAUNCHER + '\nCSPROJ = """' + ("<Project>\n" * 120) + '"""\n'
        check("a file carrying a csproj template is RED", run(repo("tpl", template)) == 1)
        check("…and the report says template", any("template literal" in f for f in findings(template)),
              str(findings(template)))

        many = LAUNCHER + "".join(f"\n\ndef helper{i}():\n    return {i}\n" for i in range(MAX_DEFS + 1))
        check("a file with more than MAX_DEFS definitions is RED", run(repo("many", many)) == 1)

        silent = LAUNCHER.replace('"run-node-tests.py", *argv', "*argv").replace(
            '"""Launch the platform\'s canonical run-node-tests.py."""', '"""Launch something."""')
        check("a file that never names the canonical is RED", run(repo("silent", silent)) == 1)
        check("…and the report says it launches nothing",
              any("launches nothing" in f for f in findings(silent)), str(findings(silent)))

        check("an unparsable file is RED", run(repo("broken", "def (:\n")) == 1)

        # The negative control on the guard's own prose: a launcher whose DOCSTRING is long is still
        # a launcher — docstrings are stripped before the literal test, so prose cannot red a repo.
        wordy = LAUNCHER.replace('"""Launch the platform\'s canonical run-node-tests.py."""',
                                 '"""' + ("Launch the canonical run-node-tests.py. " * 60) + '"""')
        check("CONTROL: a long DOCSTRING is not a template (prose never reds)",
              run(repo("wordy", wordy)) == 0, str(findings(wordy)))

    if failures:
        print(f"::error::{len(failures)} self-test case(s) FAILED: " + "; ".join(failures))
        return 1
    print("check-node-test-launcher.py self-test: all cases pass")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", type=Path, help="the node repository to check")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    if args.root is None:
        ap.error("--root is required (or pass --self-test)")
    return run(args.root.resolve())


if __name__ == "__main__":
    sys.exit(main())
