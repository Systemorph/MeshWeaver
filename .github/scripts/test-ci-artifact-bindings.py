#!/usr/bin/env python3
"""Run the resolver's actual shell and prove every migrated workflow binds its transfers."""
import importlib.util
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / '.github/scripts' / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


CHECK = load('artifact_bindings', 'check-ci-artifact-bindings.py')


class BindingTests(unittest.TestCase):
    def test_satellite_contract_has_positive_and_negative_controls(self):
        self.assertEqual(0, CHECK.self_test())

    def test_all_five_shared_graphs_bind_every_transfer(self):
        for filename in CHECK.ROOTS:
            if filename == 'ci.yml':
                continue
            with self.subTest(filename=filename):
                self.assertEqual([], CHECK.errors((ROOT / '.github/workflows' / filename).read_text(), filename))

    def test_checker_rejects_a_dropped_identity_direct_upload_or_swallowed_failure(self):
        filename = 'node-repo-module-pack.yml'
        text = (ROOT / '.github/workflows' / filename).read_text()
        mutations = [
            text.replace('expected-store-id: ${{ needs.select.outputs.artifact-store-id }}', 'expected-store-id: ', 1),
            text.replace('Systemorph/MeshWeaver/.github/actions/upload-artifact@main', 'actions/upload-artifact@v7', 1),
            text.replace('uses: Systemorph/MeshWeaver/.github/actions/download-artifact@main',
                         'continue-on-error: true\n        uses: Systemorph/MeshWeaver/.github/actions/download-artifact@main', 1),
            text.replace("needs.select.outputs.artifact-store == 'gha'", "true", 1),
            text.replace('artifact-store-id: ${{ steps.store.outputs.store-id }}', 'artifact-store-id: ', 1),
            text.replace("store: ${{ inputs.artifact-store || 'gha' }}", 'store: ${{ inputs.artifact-store || vars.MW_ARTIFACT_STORE }}', 1),
            text.replace("require-expected-store-id: 'true'", "require-expected-store-id: 'false'", 1),
        ]
        for index, mutation in enumerate(mutations):
            with self.subTest(index=index):
                self.assertTrue(CHECK.errors(mutation, filename))


if __name__ == '__main__':
    unittest.main(verbosity=2)
