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
  4. Otherwise the run judged an OLD body. Verdicts are compared PER GATE, never as one aggregate: an
     edit can clear one red gate and break a green one while the run stays red overall, and a later
     partial re-run would then refresh only the first. Below, "green"/"red" is per gate. The current body is judged here with the SAME gate scripts
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


GATES = BODY_GATES[1:]
# preflight-pr.py result name → the Build-and-Test job that runs the same gate script.
PREFLIGHT_TO_JOB = {
    "Pairs-with (cross-repo pair)": "Cross-repo pair (public surface)",
    "Satellite-pins (package pin removal)": "Satellite package pins (removal declared)",
    "Implementers (interface additions)": "Interface additions (implementers declared)",
    "Mirror-sync (i18n catalog)": "i18n catalog (mirror sync handed over)",
    "Closing keywords / Verified-closing": "Closing keywords (no accidental close)",
}


def job_state(job: dict | None) -> str:
    """'green' | 'red' | 'pending' for one job (skipped counts as passed, as the stage gate reads it)."""
    if job is None:
        return "green"
    if job.get("status") != "completed":
        return "pending"
    return "red" if job.get("conclusion") in RED else "green"


def decide(pr: dict, head: str, run: dict | None, jobs: list[dict], edited_at: dt.datetime, body_verdict) -> Action:
    """The decision table in the module docstring, PER GATE. `body_verdict()` is called only when it matters
    and answers {job name: 'green'|'red'|'unknown'} (a bare string applies to every gate)."""
    if pr.get("state") != "open":
        return Action("none", f"the pull request is {pr.get('state')} — nothing to keep true")
    if (pr.get("head") or {}).get("sha") != head:
        return Action("none", "the head moved since this edit — the new head's run reads the current body")
    if run is None:
        return Action("none", "no Build-and-Test run for this head yet — the run to come reads the current body")
    by_name = {j.get("name"): j for j in jobs}
    reader = by_name.get(BODY_READER)
    started = [parse_time(j.get("started_at")) for j in jobs
               if j.get("name") in BODY_GATES and j.get("status") != "queued" and j.get("started_at")]
    if reader is None or reader.get("status") in ("queued", "waiting", "pending", "requested") or not started:
        return Action("none", "the run's body reader has not started — it will read the current body")
    read_at = min(started)
    if read_at >= edited_at:
        return Action("none", f"the run read the body at {read_at.isoformat()}, after the edit "
                              f"({edited_at.isoformat()}) — its verdict is already the current body's")
    completed = run.get("status") == "completed"
    if job_state(reader) == "red":
        return (Action("rerun-reader", "the run's body reader itself failed — re-running it")
                if completed else Action("wait", "the run's body reader failed — waiting for the run to finish"))
    raw = body_verdict()
    current = {g: raw for g in GATES} if isinstance(raw, str) else {g: raw.get(g, "unknown") for g in GATES}
    old = {g: job_state(by_name.get(g)) for g in GATES}
    # A gate that passed (or is still judging) the OLD body but does not pass the current one: the run
    # would carry a green the current body has not earned — whatever the aggregate says.
    regressed = [g for g in GATES if old[g] in ("green", "pending") and current[g] != "green"]
    # A gate that failed the OLD body but may pass the current one: the red is no longer known true.
    cleared = [g for g in GATES if old[g] == "red" and current[g] != "red"]
    pending = [g for g in GATES if old[g] == "pending"]
    if regressed:
        why = ", ".join(f"{g} ({old[g]} → {current[g]})" for g in regressed)
        if completed:
            return Action("rerun-reader", f"the current body regresses gate(s) the run judged on the OLD body: {why} — "
                                          "re-running the body reader so the required check reflects it")
        return Action("cancel-then-rerun-reader", f"the current body regresses gate(s) the in-flight run judged on the "
                                                  f"OLD body: {why} — cancelling it before it finishes on stale input, "
                                                  "then re-running the body reader")
    if pending:
        return Action("wait", "the run's body gates are still judging the OLD body; the current body passes them — "
                              "waiting for their verdict")
    if cleared:
        why = ", ".join(f"{g} (red → {current[g]})" for g in cleared)
        if not completed:
            return Action("wait", f"gate(s) red on the OLD body may pass the current one: {why} — waiting for the "
                                  "held run to finish")
        return Action("rerun-reader", f"gate(s) red on the OLD body may pass the current one: {why} — re-running the "
                                      "body reader job (the gates re-read the body; the held build runs)")
    return Action("none", "every body gate's verdict on the OLD body is its verdict on the current body — the "
                          "required check is still true; nothing re-runs")


def pick_run(runs: list[dict], number: int) -> dict | None:
    """The newest run ASSOCIATED WITH THIS pull request. Two pull requests can share a head sha, so a run
    is never chosen by sha alone; runs for the sha that name no pull request at all are refused."""
    mine = [r for r in runs if any(p.get("number") == number for p in (r.get("pull_requests") or []))]
    if not mine and any(not r.get("pull_requests") for r in runs):
        raise RuntimeError("a Build-and-Test run for this head names no pull request — it cannot be attributed "
                           "to this one; re-run by hand if it is")
    mine.sort(key=lambda r: (r.get("created_at") or "", r.get("id") or 0))
    return mine[-1] if mine else None


def newest_per_name(jobs: list[dict]) -> list[dict]:
    """Across EVERY attempt: the newest execution of each job. A partial re-run's attempt lists only the
    jobs it re-ran, so `filter=latest` alone loses a reader that succeeded in an earlier attempt."""
    best: dict[str, dict] = {}
    for j in jobs:
        key = j.get("name")
        rank = (int(j.get("run_attempt") or 0), j.get("started_at") or "", int(j.get("id") or 0))
        cur = best.get(key)
        if cur is None or rank > (int(cur.get("run_attempt") or 0), cur.get("started_at") or "", int(cur.get("id") or 0)):
            best[key] = j
    return list(best.values())


# ── GitHub ──────────────────────────────────────────────────────────────────────────────────────


class Api:
    def __init__(self, repo: str, token: str):
        import ghrest
        self.g, self.repo, self.token = ghrest, repo, token

    def pr(self, number: int) -> dict:
        return self.g.get(f"repos/{self.repo}/pulls/{number}", self.token)

    def latest_run(self, head: str, number: int) -> dict | None:
        data = self.g.get(f"repos/{self.repo}/actions/workflows/{WORKFLOW_FILE}/runs"
                          f"?event=pull_request&head_sha={head}&per_page=50", self.token)
        return pick_run(data.get("workflow_runs") or [], number)

    def run(self, run_id: int) -> dict:
        return self.g.get(f"repos/{self.repo}/actions/runs/{run_id}", self.token)

    def jobs(self, run_id: int) -> list[dict]:
        out: list[dict] = []
        for page in range(1, 21):
            data = self.g.get(f"repos/{self.repo}/actions/runs/{run_id}/jobs?filter=all&per_page=100&page={page}",
                              self.token)
            batch = data.get("jobs") or []
            out.extend(batch)
            if len(out) >= int(data.get("total_count") or 0) or not batch:
                return newest_per_name(out)
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


def measure_body(base_ref: str, workdir: Path):
    """Judge the CURRENT body with the same gate scripts CI runs, PER GATE: {job name: 'green'|'red'|'unknown'}
    (or 'unknown' for every gate when the body or the preflight could not be read)."""
    body = workdir / "current-pr-body.md"
    report = workdir / "preflight-body.json"
    read = subprocess.run([sys.executable, "scripts/read-current-pr-body.py", "--output", str(body)])
    if read.returncode != 0:
        print("::warning::could not read the current body — treating every gate's verdict as UNKNOWN")
        return "unknown"
    subprocess.run([sys.executable, "scripts/preflight-pr.py", "--only", "body", "--base",
                    f"origin/{base_ref}", "--body-file", str(body), "--json", str(report)])
    try:
        results = json.loads(report.read_text(encoding="utf-8"))["results"]
    except (OSError, ValueError, KeyError):
        print("::warning::the preflight wrote no report — treating every gate's verdict as UNKNOWN")
        return "unknown"
    status = {"PASS": "green", "FAIL": "red"}
    verdicts = {job: "unknown" for job in GATES}
    for r in results:
        job = PREFLIGHT_TO_JOB.get(r.get("name"))
        if job:
            verdicts[job] = status.get(r.get("status"), "unknown")
    # The surface detector feeds the pair AND the implementer gate; when it fails, neither is known.
    if any(r.get("name", "").startswith("Public-surface detector") and r.get("status") == "FAIL" for r in results):
        for job in ("Cross-repo pair (public surface)", "Interface additions (implementers declared)"):
            verdicts[job] = "unknown"
    return verdicts


def regate(api, number: int, head: str, edited_at: dt.datetime, body_verdict, workflow_text: str, *,
           sleep=time.sleep, clock=time.monotonic, budget: float = WAIT_BUDGET_SECONDS, poll: float = POLL_SECONDS) -> int:
    cached: list = []

    def verdict():
        if not cached:
            cached.append(body_verdict())
            print(f"The current body, judged by the same gate scripts: {cached[0]}")
        return cached[0]

    def still_current() -> str | None:
        """Re-read the pull request right before posting: a push or a close since the decision means
        the re-run would be for an obsolete head — and it shares the new head's concurrency group."""
        now = api.pr(number)
        if now.get("state") != "open":
            return f"the pull request is now {now.get('state')}"
        if (now.get("head") or {}).get("sha") != head:
            return "the head moved while this lane worked — the new head's run reads the current body"
        return None

    deadline = clock() + budget
    while True:
        pr = api.pr(number)
        try:
            run = api.latest_run(head, number)
        except RuntimeError as exc:
            print(f"::error::{exc}")
            return 1
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
            if action.kind == "cancel-then-rerun-reader":
                gone = still_current()
                if gone:
                    print(f"[none] {gone} — not cancelling")
                    return 0
                api.cancel(run["id"])
                while api.run(run["id"]).get("status") != "completed":
                    if clock() >= deadline:
                        print(f"::error::Cancelled run {run.get('html_url')} did not complete in time — re-run its job "
                              f"`{BODY_READER}` by hand once it has.")
                        return 1
                    sleep(poll)
            gone = still_current()
            if gone:
                print(f"[none] {gone} — not re-running the old head's body reader")
                return 0
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
        self.pr_reads = getattr(self, "pr_reads", 0) + 1
        if getattr(self, "head_moves_after", None) is not None and self.pr_reads > self.head_moves_after:
            return {"state": "open", "head": {"sha": "c" * 40}}
        return self._pr

    def latest_run(self, head, number):
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
    closing = "Closing keywords (no accidental close)"
    pins = "Satellite package pins (removal declared)"
    same = {g: ("red" if g == closing else "green") for g in GATES}
    case("red run, the SAME per-gate verdicts on the current body: nothing", FakeApi(jobs=_jobs("red")), same, [])
    # Copilot #4421715005: the edit clears the red gate AND breaks a green one; the aggregate stays red,
    # and an aggregate comparison would do nothing — leaving the broken gate's stale success for a later
    # partial re-run to keep. Per gate it is a regression, so the reader re-runs.
    swapped = {g: ("red" if g == pins else "green") for g in GATES}
    case("red→red aggregate but a green gate REGRESSED: re-run the body reader", FakeApi(jobs=_jobs("red")),
         swapped, ["rerun-job:1"])
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

    # …and that case is invisible to an AGGREGATE comparison (red before, red after) — the row above
    # tests the per-gate rule, not something an aggregate would also catch.
    agg = lambda v: "red" if any(x == "red" for x in v.values()) else "green"  # noqa: E731
    if agg(same) != agg(swapped):
        failures.append("the regressed-gate row is visible to an aggregate comparison — it proves nothing")

    # Copilot #4421715049: a partial re-run's attempt lists only what it re-ran. Across ALL attempts the
    # reader from attempt 1 is kept, so the edit is judged; with attempt 2 alone it would look unstarted.
    attempt1 = [dict(j, run_attempt=1) for j in _jobs("red")]
    attempt2 = [{"id": 20, "name": closing, "status": "completed", "conclusion": "failure",
                 "started_at": T0, "run_attempt": 2}]
    merged = newest_per_name(attempt1 + attempt2)
    if next(j for j in merged if j["name"] == closing)["id"] != 20:
        failures.append("newest_per_name did not keep the newest execution of a re-run gate")
    case("reader only in an EARLIER attempt: still judged, its id re-run", FakeApi(jobs=merged), "green",
         ["rerun-job:1"])
    case("negative control: the latest attempt ALONE hides the reader (nothing would be judged)",
         FakeApi(jobs=attempt2), "green", [])

    # Copilot #4421715079: two pull requests on one head sha — the run is chosen by PR association.
    runs = [{"id": 1, "created_at": "2026-01-01T09:00:00Z", "pull_requests": [{"number": 1}]},
            {"id": 2, "created_at": "2026-01-01T09:30:00Z", "pull_requests": [{"number": 2}]}]
    if (pick_run(runs, 1) or {}).get("id") != 1:
        failures.append("pick_run chose another pull request's (newer) run for the same sha")
    if pick_run(runs, 3) is not None:
        failures.append("pick_run returned a run for a pull request none of the runs names")
    try:
        pick_run([{"id": 9, "created_at": "x", "pull_requests": []}], 1)
        failures.append("pick_run accepted a run that names no pull request")
    except RuntimeError:
        pass

    # Copilot #4421715119: the pull request is re-read right before posting; a moved head stops it.
    moved = FakeApi(jobs=_jobs("green"))
    moved.head_moves_after = 1
    case("head moved before the re-run is posted: nothing posted", moved, "red", [])
    moved = FakeApi(run_status="in_progress", jobs=_jobs("green"))
    moved.head_moves_after = 2
    case("head moved while the cancel completed: cancelled, but the OLD reader is not re-run", moved, "red",
         ["cancel:7"])

    # The per-gate verdict depends on the preflight's result names: each must still exist there.
    if set(PREFLIGHT_TO_JOB.values()) != set(GATES):
        failures.append("PREFLIGHT_TO_JOB does not cover exactly the body-gate jobs")
    pf = Path(__file__).resolve().parents[2] / "scripts" / "preflight-pr.py"
    if pf.is_file():
        src = pf.read_text(encoding="utf-8")
        for name in PREFLIGHT_TO_JOB:
            if f'"{name}"' not in src:
                failures.append(f"scripts/preflight-pr.py no longer names the result `{name}`")
    else:
        failures.append(f"{pf} not found — the per-gate name map was not checked")

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
