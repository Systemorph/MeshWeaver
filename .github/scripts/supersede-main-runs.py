#!/usr/bin/env python3
"""Cancel the `main` runs this push supersedes — and only those.

WHY (maintainer, 2026-09-08, during a roll block: "cancel superseded", "superseded means
overlapping code"). Every satellite is a monorepo, so two pushes to `main` are the same work only
when the newer one reaches everything the older one reached. It never has to be MEASURED: a push
run diffs against the SEALED publication's `source-commit.txt`, not `github.event.before`, and the
ledger rebuilds every module with no usable Published record — so a run cancelled before it
publishes is rebuilt, wholesale, by the next run on `main`. Therefore:

    a push→main run O is superseded by a push→main run N  ⇔  O's commit is an ANCESTOR of N's.

Design of record: Doc/Architecture/ModuleBuildArchitecture → "Superseded runs on main".

THE TWO GUARDS, each from a measured failure:

  * A run that has begun PUBLISHING is never cancelled: once publish-bake has started it
    finishes and seals; the newer run seals after it. Cancelling earlier — even mid-pack —
    loses nothing durable: outputs are content-addressed, so the next run reuses identical
    builds from the ledger (measured live 2026-09-08: the default once said `bundle` and would
    have protected every run from its select step on, never cancelling anything). Cancelling inside
    publish-bake left "a torn, unsealed publication" (Plugins#826); and GitHub's own
    newest-wins group starved a whole merge burst — "43 commits / 23 merges landed after it;
    22 of the last 25 main runs were cancelled with jobs=0", nothing published for hours
    (Plugins#888). Protecting past-gates runs is what makes a burst still seal.
  * Only push→main supersedes push→main. A repository_dispatch (the platform wave) and the
    schedule poll are never candidates and never actors — the #826 rule, intact.

IT CANNOT PASS SILENTLY (AGENTS.md → "A gate NEVER tests its own inputs"): a missing token, an
API failure or an unresolvable ancestry is RED naming the cause, never a quiet "nothing to do".
Cancelling nothing because nothing is superseded is a success and says so with the denominator.

    python3 supersede-main-runs.py                      # in Actions: env from the runner
    python3 supersede-main-runs.py --dry-run            # decide and print, cancel nothing
    python3 supersede-main-runs.py --self-test          # prove the decision can say no
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from typing import Callable

API = "https://api.github.com"
DEFAULT_PROTECTED = r"(?i)^(publish|bake|seal|hand-?over)"


class Red(Exception):
    """Something this lane needs could not be established. Never downgraded to 'nothing to do'."""


# ── the pure decision ─────────────────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class Candidate:
    run_id: int
    head_sha: str
    event: str
    status: str
    started_jobs: tuple[str, ...] = field(default_factory=tuple)   # names of jobs NOT queued


@dataclass(frozen=True)
class Verdict:
    run_id: int
    action: str      # "cancel" | "protect" | "keep"
    reason: str


def decide(
    this_sha: str,
    candidates: list[Candidate],
    ancestry: Callable[[str, str], str],
    protected: re.Pattern[str],
) -> list[Verdict]:
    """For each candidate run, cancel / protect / keep — with the reason a reader can check.

    `ancestry(base, head)` answers GitHub's compare status for base...head: "ahead" (head is
    ahead of base ⇒ base is an ancestor), "behind", "diverged", "identical". Pure: no I/O.
    """
    out: list[Verdict] = []
    for c in candidates:
        if c.event != "push":
            out.append(Verdict(c.run_id, "keep", f"event is {c.event}, not push — never a candidate (#826)"))
            continue
        status = ancestry(c.head_sha, this_sha)
        if status == "identical":
            out.append(Verdict(c.run_id, "keep", "same commit — a re-run, not superseded"))
            continue
        if status != "ahead":
            out.append(Verdict(c.run_id, "keep", f"{c.head_sha[:8]} is not an ancestor of {this_sha[:8]} (compare: {status})"))
            continue
        # 🚨 Match the STAGE, not any word in a job name. A reusable lane's jobs are named
        # "<caller job> / <inner job>", and an inner name can contain the word for other reasons —
        # measured live 2026-09-08: "Module bundles / Module bundle (MeshWeaver.Publish)" is the
        # PACK job of a module called Publish, and an unanchored `publish` protected that run.
        hit = [j for j in c.started_jobs if protected.search(j.split(" / ", 1)[0])]
        if hit:
            out.append(Verdict(c.run_id, "protect",
                               f"superseded, but past its gates — '{hit[0]}' has started; it will publish and this run publishes after it (#826/#888)"))
            continue
        out.append(Verdict(c.run_id, "cancel", f"{c.head_sha[:8]} is an ancestor of {this_sha[:8]} and no protected job has started"))
    return out


# ── GitHub I/O ────────────────────────────────────────────────────────────────────────────────

class GitHub:
    def __init__(self, repo: str, token: str) -> None:
        self.repo, self.token = repo, token

    def call(self, method: str, path: str, params: dict | None = None) -> dict | list:
        url = f"{API}/repos/{self.repo}/{path}"
        if params:
            url += "?" + "&".join(f"{k}={v}" for k, v in params.items())
        req = urllib.request.Request(url, method=method, headers={
            "Authorization": f"Bearer {self.token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "supersede-main-runs",
        })
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                body = r.read()
                return json.loads(body) if body else {}
        except urllib.error.HTTPError as e:
            raise Red(f"{method} {path} → HTTP {e.code}: {e.read()[:300]!r}") from e
        except urllib.error.URLError as e:
            raise Red(f"{method} {path} → {e.reason}") from e

    def run(self, run_id: int) -> dict:
        return self.call("GET", f"actions/runs/{run_id}")

    def open_push_runs(self, workflow_id: int, branch: str, exclude: int) -> list[dict]:
        found: list[dict] = []
        for status in ("queued", "in_progress", "pending", "waiting"):
            data = self.call("GET", "actions/runs", {"branch": branch, "event": "push", "status": status, "per_page": 100})
            for r in data.get("workflow_runs", []):
                if r.get("workflow_id") == workflow_id and r.get("id") != exclude:
                    found.append(r)
        return found

    def started_job_names(self, run_id: int) -> tuple[str, ...]:
        data = self.call("GET", f"actions/runs/{run_id}/jobs", {"per_page": 100})
        return tuple(j["name"] for j in data.get("jobs", []) if j.get("status") != "queued")

    def compare(self, base: str, head: str) -> str:
        data = self.call("GET", f"compare/{base}...{head}")
        status = data.get("status")
        if status not in ("ahead", "behind", "diverged", "identical"):
            raise Red(f"compare {base[:8]}...{head[:8]} answered {status!r} — ancestry could not be established")
        return status

    def cancel(self, run_id: int) -> None:
        self.call("POST", f"actions/runs/{run_id}/cancel")


# ── main ──────────────────────────────────────────────────────────────────────────────────────

def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY"))
    ap.add_argument("--run-id", type=int, default=int(os.environ.get("GITHUB_RUN_ID") or 0))
    ap.add_argument("--sha", default=os.environ.get("GITHUB_SHA"))
    ap.add_argument("--branch", default=os.environ.get("SUPERSEDE_BRANCH", "main"))
    ap.add_argument("--protected-jobs", default=os.environ.get("SUPERSEDE_PROTECTED_JOBS", DEFAULT_PROTECTED))
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args(argv)

    if a.self_test:
        return self_test()

    try:
        token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN") or ""
        if not token:
            raise Red("no GH_TOKEN / GITHUB_TOKEN — this lane needs `actions: write`; refusing to report 'nothing superseded' on no access")
        if not (a.repo and a.run_id and a.sha):
            raise Red(f"incomplete identity: repo={a.repo!r} run_id={a.run_id!r} sha={a.sha!r}")
        protected = re.compile(a.protected_jobs)
        gh = GitHub(a.repo, token)

        me = gh.run(a.run_id)
        # 🚨 Asserted here, not trusted from the caller's `if:` — a mis-wired caller must go red,
        # not quietly cancel someone's wave dispatch.
        if me.get("event") != "push" or me.get("head_branch") != a.branch:
            raise Red(f"this run is event={me.get('event')} on {me.get('head_branch')}; only a push to {a.branch} may supersede (#826)")

        raw = gh.open_push_runs(me["workflow_id"], a.branch, a.run_id)
        candidates = [
            Candidate(r["id"], r["head_sha"], r["event"], r["status"], gh.started_job_names(r["id"]))
            for r in raw
        ]
        verdicts = decide(a.sha, candidates, gh.compare, protected)

        cancelled = 0
        for v in verdicts:
            tag = {"cancel": "CANCEL ", "protect": "PROTECT", "keep": "KEEP   "}[v.action]
            print(f"{tag} run {v.run_id}: {v.reason}")
            if v.action == "cancel" and not a.dry_run:
                gh.cancel(v.run_id)
                cancelled += 1
        n = len(candidates)
        mode = "dry-run — " if a.dry_run else ""
        print(f"\n{mode}{n} open push→{a.branch} run(s) examined, "
              f"{sum(v.action=='cancel' for v in verdicts)} superseded, "
              f"{sum(v.action=='protect' for v in verdicts)} protected past gates, "
              f"{cancelled} cancelled.")
        return 0
    except Red as e:
        print(f"::error title=supersede-main-runs::{e}", file=sys.stderr)
        return 1


# ── self-test: prove it can say no ────────────────────────────────────────────────────────────

def self_test() -> int:
    protected = re.compile(DEFAULT_PROTECTED)
    lineage = {("aaa", "ccc"): "ahead", ("bbb", "ccc"): "ahead", ("ccc", "ccc"): "identical",
               ("zzz", "ccc"): "diverged", ("ddd", "ccc"): "behind"}
    anc = lambda b, h: lineage[(b, h)]
    cands = [
        Candidate(1, "aaa", "push", "in_progress", ("Required CI inputs", "Portal hosts (shard 0)")),
        Candidate(2, "bbb", "push", "in_progress", ("Validate node repos", "publish-bake / Publish and seal")),
        Candidate(8, "aaa", "push", "in_progress", ("Module bundles / Module bundle (MeshWeaver.Publish)",)),
        Candidate(3, "ccc", "push", "queued", ()),
        Candidate(4, "zzz", "push", "in_progress", ()),
        Candidate(5, "ddd", "push", "queued", ()),
        Candidate(6, "aaa", "repository_dispatch", "in_progress", ()),
        Candidate(7, "aaa", "schedule", "queued", ()),
    ]
    got = {v.run_id: v.action for v in decide("ccc", cands, anc, protected)}
    want = {1: "cancel",   # ancestor, gates only
            2: "protect",  # ancestor, but publish started — torn seal / starvation guard
            3: "keep",     # same commit: a re-run
            4: "keep",     # diverged
            5: "keep",     # behind (this run is the OLDER one)
            6: "keep",     # the wave dispatch is never a candidate
            7: "keep",     # the poll is never a candidate
            8: "cancel"}   # a PACK job of a module named Publish is not the publish stage
    fails = [f"run {k}: want {want[k]}, got {got.get(k)}" for k in want if got.get(k) != want[k]]
    if fails:
        print("::error title=supersede-main-runs self-test::" + "; ".join(fails), file=sys.stderr)
        return 1
    print(f"✓ supersede-main-runs self-test: {len(want)}/{len(want)} verdicts as designed "
          "(cancel 2, protect 1, keep 5 — the dispatch, the poll, and a module merely NAMED Publish)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
