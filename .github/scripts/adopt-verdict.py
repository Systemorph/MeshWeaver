#!/usr/bin/env python3
"""adopt-verdict — may this head ADOPT an earlier head's green verdict?

    python3 adopt-verdict.py --base origin/main --workflow "Plugin Catalog CI" [--json]
    python3 adopt-verdict.py --self-test

The fleet's bots push merges of `main` into pull requests that change NOTHING the PR authored:
the lock resolver (`Merge main into <branch> — only generated manifest.lock files conflicted`,
node-repo-resolve-locks.yml) and GitHub's own `update-branch` (auto-update-green-prs.yml). Each
push is a new head, and a new head needs every required context again — on 2026-09-13 three
30-minute MeshWeaver.Plugins runs were thrown away that way ("again manifest.lock conflict. again
when it's almost through … can we just change the manifest and let it run?").

A head may adopt when ALL of:
  1. its leading first-parent commits are MERGES FROM THE BASE — two parents, the second an
     ancestor of `--base` (any author: the resolver bot, GitHub's update-branch, a person merging
     main by hand) — or LOCK-ONLY commits (every changed path a `manifest.lock`, the regeneration
     over a merged tree), and at least one such commit exists (a person's own head never adopts);
  2. its AUTHORED DIFF — `git diff -U0 <merge-base>..<head>` minus every `manifest.lock`, minus
     `index` lines (blob ids) and `@@` headers (positions) — is byte-identical to a candidate head's,
     the candidates being every earlier head in that chain, newest first, then the authored head;
  3. that candidate's newest run of `--workflow` in this repository COMPLETED with `success`,
     read through the API (`gh api`, `GH_TOKEN`; needs `actions: read`).
Anything else — a changed authored line, a red or still-running run, an unreadable API — answers
"no" with the reason; the caller then gates as usual. Sound because the fleet's `main` protection
is `strict: false`: a green PR merges without being up to date, so a merge of main changes nothing
the verdict covers.

Stdout: `adopted=<run url or empty>`, `adopted-from-sha=`, `adoption-reason=` (or JSON with
`--json`). Exit 0 either way; exit 2 only on a usage error. Stdlib + git + gh.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path


def git(root: Path, args: list[str]) -> str | None:
    out = subprocess.run(["git", *args], cwd=root, capture_output=True, text=True)
    return out.stdout if out.returncode == 0 else None


def is_ancestor(root: Path, maybe_ancestor: str, of: str) -> bool:
    return subprocess.run(["git", "merge-base", "--is-ancestor", maybe_ancestor, of],
                          cwd=root, capture_output=True).returncode == 0


def merge_chain(root: Path, base: str, head: str = "HEAD", limit: int = 40) -> tuple[list[str], str | None]:
    """`(merges, authored)`: the leading first-parent run of merges-from-base on `head`, newest
    first, then the first commit that is not one. `authored` is None when the run never ends."""
    log = git(root, ["log", "--first-parent", f"--format=%H %P", "-n", str(limit), head])
    if not log:
        return [], None
    merges: list[str] = []
    for line in log.splitlines():
        parts = line.split()
        sha, parents = parts[0], parts[1:]
        if len(parents) == 2 and is_ancestor(root, parents[1], base):
            merges.append(sha)
            continue
        if len(parents) == 1 and lock_only(root, sha):
            # `chore: regenerate manifest.lock over the merged tree` — the generated files only;
            # the authored-diff comparison (which excludes them) still decides.
            merges.append(sha)
            continue
        return merges, sha
    return merges, None


def lock_only(root: Path, sha: str) -> bool:
    names = git(root, ["diff", "--name-only", f"{sha}^", sha])
    files = [n for n in (names or "").splitlines() if n.strip()]
    return bool(files) and all(n.endswith("manifest.lock") for n in files)


def normalise_diff(patch: str) -> str:
    """The authored change only: `index` lines carry blob ids and `@@` headers carry positions;
    both move with every merge of the base. File names and +/- content stay."""
    return "\n".join(l for l in patch.splitlines() if not l.startswith("index ") and not l.startswith("@@"))


def authored_diff(root: Path, base: str, head: str) -> str | None:
    mb = git(root, ["merge-base", base, head])
    if not mb or not mb.strip():
        return None
    patch = git(root, ["diff", "-U0", "--no-color", f"{mb.strip()}..{head}", "--", ".", ":(exclude)**/manifest.lock"])
    return None if patch is None else normalise_diff(patch)


def green_run_for(repo: str, sha: str, workflow_name: str) -> tuple[str | None, str]:
    """`(html_url, why)` of the newest completed run of `workflow_name` on `sha` — a URL only when GREEN."""
    if not repo:
        return None, "GITHUB_REPOSITORY is not set — the parent head's run cannot be read"
    if not workflow_name:
        return None, "no workflow name — the parent head's run cannot be identified"
    out = subprocess.run(["gh", "api", f"repos/{repo}/actions/runs?head_sha={sha}&per_page=30"],
                         capture_output=True, text=True)
    if out.returncode != 0:
        return None, f"the runs of {sha[:8]} could not be read ({out.stderr.strip()[:140]}) — does the job grant actions: read?"
    runs = [r for r in json.loads(out.stdout or "{}").get("workflow_runs", []) if r.get("name") == workflow_name]
    if not runs:
        return None, f"{sha[:8]} has no run of '{workflow_name}'"
    newest = max(runs, key=lambda r: r.get("run_number", 0))
    if newest.get("status") != "completed":
        return None, f"run {newest.get('html_url')} on {sha[:8]} is {newest.get('status')} — no verdict to adopt yet"
    if newest.get("conclusion") != "success":
        return None, f"run {newest.get('html_url')} on {sha[:8]} concluded {newest.get('conclusion')} — nothing green to adopt"
    return newest.get("html_url"), "green"


def decide(root: Path, base: str, workflow_name: str, repo: str) -> dict:
    merges, authored = merge_chain(root, base)
    if not merges:
        return {"adopted": "", "sha": "", "reason": "HEAD is not a merge from the base — this run gates on its own"}
    if authored is None:
        return {"adopted": "", "sha": "", "reason": "only merges from the base in the last 40 first-parent commits — refusing to guess the authored head"}
    mine = authored_diff(root, base, "HEAD")
    if mine is None:
        return {"adopted": "", "sha": "", "reason": "the authored diff of HEAD could not be computed"}
    reasons: list[str] = []
    for i, candidate in enumerate(merges[1:] + [authored]):
        theirs = authored_diff(root, base, candidate)
        if theirs is None:
            reasons.append(f"{candidate[:8]}: authored diff not computable")
            continue
        if theirs != mine:
            return {"adopted": "", "sha": "", "reason": f"the authored diff changed between {candidate[:8]} and HEAD — the base touched what this PR touched; gating on its own"}
        url, why = green_run_for(repo, candidate, workflow_name)
        if url is not None:
            return {"adopted": url, "sha": candidate,
                    "reason": f"adopted: {url} on {candidate[:8]} — {i + 1} merge(s) from the base since, authored diff byte-identical"}
        reasons.append(why)
    return {"adopted": "", "sha": "", "reason": "; ".join(reasons) or "no candidate head"}


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'✓' if ok else '✗'} {name}" + ("" if ok else f" — {detail}"))
        if not ok:
            failures.append(name)

    same = normalise_diff("diff --git a/x b/x\nindex 111..222 100644\n--- a/x\n+++ b/x\n@@ -10,0 +11 @@\n+new line\n") == \
           normalise_diff("diff --git a/x b/x\nindex 333..444 100644\n--- a/x\n+++ b/x\n@@ -40,0 +41 @@\n+new line\n")
    check("blob ids and hunk positions do not count; file names and +/- content do", same)
    check("a changed authored line counts", normalise_diff("+a\n") != normalise_diff("+b\n"))
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        root = Path(d)
        def sh(*a): return subprocess.run(["git", *a], cwd=root, capture_output=True, text=True, check=True).stdout.strip()
        sh("init", "-q", "-b", "main"); sh("config", "user.email", "t@t"); sh("config", "user.name", "t")
        (root / "a.txt").write_text("base\n"); sh("add", "."); sh("commit", "-q", "-m", "base")
        sh("checkout", "-q", "-b", "feature"); (root / "b.txt").write_text("mine\n"); sh("add", "."); authored = (sh("commit", "-q", "-m", "feat"), sh("rev-parse", "HEAD"))[1]
        sh("checkout", "-q", "main"); (root / "c.txt").write_text("theirs\n"); (root / "m" ).mkdir(); (root / "m" / "manifest.lock").write_text("v1\n"); sh("add", "."); sh("commit", "-q", "-m", "main moves")
        sh("checkout", "-q", "feature"); sh("merge", "-q", "--no-edit", "main"); merged = sh("rev-parse", "HEAD")
        merges, parent = merge_chain(root, "main")
        check("a merge from main on top of the authored head: one merge, the authored head named", merges == [merged] and parent == authored, repr((merges, parent)))
        check("the authored diff survives the merge byte-identically", authored_diff(root, "main", "HEAD") == authored_diff(root, "main", authored))
        (root / "m" / "manifest.lock").write_text("v2\n"); sh("add", "."); sh("commit", "-q", "-m", "chore: regenerate manifest.lock")
        merges, parent = merge_chain(root, "main")
        check("a lock-only regeneration commit on top rides in the chain (its diff is generated files only)", len(merges) == 2 and parent == authored, repr((merges, parent)))
        sh("reset", "-q", "--hard", merged); (root / "b.txt").write_text("changed\n"); sh("add", "."); sh("commit", "-q", "-m", "more")
        check("an authored change after the merge is a different diff", authored_diff(root, "main", "HEAD") != authored_diff(root, "main", authored))
    r = decide(Path("."), "origin/main", "", "")
    check("without a repository or workflow the answer is a reason, never an adoption", r["adopted"] == "" and r["reason"], r["reason"])
    if failures:
        print(f"\n✗ adopt-verdict self-test: {len(failures)} failure(s)")
        return 1
    print("✓ adopt-verdict self-test: diff normalisation, the merge chain, the lock-only commit, the changed line, the refusal path")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--base", default="origin/main", help="the base ref the PR targets (default origin/main)")
    ap.add_argument("--workflow", default=os.environ.get("GITHUB_WORKFLOW", ""), help="the workflow whose green run is adopted (default $GITHUB_WORKFLOW)")
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""), help="owner/name (default $GITHUB_REPOSITORY)")
    ap.add_argument("--root", default=".", help="repository root")
    ap.add_argument("--json", action="store_true", dest="as_json")
    ap.add_argument("--github-output", default=None, help="append adopted=/adopted-from-sha=/adoption-reason= here")
    ap.add_argument("--self-test", action="store_true", dest="self_test")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    result = decide(Path(args.root).resolve(), args.base, args.workflow, args.repo)
    lines = [f"adopted={result['adopted']}", f"adopted-from-sha={result['sha']}", f"adoption-reason={result['reason']}"]
    if args.github_output:
        with open(args.github_output, "a", encoding="utf-8") as out:
            out.write("\n".join(lines) + "\n")
    print(json.dumps(result) if args.as_json else "\n".join(lines))
    return 0


if __name__ == "__main__":
    sys.exit(main())
