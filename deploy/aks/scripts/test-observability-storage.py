#!/usr/bin/env python3
"""Exercise the installer's live-storage verdict without a cluster or credentials."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(os.environ.get('INSTALLER_UNDER_TEST', Path(__file__).with_name('install-observability.sh'))).resolve()


class StorageVerdictTest(unittest.TestCase):
    def run_installer(self, scenario):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'helm').write_text('#!/bin/sh\nexit 0\n')
            (root / 'kubectl').write_text('''#!/bin/sh
case "$*" in
  *volumeMounts*)
    [ "$SCENARIO" = read_error ] && exit 9
    [ "$SCENARIO" = missing_mount ] && exit 0
    printf storage ;;
  *persistentVolumeClaim.claimName*)
    [ "$SCENARIO" = claim_error ] && exit 9
    [ "$SCENARIO" = empty_dir ] && exit 0
    printf storage-loki-0 ;;
  *status.phase*)
    [ "$SCENARIO" = pvc_error ] && exit 9
    if [ "$SCENARIO" = pending ]; then printf Pending; else printf Bound; fi ;;
  *'get pods'*) printf 'loki-0 Running\\n' ;;
  *) exit 8 ;;
esac
''')
            for name in ('helm', 'kubectl'):
                (root / name).chmod(0o755)
            (root / 'values.yaml').write_text('loki: {}\n')
            env = dict(os.environ, PATH=str(root) + os.pathsep + os.environ['PATH'],
                       GRAFANA_PW='synthetic-test-only', VALUES=str(root / 'values.yaml'),
                       SCENARIO=scenario)
            return subprocess.run(['bash', str(SCRIPT)], env=env, capture_output=True,
                                  text=True, timeout=10)

    def test_bound_claim_passes(self):
        result = self.run_installer('bound')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('Verified:', result.stdout)

    def test_ephemeral_or_unverified_storage_fails(self):
        for scenario in ('read_error', 'missing_mount', 'claim_error', 'empty_dir', 'pvc_error', 'pending'):
            with self.subTest(scenario=scenario):
                result = self.run_installer(scenario)
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn('FATAL:', result.stderr)
                self.assertNotIn('Verified:', result.stdout)


if __name__ == '__main__':
    unittest.main()
