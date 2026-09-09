"""Regression coverage for declaration gates reading corrected PR metadata."""

import copy
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "reader", Path(__file__).with_name("read-current-pr-body.py"))
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)


class CurrentPrBodyTest(unittest.TestCase):
    def setUp(self):
        self.pr = {"number": 3844, "head": {"sha": "a" * 40},
                   "base": {"repo": {"full_name": "Systemorph/MeshWeaver"}},
                   "body": "old body without the required declaration"}
        self.event = {"pull_request": copy.deepcopy(self.pr)}

    def read(self, current):
        def api(path):
            self.assertEqual(path, "repos/Systemorph/MeshWeaver/pulls/3844")
            return current
        return reader.read_body(self.event, "Systemorph/MeshWeaver", api)

    def test_corrected_body_replaces_frozen_event_and_preserves_literal_text(self):
        self.pr["body"] = "Mirror-sync: tracked in #3844\n`touch /tmp/unwanted` $(echo nope)\n"
        self.assertEqual(self.read(self.pr), self.pr["body"])
        self.assertNotEqual(self.pr["body"], self.event["pull_request"]["body"])

    def test_deleted_declaration_is_not_reused_from_event(self):
        self.event["pull_request"]["body"] = "Mirror-sync: tracked in #3844"
        for body in (None, ""):
            with self.subTest(body=body):
                self.pr["body"] = body
                self.assertEqual(self.read(self.pr), "")

    def test_new_head_cannot_authorize_an_obsolete_run(self):
        self.pr["head"]["sha"] = "b" * 40
        with self.assertRaisesRegex(ValueError, "head changed"):
            self.read(self.pr)

    def test_wrong_pr_and_wrong_repository_are_refused(self):
        for field in ("number", "repository"):
            with self.subTest(field=field):
                current = copy.deepcopy(self.pr)
                if field == "number":
                    current["number"] += 1
                else:
                    current["base"]["repo"]["full_name"] = "Other/Repo"
                with self.assertRaisesRegex(ValueError, "different pull request"):
                    self.read(current)

    def test_missing_or_malformed_body_is_not_an_empty_success(self):
        del self.pr["body"]
        with self.assertRaises(KeyError):
            self.read(self.pr)
        self.pr["body"] = {"unreadable": True}
        with self.assertRaises(ValueError):
            self.read(self.pr)

    def test_failed_rest_read_never_falls_back_to_event(self):
        def fail(_):
            raise subprocess.CalledProcessError(1, ["gh", "api"])
        with self.assertRaises(subprocess.CalledProcessError):
            reader.read_body(self.event, "Systemorph/MeshWeaver", fail)

    def test_failed_refresh_removes_previous_output_and_exits_red(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "body.md"
            output.write_text("Mirror-sync: stale declaration")
            with patch("sys.argv", ["read-current-pr-body.py", "--output", str(output)]), \
                 patch.dict("os.environ", {"GITHUB_EVENT_PATH": directory + "/absent.json"}), \
                 patch("builtins.print"):
                self.assertEqual(reader.main(), 1)
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
