#!/usr/bin/env python3
"""Exercise install-observability.sh's recovery paths without a cluster or credentials.

🚨 WHY THIS EXISTS. On 2026-10-15 an estate's Infra deploy failed on every run, for the same two
reasons in a loop: its Prometheus crash-looped on a FULL volume (`preallocate: no space left on
device` while replaying the WAL — size retention never gets to run), so `helm upgrade --wait` never
saw it Ready, outlived `az aks command invoke`'s ~10-minute cap, was killed, and left the release
`pending-upgrade` — which made helm refuse the next run outright. The installer now recovers both
and never waits inside helm. Each path below runs the REAL script with helm and kubectl stubbed on
PATH, and asserts the order and shape of the calls it makes.

Every recovery case has its negative control: a deployed release is not rolled back, a Prometheus
that crashed for another reason is not touched, and a recovery that fails is RED, not skipped.

The TSDB clean-up the recovery pod runs is EXECUTED here too, against a scratch directory with a
stubbed `df`: WAL and head chunks go, then the OLDEST blocks until the volume is under target.
"""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import textwrap
import unittest
import yaml

SCRIPT = Path(os.environ.get('INSTALLER_UNDER_TEST',
                             Path(__file__).with_name('install-observability.sh'))).resolve()

# One stub serves as both `helm` and `kubectl`; it logs every call and answers by SCENARIO.
STUB = r'''#!/usr/bin/env python3
import os, sys
tool = os.path.basename(sys.argv[0]); args = sys.argv[1:]; line = ' '.join(args)
scenario = set(os.environ.get('SCENARIO', '').split(','))
with open(os.environ['CALLS'], 'a') as f:
    f.write(tool + ' ' + line + '\n')
def out(text): sys.stdout.write(text)
if tool == 'helm':
    if args[:1] == ['status']:
        status = 'deployed'
        for s in ('pending-upgrade', 'pending-install', 'pending-rollback'):
            if s in scenario: status = s
        out('NAME: loki\nLAST DEPLOYED: Thu Oct 15 05:24:07 2026\nNAMESPACE: monitoring\nSTATUS: %s\nREVISION: 10\n' % status)
    elif args[:1] == ['history']:
        out('REVISION\tUPDATED                 \tSTATUS         \tCHART            \tAPP VERSION\tDESCRIPTION\n')
        if 'never-deployed' not in scenario:
            out('7       \tSun Oct 11 08:00:29 2026\tsuperseded     \tloki-stack-2.10.3\tv2.9.3     \tUpgrade complete\n')
            out('8       \tTue Oct 13 17:01:59 2026\tdeployed       \tloki-stack-2.10.3\tv2.9.3     \tUpgrade complete\n')
            out('9       \tThu Oct 15 05:10:25 2026\tfailed         \tloki-stack-2.10.3\tv2.9.3     \tUpgrade "loki" failed: context deadline exceeded\n')
        out('10      \tThu Oct 15 05:24:07 2026\tpending-upgrade\tloki-stack-2.10.3\tv2.9.3     \tPreparing upgrade\n')
    elif args[:1] == ['rollback'] and 'rollback-fails' in scenario:
        sys.exit(1)
    sys.exit(0)
# kubectl
if 'apply' in args:
    with open(os.environ['MANIFEST'], 'w') as f: f.write(sys.stdin.read())
    sys.exit(0)
if 'wait' in args:
    sys.exit(1 if 'recovery-fails' in scenario else 0)
if 'rollout' in args:
    sys.exit(1 if ('prometheus-not-ready' in scenario and 'loki-prometheus-server' in line) else 0)
if 'get deploy loki-prometheus-server' in line or 'get deploy/loki-prometheus-server' in line:
    if 'no-prometheus' in scenario: sys.exit(1)
    if 'go-template' in line: out('app=prometheus,component=server,release=loki,')
    elif '.image' in line: out('quay.io/prometheus/prometheus:v2.41.0')
    elif 'nodeSelector' in line: out('{"agentpool":"system"}')
    elif 'tolerations' in line: pass
    elif 'claimName' in line: out('loki-prometheus-server')
    elif 'replicas' in line: out('1')
    sys.exit(0)
if 'get pods -l' in line:
    if '-o name' in line:
        if 'slow-list' in scenario:
            import time; time.sleep(3); sys.exit(1)        # a slow API server that then fails
        if 'list-error' in scenario: sys.exit(1)
        if 'termination-timeout' in scenario: out('pod/loki-prometheus-server-abc')
        sys.exit(0)                            # terminated once scaled to 0
    out('loki-prometheus-server-abc')
    sys.exit(0)
if 'get pod loki-prometheus-server-abc' in line:
    if 'recovered' in scenario: out('253 true')          # crashed once, Ready now
    elif 'out-of-space' in scenario or 'other-crash' in scenario: out('253 false')
    else: out('0 true')
    sys.exit(0)
if 'get pod loki-prometheus-tsdb-recovery' in line:
    deleted = 'delete pod loki-prometheus-tsdb-recovery' in open(os.environ['CALLS']).read()
    if 'leftover-stuck' in scenario or ('leftover-cleaner' in scenario and not deleted):
        out('pod/loki-prometheus-tsdb-recovery')
        sys.exit(0)
    if 'teardown-stuck' in scenario and os.path.exists(os.environ['MANIFEST']):
        out('pod/loki-prometheus-tsdb-recovery')   # present only once it was created
    sys.exit(0)
if 'logs loki-prometheus-server-abc' in line:
    if 'out-of-space' in scenario or 'recovered' in scenario:
        out('ts=2026-10-15 msg="Replaying WAL" segment=175 maxSegment=432\n'
            'panic: preallocate: no space left on device\n' + 'goroutine trace\n' * 2000)
    elif 'other-crash' in scenario:
        out('panic: something else entirely\n')
    sys.exit(0)
if 'get deploy,sts,ds' in line:
    if 'workload-list-fails' in scenario and 'release=' in line: sys.exit(1)
    if 'app.kubernetes.io/instance' in line:
        out('deployment.apps/loki-grafana\nstatefulset.apps/loki\n')
    else:
        out('deployment.apps/loki-prometheus-server\nstatefulset.apps/loki\n')
    sys.exit(0)
if 'volumeMounts' in line: out('storage'); sys.exit(0)
if 'persistentVolumeClaim.claimName' in line: out('storage-loki-0'); sys.exit(0)
if 'status.phase' in line: out('Bound'); sys.exit(0)
sys.exit(0)
'''


class Run:
    def __init__(self, result, calls, manifest):
        self.result, self.calls, self.manifest = result, calls, manifest
        self.stdout, self.stderr, self.code = result.stdout, result.stderr, result.returncode

    def index(self, needle):
        for i, call in enumerate(self.calls):
            if needle in call:
                return i
        return -1


def run_installer(*scenario, budget=None):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        for name in ('helm', 'kubectl'):
            (root / name).write_text(STUB)
            (root / name).chmod(0o755)
        (root / 'values.yaml').write_text('loki: {}\n')
        calls, manifest = root / 'calls', root / 'manifest'
        env = dict(os.environ, PATH=str(root) + os.pathsep + os.environ['PATH'],
                   GRAFANA_PW='synthetic-test-only', VALUES=str(root / 'values.yaml'),
                   SCENARIO=','.join(scenario), CALLS=str(calls), MANIFEST=str(manifest))
        if budget is not None:
            env['OBS_BUDGET_SECONDS'] = str(budget)
        result = subprocess.run(['bash', str(SCRIPT)], env=env, capture_output=True, text=True,
                                timeout=90)
        return Run(result,
                   calls.read_text().splitlines() if calls.exists() else [],
                   manifest.read_text() if manifest.exists() else '')


class StuckReleaseTest(unittest.TestCase):
    def test_pending_upgrade_rolls_back_to_last_deployed_then_upgrades(self):
        run = run_installer('pending-upgrade')
        self.assertEqual(run.code, 0, run.stderr)
        self.assertIn('RECOVERY:', run.stdout)
        rollback, upgrade = run.index('helm rollback loki 8 '), run.index('helm upgrade --install')
        self.assertGreaterEqual(rollback, 0, run.calls)
        self.assertGreater(upgrade, rollback, run.calls)

    def test_pending_rollback_is_recovered_the_same_way(self):
        run = run_installer('pending-rollback')
        self.assertEqual(run.code, 0, run.stderr)
        self.assertGreaterEqual(run.index('helm rollback loki 8 '), 0, run.calls)

    def test_pending_install_never_deployed_is_uninstalled(self):
        run = run_installer('pending-install', 'never-deployed')
        self.assertEqual(run.code, 0, run.stderr)
        self.assertGreater(run.index('helm upgrade --install'), run.index('helm uninstall loki'))
        self.assertGreaterEqual(run.index('helm uninstall loki'), 0, run.calls)

    def test_pending_upgrade_without_deployed_revision_is_red(self):
        run = run_installer('pending-upgrade', 'never-deployed')
        self.assertEqual(run.code, 1, run.stdout)
        self.assertIn('FATAL:', run.stderr)
        self.assertEqual(run.index('helm upgrade --install'), -1, run.calls)

    def test_failed_rollback_is_red_and_does_not_upgrade(self):
        run = run_installer('pending-upgrade', 'rollback-fails')
        self.assertEqual(run.code, 1, run.stdout)
        self.assertIn('FATAL:', run.stderr)
        self.assertEqual(run.index('helm upgrade --install'), -1, run.calls)

    def test_deployed_release_is_not_rolled_back(self):
        run = run_installer()
        self.assertEqual(run.code, 0, run.stderr)
        self.assertEqual(run.index('helm rollback'), -1, run.calls)
        self.assertNotIn('RECOVERY:', run.stdout)


class LeftoverCleanerTest(unittest.TestCase):
    def test_a_leftover_cleaner_is_removed_before_anything_else(self):
        run = run_installer('pending-upgrade', 'leftover-cleaner')
        self.assertEqual(run.code, 0, run.stderr)
        delete = run.index('delete pod loki-prometheus-tsdb-recovery')
        self.assertGreaterEqual(delete, 0, run.calls)
        self.assertLess(delete, run.index('helm rollback'), run.calls)
        self.assertLess(delete, run.index('helm upgrade'), run.calls)

    def test_a_leftover_cleaner_that_stays_blocks_rollback_and_upgrade(self):
        # The retry after a failed teardown: the server is at 0, so there is no crash evidence —
        # the preflight alone must stop helm from restoring a server onto the cleaner's disk.
        for scenario in (('leftover-stuck',), ('leftover-stuck', 'pending-upgrade')):
            with self.subTest(scenario=scenario):
                run = run_installer(*scenario)
                self.assertEqual(run.code, 1, run.stdout)
                self.assertIn('could not be confirmed gone', run.stderr)
                self.assertEqual(run.index('helm rollback'), -1, run.calls)
                self.assertEqual(run.index('helm upgrade'), -1, run.calls)
                self.assertEqual(run.index('scale deploy'), -1, run.calls)


class BoundedUpgradeTest(unittest.TestCase):
    def test_every_stage_shares_one_deadline(self):
        # The worst case: a stuck release AND a full volume. The bounds of every waiting stage,
        # added up, stay inside the budget — so the invoke cap never cuts a stage off.
        run = run_installer('pending-upgrade', 'out-of-space')
        self.assertEqual(run.code, 0, run.stderr)
        waits = [int(m.group(1)) for c in run.calls
                 for m in [re.search(r'--timeout[= ](\d+)s', c)] if m and 'rollout status' not in c
                 and 'delete pod' not in c]
        self.assertGreaterEqual(len(waits), 3, run.calls)    # rollback, recovery pod, upgrade
        # + up to 120 s waiting for the server to stop (a polled loop, not a call) + 60 s kept back.
        self.assertLessEqual(sum(waits) + 120, 540 - 60, (waits, run.calls))

    def test_a_spent_budget_starts_nothing_and_is_red(self):
        run = run_installer('pending-upgrade', budget=100)
        self.assertEqual(run.code, 1, run.stdout)
        self.assertIn('budget is spent', run.stderr)
        self.assertEqual(run.index('helm rollback'), -1, run.calls)
        self.assertEqual(run.index('helm upgrade'), -1, run.calls)

    def test_a_failed_workload_listing_is_red(self):
        run = run_installer('workload-list-fails')
        self.assertEqual(run.code, 1, run.stdout)
        self.assertIn('readiness was NOT established', run.stderr)

    def test_upgrade_never_waits_inside_helm_and_is_bounded(self):
        run = run_installer()
        upgrade = run.calls[run.index('helm upgrade --install')]
        self.assertNotIn('--wait', upgrade)
        self.assertNotIn('--atomic', upgrade)
        self.assertRegex(upgrade, r'--timeout \d+s')

    def test_readiness_is_checked_per_workload_after_the_upgrade(self):
        run = run_installer()
        self.assertEqual(run.code, 0, run.stderr)
        upgrade = run.index('helm upgrade --install')
        rollouts = [i for i, c in enumerate(run.calls) if 'rollout status' in c]
        self.assertEqual(len(rollouts), 3, run.calls)    # grafana, loki, prometheus-server — deduplicated
        self.assertTrue(all(i > upgrade for i in rollouts))
        for i in rollouts:
            seconds = int(re.search(r'--timeout=(\d+)s', run.calls[i]).group(1))
            self.assertLess(seconds, 600, run.calls[i])
        self.assertIn('All workloads of release loki are Ready.', run.stdout)

    def test_a_workload_that_is_not_ready_exits_2_and_names_it(self):
        run = run_installer('prometheus-not-ready')
        self.assertEqual(run.code, 2, run.stdout + run.stderr)
        self.assertIn('NOT READY', run.stderr)
        self.assertIn('deployment.apps/loki-prometheus-server', run.stderr)
        self.assertIn('Verified:', run.stdout)          # storage was still verified
        self.assertNotIn('All workloads', run.stdout)


class FullTsdbTest(unittest.TestCase):
    def test_out_of_space_crash_clears_the_tsdb_before_the_upgrade(self):
        run = run_installer('out-of-space')
        self.assertEqual(run.code, 0, run.stderr)
        self.assertIn('DISPOSABLE METRICS', run.stdout)
        down = run.index('scale deploy loki-prometheus-server --replicas=0')
        apply_ = run.index('apply -f -')
        up = run.index('scale deploy loki-prometheus-server --replicas=1')
        upgrade = run.index('helm upgrade --install')
        self.assertTrue(0 <= down < apply_ < up < upgrade, run.calls)
        self.assertIn('claimName: loki-prometheus-server', run.manifest)
        self.assertIn('image: quay.io/prometheus/prometheus:v2.41.0', run.manifest)
        self.assertIn('nodeSelector: {"agentpool":"system"}', run.manifest)
        self.assertIn('rm -rf wal chunks_head', run.manifest)
        pod = yaml.safe_load(run.manifest)                       # it must be a real Pod, not just text
        self.assertEqual(pod['kind'], 'Pod')
        self.assertEqual(pod['spec']['restartPolicy'], 'Never')
        self.assertEqual(pod['spec']['nodeSelector'], {'agentpool': 'system'})
        self.assertEqual(pod['spec']['tolerations'], [])
        self.assertTrue(pod['spec']['securityContext']['runAsNonRoot'])
        self.assertIn('rm -rf wal chunks_head', pod['spec']['containers'][0]['args'][0])

    def test_other_crash_is_not_touched(self):
        run = run_installer('other-crash')
        self.assertEqual(run.code, 0, run.stderr)
        self.assertEqual(run.index('scale deploy'), -1, run.calls)
        self.assertEqual(run.index(' apply '), -1, run.calls)

    def test_healthy_recovered_or_absent_prometheus_is_not_touched(self):
        # 'recovered': restarts and an out-of-space previous log, but Ready NOW — history only.
        for scenario in ((), ('no-prometheus',), ('recovered',)):
            with self.subTest(scenario=scenario):
                run = run_installer(*scenario)
                self.assertEqual(run.code, 0, run.stderr)
                self.assertEqual(run.index('scale deploy'), -1, run.calls)

    def test_unconfirmed_termination_restores_the_server_and_never_cleans(self):
        # Still listed, or the listing itself failed: neither proves the server stopped.
        for scenario in ('termination-timeout', 'list-error'):
            with self.subTest(scenario=scenario):
                run = run_installer('out-of-space', scenario, budget=185)   # a ~35 s stop deadline
                self.assertEqual(run.code, 1, run.stdout)
                self.assertIn('NOT touched', run.stderr)
                self.assertEqual(run.index('apply -f -'), -1, run.calls)
                self.assertGreater(run.index('--replicas=1'), run.index('--replicas=0'), run.calls)

    def test_slow_listings_are_held_to_the_absolute_stop_deadline(self):
        # Budget 185 s leaves a ~35 s stop deadline (150 s kept back). Each listing takes 3 s; the
        # loop must end on wall-clock time, not on a count of its sleeps, and restore the server.
        started = time.monotonic()
        run = run_installer('out-of-space', 'slow-list', budget=185)
        elapsed = time.monotonic() - started
        self.assertEqual(run.code, 1, run.stdout)
        self.assertRegex(run.stderr, r'stopped within 3[0-5]s')
        self.assertLess(elapsed, 35 + 12, elapsed)
        self.assertEqual(run.index('apply -f -'), -1, run.calls)
        self.assertGreaterEqual(run.index('--replicas=1'), 0, run.calls)
        for call in run.calls:
            if 'get pods -l' in call and '-o name' in call:
                self.assertLessEqual(int(re.search(r'--request-timeout=(\d+)s', call).group(1)), 35)

    def test_cleaner_not_confirmed_gone_leaves_the_server_at_zero(self):
        run = run_installer('out-of-space', 'recovery-fails', 'teardown-stuck')
        self.assertEqual(run.code, 1, run.stdout)
        self.assertIn('LEFT AT 0', run.stderr)
        self.assertEqual(run.index('--replicas=1'), -1, run.calls)
        # diagnostics were collected before the delete
        self.assertLess(run.index('describe pod loki-prometheus-tsdb-recovery'),
                        max(i for i, c in enumerate(run.calls) if 'delete pod loki-prometheus-tsdb-recovery' in c))

    def test_failed_recovery_is_red_and_scales_back(self):
        run = run_installer('out-of-space', 'recovery-fails')
        self.assertEqual(run.code, 1, run.stdout)
        self.assertIn('FATAL:', run.stderr)
        self.assertGreaterEqual(run.index('--replicas=1'), 0, run.calls)
        self.assertEqual(run.index('helm upgrade --install'), -1, run.calls)


class TsdbCleanupScriptTest(unittest.TestCase):
    """The script the recovery pod runs, executed against a scratch TSDB with a stubbed df."""

    # df reports 15% per remaining block and 40% for a WAL — enough to make the order observable.
    DF = textwrap.dedent('''\
        #!/bin/sh
        dir="$2"; used=0
        [ -d "$dir/wal" ] && used=40
        for b in "$dir"/0*; do [ -d "$b" ] && used=$((used + 15)); done
        [ "$used" -gt 100 ] && used=100
        echo "Filesystem 1024-blocks Used Available Capacity Mounted on"
        echo "/dev/sdx 8000000 0 0 $used% $dir"
        ''')
    BLOCKS = ['01K0000000000000000000000A', '01K0000000000000000000000B', '01K0000000000000000000000C',
              '01K0000000000000000000000D', '01K0000000000000000000000E']

    def cleanup_script(self):
        manifest = run_installer('out-of-space').manifest
        body = manifest.split('        - |\n', 1)[1].split('      volumeMounts:', 1)[0]
        return textwrap.dedent(body)

    def run_cleanup(self, blocks, target, min_times=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            tsdb, bin_ = root / 'data', root / 'bin'
            tsdb.mkdir(); bin_.mkdir()
            (bin_ / 'df').write_text(self.DF); (bin_ / 'df').chmod(0o755)
            for name in ['wal', 'chunks_head', '01K0000000000000000000000F.tmp-for-creation', *blocks]:
                (tsdb / name).mkdir()
            for i, block in enumerate(blocks):
                start = (min_times or {}).get(block, 1_700_000_000_000 + i * 7_200_000)
                (tsdb / block / 'meta.json').write_text(
                    '{\n\t"ulid": "%s",\n\t"minTime": %d,\n\t"maxTime": %d,\n\t"compaction": {"parents": '
                    '[{"ulid": "X", "minTime": 1, "maxTime": 2}]}\n}\n' % (block, start, start + 7_200_000))
            (tsdb / 'lock').write_text('')
            (tsdb / 'queries.active').write_text('')
            env = dict(os.environ, PATH=str(bin_) + os.pathsep + os.environ['PATH'],
                       TSDB_DIR=str(tsdb), TARGET_PERCENT=str(target))
            result = subprocess.run(['sh', '-c', self.cleanup_script()], env=env,
                                    capture_output=True, text=True, timeout=30)
            return result, sorted(p.name for p in tsdb.iterdir())

    def test_wal_goes_then_oldest_blocks_until_under_target(self):
        result, left = self.run_cleanup(self.BLOCKS, 60)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        # 5 blocks = 75% after the WAL is gone; dropping the oldest one reaches 60%.
        self.assertEqual(left, [*self.BLOCKS[1:], 'queries.active'])

    def test_order_is_by_sample_age_not_ulid(self):
        # Compaction minted the NEWEST ULID (E) for the OLDEST range: it goes first, A survives.
        result, left = self.run_cleanup(self.BLOCKS, 60, min_times={self.BLOCKS[4]: 1_600_000_000_000})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(left, [*self.BLOCKS[:4], 'queries.active'])

    def test_already_under_target_keeps_every_block(self):
        result, left = self.run_cleanup(self.BLOCKS[:2], 60)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(left, [*self.BLOCKS[:2], 'queries.active'])

    def test_unreachable_target_is_red(self):
        result, left = self.run_cleanup(self.BLOCKS, -1)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertEqual(left, ['queries.active'])


if __name__ == '__main__':
    unittest.main()
