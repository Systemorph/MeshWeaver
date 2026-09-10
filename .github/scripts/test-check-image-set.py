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


class ImageSetTests(unittest.TestCase):
    def check(self, failed_tag="", diagnostic="", exit_code=0, index=INDEX):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            az = root / "az"
            az.write_text("""#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
tag = sys.argv[sys.argv.index('--name') + 1]
with Path(os.environ['CALLS']).open('a') as log:
    log.write(tag + '\\n')
if tag == os.environ['FAILED_TAG']:
    print(os.environ['DIAGNOSTIC'], file=sys.stderr)
    sys.exit(int(os.environ['EXIT_CODE']))
print(os.environ['INDEX'])
""")
            az.chmod(0o755)
            env = {**os.environ, "PATH": str(root) + os.pathsep + os.environ["PATH"],
                   "FAILED_TAG": failed_tag, "DIAGNOSTIC": diagnostic,
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
        for tag in ("memex-migration:main", "memex-portal-ai:abcdef1-p1234567"):
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


if __name__ == "__main__":
    unittest.main()
