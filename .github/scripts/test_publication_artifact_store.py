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

    def publish(self, name="publication-inputs", run="10", attempt=1, value=None, filename=None):
        artifact = ARTIFACTS.Artifacts(self.store, self.repo, run, attempt)
        source = self.root / f"source-{run}-{attempt}-{name}"
        source.write_text(json.dumps(self.inputs if value is None else value))
        filename = filename or ("publication-inputs.json" if name == "publication-inputs" else "bundle.module.nupkg")
        with contextlib.redirect_stdout(io.StringIO()):
            doc = artifact.upload(name, {filename: source})
        return artifact, doc

    def invoke_base(self, runs, extra=(), jobs=None, current=None, jobs_error=None):
        """`jobs`: {run id → job count} answered by the jobs endpoint (absent ⇒ the endpoint gives
        no count, i.e. the run is treated as started). `jobs_error`: an exception the jobs
        endpoint RAISES instead (an API failure, a timeout)."""
        inputs = self.root / "inputs.json"
        inputs.write_text(json.dumps(self.inputs if current is None else current))
        args = ["publication-base", "--workflow", "ci.yml", "--inputs", str(inputs),
                "--repo", self.repo, "--artifact-store", self.store, *extra]

        def gh(endpoint):
            if "/jobs" in endpoint:
                if jobs_error is not None:
                    raise jobs_error
                run_id = int(endpoint.split("/runs/")[1].split("/")[0])
                return {} if jobs is None or run_id not in jobs else {"total_count": jobs[run_id], "jobs": []}
            return {"workflow_runs": runs}
        real_run = subprocess.run

        def call(command, **kwargs):
            if command[:2] == ["git", "merge-base"]:
                return subprocess.CompletedProcess(command, 0)
            return real_run(command, **kwargs)

        output = io.StringIO()
        with patch.object(sys, "argv", args), patch.object(BASE, "gh_json", side_effect=gh), \
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

    # ── runs that NEVER STARTED (2026-10-26: five green Plugins main runs, baseline none each time) ──
    def test_a_zero_job_cancelled_run_is_ignored_and_the_baseline_narrows(self):
        self.publish()
        never = {**self.run, "id": 11, "head_sha": "b" * 40, "conclusion": "cancelled"}
        code, result = self.invoke_base([never, self.run], jobs={11: 0})
        self.assertEqual(0, code)
        self.assertEqual(("a" * 40, []), (result["sha"], result["between"]))

    def test_negative_control_a_cancelled_run_that_ran_jobs_still_forces_full(self):
        self.publish()
        ran = {**self.run, "id": 11, "head_sha": "b" * 40, "conclusion": "cancelled"}
        code, result = self.invoke_base([ran, self.run], jobs={11: 4})
        self.assertEqual(0, code)
        self.assertEqual("", result["sha"])

    def test_an_unreadable_job_count_keeps_the_run_in_the_walk_and_attestation_decides(self):
        # gh_json raises CalledProcessError on an API failure and TimeoutExpired on a timeout; either
        # used to escape to main() and force a full build. The run must stay in the walk, and with
        # its attestation on the same toolchain the baseline narrows.
        for error in (subprocess.CalledProcessError(1, ["gh", "api"]),
                      subprocess.TimeoutExpired(["gh", "api"], 60)):
            with self.subTest(error=type(error).__name__):
                self.publish()
                self.publish(run="11")
                later = {**self.run, "id": 11, "head_sha": "b" * 40, "conclusion": "cancelled"}
                code, result = self.invoke_base([later, self.run], jobs_error=error)
                self.assertEqual(0, code)
                self.assertEqual(("a" * 40, [11]), (result["sha"], result["between"]))

    def test_negative_control_an_unreadable_job_count_with_a_moved_toolchain_is_still_full(self):
        self.publish()
        self.publish(run="11", value={"platform": "p2", "logic": "l1"})
        later = {**self.run, "id": 11, "head_sha": "b" * 40, "conclusion": "cancelled"}
        code, result = self.invoke_base([later, self.run],
                                        jobs_error=subprocess.CalledProcessError(1, ["gh", "api"]))
        self.assertEqual(0, code)
        self.assertEqual("", result["sha"])

    def test_an_in_flight_run_is_never_asked_and_still_forces_full_without_attestation(self):
        self.publish()
        flight = {**self.run, "id": 11, "head_sha": "b" * 40, "status": "in_progress", "conclusion": None}
        code, result = self.invoke_base([flight, self.run], jobs={11: 0})
        self.assertEqual(0, code)
        self.assertEqual("", result["sha"])

    # ── a PARTIAL publication is a baseline that CARRIES what it withheld ──
    def mark(self, value, run="10"):
        self.publish(name="publication-withheld", run=run, value=value, filename="publication-withheld.json")

    def test_partial_withheld_run_is_a_baseline_carrying_its_withheld_packages(self):
        self.publish()
        self.mark({"sha": "a" * 40, "withheld": ["Mail", "Hosting"]})
        out = self.root / "github-output"
        with patch.dict(os.environ, GITHUB_OUTPUT=str(out)):
            code, result = self.invoke_base(
                [self.run], extra=["--withheld-marker", "publication-withheld", "--carry-withheld"])
        self.assertEqual(0, code)
        self.assertEqual(("a" * 40, ["Hosting", "Mail"]), (result["sha"], result["carry"]))
        self.assertIn("publication-carry=Hosting Mail\n", out.read_text())

    def test_negative_control_without_carry_withheld_a_marked_run_is_walked_past(self):
        self.publish()
        self.mark({"sha": "a" * 40, "withheld": ["Mail"]})
        code, result = self.invoke_base([self.run], extra=["--withheld-marker", "publication-withheld"])
        self.assertEqual(0, code)
        self.assertEqual(("", []), (result["sha"], result["carry"]))

    def test_a_marker_naming_no_packages_is_walked_past_never_read_as_complete(self):
        for n, value in enumerate(({"sha": "a" * 40}, {"withheld": []}, {"withheld": [""]}, ["Mail"])):
            with self.subTest(value=value):
                run = {**self.run, "id": 30 + n}
                self.publish(run=str(run["id"]))
                self.mark(value, run=str(run["id"]))
                code, result = self.invoke_base(
                    [run], extra=["--withheld-marker", "publication-withheld", "--carry-withheld"])
                self.assertEqual(0, code)
                self.assertEqual("", result["sha"])

    def test_a_marker_the_store_cannot_deliver_is_a_hard_failure_not_a_fallback(self):
        # The listing finds the marker, but its archive fails the store's integrity check: a STORAGE
        # failure, which must reach main()'s hard failure (exit 1, no baseline printed) — never an
        # older baseline or a green full build.
        self.publish()
        _, doc = self.publish(name="publication-withheld", value={"withheld": ["Mail"]},
                              filename="publication-withheld.json")
        key, _ = ARTIFACTS.STORE.split_locator(doc["locator"], self.store)
        (self.shelf / key).write_bytes(b"not the attested archive")
        older = {**self.run, "id": 9, "head_sha": "c" * 40}
        self.publish(run="9")
        code, result = self.invoke_base(
            [self.run, older], extra=["--withheld-marker", "publication-withheld", "--carry-withheld"])
        self.assertEqual((1, None), (code, result))

    def test_negative_control_a_delivered_marker_with_unparseable_contents_is_walked_past(self):
        # The store delivers the archive intact; only the file inside is not JSON — contents
        # unknown, so the run is walked past (here to a full build), exit 0.
        self.publish()
        artifact = ARTIFACTS.Artifacts(self.store, self.repo, "10", 1)
        source = self.root / "garbled-marker"
        source.write_text("not json")
        with contextlib.redirect_stdout(io.StringIO()):
            artifact.upload("publication-withheld", {"publication-withheld.json": source})
        code, result = self.invoke_base(
            [self.run], extra=["--withheld-marker", "publication-withheld", "--carry-withheld"])
        self.assertEqual(0, code)
        self.assertEqual(("", []), (result["sha"], result["carry"]))

    def test_an_unmarked_run_is_a_complete_baseline_with_no_carry(self):
        self.publish()
        code, result = self.invoke_base(
            [self.run], extra=["--withheld-marker", "publication-withheld", "--carry-withheld"])
        self.assertEqual(0, code)
        self.assertEqual(("a" * 40, []), (result["sha"], result["carry"]))

    # ── the toolchain is the resolved SET plus the lane's recipe, not core main's HEAD ──
    def test_a_moved_build_logic_ref_narrows_when_both_attest_the_recipe(self):
        self.inputs = {"platform": "p1", "set": "3.1.9898", "logic": "l1", "recipe": "4"}
        self.publish()
        code, result = self.invoke_base([self.run], current={**self.inputs, "logic": "l2"})
        self.assertEqual(0, code)
        self.assertEqual("a" * 40, result["sha"])

    def test_negative_controls_recipe_set_or_a_missing_recipe_still_force_full(self):
        self.inputs = {"platform": "p1", "set": "3.1.9898", "logic": "l1", "recipe": "4"}
        self.publish()
        for label, current in (("recipe moved", {**self.inputs, "logic": "l2", "recipe": "5"}),
                               ("set moved", {**self.inputs, "logic": "l2", "set": "3.1.9901"}),
                               ("no recipe on the current side",
                                {k: v for k, v in {**self.inputs, "logic": "l2"}.items() if k != "recipe"})):
            with self.subTest(label):
                code, result = self.invoke_base([self.run], current=current)
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

        repo = self.checkout({"Floor": []})
        output = io.StringIO()
        args = ["reuse", "--matrix", '[{"module":"Floor","project":"src/Floor/Floor.csproj","test":false}]',
                "--run", "10", "--sha", "a" * 40, "--repo", self.repo, "--artifact-store", self.store,
                "--root", str(repo)]
        with patch.object(sys, "argv", args), patch.object(REUSE.subprocess, "run", side_effect=call), \
                contextlib.redirect_stdout(output):
            self.assertEqual(0, REUSE.main())
        self.assertEqual(1, len(github_paths))
        self.assertIn("reuse", json.loads(output.getvalue())[0])

    def checkout(self, projects):
        repo = self.root / "repo"
        for name, refs in projects.items():
            (repo / "src" / name).mkdir(parents=True)
            items = "".join(f'<ProjectReference Include="../{r}/{r}.csproj" />' for r in refs)
            (repo / "src" / name / f"{name}.csproj").write_text(f"<Project><ItemGroup>{items}</ItemGroup></Project>")
        return repo

    def reuse_cli(self, matrix, root):
        """Run the CLI over a real store holding every matrix entry's bundle from run 10."""
        for entry in matrix:
            self.publish(name=f"module-bundle-{entry['module']}", attempt=3)

        real_run = subprocess.run

        def call(command, **kwargs):
            if command[:2] == ["gh", "api"]:
                return subprocess.CompletedProcess(command, 0, stdout=json.dumps(self.run))
            return real_run(command, **kwargs)

        args = ["reuse", "--matrix", json.dumps(matrix), "--run", "10", "--sha", "a" * 40,
                "--repo", self.repo, "--artifact-store", self.store]
        if root is not None:
            args += ["--root", str(root)]
        output, errors = io.StringIO(), io.StringIO()
        with patch.object(sys, "argv", args), patch.object(REUSE.subprocess, "run", side_effect=call), \
                contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            self.assertEqual(0, REUSE.main())
        return {e["module"]: e for e in json.loads(output.getvalue())}, errors.getvalue()

    def test_a_sibling_riding_a_built_bundle_is_built_with_it_not_reused(self):
        # MeshWeaver.Plugins#4253: AI's sources changed and AI references Collaboration, whose
        # sources did not. Reusing Essentials' Collaboration bundle beside a freshly built AI bundle
        # (which carries its own Collaboration) put one assembly name into the gate's set at two
        # builds. Maps shares no project with AI and stays reused.
        repo = self.checkout({"Collab": [], "AI": ["Collab"], "Maps": []})
        matrix = [{"module": m, "project": f"src/{m}/{m}.csproj", "test": t}
                  for m, t in (("Collab", False), ("AI", True), ("Maps", False))]
        got, log = self.reuse_cli(matrix, repo)
        self.assertNotIn("reuse", got["Collab"])
        self.assertIn("reuse", got["Maps"])
        self.assertNotIn("reuse", got["AI"])
        self.assertIn("BUILD Collab, not reused: it shares src/Collab/Collab.csproj with AI", log)

    def test_without_a_root_nothing_is_reused(self):
        matrix = [{"module": "Maps", "project": "src/Maps/Maps.csproj", "test": False}]
        got, log = self.reuse_cli(matrix, None)
        self.assertNotIn("reuse", got["Maps"])
        self.assertIn("no --root", log)

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
