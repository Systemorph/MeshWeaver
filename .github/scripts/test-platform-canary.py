#!/usr/bin/env python3
"""Exercise the exact embedded canary evidence code without a GitHub runner."""
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

workflow = Path(__file__).resolve().parents[1] / 'workflows/node-repo-platform-canary.yml'
source = workflow.read_text().split("<<'PYTHON'\n", 1)[1].split('\n          PYTHON', 1)[0]
namespace = {'__name__': 'canary_under_test'}
exec(compile('\n'.join(line[10:] for line in source.splitlines()), str(workflow), 'exec'), namespace)
compare = namespace['compare']
read_results = namespace['read_results']
test_runner = namespace['test_runner']

MTP = '{"test": {"runner": "Microsoft.Testing.Platform"}}'


def arm(**results):
    return {'suite': {'results': results}}


class CanaryEvidenceTest(unittest.TestCase):
    def test_only_a_recorded_pass_can_be_a_regression(self):
        delta, incomplete = compare(arm(good='Passed', old='Failed'), arm(good='Failed', old='Failed'))
        self.assertEqual(['suite: good'], delta)
        self.assertEqual([], incomplete)

    def test_candidate_compiler_failure_has_a_successful_build_control(self):
        pin = {'suite': {'build': 'passed', 'results': {'test': 'Passed'}}}
        main = {'suite': {'build': 'failed', 'error': 'CS0246'}}
        self.assertEqual((['suite: BUILD FAILED at main (build passed at pin)'], []), compare(pin, main))

    def test_candidate_infrastructure_failure_is_not_a_build_regression(self):
        pin = {'suite': {'build': 'passed', 'results': {'test': 'Passed'}}}
        main = {'suite': {'build': 'unknown', 'error': 'host died'}}
        delta, incomplete = compare(pin, main)
        self.assertEqual([], delta)
        self.assertTrue(incomplete)

    def test_baseline_build_failure_never_establishes_a_pass(self):
        delta, incomplete = compare({'suite': {'error': 'BUILD FAILED'}}, arm(test='Failed'))
        self.assertEqual([], delta)
        self.assertTrue(incomplete)

    def test_both_builds_failing_is_not_a_clean_comparison(self):
        failed = {'suite': {'error': 'BUILD FAILED'}}
        self.assertTrue(compare(failed, failed)[1])

    def test_missing_candidate_test_cannot_heal_a_report(self):
        self.assertTrue(compare(arm(test='Failed'), arm(other='Passed'))[1])

    def test_skipped_candidate_test_cannot_heal_a_report(self):
        self.assertTrue(compare(arm(test='Failed'), arm(test='NotExecuted'))[1])

    def test_skipped_baseline_is_not_a_pass(self):
        delta, incomplete = compare(arm(test='NotExecuted'), arm(test='Failed'))
        self.assertEqual([], delta)
        self.assertTrue(incomplete)

    def test_new_failed_test_has_no_baseline(self):
        self.assertTrue(compare(arm(control='Passed'), arm(control='Passed', new='Failed'))[1])

    def test_no_regressions_with_two_measured_arms(self):
        self.assertEqual(([], []), compare(arm(test='Passed'), arm(test='Passed')))

    def test_suite_denominator_is_required(self):
        for left, right in [({}, {}), (arm(test='Passed'), {})]:
            with self.assertRaises(ValueError):
                compare(left, right)

    def trx(self, outcomes, code=0, total=None, summary='Completed'):
        ns = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
        root = ET.Element('TestRun', xmlns=ns)
        results = ET.SubElement(root, 'Results')
        for name, outcome in outcomes:
            ET.SubElement(results, 'UnitTestResult', testName=name, outcome=outcome)
        result_summary = ET.SubElement(root, 'ResultSummary', outcome=summary)
        ET.SubElement(result_summary, 'Counters', total=str(len(outcomes) if total is None else total),
                      failed=str(sum(v == 'Failed' for _, v in outcomes)))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'results.trx'
            ET.ElementTree(root).write(path)
            return read_results(path, code)

    def test_real_trx_names_preserve_theory_arguments(self):
        name = 'Example.Tests.Case(value: "a)b")'
        self.assertEqual({name: 'Passed'}, self.trx([(name, 'Passed')]))

    def test_no_execution_and_partial_results_are_incomplete(self):
        for outcomes in [[], [('test', 'NotExecuted')], [('test', 'InProgress')]]:
            with self.assertRaises(ValueError):
                self.trx(outcomes)
        with self.assertRaises(ValueError):
            self.trx([('test', 'Passed')], total=2)

    def test_host_crash_and_exit_mismatch_are_incomplete(self):
        for code in [1, 124, 139]:
            with self.assertRaises(ValueError):
                self.trx([('test', 'Passed')], code=code)
        with self.assertRaises(ValueError):
            self.trx([('test', 'Failed')], code=0)
        with self.assertRaises(ValueError):
            self.trx([('test', 'Passed')], summary='Aborted')

    def test_ambiguous_names_are_not_merged_silently(self):
        with self.assertRaises(ValueError):
            self.trx([('test', 'Passed'), ('test', 'Failed')], code=1)


class CanaryTestRunnerTest(unittest.TestCase):
    """Which runner an arm's `dotnet test` uses, and the cwd it runs from (#4414).

    The suite builds against the arm's PLATFORM checkout, so the platform pins the xunit.v3
    version — and xunit.v3 4.x refuses VSTest on .NET 10. `dotnet test` reads the runner from the
    first global.json walking UP from its cwd, so the cwd is half of the answer."""

    def checkouts(self, caller=None, platform=None):
        root = Path(tempfile.mkdtemp())
        repo, core = root / 'repo', root / 'core-main'
        for directory, marker in ((repo, caller), (core, platform)):
            directory.mkdir()
            if marker is not None:
                (directory / 'global.json').write_text(marker)
        return repo, core

    def test_a_platform_that_selects_mtp_carries_a_caller_that_selects_nothing(self):
        # The #4414 shape: MeshWeaver.Plugins has no global.json, core main selects MTP.
        repo, core = self.checkouts(platform=MTP)
        self.assertEqual((True, core), test_runner(repo, core),
                         'must run under MTP, FROM the platform checkout so its global.json is found')

    def test_a_caller_that_selects_mtp_runs_from_its_own_checkout(self):
        repo, core = self.checkouts(caller=MTP)
        self.assertEqual((True, repo), test_runner(repo, core))

    def test_both_selecting_mtp_keeps_the_callers_checkout(self):
        repo, core = self.checkouts(caller=MTP, platform=MTP)
        self.assertEqual((True, repo), test_runner(repo, core))

    def test_neither_selecting_mtp_is_vstest_from_the_callers_checkout(self):
        # The pin arm before 4.x: nothing changes for it.
        repo, core = self.checkouts()
        self.assertEqual((False, repo), test_runner(repo, core))

    def test_a_global_json_that_selects_another_runner_selects_nothing(self):
        repo, core = self.checkouts(caller='{"sdk": {"version": "10.0.400"}}',
                                    platform='{"test": {"runner": "VSTest"}}')
        self.assertEqual((False, repo), test_runner(repo, core))

    def test_an_unreadable_global_json_is_refused_not_guessed(self):
        repo, core = self.checkouts(caller='{not json', platform=MTP)
        with self.assertRaises(ValueError):
            test_runner(repo, core)

if __name__ == '__main__':
    unittest.main()
