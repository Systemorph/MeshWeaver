#!/usr/bin/env python3
"""Execute the own-store composite actions' actual shell, without a GitHub service."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("artifact_store_for_actions",
                                            ROOT / '.github/scripts/ci-artifact-store.py')
STORE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(STORE)


def action_shell(action):
    text = (ROOT / '.github/actions' / action / 'action.yml').read_text()
    # Each composite has one own-store shell step. Keep the test tied to that actual step,
    # rather than a hand-copied command that can keep passing after the action changes.
    marker = '      run: |\n'
    if text.count(marker) != 1:
        raise AssertionError('expected exactly one shell step in the composite')
    return '\n'.join(line[8:] for line in text.split(marker)[1].splitlines())


class CompositeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.share = self.root / 'share'
        self.share.mkdir()
        self.source = self.root / 'source'
        self.source.mkdir()
        (self.source / 'payload.txt').write_text('from our runner\n')

    def run_action(self, action, expected_code=0, **env):
        inputs = {
            'GITHUB_REPOSITORY': 'Systemorph/Plugins', 'GITHUB_RUN_ID': '123',
            'GITHUB_RUN_ATTEMPT': '1',
            'GITHUB_OUTPUT': str(self.root / 'outputs'),
            'CI_ARTIFACT_ACTION_PATH': str(ROOT / '.github/actions' / action),
            'CI_ARTIFACT_STORE': f'file:{self.share}', 'CI_ARTIFACT_NAME': 'test-receipt',
            'CI_ARTIFACT_EXPECT_STORE_ID': STORE.make_store(f'file:{self.share}').store_id(),
            'CI_ARTIFACT_PATH': str(self.source), 'CI_ARTIFACT_MISSING': 'error',
            'CI_ARTIFACT_RETENTION': '0', 'CI_ARTIFACT_COMPRESSION': '0',
            'CI_ARTIFACT_OVERWRITE': 'false', 'CI_ARTIFACT_HIDDEN': 'false',
            'CI_ARTIFACT_REPOSITORY': 'Systemorph/Plugins', 'CI_ARTIFACT_RUN_ID': '123',
            'CI_ARTIFACT_PATTERN': '', 'CI_ARTIFACT_MERGE': 'false',
        }
        result = subprocess.run(['bash', '-euo', 'pipefail', '-c', action_shell(action)],
                                env={**os.environ, **inputs, **env}, cwd=self.root,
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(expected_code, result.returncode, result.stdout + result.stderr)
        return result

    def test_composite_inputs_and_outputs_wire_identity_only_to_own_store(self):
        for action in ('upload-artifact', 'download-artifact'):
            with self.subTest(action=action):
                text = (ROOT / '.github/actions' / action / 'action.yml').read_text()
                self.assertIn('  expected-store-id:\n', text)
                self.assertIn('CI_ARTIFACT_EXPECT_STORE_ID: ${{ inputs.expected-store-id }}', text)
                self.assertIn('value: ${{ steps.own.outputs.store-id }}', text)
                github_step = text.split('      id: github\n')[1].split('    - name:')[0]
                self.assertNotIn('expected-store-id', github_step)

    def test_default_retention_and_failed_job_rerun_use_real_action_shell(self):
        self.run_action('upload-artifact')
        self.run_action('download-artifact', GITHUB_RUN_ATTEMPT='2',
                        CI_ARTIFACT_PATH=str(self.root / 'out'))
        self.assertEqual('from our runner\n', (self.root / 'out/payload.txt').read_text())
        outputs = (self.root / 'outputs').read_text()
        self.assertIn('artifact-id=\n', outputs)
        self.assertIn('artifact-locator=file:', outputs)
        self.assertIn('download-path=', outputs)
        self.assertIn(f'store-id={STORE.make_store(f"file:{self.share}").store_id()}\n', outputs)

    def test_file_actions_require_the_resolved_store_identity(self):
        for action in ('upload-artifact', 'download-artifact'):
            with self.subTest(action=action):
                result = self.run_action(action, expected_code=1, CI_ARTIFACT_EXPECT_STORE_ID='')
                self.assertIn('EMPTY value', result.stderr)
        self.assertEqual([], list(self.share.iterdir()))

    def test_other_share_is_refused_even_when_pattern_or_upload_is_empty(self):
        other = self.root / 'other-share'
        other.mkdir()
        result = self.run_action('upload-artifact', expected_code=1,
                                 CI_ARTIFACT_STORE=f'file:{other}',
                                 CI_ARTIFACT_PATH=str(self.root / 'absent'),
                                 CI_ARTIFACT_MISSING='ignore')
        self.assertIn('NOT on the store this run resolved', result.stderr)
        result = self.run_action('download-artifact', expected_code=1,
                                 CI_ARTIFACT_STORE=f'file:{other}', CI_ARTIFACT_NAME='',
                                 CI_ARTIFACT_PATTERN='receipt-*',
                                 CI_ARTIFACT_PATH=str(self.root / 'out'))
        self.assertIn('NOT on the store this run resolved', result.stderr)
        self.assertEqual([], list(other.iterdir()))
        self.assertFalse((self.root / 'out').exists())

    def test_matching_store_allows_zero_pattern_and_emits_identity(self):
        self.run_action('download-artifact', CI_ARTIFACT_NAME='', CI_ARTIFACT_PATTERN='receipt-*',
                        CI_ARTIFACT_PATH=str(self.root / 'out'))
        outputs = (self.root / 'outputs').read_text()
        self.assertIn('file-count=0\n', outputs)
        self.assertIn(f'store-id={STORE.make_store(f"file:{self.share}").store_id()}\n', outputs)

    def test_other_runs_latest_attempt_is_not_bounded_by_callers_attempt(self):
        self.run_action('upload-artifact', GITHUB_RUN_ATTEMPT='3')
        self.run_action('download-artifact', GITHUB_RUN_ID='456',
                        CI_ARTIFACT_PATH=str(self.root / 'out'))
        self.assertTrue((self.root / 'out/payload.txt').is_file())

    def test_output_names_the_canonical_extraction_directory(self):
        self.run_action('upload-artifact')
        actual = self.root / 'actual'
        actual.mkdir()
        alias = self.root / 'alias'
        alias.symlink_to(actual, target_is_directory=True)
        self.run_action('download-artifact', CI_ARTIFACT_PATH=str(alias / 'out'))
        self.assertTrue((actual / 'out/payload.txt').is_file())
        self.assertIn(f'download-path={actual}/out\n', (self.root / 'outputs').read_text())


if __name__ == '__main__':
    unittest.main(verbosity=2)
