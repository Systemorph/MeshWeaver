#!/usr/bin/env python3
"""Ledger reuse against real artifact files and the existing local MCP protocol fixture."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import select
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import urllib.request


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


LEDGER = load("module_build_ledger_store_test", "module-build-ledger.py")
ART = load("run_artifacts_ledger_test", "ci-run-artifacts.py")


class LedgerStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.share = self.root / "share"
        self.share.mkdir()
        self.spec = f"file:{self.share}"
        self.payload = self.root / "Acme.Alpha.1.0.module.nupkg"
        self.payload.write_bytes(b"this exact module bundle")
        self.digest = hashlib.sha256(self.payload.read_bytes()).hexdigest()
        self.source = ART.Artifacts(self.spec, "Systemorph/Acme", "12", 3)
        self.gh_marker = self.root / "github-was-called"
        gh = self.root / "gh"
        gh.write_text("#!/usr/bin/env python3\nimport json,pathlib,sys\n"
                      f"pathlib.Path({str(self.gh_marker)!r}).touch()\n"
                      "name=sys.argv[-1].split('name=')[-1]\n"
                      "print(json.dumps({'artifacts':[{'name':name,'expired':False}]}))\n")
        gh.chmod(0o755)
        self.environment = patch.dict(os.environ, {
            "GITHUB_REPOSITORY": "Systemorph/Acme", "GITHUB_RUN_ID": "44", "GITHUB_RUN_ATTEMPT": "1",
            "GITHUB_EVENT_NAME": "pull_request", "GITHUB_OUTPUT": "", "GITHUB_STEP_SUMMARY": "",
            "MW_ARTIFACT_STORE": self.spec, "MW_LEDGER_GH": str(gh),
            "MW_LEDGER_TOKEN": "mw_test", "MW_LEDGER_RETRY_DELAY_S": "0.01"})
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.server = subprocess.Popen([sys.executable, "-u", "-c", LEDGER._FAKE_SERVER_SRC],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.addCleanup(self.stop_server)
        self.assertTrue(select.select([self.server.stdout], [], [], 5)[0], "local MCP fixture must announce its port")
        port_line = self.server.stdout.readline().strip()
        if not port_line:
            _, error = self.server.communicate(timeout=5)
            self.fail(f"local MCP fixture did not start: {error.strip()}")
        port = int(port_line)
        self.url = f"http://127.0.0.1:{port}"
        os.environ["MW_LEDGER_URL"] = self.url
        self.me = LEDGER.run_identity("lane")
        self.entry = {"package": "Alpha", "module": "Acme.Alpha", "test": True}
        self.key = "a" * 64
        self.key_info = {"module": "Acme.Alpha", "key": self.key, "inputs": {}}
        self.record = {
            "status": "Tested", "attempts": 1, "run": {"repo": "Systemorph/Acme", "runId": "12",
                "attempt": "3", "url": "https://github.com/Systemorph/Acme/actions/runs/12"},
            "finishedAt": LEDGER.iso(LEDGER.now_utc()), "heartbeatAt": LEDGER.iso(LEDGER.now_utc()),
            "bundleSha256": self.digest,
            "bundleArtifact": {"repo": "Systemorph/Acme", "runId": "12", "name": "module-bundle-Acme.Alpha"}}
        self.seed()

    def stop_server(self):
        self.server.terminate()
        self.server.wait(timeout=5)
        self.server.stdout.close()
        self.server.stderr.close()

    def seed(self):
        data = json.dumps({f"{LEDGER.ROOT}/{self.key}": self.record}).encode()
        request = urllib.request.Request(self.url + "/__store", data=data,
                                         headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=5) as response:
            response.read()

    def client(self):
        return LEDGER.Ledger(self.url, "mw_test", lambda *args: None)

    def decide(self, through_matrix=False, access=None):
        if through_matrix:
            return LEDGER.decide(self.client(), [self.entry], [self.key_info], self.me,
                                 False, 1, 0, lambda *args: None, access)
        return LEDGER.decide_one(self.client(), self.entry, self.key_info, self.me,
                                 False, lambda *args: None, access)

    def named(self):
        return self.source.upload("module-bundle-Acme.Alpha", {self.payload.name: self.payload})

    def durable(self, missing=False):
        key = "modules/Acme.Alpha/key/bundle.module.nupkg"
        locator = f"{self.spec}/{key}#sha256={self.digest}"
        if not missing:
            self.source.store.put(key, self.payload)
        self.record["bundleStore"] = {"locator": locator}
        self.seed()
        return self.share / key

    def assert_no_github(self):
        self.assertFalse(self.gh_marker.exists(), "declared own store must never call the GitHub artifact API")

    def test_valid_durable_bundle_reuses_its_verified_locator(self):
        self.durable()
        verdict, detail = self.decide()
        self.assertEqual("reuse", verdict)
        self.assertEqual(self.record["bundleStore"], detail["store"])
        self.assert_no_github()

    def test_store_identity_mismatch_is_fatal_before_optional_ledger_access(self):
        other = self.root / "other-share"
        other.mkdir()
        expected = ART.STORE.make_store(f"file:{other}").store_id()
        with self.assertRaisesRegex(LEDGER.ArtifactStoreError, "NOT on the store"):
            LEDGER.ArtifactAccess(self.spec, self.me, expected)
        self.assert_no_github()

    def test_named_and_durable_reuse_share_the_verified_caller_identity(self):
        expected = self.source.store.store_id()
        access = LEDGER.ArtifactAccess(self.spec, self.me, expected)
        self.named()
        self.assertTrue(access.named(self.record, self.record["bundleArtifact"])[0])
        self.durable()
        self.assertTrue(access.durable(self.record)[0])
        self.assert_no_github()

    def test_named_reuse_reads_source_attempt_three_from_consumer_attempt_one(self):
        self.named()
        verdict, detail = self.decide()
        self.assertEqual("reuse", verdict)
        self.assertIsNone(detail["store"])
        self.assertIn("verified named artifact", detail["source"])
        self.assert_no_github()

    def test_missing_durable_copy_falls_back_to_named_and_clears_stale_locator(self):
        self.durable(missing=True)
        self.named()
        verdict, detail = self.decide()
        self.assertEqual("reuse", verdict)
        self.assertIsNone(detail["store"])
        self.assert_no_github()

    def test_foreign_durable_locator_is_not_propagated_to_named_download(self):
        self.record["bundleStore"] = {"locator": f"file:/a/different/store/bundle#sha256={self.digest}"}
        self.seed()
        self.named()
        verdict, detail = self.decide()
        self.assertEqual("reuse", verdict)
        self.assertIsNone(detail["store"])
        self.assert_no_github()

    def test_missing_historical_bytes_rebuild_without_github_fallback(self):
        self.durable(missing=True)
        verdict, detail = self.decide()
        self.assertEqual("build", verdict)
        self.assertTrue(detail["takeover"])
        self.assert_no_github()

    def test_corrupt_durable_bytes_are_fatal_even_when_named_copy_is_valid(self):
        path = self.durable()
        self.named()
        path.write_bytes(b"corrupt")
        with self.assertRaises(LEDGER.ArtifactStoreError):
            self.decide(through_matrix=True)
        self.assert_no_github()

    def test_corrupt_named_archive_is_not_optional_coordination_failure(self):
        doc = self.named()
        key, _ = ART.STORE.split_locator(doc["locator"], self.source.store.spec)
        (self.share / key).write_bytes(b"corrupt")
        with self.assertRaises(LEDGER.ArtifactStoreError):
            self.decide(through_matrix=True)
        self.assert_no_github()

    def test_cli_reports_corrupt_store_as_error_without_build_matrix(self):
        self.durable().write_bytes(b"corrupt")
        command = [sys.executable, str(Path(LEDGER.__file__)), "decide", "--artifact-store", self.spec,
                   "--keys", json.dumps([self.key_info]), "--matrix", json.dumps([self.entry]),
                   "--out-matrix", str(self.root / "matrix.json"), "--out-build", str(self.root / "build.json")]
        result = subprocess.run(command, capture_output=True, text=True, timeout=10)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("CI artifact store", result.stderr)
        self.assertNotIn("ledger unavailable", result.stderr)
        self.assertFalse((self.root / "matrix.json").exists())
        self.assert_no_github()

    def test_named_bytes_must_match_ledger_digest_as_well_as_archive_digest(self):
        self.named()
        self.record["bundleSha256"] = "0" * 64
        self.seed()
        with self.assertRaises(LEDGER.ArtifactStoreError):
            self.decide()
        self.assert_no_github()

    def test_malformed_named_manifest_is_fatal_not_absent(self):
        self.named()
        manifest = self.source.root / self.source.name_prefix("module-bundle-Acme.Alpha") / "3/manifest.json"
        manifest.write_text("not JSON")
        with self.assertRaises(LEDGER.ArtifactStoreError):
            self.decide()
        self.assert_no_github()

    def test_expired_named_artifact_rebuilds(self):
        self.named()
        manifest = self.source.root / self.source.name_prefix("module-bundle-Acme.Alpha") / "3/manifest.json"
        doc = json.loads(manifest.read_text())
        doc["expiresAt"] = 1
        manifest.write_text(json.dumps(doc))
        self.assertEqual("build", self.decide()[0])
        self.assert_no_github()

    def test_symlink_durable_object_is_rejected(self):
        path = self.durable(missing=True)
        path.parent.mkdir(parents=True)
        path.symlink_to(self.payload)
        with self.assertRaises(LEDGER.ArtifactStoreError):
            self.decide()
        self.assert_no_github()

    def cli(self, spec):
        command = [sys.executable, str(Path(LEDGER.__file__)), "decide", "--artifact-store", spec,
                   "--keys", json.dumps([self.key_info]), "--matrix", json.dumps([self.entry]),
                   "--out-matrix", str(self.root / "matrix.json"), "--out-build", str(self.root / "build.json")]
        env = dict(os.environ, MW_LEDGER_TOKEN="", MW_LEDGER_URL="")
        return subprocess.run(command, env=env, capture_output=True, text=True, timeout=10)

    def test_missing_mount_fails_before_missing_ledger_credentials_can_fail_open(self):
        result = self.cli(f"file:{self.root}/not-mounted")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("CI artifact store", result.stderr)
        self.assertNotIn("ledger unavailable", result.stderr)
        self.assertFalse((self.root / "matrix.json").exists())
        self.assertFalse((self.root / "not-mounted").exists())
        self.assert_no_github()

    def test_valid_store_still_allows_missing_ledger_to_build_without_coordination(self):
        result = self.cli(self.spec)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("ledger unavailable", result.stderr)
        matrix = json.loads((self.root / "matrix.json").read_text())
        self.assertEqual("build", matrix[0]["ledger"]["decision"])
        self.assertTrue(matrix[0]["ledger"]["unavailable"])
        self.assert_no_github()

    def test_unsupported_store_cannot_silently_select_github(self):
        for spec in ("none", "azblob:account/container", "file:relative"):
            with self.subTest(spec=spec):
                result = self.cli(spec)
                self.assertEqual(1, result.returncode)
                self.assertIn("CI artifact store", result.stderr)
                self.assertFalse((self.root / "matrix.json").exists())
        self.assert_no_github()

    def test_explicit_gha_keeps_existing_api_behavior_and_clears_unverified_store(self):
        self.durable(missing=True)
        verdict, detail = self.decide(access=LEDGER.ArtifactAccess("gha", self.me))
        self.assertEqual("reuse", verdict)
        self.assertIsNone(detail["store"])
        self.assertTrue(self.gh_marker.exists())


if __name__ == "__main__":
    unittest.main(verbosity=2, buffer=True)
