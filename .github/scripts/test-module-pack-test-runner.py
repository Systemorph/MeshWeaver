#!/usr/bin/env python3
"""test-module-pack-test-runner.py — EXECUTE the module-pack lane's test-runner selection (#4414).

`node-repo-module-pack.yml` runs a module's sibling `*.Test` suite in two jobs (`pack` and
`tests`). Each picks the runner and the cwd `dotnet test` runs from, then invokes it. The suite
compiles against the PLATFORM checkout ($GITHUB_WORKSPACE/meshweaver), so the platform pins the
xunit.v3 version, and xunit.v3 4.x refuses VSTest on .NET 10. Reading only the caller's
global.json ran MeshWeaver.Plugins (which has none) under VSTest, and main-cd failed every module
suite with nothing executed.

This harness EXTRACTS both call sites from the workflow — never a copy — and runs them under bash
against temp checkouts, with `dotnet` stubbed to print its cwd and arguments. It asserts the
runner, the cwd, the reporter flags and that the suite path reaches the command absolute. It
fails red when it cannot find a call site, so a rewrite cannot silently leave it testing nothing.

Stdlib only.
"""
from __future__ import annotations

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

WORKFLOW = Path(__file__).resolve().parents[1] / "workflows" / "node-repo-module-pack.yml"
MTP = '{"test": {"runner": "Microsoft.Testing.Platform"}}'
SELECT_START = "selects_mtp() {"
SELECT_END = "test_flags() {"
INVOKE_START = "# Absolute BEFORE the cd"
INVOKE_END = '"${flags[@]}"'


def dedent(lines: list[str]) -> str:
    width = min(len(l) - len(l.lstrip()) for l in lines if l.strip())
    return "\n".join(l[width:] for l in lines)


def call_sites() -> list[tuple[str, str]]:
    """(selection block incl. test_flags, invocation block) for every call site, in file order."""
    lines = WORKFLOW.read_text().splitlines()
    sites, i = [], 0
    while i < len(lines):
        if lines[i].strip() == SELECT_START:
            j = next(k for k in range(i, len(lines)) if lines[k].strip() == SELECT_END)
            # test_flags() closes at the first line indented like its opening line and equal to "}".
            indent = len(lines[j]) - len(lines[j].lstrip())
            end = next(k for k in range(j + 1, len(lines))
                       if lines[k].strip() == "}" and len(lines[k]) - len(lines[k].lstrip()) == indent)
            a = next(k for k in range(end, len(lines)) if lines[k].strip().startswith(INVOKE_START))
            b = next(k for k in range(a, len(lines)) if lines[k].rstrip().endswith(INVOKE_END))
            sites.append((dedent(lines[i:end + 1]), dedent(lines[a:b + 1])))
            i = b + 1
        else:
            i += 1
    return sites


def run(selection: str, invocation: str, caller: str | None, platform: str | None) -> dict[str, str]:
    root = Path(tempfile.mkdtemp())
    workspace = root / "caller"
    (workspace / "src" / "Mod.Test").mkdir(parents=True)
    (workspace / "meshweaver").mkdir()
    if caller is not None:
        (workspace / "global.json").write_text(caller)
    if platform is not None:
        (workspace / "meshweaver" / "global.json").write_text(platform)
    script = "\n".join([
        "set -euo pipefail",
        'dotnet() { echo "CWD=$PWD"; for a in "$@"; do echo "ARG=$a"; done; }',
        selection,
        'echo "RUNNER=$TEST_RUNNER"',
        'MODULE=Mod',
        'mapfile -t flags < <(test_flags "$RUNNER_TEMP/trx/$MODULE")',
        'tests="src/Mod.Test"',
        "(",
        invocation,
        ")",
        'echo "AFTER=$PWD"',
    ])
    env = dict(os.environ, GITHUB_WORKSPACE=str(workspace), RUNNER_TEMP=str(root / "rt"))
    out = subprocess.run(["bash", "-c", script], cwd=workspace, env=env,
                         capture_output=True, text=True, check=True).stdout
    result: dict[str, str] = {"ARGS": ""}
    for line in out.splitlines():
        key, _, value = line.partition("=")
        if key == "ARG":
            result["ARGS"] += value + "\n"
        elif key in ("CWD", "RUNNER", "AFTER"):
            result[key] = value
    result["WORKSPACE"] = str(workspace.resolve())
    return result


class ModulePackTestRunnerTest(unittest.TestCase):
    def setUp(self):
        self.sites = call_sites()

    def test_both_call_sites_are_found_and_identical(self):
        self.assertEqual(2, len(self.sites), "the pack and tests jobs each select a runner")
        self.assertEqual(self.sites[0][0], self.sites[1][0], "the two selections must not drift apart")

    def each(self, caller, platform):
        return [run(sel, inv, caller, platform) for sel, inv in self.sites]

    def test_a_platform_that_selects_mtp_carries_a_caller_that_selects_nothing(self):
        # The #4414 shape: MeshWeaver.Plugins has no global.json; core main selects MTP.
        for r in self.each(None, MTP):
            self.assertEqual("Microsoft.Testing.Platform", r["RUNNER"])
            self.assertEqual(str(Path(r["WORKSPACE"]) / "meshweaver"), str(Path(r["CWD"]).resolve()),
                             "dotnet test must run FROM the platform checkout so its global.json applies")
            self.assertIn("--report-xunit-trx\n", r["ARGS"])
            self.assertNotIn("--logger", r["ARGS"])
            self.assertNotIn("-warnaserror", r["ARGS"])
            verb, suite = r["ARGS"].splitlines()[:2]
            self.assertEqual("test", verb)
            self.assertTrue(Path(suite).is_absolute(),
                            f"the suite path must reach the command absolute, got '{suite}' — relative, "
                            "it would name a directory under the platform checkout")
            self.assertEqual(Path(r["WORKSPACE"]) / "src" / "Mod.Test", Path(suite).resolve())
            self.assertEqual(r["WORKSPACE"], str(Path(r["AFTER"]).resolve()),
                             "the cd must stay inside the subshell")

    def test_a_caller_that_selects_mtp_runs_from_its_own_checkout(self):
        for r in self.each(MTP, None):
            self.assertEqual("Microsoft.Testing.Platform", r["RUNNER"])
            self.assertEqual(r["WORKSPACE"], str(Path(r["CWD"]).resolve()))

    def test_neither_selecting_mtp_is_vstest_from_the_callers_checkout(self):
        for r in self.each(None, None):
            self.assertEqual("VSTest", r["RUNNER"])
            self.assertEqual(r["WORKSPACE"], str(Path(r["CWD"]).resolve()))
            self.assertIn("-warnaserror\n", r["ARGS"])
            self.assertIn("--logger\n", r["ARGS"])
            self.assertNotIn("--report-xunit-trx", r["ARGS"])

    def test_another_runner_named_is_not_mtp(self):
        for r in self.each('{"sdk": {"version": "10.0.400"}}', '{"test": {"runner": "VSTest"}}'):
            self.assertEqual("VSTest", r["RUNNER"])

    def test_a_red_suite_is_named_on_github_at_both_call_sites(self):
        # MeshWeaver.Plugins#3828: a red `Module tests` leg said only "the module's own suite
        # failed". Each call site records the red suite's .trx — the SAME path test_flags writes —
        # and hands it to name-failing-tests.py after the loop. That block is EXECUTED here over a
        # real TeamTest trx; the named test and the step's annotation budget are asserted.
        text = WORKFLOW.read_text()
        record = 'red+=(--suite "$MODULE=$RUNNER_TEMP/trx/$MODULE/ledger.trx")'
        self.assertEqual(2, text.count(record), "both call sites record the red suite's ledger.trx")
        self.assertEqual(2, text.count('mapfile -t flags < <(test_flags "$RUNNER_TEMP/trx/$MODULE")'),
                         "the recorded path must be the directory test_flags writes into")
        for selection, _ in self.sites:
            self.assertIn("ledger.trx", selection, "test_flags names the trx ledger.trx")
        lines = text.splitlines()
        starts = [i for i, l in enumerate(lines) if l.strip() == 'if [ "${#red[@]}" -gt 0 ]; then']
        self.assertEqual(2, len(starts), "both call sites name the red suites after the loop")
        fails = [l.strip() for l in lines if "bk fail" in l and "the module's own suite failed" in l]
        self.assertEqual(2, len(fails), "both call sites record a red suite's failure")
        core = Path(__file__).resolve().parents[2]
        # 🚨 A MULTI-SUITE batch, failure bookkeeping AND the report, as the step runs them: the
        # REAL `bk fail` line of each call site against the real batch state, then the naming
        # block. Six red suites used to print twelve `::error`s (one generic per `bk fail`, then
        # the names) and GitHub dropped the last names; eleven left no slot for any name.
        modules = [f"Mod{i:02d}" for i in range(11)]
        for fail, start in zip(fails, starts):
            end = next(k for k in range(start, len(lines)) if lines[k].strip() == "fi")
            block = dedent(lines[start:end + 1])
            root = Path(tempfile.mkdtemp())
            for m in modules:
                (root / "rt" / "trx" / m).mkdir(parents=True)
                (root / "rt" / "trx" / m / "ledger.trx").write_text(
                    '<?xml version="1.0"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                    f'<Results><UnitTestResult testName="{m}.Test.Renders" outcome="Failed"><Output>'
                    '<ErrorInfo><Message>Expected 3 rows, found 0.</Message></ErrorInfo></Output></UnitTestResult>'
                    '</Results></TestRun>')
            (root / "build-logic").symlink_to(core)
            batch = json.dumps([{"module": m, "package": m, "project": f"src/{m}/{m}.csproj"} for m in modules])
            script = "\n".join([
                "set -euo pipefail",
                'bk() { python3 "$MODULE_PACK_BATCH" --state "$BATCH_STATE" "$@"; }',
                'bk init --batch "$BATCH" > /dev/null',
                "red=()",
                f"for MODULE in {' '.join(modules)}; do",
                "  rc=1",
                f"  {fail}",
                f"  {record}",
                "done",
                block,
                'echo "FAILED_IN_STATE=$(bk list --failed | tr "\\n" " ")"',
            ])
            env = dict(os.environ, RUNNER_TEMP=str(root / "rt"), GITHUB_STEP_SUMMARY=str(root / "summary.md"),
                       MODULE_PACK_BATCH=str(core / ".github" / "scripts" / "module-pack-batch.py"),
                       BATCH_STATE=str(root / "state.json"), BATCH=batch)
            out = subprocess.run(["bash", "-c", script], cwd=root, env=env,
                                 capture_output=True, text=True, check=True).stdout
            errors = [l for l in out.splitlines() if l.startswith("::error")]
            self.assertLessEqual(len(errors), 10, f"{len(errors)} `::error`s — GitHub keeps ten per step")
            self.assertTrue(all("failing tests" in e or "more red suite" in e for e in errors),
                            "every annotation of the step is a failing-test one:\n" + "\n".join(errors))
            joined = "\n".join(errors)
            for m in modules:
                self.assertIn(m, joined, f"{m} is represented in the step's annotations")
                self.assertIn(f"FAILED {m} › {m}.Test.Renders — Expected 3 rows, found 0.", out)
            self.assertIn(f"{modules[0]}.Test.Renders", errors[0], "the first suite's TEST is named")
            self.assertEqual(" ".join(modules), out.split("FAILED_IN_STATE=")[1].split("\n")[0].strip(),
                             "the verdicts are still recorded for every red suite")
            self.assertIn("Mod00.Test.Renders", (root / "summary.md").read_text())


if __name__ == "__main__":
    unittest.main()
