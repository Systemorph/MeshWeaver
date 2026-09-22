#!/usr/bin/env python3
"""Real filesystem coverage for publication selectors using the named artifact adapter."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile


SCRIPTS = Path(__file__).resolve().parent


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


BASE = load("publication_base", "node-repo-publication-base.py")
REUSE = load("publication_reuse", "node-repo-publication-reuse.py")
ARTIFACTS = load("publication_test_artifacts", "ci-run-artifacts.py")


class PublicationArtifactStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="publication-artifact-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.shelf = self.root / "store"
        self.shelf.mkdir()
        self.store = f"file:{self.shelf}"
        self.repo = "Test/Project"
        self.inputs = {"platform": "p1", "logic": "l1"}
        self.run = {"status": "completed", "conclusion": "success", "event": "push",
                    "head_branch": "main", "head_sha": "a" * 40, "id": 10,
                    "html_url": "https://example.test/runs/10", "run_attempt": 3}
        # Deliberately unlike the source run's third attempt. The consumer's first attempt must
        # still read the source's newest manifest, or its successful earlier producer when absent.
        env = patch.dict(os.environ, GITHUB_RUN_ID="20", GITHUB_RUN_ATTEMPT="1",
                         GITHUB_REPOSITORY=self.repo)
        env.start()
        self.addCleanup(env.stop)
        os.environ.pop("GITHUB_OUTPUT", None)

    def publish(self, name="publication-inputs", run="10", attempt=1, value=None):
        artifact = ARTIFACTS.Artifacts(self.store, self.repo, run, attempt)
        source = self.root / f"source-{run}-{attempt}-{name}"
        source.write_text(json.dumps(self.inputs if value is None else value))
        filename = "publication-inputs.json" if name == "publication-inputs" else "bundle.module.nupkg"
        with contextlib.redirect_stdout(io.StringIO()):
            doc = artifact.upload(name, {filename: source})
        return artifact, doc

    def invoke_base(self, runs):
        inputs = self.root / "inputs.json"
        inputs.write_text(json.dumps(self.inputs))
        args = ["publication-base", "--workflow", "ci.yml", "--inputs", str(inputs),
                "--repo", self.repo, "--artifact-store", self.store]
        real_run = subprocess.run

        def call(command, **kwargs):
            if command[:2] == ["git", "merge-base"]:
                return subprocess.CompletedProcess(command, 0)
            return real_run(command, **kwargs)

        output = io.StringIO()
        with patch.object(sys, "argv", args), patch.object(BASE, "gh_json", return_value={"workflow_runs": runs}), \
                patch.object(BASE.subprocess, "run", side_effect=call), contextlib.redirect_stdout(output):
            result = BASE.main()
        return result, json.loads(output.getvalue()) if output.getvalue() else None

    def test_cross_run_reads_newest_source_attempt_not_consumer_attempt(self):
        self.publish(attempt=1, value={"platform": "old", "logic": "l1"})
        self.publish(attempt=3)
        with patch.object(BASE, "gh_json", side_effect=AssertionError("must not use artifact API")):
            self.assertEqual(self.inputs, BASE.attested_inputs(self.repo, "10", self.store))

    def test_partial_rerun_keeps_successful_earlier_producer(self):
        self.publish(attempt=1)
        self.assertEqual(self.inputs, BASE.attested_inputs(self.repo, "10", self.store))
        self.publish(name="module-bundle-Floor", attempt=1)
        listing = REUSE.stored_artifacts(self.store, self.repo, "10", attempt=3)
        entries = REUSE.annotate([{"module": "Floor", "test": False}], self.run, "a" * 40, listing)
        self.assertEqual("10", entries[0]["reuse"]["runId"])

    def test_caller_identity_is_verified_before_empty_cross_run_reads(self):
        other = self.root / "other-share"
        other.mkdir()
        expected = ARTIFACTS.STORE.make_store(f"file:{other}").store_id()
        with self.assertRaisesRegex(BASE.ArtifactStoreError, "NOT on the store"):
            BASE.attested_inputs(self.repo, "10", self.store, expected)
        with self.assertRaisesRegex(REUSE.ArtifactStoreError, "NOT on the store"):
            REUSE.stored_artifacts(self.store, self.repo, "10", expected_store_id=expected)

    def test_matching_caller_identity_preserves_cross_run_download(self):
        self.publish(attempt=3)
        expected = ARTIFACTS.STORE.make_store(self.store).store_id()
        self.assertEqual(self.inputs, BASE.attested_inputs(self.repo, "10", self.store, expected))

    def test_absent_own_store_baseline_rebuilds_without_github_artifact_lookup(self):
        self.assertIsNone(BASE.attested_inputs(self.repo, "10", self.store))
        code, result = self.invoke_base([self.run])
        self.assertEqual(0, code)
        self.assertEqual("", result["sha"])

    def test_attested_own_store_baseline_narrows(self):
        self.publish()
        code, result = self.invoke_base([self.run])
        self.assertEqual(0, code)
        self.assertEqual("a" * 40, result["sha"])

    def test_missing_between_attestation_cannot_hide_older_github_publication(self):
        self.publish()
        later = {**self.run, "id": 11, "head_sha": "b" * 40, "conclusion": "failure"}
        code, result = self.invoke_base([later, self.run])
        self.assertEqual(0, code)
        self.assertEqual("", result["sha"])

    def test_expired_is_not_absent_and_cannot_be_reused(self):
        artifact, doc = self.publish()
        manifest = self.shelf / artifact.name_prefix(doc["name"]) / "1" / "manifest.json"
        doc["expiresAt"] = 1
        manifest.write_text(json.dumps(doc))
        with self.assertRaisesRegex(ValueError, "expired"):
            BASE.attested_inputs(self.repo, "10", self.store)
        listing = REUSE.stored_artifacts(self.store, self.repo, "10")
        self.assertTrue(listing[0]["expired"])

    def test_corrupt_attestation_archive_fails_closed(self):
        artifact, doc = self.publish()
        key, _ = ARTIFACTS.STORE.split_locator(doc["locator"], self.store)
        (self.shelf / key).write_bytes(b"not the attested archive")
        with self.assertRaises(BASE.ArtifactStoreError):
            BASE.attested_inputs(self.repo, "10", self.store)

    def test_corrupt_manifest_fails_closed(self):
        artifact, doc = self.publish()
        manifest = self.shelf / artifact.name_prefix(doc["name"]) / "1" / "manifest.json"
        manifest.write_text("not-json")
        with self.assertRaises(BASE.ArtifactStoreError):
            BASE.attested_inputs(self.repo, "10", self.store)
        with self.assertRaises(REUSE.ArtifactStoreError):
            REUSE.stored_artifacts(self.store, self.repo, "10")

    def test_declared_missing_store_fails_even_without_history_or_modules(self):
        self.store = f"file:{self.root / 'missing'}"
        with patch.object(BASE, "gh_json", side_effect=AssertionError("store must be checked first")):
            inputs = self.root / "inputs.json"
            inputs.write_text(json.dumps(self.inputs))
            with patch.object(sys, "argv", ["base", "--workflow", "ci.yml", "--inputs", str(inputs),
                                            "--repo", self.repo, "--artifact-store", self.store]):
                self.assertEqual(1, BASE.main())
        with patch.object(sys, "argv", ["reuse", "--matrix", "[]", "--repo", self.repo,
                                        "--artifact-store", self.store]):
            self.assertEqual(1, REUSE.main())

    def test_reuse_cli_reads_run_identity_but_never_github_artifact_api(self):
        self.publish(name="module-bundle-Floor", attempt=3)
        real_run = subprocess.run
        github_paths = []

        def call(command, **kwargs):
            if command[:2] == ["gh", "api"]:
                github_paths.append(command[2])
                self.assertEqual(f"repos/{self.repo}/actions/runs/10", command[2])
                return subprocess.CompletedProcess(command, 0, stdout=json.dumps(self.run))
            return real_run(command, **kwargs)

        output = io.StringIO()
        args = ["reuse", "--matrix", '[{"module":"Floor","test":false}]', "--run", "10",
                "--sha", "a" * 40, "--repo", self.repo, "--artifact-store", self.store]
        with patch.object(sys, "argv", args), patch.object(REUSE.subprocess, "run", side_effect=call), \
                contextlib.redirect_stdout(output):
            self.assertEqual(0, REUSE.main())
        self.assertEqual(1, len(github_paths))
        self.assertIn("reuse", json.loads(output.getvalue())[0])

    def test_nested_adapter_does_not_leak_github_outputs(self):
        self.publish()
        output = self.root / "github-output"
        with patch.dict(os.environ, GITHUB_OUTPUT=str(output)):
            BASE.attested_inputs(self.repo, "10", self.store)
        self.assertFalse(output.exists())

    def test_legacy_attestation_without_store_keeps_github_path(self):
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w") as package:
            package.writestr("publication-inputs.json", json.dumps(self.inputs))
        listing = {"artifacts": [{"id": 55, "expired": False}]}
        with patch.object(BASE, "gh_json", return_value=listing) as lookup, \
                patch.object(BASE.subprocess, "run", return_value=subprocess.CompletedProcess(
                    [], 0, stdout=archive.getvalue())) as download:
            self.assertEqual(self.inputs, BASE.attested_inputs(self.repo, "10"))
        lookup.assert_called_once_with(f"repos/{self.repo}/actions/runs/10/artifacts?name=publication-inputs")
        self.assertEqual(f"repos/{self.repo}/actions/artifacts/55/zip", download.call_args.args[0][-1])

    def test_empty_own_store_reuse_keeps_normal_build_without_github_artifact_lookup(self):
        entries = [{"module": "Floor", "test": False}]
        self.assertEqual(entries, REUSE.annotate(entries, self.run, "a" * 40,
                                                 REUSE.stored_artifacts(self.store, self.repo, "10")))


if __name__ == "__main__":
    unittest.main()
