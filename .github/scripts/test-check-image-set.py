#!/usr/bin/env python3
"""Run the real image-set checker against registry success and failure responses."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("check-image-set.sh")
INDEX = {"manifests": [
    {"platform": {"os": "linux", "architecture": architecture}}
    for architecture in ("amd64", "arm64")
]}
EXPECTED_TAGS = {
    "memex-portal-ai:abcdef1", "memex-migration:abcdef1", "mw-plugin-test:abcdef1",
    "memex-portal-ai:abcdef1-p1234567",
    "memex-portal-ai:main", "memex-migration:main", "mw-plugin-test:main",
    "mw-plugin-test:latest",
    "memex-portal-ai:3.0.0-ci.42", "memex-migration:3.0.0-ci.42", "mw-plugin-test:3.0.0-ci.42",
}


PAIR_TAG = "memex-portal-ai:abcdef1-p1234567"


class ImageSetTests(unittest.TestCase):
    def check(self, failed_tag="", diagnostic="", exit_code=0, index=INDEX, failed_tags=()):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            az = root / "az"
            az.write_text("""#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
tag = sys.argv[sys.argv.index('--name') + 1]
with Path(os.environ['CALLS']).open('a') as log:
    log.write(tag + '\\n')
if tag in os.environ['FAILED_TAGS'].split(','):
    print(os.environ['DIAGNOSTIC'], file=sys.stderr)
    sys.exit(int(os.environ['EXIT_CODE']))
print(os.environ['INDEX'])
""")
            az.chmod(0o755)
            failed = ",".join(failed_tags) if failed_tags else failed_tag
            env = {**os.environ, "PATH": str(root) + os.pathsep + os.environ["PATH"],
                   "FAILED_TAGS": failed, "DIAGNOSTIC": diagnostic,
                   "EXIT_CODE": str(exit_code), "INDEX": json.dumps(index),
                   "CALLS": str(root / "calls"), "GITHUB_STEP_SUMMARY": str(root / "summary")}
            result = subprocess.run(["bash", str(SCRIPT), "abcdef1", "1234567", "--pointers", "3.0.0-ci.42"],
                                    capture_output=True, text=True, env=env, timeout=15)
            calls = (root / "calls").read_text().splitlines()
            return result, calls

    def test_full_set_checks_every_identity_and_pointer(self):
        result, calls = self.check()
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        self.assertEqual(len(calls), 11)
        self.assertEqual(set(calls), EXPECTED_TAGS)
        self.assertIn("All images exist", result.stdout)

    def test_registry_errors_remain_red_and_keep_the_actual_diagnostic(self):
        for tag in ("memex-migration:main", PAIR_TAG):
            for code, diagnostic in ((3, "ERROR: MANIFEST_UNKNOWN: tag does not exist"),
                                     (1, "ERROR: response status 503 Service Unavailable"),
                                     (2, "ERROR: registry operation was refused")):
                with self.subTest(tag=tag, code=code):
                    result, calls = self.check(tag, diagnostic, code)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn(diagnostic, result.stderr)
                    self.assertIn(f"az exit {code}", result.stdout)
                    self.assertNotIn("is MISSING", result.stdout)
                    self.assertNotIn("was NOT built", result.stdout)
                    self.assertEqual(calls.count(tag), 1, "a failed read must not be retried")
                    self.assertEqual(len(calls), 11, "a failed read must not hide later checks")
                    self.assertEqual(set(calls), EXPECTED_TAGS)

    def test_single_architecture_still_fails(self):
        result, _ = self.check(index={"manifests": INDEX["manifests"][:1]})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("not a linux amd64+arm64 image index", result.stdout)

    # ── deliverability vs provenance (MeshWeaver#4687) ──────────────────────────────────────
    #
    # 🚨 THE CASE THAT WOULD HAVE FAILED BEFORE THE FIX. A missing pair tag used to exit 1 —
    # the same answer a torn image set gives — so `gate` read "main's HEAD has no deployable
    # image" over a set every install could already roll to, and filed
    # `CD: main <sha> has an incomplete image set` saying so. 28 of the 109 such issues ever
    # opened were exactly this, including three for `0dadacc` in three consecutive hours on
    # 2026-09-17, each closed by a "successful heal" half an hour later.

    def test_a_missing_pair_tag_alone_is_exit_2_not_a_torn_set(self):
        result, calls = self.check(PAIR_TAG, "ERROR: MANIFEST_UNKNOWN: tag does not exist", 3)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("DELIVERABLE", result.stdout)
        self.assertEqual(set(calls), EXPECTED_TAGS, "every other identity is still asserted")

    def test_an_UNREADABLE_pair_read_is_exit_1_not_a_stale_pairing(self):
        # 🚨 Exit 2 PROMISES that everything else was verified and only the pairing is behind, and
        # `gate` acts on that promise — complete, no ledger entry, refresh. A 503 or a refused pull
        # establishes nothing about the tag, so reading it as "merely behind" would be the same
        # answer-that-reads-like-a-pass this change exists to remove, one layer down. Azure CLI
        # exits 3 for ResourceNotFoundError; anything else stays RED. (Copilot on MeshWeaver#4687.)
        for code, diagnostic in ((1, "ERROR: response status 503 Service Unavailable"),
                                 (2, "ERROR: registry operation was refused")):
            with self.subTest(code=code):
                result, _ = self.check(PAIR_TAG, diagnostic, code)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn("::error::", result.stdout)
                self.assertNotIn("::notice::", result.stdout)
                self.assertIn("could not be READ", result.stdout)

    def test_a_stale_pair_is_a_notice_never_an_error_annotation(self):
        # An `::error::` on a job that then succeeds is how a run's annotation list stops being
        # read. The condition is still printed, and still summarised — just not as a failure.
        result, _ = self.check(PAIR_TAG, "ERROR: MANIFEST_UNKNOWN: tag does not exist", 3)
        self.assertNotIn("::error::", result.stdout)
        self.assertIn("::notice::", result.stdout)
        self.assertIn("az exit 3", result.stdout)

    def test_a_missing_image_outranks_a_stale_pair(self):
        # 🚨 The ordering IS the contract: exit 2 asserts the set is intact, so a run that lost a
        # leg AND whose plugins HEAD moved must still exit 1. Without this, the fix would hand
        # `gate` "deliverable" over a torn set — the one failure the file exists to prevent.
        result, _ = self.check(
            failed_tags=("memex-migration:abcdef1", PAIR_TAG),
            diagnostic="ERROR: MANIFEST_UNKNOWN: tag does not exist", exit_code=3)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("::error::", result.stdout)

    def test_a_complete_and_correctly_paired_set_is_still_exit_0(self):
        # The inert control: without it, "a stale pair is exit 2" would also pass if the script
        # had started answering 2 unconditionally.
        result, _ = self.check()
        self.assertEqual(result.returncode, 0)
        self.assertNotIn("::notice::", result.stdout)


if __name__ == "__main__":
    unittest.main()
