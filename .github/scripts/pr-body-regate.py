#!/usr/bin/env python3
"""pr-body-regate.py — keep `Consolidate test results` true to the CURRENT pull-request body after an edit.

    python3 .github/scripts/pr-body-regate.py --repo Systemorph/MeshWeaver --pr 123 \
        --head <event head sha> --edited-at <pull_request.updated_at> --base-ref main
    python3 .github/scripts/pr-body-regate.py --self-test

Run by `.github/workflows/pr-body-edited.yml` on `pull_request: edited` when the BODY changed.

THE GAP. The declaration gates of `MeshWeaver Build and Test` (Pairs-with, Satellite-pins, Implementers,
Mirror-sync, closing keywords — all `needs: [pr-body]`) judge the body as it stood when the run's
`pr-body` job read it. That workflow does not fire on `edited`, so an edit reaches nothing: a body that
FIXES a red gate leaves the required check red until somebody re-runs the failed jobs by hand, and a
body that BREAKS a gate leaves a green that is no longer true.

WHY NOT SIMPLY ADD `edited` TO THAT WORKFLOW. Its concurrency group cancels the in-flight run of the
same pull request, so every title or body edit would throw away a running build; and a run started for
an edit that changed nothing the gates read would still have to produce `Consolidate test results`
(a skipped required context counts as satisfied). This lane instead acts on the EXISTING run, and only
when the verdict the run carries is no longer true:

  1. The pull request is closed, or its head moved → nothing: the new head's run reads the new body.
  2. No Build-and-Test run for the head, or its body reader has not STARTED → nothing: it will read
     the current body.
  3. The body reader started AFTER the edit → nothing: it read (or is reading) the current body.
  4. Otherwise the run judged an OLD body. The current body is judged here with the SAME gate scripts
     (`scripts/preflight-pr.py --only body`, online), and with the run's body-gate results R:
       body green, R green    → nothing: the run's verdict holds for the current body too
                                (this is the case that must NOT re-run the build — and does not);
       body green, R red      → re-run the `pr-body` JOB (by id) once the run has finished: every body
                                gate depends on it and re-runs with it, reading the current body (a new
                                attempt never reuses the old read), and the build they held runs — it
                                never ran, so that is owed, not repeated;
       body red,   R red      → nothing: the required check is red and stays red;
       body red,   R green    → re-run the `pr-body` job (its dependents — the gates, the stage gate,
                                the aggregator — follow): the gates go red, stage 0 holds the build,
                                so the re-run costs minutes and the required check turns red. An
                                IN-FLIGHT run is cancelled first (it would otherwise finish green on
                                the old body), then re-run the same way;
       body undecided         → treated as red when R is green and as green when R is red: either way
                                the `pr-body` job is re-run (truthful; a red gate holds the build).

🚨 NEVER `rerun-failed-jobs` ON THE WHOLE RUN, and never a job outside the body-gate subtree. Every
re-run is ONE job by id — `pr-body` — plus the jobs that `need` it, which GitHub re-runs with it. Before
posting it the lane (a) asserts the run is `.github/workflows/dotnet-test.yml` by its WORKFLOW PATH, not
its name, and (b) computes that subtree from the workflow file's own `needs:` graph and REFUSES, red and
named, when any job in it matches /review answered/i: `Automatic review answered` is an API-written
check-run per head (review-answered.yml and its re-judging), and a re-run that included a legacy job of
that name would overwrite the API verdict. dotnet-test.yml carries no such job today (it omits
`review-before-suites`); the assertion is what keeps that true.
     While R is still being computed, or a held red run is finishing, the lane waits — a bounded
     script poll (the stage-advance lane's posture), never a model loop.

Exit 0 when it acted or proved nothing needed doing; 1 when it could not decide or could not act
(each line says which, and what to re-run by hand).
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import subprocess
import sys
import time
import urllib.error
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

WORKFLOW_FILE = "dotnet-test.yml"
BODY_READER = "Pull-request body (read once)"
BODY_GATES = (
    BODY_READER,
    "Cross-repo pair (public surface)",
    "Satellite package pins (removal declared)",
    "Interface additions (implementers declared)",
    "i18n catalog (mirror sync handed over)",
    "Closing keywords (no accidental close)",
)
WORKFLOW_PATH = ".github/workflows/" + "dotnet-test.yml"
READER_KEY = "pr-body"
REVIEW_JOB = __import__("re").compile(r"review[\s_-]*answered", __import__("re").IGNORECASE)
RED = ("failure", "cancelled", "timed_out", "startup_failure", "action_required")
WAIT_BUDGET_SECONDS = 20 * 60
POLL_SECONDS = 30


def parse_time(value: str | None) -> dt.datetime | None:
    if not value:
        return None
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))


@dataclass
class Action:
    kind: str           # none | wait | rerun-reader | cancel-then-rerun-reader
    reason: str


def body_gate_state(jobs: list[dict]) -> str:
    """'red' | 'pending' | 'green' over the run's body-gate jobs (skipped counts as passed, as the stage gate reads it)."""
    gates = [j for j in jobs if j.get("name") in BODY_GATES]
    if any(j.get("status") == "completed" and j.get("conclusion") in RED for j in gates):
        return "red"
    if any(j.get("status") != "completed" for j in gates):
        return "pending"
    return "green"


def decide(pr: dict, head: str, run: dict | None, jobs: list[dict], edited_at: dt.datetime, body_verdict) -> Action:
    """The decision table in the module docstring. `body_verdict()` is called only when it matters."""
    if pr.get("state") != "open":
        return Action("none", f"the pull request is {pr.get('state')} — nothing to keep true")
    if (pr.get("head") or {}).get("sha") != head:
        return Action("none", "the head moved since this edit — the new head's run reads the current body")
    if run is None:
        return Action("none", "no Build-and-Test run for this head yet — the run to come reads the current body")
    reader = next((j for j in jobs if j.get("name") == BODY_READER), None)
    started = [parse_time(j.get("started_at")) for j in jobs
               if j.get("name") in BODY_GATES and j.get("status") != "queued" and j.get("started_at")]
    if reader is None or reader.get("status") in ("queued", "waiting", "pending", "requested") or not started:
        return Action("none", "the run's body reader has not started — it will read the current body")
    read_at = min(started)
    if read_at >= edited_at:
        return Action("none", f"the run read the body at {read_at.isoformat()}, after the edit "
                              f"({edited_at.isoformat()}) — its verdict is already the current body's")
    state = body_gate_state(jobs)
    completed = run.get("status") == "completed"
    verdict = body_verdict()          # 'green' | 'red' | 'unknown'
    if state == "pending":
        if verdict == "green":
            return Action("wait", "the run's body gates are still judging the OLD body; the current body passes — "
                                  "waiting for their verdict")
        # red/unknown with gates pending on the old body: they could pass and finish green on stale input.
        return Action("cancel-then-rerun-reader" if not completed else "rerun-reader",
                      f"the current body is {verdict}; the run is judging the old one — re-judge it")
    if state == "green":
        if verdict == "green":
            return Action("none", "the run's body gates passed the old body and the same gates pass the current "
                                  "body — the verdict holds; nothing re-runs")
        if completed:
            return Action("rerun-reader", f"the run's gates passed the OLD body; the current body is {verdict} — "
                                          "re-running the body reader so the required check reflects it")
        return Action("cancel-then-rerun-reader",
                      f"the in-flight run passed the OLD body; the current body is {verdict} — cancelling it "
                      "before it finishes green on stale input, then re-running the body reader")
    # state == red
    if verdict == "red":
        return Action("none", "the run's body gates are red and the current body is red too — the required "
                              "check is red and stays red")
    if not completed:
        return Action("wait", f"the run's body gates are red on the OLD body and the current body is {verdict}; "
                              "waiting for the held run to finish before re-running its failed jobs")
    return Action("rerun-reader", f"the run's body gates were red on the OLD body; the current body is {verdict} — "
                                  "re-running the body reader job (the gates re-read the body; the held build runs)")


# ── GitHub ──────────────────────────────────────────────────────────────────────────────────────


class Api:
    def __init__(self, repo: str, token: str):
        import ghrest
        self.g, self.repo, self.token = ghrest, repo, token

    def pr(self, number: int) -> dict:
        return self.g.get(f"repos/{self.repo}/pulls/{number}", self.token)

    def latest_run(self, head: str) -> dict | None:
        data = self.g.get(f"repos/{self.repo}/actions/workflows/{WORKFLOW_FILE}/runs"
                          f"?event=pull_request&head_sha={head}&per_page=20", self.token)
        runs = sorted(data.get("workflow_runs") or [], key=lambda r: (r.get("created_at") or "", r.get("id") or 0))
        return runs[-1] if runs else None

    def run(self, run_id: int) -> dict:
        return self.g.get(f"repos/{self.repo}/actions/runs/{run_id}", self.token)

    def jobs(self, run_id: int) -> list[dict]:
        out: list[dict] = []
        for page in range(1, 21):
            data = self.g.get(f"repos/{self.repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=100&page={page}",
                              self.token)
            batch = data.get("jobs") or []
            out.extend(batch)
            if len(out) >= int(data.get("total_count") or 0) or not batch:
                return out
        raise RuntimeError(f"run {run_id} lists more than 2000 jobs")

    def rerun_job(self, job_id: int) -> None:
        self.g.post(f"repos/{self.repo}/actions/jobs/{job_id}/rerun", self.token)

    def cancel(self, run_id: int) -> None:
        self.g.post(f"repos/{self.repo}/actions/runs/{run_id}/cancel", self.token)


def workflow_jobs(text: str) -> dict[str, dict]:
    """{job key: {"name": display name, "needs": [keys]}} from a workflow file — a line parser on purpose
    (no PyYAML on the runner's system python), self-tested against the real dotnet-test.yml."""
    import re
    jobs: dict[str, dict] = {}
    lines = text.splitlines()
    in_jobs, key = False, None
    for i, line in enumerate(lines):
        if re.match(r"^jobs:\s*$", line):
            in_jobs = True
            continue
        if not in_jobs:
            continue
        if re.match(r"^\S", line):
            break
        m = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if m:
            key = m.group(1)
            jobs[key] = {"name": key, "needs": []}
            continue
        if key is None:
            continue
        m = re.match(r"^    name:\s*(.+?)\s*$", line)
        if m:
            jobs[key]["name"] = m.group(1).strip().strip("'\"")
            continue
        m = re.match(r"^    needs:\s*(.*?)\s*$", line)
        if m:
            v = m.group(1)
            if v.startswith("["):
                jobs[key]["needs"] = [x.strip().strip("'\"") for x in v.strip("[]").split(",") if x.strip()]
            elif v:
                jobs[key]["needs"] = [v.strip("'\"")]
            else:
                j = i + 1
                while j < len(lines) and re.match(r"^      - ", lines[j]):
                    jobs[key]["needs"].append(lines[j].strip()[2:].strip().strip("'\""))
                    j += 1
    return jobs


def rerun_subtree(graph: dict[str, dict], root: str) -> set[str]:
    """`root` and every job that needs it, transitively — what re-running `root` by id re-runs."""
    out = {root}
    changed = True
    while changed:
        changed = False
        for k, v in graph.items():
            if k not in out and any(n in out for n in v["needs"]):
                out.add(k)
                changed = True
    return out


def scope_refusal(run: dict, jobs: list[dict], workflow_text: str) -> str | None:
    """Why the body-reader re-run must NOT be posted on this run, or None when it is safe."""
    path = str(run.get("path") or "").split("@", 1)[0]
    if path != WORKFLOW_PATH:
        return (f"run {run.get('html_url') or run.get('id')} is workflow `{path or '?'}`, not `{WORKFLOW_PATH}` — refusing to "
                "re-run anything in it")
    graph = workflow_jobs(workflow_text)
    if READER_KEY not in graph:
        return f"{WORKFLOW_PATH} has no `{READER_KEY}` job — the re-run set cannot be computed"
    subtree = rerun_subtree(graph, READER_KEY)
    labels = {k: graph[k]["name"] for k in subtree}
    hits = sorted(f"{k} ({n})" for k, n in labels.items() if REVIEW_JOB.search(k) or REVIEW_JOB.search(n))
    for j in jobs:
        name = str(j.get("name") or "")
        if any(name == n or name == k or name.startswith(f"{n} / ") or name.startswith(f"{k} / ")
               for k, n in labels.items()) and REVIEW_JOB.search(name):
            hits.append(f"run job `{name}`")
    if hits:
        return ("re-running `pr-body` would also re-run " + ", ".join(hits) + " — a review-answered job, whose "
                "verdict is an API-written check-run a re-run would overwrite. Refusing; re-run the body "
                "gates by hand without it")
    return None


def measure_body(base_ref: str, workdir: Path) -> str:
    """Judge the CURRENT body with the same gate scripts CI runs: 'green' | 'red' | 'unknown'."""
    body = workdir / "current-pr-body.md"
    read = subprocess.run([sys.executable, "scripts/read-current-pr-body.py", "--output", str(body)])
    if read.returncode != 0:
        print("::warning::could not read the current body — treating its verdict as UNKNOWN")
        return "unknown"
    rc = subprocess.run([sys.executable, "scripts/preflight-pr.py", "--only", "body", "--base",
                         f"origin/{base_ref}", "--body-file", str(body)]).returncode
    return {0: "green", 1: "red"}.get(rc, "unknown")


def regate(api, number: int, head: str, edited_at: dt.datetime, body_verdict, workflow_text: str, *,
           sleep=time.sleep, clock=time.monotonic, budget: float = WAIT_BUDGET_SECONDS, poll: float = POLL_SECONDS) -> int:
    cached: list[str] = []

    def verdict() -> str:
        if not cached:
            cached.append(body_verdict())
            print(f"The current body, judged by the same gate scripts: {cached[0].upper()}")
        return cached[0]

    deadline = clock() + budget
    while True:
        pr = api.pr(number)
        run = api.latest_run(head)
        jobs = api.jobs(run["id"]) if run else []
        action = decide(pr, head, run, jobs, edited_at, verdict)
        print(f"[{action.kind}] {action.reason}")
        if action.kind == "none":
            return 0
        if action.kind == "wait":
            if clock() >= deadline:
                print(f"::error::Waited {int(budget // 60)} min and the run ({run.get('html_url')}) has not reached a "
                      "verdict this lane can act on. Re-run its failed jobs by hand once it finishes.")
                return 1
            sleep(poll)
            continue
        reader = next((j for j in jobs if j.get("name") == BODY_READER), None)
        refusal = scope_refusal(run, jobs, workflow_text) or (
            None if reader and reader.get("id") else f"run {run.get('html_url')} has no `{BODY_READER}` job id")
        if refusal:
            print(f"::error::{refusal}")
            return 1
        try:
            if action.kind == "rerun-reader":
                api.rerun_job(reader["id"])
            elif action.kind == "cancel-then-rerun-reader":
                api.cancel(run["id"])
                while api.run(run["id"]).get("status") != "completed":
                    if clock() >= deadline:
                        print(f"::error::Cancelled run {run.get('html_url')} did not complete in time — re-run its job "
                              f"`{BODY_READER}` by hand once it has.")
                        return 1
                    sleep(poll)
                api.rerun_job(reader["id"])
        except urllib.error.HTTPError as exc:
            print(f"::error::GitHub refused `{action.kind}` on run {run.get('html_url')}: HTTP {exc.code}. A fork's "
                  f"token cannot re-run anything — re-run by hand (`{BODY_READER}`, or the failed jobs).")
            return 1
        print(f"Requested `{action.kind}` on {run.get('html_url')}.")
        return 0


# ── self-test: the decision table, every arm, against fakes ─────────────────────────────────────

T0 = "2026-01-01T10:00:00Z"
T_EDIT = dt.datetime(2026, 1, 1, 10, 5, tzinfo=dt.timezone.utc)
T_AFTER = "2026-01-01T10:06:00Z"
HEAD = "a" * 40

# A workflow shaped like dotnet-test.yml's body-gate subtree, plus an UNRELATED review job.
FAKE_WORKFLOW = """name: MeshWeaver Build and Test
on: pull_request
jobs:
  pr-body:
    name: "Pull-request body (read once)"
  closing-keywords:
    name: "Closing keywords (no accidental close)"
    needs: [pr-body]
  stage-gate:
    needs: [pr-body, closing-keywords]
  build:
    name: "Build solution (once)"
    needs:
      - stage-gate
  collect-results:
    name: "Consolidate test results"
    needs: [build, closing-keywords]
  review-answered:
    name: Automatic review answered
"""
# The same, but a review job now hangs off the body gates — the shape the assertion must refuse.
REVIEW_IN_SUBTREE = FAKE_WORKFLOW + """  lane:
    name: lane
    needs: [stage-gate]
"""


def _jobs(state: str, started: str = T0, reader_status: str = "completed") -> list[dict]:
    conclusion = {"green": "success", "red": "failure", "pending": None}[state]
    jobs = [{"id": 1, "name": BODY_READER, "status": reader_status,
             "conclusion": "success" if reader_status == "completed" else None, "started_at": started}]
    for i, name in enumerate(BODY_GATES[1:], start=2):
        jobs.append({"id": i, "name": name, "status": "completed" if conclusion else "in_progress",
                     "conclusion": conclusion if (name == "Closing keywords (no accidental close)" or state != "red")
                     else "success", "started_at": started})
    jobs.append({"id": 99, "name": "Build solution (once)", "status": "completed", "conclusion": "success",
                 "started_at": started})
    return jobs


class FakeApi:
    def __init__(self, pr_state="open", head=HEAD, run_status="completed", jobs=None, run=True,
                 finishes_after=0):
        self._pr = {"state": pr_state, "head": {"sha": head}}
        self._run = ({"id": 7, "status": run_status, "html_url": "run/7", "path": WORKFLOW_PATH}
                     if run else None)
        self._jobs = jobs or []
        self.calls: list[str] = []
        self.finishes_after = finishes_after

    def pr(self, n):
        return self._pr

    def latest_run(self, head):
        return self._run

    def run(self, run_id):
        self.finishes_after -= 1
        if self.finishes_after <= 0:
            self._run["status"] = "completed"
        return self._run

    def jobs(self, run_id):
        return self._jobs

    def rerun_failed(self, run_id):  # must never be reached: the lane re-runs ONE job by id
        self.calls.append(f"rerun-failed:{run_id}")

    def rerun_job(self, job_id):
        self.calls.append(f"rerun-job:{job_id}")

    def cancel(self, run_id):
        self.calls.append(f"cancel:{run_id}")


def self_test() -> int:
    # Fixture output is captured, never printed: an `::error::` from a fixture would read as a real
    # failure on the pull request (the posture of MeshWeaver.Plugins' self_test_quiet.py).
    import contextlib
    import io
    quiet = lambda: contextlib.redirect_stdout(io.StringIO())  # noqa: E731
    failures: list[str] = []
    asked: list[int] = []

    def case(label, api, verdict, expect_calls, expect_rc=0, budget=60.0, workflow=FAKE_WORKFLOW):
        def body_verdict():
            asked.append(1)
            return verdict
        ticks = iter(range(0, 10_000, 1))
        with quiet():
            rc = regate(api, 1, HEAD, T_EDIT, body_verdict, workflow, sleep=lambda s: None,
                        clock=lambda: float(next(ticks)), budget=budget, poll=1)
        if rc != expect_rc or api.calls != expect_calls:
            failures.append(f"{label}: rc={rc} calls={api.calls}, expected rc={expect_rc} calls={expect_calls}")

    # Nothing to do — and the body is not even judged when the run cannot be stale.
    before = len(asked)
    case("closed PR", FakeApi(pr_state="closed", jobs=_jobs("red")), "green", [])
    case("head moved", FakeApi(head="b" * 40, jobs=_jobs("red")), "green", [])
    case("no run", FakeApi(run=False), "green", [])
    case("reader still queued", FakeApi(jobs=_jobs("pending", reader_status="queued")), "red", [])
    case("reader started after the edit", FakeApi(jobs=_jobs("red", started=T_AFTER)), "green", [])
    if len(asked) != before:
        failures.append("the body was judged although the run could not have been stale")

    # Stale, completed run — the four corners.
    case("green run, green body: NOTHING re-runs (the build is not repeated)", FakeApi(jobs=_jobs("green")), "green", [])
    case("red run, green body: re-run the body reader job (never rerun-failed-jobs)", FakeApi(jobs=_jobs("red")), "green", ["rerun-job:1"])
    case("red run, red body: nothing", FakeApi(jobs=_jobs("red")), "red", [])
    case("green run, red body: re-run the body reader", FakeApi(jobs=_jobs("green")), "red", ["rerun-job:1"])
    case("green run, unknown body: re-run the body reader (truthful)", FakeApi(jobs=_jobs("green")), "unknown",
         ["rerun-job:1"])
    case("red run, unknown body: re-run the body reader job", FakeApi(jobs=_jobs("red")), "unknown", ["rerun-job:1"])

    # Stale, IN-FLIGHT run.
    case("in-flight green gates, red body: cancel, then re-run the reader",
         FakeApi(run_status="in_progress", jobs=_jobs("green"), finishes_after=2), "red", ["cancel:7", "rerun-job:1"])
    case("in-flight green gates, green body: nothing",
         FakeApi(run_status="in_progress", jobs=_jobs("green")), "green", [])
    case("in-flight pending gates, red body: cancel, then re-run the reader",
         FakeApi(run_status="in_progress", jobs=_jobs("pending")), "red", ["cancel:7", "rerun-job:1"])
    held = FakeApi(run_status="in_progress", jobs=_jobs("red"))
    orig_jobs = held.jobs
    polls = {"n": 0}

    def finishing_jobs(run_id):
        polls["n"] += 1
        if polls["n"] >= 3:
            held._run["status"] = "completed"
        return orig_jobs(run_id)

    held.jobs = finishing_jobs
    case("in-flight red gates, green body: wait for the held run, then re-run failed jobs", held, "green",
         ["rerun-job:1"])
    case("a wait that outlasts its budget is RED, never a silent pass",
         FakeApi(run_status="in_progress", jobs=_jobs("red")), "green", [], expect_rc=1, budget=3.0)

    # The body is judged at most once per lane run, however long it waits.
    asked.clear()
    held2 = FakeApi(run_status="in_progress", jobs=_jobs("red"))
    o2 = held2.jobs
    n2 = {"n": 0}

    def fj2(run_id):
        n2["n"] += 1
        if n2["n"] >= 4:
            held2._run["status"] = "completed"
        return o2(run_id)

    held2.jobs = fj2
    case("judged once across waits", held2, "green", ["rerun-job:1"])
    if len(asked) != 1:
        failures.append(f"the body was judged {len(asked)} times across waits, expected once")

    # NEGATIVE CONTROL: a table that always answers "none" must fail the arms above that act.
    global decide
    real = decide
    try:
        decide = lambda *a, **k: Action("none", "neutered")  # noqa: E731
        probe = FakeApi(jobs=_jobs("red"))
        with quiet():
            regate(probe, 1, HEAD, T_EDIT, lambda: "green", FAKE_WORKFLOW, sleep=lambda s: None, clock=lambda: 0.0)
        if probe.calls:
            failures.append("negative control: a neutered table still acted")
    finally:
        decide = real
    probe = FakeApi(jobs=_jobs("red"))
    with quiet():
        regate(probe, 1, HEAD, T_EDIT, lambda: "green", FAKE_WORKFLOW, sleep=lambda s: None, clock=lambda: 0.0)
    if probe.calls != ["rerun-job:1"]:
        failures.append("negative control: the real table did not act where the neutered one did not")

    # SCOPE: a failed body gate AND failed review-answered jobs in the same run — only the body
    # reader's id is re-run; never a review job, never rerun-failed-jobs.
    def with_review_jobs():
        jobs = _jobs("red")
        jobs.append({"id": 50, "name": "Automatic review answered", "status": "completed",
                     "conclusion": "failure", "started_at": T0})
        jobs.append({"id": 51, "name": "lane / Automatic review answered", "status": "completed",
                     "conclusion": "failure", "started_at": T0})
        return jobs
    case("failed body gate + failed review jobs: re-run ONLY the body reader id",
         FakeApi(jobs=with_review_jobs()), "green", ["rerun-job:1"])
    case("a review job inside the re-run subtree is REFUSED (red, nothing posted)",
         FakeApi(jobs=with_review_jobs()), "green", [], expect_rc=1, workflow=REVIEW_IN_SUBTREE)
    wrong = FakeApi(jobs=_jobs("red"))
    wrong._run["path"] = ".github/workflows/review-answered.yml"
    case("a run of another workflow is REFUSED by its path", wrong, "green", [], expect_rc=1)
    # NEGATIVE CONTROL for the scope assertion: with it disabled, the refused case posts — so the
    # refusal above is the assertion firing, not an accident of the fixture.
    global scope_refusal
    real_scope = scope_refusal
    try:
        scope_refusal = lambda *a, **k: None  # noqa: E731
        probe = FakeApi(jobs=with_review_jobs())
        with quiet():
            regate(probe, 1, HEAD, T_EDIT, lambda: "green", REVIEW_IN_SUBTREE, sleep=lambda s: None,
                   clock=lambda: 0.0)
        if probe.calls != ["rerun-job:1"]:
            failures.append(f"negative control: with the scope check disabled the refused case did not post ({probe.calls})")
    finally:
        scope_refusal = real_scope
    # The real dotnet-test.yml: the subtree is computed, holds the gates and the aggregator, and no
    # review-answered job — the fact this lane relies on, measured rather than remembered.
    real_wf = Path(__file__).resolve().parents[1] / "workflows" / "dotnet-test.yml"
    if real_wf.is_file():
        g = workflow_jobs(real_wf.read_text(encoding="utf-8"))
        sub = rerun_subtree(g, READER_KEY)
        for must in ("closing-keywords", "cross-repo-pair", "stage-gate", "build", "collect-results"):
            if must not in sub:
                failures.append(f"real dotnet-test.yml: `{must}` missing from the pr-body re-run subtree {sorted(sub)}")
        if any(REVIEW_JOB.search(k) or REVIEW_JOB.search(g[k]["name"]) for k in sub):
            failures.append("real dotnet-test.yml: a review-answered job is in the pr-body re-run subtree")
        if scope_refusal({"path": WORKFLOW_PATH, "html_url": "x"}, [], real_wf.read_text(encoding="utf-8")):
            failures.append("real dotnet-test.yml: the scope check refuses the real workflow")
    else:
        failures.append(f"{real_wf} not found — the subtree of the real workflow was not measured")

    if failures:
        print("self-test FAILED:")
        for f in failures:
            print(f"  ✗ {f}")
        return 1
    print("✓ pr-body-regate self-test: acts only when the run judged an OLD body — re-runs ONLY the body reader job "
          "by id (never rerun-failed-jobs, never a review-answered job: refused red when one is in the re-run subtree "
          "or the run is another workflow) when the current body clears a red gate or breaks a green one, cancels an "
          "in-flight run first, re-runs NOTHING when the verdict still holds, waits within a budget and goes red past it.")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repo")
    ap.add_argument("--pr", type=int)
    ap.add_argument("--head", help="the event's head sha")
    ap.add_argument("--edited-at", help="the event's pull_request.updated_at (ISO 8601)")
    ap.add_argument("--base-ref", default="main")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    if not (args.repo and args.pr and args.head and args.edited_at):
        ap.error("--repo, --pr, --head and --edited-at are required (or --self-test)")
    token = os.environ.get("REGATE_TOKEN", "")
    if not token:
        print("::error::REGATE_TOKEN is empty — the lane needs the run's token (actions: write) to read and re-run runs.")
        return 1
    edited_at = parse_time(args.edited_at)
    workdir = Path(os.environ.get("RUNNER_TEMP") or ".")
    workflow_text = Path(WORKFLOW_PATH).read_text(encoding="utf-8")
    return regate(Api(args.repo, token), args.pr, args.head, edited_at,
                  lambda: measure_body(args.base_ref, workdir), workflow_text)


if __name__ == "__main__":
    sys.exit(main())
