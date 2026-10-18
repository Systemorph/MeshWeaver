#!/usr/bin/env python3
"""name-failing-tests.py — NAME every failing test of a step's red suites, on GitHub, within budget.

    python3 name-failing-tests.py --suite LABEL=PATH.trx [--suite …] \
        [--summary "$GITHUB_STEP_SUMMARY"] [--annotation-budget FILE]
    python3 name-failing-tests.py --self-test

A lane that runs several `dotnet test` suites in one step (the `Module bundle` / `Module tests`
legs of node-repo-module-pack.yml) used to leave a red suite UNNAMED on GitHub: the step went red,
the ledger recorded the FAILED names in the registry, and a reader of the pull request had to dig
the failing test out of the raw log (MeshWeaver.Plugins#3828, MeshWeaver.Blazor.Views.Test). This
reads each red suite's `.trx` and says, for every failing test, its fully-qualified name and the
FIRST line of its assertion message:

  * in the LOG, one `FAILED <suite> › <test> — <assertion>` line per test, with no cap;
  * in the job SUMMARY (`--summary`), the same list as Markdown;
  * as a few bounded MULTILINE `::error` annotations. GitHub keeps at most TEN `::error`
    annotations per STEP and drops the rest SILENTLY, so one annotation per test would lose exactly
    the names this exists to carry. Every red suite gets one annotation of its own while the budget
    lasts; past that, one last annotation names every remaining suite with its count. The budget is
    a FILE (`--annotation-budget`) when several processes of one step share it.

A suite that is red with nothing to name is SAID — never an empty list that reads as "nothing
failed": no `.trx` at all (the host died before writing one, or never started), an unreadable one,
or one that records no failing test (the host exited non-zero after every test passed).

It is a REPORTER: it always exits 0, and the caller's own exit code stays the verdict. Stdlib only.
The same reading of a `.trx` (outcomes, the `<suite>.HOST_CRASHED` row record-host-crash.py
writes, the first `<ErrorInfo>` line) is MeshWeaver.Plugins' `classify-test-run.py --trx`.
"""
from __future__ import annotations

import argparse
import pathlib
import sys
import tempfile
import xml.etree.ElementTree as ET

# GitHub Actions keeps at most this many `::error` annotations per STEP.
STEP_ERROR_ANNOTATION_LIMIT = 10
# Lines carried inside ONE multiline annotation before it says "+N more".
LINES_PER_ANNOTATION = 10
FAILED_OUTCOMES = ("failed", "error", "timeout", "aborted")
HOST_CRASH_SUFFIX = ".HOST_CRASHED"


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def first_message_line(result: ET.Element, limit: int = 300) -> str:
    """The first non-blank line under `<ErrorInfo><Message>` (else `<StackTrace>`).

    ONLY under ErrorInfo: `<Output><StdOut>` and `<TextMessages><Message>` come first in document
    order and hold what the test WROTE, which is not its failure."""
    errors = [n for n in result.iter() if _local(n.tag) == "ErrorInfo"]
    for wanted in ("Message", "StackTrace"):
        for error in errors:
            for node in error:
                if _local(node.tag) != wanted or not node.text:
                    continue
                for line in node.text.splitlines():
                    line = line.strip()
                    if line:
                        return line if len(line) <= limit else line[:limit - 1] + "…"
    return ""


def failing_tests(trx: pathlib.Path) -> list[tuple[str, str]]:
    """(test, first assertion line) per failing result; a dead host's HOST_CRASHED row first."""
    rows: dict[str, str] = {}
    for el in ET.parse(trx).getroot().iter():
        if _local(el.tag) != "UnitTestResult":
            continue
        name = el.get("testName")
        if not name:
            continue
        if name.endswith(HOST_CRASH_SUFFIX) or (el.get("outcome") or "").lower() in FAILED_OUTCOMES:
            rows.setdefault(name, first_message_line(el))
    return sorted(rows.items(), key=lambda r: (not r[0].endswith(HOST_CRASH_SUFFIX), r[0]))


def escape_data(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(text: str) -> str:
    return escape_data(text).replace(":", "%3A").replace(",", "%2C")


class Budget:
    """The step's `::error` count, in a file so the processes of one step share it."""

    def __init__(self, path: str | None):
        self.path = pathlib.Path(path) if path else None

    def used(self) -> int:
        if not self.path:
            return 0
        try:
            return int(self.path.read_text().strip() or 0)
        except (OSError, ValueError):
            return 0

    def remaining(self) -> int:
        return max(0, STEP_ERROR_ANNOTATION_LIMIT - self.used())

    def spend(self, n: int) -> None:
        if self.path and n:
            self.path.write_text(str(self.used() + n))


def read_suite(label: str, trx: pathlib.Path) -> tuple[list[str], list[str]]:
    """(detail lines, markdown) for one red suite — never empty."""
    if not trx.is_file():
        why = f"no .trx at {trx} — the host died before writing one or never started, so no failing test can be named"
        return [why], [f"#### `{label}` — {why}", ""]
    try:
        rows = failing_tests(trx)
    except ET.ParseError as exc:
        why = f"its .trx is unreadable ({exc}) — no failing test can be named"
        return [why], [f"#### `{label}` — {why}", ""]
    if not rows:
        why = "exited non-zero, but its .trx records no failing test (read the host's own output above)"
        return [why], [f"#### `{label}` — {why}", ""]
    lines = [f"{t} — {m or '(no assertion message in the .trx)'}" for t, m in rows]
    md = [f"#### `{label}` — {len(rows)} failing test(s)", ""]
    md += [f"- `{t}` — " + (m.replace("`", "'") if m else "_(no assertion message in the .trx)_") for t, m in rows]
    return lines, md + [""]


def bounded_annotations(groups: list[tuple[str, list[str]]], slots: int) -> list[str]:
    """At most `slots` multiline `::error` commands, EVERY group represented."""
    groups = [(t, ls) for t, ls in groups if ls]
    if slots <= 0 or not groups:
        return []

    def one(title: str, lines: list[str]) -> str:
        body = lines[:LINES_PER_ANNOTATION]
        if len(lines) > LINES_PER_ANNOTATION:
            body.append(f"+{len(lines) - LINES_PER_ANNOTATION} more — in the log and the job summary")
        return (f"::error title={escape_property(title + ' — failing tests')}::"
                + escape_data("\n".join(body)))

    if len(groups) <= slots:
        return [one(t, ls) for t, ls in groups]
    shown, rest = groups[:slots - 1], groups[slots - 1:]
    out = [one(t, ls) for t, ls in shown]
    out.append(f"::error title={escape_property(f'{len(rest)} more red suite(s)')}::"
               + escape_data("\n".join(f"{t}: {len(ls)} line(s) — first: {ls[0]}" for t, ls in rest)))
    return out


def report(suites: list[tuple[str, pathlib.Path]], summary: str | None, budget_file: str | None) -> list[str]:
    out: list[str] = []
    groups: list[tuple[str, list[str]]] = []
    md: list[str] = []
    for label, trx in suites:
        lines, block = read_suite(label, trx)
        groups.append((label, lines))
        md += block
        out += [f"FAILED {label} › {ln}" for ln in lines]
    if summary and md:
        with open(summary, "a", encoding="utf-8") as fh:
            fh.write("### Failing tests\n\n" + "\n".join(md) + "\n")
    budget = Budget(budget_file)
    notes = bounded_annotations(groups, budget.remaining())
    budget.spend(len(notes))
    if groups and not notes:
        notes = [f"(error annotation budget for this step spent — log only) {len(groups)} red suite(s): "
                 + ", ".join(t for t, _ in groups)]
    return out + notes


# ─────────────────────────────── self-test ───────────────────────────────

_NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


def _trx(path: pathlib.Path, results: list[tuple[str, str, str]]) -> pathlib.Path:
    """results: (testName, outcome, message). StdOut written FIRST, as a real trx does."""
    rows = []
    for name, outcome, message in results:
        err = (f"<ErrorInfo><Message>{message}</Message><StackTrace>at X.Y()</StackTrace></ErrorInfo>"
               if message else "")
        rows.append(f'<UnitTestResult testName="{name}" outcome="{outcome}"><Output>'
                    f"<StdOut>=== TEST CLASS INIT {name}</StdOut>{err}</Output></UnitTestResult>")
    path.write_text(f'<?xml version="1.0"?><TestRun xmlns="{_NS}"><Results>{"".join(rows)}</Results></TestRun>')
    return path


def self_test() -> int:
    failures: list[str] = []

    def check(label: str, ok: bool, detail: str = "") -> None:
        print(f"{'✓' if ok else '✗'} {label}" + (f" — {detail}" if detail and not ok else ""))
        if not ok:
            failures.append(label)

    with tempfile.TemporaryDirectory() as tmp_s:
        tmp = pathlib.Path(tmp_s)
        red = _trx(tmp / "red.trx", [
            ("Ns.Views.Test.Renders", "Failed", "Expected 3 rows, found 0.\nsecond line"),
            ("Ns.Views.Test.Passes", "Passed", ""),
            ("Ns.Views.Test.NoMessage", "Failed", ""),
        ])
        summary = tmp / "summary.md"
        budget = tmp / "budget"
        budget.write_text("")
        out = report([("MeshWeaver.Blazor.Views", red)], str(summary), str(budget))
        log = [l for l in out if l.startswith("FAILED ")]
        check("a failing test is named with its FIRST assertion line, never its StdOut",
              "FAILED MeshWeaver.Blazor.Views › Ns.Views.Test.Renders — Expected 3 rows, found 0." in log, str(log))
        check("a passing test is not named", not any("Passes" in l for l in log))
        check("a failure with no message says so", any("NoMessage — (no assertion message" in l for l in log))
        errors = [l for l in out if l.startswith("::error")]
        check("one suite → ONE multiline annotation carrying its tests",
              len(errors) == 1 and "Ns.Views.Test.Renders" in errors[0] and "%0A" in errors[0], str(errors))
        check("the budget file counts what was spent", budget.read_text() == "1", budget.read_text())
        text = summary.read_text()
        check("the job summary names the failing test", "`Ns.Views.Test.Renders`" in text and "2 failing" in text)

        # Nothing to name is SAID, never silent.
        empty = _trx(tmp / "green.trx", [("A.B", "Passed", "")])
        out = report([("NoTrx", tmp / "absent.trx"), ("Green", empty)], None, None)
        check("a red suite without a .trx is named as such", any("NoTrx › no .trx at" in l for l in out), str(out))
        check("a red suite whose .trx has no failure is named as such",
              any("Green › exited non-zero" in l for l in out), str(out))
        (tmp / "bad.trx").write_text("<TestRun")
        out = report([("Bad", tmp / "bad.trx")], None, None)
        check("an unreadable .trx is named as such", any("unreadable" in l for l in out), str(out))

        # A dead host's row comes first.
        crash = _trx(tmp / "crash.trx", [("Z.Last", "Failed", "boom"),
                                         ("S.HOST_CRASHED", "Failed", "exit=139")])
        rows = failing_tests(crash)
        check("the HOST_CRASHED row is listed first", rows and rows[0][0] == "S.HOST_CRASHED", str(rows))

        # 🚨 THE STEP'S 10-ANNOTATION BUDGET: 12 red suites, 3 slots already spent by the step.
        many = [(f"Suite{i}", _trx(tmp / f"s{i}.trx", [(f"T{i}.{j}", "Failed", f"m{j}") for j in range(23 if i == 0 else 1)]))
                for i in range(12)]
        budget.write_text("3")
        out = report(many, None, str(budget))
        errors = [l for l in out if l.startswith("::error")]
        check("at most the remaining budget is spent (7 of 10)", len(errors) == 7, str(len(errors)))
        joined = "\n".join(errors)
        check("EVERY red suite is represented in the annotations",
              all(f"Suite{i}" in joined for i in range(12)))
        check("a long suite is cut at the per-annotation cap and says how many more",
              "+13 more" in errors[0], errors[0][:200])
        check("the log still names every one of the 23 + 11 failing tests",
              sum(1 for l in out if l.startswith("FAILED ")) == 34)
        budget.write_text("10")
        out = report(many[:2], None, str(budget))
        check("a spent budget prints no `::error`, and still names the suites in the log",
              not any(l.startswith("::error") for l in out) and any("budget for this step spent" in l for l in out))

    print(f"\nname-failing-tests self-test: {'all cases pass' if not failures else f'{len(failures)} FAILED'}")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--suite", action="append", default=[], metavar="LABEL=TRX",
                    help="A RED suite and its .trx (repeatable). The file need not exist — that is reported.")
    ap.add_argument("--summary", metavar="FILE", help="Append the list as Markdown (the job's $GITHUB_STEP_SUMMARY).")
    ap.add_argument("--annotation-budget", metavar="FILE",
                    help="The step's shared `::error` counter; without it this call assumes the whole budget is free.")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    suites = []
    for spec in args.suite:
        label, sep, path = spec.partition("=")
        if not sep or not label or not path:
            ap.error(f"--suite wants LABEL=TRX, got '{spec}'")
        suites.append((label, pathlib.Path(path)))
    for line in report(suites, args.summary, args.annotation_budget):
        print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
