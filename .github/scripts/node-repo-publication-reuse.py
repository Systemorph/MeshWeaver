#!/usr/bin/env python3
"""Reuse unchanged gate dependencies from an attested successful publication.

The source selector proves an entry unaffected (test=false). The baseline resolver proves the
toolchain inputs equal. This helper verifies run identity and artifact availability; absence
keeps the normal build leg. It uses the module lane's existing reuse/receipt path, not a builder.
An explicit --artifact-store reads named artifacts only from that store. GitHub still attests run
identity; an unavailable declared store fails, never falls back to GitHub storage or a rebuild.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys


class ArtifactStoreError(RuntimeError):
    """A declared artifact store failed; do not silently change storage or retain builds."""


def stored_artifacts(store, repo, run_id, attempt=None, expected_store_id=None):
    """List the source run's named artifacts, preserving expiry and partial-rerun semantics."""
    command = [sys.executable, str(Path(__file__).with_name("ci-run-artifacts.py")), "list",
               "--store", store, "--repository", repo, "--run-id", str(run_id),
               "--include-expired", "true"]
    if attempt is not None:
        command.extend(["--attempt", str(attempt)])
    if expected_store_id is not None:
        command.extend(["--expect-store-id", expected_store_id])
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=180, check=True,
                                env={k: v for k, v in os.environ.items() if k != "GITHUB_OUTPUT"})
        listing = json.loads(result.stdout)
        if not isinstance(listing, list):
            raise ValueError("artifact listing is not an array")
        return listing
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
        detail = getattr(exc, "stderr", "") or str(exc)
        raise ArtifactStoreError(f"declared artifact store failed: {detail.strip()[:600]}") from exc


def annotate(entries, run, sha, artifacts):
    if (run.get("status") != "completed" or run.get("conclusion") != "success"
            or run.get("event") != "push" or run.get("head_branch") != "main"
            or run.get("head_sha") != sha or not re.fullmatch(r"[0-9a-f]{40}", sha)):
        return entries
    live = {a["name"] for a in artifacts if not a.get("expired")}
    result = []
    for entry in entries:
        name = f"module-bundle-{entry['module']}"
        if entry.get("test") is False and not entry.get("ledger") and name in live:
            entry = {**entry, "reuse": {"runId": str(run["id"]), "name": name,
                                        "source": run["html_url"]}}
        result.append(entry)
    return result


def self_test():
    entries = [{"module": "Floor", "test": False}, {"module": "Changed", "test": True}]
    run = dict(status="completed", conclusion="success", event="push", head_branch="main",
               head_sha="a" * 40, id=10, html_url="https://example.test/runs/10")
    artifacts = [{"name": "module-bundle-Floor", "expired": False}]
    got = annotate(entries, run, "a" * 40, artifacts)
    assert got[0]["reuse"]["runId"] == "10" and "reuse" not in got[1]
    for change in [dict(conclusion="failure"), dict(event="pull_request"), dict(status="in_progress"),
                   dict(head_branch="other"), dict(head_sha="b" * 40)]:
        assert annotate(entries, {**run, **change}, "a" * 40, artifacts) == entries
    assert annotate(entries, run, "a" * 40, []) == entries
    assert annotate(entries, run, "a" * 40, [{**artifacts[0], "expired": True}]) == entries
    assert annotate([{**entries[0], "ledger": {"decision": "build"}}], run, "a" * 40, artifacts)[0].get("reuse") is None
    # The wiring, not only the annotation: the lane's "Which selected modules still owe a test
    # run" step writes final.json — the list the pack legs are cut from — and it MUST read this
    # step's annotated output. Cut from the pre-annotation list (Plugins#1853), every reused
    # entry's leg planned BUILD against a global build that had honoured the reuse and emitted
    # nothing; this pins the env line so that regression cannot return in silence.
    lane = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "workflows",
                        "node-repo-module-pack.yml")
    if os.path.exists(lane):
        with open(lane, encoding="utf-8") as fh:
            text = fh.read()
        head = text.split('> "$RUNNER_TEMP/final.json"', 1)[0]
        selection = re.findall(r"SELECTION: \$\{\{ (steps\.[a-z-]+\.outputs\.modules) \}\}", head)
        assert selection and selection[-1] == "steps.reuse.outputs.modules", \
            f"final.json must be cut from the reuse-annotated list, not {selection}"
    print("publication reuse: 10 assertions passed")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--matrix")
    p.add_argument("--run", default="")
    p.add_argument("--sha", default="")
    p.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    p.add_argument("--artifact-store", default="",
                   help="explicit named artifact store; empty/gha keeps GitHub storage, no fallback when declared")
    p.add_argument("--self-test", action="store_true")
    p.add_argument("--expect-store-id", default=None, help="physical store identity resolved by this run's first producer")
    a = p.parse_args()
    if a.self_test:
        self_test()
        return 0
    entries = json.loads(a.matrix)
    own_store = bool(a.artifact_store and a.artifact_store != "gha")
    if own_store:
        try:
            # Check the declared mount even when no unchanged module needs a historical artifact.
            stored_artifacts(a.artifact_store, a.repo, os.environ.get("GITHUB_RUN_ID", "1"),
                             expected_store_id=a.expect_store_id)
        except ArtifactStoreError as exc:
            print(f"::error::{exc}", file=sys.stderr)
            return 1
    if a.run and a.sha and any(e.get("test") is False for e in entries):
        try:
            if not a.run.isdigit() or not re.fullmatch(r"[\w.-]+/[\w.-]+", a.repo):
                raise ValueError("invalid run or repository")
            def api(path, *args):
                return json.loads(subprocess.run(["gh", "api", path, *args], capture_output=True,
                                                 text=True, timeout=60, check=True).stdout)
            run = api(f"repos/{a.repo}/actions/runs/{a.run}")
            if own_store:
                artifacts = stored_artifacts(a.artifact_store, a.repo, a.run, run.get("run_attempt"),
                                             a.expect_store_id)
            else:
                pages = api(f"repos/{a.repo}/actions/runs/{a.run}/artifacts?per_page=100", "--paginate", "--slurp")
                artifacts = [art for page in pages for art in page["artifacts"]]
            entries = annotate(entries, run, a.sha, artifacts)
        except ArtifactStoreError as exc:
            print(f"::error::{exc}", file=sys.stderr)
            return 1
        except (OSError, ValueError, KeyError, subprocess.SubprocessError):
            print("::warning::publication artifacts unavailable; retaining normal builds", file=sys.stderr)
    build = [e for e in entries if not e.get("reuse") and e.get("ledger", {}).get("decision") != "reuse"]
    print(f"publication reuse: {sum(bool(e.get('reuse')) for e in entries)} unchanged gate dependencies", file=sys.stderr)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            out.write(f"modules={json.dumps(entries)}\nbuild={json.dumps(build)}\n")
    print(json.dumps(entries))
    return 0


if __name__ == "__main__":
    sys.exit(main())
