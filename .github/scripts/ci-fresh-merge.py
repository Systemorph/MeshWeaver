#!/usr/bin/env python3
"""ci-fresh-merge.py — a pull request's suites test its head merged onto the CURRENT base tip.

(The name on this first line is load-bearing: the `fresh-merge` action refuses a body whose first
400 bytes do not name it.)

WHY (policy `suites-test-fresh-merge`, Doc/Architecture/FreshMergeUnderTest)
---------------------------------------------------------------------------
A `pull_request` run checks out `refs/pull/<n>/merge` AT THE COMMIT THE EVENT CARRIED. GitHub
builds that merge when the head is pushed and recomputes it lazily, so the commit a run tests is
the head merged onto main as main stood at the PUSH — and every later re-run (a queue dispatch, a
transient retry, the steward) tests the SAME old merge. Measured 2026-10-05 on core: of the 18
open pull requests, 13 had a `merge_commit_sha` whose first parent was NOT main's tip, and
re-reading the pull requests did not refresh a single one. A green earned that way can be green
against a main that has since moved — "a gate fix in main does not reach open PRs".

So every suite job, after its checkout, replaces that tree with a merge it computes itself:

    tree   = git merge-tree --write-tree --merge-base=<merge-base(main, head)> <main tip> <head>
    commit = git commit-tree <tree> -p <main tip> -p <head>      (fixed identity and dates)

The merge commit is a pure function of (main tip, head): every job of one run that is handed the
SAME main sha arrives at the byte-identical commit, so a build leg and the test leg reading its
artifacts test one tree. Nothing is pushed — no branch update, no new head, no new review, no run
loop, no write token, and it works for a fork's pull request too.

A head that CONFLICTS with the current main is RED here, naming the paths: such a pull request
cannot merge anyway, and testing GitHub's stale merge would paint it green against a main it no
longer merges onto.

USAGE
-----
  ci-fresh-merge.py apply [--path DIR] [--main-sha SHA] [--content-ref REF]
      Reads GITHUB_EVENT_NAME / GITHUB_EVENT_PATH / GITHUB_REPOSITORY (and GH_TOKEN for the one
      compare read a shallow checkout needs). Writes main-sha, merge-sha, mode to $GITHUB_OUTPUT
      and one summary line. Not a pull request, or a caller that named a content ref → mode
      `not-a-pull-request` / `content-ref`, nothing changed, said in green.
  ci-fresh-merge.py resolve [--path DIR] [--main-sha SHA] [--content-ref REF]
      The same, without touching the checkout: only names the base tip (main-sha) that a planning
      job hands to the jobs which merge onto it.
  ci-fresh-merge.py --self-test
      Builds throwaway repositories and proves each verdict both ways (see self_test()).
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

IDENTITY = {
    "GIT_AUTHOR_NAME": "meshweaver-ci", "GIT_AUTHOR_EMAIL": "ci@meshweaver.invalid",
    "GIT_COMMITTER_NAME": "meshweaver-ci", "GIT_COMMITTER_EMAIL": "ci@meshweaver.invalid",
}
MIN_GIT = (2, 40)  # `merge-tree --write-tree` is 2.38; `--merge-base=` is 2.40


class Refused(Exception):
    """A verdict that must make the job RED, with the line that says why."""


def git(path: str, *args: str, env: dict | None = None, check: bool = True) -> subprocess.CompletedProcess:
    full = dict(os.environ)
    if env:
        full.update(env)
    r = subprocess.run(["git", "-C", path, *args], capture_output=True, text=True, env=full)
    if check and r.returncode != 0:
        raise Refused(f"git {' '.join(args)} failed ({r.returncode}): {(r.stderr or r.stdout).strip()[:600]}")
    return r


def git_version(path: str) -> tuple[int, int]:
    out = git(path, "version").stdout
    m = re.search(r"(\d+)\.(\d+)", out)
    return (int(m.group(1)), int(m.group(2))) if m else (0, 0)


def has_commit(path: str, sha: str) -> bool:
    return git(path, "cat-file", "-e", f"{sha}^{{commit}}", check=False).returncode == 0


def fetch(path: str, *shas: str) -> None:
    missing = [s for s in shas if s and not has_commit(path, s)]
    if not missing:
        return
    shallow = git(path, "rev-parse", "--is-shallow-repository").stdout.strip() == "true"
    args = ["fetch", "--no-tags", "--quiet"] + (["--depth=1"] if shallow else []) + ["origin", *missing]
    git(path, *args)


def parents(path: str, sha: str) -> list[str]:
    return git(path, "rev-list", "--parents", "-n", "1", sha).stdout.split()[1:]


def merge_base(path: str, main: str, head: str, compare) -> str:
    r = git(path, "merge-base", main, head, check=False)
    if r.returncode == 0 and r.stdout.strip():
        return r.stdout.strip()
    # A shallow checkout cannot walk to the merge-base; the compare read names it (one REST call).
    mb = compare(main, head)
    if not mb:
        raise Refused(f"the merge-base of main {main[:12]} and head {head[:12]} could not be read — the fresh merge cannot be computed")
    return mb


def fresh_merge(path: str, main: str, head: str, compare) -> tuple[str, str]:
    """Return (mode, merge_sha) and leave `path` checked out at the merge. Raises Refused on a conflict."""
    if git_version(path) < MIN_GIT:
        raise Refused(f"git {'.'.join(map(str, git_version(path)))} is older than {'.'.join(map(str, MIN_GIT))} — `merge-tree --merge-base` is unavailable on this runner; the fresh merge cannot be computed (fix the runner image, never test the stale merge)")
    current = git(path, "rev-parse", "HEAD", check=False).stdout.strip()
    if current and parents(path, current) == [main, head]:
        return "already-fresh", current
    fetch(path, main, head)
    mb = merge_base(path, main, head, compare)
    fetch(path, mb)
    r = git(path, "merge-tree", "--write-tree", "--name-only", f"--merge-base={mb}", main, head, check=False)
    if r.returncode == 1:
        lines = r.stdout.strip().split("\n")
        conflicted = [l for l in lines[1:] if l and not l.startswith("Auto-merging") and not l.startswith("CONFLICT")][:20]
        raise Refused(f"head {head[:12]} CONFLICTS with the current main {main[:12]} ({', '.join(conflicted) or 'see git merge-tree'}) — merge main into the branch, resolve, and push; a stale merge is never tested in its place")
    if r.returncode != 0:
        raise Refused(f"git merge-tree failed ({r.returncode}): {(r.stderr or r.stdout).strip()[:600]}")
    tree = r.stdout.strip().split("\n")[0]
    date = git(path, "show", "-s", "--format=%cI", head).stdout.strip()
    env = dict(IDENTITY, GIT_AUTHOR_DATE=date, GIT_COMMITTER_DATE=date)
    msg = f"CI fresh merge of {head} onto {main}"
    commit = git(path, "commit-tree", tree, "-p", main, "-p", head, "-m", msg, env=env).stdout.strip()
    git(path, "checkout", "--quiet", "--detach", "--force", commit)
    return "merged", commit


def gh_compare(repo: str):
    def read(main: str, head: str) -> str:
        r = subprocess.run(["gh", "api", f"repos/{repo}/compare/{main}...{head}", "--jq", ".merge_base_commit.sha"],
                           capture_output=True, text=True)
        return r.stdout.strip() if r.returncode == 0 else ""
    return read


def write_outputs(**kv: str) -> None:
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a") as f:
            for k, v in kv.items():
                f.write(f"{k}={v}\n")


def summary(line: str) -> None:
    s = os.environ.get("GITHUB_STEP_SUMMARY")
    if s:
        with open(s, "a") as f:
            f.write(line + "\n")


def apply(args: list[str], resolve_only: bool = False) -> int:
    path, main_sha, content_ref = ".", "", ""
    it = iter(args)
    for a in it:
        if a == "--path": path = next(it)
        elif a == "--main-sha": main_sha = next(it).strip()
        elif a == "--content-ref": content_ref = next(it).strip()
        else:
            print(f"::error::unknown argument {a}"); return 2
    event = os.environ.get("GITHUB_EVENT_NAME", "")
    if event != "pull_request":
        print(f"event '{event}' is not a pull request — the commit under test is the event's own; nothing to merge")
        write_outputs(**{"main-sha": "", "merge-sha": "", "mode": "not-a-pull-request"})
        return 0
    if content_ref:
        print(f"the caller named content ref '{content_ref}' — that ref is tested as given, not merged onto main")
        write_outputs(**{"main-sha": "", "merge-sha": "", "mode": "content-ref"})
        return 0
    with open(os.environ["GITHUB_EVENT_PATH"]) as f:
        pr = json.load(f)["pull_request"]
    head, base = pr["head"]["sha"], pr["base"]["ref"]
    repo = os.environ.get("GITHUB_REPOSITORY", "")
    try:
        if not main_sha:
            out = git(path, "ls-remote", "origin", f"refs/heads/{base}").stdout.split()
            if not out:
                raise Refused(f"the tip of '{base}' could not be read from origin")
            main_sha = out[0]
        if resolve_only:
            print(f"the run tests against {base}@{main_sha[:12]} — every suite job merges head {head[:12]} onto it")
            summary(f"- 🔀 Suites test head `{head[:12]}` merged onto `{base}@{main_sha[:12]}` (resolved once for this run)")
            write_outputs(**{"main-sha": main_sha, "merge-sha": "", "mode": "resolved"})
            return 0
        stale = parents(path, "HEAD")[0] if has_commit(path, "HEAD") and len(parents(path, "HEAD")) == 2 else ""
        mode, merge = fresh_merge(path, main_sha, head, gh_compare(repo))
    except Refused as e:
        print(f"::error title=Fresh merge with {base}::{e}")
        summary(f"### ❌ Fresh merge refused — {e}")
        return 1
    note = "" if not stale or stale == main_sha else f" (GitHub's merge ref was built on {base}@{stale[:12]})"
    line = f"Tested: head `{head[:12]}` merged onto `{base}@{main_sha[:12]}` → `{merge[:12]}` ({mode}){note}"
    print(line)
    summary(f"- 🔀 {line}")
    write_outputs(**{"main-sha": main_sha, "merge-sha": merge, "mode": mode})
    return 0


# ───────────────────────────── self-test ─────────────────────────────
def self_test() -> int:
    tmp = tempfile.mkdtemp(prefix="fresh-merge-")
    fails: list[str] = []

    def check(cond: bool, what: str) -> None:
        print(("✓ " if cond else "✗ ") + what)
        if not cond:
            fails.append(what)

    def commit(repo: str, files: dict, msg: str) -> str:
        for name, body in files.items():
            with open(os.path.join(repo, name), "w") as f:
                f.write(body)
        git(repo, "add", "-A")
        git(repo, "commit", "-q", "-m", msg, env=dict(IDENTITY, GIT_AUTHOR_DATE="2026-01-01T00:00:00Z", GIT_COMMITTER_DATE="2026-01-01T00:00:00Z"))
        return git(repo, "rev-parse", "HEAD").stdout.strip()

    try:
        origin = os.path.join(tmp, "origin")
        os.makedirs(origin)
        git(origin, "init", "-q", "-b", "main")
        git(origin, "config", "uploadpack.allowAnySHA1InWant", "true")
        base = commit(origin, {"a.txt": "a\n", "b.txt": "b\n"}, "base")
        git(origin, "checkout", "-q", "-b", "feature")
        head = commit(origin, {"a.txt": "a changed by the PR\n"}, "pr")
        git(origin, "checkout", "-q", "main")
        stale_main = base
        main = commit(origin, {"b.txt": "b changed on main after the push\n"}, "main moves")
        git(origin, "checkout", "-q", "-b", "conflict", base)
        conflict_head = commit(origin, {"b.txt": "b changed by another PR\n"}, "conflicting pr")
        git(origin, "checkout", "-q", "main")

        def clone(name: str, shallow: bool) -> str:
            d = os.path.join(tmp, name)
            url = "file://" + origin
            subprocess.run(["git", "clone", "-q"] + (["--depth=1"] if shallow else []) + ["--no-checkout", url, d], check=True, capture_output=True)
            git(d, "fetch", "-q", *(["--depth=1"] if shallow else []), "origin", head)
            git(d, "checkout", "-q", "--detach", head)
            return d

        compare_calls: list[tuple[str, str]] = []

        def compare(m: str, h: str) -> str:
            compare_calls.append((m, h))
            return base

        # 1. Full history: the merge has parents [main tip, head] and BOTH sides' changes.
        full = clone("full", shallow=False)
        mode, merged = fresh_merge(full, main, head, compare)
        check(mode == "merged", "a stale checkout is replaced by a fresh merge")
        check(parents(full, merged) == [main, head], "the merge's parents are [current main tip, head]")
        check(open(os.path.join(full, "a.txt")).read() == "a changed by the PR\n", "the working tree carries the PR's change")
        check(open(os.path.join(full, "b.txt")).read() == "b changed on main after the push\n", "the working tree carries main's NEWER change (the point)")
        check(not compare_calls, "with history present the merge-base is computed locally (no API read)")

        # 2. Determinism: a second job handed the same main sha arrives at the byte-identical commit.
        shallow = clone("shallow", shallow=True)
        mode2, merged2 = fresh_merge(shallow, main, head, compare)
        check(merged2 == merged, "a shallow checkout computes the IDENTICAL merge commit (one tree per run)")
        check(compare_calls == [(main, head)], "a shallow checkout reads the merge-base through the compare (once)")

        # 3. Idempotent: already on the fresh merge → nothing recomputed.
        mode3, merged3 = fresh_merge(full, main, head, compare)
        check(mode3 == "already-fresh" and merged3 == merged, "a checkout already on the fresh merge is left alone")

        # 4. NEGATIVE CONTROL — the stale merge is detectably different: the merge onto the OLD main
        #    lacks main's change, so a test that passed on it could not have seen main's state.
        stale = clone("stale", shallow=False)
        _, stale_merge = fresh_merge(stale, stale_main, head, compare)
        check(stale_merge != merged and open(os.path.join(stale, "b.txt")).read() == "b\n",
              "negative control: merging onto the OLD main yields a different tree (the stale merge is not what we test)")

        # 5. A conflict with the current main is RED, naming the path — never tested stale instead.
        c = clone("conflict", shallow=False)
        git(c, "fetch", "-q", "origin", conflict_head)
        try:
            fresh_merge(c, main, conflict_head, compare)
            check(False, "a head that conflicts with the current main is refused")
        except Refused as e:
            check("CONFLICTS" in str(e) and "b.txt" in str(e), "a head that conflicts with the current main is refused, naming b.txt")

        # 6. apply(): a non-PR event and a named content ref change nothing, and say so.
        os.environ["GITHUB_EVENT_NAME"] = "push"
        out = os.path.join(tmp, "out")
        os.environ["GITHUB_OUTPUT"] = out
        os.environ.pop("GITHUB_STEP_SUMMARY", None)
        rc = apply(["--path", full])
        check(rc == 0 and "mode=not-a-pull-request" in open(out).read(), "a push is not merged (mode not-a-pull-request)")
        os.environ["GITHUB_EVENT_NAME"] = "pull_request"
        rc = apply(["--path", full, "--content-ref", "abc"])
        check(rc == 0 and "mode=content-ref" in open(out).read(), "a caller-named content ref is tested as given")

        # 7. apply() end to end on a pull_request event, main resolved from origin.
        ev = os.path.join(tmp, "event.json")
        with open(ev, "w") as f:
            json.dump({"pull_request": {"head": {"sha": head}, "base": {"ref": "main"}}}, f)
        os.environ["GITHUB_EVENT_PATH"] = ev
        e2e = clone("e2e", shallow=False)
        open(out, "w").close()
        rc = apply(["--path", e2e])
        o = open(out).read()
        check(rc == 0 and f"main-sha={main}" in o and f"merge-sha={merged}" in o, "apply resolves main's tip from origin and writes main-sha / merge-sha")
        planner = clone("planner", shallow=True)
        open(out, "w").close()
        rc = apply(["--path", planner], resolve_only=True)
        check(rc == 0 and f"main-sha={main}" in open(out).read() and git(planner, "rev-parse", "HEAD").stdout.strip() == head,
              "resolve names main's tip and leaves the planner's checkout untouched")
        with open(ev, "w") as f:
            json.dump({"pull_request": {"head": {"sha": conflict_head}, "base": {"ref": "main"}}}, f)
        c2 = clone("e2e-conflict", shallow=False)
        git(c2, "fetch", "-q", "origin", conflict_head)
        check(apply(["--path", c2]) == 1, "apply exits RED on a conflict with the current main")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
        for k in ("GITHUB_EVENT_NAME", "GITHUB_OUTPUT", "GITHUB_EVENT_PATH"):
            os.environ.pop(k, None)
    print(f"{'FAILED' if fails else 'OK'}: {len(fails)} failing case(s)")
    return 1 if fails else 0


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        sys.exit(self_test())
    if sys.argv[1:2] == ["apply"]:
        sys.exit(apply(sys.argv[2:]))
    if sys.argv[1:2] == ["resolve"]:
        sys.exit(apply(sys.argv[2:], resolve_only=True))
    print(__doc__)
    sys.exit(2)
