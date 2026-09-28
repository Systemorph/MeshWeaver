#!/usr/bin/env python3
"""Which MeshWeaver.Plugins commit may an advisory dependent-suites run test WITH?

    python3 .github/scripts/paired-plugins-head.py --pr N     # GH_TOKEN reads MeshWeaver.Plugins
    python3 .github/scripts/paired-plugins-head.py --self-test

A core pull request that declares `Pairs-with: Systemorph/MeshWeaver.Plugins#<n>` is measured
together with that Plugins pull request's HEAD (policy `paired-change-sets`). That head is then
checked out and BUILT on MeshWeaver.Plugins' private runners, beside the private source and the
credentials those runners hold (`core-candidate.yml`). So the head is trusted only when it lives IN
Systemorph/MeshWeaver.Plugins: a pull request opened from a FORK carries code from anyone, and is
REFUSED — the same trust boundary the retired merge-queue path drew for a fork's core entry.

  * open, head in Systemorph/MeshWeaver.Plugins  → that head sha;
  * open, head in any other repository (a fork)  → REFUSED, red, named — re-open it from a branch;
  * closed or merged                             → "" (test against Plugins' default branch: the
                                                   change is there, or it was abandoned).

Writes `plugins=<sha or empty>` to $GITHUB_OUTPUT.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys

PLUGINS = "Systemorph/MeshWeaver.Plugins"
SHA = re.compile(r"^[0-9a-f]{40}$")


def decide(pr: object) -> tuple[str | None, str]:
    """(the Plugins sha to test with — "" for the default branch — or None when REFUSED, the
    sentence). Pure: the self-test drives it."""
    if not isinstance(pr, dict):
        return None, "the paired pull request could not be read as an object"
    state = pr.get("state")
    head = pr.get("head") if isinstance(pr.get("head"), dict) else {}
    sha = head.get("sha")
    repo = (head.get("repo") or {}).get("full_name") if isinstance(head.get("repo"), dict) else None
    if state != "open":
        return "", f"the paired pull request is {state} — testing against Plugins' default branch instead"
    if not (isinstance(sha, str) and SHA.match(sha)):
        return None, f"the paired pull request has no readable head sha ({sha!r})"
    if not isinstance(repo, str) or repo.lower() != PLUGINS.lower():
        return None, (f"the paired pull request's head lives in {repo or 'an unreadable (deleted?) repository'}, not "
                      f"{PLUGINS}. Its code would be built on {PLUGINS}' private runners beside the private source "
                      "— refused; re-open it from a branch of the repository itself")
    return sha, f"paired Plugins head {sha[:9]} ({repo})"


def self_test() -> int:
    failures = 0

    def check(name: str, ok: bool, detail: str = "") -> None:
        nonlocal failures
        print(("  ok   " if ok else "  FAIL ") + name + ("" if ok else f" — {detail}"))
        failures += 0 if ok else 1

    s = "a" * 40
    own = {"state": "open", "head": {"sha": s, "repo": {"full_name": PLUGINS}}}
    got, why = decide(own)
    check("an open head IN MeshWeaver.Plugins is tested with", got == s, why)
    got, why = decide({"state": "open", "head": {"sha": s, "repo": {"full_name": "someone/MeshWeaver.Plugins"}}})
    check("an open FORK head is REFUSED (never built beside the private source), naming where it lives",
          got is None and "refused" in why and "someone/MeshWeaver.Plugins" in why, why)
    got, why = decide({"state": "open", "head": {"sha": s, "repo": None}})
    check("an open head whose repository is gone (deleted fork) is REFUSED", got is None, why)
    got, _ = decide({"state": "closed", "head": {"sha": s, "repo": {"full_name": "someone/fork"}}})
    check("a closed pair falls back to the default branch (no fork code runs)", got == "")
    got, _ = decide({"state": "open", "head": {"sha": "abc", "repo": {"full_name": PLUGINS}}})
    check("a head that is not a 40-hex sha is REFUSED", got is None)
    got, _ = decide({"state": "open", "head": {"sha": s, "repo": {"full_name": "systemorph/meshweaver.plugins"}}})
    check("the repository name compares case-insensitively (GitHub resolves it so)", got == s)
    got, _ = decide(["not", "a", "pr"])
    check("an unreadable answer is REFUSED", got is None)
    print(f"paired-plugins-head self-test: {failures} failure(s)")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--pr", type=int)
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.pr:
        ap.error("--pr is required")
    r = subprocess.run(["gh", "api", f"repos/{PLUGINS}/pulls/{a.pr}"], capture_output=True, text=True)
    if r.returncode != 0:
        print(f"::error::cannot read {PLUGINS}#{a.pr} with the dispatch App's token — the pair declared in this PR's "
              f"body cannot be tested together ({r.stderr.strip()[:200]})")
        return 1
    try:
        pr = json.loads(r.stdout)
    except ValueError:
        pr = None
    sha, why = decide(pr)
    if sha is None:
        print(f"::error::{PLUGINS}#{a.pr}: {why}")
        return 1
    print(f"::notice::{why}" if sha == "" else why)
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a") as f:
            f.write(f"plugins={sha}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
