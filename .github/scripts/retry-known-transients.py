#!/usr/bin/env python3
"""One automatic retry for NAMED infrastructure transients — the decision half of
`retry-known-transients.yml`, extracted so it can be PROVEN offline.

"can't we automate this?" (maintainer, 2026-08-31) — the queue-drain labour that motivated this
was re-running ~25-minute CI cycles whose only failures were registry blips.

🚨 THIS IS NOT A FLAKE-RETRIER, and four guards make that structural:

  1. It reruns ONLY when EVERY failed job's log matches a NAMED signature below. One failed job
     without a match — a compile error, a test failure, anything — and it does nothing: a real red
     must stay red, and retrying it would be the investigate-the-red rule's forbidden shape.
  2. ONE attempt, ever: the workflow acts only on run_attempt 1, so a transient that persists
     through the retry surfaces as the durable failure it is.
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
    same shape `.github/scripts/resolve-platform.py` already uses for the one job log it reads,
    including `add_unredirected_header` so the token never crosses the 302 to signed storage.

  • **A read is not believed unless it looks like a job log.** An empty or marker-less body is
    treated as UNREADABLE rather than as a log that happens to match no signature — otherwise
    "I read nothing" and "I read a clean log" become the same answer again, one exit code apart.

Landing this in core reds no other repository: the retry steward is the one CI mechanism in this
fleet that is neither fetched at a pin nor copy-guarded (`check-resolver-copy.py` covers exactly
`scripts/resolve-platform.py`, one constant, and parses Python so it cannot see a workflow at all).
Core's own `block_signatures` clause, added 2026-09-13, reached no satellite and reddened nobody.
The four other copies — Crm, Reinsurance, SocialMedia (inline shell) and MeshWeaver.Plugins
(`scripts/retry-known-transients.py`) — are still blind, on purpose: reviving `rerun-failed-jobs` on
a SATELLITE's `main` erases the `Platform for this run` annotation its PR ceiling reads (#4491,
open), and that is a decision for the maintainer, not a side effect of this fix. Core has no such
ceiling — it only ever runs the resolver's `--self-test` — so reviving core's steward is safe.

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
import urllib.request

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
SIGNATURES = re.compile(
    r"meshweaver\.azurecr\.io.*connection refused"
    r"|dial tcp .*: connect: connection refused"
    r"|registry answered 503 for https://memex\.meshweaver\.cloud"
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
        request = urllib.request.Request(
            f"{GITHUB_API}{path}",
            method=method,
            data=b"" if method == "POST" else None,
            headers={
                "Accept": "application/vnd.github+json",
                "X-GitHub-Api-Version": "2022-11-28",
                "User-Agent": "meshweaver-retry-known-transients",
            },
        )
        # 🚨 UNREDIRECTED. A job-logs path answers a 302 to SIGNED storage, and urllib forwards
        # ordinary headers across a redirect — which would hand this token to a host that is not
        # GitHub and does not need it. The signed URL carries its own authorisation.
        request.add_unredirected_header("Authorization", f"Bearer {token}")
        with urllib.request.urlopen(request, timeout=60) as response:
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
    def __init__(self, retry: bool, reason: str) -> None:
        self.retry = retry
        self.reason = reason


def read_job_log(repo: str, job_id: int, fetch) -> str:
    """Return the job's log, or raise LogUnreadable naming exactly what went wrong."""
    try:
        body = fetch(f"/repos/{repo}/actions/jobs/{job_id}/logs")
    except LogUnreadable:
        raise
    except urllib.error.HTTPError as error:
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


def classify(log: str) -> str | None:
    """Name the matched signature, or None when nothing named matches."""
    if SIGNATURES.search(log):
        return "a named transient signature"
    if BLOCK_SIGNATURES.search(log.replace("\n", " ")):
        return "a named transient BLOCK signature (App-token mint answered 5xx by api.github.com)"
    return None


def failed_job_ids(repo: str, run_id: str, fetch) -> list[int]:
    """EVERY failed job of the run — paginated.

    🚨 The population must be COMPLETE or the EVERY-job rule is evaluated over a truncated one, and
    a run whose real failure sits on page two would be retried as if it were all transients.
    """
    ids: list[int] = []
    seen = 0
    page = 1
    while True:
        # The job LIST is an input too. An HTTP/transport failure here must arrive as the same
        # named red as an unreadable log — never as a traceback, and never as an empty list, which
        # `decide` would report as the documented "no failed jobs listed" no-op.
        try:
            payload = fetch(f"/repos/{repo}/actions/runs/{run_id}/jobs?per_page=100&page={page}")
        except urllib.error.HTTPError as error:
            raise LogUnreadable(
                f"listing the jobs of run {run_id} answered HTTP {error.code} {error.reason}") from error
        except urllib.error.URLError as error:
            raise LogUnreadable(f"listing the jobs of run {run_id} failed: {error.reason}") from error
        # 🚨 VALIDATE THE ENVELOPE. A malformed or short page must not read as "the end of the
        # list": that would hand `decide` a TRUNCATED population, and the EVERY-job rule over a
        # truncated population can retry a run whose real failure it never saw — or, when the
        # first page is empty, report the documented "no failed jobs listed" no-op over a read
        # that failed. Both are #4534 again, one exit code apart.
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
        ids.extend(job["id"] for job in jobs if job.get("conclusion") == "failure")
        if seen >= total:
            break
        page += 1
    return ids


def decide(repo: str, run_id: str, fetch) -> Decision:
    failed = failed_job_ids(repo, run_id, fetch)
    if not failed:
        return Decision(False, f"run {run_id}: failure conclusion but no failed jobs listed — doing nothing.")
    for job_id in failed:
        log = read_job_log(repo, job_id, fetch)  # raises LogUnreadable → red, never a decline
        matched = classify(log)
        if matched is None:
            return Decision(False, (
                f"job {job_id}: NO named signature matched — this is (or may be) a real red, "
                "so no retry. Investigate it."))
        print(f"job {job_id}: matches {matched}.")
    return Decision(True, f"all {len(failed)} failed job(s) matched named transients — one retry.")


def run(repo: str, run_id: str, fetch, dry_run: bool) -> int:
    try:
        decision = decide(repo, run_id, fetch)
    except LogUnreadable as error:
        # 🚨 #4534. The whole reason this is not `exit 0`. A steward that cannot read a log cannot
        # prove ANYTHING — and for a week it said "no retry" in a sentence that read like a verdict.
        print(f"::error title=Retry steward is blind::could not read a job log of run {run_id} "
              f"in {repo}: {error}. This steward cannot prove a transient when it cannot see its "
              f"input, so it fails instead of declining. Investigate the read, then the run.")
        return 1
    print(decision.reason)
    if not decision.retry:
        return 0
    if dry_run:
        print("--dry-run: NOT posting rerun-failed-jobs.")
        return 0
    try:
        fetch(f"/repos/{repo}/actions/runs/{run_id}/rerun-failed-jobs", method="POST")
    except (urllib.error.HTTPError, urllib.error.URLError, LogUnreadable) as error:
        # The decision was sound and the retry did not happen. Say both, by name — a run left
        # un-retried because the POST was refused must not look like a run we chose not to retry.
        reason = (f"HTTP {error.code} {error.reason}" if isinstance(error, urllib.error.HTTPError)
                  else getattr(error, "reason", error))
        print(f"::error title=Retry steward could not rerun::run {run_id} in {repo} matched named "
              f"transients, but POST rerun-failed-jobs was refused: {reason}.")
        return 1
    print(f"posted rerun-failed-jobs for run {run_id}.")
    return 0


# ──────────────────────────────────────────── Self-test ────────────────────────────────────────────

REAL_LOG_HEAD = (
    "﻿2026-09-17T04:35:31.5696641Z Current runner version: '2.337.0'\n"
    "2026-09-17T04:35:31.6698589Z \x1b[36;1mset -euo pipefail\x1b[0m\n"
)


def _fake(logs: dict[int, object], total: int | None = None,
          pages: list[list[int]] | None = None):
    """A fetch double. `logs` maps job id → log text, or an Exception to raise.

    `pages` (a list of job-id lists) serves a DIFFERENT set of job records per page, so a test can
    prove that page two's records are USED and not merely requested.
    """
    calls: list[str] = []

    def fetch(path: str, method: str = "GET"):
        calls.append(f"{method} {path}")
        if path.endswith("/rerun-failed-jobs"):
            return {}
        if "/jobs?" in path:
            if pages is not None:
                index = int(path.split("&page=")[1]) - 1
                served = pages[index] if 0 <= index < len(pages) else []
                return {
                    "total_count": sum(len(p) for p in pages),
                    "jobs": [{"id": job_id, "conclusion": "failure"} for job_id in served],
                }
            return {
                "total_count": total if total is not None else len(logs),
                "jobs": [{"id": job_id, "conclusion": "failure"} for job_id in logs],
            }
        job_id = int(path.split("/jobs/")[1].split("/")[0])
        value = logs[job_id]
        if isinstance(value, Exception):
            raise value
        return value

    fetch.calls = calls  # type: ignore[attr-defined]
    return fetch


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, condition: bool) -> None:
        print(f"  {'ok  ' if condition else 'FAIL'}  {name}")
        if not condition:
            failures.append(name)

    print("retry-known-transients self-test")

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
        ("HTTP 410", urllib.error.HTTPError("u", 410, "Gone", {}, None)),  # type: ignore[arg-type]
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
            return {"total_count": 1, "jobs": [{"id": 1, "conclusion": "failure"}]}
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
    check("...and the blocking job named is the page-2 one", "job 3:" in decision.reason)
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

    # ══ THE REAL ADAPTER, end to end. ══════════════════════════════════════════════════════════
    # 🚨 Every case above injects a DOUBLE, so none of them executes `github_fetch_with` — the
    # component that actually broke in #4534. A regression there would make the live steward blind
    # again while every case above stayed green, which is the exact failure mode this file exists
    # to prevent. So the adapter is driven against a loopback server (offline, no network) over
    # the shape that broke: a 302 to signed storage, answering an escape-bearing log.
    failures.extend(_self_test_adapter(check))

    print(f"{'FAILED: ' + ', '.join(failures) if failures else 'all cases passed'}")
    return 1 if failures else 0


def _self_test_adapter(check) -> list[str]:
    import http.server
    import threading

    global GITHUB_API
    seen_auth: dict[str, str | None] = {}
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
                # The shape that matters: a redirect to SIGNED storage on the same server.
                self.send_response(302)
                self.send_header("Location", "/signed-storage/log.txt")
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

        # 🚨 The token must NOT cross to the storage host. `add_unredirected_header` is what stops
        # it; a reader rewritten with a plain header would leak it and nothing else would notice.
        adapter_check("the API request carried the token",
                      seen_auth.get("/repos/o/r/actions/jobs/1/logs") == "Bearer secret-token")
        adapter_check("the REDIRECT target received NO Authorization header",
                      seen_auth.get("/signed-storage/log.txt") is None)

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
    return local


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", help="owner/name")
    parser.add_argument("--run-id", help="the failed run to consider")
    parser.add_argument("--token", default=None, help="defaults to $GH_TOKEN")
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
    return run(args.repo, args.run_id, github_fetch_with(token), args.dry_run)


if __name__ == "__main__":
    sys.exit(main())
