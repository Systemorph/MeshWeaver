#!/usr/bin/env python3
"""One automatic retry for NAMED infrastructure transients — the decision half of
`retry-known-transients.yml`, extracted so it can be PROVEN offline.

"can't we automate this?" (maintainer, 2026-08-31) — the queue-drain labour that motivated this
was re-running ~25-minute CI cycles whose only failures were registry blips.

ONE implementation for the whole fleet: core's own `retry-known-transients.yml` and every node
repo's thin caller run this file through the reusable lane `node-repo-retry-transients.yml`
(fetched at the lane's `scripts-ref`). Before that lane the satellites carried hand-copied inline
shell that had drifted — three of them read only the LOG, so a self-hosted runner that died left
them nothing to read and they declined every time (MeshWeaver.Manufacturing#84).

🚨 THIS IS NOT A FLAKE-RETRIER, and four guards make that structural:

  1. It reruns ONLY when EVERY failed job is PROVEN — by a NAMED log signature when the job's own
     step failed and its log is readable; by a NAMED check-run ANNOTATION when the job failed with
     no step of its own failing (a runner death, a refused job), or when a step failed and its log
     was never written because the runner died mid-step (MeshWeaver.Plugins#1849, #2773); or as a
     CONSEQUENTIAL/verdict job that failed only because those did. One failed job without proof —
     a compile error, a test failure, anything — and it does nothing: a real red must stay red,
     and retrying it would be the investigate-the-red rule's forbidden shape.
  2. ONE attempt, ever: only the event's run_attempt 1 is judged (`--attempt`, which also pins the
     job listing to THAT attempt), so a transient that persists through the retry surfaces as the
     durable failure it is.
  3. Every decision is printed: which jobs failed, which signature matched, or which job refused
     the retry — so a rerun in the history is never a mystery.
  4. 🚨 A log this steward cannot READ is a LOUD, NAMED FAILURE (exit 1), never a decline.
     See "Why this is a script" below — this guard is the reason it exists.

────────────────────────── Why this is a script, and why it uses no `gh` ──────────────────────────

#4534: between at least 2026-09-10 and 2026-09-17 this steward read ZERO job logs, fleet-wide, and
said so in a sentence that reads like a deliberate judgement:

    job 104976536291: log unreadable (the response contains terminal escape sequences; pass
    --allow-escape-sequences to output it anyway) — cannot prove a transient, no retry.

...and then `exit 0`, so the run went GREEN. The `gh` on the hosted runner refuses to OUTPUT a
response body containing terminal escape sequences unless `--allow-escape-sequences` is passed, and
EVERY Actions log contains them — the runner echoes each `run:` script line as `\x1b[36;1m…\x1b[0m`.
So the read could never succeed, and an automation that had gone blind reported a clean decision for
a week with nobody noticing. That is precisely the skip-trapdoor AGENTS.md forbids: a gate that
cannot see its input must SAY SO, not pass.

Two things follow, and both are deliberate:

  • **No `gh` for the log read.** The refusal lives in `gh`'s OUTPUT policy, not in the API: measured
    2026-09-17, a plain REST GET of the identical URL answered HTTP 200 with 7,251 bytes over 46
    escape-bearing lines. Passing `--allow-escape-sequences` would work on the runner and FAIL on
    every older `gh` (the local 2.95.0 has neither the refusal nor the flag), trading one silent
    breakage for another; feature-detecting it would leave this steward hostage to the output policy
    of a tool that has already changed under us once. Plain REST depends on none of it. This is the
    same shape `.github/scripts/resolve-platform.py` already uses for the one job log it reads. The
    transport is the shared `ghrest.py` (fetched beside this file by the lane), which strips the
    token on any redirect that leaves GitHub's origin, so it never crosses the 302 to signed storage.

  • **A read is not believed unless it looks like a job log.** An empty or marker-less body is
    treated as UNREADABLE rather than as a log that happens to match no signature — otherwise
    "I read nothing" and "I read a clean log" become the same answer again, one exit code apart.

History: this file was core's alone while the satellites carried inline copies — fetched at no
pin and guarded by no copy check, so a fix here (#4534's log read, core's `block_signatures`, the
NuGet signatures) reached none of them. They were held back on purpose while reviving
`rerun-failed-jobs` on a satellite's `main` could erase the `Platform for this run` annotation its
PR ceiling reads (#4491); #4493 made the ceiling read the attempt that PUBLISHED the annotation, so
that hazard is closed, and a caller that still wants the whole run re-run passes
`rerun-scope: all`. MeshWeaver.Plugins keeps its own, larger steward (`scripts/retry-known-
transients.py`: run-void signatures over its own resolver, `ci.yml` cascades, runner-node lookup) —
its runner-death and verdict rules are what this file adopted.

────────────────────────────────────── The signature list ──────────────────────────────────────

CURATED. Add an entry with the incident that proved it transient, in the commit message. Signatures
assert INFRASTRUCTURE identity (the registry hostname, the exact refusal), never generic words like
"error" or "timeout".
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ghrest  # noqa: E402 — the shared GitHub REST client; lanes fetch it beside this file

GITHUB_API = "https://api.github.com"

# Bounds the text read. A job log is a stream with no declared length, so a cap is the difference
# between a bounded read and an OOM on a runner. Over the cap is a REFUSAL, never a truncated parse
# — a signature search over half a log can only produce a false "no match", i.e. a false decline.
MAX_LOG_BYTES = 16 * 1024 * 1024

# 🚨 THE POSITIVE CONTROL, built into the automation itself. Every line the Actions API serves in a
# job log is prefixed with its ISO-8601 timestamp (`2026-09-17T04:35:31.5696641Z Current runner
# version: …`), and the body opens with a UTF-8 BOM. A body carrying no such line is NOT a job log,
# whatever the status code said, and must never be classified — an empty string matches no signature
# and would otherwise read exactly like an honest "no transient here".
JOB_LOG_LINE = re.compile(r"^﻿?\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z ", re.MULTILINE)

# Matched LINE-WISE, as `grep -E` did: no DOTALL, so `.*` cannot span lines.
#   meshweaver.azurecr.io.*connection refused — 2026-08-31, ACR login refused mid-run, 10+ jobs
#     across Plugins#1032; every build that ran was green.
#   dial tcp …: connect: connection refused — the same ACR refusal seen from the containerd side.
#   registry answered 503 — #2915, the plugins registry's key-resolution outage window.
#   NuGet restore against api.nuget.org — #6364: run 37968714635 (push, 78a670a) job 113951063426
#     "Build solution (once)" died in `dotnet restore` at 17:51:31–46Z with `Connection reset by
#     peer` on every request to api.nuget.org (NuGet's own retries exhausted: `error : Failed to
#     download package 'Microsoft.Playwright.1.63.0' from 'https://api.nuget.org/…'` and `error
#     NU1301: Failed to retrieve information about 'System.Data.DataSetExtensions' from remote source
#     'https://api.nuget.org/…'`). Nothing compiled; the same tree went on to build green. Main
#     therefore settled red, CD built nothing (#6364), and this steward declined because no
#     signature named the feed. Both forms assert the feed's OWN hostname. NU1301 is NuGet's
#     "could not reach the source" code; a package that does not EXIST is NU1101/NU1102, and a
#     private feed refusing a credential names another host — neither matches.
#   NOT MEASURED — artifact store miss — ci-run-artifacts.py's exact-name miss AFTER its one re-read
#     (MeshWeaver#7656). The job measured nothing about the diff; its producer had succeeded (a
#     consumer whose producer FAILED reports that red once instead, and the failed producer vetoes
#     the retry on its own log). Matched on the EMITTED annotation title, never the echoed source.
SIGNATURES = re.compile(
    r"##\[error\]ci-run-artifacts: NOT MEASURED — artifact store miss \(infra\)"
    r"|meshweaver\.azurecr\.io.*connection refused"
    r"|dial tcp .*: connect: connection refused"
    r"|registry answered 503 for https://memex\.meshweaver\.cloud"
    r"|error NU1301: Failed to retrieve information about '[^']+' from remote source 'https://api\.nuget\.org/"
    r"|error : Failed to download package '[^']+' from 'https://api\.nuget\.org/"
)

# 🚨 CONSEQUENTIAL failures: a job that went red ONLY because an upstream job never ran. It is not a
# transient in its own right — it proves nothing about the infrastructure — so it can never cause a
# retry alone (`decide` requires at least one job matching SIGNATURES/BLOCK_SIGNATURES). But without
# it the EVERY-job rule declines every transient that kills the build: `Consolidate test results`
# then reports `The test matrix concluded 'skipped'` (the shard matrix was skipped because its
# `needs:` failed) and that line matched nothing, which is exactly how #6364's restore blip went
# un-retried. `skipped` is the only accepted conclusion: a shard that FAILED or was CANCELLED makes
# the matrix `failure`/`cancelled`, and those are that shard's own red, judged on its own log.
#
# 🔗 THE CASCADE LINE (ci-cascade-verdict.py, policy `report-a-cascade-once`): an aggregator that ran
# NONE of its own checks because an upstream job failed prints exactly one `red because <root> … —
# see it` and stops. Consequential by construction: the root is a failed job of the same run, judged
# on its own log. Matched on the EMITTED `##[error]` record only — never the echoed `run:` source.
#
# 🚦 THE STAGE-0 HOLD (node-repo-stage-gate.yml, and core's Consolidate which repeats it): the stage
# gate ran none of the suites because a stage-0 control did not SUCCEED. Consequential by
# construction: every stage-0 control that held it is a red job of the same run, judged on its own
# evidence — measured on MeshWeaver#7694 run 41100409024, where the two controls were CANCELLED by a
# lost completion (see JOB_TIME_LIMIT_ANNOTATION) and this line was the gate's only red.
STAGE0_HOLD_LINE = "STAGE 2 HELD (stage 0 red): "


def stage_gate_emits_hold(lane_text: str) -> bool:
    """True when the stage-gate lane still EMITS the hold line as an error record — an `echo` of a
    `::error …::` command whose message starts with STAGE0_HOLD_LINE. A comment or any other mention
    does not count (review on #7704): the signature is only evidence while the producer prints it."""
    return re.search(r'^\s*echo "::error[^"]*::' + re.escape(STAGE0_HOLD_LINE), lane_text, re.M) is not None


CONSEQUENTIAL_SIGNATURES = re.compile(
    r"The test matrix concluded 'skipped' — at least one shard did not succeed"
    r"|##\[error\]red because .+ — see (?:it|them)\."
    r"|##\[error\]" + re.escape(STAGE0_HOLD_LINE)
)

# 🚨 BLOCK signatures span TWO consecutive log lines, so they are matched on the log with newlines
# collapsed to spaces (each raw line carries its ISO timestamp, hence the `[0-9TZ:.-]*` between the
# two halves).
#   api.github.com/app/installations/<id>/access_tokens + status: 5xx — 2026-09-13 08:56:18–08:56:40Z,
#     run 34748690865 (push, ff277fb67) job 103701175062 "AGENTS.md shared rule blocks":
#     actions/create-github-app-token got `status: 500` from GitHub's own API on all 4 of its
#     attempts; the same gate was green on 8 other runs between 08:49Z and 08:57Z, and the two manual
#     `rerun-failed-jobs` POSTs at 09:01Z and 09:03Z ALSO answered 500 — GitHub was degraded, nothing
#     in this repo was. The regex asserts GitHub's OWN identity (the exact installations/access_tokens
#     URL in the mint's RESPONSE block, immediately followed by its status) and accepts ONLY a 5xx
#     there: a 401/403/404/422 on that same URL is a PERMISSION or installation problem — the App
#     lacks a grant, or the installation id is wrong — and MUST NOT retry.
BLOCK_SIGNATURES = re.compile(
    r"url: 'https://api\.github\.com/app/installations/[0-9]+/access_tokens', [0-9TZ:.-]*\s*status: 5[0-9][0-9],"
)


class LogUnreadable(Exception):
    """The steward cannot SEE its input. Never a decline — always a red."""


# ───────────────────────────────── GitHub REST (no `gh`, see above) ─────────────────────────────────

def github_fetch_with(token: str):
    def fetch(path: str, method: str = "GET"):
        # 🚨 THE TOKEN STAYS ON GITHUB'S ORIGIN. A job-logs path answers a 302 to SIGNED storage, and
        # stock urllib forwards ordinary headers across a redirect — which would hand this token to
        # a host that is not GitHub and does not need it. ghrest strips it on any redirect that
        # leaves the origin; the signed URL carries its own authorisation.
        with ghrest.request(path, token, method=method, data=b"" if method == "POST" else None,
                            api=GITHUB_API, timeout=60,
                            user_agent="meshweaver-retry-known-transients") as response:
            if path.endswith("/logs"):
                raw = response.read(MAX_LOG_BYTES + 1)
                if len(raw) > MAX_LOG_BYTES:
                    raise LogUnreadable(f"job log exceeds {MAX_LOG_BYTES} bytes — refusing a truncated parse")
                try:
                    return raw.decode("utf-8", "strict")
                except UnicodeDecodeError as error:
                    raise LogUnreadable("job log is not UTF-8 text") from error
            body = response.read()
            if not body:
                return {}
            try:
                return json.loads(body)
            except (json.JSONDecodeError, UnicodeDecodeError) as error:
                # A 200 carrying unparseable bytes is an input this steward cannot read. Without
                # this it escaped as a bare traceback — loud, but unnamed, and contradicting the
                # contract the module docstring states.
                raise LogUnreadable(f"{path} answered unparseable JSON: {error}") from error

    return fetch


# ────────────────────────────────────────── Decisions ──────────────────────────────────────────

class Decision:
    def __init__(self, retry: bool, reason: str, scope: str = "failed-jobs",
                 logless: list[str] | None = None) -> None:
        self.retry = retry
        self.reason = reason
        # `failed-jobs` → POST rerun-failed-jobs; `all` → POST rerun (the whole run).
        self.scope = scope
        # Jobs judged WITHOUT a log (never written, or expired) — each raised as a `::warning::` so
        # the routine decline is visible on the run rather than buried in a green job's log.
        self.logless = logless or []


# What each proof KIND means for the EVERY-job rule.
TRANSIENT, CONSEQUENTIAL_KIND, UNPROVEN = "TRANSIENT", "CONSEQUENTIAL", "UNPROVEN"
RERUN_FAILED, RERUN_ALL = "failed-jobs", "all"

# A step that did not end in success/skipped is the job's OWN failure.
FAILED_STEP_CONCLUSIONS = frozenset({"failure", "timed_out", "cancelled", "action_required"})


class NoLog:
    """GitHub holds no log for this job — a FACT about the job, not a failed read.

    404: nothing was ever uploaded, which is what a runner that died before writing one leaves
    (the reason, if any, is then in the job's check-run ANNOTATION). 410: the log EXPIRED with the
    run's retention — it existed, may have shown a real failure, and proves nothing now.
    """

    def __init__(self, status: int, detail: str) -> None:
        self.status = status
        self.detail = detail


def read_job_log(repo: str, job_id: int, fetch) -> str | NoLog:
    """The job's log; a `NoLog` when GitHub holds none; raise LogUnreadable for anything else."""
    try:
        body = fetch(f"/repos/{repo}/actions/jobs/{job_id}/logs")
    except LogUnreadable:
        raise
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return NoLog(404, "HTTP 404 — GitHub holds no log for this job (nothing was uploaded; "
                              "a runner that died mid-job leaves exactly this)")
        if error.code == 410:
            return NoLog(410, "HTTP 410 — this job's log has EXPIRED with the run's retention")
        raise LogUnreadable(f"HTTP {error.code} {error.reason}") from error
    except urllib.error.URLError as error:
        raise LogUnreadable(f"transport failure: {error.reason}") from error
    if not isinstance(body, str):
        raise LogUnreadable(f"the logs endpoint returned {type(body).__name__}, not text")
    if not body.strip():
        raise LogUnreadable("the logs endpoint returned an EMPTY body")
    if not JOB_LOG_LINE.search(body):
        raise LogUnreadable(
            f"the {len(body)}-byte body carries no timestamped log line — this is not a job log")
    return body


def read_annotations(repo: str, job_id: int, fetch) -> list[dict]:
    """EVERY annotation of the job's check run — the ONLY record of a runner death (Plugins#1849).

    🚨 Needs `checks: read`. A refusal is a RED naming the grant, never a quiet "cannot prove a
    transient": that is the shape that left the satellites' copies blind to every runner death
    (MeshWeaver.Manufacturing#84) — they never asked, so they could never know.
    """
    items: list[dict] = []
    page = 1
    while True:
        try:
            payload = fetch(f"/repos/{repo}/check-runs/{job_id}/annotations?per_page=100&page={page}")
        except LogUnreadable:
            raise
        except urllib.error.HTTPError as error:
            raise LogUnreadable(
                f"reading the annotations of job {job_id} answered HTTP {error.code} {error.reason} — "
                "this steward NEEDS them to tell a runner death from a real red; the calling job "
                "must grant `checks: read`") from error
        except urllib.error.URLError as error:
            raise LogUnreadable(
                f"reading the annotations of job {job_id} failed in transport: {error.reason}") from error
        # 🚨 No `{}` → `[]` shortcut. GitHub answers "no annotations" with the two bytes `[]`; an
        # EMPTY body (which the adapter decodes to `{}`) is a response this steward cannot read, and
        # treating it as "no annotations" would turn a blind read into a confident decline.
        if not isinstance(payload, list):
            raise LogUnreadable(
                f"the annotations of job {job_id} came back as {type(payload).__name__}, not a list")
        items.extend(a for a in payload if isinstance(a, dict))
        if len(payload) < 100:
            return items
        page += 1


# 🚨 THE ANNOTATION SIGNATURES — GitHub's OWN sentences, anchored, failure-level only. Curated like
# the log signatures; each carries the incident that measured it.
#   runner death — MeshWeaver.Plugins#1849, run 34824858608: two legs died in the SAME second on two
#     self-hosted runners, the test step frozen `in_progress`, no step marked failure, the log
#     `BlobNotFound`, and this single annotation. MeshWeaver.Manufacturing#84 is the same shape on a
#     satellite whose inline copy never read annotations, so it declined and main stayed red.
#   runner shutdown — the sentence a runner leaves when it is TOLD to stop before it vanishes (a node
#     drained with notice). Admitted on what it MEANS — the runner process was stopped from outside
#     the job — and only through the same two doors as a death (Memex#695; Plugins lists it too).
#   budget refusal — 2026-09-11, MeshWeaver.Plugins: an org Actions budget refused 17 jobs in an
#     18-minute window (~2 s, `steps: []`, no log). The steward's own job having started is what says
#     the window has at least partly closed; if it has not, the one retry fails too and stays red.
RUNNER_DEATH_ANNOTATION = r"^The self-hosted runner lost communication with the server\."
RUNNER_SHUTDOWN_ANNOTATION = r"^The runner has received a shutdown signal\."
RUNNER_GONE_ANNOTATIONS = (RUNNER_DEATH_ANNOTATION, RUNNER_SHUTDOWN_ANNOTATION)

# 🚨 A LOST COMPLETION — GitHub's time-limit sentence over a job whose WORK had already finished.
# Measured 2026-10-30 17:44–17:56Z on hosted `ubuntu-latest` runners, four jobs over two runs:
#   MeshWeaver#7694 run 41100409024 job 124219237937 "i18n catalog (mirror sync handed over)": every
#     step success INCLUDING "Complete job" at 17:47:03 (the log ends at "Cleaning up orphan
#     processes"), then "The job has exceeded the maximum execution time of 5m0s" and conclusion
#     `cancelled` at 17:51:50. Job 124218588224 "CI's own shell": all 133 steps success by 17:53:45,
#     cut at its 10m0s cap at 17:55:54.
#   MeshWeaver#7697 run 41100231113 job 124218462994 "Satellite package pins": every work step success
#     by 17:44:27, the runner-owned "Post Run actions/checkout" left `in_progress`, cut at 5m0s.
# The runner never delivered the completion it had written, so GitHub cut a FINISHED job at its cap.
# The run then concludes `cancelled`, the stage gate reads `cancelled` as stage-0 red, and nothing in
# the lane ever looked (it judged `failure` runs only) — the pull request stayed red until a person
# re-ran it.
# 🚨 THE STEPS ARE THE PROOF, not the sentence. The same sentence is what a REAL hang at the cap
# leaves, so it is admitted only when every work step (everything but the runner's own `Post …` and
# `Complete job` bookkeeping) COMPLETED with success/skipped — a step cut mid-work fails the test and
# the job stays UNPROVEN. It is matched separately from ANNOTATION_SIGNATURES for that reason.
JOB_TIME_LIMIT_ANNOTATION = r"^The job has exceeded the maximum execution time of [0-9]+m[0-9]+s$"
RUNNER_BOOKKEEPING_STEP = re.compile(r"^(?:Post |Complete job$)")
# A job cancelled because a NEWER run took its concurrency group (a push superseded it), or by a
# person, is never a transient: the newer run is the one that counts, or somebody meant it.
SUPERSEDED_ANNOTATION = r"^Canceling since a higher priority waiting request for .+ exists"
ANNOTATION_SIGNATURES: tuple[tuple[str, str], ...] = (
    (RUNNER_DEATH_ANNOTATION, "runner death — the self-hosted runner lost communication with the server"),
    (RUNNER_SHUTDOWN_ANNOTATION, "runner shutdown — the runner received a shutdown signal mid-job"),
    (r"^The job was not started because an Actions budget is preventing further use\.",
     "refused job — an org Actions budget prevented the start"),
)

# 🚨 VERDICT STEPS — an aggregator's own assertion over OTHER jobs' outcomes. A job whose ONLY failed
# step is one of these failed BECAUSE something else did, so it never vetoes a retry — and never
# causes one alone. Each entry names the core reusable lane that DEFINES the step, and the self-test
# asserts the step really is there (a renamed step must red here, not silently decline every death
# behind its aggregator). Measured on MeshWeaver.Plugins PR #2014, run 35222879197: a runner died in a
# module-test leg, and the aggregator's `Exactly the selected bundles were built` vetoed the retry.
#
# 🚨 THE STEP NAME IS NOT THE PROOF. The same step also fails on its OWN findings — a FULL-scope
# selection that disagrees with the caller's matrix, a failed selector, an empty lane key — none of
# which a retry can fix. So an entry carries (the defining lane, the log line that says an UPSTREAM
# job did not succeed, the sentences of the step's independent findings). The verdict job is excused
# only when its log shows the upstream line AND none of the independent findings; anything else is
# UNPROVEN and vetoes the retry. The self-test asserts every string still occurs in the lane.
VERDICT_STEPS: dict[str, tuple[str, str, tuple[str, ...]]] = {
    "Exactly the selected bundles were built": (
        "node-repo-module-pack.yml",
        # The step's first line: `echo "scope=$SCOPE, …; pack job: $PACK_RESULT, tests lane: $TESTS_RESULT"`.
        r"; pack job: (?:failure|cancelled), tests lane: |; pack job: [a-z]+, tests lane: (?:failure|cancelled)\b",
        # Fragments that sit on ONE source line: the step wraps its messages with `\`+newline,
        # which leaves the next line's indentation inside the printed sentence.
        ("selection and the caller's matrix disagree",
         "the selection job reported",
         "the selection produced no lane key"),
    ),
}
# The literal fragments of each evidence line, asserted present in the defining lane.
VERDICT_EVIDENCE_IN_LANE: dict[str, str] = {
    "Exactly the selected bundles were built": "pack job: $PACK_RESULT, tests lane: $TESTS_RESULT",
}


# The runner ECHOES a step's whole `run:` script before executing it, each source line wrapped in
# the cyan SGR `\x1b[36;1m…\x1b[0m`. That preamble carries every `echo "::error …"` the step COULD
# emit, whichever branch runs — so it is source, never evidence (review on #6898).
ECHOED_SOURCE = "\x1b[36;1m"


def emitted_lines(log: str) -> list[str]:
    """The log WITHOUT the echoed `run:` source: only what the step actually printed."""
    return [line for line in log.splitlines() if ECHOED_SOURCE not in line]


def verdict_excused(own: list[str], log: str) -> str | None:
    """Why a verdict-only job is a CONSEQUENCE of upstream failures, or None when it is not proven.

    Both halves read EMITTED records only. The upstream line is the step's own output (its echo
    prints the resolved results; the echoed source shows `$PACK_RESULT` unexpanded), and a finding
    counts only as an error record the step RAISED — a `##[error]` line, which is what `::error`
    becomes once executed. The same sentence in the echoed preamble is a branch that may never run.
    """
    if not own or not set(own) <= VERDICT_STEPS.keys():
        return None
    output = emitted_lines(log)
    errors = [line for line in output if "##[error]" in line]
    for step in own:
        _, upstream, independent = VERDICT_STEPS[step]
        if not any(re.search(upstream, line) for line in output):
            return None
        if any(sentence in line for line in errors for sentence in independent):
            return None
    return (f"verdict step {own[0]!r} failed, and its log names an upstream job that did not succeed "
            "and none of the step's own findings — a consequence of other failed jobs, never a cause")


def failed_steps(job: dict) -> list[str]:
    return [str(s.get("name", "?")) for s in job.get("steps") or []
            if s.get("conclusion") in FAILED_STEP_CONCLUSIONS]


def match_annotations(annotations: list[dict]) -> str | None:
    for annotation in annotations:
        if annotation.get("annotation_level") != "failure":
            continue
        message = str(annotation.get("message") or "")
        for pattern, meaning in ANNOTATION_SIGNATURES:
            if re.search(pattern, message):
                return f"annotation `{pattern}` — {meaning}"
    return None


def _failure_messages(annotations: list[dict]) -> list[str]:
    return [str(a.get("message") or "") for a in annotations if a.get("annotation_level") == "failure"]


def lost_completion(job: dict, annotations: list[dict]) -> str | None:
    """Why a job GitHub cut at its time limit had already FINISHED its work, or None.

    All three must hold: the time-limit sentence is a failure annotation of the job; no annotation
    says a newer run superseded it; and every work step COMPLETED with success/skipped (only the
    runner's own `Post …` / `Complete job` bookkeeping may be unfinished). A step cut mid-work is a
    real hang at the cap, and the job stays UNPROVEN.
    """
    messages = _failure_messages(annotations)
    limit = next((m for m in messages if re.search(JOB_TIME_LIMIT_ANNOTATION, m)), None)
    if limit is None or any(re.search(SUPERSEDED_ANNOTATION, m) for m in messages):
        return None
    work = [s for s in job.get("steps") or []
            if not RUNNER_BOOKKEEPING_STEP.match(str(s.get("name", "")))]
    if not work or any(s.get("status") != "completed" or s.get("conclusion") not in ("success", "skipped")
                       for s in work):
        return None
    return (f"lost completion — every one of its {len(work)} work step(s) completed green, then "
            f"GitHub cut the job: {limit!r} (the runner never delivered the completion it wrote)")


def runner_gone(annotations: list[dict]) -> bool:
    return any(a.get("annotation_level") == "failure"
               and any(re.search(p, str(a.get("message") or "")) for p in RUNNER_GONE_ANNOTATIONS)
               for a in annotations)


CONSEQUENTIAL = "a CONSEQUENTIAL failure (an upstream job never ran — not a transient by itself)"


def classify(log: str) -> str | None:
    """Name the matched LOG signature, or None when nothing named matches."""
    if SIGNATURES.search(log):
        return "a named transient signature"
    if BLOCK_SIGNATURES.search(log.replace("\n", " ")):
        return "a named transient BLOCK signature (App-token mint answered 5xx by api.github.com)"
    if CONSEQUENTIAL_SIGNATURES.search(log):
        return CONSEQUENTIAL
    return None


def judge(repo: str, job: dict, fetch) -> tuple[str, str, bool]:
    """One failed job → (TRANSIENT | CONSEQUENTIAL | UNPROVEN, the printed reason, judged-without-log).

    Three shapes of proof, and their ORDER is the safety property:
      1. NO step of the job's own failed — the failure came from OUTSIDE the steps (a runner that
         vanished, a job GitHub refused to start). The log is not where the reason is; the check-run
         ANNOTATION is. A named annotation proves it; anything else declines.
      2. A step failed and the log is READABLE — the log alone decides; an annotation never
         overrides a step's own failure that the log can speak to.
      3. A step failed and the log was NEVER WRITTEN (404) — GitHub deletes the log of a job whose
         runner died mid-step (Plugins#2773). Only a "the runner went away" annotation excuses that;
         an EXPIRED log (410) never does, because it existed and may have shown a real red.

    🚨 A job that did NOT conclude `failure` (cancelled, timed out) is judged BEFORE all three, on its
    annotations alone (review on #7704). A superseded job is vetoed outright: re-running it revives an
    old head, which takes the pull request's concurrency group from the current head (#7696). Every
    other cancelled job is proven only by a named annotation or a lost completion — never by a log
    signature, because a cancelled step's log says what the step was doing when it was STOPPED, not
    why it was stopped.
    """
    job_id = job["id"]
    if job.get("conclusion") != "failure":
        annotations = read_annotations(repo, job_id, fetch)
        if any(re.search(SUPERSEDED_ANNOTATION, m) for m in _failure_messages(annotations)):
            return UNPROVEN, (f"cancelled because a NEWER run of its concurrency group superseded it — "
                              "the newer run is the one that counts; re-running this one would take "
                              "the group from it, so no retry"), True
        matched = match_annotations(annotations) or lost_completion(job, annotations)
        if matched:
            return TRANSIENT, f"{matched} (concluded {job.get('conclusion')})", True
        seen = _failure_messages(annotations)[:3] or ["no failure-level annotation"]
        return UNPROVEN, (f"concluded {job.get('conclusion')} with no named proof (annotations: "
                          f"{[m[:120] for m in seen]}) — a cancel nobody can explain is not a "
                          "transient, no retry. Investigate it."), True
    own = failed_steps(job)
    if not own:
        annotations = read_annotations(repo, job_id, fetch)
        matched = match_annotations(annotations) or lost_completion(job, annotations)
        where = "no step ran" if not job.get("steps") else "no step of its own failed"
        # Judged WITHOUT a log by construction (the log was never read), so both outcomes are
        # listed for the `Judged without a log` warning and the summary carries their evidence.
        if matched:
            return TRANSIENT, f"{matched} ({where})", True
        seen = [str(a.get("message", ""))[:120] for a in annotations
                if a.get("annotation_level") == "failure"] or ["no failure-level annotation"]
        return UNPROVEN, (f"failed with {where}, and no annotation matches a named signature: {seen} — "
                          "cannot prove a transient, no retry"), True
    log = read_job_log(repo, job_id, fetch)  # raises LogUnreadable → red, never a decline
    if isinstance(log, str):
        matched = classify(log)
        if matched == CONSEQUENTIAL:
            return CONSEQUENTIAL_KIND, matched, False
        if matched is not None:
            return TRANSIENT, f"{matched} (failed step {own[0]!r})", False
        excused = verdict_excused(own, log)
        if excused:
            return CONSEQUENTIAL_KIND, excused, False
        if set(own) <= VERDICT_STEPS.keys():
            return UNPROVEN, (f"verdict step {own[0]!r} failed WITHOUT evidence that an upstream job "
                              "caused it (or with one of the step's own findings) — this may be a "
                              "real red, so no retry. Investigate it."), False
        return UNPROVEN, (f"NO named signature matched (failed step {own[0]!r}) — this is (or may "
                          "be) a real red, so no retry. Investigate it."), False
    if log.status == 404:
        annotations = read_annotations(repo, job_id, fetch)
        if runner_gone(annotations):
            return TRANSIENT, (f"runner gone mid-step — step {own[0]!r} failed, {log.detail}, and "
                               f"{match_annotations(annotations)}; retried ONCE"), True
    return UNPROVEN, (f"step {own[0]!r} failed and {log.detail} — cannot prove a transient, "
                      "no retry"), True


# The job conclusions that are a red to judge. `cancelled` is one: a job GitHub cut at its time limit
# concludes `cancelled`, never `failure` (MeshWeaver#7694 — see JOB_TIME_LIMIT_ANNOTATION). Each
# cancelled job is judged like any other red: proven, or it vetoes — a superseded or hand-cancelled
# job carries no named proof, so a run cancelled that way is never retried.
RED_JOB_CONCLUSIONS = frozenset({"failure", "cancelled", "timed_out"})
# The run conclusions this steward judges — the lane's `if:` admits the same two.
JUDGED_RUN_CONCLUSIONS = frozenset({"failure", "cancelled"})


def failed_jobs(repo: str, run_id: str, attempt: int | None, fetch) -> list[dict]:
    """EVERY red job (RED_JOB_CONCLUSIONS) of the run (of the judged ATTEMPT when one is given) — paginated.

    🚨 The population must be COMPLETE or the EVERY-job rule is evaluated over a truncated one, and
    a run whose real failure sits on page two would be retried as if it were all transients.
    🚨 The attempt is the EVENT's, pinned: a hand rerun that starts before this steward reads the
    jobs would otherwise be judged (the latest attempt), and the one-retry guarantee with it.
    """
    base = (f"/repos/{repo}/actions/runs/{run_id}/attempts/{attempt}/jobs" if attempt
            else f"/repos/{repo}/actions/runs/{run_id}/jobs")
    jobs_out: list[dict] = []
    seen = 0
    page = 1
    while True:
        # The job LIST is an input too. An HTTP/transport failure here must arrive as the same
        # named red as an unreadable log — never as a traceback, and never as an empty list, which
        # `decide` would report as the documented "no failed jobs listed" no-op.
        try:
            payload = fetch(f"{base}?per_page=100&page={page}")
        except urllib.error.HTTPError as error:
            raise LogUnreadable(
                f"listing the jobs of run {run_id} answered HTTP {error.code} {error.reason}") from error
        except urllib.error.URLError as error:
            raise LogUnreadable(f"listing the jobs of run {run_id} failed: {error.reason}") from error
        # 🚨 VALIDATE THE ENVELOPE. A malformed or short page must not read as "the end of the
        # list": that would hand `decide` a TRUNCATED population (#4534 again, one exit code apart).
        if not isinstance(payload, dict) or not isinstance(payload.get("jobs"), list):
            raise LogUnreadable(
                f"listing the jobs of run {run_id} returned an envelope with no jobs list")
        total = payload.get("total_count")
        if not isinstance(total, int) or total < 0:
            raise LogUnreadable(
                f"listing the jobs of run {run_id} returned no usable total_count")
        jobs = payload["jobs"]
        if not jobs:
            if seen < total:
                raise LogUnreadable(
                    f"listing the jobs of run {run_id} stopped at {seen} of {total} job records — "
                    "an incomplete job set cannot support the every-job rule")
            break
        seen += len(jobs)
        jobs_out.extend(job for job in jobs
                        if isinstance(job, dict) and job.get("conclusion") in RED_JOB_CONCLUSIONS)
        if seen >= total:
            break
        page += 1
    return jobs_out


def decide(repo: str, run_id: str, fetch, attempt: int | None = None,
           scope: str = RERUN_FAILED) -> Decision:
    failed = failed_jobs(repo, run_id, attempt, fetch)
    if not failed:
        return Decision(False, f"run {run_id}: a red conclusion but no failed or cancelled jobs listed "
                               "(a run evicted from its concurrency group before any job started) — "
                               "doing nothing.")
    transients = 0
    consequential = 0
    logless: list[str] = []
    for job in failed:
        label = f"job {job['id']}" + (f" ({job['name']})" if job.get("name") else "")
        kind, reason, without_log = judge(repo, job, fetch)
        if without_log:
            logless.append(f"{label}: {reason}")
        if kind == UNPROVEN:
            return Decision(False, f"{label}: {reason}", logless=logless)
        if kind == TRANSIENT:
            transients += 1
        else:
            consequential += 1
        print(f"{label}: {kind} — {reason}.")
    if transients == 0:
        # Every failure was the echo of an upstream that never ran, and nothing NAMED the cause.
        # That is not proof of a transient — it is a run whose real failure is not a failed job
        # (cancelled, or never started), so it stays red for a person to read.
        return Decision(False, (
            f"run {run_id}: all {len(failed)} failed job(s) are CONSEQUENTIAL and none names a "
            "transient cause — no retry. Investigate the job that did not run."), logless=logless)
    # 🚨 A CONSEQUENTIAL job means something was SKIPPED behind the transient, and a skipped job does
    # NOT come back from `rerun-failed-jobs` — measured on MeshWeaver.Plugins run 35371177496 attempt 2:
    # the needs that caused the skip went green and the dependent stayed skipped. So the retry would
    # only re-fail the aggregator. Re-run the whole run instead.
    if consequential:
        scope = RERUN_ALL
    # A CANCELLED job is re-run by the whole-run POST, which needs no reading of whether GitHub's
    # "failed jobs" includes the cancelled ones; one retry, ever, so the extra jobs are bounded.
    if any(job.get("conclusion") != "failure" for job in failed):
        scope = RERUN_ALL
    return Decision(True, (f"all {len(failed)} failed job(s) are proven ({transients} transient, "
                           f"{consequential} consequential) — one retry ({scope})."),
                    scope=scope, logless=logless)


def summarize(lines: list[str]) -> None:
    """Every decision into the job SUMMARY too: on this lane a decline is the ROUTINE outcome, and a
    green job's log is a room nobody enters."""
    import os
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    try:
        with open(path, "a", encoding="utf-8") as handle:
            handle.write("### Retry known transients\n\n" + "\n".join(f"- {line}" for line in lines) + "\n")
    except OSError as error:
        print(f"::warning::could not write the job summary: {error}")


def run(repo: str, run_id: str, fetch, dry_run: bool, attempt: int | None = None,
        scope: str = RERUN_FAILED) -> int:
    if attempt is not None and attempt != 1:
        # ONE retry, ever. Said out loud rather than skipped: a skipped job reads like a pass.
        line = (f"run {run_id} attempt {attempt}: the one automatic retry is spent (or this is a hand "
                "re-run) — this red stands. Investigate it.")
        print(line)
        summarize([line])
        return 0
    try:
        decision = decide(repo, run_id, fetch, attempt, scope)
    except LogUnreadable as error:
        # 🚨 #4534. The whole reason this is not `exit 0`. A steward that cannot read its input
        # cannot prove ANYTHING — and for a week it said "no retry" in a sentence that read like a
        # verdict.
        print(f"::error title=Retry steward is blind::could not read an input of run {run_id} "
              f"in {repo}: {error}. This steward cannot prove a transient when it cannot see its "
              f"input, so it fails instead of declining. Investigate the read, then the run.")
        return 1
    for line in decision.logless:
        print(f"::warning title=Judged without a log::{line}")
    print(decision.reason)
    summarize(decision.logless + [decision.reason])
    if not decision.retry:
        return 0
    endpoint = "rerun" if decision.scope == RERUN_ALL else "rerun-failed-jobs"
    # 🚨 THE DECISION IS ABOUT ONE ATTEMPT; THE POST IS ABOUT THE RUN AS IT IS NOW. A hand re-run can
    # start (and even fail for a real defect) while this steward is queued, and the event this job
    # carries stays stale forever — re-running the steward replays it. So the run is read again
    # immediately before the POST, and the retry happens only while the run is still the completed
    # failure of the attempt that was judged. An unreadable answer is a RED, never a guess.
    judged = attempt if attempt is not None else 1
    try:
        current = fetch(f"/repos/{repo}/actions/runs/{run_id}")
    except (urllib.error.HTTPError, urllib.error.URLError) as error:
        reason = (f"HTTP {error.code} {error.reason}" if isinstance(error, urllib.error.HTTPError)
                  else error.reason)
        print(f"::error title=Retry steward is blind::run {run_id} in {repo} is proven transient, but "
              f"re-reading the run before the POST failed: {reason}. Not retrying on a stale view.")
        return 1
    except LogUnreadable as error:
        print(f"::error title=Retry steward is blind::run {run_id} in {repo}: re-reading the run "
              f"before the POST failed: {error}. Not retrying on a stale view.")
        return 1
    if not isinstance(current, dict) or not isinstance(current.get("run_attempt"), int):
        print(f"::error title=Retry steward is blind::run {run_id} in {repo}: the run metadata read "
              "before the POST carries no run_attempt. Not retrying on a stale view.")
        return 1
    if (current["run_attempt"] != judged or current.get("status") != "completed"
            or current.get("conclusion") not in JUDGED_RUN_CONCLUSIONS):
        line = (f"run {run_id} moved on after attempt {judged} was judged (now attempt "
                f"{current['run_attempt']}, {current.get('status')}/{current.get('conclusion')}) — "
                "somebody re-ran it, so this steward does NOT retry over them.")
        print(line)
        summarize([line])
        return 0
    if dry_run:
        print(f"--dry-run: NOT posting {endpoint}.")
        return 0
    try:
        fetch(f"/repos/{repo}/actions/runs/{run_id}/{endpoint}", method="POST")
    except (urllib.error.HTTPError, urllib.error.URLError, LogUnreadable) as error:
        # The decision was sound and the retry did not happen. Say both, by name — a run left
        # un-retried because the POST was refused must not look like a run we chose not to retry.
        reason = (f"HTTP {error.code} {error.reason}" if isinstance(error, urllib.error.HTTPError)
                  else getattr(error, "reason", error))
        print(f"::error title=Retry steward could not rerun::run {run_id} in {repo} is proven "
              f"transient, but POST {endpoint} was refused: {reason}.")
        return 1
    print(f"posted {endpoint} for run {run_id}.")
    return 0


# ──────────────────────────────────────────── Self-test ────────────────────────────────────────────

REAL_LOG_HEAD = (
    "﻿2026-09-17T04:35:31.5696641Z Current runner version: '2.337.0'\n"
    "2026-09-17T04:35:31.6698589Z \x1b[36;1mset -euo pipefail\x1b[0m\n"
)


RUN_STILL_RED = {"run_attempt": 1, "status": "completed", "conclusion": "failure"}
ONE_FAILED_STEP = [{"name": "Run", "status": "completed", "conclusion": "failure"}]


def _steps(*triples: tuple[str, str, str | None]) -> list[dict]:
    return [{"name": n, "status": s, "conclusion": c} for n, s, c in triples]


def _fake(logs: dict[int, object], total: int | None = None,
          pages: list[list[int]] | None = None,
          steps: dict[int, list[dict]] | None = None,
          annotations: dict[int, object] | None = None,
          run_state: object = None,
          conclusions: dict[int, str] | None = None):
    """A fetch double. `logs` maps job id → log text, or an Exception to raise.

    `pages` (a list of job-id lists) serves a DIFFERENT set of job records per page, so a test can
    prove that page two's records are USED and not merely requested. `steps` overrides a job's
    `steps[]` (default: one failed step — the job's OWN failure). `annotations` maps job id → the
    check-run annotation list, or an Exception to raise; an absent entry is an EMPTY list.
    `run_state` is what GET runs/<id> answers right before the POST (default: attempt 1, still a
    completed failure), or an Exception to raise. `conclusions` overrides a job's conclusion
    (default `failure`).
    """
    calls: list[str] = []
    steps = steps or {}
    annotations = annotations or {}
    conclusions = conclusions or {}

    def record(job_id: int) -> dict:
        return {"id": job_id, "name": f"job-{job_id}", "conclusion": conclusions.get(job_id, "failure"),
                "steps": steps.get(job_id, ONE_FAILED_STEP)}

    def fetch(path: str, method: str = "GET"):
        calls.append(f"{method} {path}")
        if method == "POST":
            return {}
        if re.search(r"/actions/runs/[0-9]+$", path):
            state = run_state if run_state is not None else dict(RUN_STILL_RED)
            if isinstance(state, Exception):
                raise state
            return state
        if "/jobs?" in path:
            if pages is not None:
                index = int(path.split("&page=")[1]) - 1
                served = pages[index] if 0 <= index < len(pages) else []
                return {"total_count": sum(len(p) for p in pages),
                        "jobs": [record(job_id) for job_id in served]}
            return {"total_count": total if total is not None else len(logs),
                    "jobs": [record(job_id) for job_id in logs]}
        if "/check-runs/" in path:
            job_id = int(path.split("/check-runs/")[1].split("/")[0])
            value = annotations.get(job_id, [])
            if isinstance(value, Exception):
                raise value
            return value
        job_id = int(path.split("/jobs/")[1].split("/")[0])
        value = logs[job_id]
        if isinstance(value, Exception):
            raise value
        return value

    fetch.calls = calls  # type: ignore[attr-defined]
    return fetch


def _posted(fetch) -> list[str]:
    return [call for call in fetch.calls if call.startswith("POST ")]  # type: ignore[attr-defined]


def _http(code: int) -> urllib.error.HTTPError:
    return urllib.error.HTTPError("u", code, {404: "Not Found", 410: "Gone", 403: "Forbidden",
                                              500: "Server Error"}.get(code, "x"), {}, None)  # type: ignore[arg-type]


# Measured (MeshWeaver.Plugins#1849, run 34824858608): GitHub's verbatim runner-death annotation.
LOST_COMMUNICATION = [{
    "path": ".github", "start_line": 1, "end_line": 1, "annotation_level": "failure", "title": "",
    "message": "The self-hosted runner lost communication with the server. Verify the machine is "
               "running and has a healthy network connection. Anything in your workflow that "
               "terminates the runner process, starves it for CPU/Memory, or blocks its network "
               "access can cause this error.", "raw_details": ""}]
# 🚨 SYNTHETIC: the runner's own sentence, not yet measured in this fleet (see the signature list).
SHUTDOWN_SIGNAL = [{"annotation_level": "failure",
                    "message": "The runner has received a shutdown signal. This can happen when the "
                               "runner service is stopped, or a manually started runner is canceled."}]
BUDGET_ANNOTATION = [{"annotation_level": "failure",
                      "message": "The job was not started because an Actions budget is preventing further use."}]
# The frozen leg of #1849: the test step left `in_progress`, nothing marked failure.
FROZEN_LEG = _steps(("Set up job", "completed", "success"),
                    ("Test this leg's suites", "in_progress", None),
                    ("Complete job", "pending", None))
# Plugins#2773: the runner died WHILE a step ran, so the step is marked failure and the log is gone.
DIED_IN_STEP = _steps(("Set up job", "completed", "success"),
                      ("Upload the built markers", "completed", "failure"))
VERDICT_ONLY = _steps(("Set up job", "completed", "success"),
                      ("Exactly the selected bundles were built", "completed", "failure"))
# ── MeshWeaver#7694 / #7697 (2026-10-30), measured verbatim: a FINISHED job cut at its time limit. ──
TIME_LIMIT_5M = [{"annotation_level": "failure", "title": "",
                  "message": "The job has exceeded the maximum execution time of 5m0s"},
                 {"annotation_level": "notice",
                  "message": "\"The ubuntu-latest label will migrate to Ubuntu 26 beginning October 19, 2026.\""}]
# Job 124219237937: every step green INCLUDING "Complete job", then cut.
FINISHED_THEN_CUT = _steps(("Set up job", "completed", "success"),
                           ("Run actions/checkout@v7", "completed", "success"),
                           ("Every catalog change hands the mirror sync over", "completed", "success"),
                           ("Post Run actions/checkout@v7", "completed", "success"),
                           ("Complete job", "completed", "success"))
# Job 124218462994: the work green, the runner's own post step left in progress, then cut.
POST_STEP_STUCK = _steps(("Set up job", "completed", "success"),
                         ("Run actions/checkout@v7", "completed", "success"),
                         ("Satellite pins are declared", "completed", "success"),
                         ("Post Run actions/checkout@v7", "in_progress", None))
# The REAL hang the same sentence also describes: a work step still running when the cap fired.
WORK_STEP_HUNG = _steps(("Set up job", "completed", "success"),
                        ("Run the suite", "in_progress", None),
                        ("Complete job", "pending", None))
# MeshWeaver#7696 run 41103074897: a job a NEWER run superseded — never a transient.
SUPERSEDED = [{"annotation_level": "failure",
               "message": "Canceling since a higher priority waiting request for build-test-refs/pull/7696/merge exists"},
              {"annotation_level": "failure", "message": "The operation was canceled."}]
# The stage gate's and Consolidate's own lines over the two cut controls (run 41100409024).
STAGE0_HELD_LOG = REAL_LOG_HEAD + (
    "2026-10-30T17:56:01.8136634Z ##[error]STAGE 2 HELD (stage 0 red): a cheap static control is red "
    "(i18n-mirror-sync=cancelled workflow-shell=cancelled) — fix it and push; the new head restarts at stage 0\n"
    "2026-10-30T17:56:01.8144950Z ##[error]Process completed with exit code 1.\n")
GATE_STEP = _steps(("Set up job", "completed", "success"),
                   ("Stage 1 — is this head reviewed and every finding answered?", "completed", "failure"))
RUN_CANCELLED = {"run_attempt": 1, "status": "completed", "conclusion": "cancelled"}


def self_test() -> int:
    """Every case below drives `run()`, which appends to `$GITHUB_STEP_SUMMARY` — and the lane runs
    this inside the REAL job, so its synthetic retries and declines would land in the live summary
    (review on #6898). The variable is unset for the duration and restored afterwards."""
    import os
    saved = os.environ.pop("GITHUB_STEP_SUMMARY", None)
    try:
        return _self_test_cases(saved)
    finally:
        if saved is not None:
            os.environ["GITHUB_STEP_SUMMARY"] = saved


def _self_test_cases(live_summary: str | None) -> int:
    import os
    import tempfile
    failures: list[str] = []

    def check(name: str, condition: bool) -> None:
        print(f"  {'ok  ' if condition else 'FAIL'}  {name}")
        if not condition:
            failures.append(name)

    print("retry-known-transients self-test")
    check("the self-test runs with NO job summary to write into",
          os.environ.get("GITHUB_STEP_SUMMARY") is None)
    # Positive control: `summarize` really writes when a summary is set, so the guard above is what
    # keeps the cases below out of the live one — not a summarize that never writes.
    with tempfile.TemporaryDirectory() as scratch:
        probe = os.path.join(scratch, "summary.md")
        os.environ["GITHUB_STEP_SUMMARY"] = probe
        try:
            summarize(["probe line"])
        finally:
            del os.environ["GITHUB_STEP_SUMMARY"]
        written = open(probe, encoding="utf-8").read() if os.path.exists(probe) else ""
        check("...and summarize() writes when one IS set (positive control)", "probe line" in written)
    if live_summary is not None:
        before = os.path.getsize(live_summary) if os.path.exists(live_summary) else -1

    # ── The #4534 regression: a REAL log, escape sequences and all, must be read and classified. ──
    escaped = REAL_LOG_HEAD + (
        "2026-09-17T04:35:32.0Z \x1b[36;1mdocker login meshweaver.azurecr.io\x1b[0m\n"
        "2026-09-17T04:35:33.0Z Error response from daemon: Get https://meshweaver.azurecr.io/v2/: "
        "dial tcp 51.12.25.82:443: connect: connection refused\n")
    fetch = _fake({1: escaped})
    decision = decide("o/r", "9", fetch)
    check("a log carrying ESC sequences is READ, not refused (#4534)", decision.retry)
    check("...and the escape-bearing body really did contain ESC", "\x1b[" in escaped)

    # ── Each unreadable SHAPE is pinned to its own diagnosis. ──
    # Not decoration: an operator reading the red needs "the endpoint answered nothing" and "the
    # endpoint answered something that is not a log" to be different sentences. Asserting only the
    # exit code would let either guard be deleted while the other silently covered for it.
    for body, expected in (("", "EMPTY body"), ("   \n  ", "EMPTY body"),
                           ("<html>502 Bad Gateway</html>", "not a job log")):
        try:
            read_job_log("o/r", 1, _fake({1: body}))
            check(f"a body {body!r} is refused as {expected!r}", False)
        except LogUnreadable as error:
            check(f"a body {body!r} is refused as {expected!r}", expected in str(error))

    # ── Positive control: the marker assertion is FALSIFIABLE. ──
    # A body with the transient text but NO timestamped line must be refused as unreadable, not
    # matched — otherwise the marker check would be a step that cannot fail.
    unmarked = "dial tcp 51.12.25.82:443: connect: connection refused\n"
    try:
        read_job_log("o/r", 1, _fake({1: unmarked}))
        check("a marker-less body is refused even when it WOULD match a signature", False)
    except LogUnreadable as error:
        check("a marker-less body is refused even when it WOULD match a signature",
              "not a job log" in str(error))

    # ── Negative control: a genuinely unreadable log FAILS LOUDLY, and posts nothing. ──
    for name, blow_up in (
        ("HTTP 500", urllib.error.HTTPError("u", 500, "Server Error", {}, None)),  # type: ignore[arg-type]
        ("transport", urllib.error.URLError("connection reset")),
        ("empty body", None),
    ):
        fetch = _fake({1: blow_up if blow_up is not None else ""})
        code = run("o/r", "9", fetch, dry_run=False)
        check(f"an unreadable log ({name}) exits 1, LOUDLY", code == 1)
        check(f"an unreadable log ({name}) posts NO rerun",
              not any("rerun-failed-jobs" in call for call in fetch.calls))  # type: ignore[attr-defined]

    # ── The JOB LIST is an input too, and a failure to read it is the same red. ──
    # Found by running this script against a non-existent run id: the HTTPError escaped as a
    # traceback — loud, but not NAMED, and one `except` away from being reported as the documented
    # "no failed jobs listed" no-op, which is the #4534 shape all over again.
    def exploding_list(path: str, method: str = "GET"):
        raise urllib.error.HTTPError(path, 404, "Not Found", {}, None)  # type: ignore[arg-type]

    code = run("o/r", "9", exploding_list, dry_run=False)
    check("an unlistable job set exits 1, LOUDLY (not a traceback, not a no-op)", code == 1)

    # ── The classifier still REFUSES a real failure. Restoring retries must not retry real reds. ──
    real_red = REAL_LOG_HEAD + (
        "2026-09-17T04:36:00.0Z error CS0535: 'FakeEaGraphAuth' does not implement interface member\n"
        "2026-09-17T04:36:01.0Z Build FAILED.\n")
    fetch = _fake({1: real_red})
    decision = decide("o/r", "9", fetch)
    check("a compile error is NOT a transient", not decision.retry)
    check("...and says so in a sentence naming the job", "NO named signature matched" in decision.reason)
    code = run("o/r", "9", fetch, dry_run=False)
    check("a real red declines with exit 0 (a decline is not a failure)", code == 0)
    check("a real red posts NO rerun",
          not any("rerun-failed-jobs" in call for call in fetch.calls))  # type: ignore[attr-defined]

    # ── The EVERY-job rule: one real red among transients blocks the retry. ──
    fetch = _fake({1: escaped, 2: real_red})
    check("one real red among transients blocks the retry", not decide("o/r", "9", fetch).retry)

    # ── The BLOCK signature, and the 5xx-only guard it was written with. ──
    mint_5xx = REAL_LOG_HEAD + (
        "2026-09-13T08:56:18.0Z url: 'https://api.github.com/app/installations/12345/access_tokens',\n"
        "2026-09-13T08:56:18.1Z status: 500,\n")
    check("an App-token mint 5xx matches the BLOCK signature", decide("o/r", "9", _fake({1: mint_5xx})).retry)
    for status in ("403", "404", "401", "422"):
        denied = mint_5xx.replace("status: 500,", f"status: {status},")
        check(f"...but status {status} on that URL does NOT retry (permission, not outage)",
              not decide("o/r", "9", _fake({1: denied})).retry)

    # ── #6364: a NuGet feed transport death kills the build, and Consolidate echoes it. ──
    # Lines copied from run 37968714635 (job 113951063426, and 113951406887 for Consolidate).
    nuget_reset = REAL_LOG_HEAD + (
        "2026-10-09T17:51:44.6942784Z /usr/share/dotnet/sdk/10.0.401/NuGet.targets(198,5): error : "
        "Failed to download package 'Microsoft.Playwright.1.63.0' from 'https://api.nuget.org/v3-"
        "flatcontainer/microsoft.playwright/1.63.0/microsoft.playwright.1.63.0.nupkg'. [/w/MeshWeaver.slnx]\n"
        "2026-10-09T17:51:46.4603587Z /w/src/X/X.csproj : error NU1301: Failed to retrieve "
        "information about 'System.Data.DataSetExtensions' from remote source 'https://api.nuget.org/"
        "v3-flatcontainer/system.data.datasetextensions/index.json'. [/w/MeshWeaver.slnx]\n")
    consolidate = REAL_LOG_HEAD + (
        "2026-10-09T17:52:08.8824988Z ##[error]No test results found in any shard — the suite did not "
        "run, so this run proves nothing.\n"
        "2026-10-09T17:52:08.9238825Z ##[error]The test matrix concluded 'skipped' — at least one "
        "shard did not succeed. See the shard jobs; this run does not cover the whole suite.\n")
    check("a NuGet restore killed by api.nuget.org resets is a transient (#6364)",
          decide("o/r", "9", _fake({1: nuget_reset})).retry)
    check("...and the build + its consequential Consolidate red retry together (#6364)",
          decide("o/r", "9", _fake({1: nuget_reset, 2: consolidate})).retry)
    for name, line in (
        ("NU1301 from another feed (a private-feed credential)",
         "error NU1301: Failed to retrieve information about 'A' from remote source 'https://pkgs.dev.azure.com/x/index.json'"),
        ("a package that does not exist (NU1101)",
         "error NU1101: Unable to find package Nope. No packages exist with this id in source(s): nuget.org"),
    ):
        check(f"...but {name} does NOT retry",
              not decide("o/r", "9", _fake({1: REAL_LOG_HEAD + "2026-10-09T17:51:46.0Z " + line + "\n"})).retry)
    check("a CONSEQUENTIAL red ALONE never retries (it names no cause)",
          not decide("o/r", "9", _fake({1: consolidate})).retry)
    check("a CONSEQUENTIAL red beside a REAL red never retries",
          not decide("o/r", "9", _fake({1: real_red, 2: consolidate})).retry)
    failed_matrix = consolidate.replace("concluded 'skipped'", "concluded 'failure'")
    check("a matrix that concluded 'failure' is a shard's own red, NOT consequential",
          not decide("o/r", "9", _fake({1: nuget_reset, 2: failed_matrix})).retry)

    # ── report-a-cascade-once: the cascade line is consequential; the store miss is transient. ──
    cascade = REAL_LOG_HEAD + (
        "2026-10-30T07:37:48.2347562Z ##[error]red because Resolve the released platform failed — see it. "
        "This job's own checks were not run: behind that red they would only restate it.\n")
    store_miss = REAL_LOG_HEAD + (
        "2026-10-30T06:42:58.1824095Z ##[error]ci-run-artifacts: NOT MEASURED — artifact store miss "
        "(infra) — no artifact matches this repository, run, attempt and name after one re-read — x\n")
    check("a cascade line is CONSEQUENTIAL: alone it never retries", not decide("o/r", "9", _fake({1: cascade})).retry)
    check("...beside a transient it retries with it", decide("o/r", "9", _fake({1: nuget_reset, 2: cascade})).retry)
    check("...beside a REAL red it never retries", not decide("o/r", "9", _fake({1: real_red, 2: cascade})).retry)
    check("...and only the EMITTED record counts, never the echoed source",
          classify(REAL_LOG_HEAD + "2026-10-30T07:37:48.0Z \x1b[36;1mecho red because X failed — see it.\x1b[0m\n") is None)
    check("an artifact-store miss (NOT MEASURED) is a transient: one mechanical retry",
          decide("o/r", "9", _fake({1: store_miss})).retry)
    check("...but a store miss beside a REAL red (its producer failed) never retries",
          not decide("o/r", "9", _fake({1: real_red, 2: store_miss})).retry)
    check("...and the PRE-classification miss line (no NOT MEASURED) matches nothing",
          classify(REAL_LOG_HEAD + "2026-10-30T06:42:58.1Z ##[error]ci-run-artifacts: no artifact matches "
                   "this repository, run, attempt and name — x\n") is None)

    # ── A retry actually posts. ──
    fetch = _fake({1: escaped})
    code = run("o/r", "9", fetch, dry_run=False)
    check("an all-transient run POSTS rerun-failed-jobs",
          code == 0 and any("POST /repos/o/r/actions/runs/9/rerun-failed-jobs" in call
                            for call in fetch.calls))  # type: ignore[attr-defined]
    fetch = _fake({1: escaped})
    run("o/r", "9", fetch, dry_run=True)
    check("--dry-run posts nothing",
          not any("rerun-failed-jobs" in call for call in fetch.calls))  # type: ignore[attr-defined]

    # ── A refused POST is a red, not a silent non-retry. ──
    def refuses_post(path: str, method: str = "GET"):
        if method == "POST":
            raise urllib.error.HTTPError(path, 403, "Forbidden", {}, None)  # type: ignore[arg-type]
        if "/jobs?" in path:
            return {"total_count": 1, "jobs": [{"id": 1, "conclusion": "failure", "steps": ONE_FAILED_STEP}]}
        if path.endswith("/actions/runs/9"):
            return dict(RUN_STILL_RED)
        return escaped

    check("a refused rerun POST exits 1, not a quiet non-retry",
          run("o/r", "9", refuses_post, dry_run=False) == 1)

    # ── Pagination: a failure on page two is READ AND USED, not merely requested. ──
    # Page 1 is all transient, page 2 carries a compile red. Asserting only "page=2 was requested"
    # would still pass if page-2 records were ignored or duplicated, so the assertion is on the
    # DECISION: the page-2 red must block the retry.
    fetch = _fake({1: escaped, 2: escaped, 3: real_red}, pages=[[1, 2], [3]])
    decision = decide("o/r", "9", fetch)
    check("page 2 is requested at all", any("page=2" in call for call in fetch.calls))  # type: ignore[attr-defined]
    check("a NON-transient on page 2 blocks the retry (page-2 records are USED)", not decision.retry)
    check("...and the blocking job named is the page-2 one", decision.reason.startswith("job 3 "))
    # The mirror case: both pages transient ⇒ retry. Without it the case above could pass for a
    # reader that treats any second page as a refusal.
    all_transient = _fake({1: escaped, 2: escaped, 3: escaped}, pages=[[1, 2], [3]])
    check("both pages transient ⇒ still one retry", decide("o/r", "9", all_transient).retry)

    # ── An INCOMPLETE job listing is a red, not a quiet "no failed jobs". ──
    short = _fake({1: escaped}, pages=[[1], []])  # total_count says 1, page 2 empty ⇒ consistent
    check("a listing that completes is fine", decide("o/r", "9", short) is not None)
    truncated = _fake({}, total=150, pages=None)  # total_count 150, page 1 serves 0 records
    code = run("o/r", "9", truncated, dry_run=False)
    check("a listing truncated before total_count exits 1, not a no-op", code == 1)
    for broken in ({"total_count": 5}, {"jobs": "nope", "total_count": 1}, []):
        check(f"a malformed jobs envelope {broken!r} is a red",
              run("o/r", "9", lambda p, method="GET", b=broken: b, dry_run=False) == 1)

    # ── An unparseable 200 is a NAMED red, not a traceback. ──
    def bad_json(path: str, method: str = "GET"):
        raise LogUnreadable(f"{path} answered unparseable JSON: expecting value")

    check("an unparseable API response exits 1, LOUDLY", run("o/r", "9", bad_json, dry_run=False) == 1)

    # ── No failed jobs listed: a documented no-op, not a retry. ──
    check("a run with no failed jobs does nothing", not decide("o/r", "9", _fake({})).retry)

    # ══ RUNNER DEATH — the annotation path (MeshWeaver.Manufacturing#84, Plugins#1849/#2773). ══════
    # The satellites' inline copies read only the LOG, so a runner that died left them nothing to
    # read and they declined ("GitHub holds no log (HTTP 404) — cannot prove a transient"), every
    # time, while the ci-main-red sentinel reopened. These cases are that shape in both directions.
    fetch = _fake({1: _http(404)}, steps={1: FROZEN_LEG}, annotations={1: LOST_COMMUNICATION})
    decision = decide("o/r", "9", fetch)
    check("a runner death with NO failed step is a transient (read from the ANNOTATION)", decision.retry)
    check("...and the log was never even asked for (the annotation is where the reason is)",
          not any(call.endswith("/logs") for call in fetch.calls))  # type: ignore[attr-defined]
    check("a refused job (`steps: []`, Actions budget annotation) is a transient",
          decide("o/r", "9", _fake({1: _http(404)}, steps={1: []},
                                   annotations={1: BUDGET_ANNOTATION})).retry)
    check("a runner SHUTDOWN signal with no failed step is a transient",
          decide("o/r", "9", _fake({1: _http(404)}, steps={1: FROZEN_LEG},
                                   annotations={1: SHUTDOWN_SIGNAL})).retry)
    unnamed = [{"annotation_level": "failure", "message": "Process completed with exit code 1."}]
    fetch = _fake({1: _http(404)}, steps={1: FROZEN_LEG}, annotations={1: unnamed})
    check("no failed step and an UNNAMED annotation does NOT retry",
          run("o/r", "9", fetch, dry_run=False) == 0 and not _posted(fetch))
    notice = [dict(LOST_COMMUNICATION[0], annotation_level="notice")]
    check("the death sentence at NOTICE level does NOT retry (failure-level only)",
          not decide("o/r", "9", _fake({1: _http(404)}, steps={1: FROZEN_LEG},
                                       annotations={1: notice})).retry)

    # The #2773 door: a step failed, the log was NEVER written (404), the runner-death annotation is
    # there. That is a death mid-step — and the ONLY way a failed step is excused without its log.
    fetch = _fake({1: _http(404)}, steps={1: DIED_IN_STEP}, annotations={1: LOST_COMMUNICATION})
    code = run("o/r", "9", fetch, dry_run=False)
    check("a death MID-STEP (failed step, 404 log, death annotation) retries ONCE",
          code == 0 and _posted(fetch) == ["POST /repos/o/r/actions/runs/9/rerun-failed-jobs"])
    check("...and an EXPIRED log (410) with the same annotation does NOT — it existed, it may have "
          "shown a real red",
          not decide("o/r", "9", _fake({1: _http(410)}, steps={1: DIED_IN_STEP},
                                       annotations={1: LOST_COMMUNICATION})).retry)
    check("...and a BUDGET annotation never excuses a failed step (only 'the runner went away' does)",
          not decide("o/r", "9", _fake({1: _http(404)}, steps={1: DIED_IN_STEP},
                                       annotations={1: BUDGET_ANNOTATION})).retry)

    # A failed step whose log IS readable is judged by the log alone — an annotation never
    # overrides it. The annotation must not even be READ (it would be evidence for nothing).
    fetch = _fake({1: real_red}, annotations={1: LOST_COMMUNICATION})
    check("a READABLE real red stays red even beside a death annotation",
          not decide("o/r", "9", fetch).retry)
    check("...and its annotations were never read",
          not any("/check-runs/" in call for call in fetch.calls))  # type: ignore[attr-defined]

    # The decline that USED to be the only outcome is still the outcome when nothing proves a death,
    # and it is a decline (exit 0, named, a warning) — never a retry and never a red.
    fetch = _fake({1: _http(404)}, steps={1: DIED_IN_STEP})
    decision = decide("o/r", "9", fetch)
    check("a failed step with NO log and NO death annotation declines",
          not decision.retry and "cannot prove a transient" in decision.reason)
    check("...and is listed for a `Judged without a log` warning", len(decision.logless) == 1)
    check("...and exits 0 posting nothing", run("o/r", "9", fetch, dry_run=False) == 0 and not _posted(fetch))
    fetch = _fake({1: _http(410)})
    check("an EXPIRED log (410) declines by name (a fact about the job, not a blind read)",
          run("o/r", "9", fetch, dry_run=False) == 0 and not _posted(fetch))

    # 🚨 The annotations are an INPUT. A refusal (no `checks: read`) is a RED naming the grant —
    # never the quiet decline that kept the satellites blind.
    for status in (403, 404, 500):
        fetch = _fake({1: _http(404)}, steps={1: FROZEN_LEG}, annotations={1: _http(status)})
        check(f"unreadable annotations (HTTP {status}) exit 1 and post nothing",
              run("o/r", "9", fetch, dry_run=False) == 1 and not _posted(fetch))
    fetch = _fake({1: _http(404)}, steps={1: FROZEN_LEG}, annotations={1: "<html>nope</html>"})
    check("annotations that are not a list exit 1", run("o/r", "9", fetch, dry_run=False) == 1)
    many = [{"annotation_level": "warning", "message": "noise"}] * 100

    def paged_annotations(path: str, method: str = "GET"):
        if "/check-runs/" in path:
            return many if path.endswith("page=1") else LOST_COMMUNICATION
        return _fake({1: _http(404)}, steps={1: FROZEN_LEG})(path, method)

    check("an annotation on page 2 is READ (a full page is not the end)",
          decide("o/r", "9", paged_annotations).retry)

    # ── Verdict jobs: excused only beside a proven transient, never a cause alone. ──
    # The runner's echo of the step's `run:` source — every finding the step COULD raise, as source.
    verdict_preamble = "".join(
        f"2026-09-22T09:49:19.{i}Z \x1b[36;1m{src}\x1b[0m\n" for i, src in enumerate((
            'echo "scope=$SCOPE, selection named $COUNT module bundle(s); pack job: $PACK_RESULT, '
            'tests lane: $TESTS_RESULT"',
            'echo "::error title=Module bundles incomplete::the scope is FULL, so every one of the \\',
            "$declared declared module(s) must be packed — but the selection named $COUNT. The \\",
            'selection and the caller\'s matrix disagree."',
            "echo \"::error title=Module bundles incomplete::the selection job reported \\",
            "echo \"::error title=Module bundles incomplete::the selection produced no lane key, so \\",
        )))
    verdict_upstream = REAL_LOG_HEAD + verdict_preamble + (
        "2026-09-22T09:49:20.0Z scope=affected, selection named 3 module bundle(s); pack job: failure, "
        "tests lane: skipped\n"
        "2026-09-22T09:49:21.0Z ##[error]Process completed with exit code 1.\n")
    check("the verdict fixture really carries the echoed findings (so the next case can fail)",
          all(s in verdict_preamble for s in VERDICT_STEPS["Exactly the selected bundles were built"][2]))
    check("a lone VERDICT job never retries (its own red, with nothing behind it)",
          not decide("o/r", "9", _fake({1: verdict_upstream}, steps={1: VERDICT_ONLY})).retry)
    fetch = _fake({1: _http(404), 2: verdict_upstream}, steps={1: FROZEN_LEG, 2: VERDICT_ONLY},
                  annotations={1: LOST_COMMUNICATION})
    code = run("o/r", "9", fetch, dry_run=False)
    check("a death + the aggregator that failed BECAUSE of it retry together",
          code == 0 and bool(_posted(fetch)))
    check("...as a WHOLE-run rerun: the skipped dependents do not come back from rerun-failed-jobs",
          _posted(fetch) == ["POST /repos/o/r/actions/runs/9/rerun"])
    check("a verdict job beside a REAL red never retries",
          not decide("o/r", "9", _fake({1: real_red, 2: verdict_upstream},
                                       steps={2: VERDICT_ONLY})).retry)
    # 🚨 The step NAME is not the proof (review on #6898): the same step fails on its OWN findings.
    full_scope_mismatch = verdict_upstream.replace(
        "##[error]Process completed", "##[error]the scope is FULL, so every one of the 4 declared "
        "module(s) must be packed — but the selection named 3. The selection and the caller's matrix "
        "disagree.\n2026-09-22T09:49:21.1Z ##[error]Process completed")
    check("a verdict step's OWN finding (FULL-scope mismatch) beside a runner death does NOT retry",
          not decide("o/r", "9", _fake({1: _http(404), 2: full_scope_mismatch},
                                       steps={1: FROZEN_LEG, 2: VERDICT_ONLY},
                                       annotations={1: LOST_COMMUNICATION})).retry)
    upstream_green = verdict_upstream.replace("pack job: failure", "pack job: success")
    check("a genuine consequential verdict is excused DESPITE the findings echoed in its source "
          "(review on #6898)",
          decide("o/r", "9", _fake({1: _http(404), 2: verdict_upstream},
                                   steps={1: FROZEN_LEG, 2: VERDICT_ONLY},
                                   annotations={1: LOST_COMMUNICATION})).retry)
    echoed_only_upstream = REAL_LOG_HEAD + verdict_preamble.replace(
        "pack job: $PACK_RESULT", "pack job: failure") + "2026-09-22T09:49:21.0Z ##[error]x\n"
    check("an upstream 'failure' that appears only in ECHOED source is not evidence",
          not decide("o/r", "9", _fake({1: _http(404), 2: echoed_only_upstream},
                                       steps={1: FROZEN_LEG, 2: VERDICT_ONLY},
                                       annotations={1: LOST_COMMUNICATION})).retry)
    check("a verdict step whose upstream jobs SUCCEEDED beside a runner death does NOT retry",
          not decide("o/r", "9", _fake({1: _http(404), 2: upstream_green},
                                       steps={1: FROZEN_LEG, 2: VERDICT_ONLY},
                                       annotations={1: LOST_COMMUNICATION})).retry)
    tests_failed = verdict_upstream.replace("pack job: failure, tests lane: skipped",
                                            "pack job: success, tests lane: failure")
    check("...while a failed TESTS lane behind the verdict is upstream evidence too",
          decide("o/r", "9", _fake({1: _http(404), 2: tests_failed},
                                   steps={1: FROZEN_LEG, 2: VERDICT_ONLY},
                                   annotations={1: LOST_COMMUNICATION})).retry)
    # The verdict list is only as good as its names. Each must still be a step of the core lane that
    # defines it — a renamed step would silently veto every death behind that aggregator.
    workflows = Path(__file__).resolve().parent.parent / "workflows"
    for step, (lane, _, independent) in VERDICT_STEPS.items():
        defined = workflows / lane
        text = defined.read_text(encoding="utf-8") if defined.is_file() else ""
        check(f"verdict step {step!r} is still declared in {lane} (read: {defined.is_file()})",
              f"name: {step}" in text)
        check(f"...and its upstream-evidence line is still printed there",
              VERDICT_EVIDENCE_IN_LANE[step] in text)
        for sentence in independent:
            check(f"...and its own finding {sentence!r} is still raised there", sentence in text)

    # ── Review on #6898: an EMPTY annotations body is a blind read, not "no annotations". ──
    fetch = _fake({1: _http(404)}, steps={1: FROZEN_LEG}, annotations={1: {}})
    check("an EMPTY annotations response exits 1 (never read as an empty list)",
          run("o/r", "9", fetch, dry_run=False) == 1 and not _posted(fetch))
    check("...while a real `[]` is a legitimate 'no annotations' (a named decline)",
          run("o/r", "9", _fake({1: _http(404)}, steps={1: FROZEN_LEG},
                                annotations={1: []}), dry_run=False) == 0)

    # ── Review on #6898: annotation-only outcomes were judged without a log, and say so. ──
    decision = decide("o/r", "9", _fake({1: _http(404)}, steps={1: FROZEN_LEG},
                                        annotations={1: LOST_COMMUNICATION}))
    check("a runner death proven by annotation is listed as judged WITHOUT a log",
          decision.retry and len(decision.logless) == 1 and "lost communication" in decision.logless[0])
    decision = decide("o/r", "9", _fake({1: _http(404)}, steps={1: FROZEN_LEG}, annotations={1: unnamed}))
    check("...and so is an annotation-only decline", not decision.retry and len(decision.logless) == 1)

    # ── Review on #6898: the POST acts on the run as it is NOW, not on the stale event. ──
    for name, state in (("a hand re-run started attempt 2", {"run_attempt": 2, "status": "in_progress", "conclusion": None}),
                        ("attempt 2 already failed for real", {"run_attempt": 2, "status": "completed", "conclusion": "failure"}),
                        ("the run is now green", {"run_attempt": 1, "status": "completed", "conclusion": "success"})):
        fetch = _fake({1: escaped}, run_state=state)
        check(f"{name}: no POST, exit 0, named",
              run("o/r", "9", fetch, dry_run=False, attempt=1) == 0 and not _posted(fetch))
    for name, state in (("HTTP 500", _http(500)), ("no run_attempt", {"status": "completed"}),
                        ("a non-object", ["nope"])):
        fetch = _fake({1: escaped}, run_state=state)
        check(f"an unreadable run re-read ({name}) exits 1 and posts nothing",
              run("o/r", "9", fetch, dry_run=False, attempt=1) == 1 and not _posted(fetch))
    fetch = _fake({1: escaped})
    run("o/r", "9", fetch, dry_run=False, attempt=1)
    reads = [c for c in fetch.calls if c == "GET /repos/o/r/actions/runs/9"]  # type: ignore[attr-defined]
    check("the run is re-read immediately BEFORE the POST",
          bool(reads) and fetch.calls.index(reads[-1]) == len(fetch.calls) - 2)  # type: ignore[attr-defined]

    # ── ONE retry, ever, and the attempt is the EVENT's. ──
    fetch = _fake({1: escaped})
    check("attempt 2 reads nothing, posts nothing, and exits 0 naming why",
          run("o/r", "9", fetch, dry_run=False, attempt=2) == 0 and not fetch.calls)  # type: ignore[attr-defined]
    fetch = _fake({1: escaped})
    run("o/r", "9", fetch, dry_run=False, attempt=1)
    check("attempt 1 lists THAT attempt's jobs (a hand rerun is never the one judged)",
          any("/runs/9/attempts/1/jobs?" in call for call in fetch.calls))  # type: ignore[attr-defined]
    fetch = _fake({1: escaped})
    run("o/r", "9", fetch, dry_run=False, scope=RERUN_ALL)
    check("a caller that asks for the whole run gets POST rerun",
          _posted(fetch) == ["POST /repos/o/r/actions/runs/9/rerun"])

    # ══ MeshWeaver#7694 / #7697: a FINISHED job GitHub cut at its time limit, in a `cancelled` run. ══
    # Run 41100409024: two stage-0 controls finished green, were cut (`cancelled`), and the stage gate
    # held the suites over them. Before this case the steward never listed a cancelled job, so it
    # answered "no failed jobs" (or skipped the run entirely) and the pull request stayed red.
    cut_run = {1: "", 2: "", 3: STAGE0_HELD_LOG}
    fetch = _fake(cut_run, steps={1: FINISHED_THEN_CUT, 2: POST_STEP_STUCK, 3: GATE_STEP},
                  annotations={1: TIME_LIMIT_5M, 2: TIME_LIMIT_5M},
                  conclusions={1: "cancelled", 2: "cancelled"}, run_state=dict(RUN_CANCELLED))
    decision = decide("o/r", "9", fetch, attempt=1)
    check("#7694: two FINISHED controls cut at their time limit + the stage-0 hold → ONE retry",
          decision.retry)
    check("...of the WHOLE run (a cancelled job and a consequential gate are both in it)",
          decision.scope == RERUN_ALL)
    fetch = _fake(cut_run, steps={1: FINISHED_THEN_CUT, 2: POST_STEP_STUCK, 3: GATE_STEP},
                  annotations={1: TIME_LIMIT_5M, 2: TIME_LIMIT_5M},
                  conclusions={1: "cancelled", 2: "cancelled"}, run_state=dict(RUN_CANCELLED))
    check("...and run() POSTs it although the run's conclusion is `cancelled`, not `failure`",
          run("o/r", "9", fetch, dry_run=False, attempt=1) == 0
          and _posted(fetch) == ["POST /repos/o/r/actions/runs/9/rerun"])
    check("a lone finished-then-cut job (no consequential job) still re-runs the whole run",
          decide("o/r", "9", _fake({1: ""}, steps={1: FINISHED_THEN_CUT}, annotations={1: TIME_LIMIT_5M},
                                   conclusions={1: "cancelled"})).scope == RERUN_ALL)
    # Negative controls — each must decline, or this is a flake-retrier.
    check("a WORK step still running when the cap fired is a real hang → NO retry",
          not decide("o/r", "9", _fake({1: ""}, steps={1: WORK_STEP_HUNG}, annotations={1: TIME_LIMIT_5M},
                                       conclusions={1: "cancelled"})).retry)
    check("a work step that FAILED before the cut → NO retry (its own log decides, and matches nothing)",
          not decide("o/r", "9", _fake({1: REAL_LOG_HEAD + "2026-10-30T17:47:03.0Z boom\n"},
                                       steps={1: ONE_FAILED_STEP}, annotations={1: TIME_LIMIT_5M},
                                       conclusions={1: "cancelled"})).retry)
    check("finished steps WITHOUT the time-limit sentence (cancelled for another reason) → NO retry",
          not decide("o/r", "9", _fake({1: ""}, steps={1: FINISHED_THEN_CUT},
                                       annotations={1: [{"annotation_level": "failure",
                                                         "message": "The operation was canceled."}]},
                                       conclusions={1: "cancelled"})).retry)
    check("#7696: a job SUPERSEDED by a newer run of its concurrency group → NO retry",
          not decide("o/r", "9", _fake({1: ""}, steps={1: FINISHED_THEN_CUT},
                                       annotations={1: SUPERSEDED + TIME_LIMIT_5M[:1]},
                                       conclusions={1: "cancelled"})).retry)
    check("...nor one superseded before any step ran",
          not decide("o/r", "9", _fake({1: ""}, steps={1: []}, annotations={1: SUPERSEDED},
                                       conclusions={1: "cancelled"})).retry)
    check("a time-limit sentence at NOTICE level is not the failure → NO retry",
          not decide("o/r", "9", _fake({1: ""}, steps={1: FINISHED_THEN_CUT},
                                       annotations={1: [dict(TIME_LIMIT_5M[0], annotation_level="notice")]},
                                       conclusions={1: "cancelled"})).retry)
    check("the stage-0 hold ALONE (nothing proven upstream) → NO retry",
          not decide("o/r", "9", _fake({3: STAGE0_HELD_LOG}, steps={3: GATE_STEP})).retry)
    check("the stage-0 hold in ECHOED source only is not evidence",
          classify(REAL_LOG_HEAD + "2026-10-30T17:56:01.0Z \x1b[36;1m  echo \"::error title=x::"
                   + STAGE0_HOLD_LINE + "…\"\x1b[0m\n") is None)
    # The hold line is only evidence while the gate still prints it.
    gate_lane = workflows / "node-repo-stage-gate.yml"
    gate_text = gate_lane.read_text(encoding="utf-8") if gate_lane.is_file() else ""
    check(f"node-repo-stage-gate.yml still EMITS {STAGE0_HOLD_LINE!r} as an error (read: {gate_lane.is_file()})",
          stage_gate_emits_hold(gate_text))
    # Review on #7704: a mention in a COMMENT must not satisfy the guard once the echo is gone.
    comment_only = ("            # The prefix \"" + STAGE0_HOLD_LINE + "\" is a contract.\n"
                    "            echo \"::error title=Stage 2 held::stage zero is red\"\n")
    check("...and a lane that only MENTIONS the line (its echo renamed) fails that guard",
          not stage_gate_emits_hold(comment_only))
    check("...while the real echo shape passes it (positive control)",
          stage_gate_emits_hold("            echo \"::error title=Stage 2 held — stage 0 red::"
                                + STAGE0_HOLD_LINE + "a cheap static control is red\"\n"))
    # Review on #7704: a SUPERSEDED job is vetoed before ANY proof — even a cancelled work step whose
    # log carries a named transient signature (re-running it would revive the old head).
    cancelled_step = _steps(("Set up job", "completed", "success"), ("Run", "completed", "cancelled"))
    fetch = _fake({1: escaped}, steps={1: cancelled_step}, annotations={1: SUPERSEDED},
                  conclusions={1: "cancelled"}, run_state=dict(RUN_CANCELLED))
    check("a SUPERSEDED job with a cancelled step and a TRANSIENT log signature → NO retry, no POST",
          run("o/r", "9", fetch, dry_run=False, attempt=1) == 0 and not _posted(fetch))
    check("...nor a cancelled job with a transient log and NO named proof (a cancel's log is not why)",
          not decide("o/r", "9", _fake({1: escaped}, steps={1: cancelled_step},
                                       annotations={1: [{"annotation_level": "failure",
                                                         "message": "The operation was canceled."}]},
                                       conclusions={1: "cancelled"})).retry)

    # ── The lane grants what this script reads. A lane without `checks: read` is blind to deaths. ──
    lane = workflows / "node-repo-retry-transients.yml"
    lane_text = lane.read_text(encoding="utf-8") if lane.is_file() else ""
    # ...and it WAKES for the runs this script judges: a lane filtered to `failure` never sees a run
    # whose only reds were cut jobs (they conclude `cancelled`) — the #7694 shape.
    check("the steward judges `failure` AND `cancelled` runs",
          JUDGED_RUN_CONCLUSIONS == {"failure", "cancelled"})
    for conclusion in ("failure", "cancelled"):
        check(f"node-repo-retry-transients.yml admits a `{conclusion}` run",
              f"github.event.workflow_run.conclusion == '{conclusion}'" in lane_text)
    for grant in ("actions: write", "checks: read"):
        check(f"node-repo-retry-transients.yml grants `{grant}` (read: {lane.is_file()})",
              re.search(rf"^\s+{re.escape(grant)}\b", lane_text, re.M) is not None)

    # ══ THE REAL ADAPTER, end to end. ══════════════════════════════════════════════════════════
    # 🚨 Every case above injects a DOUBLE, so none of them executes `github_fetch_with` — the
    # component that actually broke in #4534. A regression there would make the live steward blind
    # again while every case above stayed green, which is the exact failure mode this file exists
    # to prevent. So the adapter is driven against a loopback server (offline, no network) over
    # the shape that broke: a 302 to signed storage, answering an escape-bearing log.
    failures.extend(_self_test_adapter(check))
    if live_summary is not None:
        after = os.path.getsize(live_summary) if os.path.exists(live_summary) else -1
        check("the LIVE job summary was not written by the self-test", after == before)

    print(f"{'FAILED: ' + ', '.join(failures) if failures else 'all cases passed'}")
    return 1 if failures else 0


def _self_test_adapter(check) -> list[str]:
    import http.server
    import threading

    global GITHUB_API
    seen_auth: dict[str, str | None] = {}
    storage_origin = [""]  # the signed-storage server's origin, set once it is listening
    log_body = (REAL_LOG_HEAD + "2026-09-17T04:35:33.0Z dial tcp 51.12.25.82:443: connect: "
                "connection refused\n").encode("utf-8")

    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args):  # keep the self-test output clean
            pass

        def _record(self):
            seen_auth[self.path] = self.headers.get("Authorization")

        def do_GET(self):
            self._record()
            if self.path.endswith("/logs"):
                # The shape that matters: a redirect to SIGNED storage on ANOTHER origin (a second
                # server on its own port), as GitHub's blob storage is another host.
                self.send_response(302)
                self.send_header("Location", f"{storage_origin[0]}/signed-storage/log.txt")
                self.end_headers()
            elif self.path.startswith("/signed-storage/"):
                self.send_response(200)
                self.send_header("Content-Type", "text/plain; charset=utf-8")
                self.send_header("Content-Length", str(len(log_body)))
                self.end_headers()
                self.wfile.write(log_body)
            elif self.path.startswith("/bad-json"):
                payload = b"{not json at all"
                self.send_response(200)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
            else:
                payload = b'{"total_count": 1, "jobs": [{"id": 1, "conclusion": "failure"}]}'
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

        def do_POST(self):
            self._record()
            self.send_response(201)
            self.send_header("Content-Length", "0")
            self.end_headers()

    server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    storage = http.server.HTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=storage.serve_forever, daemon=True).start()
    storage_origin[0] = f"http://127.0.0.1:{storage.server_address[1]}"
    original, GITHUB_API = GITHUB_API, f"http://127.0.0.1:{server.server_address[1]}"
    local: list[str] = []

    def adapter_check(name: str, condition: bool) -> None:
        print(f"  {'ok  ' if condition else 'FAIL'}  {name}")
        if not condition:
            local.append(name)

    try:
        fetch = github_fetch_with("secret-token")

        # THE #4534 REGRESSION, through the real reader: a redirected, escape-bearing log.
        body = read_job_log("o/r", 1, fetch)
        adapter_check("the real adapter follows the 302 and returns the log", "connection refused" in body)
        adapter_check("...and the log it returned really carries ESC", "\x1b[" in body)
        adapter_check("...and the classifier matches it", classify(body) is not None)

        # 🚨 The token must NOT cross to the storage host. ghrest's origin rule is what stops it; a
        # reader rewritten onto stock urllib with a plain header would leak it and nothing else would
        # notice (ghrest's own self-test measures that negative control).
        adapter_check("the API request carried the token",
                      seen_auth.get("/repos/o/r/actions/jobs/1/logs") == "Bearer secret-token")
        adapter_check("the REDIRECT target (another origin) was reached, with NO Authorization header",
                      "/signed-storage/log.txt" in seen_auth and seen_auth["/signed-storage/log.txt"] is None)

        adapter_check("a JSON endpoint decodes to a dict", isinstance(fetch("/repos/o/r/x"), dict))
        adapter_check("a POST returns without raising", fetch("/repos/o/r/y", method="POST") == {})
        try:
            fetch("/bad-json")
            adapter_check("an unparseable 200 raises LogUnreadable", False)
        except LogUnreadable as error:
            adapter_check("an unparseable 200 raises LogUnreadable", "unparseable JSON" in str(error))

        # The byte cap refuses rather than truncating: a half log can only produce a false decline.
        global MAX_LOG_BYTES
        cap, MAX_LOG_BYTES = MAX_LOG_BYTES, 10
        try:
            fetch("/repos/o/r/actions/jobs/1/logs")
            adapter_check("a log over the byte cap is REFUSED, not truncated", False)
        except LogUnreadable as error:
            adapter_check("a log over the byte cap is REFUSED, not truncated", "exceeds" in str(error))
        finally:
            MAX_LOG_BYTES = cap
    finally:
        GITHUB_API = original
        server.shutdown()
        server.server_close()
        storage.shutdown()
        storage.server_close()
    return local


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", help="owner/name")
    parser.add_argument("--run-id", help="the failed run to consider")
    parser.add_argument("--token", default=None, help="defaults to $GH_TOKEN")
    parser.add_argument("--attempt", type=int, default=None,
                        help="the EVENT's run_attempt; only attempt 1 is judged (one retry, ever)")
    parser.add_argument("--rerun-scope", choices=(RERUN_FAILED, RERUN_ALL), default=RERUN_FAILED,
                        help="failed-jobs (POST rerun-failed-jobs) or all (POST rerun); a proven "
                             "consequential job forces all")
    parser.add_argument("--dry-run", action="store_true", help="decide and print, never POST")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if not args.repo or not args.run_id:
        parser.error("--repo and --run-id are required unless --self-test")

    import os
    token = args.token or os.environ.get("GH_TOKEN", "")
    if not token:
        # An absent credential is a red, not a decline — the same rule as an unreadable log.
        print("::error title=Retry steward is blind::no GH_TOKEN — the steward cannot read anything.")
        return 1
    return run(args.repo, args.run_id, github_fetch_with(token), args.dry_run,
               attempt=args.attempt, scope=args.rerun_scope)


if __name__ == "__main__":
    sys.exit(main())
