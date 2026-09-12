#!/usr/bin/env python3
"""Find a conservative publication baseline from a successful trunk publishing workflow.

The caller opts in by naming a workflow whose successful main PUSH publishes all selected
outputs. Only such a run ADVANCES the baseline. A failed, cancelled or in-flight run after it does
NOT reset it (per-module deploy, maintainer 2026-09-11: "no huge runs ever"): the caller narrows by
the history UNION from the baseline (`git log baseline..HEAD`, node-repo-scope.py), which carries
every change those runs held, so the next run rebuilds whatever they left unpublished. Resetting to
FULL instead made one cancelled run cost the next run the whole catalog — and under a steady merge
rate every full run was cancelled in turn, so Plugins main published NO module for nine hours.

What DOES reset it — a full build, never an omitted publication:
  * a run after the baseline that attested a DIFFERENT toolchain (platform, images, build logic):
    it may have published some modules with it, which git history cannot show;
  * a successful run that is not an ancestor of HEAD (history rewritten — a union from any older
    baseline would miss what that run published from the rewritten commits);
  * no successful run in the page read, too many unsettled runs to attest, an unavailable API, or
    an unreadable attestation.
"""
import argparse
import io
import json
import os
import re
import subprocess
import sys
import zipfile
from urllib.parse import quote

# Beyond this many unsettled runs since the last success, attesting each one costs more than the
# full build it would save — and that many reds in a row is itself worth a full rebuild.
MAX_UNSETTLED = 30


def baseline(runs, branch, ancestor, current_run=""):
    """The newest successful main push that is an ancestor of HEAD, plus the ids of the publishing
    runs NEWER than it (failed, cancelled, in flight) whose toolchain the caller must still check.
    `runs` is the API's newest-first listing. Pure: `ancestor(sha)` is the only question asked."""
    publishing = {"push", "repository_dispatch", "schedule"}
    between = []
    for run in runs:
        if str(run.get("id")) == str(current_run) or run.get("head_branch") != branch or run.get("event") not in publishing:
            continue
        sha = run.get("head_sha", "")
        if (run.get("event") == "push" and run.get("status") == "completed"
                and run.get("conclusion") == "success" and re.fullmatch(r"[0-9a-f]{40}", sha)):
            if ancestor(sha):
                return {"sha": sha, "run": run["id"], "url": run["html_url"], "between": between}
            break
        between.append(run["id"])
    return {"sha": "", "run": "", "url": "", "between": between}


def toolchain_verdict(current, prior, between):
    """None when the baseline may narrow; otherwise the reason for a full build. `prior` is what
    the baseline run attested; `between` pairs each later run with its attestation, or None when
    it never reached the scope job (no artifact — it built and published nothing)."""
    if prior != current:
        return "publication toolchain changed since the baseline run — full build"
    for run_id, inputs in between:
        if inputs is not None and inputs != current:
            return (f"run {run_id}, after the baseline, attested a different toolchain and may have "
                    "published with it — full build")
    return None


def self_test():
    good = dict(event="push", head_branch="main", status="completed", conclusion="success",
                head_sha="a" * 40, id=1, html_url="https://example.test/runs/1")

    def newer(**change):
        return {**good, "id": 2, "head_sha": "b" * 40, "html_url": "https://example.test/runs/2", **change}

    yes = lambda _: True  # noqa: E731
    passed = 0

    def check(condition, what):
        nonlocal passed
        assert condition, what
        passed += 1

    # WALKED PAST: the baseline stays the older success and the newer run comes back to be
    # toolchain-checked — a run that did not succeed carries changes the union already contains.
    for change in [dict(conclusion="failure"), dict(conclusion="cancelled"),
                   dict(status="in_progress", conclusion=None), dict(event="repository_dispatch"),
                   dict(event="schedule"), dict(head_sha="short")]:
        got = baseline([newer(**change), good], "main", yes)
        check(got["run"] == 1 and got["between"] == [2], f"walk past {change}: {got}")
    # IGNORED: not a publishing run of this branch — neither the baseline nor between.
    for change in [dict(event="workflow_dispatch"), dict(event="pull_request"), dict(head_branch="other")]:
        got = baseline([newer(**change), good], "main", yes)
        check(got["run"] == 1 and got["between"] == [], f"ignore {change}: {got}")
    got = baseline([newer(), good], "main", yes, current_run="2")
    check(got["run"] == 1 and got["between"] == [], "the current run is never its own baseline")
    check(baseline([newer(), good], "main", yes)["run"] == 2, "the newest success wins")
    got = baseline([newer(), good], "main", lambda sha: sha != "b" * 40)
    check(got["sha"] == "" and got["run"] == "", "a success off HEAD's line stops the walk (full)")
    got = baseline([newer(conclusion="failure")], "main", yes)
    check(got["sha"] == "" and got["between"] == [2], "no success at all is a full build")
    check(baseline([], "main", yes)["sha"] == "", "an empty listing is a full build")

    same = {"platform": "p1", "portal": "d1", "tester": "t1", "logic": "l1"}
    moved = {**same, "platform": "p2"}
    check(toolchain_verdict(same, same, []) is None, "same toolchain narrows")
    check(toolchain_verdict(same, moved, []) is not None, "a baseline on another toolchain is full")
    check(toolchain_verdict(same, None, []) is not None, "a baseline with no attestation is full")
    check(toolchain_verdict(same, same, [(2, None)]) is None,
          "a later run that never reached the scope job published nothing — narrows")
    check(toolchain_verdict(same, same, [(2, same), (3, same)]) is None, "later runs on the same toolchain narrow")
    check("run 3" in (toolchain_verdict(same, same, [(2, same), (3, moved)]) or ""),
          "a later run on another toolchain is full, and names the run")
    print(f"publication baseline: {passed} assertions passed")


def gh_json(path):
    out = subprocess.run(["gh", "api", path], capture_output=True, text=True, timeout=60, check=True)
    return json.loads(out.stdout)


def attested_inputs(repo, run_id):
    """What a run attested in its `publication-inputs` artifact, or None when it has none (it never
    reached the scope job). Read through the artifact API rather than `gh run download`, which
    refuses a run that is still in flight — and in-flight runs are exactly the ones walked past."""
    listing = gh_json(f"repos/{repo}/actions/runs/{run_id}/artifacts?name=publication-inputs")
    live = [x for x in listing.get("artifacts", []) if not x.get("expired")]
    if not live:
        if listing.get("total_count"):
            raise ValueError(f"run {run_id}'s publication-inputs artifact has expired")
        return None
    blob = subprocess.run(["gh", "api", f"repos/{repo}/actions/artifacts/{live[0]['id']}/zip"],
                          capture_output=True, timeout=60, check=True).stdout
    with zipfile.ZipFile(io.BytesIO(blob)) as archive:
        return json.loads(archive.read("publication-inputs.json"))


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
    with open(a.inputs, encoding="utf-8") as fh:
        current = json.load(fh)
    if not current or not isinstance(current, dict) or not all(current.values()):
        p.error("resolved publication inputs must be a nonempty object with no empty values")
    endpoint = (f"repos/{a.repo}/actions/workflows/{quote(a.workflow, safe='')}/runs"
                f"?branch={quote(a.branch, safe='')}&per_page=100")
    empty = dict(sha="", run="", url="", between=[])
    try:
        runs = gh_json(endpoint)["workflow_runs"]
        if not isinstance(runs, list):
            raise ValueError("workflow_runs is not a list")
        result = baseline(runs, a.branch, lambda sha: subprocess.run(
            ["git", "merge-base", "--is-ancestor", sha, "HEAD"], cwd=a.root,
            capture_output=True, timeout=30).returncode == 0, os.environ.get("GITHUB_RUN_ID", ""))
        if result["run"] and len(result["between"]) > MAX_UNSETTLED:
            print(f"{len(result['between'])} unsettled runs since the last success (more than "
                  f"{MAX_UNSETTLED}) — full build", file=sys.stderr)
            result = empty
        elif result["run"]:
            # Git source alone cannot see a repository variable overriding the platform ref.
            # Every run attests its actual resolved inputs in this run-scoped artifact.
            reason = toolchain_verdict(current, attested_inputs(a.repo, result["run"]),
                                       [(rid, attested_inputs(a.repo, rid)) for rid in result["between"]])
            if reason:
                print(reason, file=sys.stderr)
                result = empty
            elif result["between"]:
                print(f"walked past {len(result['between'])} unsettled run(s) "
                      f"({', '.join(map(str, result['between']))}) — the history union from the "
                      "baseline carries their changes, so the next build covers them", file=sys.stderr)
    except (OSError, ValueError, KeyError, zipfile.BadZipFile, subprocess.SubprocessError) as exc:
        print(f"::warning::publication baseline unavailable ({type(exc).__name__}); full build", file=sys.stderr)
        result = empty
    print(json.dumps(result))
    print(f"publication baseline: {result['url'] or 'none — full build'}", file=sys.stderr)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            out.write(f"publication-base={result['sha']}\npublication-run={result['run']}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
