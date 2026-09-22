#!/usr/bin/env python3
"""Executable transport contract: real archives and real directories, no GitHub service."""
import concurrent.futures
import contextlib
import importlib.util
import io
import json
import multiprocessing
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "ci_run_artifacts", Path(__file__).with_name("ci-run-artifacts.py"))
ART = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ART)


def contend_for_attempt(folder, connection):
    """A separate publisher/pruner process must observe the parent's lock, then acquire it."""
    try:
        with ART.attempt_lock(Path(folder), 1, blocking=False):
            connection.send("unexpected-acquisition")
            return
    except BlockingIOError:
        connection.send("blocked")
    if connection.recv() != "parent-released":
        return
    with ART.attempt_lock(Path(folder), 1, blocking=False):
        connection.send("acquired")


class ArtifactTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.share = self.root / "share"
        self.share.mkdir()
        self.source = self.root / "input"
        self.source.mkdir()
        self.store = f"file:{self.share}"
        self.art = self.client()

    def client(self, attempt=1, run="12", repo="Systemorph/Plugins"):
        return ART.Artifacts(self.store, repo, run, attempt)

    def file(self, relative, content="data", mode=0o644):
        path = self.source / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)
        path.chmod(mode)
        return path

    def publish(self, name="build", content="data", client=None):
        path = self.file(name + ".txt", content)
        return (client or self.art).upload(name, {"payload.txt": path})

    def test_directory_and_glob_roots_exclusions_hidden_and_executable(self):
        self.file("build/bin/run", "#!/bin/sh", 0o755)
        self.file("build/bin/skip.txt")
        self.file("build/.secret")
        files = ART.collect(["build", "!build/**/skip.txt"], [], False, self.source)
        self.assertEqual(["bin/run"], list(files))
        self.art.upload("tool", files)
        out = self.root / "out"
        self.art.download(out, name="tool")
        self.assertEqual(0o755, (out / "bin/run").stat().st_mode & 0o777)
        files = ART.collect(["build/*/run"], [], False, self.source)
        self.assertEqual(["bin/run"], list(files))
        files = ART.collect(["build"], [], True, self.source)
        self.assertIn(".secret", files)

    def test_multiple_roots_and_exclusions_do_not_move_common_root(self):
        self.file("one/a.txt")
        self.file("two/b.txt")
        files = ART.collect(["one/a.txt\ntwo/b.txt\n!two/b.txt"], [], False, self.source)
        self.assertEqual(["one/a.txt"], list(files))
        self.assertEqual(["a.txt"], list(ART.collect(["one/a.txt"], [], False, self.source)))

    def test_partial_rerun_uses_previous_successful_producer_but_never_future_or_other_run(self):
        self.publish("old", "attempt 1")
        self.publish("new", "attempt 2", self.client(2))
        current = self.client(2)
        self.assertEqual(["new", "old"], [d["name"] for d in current.list()])
        self.assertEqual(["old"], [d["name"] for d in self.art.list()])
        self.assertEqual([], self.client(2, run="13").list())
        self.assertEqual([], self.client(2, repo="Systemorph/Other").list())
        self.publish("old", "new bytes", self.client(2))
        out = self.root / "out"
        current.download(out, name="old")
        self.assertEqual("new bytes", (out / "payload.txt").read_text())

    def test_same_attempt_is_immutable_unless_overwrite_is_explicit(self):
        original = self.publish()
        self.assertEqual(original, self.publish())
        with self.assertRaises(ART.Red):
            self.publish(content="different")
        self.art.upload("build", {"payload.txt": self.file("replacement", "different")}, overwrite=True)
        self.art.download(self.root / "out", name="build")
        self.assertEqual("different", (self.root / "out/payload.txt").read_text())

    def test_concurrent_names_do_not_share_a_mutable_index(self):
        path = self.file("payload")
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
            futures = [executor.submit(self.art.upload, f"artifact-{i}", {"payload": path}) for i in range(12)]
            for future in futures:
                future.result()
        self.assertEqual(12, len(self.art.list()))

    def test_inflight_stage_precedes_archiving_and_failed_upload_cleans_it(self):
        archive_files = ART.archive_files
        folder = self.art.root / self.art.name_prefix("building")
        def observe_stage(*args):
            self.assertEqual(1, len(list(folder.glob(".publish-*"))))
            return archive_files(*args)
        with patch.object(ART, "archive_files", side_effect=observe_stage):
            with self.assertRaises(FileNotFoundError):
                self.art.upload("building", {"missing": self.source / "missing"})
        self.assertEqual([], list(folder.glob(".publish-*")))
        self.assertEqual([], self.art.list())

    def test_retention_bound_matches_pruner_contract(self):
        path = self.file("payload")
        for retention in (0, -1, 91):
            with self.assertRaises(ART.Red):
                self.art.upload("invalid", {"payload": path}, retention=retention)
        self.assertEqual([], self.art.list())
        self.art.upload("long-lived", {"payload": path}, retention=90)
        self.assertEqual(90, self.art.list("long-lived")[0]["retentionDays"])

    def test_attempt_lock_contends_between_processes_and_keeps_its_inode(self):
        folder = self.art.root / self.art.name_prefix("lock")
        folder.mkdir(parents=True)
        context = multiprocessing.get_context("spawn")
        parent, child = context.Pipe()
        process = context.Process(target=contend_for_attempt, args=(str(folder), child))
        try:
            with ART.attempt_lock(folder, 1):
                lock = folder / ".locks/1.lock"
                inode = lock.stat().st_ino
                process.start()
                self.assertTrue(parent.poll(5), "child must report its nonblocking lock verdict")
                self.assertEqual("blocked", parent.recv())
            parent.send("parent-released")
            self.assertTrue(parent.poll(5), "child must acquire after parent releases")
            self.assertEqual("acquired", parent.recv())
            process.join(5)
            self.assertEqual(0, process.exitcode)
            self.assertEqual(inode, lock.stat().st_ino)
        finally:
            if process.is_alive():
                process.terminate()
                process.join(5)
            parent.close()
            child.close()

    def test_pattern_braces_directory_layout_and_merge(self):
        self.art.upload("module-AI", {"AI.dll": self.file("a")})
        self.art.upload("module-Maps", {"Maps.dll": self.file("b")})
        out = self.root / "out"
        self.art.download(out, pattern="module-{AI,Maps}")
        self.assertTrue((out / "module-AI/AI.dll").is_file())
        merged = self.root / "merged"
        self.art.download(merged, pattern="module-*", merge_multiple=True)
        self.assertEqual(["AI.dll", "Maps.dll"], sorted(p.name for p in merged.iterdir()))

    def test_corrupt_archive_is_red_before_any_output_written(self):
        self.publish("first")
        bad = self.publish("second")
        key, _ = ART.STORE.split_locator(bad["locator"], self.art.store.spec)
        (self.share / key).write_bytes(b"damaged")
        out = self.root / "out"
        with self.assertRaises(ART.Red):
            self.art.download(out)
        self.assertFalse(out.exists())

    def test_manifest_tampering_is_red(self):
        self.publish()
        manifest = self.share / self.art.name_prefix("build") / "1/manifest.json"
        doc = json.loads(manifest.read_text())
        doc["runId"] = "999"
        manifest.write_text(json.dumps(doc))
        with self.assertRaises(ART.Red):
            self.art.list("build")

    def test_archive_traversal_links_and_duplicate_paths_are_refused(self):
        for name, kind in (("../escape", tarfile.REGTYPE), ("/escape", tarfile.REGTYPE),
                           ("link", tarfile.SYMTYPE), ("hard", tarfile.LNKTYPE)):
            archive = self.root / "hostile.tar.gz"
            with tarfile.open(archive, "w:gz") as tar:
                member = tarfile.TarInfo(name)
                member.type = kind
                tar.addfile(member)
            doc = self.publish("hostile")
            digest = ART.STORE.sha256_file(archive)
            key = f"{self.art.name_prefix('hostile')}/objects/1/{digest}.tar.gz"
            doc["locator"] = self.art.store.put(key, archive)
            doc["archiveSha256"] = digest
            doc["archiveBytes"] = archive.stat().st_size
            with self.assertRaises(ART.Red):
                self.art.verified_archive(doc, self.root / "check.tar.gz")
        archive = self.root / "duplicates.tar.gz"
        with tarfile.open(archive, "w:gz") as tar:
            tar.addfile(tarfile.TarInfo("duplicate"))
            tar.addfile(tarfile.TarInfo("duplicate"))
        doc["archiveSha256"] = ART.STORE.sha256_file(archive)
        key = f"{self.art.name_prefix('hostile')}/objects/1/{doc['archiveSha256']}.tar.gz"
        doc["locator"] = self.art.store.put(key, archive)
        doc["archiveBytes"] = archive.stat().st_size
        with self.assertRaises(ART.Red):
            self.art.verified_archive(doc, self.root / "check.tar.gz")

    def test_upload_and_download_refuse_symlinks(self):
        target = self.file("real")
        (self.source / "link").symlink_to(target)
        with self.assertRaises(ART.Red):
            ART.collect(["link"], [], True, self.source)
        (self.source / "linked-directory").symlink_to(self.source, target_is_directory=True)
        with self.assertRaises(ART.Red):
            ART.collect(["linked-directory"], [], False, self.source)
        self.publish()
        out = self.root / "out"
        out.mkdir()
        (out / "payload.txt").symlink_to(target)
        with self.assertRaises(ART.Red):
            self.art.download(out, name="build")
        self.assertEqual("data", target.read_text())

    def test_invalid_namespace_and_unmounted_store_are_refused(self):
        for repo in ("../Plugins", "Systemorph/../Plugins", "/Systemorph/Plugins"):
            with self.assertRaises(ART.Red):
                self.client(repo=repo)
        for name in ("../escape", "a/b", "a\\b", "bad\nname"):
            with self.assertRaises(ART.Red):
                self.art.name_prefix(name)
        for store in ("gha", "", "azblob:account/container", f"file:{self.root}/not-mounted"):
            with self.assertRaises(ART.Red):
                ART.Artifacts(store, "Systemorph/Plugins", "12", 1)
        self.assertFalse((self.root / "not-mounted").exists())

    def test_store_failure_preserves_the_underlying_reason(self):
        with patch.object(ART.STORE.FileStore, "reachable", return_value="share write refused: disk full"):
            with self.assertRaisesRegex(ART.Red, "share write refused: disk full"):
                self.client()

    def test_store_identity_is_checked_before_probe_or_any_artifact_selection(self):
        other = self.root / "other-share"
        other.mkdir()
        expected = ART.STORE.make_store(f"file:{other}").store_id()
        for identity in (expected, "", "   "):
            with self.subTest(identity=identity), patch.object(
                    ART.STORE.FileStore, "reachable") as reachable:
                with self.assertRaises(ART.Red):
                    ART.Artifacts(self.store, "Systemorph/Plugins", "12", 1,
                                  expect_store_id=identity)
                reachable.assert_not_called()
        self.assertEqual([], list(self.share.iterdir()))

    def test_matching_identity_preserves_empty_collections_and_alias_paths(self):
        expected = self.art.store.store_id()
        alias = self.root / "alias"
        alias.symlink_to(self.share, target_is_directory=True)
        client = ART.Artifacts(f"file:{alias}", "Systemorph/Plugins", "12", 1,
                               expect_store_id=expected)
        self.assertEqual(0, client.download(self.root / "out", pattern="receipt-*"))
        self.assertEqual([], client.list())

    def test_cli_rejects_wrong_store_before_empty_upload_download_list_or_probe(self):
        other = self.root / "other-share"
        other.mkdir()
        expected = ART.STORE.make_store(f"file:{other}").store_id()
        context = ["--store", self.store, "--repository", "Systemorph/Plugins",
                   "--run-id", "12", "--expect-store-id", expected]
        selections = (("upload", ["--name", "absent", "--path", str(self.root / "absent"),
                                  "--if-no-files-found", "ignore"]),
                      ("download", ["--pattern", "receipt-*", "--path", str(self.root / "out")]),
                      ("download", ["--path", str(self.root / "out")]),
                      ("list", []), ("probe", ["--name", "absent"]))
        for verb, selection in selections:
            output, error = io.StringIO(), io.StringIO()
            with self.subTest(verb=verb, selection=selection), contextlib.redirect_stdout(output), \
                    contextlib.redirect_stderr(error):
                self.assertEqual(1, ART.main([verb, *context, *selection]))
            self.assertEqual("", output.getvalue())
            self.assertIn("NOT on the store this run resolved", error.getvalue())
        self.assertFalse((self.root / "out").exists())
        self.assertEqual([], list(self.share.iterdir()))

    def test_missing_and_no_file_policies_have_distinct_verdicts(self):
        with self.assertRaises(ART.Red):
            self.art.download(self.root / "out", name="absent")
        args = ["upload", "--store", self.store, "--repository", "Systemorph/Plugins",
                "--run-id", "12", "--name", "empty", "--path", str(self.source / "absent")]
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, ART.main(args + ["--if-no-files-found", "error"]))
            self.assertEqual(0, ART.main(args + ["--if-no-files-found", "warn"]))
            self.assertEqual(0, ART.main(args + ["--if-no-files-found", "ignore"]))
        self.assertEqual([], self.art.list())

    def test_empty_pattern_and_all_downloads_succeed_but_exact_name_is_required(self):
        out = self.root / "out"
        self.assertEqual(0, self.art.download(out))
        self.assertEqual(0, self.art.download(out, pattern="compile-receipt-*"))
        self.publish("unrelated")
        self.assertEqual(0, self.art.download(out, pattern="compile-receipt-*"))
        self.assertFalse(out.exists())
        args = ["download", "--store", self.store, "--repository", "Systemorph/Plugins",
                "--run-id", "12", "--path", str(out), "--pattern", "compile-receipt-*"]
        output = io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(0, ART.main(args))
            self.assertEqual(1, ART.main(args[:-2] + ["--name", "compile-receipt-required"]))
        self.assertIn(f"download-path={out.resolve()}", output.getvalue())
        self.assertIn("file-count=0", output.getvalue())

    def test_empty_pattern_does_not_hide_manifest_corruption_or_unmounted_store(self):
        self.publish("unrelated")
        manifest = self.share / self.art.name_prefix("unrelated") / "1/manifest.json"
        manifest.write_text("not JSON")
        with self.assertRaises(ART.Red):
            self.art.download(self.root / "out", pattern="compile-receipt-*")
        args = ["download", "--store", f"file:{self.root}/unmounted",
                "--repository", "Systemorph/Plugins", "--run-id", "12",
                "--path", str(self.root / "out"), "--pattern", "compile-receipt-*"]
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, ART.main(args))
        self.assertFalse((self.root / "unmounted").exists())

    def test_expiry_is_visible_for_metadata_but_not_download(self):
        self.publish()
        manifest = self.share / self.art.name_prefix("build") / "1/manifest.json"
        doc = json.loads(manifest.read_text())
        doc["expiresAt"] = 1
        manifest.write_text(json.dumps(doc))
        self.assertEqual([], self.art.list())
        self.assertTrue(self.art.list(include_expired=True)[0]["expired"])
        with self.assertRaises(ART.Red):
            self.art.download(self.root / "out", name="build")

    def test_unbounded_cross_run_list_reads_latest_attempt(self):
        self.publish(client=self.client(3))
        args = ["list", "--store", self.store, "--repository", "Systemorph/Plugins", "--run-id", "12"]
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(0, ART.main(args))
        self.assertEqual(3, json.loads(output.getvalue())[0]["attempt"])

    def test_boolean_action_inputs_and_cli_outputs(self):
        path = self.file("payload")
        args = ["upload", "--store", self.store, "--repository", "Systemorph/Plugins",
                "--run-id", "12", "--attempt", "1", "--name", "cli", "--path", str(path),
                "--include-hidden-files", "false", "--overwrite", "false",
                "--compression-level", "0", "--retention-days", "0"]
        output = io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(0, ART.main(args))
        self.assertIn("artifact-id=\n", output.getvalue())
        self.assertIn("artifact-digest=", output.getvalue())
        self.assertIn("artifact-url=\n", output.getvalue())
        self.assertIn("artifact-locator=file:", output.getvalue())
        self.assertIn(f"store-id={self.art.store.store_id()}\n", output.getvalue())
        self.assertEqual(7, self.art.list("cli")[0]["retentionDays"])
        args = ["download", "--store", self.store, "--repository", "Systemorph/Plugins",
                "--run-id", "12", "--name", "cli", "--path", str(self.root / "cli"),
                "--merge-multiple", "true"]
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(0, ART.main(args))
        self.assertIn(f"download-path={(self.root / 'cli').resolve()}", output.getvalue())


if __name__ == "__main__":
    unittest.main(verbosity=2, buffer=True)
