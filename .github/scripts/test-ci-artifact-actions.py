#!/usr/bin/env python3
"""Execute the own-store composite actions' actual shell, without a GitHub service."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


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

    def run_action(self, action, **env):
        inputs = {
            'GITHUB_REPOSITORY': 'Systemorph/Plugins', 'GITHUB_RUN_ID': '123',
            'GITHUB_RUN_ATTEMPT': '1',
            'GITHUB_OUTPUT': str(self.root / 'outputs'),
            'CI_ARTIFACT_ACTION_PATH': str(ROOT / '.github/actions' / action),
            'CI_ARTIFACT_STORE': f'file:{self.share}', 'CI_ARTIFACT_NAME': 'test-receipt',
            'CI_ARTIFACT_PATH': str(self.source), 'CI_ARTIFACT_MISSING': 'error',
            'CI_ARTIFACT_RETENTION': '0', 'CI_ARTIFACT_COMPRESSION': '0',
            'CI_ARTIFACT_OVERWRITE': 'false', 'CI_ARTIFACT_HIDDEN': 'false',
            'CI_ARTIFACT_REPOSITORY': 'Systemorph/Plugins', 'CI_ARTIFACT_RUN_ID': '123',
            'CI_ARTIFACT_PATTERN': '', 'CI_ARTIFACT_MERGE': 'false',
        }
        result = subprocess.run(['bash', '-euo', 'pipefail', '-c', action_shell(action)],
                                env={**os.environ, **inputs, **env}, cwd=self.root,
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return result

    def test_default_retention_and_failed_job_rerun_use_real_action_shell(self):
        self.run_action('upload-artifact')
        self.run_action('download-artifact', GITHUB_RUN_ATTEMPT='2',
                        CI_ARTIFACT_PATH=str(self.root / 'out'))
        self.assertEqual('from our runner\n', (self.root / 'out/payload.txt').read_text())
        outputs = (self.root / 'outputs').read_text()
        self.assertIn('artifact-id=\n', outputs)
        self.assertIn('artifact-locator=file:', outputs)
        self.assertIn('download-path=', outputs)

    def test_other_runs_latest_attempt_is_not_bounded_by_callers_attempt(self):
        self.run_action('upload-artifact', GITHUB_RUN_ATTEMPT='3')
        self.run_action('download-artifact', GITHUB_RUN_ID='456',
                        CI_ARTIFACT_PATH=str(self.root / 'out'))
        self.assertTrue((self.root / 'out/payload.txt').is_file())


if __name__ == '__main__':
    unittest.main(verbosity=2)
