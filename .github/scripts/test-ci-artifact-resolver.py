#!/usr/bin/env python3
"""Run the resolver action's actual shell against real filesystem stores."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / '.github/scripts' / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ACTION = load('artifact_action_shell', 'test-ci-artifact-actions.py')


class ResolverTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name).resolve()
        self.share = self.root / 'share'
        self.share.mkdir()

    def resolve(self, store, expected='', code=0, require=False, ambient=''):
        output = self.root / 'outputs'
        result = subprocess.run(['bash', '-euo', 'pipefail', '-c', ACTION.action_shell('resolve-artifact-store')],
                                capture_output=True, text=True, timeout=30,
                                env={**os.environ, 'CI_ARTIFACT_ACTION_PATH': str(ROOT / '.github/actions/resolve-artifact-store'),
                                     'CI_ARTIFACT_STORE': store, 'CI_ARTIFACT_EXPECT_STORE_ID': expected,
                                     'CI_ARTIFACT_REQUIRE_STORE_ID': str(require).lower(),
                                     'MW_ARTIFACT_STORE': ambient,
                                     'GITHUB_OUTPUT': str(output), 'GITHUB_STEP_SUMMARY': ''})
        self.assertEqual(code, result.returncode, result.stdout + result.stderr)
        return output.read_text() if output.exists() else '', result

    def test_initial_producer_emits_actual_identity_and_matching_caller_is_accepted(self):
        store = f'file:{self.share}'
        expected = ACTION.STORE.make_store(store).store_id()
        output, _ = self.resolve(store)
        self.assertIn(f'store={store}\n', output)
        self.assertIn(f'store-id={expected}\n', output)
        self.resolve(store, expected)

    def test_wrong_share_is_rejected_before_outputs(self):
        other = self.root / 'other'
        other.mkdir()
        output, result = self.resolve(f'file:{self.share}', ACTION.STORE.make_store(f'file:{other}').store_id(), 1)
        self.assertEqual('', output)
        self.assertIn('NOT on the store', result.stderr)

    def test_missing_declared_store_does_not_fall_back(self):
        output, _ = self.resolve(f'file:{self.root / "missing"}', code=1)
        self.assertNotIn('store=gha', output)

    def test_empty_and_explicit_gha_preserve_public_caller_backend(self):
        for value in ('', 'gha'):
            with self.subTest(value=value):
                output, _ = self.resolve(value)
                self.assertIn('store=gha\n', output)

    def test_unsupported_named_backend_is_refused(self):
        output, _ = self.resolve('azblob:account/container', code=1)
        self.assertEqual('', output)

    def test_reusable_requires_producer_identity_before_resolving_its_own_store(self):
        output, result = self.resolve(f'file:{self.share}', code=1, require=True)
        self.assertEqual('', output)
        self.assertIn('require expected-store-id from the first producer', result.stderr)
        self.resolve(f'file:{self.share}', ACTION.STORE.make_store(f'file:{self.share}').store_id(), require=True)

    def test_legacy_gha_caller_ignores_ambient_own_store_and_needs_no_identity(self):
        output, _ = self.resolve('gha', require=True, ambient=f'file:{self.share}')
        self.assertIn('store=gha', output.splitlines())

    def test_contract_wires_the_consumer_precondition_and_both_outputs(self):
        action = (ROOT / '.github/actions/resolve-artifact-store/action.yml').read_text()
        self.assertIn('CI_ARTIFACT_REQUIRE_STORE_ID: ${{ inputs.require-expected-store-id }}', action)
        for key in ('store', 'store-id'):
            self.assertIn(f'value: ${{{{ steps.resolve.outputs.{key} }}}}', action)



if __name__ == '__main__':
    unittest.main(verbosity=2)
