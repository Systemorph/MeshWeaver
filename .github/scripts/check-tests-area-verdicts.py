#!/usr/bin/env python3
"""check-tests-area-verdicts.py — a NodeType that ships a `Test/` folder must have RUN it.

The content gate (`mw-plugin-test`, driven by `bake-then-gate.sh`) prints one line per NodeType:

    ok  Doc/Architecture/ATypicalNodeType/Budget: compile=Ok render=ok tests=ok
            Tests host: Doc/Architecture/ATypicalNodeType/Budget/GateProbe — …
            8/8 passed
    ok  Doc/Architecture/BusinessRules/Cession: compile=Ok render=ok tests=skipped

Both lines start with `ok`, and the gate is right to pass the second: a type with no `Tests` area
has nothing to execute. What the gate CANNOT tell is whether a type that ships tests failed to
declare its area — it decides "has a Tests area" by reading the configuration lambda as TEXT for a
literal `WithView("Tests"`, so an area registered inside an extension method, a typo in the name,
or a lambda that lost the call all read `tests=skipped`, `ok`, green. The suite beside it then runs
nowhere, and nothing says so.

This closes that hole from the other side — the TREE:

  * every `<NodeType>/Test/` folder holding `.cs` files must belong to a NodeType the gate
    reported on — a `Test/` folder no NodeType owns is never compiled by the mesh at all;
  * that NodeType's verdict must be `tests=ok` — `skipped` means the suite never ran;
  * and it must carry an `N/M passed` count with N == M and M > 0 — a `tests=ok` with no count
    (the gate's "all rendered cases green" fallback) cannot notice a suite that shrank to nothing.

It is core's counterpart of MeshWeaver.Plugins' `check-test-suites.py` (the "Tests-area ratchet
over the gate log"): same log, same line formats, but keyed on the tree rather than on an allow
file, because core's trees carry no Tests-area debt to ratchet down.

🚨 IT MUST BE ABLE TO FAIL. Everything here reads the gate's stdout, so a truncated log or a changed
format would make every type "absent" — so an absent summary header, an absent terminal verdict line
and a type with no line at all are each RED, never a pass. `--require-tests` additionally reds a tree
with no `Test/` folder at all, for the tree that is SUPPOSED to carry the showcase: deleting it must
not turn this check vacuous. `--self-test` pins every one of those directions.

Usage:
    python3 .github/scripts/check-tests-area-verdicts.py --gate-log <log> --tree <staged-tree> [--require-tests]
    python3 .github/scripts/check-tests-area-verdicts.py --self-test

Stdlib only. Doc: Doc/Architecture/ATypicalNodeType.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import tempfile
from pathlib import Path

SUMMARY_HEADER = "=== mw-plugin-test summary ==="
VERDICT_RE = re.compile(r"^(ALL GREEN\.|GREEN — |GATE FAILED)")
DEBT = r"(?:\s+\[known-debt\])?"
TYPE_RE = re.compile(
    r"^\s+(?:ok|RED)\s+(?P<path>[^\s:]+):"
    rf"\s+compile=(?P<compile>\S+){DEBT}"
    rf"\s+render=(?P<render>\S+){DEBT}"
    rf"\s+tests=(?P<tests>\S+){DEBT}\s*$")
# GateReport indents a type line by four spaces and its detail lines by eight.
DETAIL_INDENT = "        "
COUNT_RE = re.compile(r"(\d+)\s*/\s*(\d+)\s+passed")


def parse_log(text: str) -> tuple[dict[str, dict], list[str]]:
    """Every type line of the summary, with its detail lines. Returns (types, integrity errors)."""
    lines = text.splitlines()
    errors: list[str] = []
    try:
        start = lines.index(SUMMARY_HEADER)
    except ValueError:
        return {}, [f"the gate log has no '{SUMMARY_HEADER}' line — the gate did not finish, "
                    "so no Tests area can be said to have run"]
    if not any(VERDICT_RE.match(line) for line in lines[start:]):
        errors.append("the gate log has no terminal verdict line (ALL GREEN. / GREEN — / GATE FAILED) "
                      "— it is truncated, so a missing type could not be told from an unrun one")
    types: dict[str, dict] = {}
    current: dict | None = None
    for line in lines[start + 1:]:
        match = TYPE_RE.match(line)
        if match:
            current = {"tests": match.group("tests"), "details": []}
            types[match.group("path")] = current
        elif current is not None and line.startswith(DETAIL_INDENT):
            current["details"].append(line.strip())
        else:
            current = None
    return types, errors


def is_nodetype(json_path: Path) -> bool:
    try:
        node = json.loads(json_path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return False
    content = node.get("content") if isinstance(node, dict) else None
    return isinstance(content, dict) and content.get("$type") == "NodeTypeDefinition"


def tested_types(tree: Path) -> tuple[list[str], list[str]]:
    """(NodeType paths owning a Test/ folder with .cs files, Test/ folders no NodeType owns)."""
    owned: list[str] = []
    orphans: list[str] = []
    for test_dir in sorted(p for p in tree.rglob("Test") if p.is_dir()):
        if not any(test_dir.rglob("*.cs")):
            continue
        owner = test_dir.parent
        relative = owner.relative_to(tree).as_posix()
        # APPEND the suffix — `with_suffix` would REPLACE a dotted folder's last segment
        # (`ACME.Foo` → `ACME.json`) and call a legitimate Test/ folder an orphan.
        if is_nodetype(owner.with_name(owner.name + ".json")) or is_nodetype(owner / "index.json"):
            owned.append(relative)
        else:
            orphans.append(f"{relative}/Test")
    return owned, orphans


def judge(types: dict[str, dict], owned: list[str], orphans: list[str],
          require_tests: bool) -> list[str]:
    """Every failure, as a sentence naming the NodeType and what to do. Pure."""
    failures: list[str] = []
    if require_tests and not owned:
        failures.append("this tree must carry at least one NodeType with a Test/ folder (the showcase, "
                        "Doc/Architecture/ATypicalNodeType) and carries none — deleting it would make "
                        "this check vacuous")
    for orphan in orphans:
        failures.append(f"{orphan}: no NodeType owns this Test/ folder (no sibling NodeType .json), so "
                        "the mesh never compiles it and its cases run nowhere")
    for path in owned:
        verdict = types.get(path)
        if verdict is None:
            failures.append(f"{path}: ships a Test/ folder but the gate log has no line for it — "
                            "the gate never judged it")
            continue
        tests = verdict["tests"]
        if tests == "skipped":
            failures.append(f"{path}: ships a Test/ folder but reported tests=skipped — its configuration "
                            "has no literal WithView(\"Tests\", …), so the suite ran nowhere")
            continue
        if tests != "ok":
            failures.append(f"{path}: tests={tests} — see the gate's own detail lines for the failing cases")
            continue
        counts = [COUNT_RE.search(d) for d in verdict["details"]]
        count = next((c for c in counts if c), None)
        if count is None:
            failures.append(f"{path}: tests=ok with no 'N/M passed' count — the verdict cannot notice a "
                            "suite that shrank; render the literal token")
            continue
        passed, total = int(count.group(1)), int(count.group(2))
        if total == 0 or passed != total:
            failures.append(f"{path}: {passed}/{total} passed — a Tests area must run at least one case "
                            "and pass all of them")
    return failures


def run(gate_log: Path, tree: Path, require_tests: bool) -> int:
    if not tree.is_dir():
        print(f"::error::tree '{tree}' does not exist — nothing was staged, so nothing can be checked")
        return 1
    try:
        text = gate_log.read_text(encoding="utf-8")
    except OSError as exc:
        print(f"::error::gate log '{gate_log}' cannot be read ({exc}) — the gate did not run")
        return 1
    types, errors = parse_log(text)
    owned, orphans = tested_types(tree)
    failures = errors + judge(types, owned, orphans, require_tests)
    for path in owned:
        verdict = types.get(path)
        count = next((COUNT_RE.search(d).group(0) for d in (verdict or {}).get("details", [])
                      if COUNT_RE.search(d)), "no count")
        print(f"  {path}: tests={verdict['tests'] if verdict else 'ABSENT'} ({count})")
    if failures:
        for failure in failures:
            print(f"::error::{failure}")
        return 1
    print(f"✓ {len(owned)} NodeType(s) with a Test/ folder, every one executed and counted "
          f"({tree.name})" if owned else f"✓ no Test/ folder in {tree.name} — nothing to execute")
    return 0


# ── self-test ────────────────────────────────────────────────────────────────────────────────────
NODETYPE = '{"id":"T","content":{"$type":"NodeTypeDefinition","configuration":""}}'
GREEN_LOG = """noise
=== mw-plugin-test summary ===
[PASS] P (3 node(s), 2 type(s))
    ok  P/Tested: compile=Ok render=ok tests=ok
        Tests host: P/Tested/GateProbe — the probe instance the gate created for this check
        3/3 passed
    ok  P/Plain: compile=Ok render=ok tests=skipped
ALL GREEN.
"""


def _tree(root: Path, with_test: bool = True, owned: bool = True, name: str = "Tested") -> Path:
    (root / "P" / name / "Source").mkdir(parents=True)
    (root / "P" / name / "Source" / "A.cs").write_text("class A {}")
    if with_test:
        (root / "P" / name / "Test").mkdir()
        (root / "P" / name / "Test" / "ATests.cs").write_text("class ATests {}")
    if owned:
        (root / "P" / f"{name}.json").write_text(NODETYPE)
    (root / "P" / "Plain.json").write_text(NODETYPE)
    return root


def _self_test() -> int:
    cases: list[tuple[str, str, dict, bool]] = [
        # (name, log, tree options, require_tests) → expected pass?
        ("green: a Test/ folder that ran and counted", GREEN_LOG, {}, True),
        ("green: no Test/ folder and none required", GREEN_LOG, {"with_test": False}, False),
        ("green: a dotted NodeType folder owns its Test/ folder",
            GREEN_LOG.replace("P/Tested:", "P/Te.sted:"), {"name": "Te.sted"}, True),
    ]
    reds: list[tuple[str, str, dict, bool]] = [
        ("red: skipped", GREEN_LOG.replace("tests=ok", "tests=skipped"), {}, False),
        ("red: failed", GREEN_LOG.replace("tests=ok", "tests=FAILED").replace("3/3", "2/3"), {}, False),
        ("red: ok without a count", GREEN_LOG.replace("        3/3 passed\n", ""), {}, False),
        ("red: short count", GREEN_LOG.replace("3/3 passed", "2/3 passed"), {}, False),
        ("red: zero cases", GREEN_LOG.replace("3/3 passed", "0/0 passed"), {}, False),
        ("red: type absent from the log", GREEN_LOG.replace("P/Tested:", "P/Other:"), {}, False),
        ("red: no summary header", GREEN_LOG.replace(SUMMARY_HEADER, ""), {}, False),
        ("red: truncated, no verdict line", GREEN_LOG.replace("ALL GREEN.\n", ""), {}, False),
        ("red: an orphan Test/ folder", GREEN_LOG, {"owned": False}, False),
        ("red: required but no Test/ folder", GREEN_LOG, {"with_test": False}, True),
    ]
    bad: list[str] = []
    for name, log, options, require, expect_pass in (
            [(*c, True) for c in cases] + [(*r, False) for r in reds]):
        with tempfile.TemporaryDirectory() as tmp:
            tree = _tree(Path(tmp) / "tree", **options)
            log_file = Path(tmp) / "gate.log"
            log_file.write_text(log, encoding="utf-8")
            types, errors = parse_log(log)
            owned, orphans = tested_types(tree)
            failed = bool(errors + judge(types, owned, orphans, require))
            if failed == expect_pass:
                bad.append(f"{name}: expected {'PASS' if expect_pass else 'FAIL'}, got "
                           f"{'FAIL' if failed else 'PASS'}")
    if bad:
        for line in bad:
            print(f"::error::self-test: {line}")
        return 1
    print(f"✓ self-test green — {len(cases)} passing and {len(reds)} failing cases behave")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--gate-log", type=Path)
    parser.add_argument("--tree", type=Path, help="the STAGED tree the gate judged")
    parser.add_argument("--require-tests", action="store_true",
                        help="red when the tree carries no Test/ folder at all")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return _self_test()
    if args.gate_log is None or args.tree is None:
        parser.error("--gate-log and --tree are required (or --self-test)")
    return run(args.gate_log, args.tree, args.require_tests)


if __name__ == "__main__":
    sys.exit(main())
