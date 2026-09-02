#!/usr/bin/env python3
"""merge-queue-steward.py — acts on a pull request the merge queue has DEQUEUED, so a human does not have to.

(The name on this first line is load-bearing, like every central guard script here: a lane that
fetches this file checks its first 400 bytes name it.)

WHY THIS EXISTS (maintainer, 2026-09-02)
----------------------------------------
"In the past the merge queue was too fragile — should we have another go? It must be very
reliable; so far we always had many red and I had to run after it."

The queue was enabled on 2026-08-30 (#2799) and removed on 2026-09-01. What was measured in between
is in Doc/Architecture/MergeQueue: every membership mutation rebuilt the speculative stack and
restarted every in-flight build (the "churn window" — over an hour in which nothing landed and
nothing failed), and every pull request a flake ejected had to be re-queued BY HAND. The first is a
queue-settings defect (`max_entries_to_build: 1` removes the stack). The second is what this script
is for: it is the hand that re-queues, and it only re-queues on EVIDENCE.

WHAT IT DOES
------------
GitHub fires `pull_request` with `action: dequeued` and a `reason` whenever an entry leaves the
queue without merging. The steward reads the reason and decides ONE of four things:

  requeue  — put the pull request back in the queue (GraphQL `enqueuePullRequest`), record the
             attempt in a hidden marker comment, and say why in the same comment;
  reject   — leave it out, comment the failing assertion(s) and the run, add the `queue-rejected`
             label; a human owns it from here;
  comment  — a reason the steward takes no action on (MANUAL, QUEUE_CLEARED, …): say so once;
  noop     — the entry merged (MERGE / ALREADY_MERGED); nothing to do.

The decision table, in full (see `classify`):

  CI_TIMEOUT                                       requeue kind=timeout   cap 2 per head sha
  CI_FAILURE  a non-shard job failed               reject  — a build or gate failure is never a flake
  CI_FAILURE  every failed assertion is catalogued  requeue kind=flake     cap 2 per head sha
  CI_FAILURE  a shard failed on an infrastructure   requeue kind=infra     cap 2 per head sha
              step and left no test evidence
  CI_FAILURE  ≥1 uncatalogued assertion, the group  requeue kind=bisect    cap 1 per head sha
              held >1 PR and this PR's own run was  (the culprit's solo group fails and stays out)
              green
  CI_FAILURE  anything else                         reject
  everything else                                  comment once

🚨 INVARIANTS
  * It never re-runs a workflow. A re-run of the same tree hides the bug the failing run found;
    a re-QUEUE builds a NEW tree (main has moved), which is a different measurement.
  * A catalogued flake is EVIDENCE-BEARING: its `assertionPattern` is a regex over the failure
    MESSAGE and STACK, never over the test name, and it carries an issue URL, run URLs, and an
    expiry at most 30 days out. An expired entry is treated as uncatalogued.
  * Caps are per HEAD SHA, read back from the steward's own marker comments on the PR — a new
    push resets them, because a new head is a new question.
  * `--self-test` proves every row of the table can be reached AND that the negative rows reject.
    An unproven gate is no gate; the workflow runs it before every real decision.

USAGE
-----
  merge-queue-steward.py --self-test
  merge-queue-steward.py act --pr N --reason CI_FAILURE [--dry-run]     (GH_TOKEN, GH_REPO in env)
  merge-queue-steward.py status                                          (queue config vs recommended)

Only the standard library. Every GitHub call goes through `gh api` with the token in GH_TOKEN.
"""
from __future__ import annotations

import argparse
import dataclasses
import datetime as dt
import json
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

CATALOGUE_PATH = ".github/known-flakes.json"
LABEL = "queue-rejected"
REQUIRED_CHECK = "Consolidate test results"
QUEUE_BRANCH = "main"
TEST_STEP = "Run Tests"
VERDICT_STEP_PREFIXES = ("Summarize test failures", "Fail on non-zero project exit", "Gate:")
SHARD_JOB = re.compile(r"^Run tests \(shard (\d+)\)$")
MARKER = re.compile(r"<!-- steward: requeued=(?P<n>\d+) head=(?P<head>[0-9a-f]{7,40}) kind=(?P<kind>[a-z]+) -->")
CAPS = {"timeout": 2, "flake": 2, "infra": 2, "bisect": 1}
MAX_CATALOGUE_DAYS = 30
NOOP_REASONS = {"MERGE", "ALREADY_MERGED"}
COMMENT_REASONS = {
    "MANUAL", "QUEUE_CLEARED", "ROLL_BACK", "BRANCH_PROTECTIONS", "GIT_TREE_INVALID",
    "INVALID_MERGE_COMMIT", "MERGE_CONFLICT", "UNKNOWN_REMOVAL_REASON",
}
TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
DEAD_HOST_MARKER = re.compile(r"\[CI\] (?P<name>\S+)(?: \(part \d+/\d+\))? exit=(?P<code>[1-9]\d*) (?P<cls>[A-Z_]+)")

# The settings the doc recommends (Doc/Architecture/MergeQueue). `status` compares the live queue
# against them so drift is reported rather than discovered.
RECOMMENDED_QUEUE = {
    "mergeMethod": "MERGE",
    "mergingStrategy": "ALLGREEN",
    "maximumEntriesToBuild": 1,
    "maximumEntriesToMerge": 3,
    "minimumEntriesToMerge": 1,
    "minimumEntriesToMergeWaitTime": 3,
    "checkResponseTimeout": 45,
}


# ─────────────────────────────── data ───────────────────────────────

@dataclasses.dataclass(frozen=True)
class Failure:
    test: str
    message: str
    stack: str
    shard: str

    @property
    def headline(self) -> str:
        first = (self.message or "").strip().splitlines()
        return first[0][:240] if first else "(no message)"


@dataclasses.dataclass(frozen=True)
class Shard:
    shard: str
    failed_steps: tuple[str, ...]
    artifact_present: bool
    failures: tuple[Failure, ...]
    dead_host_markers: tuple[str, ...]

    @property
    def failed_on_infrastructure_only(self) -> bool:
        """The test step and every verdict step passed (or never ran), yet the job is red: a
        download, an upload, a setup step died. That is a property of the runner, not the tree."""
        if not self.failed_steps:
            return False
        for step in self.failed_steps:
            if step == TEST_STEP or step.startswith(VERDICT_STEP_PREFIXES):
                return False
        return True


@dataclasses.dataclass(frozen=True)
class RunEvidence:
    run_id: int
    run_url: str
    failed_jobs: tuple[str, ...]      # non-shard jobs that failed (build, gates) — never a flake
    shards: tuple[Shard, ...]         # the failing shards, with whatever evidence they left


@dataclasses.dataclass(frozen=True)
class FlakeEntry:
    id: str
    pattern: re.Pattern
    test_name: str
    issue: str
    expires: dt.date
    added_on: dt.date
    added_by: str
    evidence: tuple[str, ...]

    def active(self, today: dt.date) -> bool:
        return self.expires >= today

    def matches(self, failure: Failure) -> bool:
        return self.pattern.search(f"{failure.message}\n{failure.stack}") is not None


@dataclasses.dataclass(frozen=True)
class Context:
    reason: str
    pr: int
    head_sha: str
    group_prs: tuple[int, ...]         # every PR in the failed group's temporary branch, this one included
    own_run_green: bool | None         # did this PR's own latest pull_request run pass the required check?
    attempts: dict                     # kind -> count of steward re-queues already spent on this head sha
    today: dt.date


@dataclasses.dataclass(frozen=True)
class Decision:
    action: str                        # requeue | reject | comment | noop
    kind: str                          # timeout | flake | infra | bisect | build | uncatalogued | cap | unclassifiable | noop | comment
    summary: str
    details: tuple[str, ...] = ()
    matched: tuple = ()                # (Failure, FlakeEntry)
    unmatched: tuple = ()              # Failure


# ─────────────────────────────── catalogue ───────────────────────────────

class CatalogueError(Exception):
    pass


def load_catalogue(text: str) -> tuple[FlakeEntry, ...]:
    """Parse and VALIDATE known-flakes.json. A malformed entry is an error, not a skipped row —
    a catalogue that silently drops what it cannot read would re-queue on nothing."""
    try:
        doc = json.loads(text)
    except json.JSONDecodeError as e:
        raise CatalogueError(f"{CATALOGUE_PATH} is not valid JSON: {e}") from e
    entries = doc.get("entries")
    if not isinstance(entries, list):
        raise CatalogueError(f"{CATALOGUE_PATH} must carry an 'entries' array")
    out = []
    seen = set()
    for i, e in enumerate(entries):
        where = f"{CATALOGUE_PATH} entries[{i}]"
        for key in ("id", "assertionPattern", "testName", "issue", "expires", "addedOn", "addedBy", "evidence"):
            if key not in e or e[key] in ("", None, []):
                raise CatalogueError(f"{where} is missing '{key}'")
        if e["id"] in seen:
            raise CatalogueError(f"{where} duplicates id '{e['id']}'")
        seen.add(e["id"])
        try:
            pattern = re.compile(e["assertionPattern"])
        except re.error as err:
            raise CatalogueError(f"{where} ({e['id']}): assertionPattern does not compile: {err}") from err
        if pattern.search(""):
            raise CatalogueError(f"{where} ({e['id']}): assertionPattern matches the empty string — it would match every failure")
        # Note what is NOT checked: whether the pattern happens to match the test name. The matcher
        # never sees the name (`FlakeEntry.matches` reads message + stack), so a name-shaped pattern
        # simply never fires — the self-test pins that.
        if not re.fullmatch(r"https://github\.com/[\w.-]+/[\w.-]+/issues/\d+", e["issue"]):
            raise CatalogueError(f"{where} ({e['id']}): issue must be a GitHub issue URL, got {e['issue']!r}")
        try:
            expires = dt.date.fromisoformat(e["expires"])
            added_on = dt.date.fromisoformat(e["addedOn"])
        except ValueError as err:
            raise CatalogueError(f"{where} ({e['id']}): expires/addedOn must be ISO dates: {err}") from err
        if expires < added_on:
            raise CatalogueError(f"{where} ({e['id']}): expires {expires} is before addedOn {added_on}")
        if (expires - added_on).days > MAX_CATALOGUE_DAYS:
            raise CatalogueError(
                f"{where} ({e['id']}): expires {expires} is more than {MAX_CATALOGUE_DAYS} days after addedOn "
                f"{added_on} — a flake entry is a stopgap with a deadline, not a permanent allowance")
        evidence = tuple(e["evidence"]) if isinstance(e["evidence"], list) else (e["evidence"],)
        for url in evidence:
            if not re.fullmatch(r"https://github\.com/[\w.-]+/[\w.-]+/actions/runs/\d+(/.*)?", url):
                raise CatalogueError(f"{where} ({e['id']}): evidence must be workflow-run URLs, got {url!r}")
        out.append(FlakeEntry(e["id"], pattern, e["testName"], e["issue"], expires, added_on, e["addedBy"], evidence))
    return tuple(out)


# ─────────────────────────────── evidence parsing ───────────────────────────────

def parse_trx(text: str, shard: str) -> tuple[Failure, ...]:
    root = ET.fromstring(text)
    out = []
    for r in root.iter(f"{TRX_NS}UnitTestResult"):
        if r.get("outcome") != "Failed":
            continue
        msg = r.find(f"{TRX_NS}Output/{TRX_NS}ErrorInfo/{TRX_NS}Message")
        st = r.find(f"{TRX_NS}Output/{TRX_NS}ErrorInfo/{TRX_NS}StackTrace")
        out.append(Failure(
            test=r.get("testName") or "(unnamed)",
            message=(msg.text or "") if msg is not None else "",
            stack=(st.text or "") if st is not None else "",
            shard=shard))
    return tuple(out)


def parse_dead_host_markers(log_text: str) -> tuple[str, ...]:
    """`[CI] <project> exit=N <CLASS>` lines whose class is not TESTFAIL: the host died. Since
    #2495 the crash is ALSO written into the trx as `<project>.HOST_CRASHED`, so this is the
    second, independent channel — used only to refuse a pass when the trx channel is empty."""
    out = []
    for line in log_text.splitlines():
        m = DEAD_HOST_MARKER.search(line)
        if m and m.group("cls") != "TESTFAIL":
            out.append(line.strip())
    return tuple(out)


def read_shard_artifact(directory: Path, shard: str) -> tuple[tuple[Failure, ...], tuple[str, ...]]:
    failures: list[Failure] = []
    markers: list[str] = []
    for trx in sorted(directory.rglob("*.trx")):
        try:
            failures.extend(parse_trx(trx.read_text(encoding="utf-8", errors="replace"), shard))
        except ET.ParseError:
            # A truncated trx from a host killed mid-write is itself evidence of a dead host.
            markers.append(f"{trx.name}: unparseable trx (host died mid-write?)")
    for log in sorted(directory.rglob("test-results.log")):
        markers.extend(parse_dead_host_markers(log.read_text(encoding="utf-8", errors="replace")))
    return tuple(failures), tuple(markers)


def count_attempts(comment_bodies, head_sha: str) -> dict:
    attempts = {k: 0 for k in CAPS}
    for body in comment_bodies:
        for m in MARKER.finditer(body or ""):
            if head_sha.startswith(m.group("head")) or m.group("head").startswith(head_sha):
                attempts[m.group("kind")] = attempts.get(m.group("kind"), 0) + 1
    return attempts


# ─────────────────────────────── the decision ───────────────────────────────

def classify(ctx: Context, evidence: RunEvidence | None, catalogue: tuple[FlakeEntry, ...]) -> Decision:
    reason = ctx.reason.upper()
    if reason in NOOP_REASONS:
        return Decision("noop", "noop", f"reason {reason}: the entry merged — nothing to do")

    if reason == "CI_TIMEOUT":
        spent = ctx.attempts.get("timeout", 0)
        if spent < CAPS["timeout"]:
            return Decision("requeue", "timeout",
                            f"the queue's check timed out (CI_TIMEOUT); re-queued — attempt {spent + 1} of {CAPS['timeout']} for this head")
        return Decision("reject", "cap",
                        f"the queue's check timed out {spent} times for this head — a run that does not finish is STUCK, not slow; find what is not completing")

    if reason != "CI_FAILURE":
        return Decision("comment", "comment",
                        f"removed from the queue with reason `{reason}` — the steward takes no action for this reason")

    if evidence is None:
        return Decision("reject", "unclassifiable",
                        "no merge_group run of 'MeshWeaver Build and Test' was found for this pull request's queue branch, so the steward cannot read what failed")

    if evidence.failed_jobs:
        return Decision("reject", "build",
                        "a job other than a test shard failed — a build or gate failure is never a flake",
                        details=tuple(f"failed job: `{j}`" for j in evidence.failed_jobs))

    if not evidence.shards:
        return Decision("reject", "unclassifiable",
                        "the run failed without a failing test shard or a failing job the steward recognises")

    active = [e for e in catalogue if e.active(ctx.today)]
    matched: list = []
    unmatched: list = []
    infra: list[str] = []
    unclassifiable: list[str] = []
    for s in evidence.shards:
        if s.failures:
            for f in s.failures:
                hit = next((e for e in active if e.matches(f)), None)
                (matched if hit else unmatched).append((f, hit) if hit else f)
        elif s.dead_host_markers:
            unclassifiable.append(
                f"shard {s.shard}: the test host died without a recorded failure — " + "; ".join(s.dead_host_markers[:3]))
        elif not s.artifact_present and s.failed_on_infrastructure_only:
            infra.append(f"shard {s.shard} failed at `{'`, `'.join(s.failed_steps)}` and left no test evidence — an infrastructure failure, not a verdict about the tree")
        elif not s.artifact_present:
            unclassifiable.append(
                f"shard {s.shard} failed at `{'`, `'.join(s.failed_steps) or '(unknown step)'}` and left no artifact to read")
        else:
            unclassifiable.append(
                f"shard {s.shard} failed but its artifact carries neither a failed test nor a dead-host marker")

    if unclassifiable:
        return Decision("reject", "unclassifiable",
                        "the failure could not be classified from the evidence the run left",
                        details=tuple(unclassifiable), matched=tuple(matched), unmatched=tuple(unmatched))

    if unmatched:
        names = tuple(f"`{f.test}` — {f.headline}" for f in unmatched)
        if len(ctx.group_prs) > 1 and ctx.own_run_green is True:
            spent = ctx.attempts.get("bisect", 0)
            if spent < CAPS["bisect"]:
                others = [p for p in ctx.group_prs if p != ctx.pr]
                return Decision("requeue", "bisect",
                                f"bisecting: your group of {len(ctx.group_prs)} pull requests failed on an uncatalogued assertion while this PR's own run was green; re-queued ALONE — "
                                f"if the solo build fails too, the PR stays out (group: {', '.join(f'#{p}' for p in others)})",
                                details=names, matched=tuple(matched), unmatched=tuple(unmatched))
            return Decision("reject", "uncatalogued",
                            "the solo build after a bisect still fails on an uncatalogued assertion — this pull request is the culprit",
                            details=names, matched=tuple(matched), unmatched=tuple(unmatched))
        return Decision("reject", "uncatalogued",
                        "the group build failed on an assertion the flake catalogue does not know",
                        details=names, matched=tuple(matched), unmatched=tuple(unmatched))

    kind = "flake" if matched else "infra"
    spent = ctx.attempts.get(kind, 0)
    details = tuple(f"`{f.test}` — matches `{e.id}` ({e.issue})" for f, e in matched) + tuple(infra)
    if spent < CAPS[kind]:
        what = "only on catalogued flakes" if kind == "flake" else "on infrastructure, not on the tree"
        return Decision("requeue", kind,
                        f"the group build failed {what}; re-queued — attempt {spent + 1} of {CAPS[kind]} for this head",
                        details=details, matched=tuple(matched))
    return Decision("reject", "cap",
                    f"already re-queued {spent} times for this head on {kind} grounds — the cap is {CAPS[kind]}; a flake that fires this often on one head is not noise",
                    details=details, matched=tuple(matched))


# ─────────────────────────────── GitHub adapter ───────────────────────────────

class Gh:
    def __init__(self, repo: str):
        self.repo = repo

    def api(self, path: str, *args: str, method: str | None = None, paginate: bool = False):
        cmd = ["gh", "api", path if path.startswith(("graphql", "/")) else f"repos/{self.repo}/{path}"]
        if method:
            cmd += ["-X", method]
        if paginate:
            cmd += ["--paginate", "--slurp"]
        cmd += list(args)
        p = subprocess.run(cmd, capture_output=True, text=True, check=False)
        if p.returncode != 0:
            raise RuntimeError(f"gh api {path} failed ({p.returncode}): {p.stderr.strip()[:800]}")
        if not p.stdout.strip():
            return None
        data = json.loads(p.stdout)
        if paginate and isinstance(data, list) and data and isinstance(data[0], list):
            data = [x for page in data for x in page]
        return data

    def graphql(self, query: str, **variables):
        args = ["-f", f"query={query}"]
        for k, v in variables.items():
            args += (["-F", f"{k}={v}"] if isinstance(v, int) else ["-f", f"{k}={v}"])
        return self.api("graphql", *args)

    # ── reads ──
    def pull_request(self, number: int) -> dict:
        return self.api(f"pulls/{number}")

    def latest_failed_merge_group_run(self, number: int) -> dict | None:
        """The newest FAILED queue build of this PR. Not merely the newest: after an ejection the
        queue may already be rebuilding the entry (a run in progress), and a PR that landed earlier
        through the queue has a green run under the same prefix — neither is the failure to read."""
        prefix = f"gh-readonly-queue/{QUEUE_BRANCH}/pr-{number}-"
        runs = []
        for page in (1, 2):
            data = self.api(f"actions/runs?event=merge_group&per_page=100&page={page}")
            runs += (data or {}).get("workflow_runs", [])
        mine = [r for r in runs if (r.get("head_branch") or "").startswith(prefix)
                and r.get("name") == "MeshWeaver Build and Test"
                and r.get("status") == "completed" and r.get("conclusion") == "failure"]
        return max(mine, key=lambda r: r["created_at"]) if mine else None

    def run_evidence(self, run: dict, workdir: Path) -> RunEvidence:
        jobs = (self.api(f"actions/runs/{run['id']}/jobs?per_page=100") or {}).get("jobs", [])
        failed_jobs = []
        shards = []
        for job in jobs:
            if job.get("conclusion") != "failure":
                continue
            m = SHARD_JOB.match(job["name"])
            if m:
                shard = m.group(1)
                steps = tuple(s["name"] for s in job.get("steps", []) if s.get("conclusion") == "failure")
                target = workdir / f"shard{shard}"
                present = self.download_artifact(run["id"], f"testResults-shard{shard}", target)
                failures, markers = read_shard_artifact(target, shard) if present else ((), ())
                shards.append(Shard(shard, steps, present, failures, markers))
            elif job["name"] != REQUIRED_CHECK:
                failed_jobs.append(job["name"])
        return RunEvidence(run["id"], run["html_url"], tuple(failed_jobs), tuple(shards))

    def download_artifact(self, run_id: int, name: str, target: Path) -> bool:
        target.mkdir(parents=True, exist_ok=True)
        p = subprocess.run(["gh", "run", "download", str(run_id), "--repo", self.repo, "-n", name, "-D", str(target)],
                           capture_output=True, text=True, check=False)
        if p.returncode != 0:
            print(f"::notice::artifact {name} of run {run_id} is not available: {p.stderr.strip()[:300]}")
            return False
        return True

    def group_pull_requests(self, run: dict, own: int) -> tuple[int, ...]:
        """Every PR in the group this run built. The queue branch is `gh-readonly-queue/main/pr-<N>-<base>`
        — <base> is main's tip the group was built from — and each hop of the first-parent chain
        from the run's head back to <base> is one entry's merge commit, whose message names its PR.
        Bounded: a chain that does not reach <base> in ten hops yields what it found, never a loop."""
        m = re.match(rf"gh-readonly-queue/{QUEUE_BRANCH}/pr-\d+-([0-9a-f]{{7,40}})$", run.get("head_branch") or "")
        base = m.group(1) if m else None
        found: list[int] = []
        sha = run["head_sha"]
        for _ in range(10):
            if base and sha.startswith(base):
                break   # reached main's tip: the chain is complete
            commit = self.api(f"commits/{sha}")
            msg = ((commit or {}).get("commit") or {}).get("message", "")
            first = msg.splitlines()[0] if msg else ""
            pr = re.search(r"Merge pull request #(\d+)", msg) or re.search(r"\(#(\d+)\)\s*$", first)
            if pr:
                found.append(int(pr.group(1)))
            parents = (commit or {}).get("parents", [])
            if not parents:
                break
            sha = parents[0]["sha"]
        if own not in found:
            found.append(own)
        return tuple(dict.fromkeys(found))

    def own_run_green(self, head_sha: str) -> bool | None:
        data = self.api(f"commits/{head_sha}/check-runs?check_name={REQUIRED_CHECK.replace(' ', '%20')}&per_page=50")
        runs = [c for c in (data or {}).get("check_runs", []) if c.get("status") == "completed"]
        if not runs:
            return None
        latest = max(runs, key=lambda c: c.get("completed_at") or "")
        return latest.get("conclusion") == "success"

    def comment_bodies(self, number: int) -> list[str]:
        data = self.api(f"issues/{number}/comments?per_page=100", paginate=True) or []
        return [c.get("body", "") for c in data]

    def queue_status(self) -> dict | None:
        owner, name = self.repo.split("/")
        q = ("query($o:String!,$r:String!,$b:String!){repository(owner:$o,name:$r){mergeQueue(branch:$b){"
             "configuration{mergeMethod mergingStrategy maximumEntriesToBuild maximumEntriesToMerge "
             "minimumEntriesToMerge minimumEntriesToMergeWaitTime checkResponseTimeout} "
             "entries(first:20){nodes{position state enqueuedAt pullRequest{number}}}}}}")
        data = self.graphql(q, o=owner, r=name, b=QUEUE_BRANCH)
        return ((data or {}).get("data") or {}).get("repository", {}).get("mergeQueue")

    # ── writes ──
    def comment(self, number: int, body: str) -> None:
        self.api(f"issues/{number}/comments", "-f", f"body={body}", method="POST")

    def label(self, number: int) -> None:
        subprocess.run(["gh", "label", "create", LABEL, "--repo", self.repo, "--force",
                        "--color", "B60205", "--description",
                        "The merge queue rejected this PR on an uncatalogued failure; a human owns it now"],
                       capture_output=True, text=True, check=False)
        self.api(f"issues/{number}/labels", "-f", f"labels[]={LABEL}", method="POST")

    def enqueue(self, node_id: str, head_sha: str) -> str:
        try:
            self.graphql("mutation($id:ID!,$oid:GitObjectID!){enqueuePullRequest(input:{pullRequestId:$id,expectedHeadOid:$oid}){mergeQueueEntry{position}}}",
                         id=node_id, oid=head_sha)
            return "enqueued"
        except RuntimeError as e:
            # The queue only admits a PR whose own required checks are green. When they are not
            # yet (a fresh head), arm auto-merge instead: GitHub enqueues it the moment they are.
            self.graphql("mutation($id:ID!){enablePullRequestAutoMerge(input:{pullRequestId:$id}){clientMutationId}}", id=node_id)
            return f"armed auto-merge (direct enqueue refused: {str(e)[:160]})"


# ─────────────────────────────── act ───────────────────────────────

def render_comment(decision: Decision, ctx: Context, evidence: RunEvidence | None, requeue_outcome: str | None) -> str:
    icon = {"requeue": "🔁", "reject": "⛔", "comment": "ℹ️"}.get(decision.action, "•")
    title = {"requeue": "re-queued", "reject": "left dequeued", "comment": "no action"}.get(decision.action, decision.action)
    if decision.kind == "bisect":
        icon, title = "🔀", "re-queued alone (bisecting)"
    lines = [f"{icon} **Merge-queue steward: {title}** — {decision.summary}."]
    if evidence:
        lines.append(f"Group build: {evidence.run_url}")
    for d in decision.details:
        lines.append(f"- {d}")
    if decision.action == "reject" and decision.kind in ("uncatalogued", "unclassifiable", "build"):
        lines.append("")
        lines.append("Not a catalogued flake. Fix the failure, or — with run URLs, an issue and an assertion-message "
                     "pattern — add it to `.github/known-flakes.json` (see Doc/Architecture/MergeQueue). "
                     f"Re-queue with `gh pr merge <n> --auto` once the head is green; label `{LABEL}` marks this PR as needing a person.")
    if requeue_outcome:
        lines.append("")
        lines.append(f"_{requeue_outcome}_")
    if decision.action == "requeue":
        total = sum(ctx.attempts.values()) + 1
        lines.append(f"<!-- steward: requeued={total} head={ctx.head_sha} kind={decision.kind} -->")
    return "\n".join(lines)


def act(args) -> int:
    repo = os.environ.get("GH_REPO") or os.environ.get("GITHUB_REPOSITORY")
    if not repo:
        print("::error::GH_REPO (or GITHUB_REPOSITORY) must name the repository")
        return 2
    if not args.dry_run and not os.environ.get("GH_TOKEN"):
        # A write needs the App installation token explicitly. A dry run may read with whatever
        # `gh` is logged in as — that is how the decision is rehearsed from a workstation.
        print("::error::GH_TOKEN must carry a token with pull_requests:write (the App installation token) to act")
        return 2
    gh = Gh(repo)
    catalogue = load_catalogue(Path(CATALOGUE_PATH).read_text(encoding="utf-8"))
    pr = gh.pull_request(args.pr)
    head_sha = pr["head"]["sha"]
    today = dt.date.today()
    attempts = count_attempts(gh.comment_bodies(args.pr), head_sha)
    reason = args.reason.upper()

    evidence = None
    group: tuple[int, ...] = (args.pr,)
    own_green: bool | None = None
    if reason == "CI_FAILURE":
        run = gh.latest_failed_merge_group_run(args.pr)
        if run:
            with tempfile.TemporaryDirectory(prefix="steward-") as tmp:
                evidence = gh.run_evidence(run, Path(tmp))
            group = gh.group_pull_requests(run, args.pr)
            own_green = gh.own_run_green(head_sha)

    ctx = Context(reason, args.pr, head_sha, group, own_green, attempts, today)
    decision = classify(ctx, evidence, catalogue)

    print(f"PR #{args.pr} head {head_sha[:9]} reason {reason} → {decision.action} ({decision.kind}): {decision.summary}")
    for d in decision.details:
        print(f"  - {d}")
    print(f"  group={group} own_run_green={own_green} attempts={attempts}")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as fh:
            fh.write(f"### Steward: {decision.action} ({decision.kind})\n\n{decision.summary}\n\n")
            for d in decision.details:
                fh.write(f"- {d}\n")

    if decision.action == "noop":
        return 0
    if args.dry_run:
        print("dry run — nothing written")
        print(render_comment(decision, ctx, evidence, None))
        return 0
    if pr.get("state") != "open":
        print(f"PR is {pr.get('state')} — nothing to act on")
        return 0

    outcome = None
    if decision.action == "requeue":
        outcome = gh.enqueue(pr["node_id"], head_sha)
    body = render_comment(decision, ctx, evidence, outcome)
    if decision.action == "comment":
        # Say it ONCE per head: a queue cleared three times is three events, one message.
        marker = f"<!-- steward: noted reason={reason} head={head_sha} -->"
        if any(marker in b for b in gh.comment_bodies(args.pr)):
            print("already noted for this head — not repeating")
            return 0
        body = f"{body}\n{marker}"
    gh.comment(args.pr, body)
    if decision.action == "reject":
        gh.label(args.pr)
    return 0


def status(args) -> int:
    repo = os.environ.get("GH_REPO") or os.environ.get("GITHUB_REPOSITORY")
    if not repo:
        print("::error::GH_REPO (or GITHUB_REPOSITORY) must name the repository")
        return 2
    gh = Gh(repo)
    catalogue = load_catalogue(Path(CATALOGUE_PATH).read_text(encoding="utf-8"))
    today = dt.date.today()
    print(f"catalogue: {len(catalogue)} entries, {sum(1 for e in catalogue if e.active(today))} active on {today}")
    for e in catalogue:
        print(f"  {'active ' if e.active(today) else 'EXPIRED'} {e.id}  {e.test_name}  expires {e.expires}  {e.issue}")
    mq = gh.queue_status()
    if mq is None:
        print(f"merge queue on {QUEUE_BRANCH}: NOT ENABLED (mergeQueue is null) — add the merge_queue rule to the ruleset; see Doc/Architecture/MergeQueue")
        return 0
    cfg = mq.get("configuration") or {}
    drift = {k: (cfg.get(k), v) for k, v in RECOMMENDED_QUEUE.items() if cfg.get(k) != v}
    print(f"merge queue on {QUEUE_BRANCH}: enabled; configuration {json.dumps(cfg)}")
    for k, (live, want) in drift.items():
        print(f"::warning::queue setting {k} is {live}, the documented recommendation is {want}")
    for e in (mq.get("entries") or {}).get("nodes", []):
        print(f"  entry {e['position']}: PR #{e['pullRequest']['number']} {e['state']} since {e['enqueuedAt']}")
    return 0


# ─────────────────────────────── self-test ───────────────────────────────

def self_test() -> int:
    today = dt.date(2026, 9, 2)
    ok = True

    def check(cond: bool, what: str):
        nonlocal ok
        print(("  ok   " if cond else "  FAIL ") + what)
        ok = ok and cond

    def entry(pattern: str, expires: str = "2026-09-30", added: str = "2026-09-02") -> FlakeEntry:
        return FlakeEntry("e1", re.compile(pattern), "Some.Test", "https://github.com/Systemorph/MeshWeaver/issues/1",
                          dt.date.fromisoformat(expires), dt.date.fromisoformat(added), "self-test",
                          ("https://github.com/Systemorph/MeshWeaver/actions/runs/1",))

    flaky = Failure("MeshWeaver.X.Test.SomeTest.Case", "System.TimeoutException : The operation has timed out.",
                    "at MeshWeaver.X.Test.SomeTest.Case() in SomeTest.cs:line 10", "2")
    honest = Failure("MeshWeaver.Y.Test.Other.Case", "Expected value to be 3, but found 2.", "at Other.Case()", "2")
    cat = (entry(r"TimeoutException : The operation has timed out"),)
    no_attempts = {k: 0 for k in CAPS}

    def ctx(reason="CI_FAILURE", group=(7,), own=None, attempts=None, pr=7):
        return Context(reason, pr, "abc123abc123", group, own, dict(attempts or no_attempts), today)

    def ev(failed_jobs=(), shards=()):
        return RunEvidence(1, "https://github.com/Systemorph/MeshWeaver/actions/runs/1", tuple(failed_jobs), tuple(shards))

    def shard(*failures, steps=(TEST_STEP,), present=True, markers=()):
        return Shard("2", tuple(steps), present, tuple(failures), tuple(markers))

    print("classify:")
    d = classify(ctx(), ev(failed_jobs=("Build solution (once)",)), cat)
    check(d.action == "reject" and d.kind == "build", "build error ⇒ rejected")
    d = classify(ctx(), ev(shards=(shard(flaky),)), cat)
    check(d.action == "requeue" and d.kind == "flake", "catalogued assertion ⇒ requeue (flake)")
    d = classify(ctx(), ev(shards=(shard(honest),)), cat)
    check(d.action == "reject" and d.kind == "uncatalogued", "uncatalogued assertion ⇒ rejected")
    d = classify(ctx(), ev(shards=(shard(flaky, honest),)), cat)
    check(d.action == "reject" and d.kind == "uncatalogued" and len(d.matched) == 1, "one catalogued + one not ⇒ rejected (the honest one decides)")
    d = classify(ctx(), ev(shards=(shard(flaky),)), (entry(r"TimeoutException", expires="2026-09-01", added="2026-08-02"),))
    check(d.action == "reject" and d.kind == "uncatalogued", "expired entry ⇒ treated as uncatalogued ⇒ rejected")
    d = classify(ctx(attempts={**no_attempts, "flake": 2}), ev(shards=(shard(flaky),)), cat)
    check(d.action == "reject" and d.kind == "cap", "flake cap reached ⇒ rejected")
    d = classify(ctx(attempts={**no_attempts, "flake": 1}), ev(shards=(shard(flaky),)), cat)
    check(d.action == "requeue" and "attempt 2 of 2" in d.summary, "one flake requeue spent ⇒ second still allowed")
    d = classify(ctx(group=(5, 7), own=True), ev(shards=(shard(honest),)), cat)
    check(d.action == "requeue" and d.kind == "bisect", "multi-PR group + own run green ⇒ bisect requeue")
    d = classify(ctx(group=(5, 7), own=False), ev(shards=(shard(honest),)), cat)
    check(d.action == "reject", "multi-PR group + own run red ⇒ rejected")
    d = classify(ctx(group=(5, 7), own=None), ev(shards=(shard(honest),)), cat)
    check(d.action == "reject", "multi-PR group + own run unknown ⇒ rejected (unknown is not green)")
    d = classify(ctx(group=(5, 7), own=True, attempts={**no_attempts, "bisect": 1}), ev(shards=(shard(honest),)), cat)
    check(d.action == "reject" and d.kind == "uncatalogued", "bisect already spent ⇒ rejected as the culprit")
    d = classify(ctx(group=(7,), own=True), ev(shards=(shard(honest),)), cat)
    check(d.action == "reject", "solo group + own run green ⇒ rejected (nothing to bisect against)")
    d = classify(ctx(reason="CI_TIMEOUT"), None, cat)
    check(d.action == "requeue" and d.kind == "timeout", "CI_TIMEOUT ⇒ requeue once")
    d = classify(ctx(reason="CI_TIMEOUT", attempts={**no_attempts, "timeout": 2}), None, cat)
    check(d.action == "reject" and d.kind == "cap", "CI_TIMEOUT twice already ⇒ rejected")
    d = classify(ctx(), ev(shards=(shard(steps=("Upload artifact: shard test results",), present=False),)), cat)
    check(d.action == "requeue" and d.kind == "infra", "shard died on an infrastructure step with no artifact ⇒ requeue (infra)")
    d = classify(ctx(), ev(shards=(shard(steps=(TEST_STEP,), present=False),)), cat)
    check(d.action == "reject" and d.kind == "unclassifiable", "test step failed and no artifact ⇒ rejected")
    d = classify(ctx(), ev(shards=(shard(steps=("Summarize test failures (this shard)",), present=False),)), cat)
    check(d.action == "reject", "a verdict step failed and no artifact ⇒ rejected (not infrastructure)")
    d = classify(ctx(), ev(shards=(shard(markers=("[CI] MeshWeaver.Z.Test exit=124 TIMEOUT (8m cap)",)),)), cat)
    check(d.action == "reject" and d.kind == "unclassifiable", "dead host without a recorded failure ⇒ rejected")
    d = classify(ctx(), ev(shards=(shard(),)), cat)
    check(d.action == "reject" and d.kind == "unclassifiable", "red shard whose artifact shows nothing ⇒ rejected")
    d = classify(ctx(), None, cat)
    check(d.action == "reject", "no merge_group run found ⇒ rejected")
    d = classify(ctx(), ev(), cat)
    check(d.action == "reject", "run red with no recognisable failing job ⇒ rejected")
    d = classify(ctx(reason="MANUAL"), None, cat)
    check(d.action == "comment", "MANUAL ⇒ comment only")
    d = classify(ctx(reason="MERGE"), None, cat)
    check(d.action == "noop", "MERGE ⇒ noop")
    named = Failure("MeshWeaver.X.Test.TimeoutExceptionTest.Case", "Expected 1 but found 2.", "", "2")
    d = classify(ctx(), ev(shards=(shard(named),)), (entry(r"TimeoutException"),))
    check(d.action == "reject", "pattern matching only the TEST NAME does not catalogue (evidence is the message)")

    print("catalogue validation:")
    good = json.dumps({"entries": [{"id": "x", "assertionPattern": "timed out", "testName": "T", "issue": "https://github.com/Systemorph/MeshWeaver/issues/9",
                                    "expires": "2026-09-30", "addedOn": "2026-09-02", "addedBy": "me",
                                    "evidence": ["https://github.com/Systemorph/MeshWeaver/actions/runs/1"]}]})
    check(len(load_catalogue(good)) == 1, "a well-formed entry loads")
    check(len(load_catalogue(json.dumps({"entries": []}))) == 0, "an empty catalogue loads")

    def refuses(mutation: dict, what: str):
        e = json.loads(good)["entries"][0]
        e.update(mutation)
        e = {k: v for k, v in e.items() if v is not None}
        try:
            load_catalogue(json.dumps({"entries": [e]}))
            check(False, what)
        except CatalogueError:
            check(True, what)

    refuses({"issue": None}, "missing issue ⇒ refused")
    refuses({"issue": "https://example.com/1"}, "non-GitHub issue URL ⇒ refused")
    refuses({"expires": "2026-11-30"}, "expiry more than 30 days out ⇒ refused")
    refuses({"expires": "2026-08-01"}, "expiry before addedOn ⇒ refused")
    refuses({"assertionPattern": "("}, "invalid regex ⇒ refused")
    refuses({"assertionPattern": ".*"}, "pattern matching everything ⇒ refused")
    refuses({"evidence": []}, "no evidence ⇒ refused")
    refuses({"evidence": ["https://github.com/Systemorph/MeshWeaver/pull/1"]}, "evidence that is not a run URL ⇒ refused")

    print("evidence parsing:")
    trx = ('<?xml version="1.0"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
           '<UnitTestResult testName="A.B.C" outcome="Failed"><Output><ErrorInfo><Message>boom\nline2</Message>'
           '<StackTrace>at A.B.C()</StackTrace></ErrorInfo></Output></UnitTestResult>'
           '<UnitTestResult testName="A.B.D" outcome="Passed"/></Results></TestRun>')
    fs = parse_trx(trx, "3")
    check(len(fs) == 1 and fs[0].test == "A.B.C" and fs[0].message.startswith("boom") and fs[0].stack == "at A.B.C()" and fs[0].shard == "3",
          "trx: failed results are extracted with message and stack; passed ones are not")
    ms = parse_dead_host_markers("[CI] MeshWeaver.A.Test exit=1 TESTFAIL (1 failing) elapsed=3s\n"
                                 "[CI] MeshWeaver.B.Test (part 2/3) exit=124 TIMEOUT (8m cap) elapsed=480s\n"
                                 "[CI] MeshWeaver.C.Test exit=0 elapsed=9s")
    check(len(ms) == 1 and "MeshWeaver.B.Test" in ms[0], "markers: only a non-TESTFAIL non-zero exit is a dead host")
    at = count_attempts(["hello", "<!-- steward: requeued=1 head=abc123abc123 kind=flake -->",
                         "<!-- steward: requeued=2 head=abc123abc123 kind=bisect -->",
                         "<!-- steward: requeued=1 head=fffffffff kind=flake -->"], "abc123abc123")
    check(at["flake"] == 1 and at["bisect"] == 1 and at["timeout"] == 0, "attempts are counted per kind and per head sha")
    body = render_comment(classify(ctx(), ev(shards=(shard(flaky),)), cat), ctx(), ev(shards=(shard(flaky),)), "enqueued")
    check("<!-- steward: requeued=1 head=abc123abc123 kind=flake -->" in body and "issues/1" in body,
          "a requeue comment carries the marker and the catalogue issue")
    body = render_comment(classify(ctx(), ev(shards=(shard(honest),)), cat), ctx(), ev(shards=(shard(honest),)), None)
    check("<!-- steward: requeued" not in body and "Expected value to be 3" in body, "a reject comment names the assertion and spends no attempt")

    print("self-test " + ("PASSED" if ok else "FAILED"))
    return 0 if ok else 1


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--self-test", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    a = sub.add_parser("act")
    a.add_argument("--pr", type=int, required=True)
    a.add_argument("--reason", required=True)
    a.add_argument("--dry-run", action="store_true")
    a.add_argument("--repo", help="owner/name (default: GH_REPO or GITHUB_REPOSITORY)")
    s = sub.add_parser("status")
    s.add_argument("--repo", help="owner/name (default: GH_REPO or GITHUB_REPOSITORY)")
    args = ap.parse_args(argv)
    if getattr(args, "repo", None):
        os.environ["GH_REPO"] = args.repo
    if args.self_test:
        return self_test()
    if args.cmd == "act":
        return act(args)
    if args.cmd == "status":
        return status(args)
    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
