#!/usr/bin/env python3
"""pr-body-regate.py — keep `Consolidate test results` true to the CURRENT pull-request body after an edit.

    python3 .github/scripts/pr-body-regate.py --repo Systemorph/MeshWeaver --pr 123 \
        --head <event head sha> --edited-at <pull_request.updated_at> --base-ref main
    python3 .github/scripts/pr-body-regate.py --self-test

Run by `.github/workflows/pr-body-edited.yml` on `pull_request: edited` when the BODY changed.

THE GAP. The declaration gates of `MeshWeaver Build and Test` (Pairs-with, Satellite-pins, Implementers,
Mirror-sync, closing keywords — all `needs: [pr-body]`) judge the body as it stood when the run's
`pr-body` job read it. That workflow does not fire on `edited`, so an edit reaches nothing: a body that
FIXES a red gate leaves the required check red until somebody re-runs by hand, and a body that BREAKS a
gate leaves a green that is no longer true.

WHY NOT `edited` ON THAT WORKFLOW. Its concurrency group cancels the in-flight run of the same pull
request, so every title or body edit would throw a running build away; and a run started for an edit
would still have to publish the required context (a skipped required context counts as satisfied).

THIS LANE NEVER CANCELS ANYTHING. It acts on the EXISTING run, re-running ONE job — `pr-body`, by id —
and only when the verdict the run carries is no longer true for the current body:

  1. The pull request is closed, or its head moved → nothing: the new head's run reads the new body.
  2. No Build-and-Test run associated with THIS pull request for the head, or its body reader has not
     STARTED → nothing: it will read the current body.
  3. The body reader started AFTER the edit → nothing: it read (or is reading) the current body.
  4. Otherwise the run judged an OLD body. The current body is judged here with the SAME gate scripts
     (`scripts/preflight-pr.py --only body`), and verdicts are compared PER GATE — never as one
     aggregate, because an edit can clear one red gate and break a green one while the run stays red:
       every gate's verdict unchanged          → nothing (the build is NOT re-run);
       a gate regressed or cleared, run DONE   → re-run the `pr-body` job: every gate needs it and
                                                 re-runs with it, re-reading the body (a new attempt
                                                 never reuses the old read); a red gate holds the build,
                                                 a cleared one releases the build it held (owed);
       a gate regressed or cleared, or a gate still judging, run IN PROGRESS
                                               → WAIT for the run to finish (bounded script poll), then
                                                 decide again. A running build is never cancelled: the
                                                 re-run happens on the finished run.
     A gate job the run has not listed yet is PENDING while the run is unfinished — never green.

🚨 NEVER `rerun-failed-jobs` ON THE WHOLE RUN. Before posting the one re-run the lane (a) asserts the run
is `.github/workflows/dotnet-test.yml` by its WORKFLOW PATH, (b) reads that workflow at the run's OWN
`head_sha` (REST contents), refusing red when it cannot, and (c) computes the `pr-body` subtree from that
revision's `needs:` graph and REFUSES, red and named, when any job in it matches /review answered/i:
`Automatic review answered` is an API-written check-run, and a re-run including a legacy job of that name
would overwrite it. Immediately before posting, the pull request is read again: closed or moved → stop.

Exit 0 when it acted or proved nothing needed doing; 1 when it could not decide or could not act
(each line says which, and what to re-run by hand).
"""
from __future__ import annotations

import argparse
import base64
import datetime as dt
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

WORKFLOW_FILE = "dotnet-test.yml"
WORKFLOW_PATH = ".github/workflows/" + WORKFLOW_FILE
READER_KEY = "pr-body"
BODY_READER = "Pull-request body (read once)"
GATES = (
    "Cross-repo pair (public surface)",
    "Satellite package pins (removal declared)",
    "Interface additions (implementers declared)",
    "i18n catalog (mirror sync handed over)",
    "Closing keywords (no accidental close)",
)
BODY_GATES = (BODY_READER, *GATES)
# preflight-pr.py result name → the Build-and-Test job that runs the same gate script.
PREFLIGHT_TO_JOB = {
    "Pairs-with (cross-repo pair)": "Cross-repo pair (public surface)",
    "Satellite-pins (package pin removal)": "Satellite package pins (removal declared)",
    "Implementers (interface additions)": "Interface additions (implementers declared)",
    "Mirror-sync (i18n catalog)": "i18n catalog (mirror sync handed over)",
    "Closing keywords / Verified-closing": "Closing keywords (no accidental close)",
}
REVIEW_JOB = re.compile(r"review[\s_-]*answered", re.IGNORECASE)
RED = ("failure", "cancelled", "timed_out", "startup_failure", "action_required")
# A held run (a red gate holds the build) finishes in minutes; a running build can take most of the job's
# 45-minute cap. Past this budget the lane is red and says what to re-run by hand.
WAIT_BUDGET_SECONDS = 40 * 60
POLL_SECONDS = 60


def parse_time(value: str | None) -> dt.datetime | None:
    if not value:
        return None
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))


@dataclass
class Action:
    kind: str           # none | wait | rerun-reader
    reason: str


def job_state(job: dict | None, run_completed: bool) -> str:
    """'green' | 'red' | 'pending' | 'absent' for one gate job. Skipped counts as passed (as the stage gate
    reads it). A job not listed yet is PENDING while the run is unfinished — never green."""
    if job is None:
        return "absent" if run_completed else "pending"
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
        return Action("none", "no Build-and-Test run for this pull request's head yet — it will read the current body")
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
    if job_state(reader, completed) == "red":
        return (Action("rerun-reader", "the run's body reader itself failed — re-running it")
                if completed else Action("wait", "the run's body reader failed — waiting for the run to finish"))
    raw = body_verdict()
    current = {g: raw for g in GATES} if isinstance(raw, str) else {g: raw.get(g, "unknown") for g in GATES}
    old = {g: job_state(by_name.get(g), completed) for g in GATES}
    pending = [g for g in GATES if old[g] == "pending"]
    regressed = [g for g in GATES if old[g] == "green" and current[g] != "green"]
    cleared = [g for g in GATES if old[g] in ("red", "absent") and current[g] != "red"]
    if not (pending or regressed or cleared):
        return Action("none", "every body gate's verdict on the OLD body is its verdict on the current body — the "
                              "required check is still true; nothing re-runs")
    why = ", ".join([f"{g} (still judging)" for g in pending]
                    + [f"{g} ({old[g]} → {current[g]})" for g in regressed + cleared])
    if not completed:
        return Action("wait", f"the run judged an OLD body and is still running: {why} — waiting for it to finish; a "
                              "running build is never cancelled")
    return Action("rerun-reader", f"the run's verdict is stale for the current body: {why} — re-running the body "
                                  "reader job (its dependents follow)")


def pick_run(runs: list[dict], number: int) -> dict | None:
    """The newest run ASSOCIATED WITH THIS pull request. Two pull requests can share a head sha, so a run
    is never chosen by sha alone; runs for the sha that name no pull request at all are refused."""
    mine = [r for r in runs if any(p.get("number") == number for p in (r.get("pull_requests") or []))]
    if not mine and any(not r.get("pull_requests") for r in runs):
        raise RuntimeError("a Build-and-Test run for this head names no pull request — it cannot be attributed "
                           "to this one; re-run its `pr-body` job by hand if it is")
    mine.sort(key=lambda r: (r.get("created_at") or "", r.get("id") or 0))
    return mine[-1] if mine else None


def newest_per_name(jobs: list[dict]) -> list[dict]:
    """Across EVERY attempt: the newest execution of each job. A partial re-run's attempt lists only the
    jobs it re-ran, so `filter=latest` alone loses a reader that succeeded in an earlier attempt."""
    def rank(j):
        return (int(j.get("run_attempt") or 0), j.get("started_at") or "", int(j.get("id") or 0))
    best: dict[str, dict] = {}
    for j in jobs:
        cur = best.get(j.get("name"))
        if cur is None or rank(j) > rank(cur):
            best[j.get("name")] = j
    return list(best.values())


# ── the re-run's scope ──────────────────────────────────────────────────────────────────────────


def workflow_jobs(text: str) -> dict[str, dict]:
    """{job key: {"name": display name, "needs": [keys]}} from a workflow file — a line parser on purpose
    (no PyYAML on the runner's system python), self-tested against the real dotnet-test.yml."""
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


def scope_refusal(run: dict, jobs: list[dict], workflow_text: str | None) -> str | None:
    """Why the body-reader re-run must NOT be posted on this run, or None when it is safe."""
    path = str(run.get("path") or "").split("@", 1)[0]
    if path != WORKFLOW_PATH:
        return (f"run {run.get('html_url') or run.get('id')} is workflow `{path or '?'}`, not `{WORKFLOW_PATH}` — "
                "refusing to re-run anything in it")
    if not workflow_text:
        return (f"{WORKFLOW_PATH} could not be read at the run's own revision ({run.get('head_sha')}) — the "
                "re-run set cannot be computed, so nothing is re-run")
    graph = workflow_jobs(workflow_text)
    if READER_KEY not in graph:
        return f"{WORKFLOW_PATH} at {run.get('head_sha')} has no `{READER_KEY}` job — the re-run set cannot be computed"
    labels = {k: graph[k]["name"] for k in rerun_subtree(graph, READER_KEY)}
    everything = {k: v["name"] for k, v in graph.items()}

    def known(name: str, table: dict) -> bool:
        return any(name in (n, k) or name.startswith(f"{n} / ") or name.startswith(f"{k} / ") for k, n in table.items())

    # The run executed the merge of its head with the base AS IT STOOD THEN; the head_sha revision read
    # here can differ from it on the base side. So a review job the RUN executed that this graph does not
    # know at all cannot be shown to be outside the re-run set — refuse rather than assume.
    unknown = sorted({str(j.get("name")) for j in jobs
                      if REVIEW_JOB.search(str(j.get("name") or "")) and not known(str(j.get("name")), everything)})
    if unknown:
        return ("the run executed review-answered job(s) " + ", ".join(f"`{n}`" for n in unknown) + " that the "
                f"workflow at {run.get('head_sha')} does not declare — the re-run set cannot be proven to exclude "
                "them. Refusing; re-run the body gates by hand")
    hits = sorted(f"{k} ({n})" for k, n in labels.items() if REVIEW_JOB.search(k) or REVIEW_JOB.search(n))
    for j in jobs:
        name = str(j.get("name") or "")
        if known(name, labels) and REVIEW_JOB.search(name):
            hits.append(f"run job `{name}`")
    if hits:
        return ("re-running `pr-body` would also re-run " + ", ".join(hits) + " — a review-answered job, whose "
                "verdict is an API-written check-run a re-run would overwrite. Refusing; re-run the body "
                "gates by hand without it")
    return None


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

    def workflow_text(self, run: dict) -> str | None:
        """The workflow file at the run's OWN revision — not the edited event's checkout."""
        path = str(run.get("path") or "").split("@", 1)[0]
        try:
            data = self.g.get(f"repos/{self.repo}/contents/{path}?ref={run.get('head_sha')}", self.token)
            return base64.b64decode(data["content"]).decode("utf-8")
        except (urllib.error.URLError, KeyError, TypeError, ValueError):
            return None

    def rerun_job(self, job_id: int) -> None:
        self.g.post(f"repos/{self.repo}/actions/jobs/{job_id}/rerun", self.token)


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


def regate(api, number: int, head: str, edited_at: dt.datetime, body_verdict, *,
           sleep=time.sleep, clock=time.monotonic, budget: float = WAIT_BUDGET_SECONDS, poll: float = POLL_SECONDS) -> int:
    cached: list = []

    def verdict():
        if not cached:
            cached.append(body_verdict())
            print(f"The current body, judged by the same gate scripts: {cached[0]}")
        return cached[0]

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
                print(f"::error::Waited {int(budget // 60)} min and run {run.get('html_url')} is still running. Once it "
                      f"finishes, re-run its `{BODY_READER}` job by hand so the body gates judge the current body.")
                return 1
            sleep(poll)
            continue
        reader = next((j for j in jobs if j.get("name") == BODY_READER), None)
        refusal = scope_refusal(run, jobs, api.workflow_text(run)) or (
            None if reader and reader.get("id") else f"run {run.get('html_url')} has no `{BODY_READER}` job id")
        if refusal:
            print(f"::error::{refusal}")
            return 1
        # Re-read right before posting: a push or a close since the decision makes the re-run obsolete.
        now = api.pr(number)
        if now.get("state") != "open" or (now.get("head") or {}).get("sha") != head:
            print("[none] the pull request closed or its head moved while this lane worked — not re-running the old "
                  "head's body reader")
            return 0
        try:
            api.rerun_job(reader["id"])
        except urllib.error.HTTPError as exc:
            # A read-only token (a Dependabot-triggered edit gets one whatever `permissions:` says) cannot
            # re-run anything. No credential is added for it: the lane says what to do, red.
            print(f"::error::GitHub refused the re-run (HTTP {exc.code}; this event's token may be read-only, as a "
                  f"Dependabot-triggered edit's is). The body gates' verdict is stale for the current body — re-run "
                  f"the `{BODY_READER}` job by hand: {run.get('html_url')}")
            return 1
        print(f"Re-ran `{BODY_READER}` (job {reader['id']}) on {run.get('html_url')}.")
        return 0


# ── self-test: the decision table, every arm, against fakes ─────────────────────────────────────

T0 = "2026-01-01T10:00:00Z"
T_EDIT = dt.datetime(2026, 1, 1, 10, 5, tzinfo=dt.timezone.utc)
T_AFTER = "2026-01-01T10:06:00Z"
HEAD = "a" * 40
CLOSING = "Closing keywords (no accidental close)"
PINS = "Satellite package pins (removal declared)"

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
  lane:
    name: lane
"""
# The same, but a review job now hangs off the body gates — the shape the assertion must refuse.
REVIEW_IN_SUBTREE = FAKE_WORKFLOW.replace("""  lane:
    name: lane
""", """  lane:
    name: lane
    needs: [stage-gate]
""")


def _jobs(state: str, started: str = T0, reader_status: str = "completed") -> list[dict]:
    """The reader plus every gate; 'red' fails the closing-keyword gate only, 'pending' leaves gates running."""
    jobs = [{"id": 1, "name": BODY_READER, "status": reader_status,
             "conclusion": "success" if reader_status == "completed" else None, "started_at": started}]
    for i, name in enumerate(GATES, start=2):
        if state == "pending":
            jobs.append({"id": i, "name": name, "status": "in_progress", "conclusion": None, "started_at": started})
        else:
            failed = state == "red" and name == CLOSING
            jobs.append({"id": i, "name": name, "status": "completed",
                         "conclusion": "failure" if failed else "success", "started_at": started})
    jobs.append({"id": 99, "name": "Build solution (once)", "status": "completed", "conclusion": "success",
                 "started_at": started})
    return jobs


class FakeApi:
    def __init__(self, pr_state="open", head=HEAD, run_status="completed", jobs=None, run=True,
                 workflow=FAKE_WORKFLOW, head_moves_after=None):
        self._pr = {"state": pr_state, "head": {"sha": head}}
        self._run = ({"id": 7, "status": run_status, "html_url": "run/7", "path": WORKFLOW_PATH, "head_sha": HEAD}
                     if run else None)
        self._jobs = jobs if jobs is not None else []
        self.workflow = workflow
        self.calls: list[str] = []
        self.workflow_reads: list[str] = []
        self.head_moves_after = head_moves_after
        self.pr_reads = 0
        self.finish_after_polls: int | None = None
        self.polls = 0

    def pr(self, n):
        self.pr_reads += 1
        if self.head_moves_after is not None and self.pr_reads > self.head_moves_after:
            return {"state": "open", "head": {"sha": "c" * 40}}
        return self._pr

    def latest_run(self, head, number):
        return self._run

    def jobs(self, run_id):
        self.polls += 1
        if self.finish_after_polls is not None and self.polls >= self.finish_after_polls:
            self._run["status"] = "completed"
            for j in self._jobs:
                if j.get("status") != "completed":
                    j.update(status="completed", conclusion="success")
        return self._jobs

    def workflow_text(self, run):
        self.workflow_reads.append(run.get("head_sha"))
        return self.workflow

    def rerun_job(self, job_id):
        self.calls.append(f"rerun-job:{job_id}")

    # The two calls this lane must never make — recorded so a regression is visible.
    def rerun_failed(self, run_id):
        self.calls.append(f"rerun-failed:{run_id}")

    def cancel(self, run_id):
        self.calls.append(f"cancel:{run_id}")


def self_test() -> int:
    # Fixture output is captured, never printed: an `::error::` from a fixture would read as a real
    # failure on the pull request (the posture of MeshWeaver.Plugins' self_test_quiet.py).
    import contextlib
    import io

    def quiet():
        return contextlib.redirect_stdout(io.StringIO())

    failures: list[str] = []
    asked: list[int] = []
    count = {"cases": 0}

    def case(label, api, verdict, expect_calls, expect_rc=0, budget=60.0):
        count["cases"] += 1

        def body_verdict():
            asked.append(1)
            return verdict
        ticks = iter(range(0, 10_000, 1))
        with quiet():
            rc = regate(api, 1, HEAD, T_EDIT, body_verdict, sleep=lambda s: None,
                        clock=lambda: float(next(ticks)), budget=budget, poll=1)
        if rc != expect_rc or api.calls != expect_calls:
            failures.append(f"{label}: rc={rc} calls={api.calls}, expected rc={expect_rc} calls={expect_calls}")

    # Nothing to do — and the body is not even judged when the run cannot be stale.
    before = len(asked)
    case("closed PR", FakeApi(pr_state="closed", jobs=_jobs("red")), "green", [])
    case("head moved", FakeApi(head="b" * 40, jobs=_jobs("red")), "green", [])
    case("no run for this PR", FakeApi(run=False), "green", [])
    case("reader still queued", FakeApi(jobs=_jobs("pending", reader_status="queued")), "red", [])
    case("reader started after the edit", FakeApi(jobs=_jobs("red", started=T_AFTER)), "green", [])
    if len(asked) != before:
        failures.append("the body was judged although the run could not have been stale")

    # Stale, finished run.
    same = {g: ("red" if g == CLOSING else "green") for g in GATES}
    swapped = {g: ("red" if g == PINS else "green") for g in GATES}
    case("green run, green body: NOTHING re-runs (the build is not repeated)", FakeApi(jobs=_jobs("green")), "green", [])
    case("red run, green body: re-run the body reader (never rerun-failed-jobs)", FakeApi(jobs=_jobs("red")), "green",
         ["rerun-job:1"])
    case("red run, the SAME per-gate verdicts: nothing", FakeApi(jobs=_jobs("red")), same, [])
    case("green run, red body: re-run the body reader", FakeApi(jobs=_jobs("green")), "red", ["rerun-job:1"])
    case("green run, unknown body: re-run the body reader (truthful)", FakeApi(jobs=_jobs("green")), "unknown",
         ["rerun-job:1"])
    case("red run, unknown body: re-run the body reader", FakeApi(jobs=_jobs("red")), "unknown", ["rerun-job:1"])
    # An edit that clears the red gate AND breaks a green one keeps the aggregate red — per gate it re-runs.
    case("red→red aggregate but a green gate REGRESSED: re-run the body reader", FakeApi(jobs=_jobs("red")),
         swapped, ["rerun-job:1"])

    def agg(v):
        return "red" if any(x == "red" for x in v.values()) else "green"
    if agg(same) != agg(swapped):
        failures.append("the regressed-gate row is visible to an aggregate comparison — it proves nothing")

    # A RUNNING build is never cancelled: the lane waits for it to finish, then re-runs the reader.
    waits = FakeApi(run_status="in_progress", jobs=_jobs("green"))
    waits.finish_after_polls = 3
    case("in-flight run, a gate regressed: WAIT for it to finish, then re-run (no cancel)", waits, "red",
         ["rerun-job:1"])
    if waits.polls < 3:
        failures.append("the regressed in-flight case re-ran before the run finished")
    case("in-flight run, verdicts unchanged and gates done: nothing (no runner held)",
         FakeApi(run_status="in_progress", jobs=_jobs("green")), "green", [])
    held = FakeApi(run_status="in_progress", jobs=_jobs("red"))
    held.finish_after_polls = 3
    case("in-flight held run, gate cleared: wait, then re-run", held, "green", ["rerun-job:1"])
    case("a wait that outlasts its budget is RED, never a silent pass (and never a cancel)",
         FakeApi(run_status="in_progress", jobs=_jobs("red")), "green", [], expect_rc=1, budget=3.0)

    # Copilot #4421953633: a gate job the run has not listed yet is PENDING while the run is unfinished.
    starting = [j for j in _jobs("green") if j["name"] in (BODY_READER, PINS)]
    case("gates not listed yet while the run starts: wait, never read as green", FakeApi(
        run_status="in_progress", jobs=[dict(j) for j in starting]), "green", [], expect_rc=1, budget=3.0)
    # …and that row depends on it: read as green, the same unlisted gates would let the lane stop at once.
    real_state = job_state
    try:
        globals()["job_state"] = lambda job, run_completed: "green" if job is None else real_state(job, run_completed)
        probe = FakeApi(run_status="in_progress", jobs=[dict(j) for j in starting])
        with quiet():
            rc = regate(probe, 1, HEAD, T_EDIT, lambda: "green", sleep=lambda s: None, clock=lambda: 0.0)
        if rc != 0:
            failures.append("negative control: unlisted gates read as green did not end the lane — the pending row proves nothing")
    finally:
        globals()["job_state"] = real_state
    if job_state(None, run_completed=False) != "pending" or job_state(None, run_completed=True) != "absent":
        failures.append("an unlisted gate job is not pending (unfinished run) / absent (finished run)")

    # The body is judged at most once per lane run, however long it waits.
    asked.clear()
    once = FakeApi(run_status="in_progress", jobs=_jobs("red"))
    once.finish_after_polls = 4
    case("judged once across waits", once, "green", ["rerun-job:1"])
    if len(asked) != 1:
        failures.append(f"the body was judged {len(asked)} times across waits, expected once")

    # Earlier attempts: a partial re-run's attempt lists only what it re-ran.
    attempt1 = [dict(j, run_attempt=1) for j in _jobs("red")]
    attempt2 = [{"id": 20, "name": CLOSING, "status": "completed", "conclusion": "failure",
                 "started_at": T0, "run_attempt": 2}]
    merged = newest_per_name(attempt1 + attempt2)
    if next(j for j in merged if j["name"] == CLOSING)["id"] != 20:
        failures.append("newest_per_name did not keep the newest execution of a re-run gate")
    case("reader only in an EARLIER attempt: still judged, its id re-run", FakeApi(jobs=merged), "green",
         ["rerun-job:1"])
    case("negative control: the latest attempt ALONE hides the reader (nothing would be judged)",
         FakeApi(jobs=attempt2), "green", [])

    # Two pull requests on one head sha — the run is chosen by PR association.
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

    # The pull request is re-read right before posting; a moved head stops it.
    case("head moved before the re-run is posted: nothing posted", FakeApi(jobs=_jobs("green"), head_moves_after=1),
         "red", [])

    # A refused re-run (a read-only token — a Dependabot-triggered edit) is RED, naming the run; nothing else.
    refused = FakeApi(jobs=_jobs("red"))

    def forbidden(job_id):
        raise urllib.error.HTTPError("https://api.github.com/x", 403, "Resource not accessible by integration",
                                     None, None)
    refused.rerun_job = forbidden
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        rc = regate(refused, 1, HEAD, T_EDIT, lambda: "green", sleep=lambda s: None, clock=lambda: 0.0)
    count["cases"] += 1
    if rc != 1 or "run/7" not in out.getvalue() or refused.calls:
        failures.append(f"a 403 on the re-run must be red naming the run (rc={rc}, calls={refused.calls})")

    # SCOPE: a failed body gate AND failed review-answered jobs — only the reader's id is re-run.
    def with_review_jobs():
        jobs = _jobs("red")
        jobs.append({"id": 50, "name": "Automatic review answered", "status": "completed",
                     "conclusion": "failure", "started_at": T0})
        jobs.append({"id": 51, "name": "lane / Automatic review answered", "status": "completed",
                     "conclusion": "failure", "started_at": T0})
        return jobs
    case("failed body gate + failed review jobs: re-run ONLY the body reader id",
         FakeApi(jobs=with_review_jobs()), "green", ["rerun-job:1"])
    if REVIEW_IN_SUBTREE == FAKE_WORKFLOW or "lane" not in rerun_subtree(workflow_jobs(REVIEW_IN_SUBTREE), READER_KEY):
        failures.append("fixture: REVIEW_IN_SUBTREE does not put the review lane inside the pr-body subtree")
    case("a review job inside the re-run subtree is REFUSED (red, nothing posted)",
         FakeApi(jobs=with_review_jobs(), workflow=REVIEW_IN_SUBTREE), "green", [], expect_rc=1)
    wrong = FakeApi(jobs=_jobs("red"))
    wrong._run["path"] = ".github/workflows/review-answered.yml"
    case("a run of another workflow is REFUSED by its path", wrong, "green", [], expect_rc=1)

    # Copilot #4421953577: the graph comes from the RUN's revision, and an unreadable one refuses.
    rev = FakeApi(jobs=_jobs("red"))
    case("the workflow graph is read at the run's own head_sha", rev, "green", ["rerun-job:1"])
    if rev.workflow_reads != [HEAD]:
        failures.append(f"the workflow was read at {rev.workflow_reads}, not the run's head_sha")
    case("the run executed a review job its revision's graph does not declare: REFUSED red",
         FakeApi(jobs=_jobs("red") + [{"id": 52, "name": "Automatic review answered (legacy)", "status": "completed",
                                        "conclusion": "failure", "started_at": T0}]), "green", [], expect_rc=1)
    case("the run's workflow revision cannot be read: REFUSED red, nothing posted",
         FakeApi(jobs=_jobs("red"), workflow=None), "green", [], expect_rc=1)

    # NEGATIVE CONTROL for the scope assertion: with it disabled, the refused case posts.
    global scope_refusal
    real_scope = scope_refusal
    try:
        scope_refusal = lambda *a, **k: None  # noqa: E731
        probe = FakeApi(jobs=with_review_jobs(), workflow=REVIEW_IN_SUBTREE)
        with quiet():
            regate(probe, 1, HEAD, T_EDIT, lambda: "green", sleep=lambda s: None, clock=lambda: 0.0)
        if probe.calls != ["rerun-job:1"]:
            failures.append(f"negative control: with the scope check disabled the refused case did not post ({probe.calls})")
    finally:
        scope_refusal = real_scope

    # NEGATIVE CONTROL for the table: one that always answers "none" must not act where the real one does.
    global decide
    real_decide = decide
    try:
        decide = lambda *a, **k: Action("none", "neutered")  # noqa: E731
        probe = FakeApi(jobs=_jobs("red"))
        with quiet():
            regate(probe, 1, HEAD, T_EDIT, lambda: "green", sleep=lambda s: None, clock=lambda: 0.0)
        if probe.calls:
            failures.append("negative control: a neutered table still acted")
    finally:
        decide = real_decide

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

    # The real dotnet-test.yml: the subtree holds the gates and the aggregator, and no review job.
    real_wf = Path(__file__).resolve().parents[1] / "workflows" / WORKFLOW_FILE
    if real_wf.is_file():
        text = real_wf.read_text(encoding="utf-8")
        g = workflow_jobs(text)
        sub = rerun_subtree(g, READER_KEY)
        for must in ("closing-keywords", "cross-repo-pair", "stage-gate", "build", "collect-results"):
            if must not in sub:
                failures.append(f"real {WORKFLOW_FILE}: `{must}` missing from the pr-body re-run subtree {sorted(sub)}")
        if any(REVIEW_JOB.search(k) or REVIEW_JOB.search(g[k]["name"]) for k in sub):
            failures.append(f"real {WORKFLOW_FILE}: a review-answered job is in the pr-body re-run subtree")
        if scope_refusal({"path": WORKFLOW_PATH, "html_url": "x", "head_sha": HEAD}, [], text):
            failures.append(f"real {WORKFLOW_FILE}: the scope check refuses the real workflow")
    else:
        failures.append(f"{real_wf} not found — the subtree of the real workflow was not measured")

    if failures:
        print("self-test FAILED:")
        for f in failures:
            print(f"  ✗ {f}")
        return 1
    print(f"✓ pr-body-regate self-test: {count['cases']} cases + direct checks — never cancels; waits for a running "
          "build and re-runs ONLY the body reader job by id (never rerun-failed-jobs, never a review-answered job, "
          "graph read at the run's own revision) when a gate's verdict changed for the current body; re-runs "
          "NOTHING when every gate's verdict holds; unlisted gates are pending; red past its wait budget.")
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
    workdir = Path(os.environ.get("RUNNER_TEMP") or ".")
    return regate(Api(args.repo, token), args.pr, args.head, parse_time(args.edited_at),
                  lambda: measure_body(args.base_ref, workdir))


if __name__ == "__main__":
    sys.exit(main())
