#!/usr/bin/env python3
"""Find a conservative publication baseline from a successful trunk publishing workflow.

The caller opts in by naming a workflow whose successful main PUSH publishes all selected
outputs. A failed, cancelled, manual or release-follow run cannot advance this baseline.
An unavailable API or missing ancestor costs a full build, never an omitted publication.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path
from urllib.parse import quote


def baseline(runs, branch, ancestor, current_run=""):
    publishing = {"push", "repository_dispatch", "schedule"}
    for run in runs:
        if str(run.get("id")) == str(current_run) or run.get("head_branch") != branch or run.get("event") not in publishing:
            continue
        sha = run.get("head_sha", "")
        if (run.get("event") == "push" and run.get("head_branch") == branch
                and run.get("status") == "completed" and run.get("conclusion") == "success"
                and re.fullmatch(r"[0-9a-f]{40}", sha) and ancestor(sha)):
            return {"sha": sha, "run": run["id"], "url": run["html_url"]}
        # A later partial publish may have used a different variable-resolved toolchain,
        # invisible in git history. Without a successful receipt for it, rebuild everything.
        break
    return {"sha": "", "run": "", "url": ""}


def self_test():
    good = dict(event="push", head_branch="main", status="completed", conclusion="success",
                head_sha="a" * 40, id=1, html_url="https://example.test/runs/1")
    for change in [dict(event="workflow_dispatch"), dict(event="repository_dispatch"),
                   dict(event="schedule"), dict(conclusion="failure"), dict(conclusion="cancelled"),
                   dict(status="in_progress"), dict(head_branch="other"), dict(head_sha="short")]:
        expected = 1 if change.get("event") == "workflow_dispatch" or change.get("head_branch") == "other" else ""
        assert baseline([{**good, **change}, good], "main", lambda _: True)["run"] == expected
        assert baseline([{**good, **change}], "main", lambda _: True)["sha"] == ""
    assert baseline([good], "main", lambda _: False)["sha"] == ""
    assert baseline([], "main", lambda _: True)["sha"] == ""
    assert baseline([good], "main", lambda _: True)["sha"] == "a" * 40
    print("publication baseline: 19 assertions passed")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--workflow")
    p.add_argument("--inputs", help="current resolved platform/toolchain inputs JSON")
    p.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    p.add_argument("--root", default=".")
    p.add_argument("--branch", default="main")
    p.add_argument("--self-test", action="store_true")
    a = p.parse_args()
    if a.self_test:
        self_test()
        return 0
    if not a.workflow or not a.inputs or not re.fullmatch(r"[\w.-]+/[\w.-]+", a.repo):
        p.error("--workflow, --inputs and --repo owner/name are required")
    current = json.loads(Path(a.inputs).read_text())
    if not current or not isinstance(current, dict) or not all(current.values()):
        p.error("resolved publication inputs must be a nonempty object with no empty values")
    endpoint = (f"repos/{a.repo}/actions/workflows/{quote(a.workflow, safe='')}/runs"
                f"?branch={quote(a.branch, safe='')}&per_page=100")
    try:
        response = subprocess.run(["gh", "api", endpoint], capture_output=True, text=True,
                                  timeout=60, check=True)
        runs = json.loads(response.stdout)["workflow_runs"]
        if not isinstance(runs, list):
            raise ValueError("workflow_runs is not a list")
        result = baseline(runs, a.branch, lambda sha: subprocess.run(
            ["git", "merge-base", "--is-ancestor", sha, "HEAD"], cwd=a.root,
            capture_output=True, timeout=30).returncode == 0, os.environ.get("GITHUB_RUN_ID", ""))
        if result["run"]:
            # Git source alone cannot see a repository variable overriding the platform ref.
            # Successful runs attest their actual resolved inputs in this run-scoped artifact.
            with tempfile.TemporaryDirectory() as tmp:
                subprocess.run(["gh", "run", "download", str(result["run"]), "--repo", a.repo,
                                "--name", "publication-inputs", "--dir", tmp],
                               capture_output=True, text=True, timeout=60, check=True)
                previous = json.loads((Path(tmp) / "publication-inputs.json").read_text())
                if previous != current:
                    print("publication toolchain changed — full build", file=sys.stderr)
                    result = dict(sha="", run="", url="")
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as exc:
        print(f"::warning::publication baseline unavailable ({type(exc).__name__}); full build", file=sys.stderr)
        result = dict(sha="", run="", url="")
    print(json.dumps(result))
    print(f"publication baseline: {result['url'] or 'none — full build'}", file=sys.stderr)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            out.write(f"publication-base={result['sha']}\npublication-run={result['run']}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
