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
    "memex-portal-ai:main", "memex-migration:main", "mw-plugin-test:main",
    "mw-plugin-test:latest",
    "memex-migration:3.0.0-ci.42", "mw-plugin-test:3.0.0-ci.42",
}
# 🚨 The portal's version tag is the ARMING write (main-cd `arm`, policy `one-promotion-gate`): the
# set's verification must never ask for it, or every promoted set waiting for its dependent-suites
# verdict would read as a torn delivery.
ARMING_TAG = "memex-portal-ai:3.0.0-ci.42"


class ImageSetTests(unittest.TestCase):
    def check(self, failed_tag="", diagnostic="", exit_code=0, index=INDEX, failed_tags=(),
              args=("abcdef1", "--pointers", "3.0.0-ci.42")):
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
            result = subprocess.run(["bash", str(SCRIPT), *args],
                                    capture_output=True, text=True, env=env, timeout=15)
            log = root / "calls"
            calls = log.read_text().splitlines() if log.exists() else []
            return result, calls

    def test_full_set_checks_every_identity_and_pointer(self):
        result, calls = self.check()
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        self.assertEqual(len(calls), 9)
        self.assertEqual(set(calls), EXPECTED_TAGS)
        self.assertIn("All images exist", result.stdout)

    def test_registry_errors_remain_red_and_keep_the_actual_diagnostic(self):
        for tag in ("memex-migration:main", "memex-portal-ai:abcdef1"):
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
                    self.assertEqual(len(calls), 9, "a failed read must not hide later checks")
                    self.assertEqual(set(calls), EXPECTED_TAGS)

    def test_the_portal_version_tag_is_never_asserted_it_is_the_arming_write(self):
        result, calls = self.check()
        self.assertNotIn(ARMING_TAG, calls)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_single_architecture_still_fails(self):
        result, _ = self.check(index={"manifests": INDEX["manifests"][:1]})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("not a linux amd64+arm64 image index", result.stdout)

    # ── the retired pair tag (policy `platform-module-deploy-separate`) ─────────────────────
    #
    # main-cd no longer mints `memex-portal-ai:<sha>-p<plugins>`, so "the set" must never ask for it:
    # a checker that still did would read every new set as stale and rebuild on every Plugins merge.

    def test_no_pair_tag_is_ever_asked_for(self):
        result, calls = self.check()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse([c for c in calls if "-p" in c.split(":", 1)[1]], calls)
        self.assertNotIn("::notice::", result.stdout)

    def test_a_second_positional_argument_is_refused_red_not_silently_ignored(self):
        # The negative control on the retirement: a stale caller still passing the plugins sha must
        # be told, RED, before any registry read — never answered about a different question.
        result, calls = self.check(args=("abcdef1", "1234567", "--pointers", "3.0.0-ci.42"))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("retired", result.stdout)
        self.assertEqual(calls, [], "a refused invocation must read nothing")

    def test_a_missing_image_is_still_red(self):
        result, _ = self.check("memex-migration:abcdef1", "ERROR: MANIFEST_UNKNOWN: tag does not exist", 3)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("::error::", result.stdout)


if __name__ == "__main__":
    unittest.main()
