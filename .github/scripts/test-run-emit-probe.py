#!/usr/bin/env python3
"""Regression checks for evidence classification and the fixed subprocess driver."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("emit_driver", Path(__file__).with_name("run-emit-probe.py"))
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)
GUID = "00000000-0000-0000-0000-000000000001"


def evidence(iterations=1000, negative=False, pgo="unset", changed=None):
    environment = dict(kind="environment", runtime=".NET 10.0.12", runtimeVersion="10.0.12",
        os="Ubuntu 24.04.4 LTS", architecture="X64", processors=4, roslyn="5.9.0+fixture",
        roslynMvid=GUID, corelibMvid=GUID, tieredPgo=pgo, tieredCompilation="unset",
        iterations=iterations, workers=1, negative=negative)
    environment.update(changed or {})
    counts = {leg + (":diagnostics:CS1001,CS1513" if negative and leg.endswith("nested") else ":emits"):
              iterations for leg in driver.LEGS}
    successful = sum(n for k, n in counts.items() if k.endswith(":emits"))
    lines = [json.dumps(environment), f"SUMMARY expectedAttempts={4*iterations} observedAttempts={4*iterations} "
             f"successfulEmits={successful} failures={4*iterations-successful} elapsedSeconds=1.0"]
    return "\n".join(lines + [f"COUNT {k}={n}" for k, n in counts.items()]) + "\n"


class ClassificationTests(unittest.TestCase):
    def classify(self, text=None, code=0, iterations=1000, negative=False, pgo="unset", expired=False):
        return driver.classify(evidence() if text is None else text, code, expired, iterations, negative, pgo)

    def test_complete_four_legs_pass(self):
        self.assertEqual("no-failure-reproduced", self.classify()["classification"])
        self.assertTrue(self.classify()["passed"])

    def test_invalid_source_is_expected_only_when_flat_legs_emit(self):
        text = evidence(2, True)
        verdict = self.classify(text, 1, 2, True)
        self.assertTrue(verdict["passed"])
        self.assertEqual("expected-diagnostics", verdict["classification"])
        self.assertFalse(self.classify(evidence(2), 0, 2, True)["passed"])
        self.assertFalse(self.classify(text.replace("shared-flat:emits", "shared-flat:diagnostics:CS1001"), 1, 2, True)["passed"])

    def test_exit_zero_without_summary_cannot_pass(self):
        result = self.classify("", 0)
        self.assertEqual("incomplete", result["classification"])
        self.assertFalse(result["passed"])

    def test_missing_duplicate_and_fabricated_counts_fail(self):
        for text in (evidence().replace("COUNT shared-flat:emits=1000\n", ""),
                     evidence() + "COUNT shared-flat:emits=1000\n",
                     evidence().replace("pristine-flat", "invented-leg"),
                     evidence().replace("successfulEmits=4000", "successfulEmits=3999")):
            with self.subTest(text=text[-80:]):
                self.assertEqual("harness-mismatch", self.classify(text)["classification"])

    def test_wrong_runtime_architecture_compiler_and_arm_fail(self):
        for change in ({"runtimeVersion": "10.0.11"}, {"architecture": "Arm64"},
                       {"os": "Darwin"}, {"roslyn": "5.8.0"}, {"workers": 4},
                       {"tieredPgo": "0"}, {"roslynMvid": "unknown"}):
            with self.subTest(change=change):
                self.assertEqual("harness-mismatch", self.classify(evidence(changed=change))["classification"])
        self.assertTrue(self.classify(evidence(pgo="0"), pgo="0")["passed"])

    def test_exception_is_not_an_automatic_ci_bug_match(self):
        text = evidence().replace("shared-nested:emits", "shared-nested:throws:NullReferenceException")
        text = text.replace("successfulEmits=4000 failures=0", "successfulEmits=3000 failures=1000")
        result = self.classify(text, 1)
        self.assertEqual("emit-exception", result["classification"])
        self.assertFalse(result["passed"])

    def test_crash_timeout_and_exit_contradiction_are_distinct(self):
        self.assertEqual("runtime-crash", self.classify("", -11)["classification"])
        self.assertEqual("incomplete", self.classify("", -9, expired=True)["classification"])
        self.assertEqual("harness-mismatch", self.classify(code=1)["classification"])


class DriverTests(unittest.TestCase):
    def setup_root(self, root):
        for path in (driver.PROJECT / "Program.cs", driver.PROJECT / "EmitProbe.csproj", driver.DLL):
            (root / path).parent.mkdir(parents=True, exist_ok=True)
            (root / path).write_text("fixed fixture")

    def drive(self, root, runner, env=None, system="Linux", machine="x86_64"):
        with patch.object(driver.shutil, "which", return_value="/fixture/dotnet"), contextlib.redirect_stdout(io.StringIO()):
            code = driver.run(root, env or {}, system, machine, runner)
        return code, json.loads((root / driver.OUTPUT / "verdict.json").read_text())

    def test_all_arms_run_once_even_when_first_fails_and_are_fixed(self):
        calls = []
        def runner(command, env, log):
            calls.append((command, env))
            iteration = int(command[command.index("--iterations") + 1])
            negative = "--invalid-source" in command
            log.write_text("" if len(calls) == 1 else evidence(iteration, negative, env.get("DOTNET_TieredPGO", "unset")))
            return (1 if negative else 0), False, 0.1
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.setup_root(root)
            code, report = self.drive(root, runner)
            self.assertEqual(1, code)
            self.assertEqual(3, len(calls))
            self.assertEqual(["incomplete", "no-failure-reproduced", "expected-diagnostics"],
                             [arm["classification"] for arm in report["arms"]])
            self.assertEqual(3, len(report["hashes"]))
            self.assertTrue(all(len(h) == 64 for h in report["hashes"].values()))
            self.assertEqual(["1000", "1000", "2"], [c[0][3] for c in calls])
            self.assertTrue(all(c[0][4:6] == ["--workers", "1"] for c in calls))
            self.assertNotIn("DOTNET_TieredPGO", calls[0][1])
            self.assertEqual("0", calls[1][1]["DOTNET_TieredPGO"])
            self.assertNotIn("DOTNET_TieredPGO", calls[2][1])

    def test_missing_build_or_failed_build_with_stale_dll_never_runs(self):
        for built, env in ((False, {}), (True, {"MW_EMIT_BUILD_OUTCOME": "failure"})):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                if built:
                    self.setup_root(root)
                code, report = self.drive(root, lambda *_: self.fail("must not execute"), env)
                self.assertEqual(1, code)
                self.assertTrue(all(a["classification"] == "not-run" for a in report["arms"]))

    def test_host_and_ci_premises_fail_closed(self):
        good = dict(GITHUB_ACTIONS="true", RUNNER_OS="Linux", RUNNER_ARCH="X64",
                    MW_EMIT_NATIVE_RUNNER="ubuntu-24.04-hosted")
        self.assertIsNone(driver.native_refusal(good, "Linux", "x86_64"))
        for env, system, machine in (({}, "Darwin", "arm64"), ({}, "Linux", "aarch64"),
            (dict(good, RUNNER_ARCH="ARM64"), "Linux", "x86_64"),
            ({"GITHUB_ACTIONS": "true"}, "Linux", "x86_64"),
            ({"DOTNET_TieredPGO": "0"}, "Linux", "x86_64")):
            self.assertIsNotNone(driver.native_refusal(env, system, machine))

    def test_real_process_output_and_timeout_are_preserved(self):
        # A tiny subprocess fixture exercises the transport, without invoking Roslyn or a mesh.
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "probe.log"
            code, expired, _ = driver.execute([sys.executable, "-c", "print('witness', flush=True)"], os.environ.copy(), log, 5)
            self.assertEqual((0, False), (code, expired))
            self.assertIn("witness", log.read_text())
            code, expired, _ = driver.execute([sys.executable, "-c", "import time; print('started', flush=True); time.sleep(30)"], os.environ.copy(), log, 0.3)
            self.assertTrue(expired)
            self.assertNotEqual(0, code)
            self.assertIn("started", log.read_text())


if __name__ == "__main__":
    unittest.main()
