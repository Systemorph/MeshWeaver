#!/usr/bin/env python3
"""Exercise check-observability-values.sh's verdicts without the network or a container runtime.

🚨 WHY THIS EXISTS. The checker it tests is itself a gate, and a gate that has quietly stopped
being able to fail is worse than no gate — it reports a tick nobody re-examines. That is not
hypothetical here: the FIRST version of check-observability-values.py did exactly that. It compared
the values against the rendered config and passed a deliberately mis-nested `ingestor:` key,
because loki-stack merges `loki.config` through VERBATIM, so an unknown key arrives in the render
intact. The check could not fail for the one component #3773 was about. That is what the binary
half is for, and this file is what stops the pair regressing to that state silently.

Every case below is a control that MUST be red. `test_clean_values_pass` is the positive control:
without it, a checker that failed everything would pass this file.

helm and docker are stubbed on PATH, so this runs anywhere, offline, in about a second — the same
technique as test-observability-storage.py. What it CANNOT tell you is whether the real chart still
renders these keys or the real Loki binary still accepts them; that is the gate itself, in CI.
"""
import base64
import json
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest
import yaml

SCRIPT = Path(os.environ.get('CHECKER_UNDER_TEST',
                             Path(__file__).with_name('check-observability-values.sh'))).resolve()

# A minimal but STRUCTURALLY REAL render: the two Secrets the checker resolves, in the two
# different encodings loki-stack actually uses (loki base64 under `data`, promtail plaintext under
# `stringData`). A fixture that used one encoding for both would let an encoding bug pass.
LOKI_CONFIG = {
    'ingester': {'wal': {'enabled': True, 'dir': '/data/loki/wal', 'flush_on_shutdown': True}},
    'compactor': {'retention_enabled': True},
    'limits_config': {'retention_period': '720h'},
}
PROMTAIL_CONFIG = {'clients': [{'url': 'http://loki:3100/loki/api/v1/push'}]}


def manifest(loki_config=None, promtail_config=None, omit_loki=False):
    documents = []
    if not omit_loki:
        documents.append({
            'kind': 'Secret', 'metadata': {'name': 'loki'},
            'data': {'loki.yaml': base64.b64encode(
                yaml.safe_dump(LOKI_CONFIG if loki_config is None else loki_config).encode()
            ).decode()},
        })
    documents.append({
        'kind': 'Secret', 'metadata': {'name': 'loki-promtail'},
        'stringData': {'promtail.yaml': yaml.safe_dump(
            PROMTAIL_CONFIG if promtail_config is None else promtail_config)},
    })
    return '\n---\n'.join(yaml.safe_dump(d) for d in documents)


class CheckerVerdictTest(unittest.TestCase):
    def run_checker(self, values, *, rendered=None, binary_valid=True, binary_exit=0,
                    app_version='2.9.3'):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'render.yaml').write_text(rendered if rendered is not None else manifest())
            (root / 'helm').write_text(textwrap.dedent(f'''\
                #!/bin/sh
                case "$1" in
                  template) cat "{root}/render.yaml" ;;
                  show) printf 'apiVersion: v2\\nname: loki-stack\\nversion: 2.10.3\\nappVersion: v{app_version}\\n' ;;
                  repo) exit 0 ;;
                  *) exit 0 ;;
                esac
                '''))
            # The docker stub CONSUMES stdin: the checker pipes the rendered config into the
            # container, and a stub that did not read it would leave the pipe unexamined and pass
            # a checker that had stopped sending anything.
            (root / 'docker').write_text(textwrap.dedent(f'''\
                #!/bin/sh
                case "$1" in
                  info) exit 0 ;;
                  run)
                    piped=$(cat)
                    [ -n "$piped" ] || {{ echo "stub: nothing was piped in" >&2; exit 3; }}
                    {'echo "level=info msg=\\"config is valid\\""' if binary_valid else 'echo "failed parsing config: field ingestor not found in type loki.ConfigWrapper" >&2'}
                    exit {binary_exit} ;;
                esac
                exit 0
                '''))
            for name in ('helm', 'docker'):
                (root / name).chmod(0o755)
            values_path = root / 'values.yaml'
            values_path.write_text(yaml.safe_dump(values))
            env = dict(os.environ, PATH=str(root) + os.pathsep + os.environ['PATH'])
            return subprocess.run(['bash', str(SCRIPT), str(values_path)], env=env,
                                  capture_output=True, text=True, timeout=60)

    # The values the repository actually ships, in shape: every leaf reaches the render.
    CLEAN = {'loki': {'config': LOKI_CONFIG}}

    def test_clean_values_pass(self):
        """POSITIVE CONTROL. Without this, a checker that failed everything would pass this file."""
        result = self.run_checker(self.CLEAN)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('Verified:', result.stdout)

    def test_orphaned_key_fails(self):
        """The promtail.config.wal shape: helm accepts the key, the chart templates nothing."""
        values = {'loki': {'config': LOKI_CONFIG},
                  'promtail': {'config': {'wal': {'enabled': True}}}}
        result = self.run_checker(values)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('reaches NOTHING', result.stdout)
        self.assertIn('promtail.config.wal.enabled', result.stdout)

    def test_overridden_value_fails(self):
        """Set, arrives, but the chart wins — invisible to a presence-only check."""
        overridden = json.loads(json.dumps(LOKI_CONFIG))
        overridden['limits_config']['retention_period'] = '24h'
        result = self.run_checker(self.CLEAN, rendered=manifest(loki_config=overridden))
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('the chart overrode it', result.stdout)

    def test_binary_rejection_fails(self):
        """🚨 THE CASE THE RENDER CHECK CANNOT SEE — loki-stack merges `loki.config` verbatim, so a
        mis-nested key arrives intact and every leaf 'reaches' the render. Only the binary knows."""
        result = self.run_checker(self.CLEAN, binary_valid=False, binary_exit=1)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('REJECTED by grafana/loki:', result.stdout)

    def test_binary_silence_fails(self):
        """Exit 0 without the verdict line means the validator never ran. Silence is not success."""
        result = self.run_checker(self.CLEAN, binary_valid=False, binary_exit=0)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('never said `config is valid`', result.stdout)

    def test_missing_loki_document_fails(self):
        """No Secret/loki means nothing was validated — that is a failure, not an empty pass."""
        result = self.run_checker(self.CLEAN, rendered=manifest(omit_loki=True))
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('could not be validated against the binary', result.stdout)

    def test_empty_render_fails(self):
        """A render that produced nothing would make every key look present-by-absence."""
        result = self.run_checker(self.CLEAN, rendered='')
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('rendered NOTHING', result.stdout)

    def test_too_few_leaves_fails(self):
        """A values file that lost its config section must not report a pass on nothing."""
        result = self.run_checker({'loki': {'config': {'compactor': {'retention_enabled': True}}}})
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('is not evidence', result.stdout)

    def test_unknown_component_fails(self):
        """A component whose rendered config the checker cannot locate must not pass silently."""
        values = {'loki': {'config': LOKI_CONFIG}, 'grafana': {'config': {'anything': 1}}}
        result = self.run_checker(values)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn('does not know where', result.stdout)


if __name__ == '__main__':
    unittest.main()
