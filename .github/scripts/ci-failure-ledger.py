#!/usr/bin/env python3
"""ci-failure-ledger.py — ONE `ci-main-red` issue per repository, kept true by every run of `main`.

(The name on this first line is load-bearing: node-repo-ci-failure.yml fetches this file at its
scripts ref and refuses a body whose first 400 bytes do not name it — the same shape as
check-workflow-timeouts.py and compile-check.py.)

WHY (maintainer, 2026-09-12: "put the ci-failure on all repos, in main; triaging is done by
systemorph-com; communicate via MCP — open a thread with a triage agent")
-------------------------------------------------------------------------------------------------
Every satellite runs its full build on `push: [main]` and once a day on `schedule`. A red run there
is attached to no pull request, no reviewer and no check list — it is red in an empty room, the twin
of a gate that cannot fail (AGENTS.md → "a SCHEDULED lane's honest red is red in an EMPTY ROOM").
This script routes it onto an artefact that outlives the run: one issue in the calling repository,
labelled `ci-main-red`, whose BODY is a dated ledger with one entry per red run. The lane then POSTs
a signed event to the control portal, whose triage agent opens a thread on it.

🚨 A MECHANISM MAY ONLY CLOSE AN ISSUE IT OPENED. Core's main-cd.yml files and heals its own
`ci-failure` issue (a DELIVERY hole, a different subject) by listing that label alone, `--limit 1`.
Sharing the label would let a CD heal CLOSE this ledger while CI is still red — and between that
close and the next reopen the signal reads "resolved". So this ledger has its OWN label
(`ci-main-red`, never `ci-failure`) and its own ownership mark, a hidden line in the body
(`<!-- ci-main-red ledger -->`). Label, title and marker are all PUBLIC fields anyone with triage
can put on any issue, so the fourth term is the one a human cannot forge: the issue's AUTHOR must be
the Actions bot (`user.login == "github-actions[bot]"`, `user.type == "Bot"`) — which is also why
the lane takes the caller's GITHUB_TOKEN and nothing else. An issue lacking any of the four is never
appended to, reopened, commented on or closed — it is logged and left alone, and a fresh ledger is
filed beside it if one is needed.

The control-portal URL is validated BEFORE anything is signed (`--check-url`): https, the expected
host (`control-webhook-host`, default control.systemorph.com), a path under /api/hooks/. A signed
HMAC must never travel to whatever non-empty string a caller put in a variable.

THE FIVE RULES, each covered by --self-test
--------------------------------------------
  * two or more matching OPEN issues     → FOLD every newer one into the oldest first (its entries
                                           appended there, a comment on each side, the newer one
                                           closed as not planned), then apply the rules below. The
                                           label listing is not a transaction: it once answered
                                           "0 owned" beside an open ledger and a second one was
                                           filed (#4431 beside #4413, 2026-09-15). See `fold_duplicates`.
  * `failure` + no matching issue        → CREATE one (label + exact title), first ledger entry.
  * `failure` + a matching OPEN issue    → APPEND an entry to its body. Never a second open issue.
  * `failure` + a matching issue CLOSED
    within the last REOPEN_WINDOW_DAYS   → REOPEN it and append (flapping: one story, one issue).
    Closed longer ago                    → CREATE a fresh one (its age is the outage's age, #3176).
  * `success` + a matching OPEN issue    → comment `green again: <run URL>` and CLOSE it.
    `success` + none                     → no-op, and NO event (nothing changed).

An issue this script creates never carries `ci-failure` — that label is CD's, and CD's search
must never find the ledger any more than the ledger finds CD's.

IT CANNOT PASS SILENTLY. A malformed `failed-jobs` input, an outcome that is neither word, a token
that cannot write issues (403 → the message names the `issues: write` grant on the CALLER job), an
API failure — each is RED naming the cause, never a quiet "nothing to do". An API OUTAGE (a
rate-limit 403/429 — "API rate limit exceeded for installation" — a 5xx, or no connection) is NOT
a permission defect: it is waited out to its reset inside MW_API_OUTAGE_WAIT_MINUTES, and past
that the run exits 3 with "NO VERDICT — GitHub API rate-limited until <reset>" (`ApiOutage`,
#7416/#7448). A plain 403 without a rate-limit sign stays the grant message. The one deliberate
"nothing to do" (green, no open issue) SAYS so with the denominator.

    python3 ci-failure-ledger.py                 # in Actions: everything from the environment
    python3 ci-failure-ledger.py --self-test     # prove every rule, against a fake GitHub
    python3 ci-failure-ledger.py --check-url URL --expected-host HOST   # the inbox URL, or red
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone

API = "https://api.github.com"
DEFAULT_LABEL = "ci-main-red"
DEFAULT_TITLE = "ci-main-red: main is red"
DEFAULT_REOPEN_WINDOW_DAYS = 7
MAX_LEDGER_ENTRIES = 40          # ~500 bytes an entry; GitHub caps a body at 65,536 characters
LEDGER_MARK = "<!-- ci-main-red ledger -->"  # the ownership mark: only an issue carrying it is ours
FORBIDDEN_LABEL = "ci-failure"               # CD's label; the ledger never files under it

# 🏷️ The ledger states its own TYPE and SEVERITY (Doc/Architecture/IssueTaxonomy, policy
# `release-blocker-gate`): an issue without them is invisible to the release gate, which counts open
# sev:B/sev:H bugs. A red main is a primary path broken — nothing the repository merges is published
# while it is open — so a bug at sev:H. Triage's BugThreadRefusal still gives a ledger no second worker:
# the red run's own item carries it. The look is core's own (scripts/check-issue-taxonomy.py CANONICAL).
#
# The SEVERITY is the caller's (`severity` input, default sev:H so every existing caller is unchanged).
# A scheduled lane's red (#7416 — lock-pinned-digests, pinned-digests) is a bug at sev:M: tracked and
# visible, never a release gate by itself; a person raises it to sev:H when it threatens production.
# A bug never takes anything but one of the four severities.
DEFAULT_SEVERITY = "sev:H"
SEVERITIES = ("sev:B", "sev:H", "sev:M", "sev:L")
TAXONOMY = ("bug", DEFAULT_SEVERITY)
TAXONOMY_LOOK = {
    "bug": ("d73a4a", "Something isn't working"),
    "sev:B": ("B60205", "BLOCKING - the release cannot ship while this is open"),
    "sev:H": ("D93F0B", "BLOCKING: primary path broken, intermittent user-visible failure, or silently wrong results."),
    "sev:M": ("FBCA04", "Medium - secondary path broken, or easy workaround"),
    "sev:L": ("0E8A16", "Low - cosmetic, rare edge case, or developer-only"),
}


def taxonomy(severity: str) -> tuple[str, str]:
    """("bug", severity) — or RED for anything that is not one of the four severities."""
    if severity not in SEVERITIES:
        raise Red(f"severity {severity!r} is not one of {', '.join(SEVERITIES)} — an unknown severity label "
                  "is invisible to the release gate and the taxonomy lane alike")
    return ("bug", severity)
TYPE_LABELS = ("bug", "enhancement", "documentation", "chore")


def needs_taxonomy(labels: tuple[str, ...]) -> bool:
    """True when the issue carries neither a type nor a severity — a classification a person gave is never touched."""
    return not any(l.lower() in TYPE_LABELS or l.lower().startswith("sev:") for l in labels)
BOT_LOGIN, BOT_TYPE = "github-actions[bot]", "Bot"   # the only author whose issues this ledger owns
INBOX_PATH_PREFIX = "/api/hooks/"
ENTRY_HEAD = "### "              # every ledger entry starts with this at column 0
FAILED_CONCLUSIONS = ("failure", "timed_out")
# 🚨 A failed job with ZERO steps never STARTED. Measured 2026-09-11 (Memex#275): an org Actions
# budget refused 20 jobs across three private repos, each concluding `failure` in ~2 s with no
# steps, unexpanded matrix names and `BlobNotFound` on /logs — a costume shared by a reusable
# workflow the runner could not resolve. The reason lives in ONE place, the job's annotation
# (`GET check-runs/<job id>/annotations`, needs `checks: read`, which this lane does not demand and
# every caller would have to grant). What the ledger CAN say from the fields it already reads is
# that nothing ran, so the reader stops treating the job as a failed step and reads the annotation
# instead — and so the control portal's triage (which holds a credential that CAN read it) knows
# to. Note the structural limit: while a budget refusal is IN FORCE the reporter job of the same
# run is refused with the gates (measured: 16 failure / 7 skipped / 0 ran), so a live refusal
# reaches this ledger only from a run whose window closed before the reporter started.
NEVER_STARTED_NOTE = ("never started — no step ran, which is a refused job (an org Actions budget) or "
                      "a workflow the runner could not resolve; the job's annotation says which")

# ── the ARTIFACT-STORE MISS ledger (`--artifact-misses`) ──────────────────────────────────────
#
# ci-run-artifacts.py classifies an exact-name download that finds nothing after its one re-read as
# NOT MEASURED (infra): the producer had succeeded, so the miss says something about the shared
# store and nothing about the diff (ReadingCiSignals → "An artifact-store miss is NOT MEASURED").
# Those misses used to be routed to a HAND-MAINTAINED tracking issue (MeshWeaver#7656) that a person
# had to remember to comment on, so a miss nobody copied there was a miss nobody counted. They now
# go onto a ledger of their own, kept by this script under every rule above: the repository's main
# build lane (label `ci-main-red`) runs this mode after its own ledger, a red run whose red jobs
# PRINTED the miss appends a dated entry (filing, or reopening inside the window), and the next
# green `main` closes it. The ledger KEY is its own label + exact title + the ownership mark, never
# `ci-main-red`'s, so the two stories never share an issue and neither can close the other.
#
# The miss is recognised by the line ci-run-artifacts.py EMITS (`::error title=…::ci-run-artifacts:
# NOT MEASURED — artifact store miss (infra) — …`), which the job log carries as `##[error]…` — the
# same signature retry-known-transients.py matches, never the echoed source (`::error title=` inside
# an escape-coloured `run:` echo). test_ci_run_artifacts.py proves the emitter and this pattern agree.
MISS_LABEL = "ci-artifact-store-miss"
MISS_TITLE = "ci-artifact-store-miss: an artifact-store miss — NOT MEASURED (infra)"
MISS_SEVERITY = "sev:M"          # infra, tracked and visible; a person raises it if it threatens production
MISS_LINE = re.compile(r"##\[error\]ci-run-artifacts: NOT MEASURED — artifact store miss \(infra\)")
MAX_LOG_BYTES = 16 * 1024 * 1024  # over the cap is RED — a match over part of a log is a false "no miss"
LABEL_DESCRIPTIONS = {
    MISS_LABEL: "a CI job could not read an artifact its producer published — NOT MEASURED (infra), the miss ledger",
}
DEFAULT_LABEL_DESCRIPTION = "main is red — the CI ledger every red run on main appends to (not CD's ci-failure)"


class Red(Exception):
    """Something this lane needs could not be established. Never downgraded to 'nothing to do'."""


# ── the pure parts ────────────────────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class Run:
    repo: str
    outcome: str
    run_id: str
    run_url: str
    sha: str
    trigger: str
    failed_jobs: tuple[dict, ...]
    platform_set: str
    jobs_note: str = ""          # set when the run's jobs could not be listed (recorded, then RED)


@dataclass
class Issue:
    number: int
    title: str
    state: str
    body: str
    html_url: str
    labels: tuple[str, ...] = ()
    closed_at: str | None = None
    author: str = ""
    author_type: str = ""


@dataclass
class Verdict:
    action: str                  # create | append | reopen | close | noop
    issue: int | None
    reason: str


def parse_failed_jobs(raw: str) -> tuple[dict, ...]:
    """The caller's `failed-jobs` input as a tuple of {name, url, step, neverStarted}. Anything else
    is refused. `neverStarted` is optional (default false) and must be a boolean when given.

    Refused BEFORE any write: an entry that names no job is a ledger that cannot be triaged, and a
    ledger written from garbage looks exactly like one written from evidence.
    """
    raw = (raw or "").strip() or "[]"
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as e:
        raise Red(f"failed-jobs is not JSON ({e}); expected an array of {{name, url, step}}") from e
    if not isinstance(data, list):
        raise Red(f"failed-jobs must be a JSON ARRAY of {{name, url, step}}, got {type(data).__name__}")
    out = []
    for i, item in enumerate(data):
        if not isinstance(item, dict):
            raise Red(f"failed-jobs[{i}] is {type(item).__name__}, expected an object {{name, url, step}}")
        name, url, step = item.get("name"), item.get("url"), item.get("step", "")
        if not isinstance(name, str) or not name.strip():
            raise Red(f"failed-jobs[{i}] has no string `name`")
        if not isinstance(url, str) or not url.startswith("https://"):
            raise Red(f"failed-jobs[{i}] (`{name}`) has no https `url`")
        if step is None:
            step = ""
        if not isinstance(step, str):
            raise Red(f"failed-jobs[{i}] (`{name}`) has a non-string `step`")
        never_started = item.get("neverStarted", False)
        if not isinstance(never_started, bool):
            raise Red(f"failed-jobs[{i}] (`{name}`) has a non-boolean `neverStarted`")
        entry = {"name": name.strip(), "url": url, "step": step.strip(), "neverStarted": never_started}
        cause = item.get("cause")
        if cause is not None:
            # Rendered inside a markdown table cell and backticks, so it must be a short single-line
            # token — `--list-failed-jobs` writes `<hash>/<n>` and nothing else ever needs more.
            if not isinstance(cause, str) or not CAUSE_RE.fullmatch(cause):
                raise Red(f"failed-jobs[{i}] (`{name}`) has a `cause` that is not a cause signature "
                          f"(`<10 hex>/<count>` or `-/0`): {cause!r}")
            entry["cause"] = cause
        out.append(entry)
    return tuple(out)


# ── WHICH red is this? (#7416) ────────────────────────────────────────────────────────────────
#
# A lane red ten runs in a row is not necessarily red for ONE reason. Measured on core's
# lock-pinned-digests lane (runs 25-34, 2026-09-15..21): runs 25-27 died on an installation's
# `/api/version` timing out, runs 28-34 on an undeclared registry — the SAME job, the SAME step, two
# unrelated causes. A ledger that names only the failed step therefore reads "same red for six days",
# and a reader who diagnosed the first cause and came back later had no way to see it had changed.
#
# The distinguishing evidence is the failing job's ERROR ANNOTATIONS (every `::error::` line a
# script prints becomes one). They are NOT copied into the issue: a lane's errors may name a host or
# a registry of a client estate, and the issue is public text (policy `issue-text-name-scan`). What
# the ledger records is a SIGNATURE — a short hash over the normalised error lines — so two reds with
# the same cause carry the same signature and a changed cause reads as CHANGED, with the run link one
# click away for the text. Normalisation folds what varies run to run without changing the cause:
# case, whitespace, every digit (counts, timings, timestamps, run ids) and every long hex run (shas,
# digests). GitHub's own `Process completed with exit code N.` line says nothing about the cause and
# is dropped. What a signature does NOT establish: two signatures differing is "the error TEXT
# differs", which a re-worded message can also produce — read both runs before calling it a new cause.
CAUSE_RE = re.compile(r"(?:[0-9a-f]{10}|-)/\d{1,4}")
GENERIC_EXIT_RE = re.compile(r"^process completed with exit code \d+\.?$", re.IGNORECASE)
LISTED_CONCLUSIONS = FAILED_CONCLUSIONS + ("cancelled",)   # a job cut at its timeout-minutes concludes `cancelled`
_HEX_RUN = re.compile(r"\b[0-9a-f]{7,}\b")
_DIGITS = re.compile(r"\d+")
_SPACE = re.compile(r"\s+")
_CAUSE_ROW = re.compile(r"^\| cause \| `([0-9a-f]{10}|unknown)`", re.MULTILINE)


def normalise_error(message: str) -> str:
    """One error annotation's text with what varies between runs of the SAME cause folded. Pure."""
    m = _SPACE.sub(" ", (message or "").strip().lower())
    m = _HEX_RUN.sub("h", m)
    return _DIGITS.sub("0", m)


def cause_signature(messages: list[str]) -> str:
    """`<10 hex>/<n>` over a job's error annotations (n = distinct normalised lines), `-/0` when the
    job printed nothing but the generic exit-code line. Pure, order-insensitive."""
    lines = sorted({normalise_error(x) for x in messages if x and not GENERIC_EXIT_RE.match(x.strip())})
    lines = [x for x in lines if x]
    if not lines:
        return "-/0"
    return hashlib.sha256("\n".join(lines).encode()).hexdigest()[:10] + f"/{len(lines)}"


def run_cause(jobs: tuple[dict, ...]) -> str:
    """The run's cause identity: a hash over every failed job's (name, step, cause signature). Two
    reds with the same identity failed the same jobs at the same steps with the same error text."""
    if not jobs:
        return "unknown"
    # 🚨 Comparable ONLY when every failed job carries error evidence (Copilot on #7422). Without a
    # signature the hash is over job + step names alone — two unrelated failures in one step would
    # read "same" without either error being read — and `-/0` is a job that printed no error at all.
    if any(not j.get("cause") or j["cause"].startswith("-/") for j in jobs):
        return "n/a"
    parts = sorted(f"{j['name']}|{j.get('step', '')}|{j.get('cause', '')}" for j in jobs)
    return hashlib.sha256("\n".join(parts).encode()).hexdigest()[:10]


def previous_cause(body: str) -> str | None:
    """The cause identity the NEWEST ledger entry in `body` recorded, or None (no entry, or an entry
    written before causes were recorded)."""
    entries = entries_of(body or "")
    if not entries:
        return None
    m = _CAUSE_ROW.search(entries[-1])
    return m.group(1) if m and m.group(1) != "unknown" else None


def cause_row(run: Run, previous_body: str) -> str:
    """The `| cause |` table row: this red's identity and whether it CHANGED since the previous one."""
    now_cause = run_cause(run.failed_jobs)
    if now_cause == "unknown":
        return "| cause | `unknown` — this run's failed jobs could not be listed, so its cause cannot be compared |"
    if now_cause == "n/a":
        return ("| cause | `n/a` — not compared: a failed job carries no error signature (the caller did not pass "
                "`failed-jobs` from `--list-failed-jobs`, or the job printed no error beyond its exit code) |")
    before = previous_cause(previous_body)
    if before is None:
        verdict = "first recorded red in this ledger (no earlier entry carries a cause)"
    elif before == now_cause:
        verdict = "**same** as the previous red"
    else:
        verdict = (f"🔀 **CHANGED** since the previous red (`{before}`) — a different job, step or error text "
                   "failed this time; read THIS run's annotations, not the earlier diagnosis")
    return f"| cause | `{now_cause}` — {verdict} |"


def _parse_iso(ts: str) -> datetime:
    return datetime.fromisoformat(ts.replace("Z", "+00:00")).astimezone(timezone.utc)


def owns(issue: Issue, label: str, title: str) -> bool:
    """The ownership test: label AND exact title AND the hidden marker in the body AND authored by
    the Actions bot. The first three are public fields anyone can set on any issue; the author is
    the term a human cannot forge, so a marker on a human-authored issue is FOREIGN. A hand-labelled
    issue, a same-titled issue from another writer, a rewritten body, or a copy someone pasted is
    never something this ledger closes."""
    return (label in issue.labels and issue.title == title and LEDGER_MARK in (issue.body or "")
            and issue.author == BOT_LOGIN and issue.author_type == BOT_TYPE)


def validate_control_url(url: str, expected_host: str) -> str | None:
    """None when `url` is the control portal's inbox; otherwise the sentence that names what is wrong.

    Nothing is signed until this says None: an HMAC over the event, sent to an arbitrary URL, is a
    credential handed to whoever owns that host.
    """
    try:
        u = urllib.parse.urlsplit(url or "")
    except ValueError as e:                                     # noqa: BLE001 - reported, not raised
        return f"control-webhook-url {url!r} does not parse ({e})"
    if u.scheme != "https":
        return f"control-webhook-url {url!r} is not https — the signature would travel in clear"
    if u.username or u.password:
        return f"control-webhook-url {url!r} carries userinfo — refused"
    if (u.hostname or "").lower() != (expected_host or "").lower() or not expected_host:
        return (f"control-webhook-url {url!r} points at host {u.hostname!r}, not the control portal "
                f"{expected_host!r} (inputs.control-webhook-host) — refusing to sign an event for it")
    if u.port not in (None, 443):
        return f"control-webhook-url {url!r} uses port {u.port} — the control portal serves 443 only"
    if not u.path.startswith(INBOX_PATH_PREFIX):
        return (f"control-webhook-url {url!r} path {u.path!r} is not under {INBOX_PATH_PREFIX} — the webhook "
                f"inbox lives at /api/hooks/<target> (e.g. /api/hooks/Hosting/PlatformBuilds)")
    return None


def decide(outcome: str, matching: list[Issue], now: datetime, reopen_window_days: int) -> Verdict:
    """The rule table above, over the issues this ledger OWNS (see `owns`)."""
    if outcome not in ("failure", "success"):
        raise Red(f"outcome must be `failure` or `success`, got {outcome!r}")
    open_ones = sorted((i for i in matching if i.state == "open"), key=lambda i: i.number)
    if outcome == "success":
        if open_ones:
            return Verdict("close", open_ones[0].number, "main is green again — closing the open ledger")
        return Verdict("noop", None, "main is green and no ci-failure ledger is open — nothing changed, nothing to tell")
    if open_ones:
        return Verdict("append", open_ones[0].number, "an open ledger exists — appending, never a second open issue")
    window = timedelta(days=reopen_window_days)
    recent = [i for i in matching
              if i.state == "closed" and i.closed_at and now - _parse_iso(i.closed_at) <= window]
    if recent:
        newest = max(recent, key=lambda i: _parse_iso(i.closed_at or ""))
        return Verdict("reopen", newest.number,
                       f"#{newest.number} was closed {now - _parse_iso(newest.closed_at or ''):} ago (inside "
                       f"{reopen_window_days} d) — flapping: reopening it rather than filing a second story")
    return Verdict("create", None, "no ledger is open or recently closed — filing a fresh one")


def never_started(jobs: tuple[dict, ...]) -> tuple[dict, ...]:
    """The failed jobs that never ran a step. Pure."""
    return tuple(j for j in jobs if j.get("neverStarted"))


def failed_job_entry(job: dict) -> dict:
    """One `actions/runs/{id}/jobs` item (already known to have failed) as a ledger job. Pure.

    `neverStarted` is `steps == []` on a job that CONCLUDED failure: GitHub records a step for
    everything a runner executed, so a failed job with none was refused or unresolvable before a
    runner touched it. A `skipped` job also has zero steps, but it never reaches here — it did not
    fail. The step count is the only field the job JSON carries about this; WHY it never started is
    in the annotation, which needs `checks: read` (see NEVER_STARTED_NOTE).
    """
    steps = job.get("steps") or []
    step = next((s.get("name", "") for s in steps if s.get("conclusion") in LISTED_CONCLUSIONS), "")
    return {"name": job.get("name", "?"), "url": job.get("html_url", ""), "step": step,
            "neverStarted": len(steps) == 0}


def ledger_entry(run: Run, now: datetime, previous_body: str = "") -> str:
    stamp = now.strftime("%Y-%m-%dT%H:%M:%SZ")
    if run.failed_jobs:
        jobs = "<br>".join(
            f"[{j['name']}]({j['url']})"
            + (f" — step `{j['step']}`" if j["step"] else "")
            + (f" — errors `{j['cause']}`" if j.get("cause") else "")
            + (f" — ⛔ _{NEVER_STARTED_NOTE}_" if j.get("neverStarted") else "")
            for j in run.failed_jobs)
    else:
        jobs = run.jobs_note or "_(none listed)_"
    rows = [
        f"{ENTRY_HEAD}{stamp} — **{run.outcome}** — [run {run.run_id}]({run.run_url})",
        "",
        "| | |",
        "|---|---|",
        f"| commit | `{run.sha}` |",
        f"| trigger | `{run.trigger}` |",
        f"| platform set | {('`' + run.platform_set + '`') if run.platform_set else '_(none)_'} |",
        f"| failed jobs | {jobs} |",
        cause_row(run, previous_body),
    ]
    refused = never_started(run.failed_jobs)
    if refused:
        # Said ONCE per entry, above the job list, because the job list is what a reader skims.
        # The claim is about VERDICTS, never about the whole run: this ledger lists failed jobs
        # only, so "every failed job never started" is exactly what it can prove — no failing step
        # executed, hence no failing verdict about the commit — and it says nothing about jobs
        # that ran green before a window opened.
        every = len(refused) == len(run.failed_jobs)
        rows.append(
            f"| never started | **{len(refused)} of {len(run.failed_jobs)}** failed job(s) ran no step"
            + (" — EVERY failed job of this run never started, so no failing step executed and this "
               "red carries no verdict about the commit. " if every
               else " — those jobs carry no verdict about the commit; the failed jobs that ran do. ")
            + "Read the annotation on the job page before debugging the diff: `The job was not started "
            "because an Actions budget is preventing further use.` is an org budget refusal (re-run once "
            "the window has passed — Doc/Architecture/ReadingCiSignals); anything else names a workflow "
            "defect. |")
    return "\n".join(rows)


def new_body(repo: str, reopen_window_days: int, title: str = DEFAULT_TITLE) -> str:
    # A lane with its own label (a scheduled lane, #7416) is not "main is red": say which lane.
    if title == MISS_TITLE:
        return "\n".join([
            f"A CI job of **{repo}** could not read an artifact its producer had published — **NOT MEASURED "
            f"(infra)**, never a code finding (Doc/Architecture/ReadingCiSignals → \"An artifact-store miss is NOT "
            f"MEASURED\"). This issue is the ledger: every red run on `main` with such a miss appends a dated entry "
            f"below, and the next green `main` closes it. A miss within {reopen_window_days} days of the close "
            f"REOPENS this issue rather than filing a second one.",
            "",
            "Each failed job links to its log; the miss line names what the store held for that name (nothing, "
            "an upload in flight, a later attempt only, expired, no manifest, or readable now).",
            "",
            LEDGER_MARK,
            "",
            "## Ledger",
        ])
    subject = (f"`main` of **{repo}** is red. This issue is the ledger: every red run on `main` appends a dated "
               if title == DEFAULT_TITLE else
               f"**{title}** — a lane of **{repo}** is red. This issue is the ledger: every red run of the lane "
               f"appends a dated ")
    return "\n".join([
        subject +
        f"entry below, and the run that goes green again closes it. A red within {reopen_window_days} days "
        f"of the close REOPENS this issue rather than filing a second one, so one outage is one story.",
        "",
        "Each entry is also POSTed, signed, to the control portal's triage inbox "
        "(`Hosting/PlatformBuilds` on control.systemorph.com), whose triage agent opens a thread on it. "
        "Read the newest entry first — the failed jobs link straight to their logs.",
        "",
        LEDGER_MARK,
        "",
        "## Ledger",
    ])


_TRIM_RE = re.compile(r"_(\d+) older entr(?:y|ies) trimmed[^_]*_")


def append_entry(body: str, entry: str, max_entries: int = MAX_LEDGER_ENTRIES) -> str:
    """The body with `entry` appended, oldest entries trimmed past `max_entries` (the head stays).

    The trim count is CUMULATIVE across calls, so a long-lived ledger says how much history it has
    shed in total rather than "1 older entry trimmed" on every append past the cap.
    """
    body = (body or "").rstrip()
    if LEDGER_MARK not in body:
        # A body edited by hand into something unrecognisable still gets its ledger back.
        body = body + "\n\n" + LEDGER_MARK
    head, _, tail = body.partition(LEDGER_MARK)
    parts = tail.split("\n" + ENTRY_HEAD)
    lead, old = parts[0], parts[1:]
    if lead.strip().startswith(ENTRY_HEAD):             # an entry glued straight onto the mark
        old, lead = [lead.strip()[len(ENTRY_HEAD):]] + old, ""
    m = _TRIM_RE.search(lead)
    already = int(m.group(1)) if m else 0
    entries = [ENTRY_HEAD + e.strip() for e in old if e.strip()] + [entry.strip()]
    dropped = max(0, len(entries) - max_entries)
    entries = entries[dropped:]
    total = already + dropped
    note = (f"\n\n_{total} older entr{'y' if total == 1 else 'ies'} trimmed — the run list on the Actions tab keeps them._"
            if total else "")
    return head.rstrip() + "\n\n" + LEDGER_MARK + "\n\n## Ledger" + note + "\n\n" + "\n\n".join(entries) + "\n"


def event_body(kind: str, run: Run, issue: Issue) -> str:
    """The EXACT bytes the lane signs and POSTs — one shape per event, field order fixed."""
    if kind == "ci-failure":
        # `neverStarted` rides on every job entry (a boolean, never absent, so a receiver comparing
        # shapes sees one shape) and the run-level count says at a glance whether ANY verdict was
        # reached. A receiver that does not know the fields ignores them (System.Text.Json default).
        obj = {"event": "ci-failure", "repo": run.repo, "sha": run.sha, "run": int(run.run_id),
               "runUrl": run.run_url, "trigger": run.trigger, "failedJobs": list(run.failed_jobs),
               "neverStarted": len(never_started(run.failed_jobs)),
               "platformSet": run.platform_set, "issueUrl": issue.html_url, "issueNumber": issue.number,
               "cause": (lambda c: c if c not in ("unknown", "n/a") else "")(run_cause(run.failed_jobs))}
    elif kind == "ci-green":
        obj = {"event": "ci-green", "repo": run.repo, "sha": run.sha, "run": int(run.run_id),
               "runUrl": run.run_url, "issueNumber": issue.number}
    else:
        raise Red(f"unknown event kind {kind!r}")
    return json.dumps(obj, separators=(",", ":"), ensure_ascii=False)


# ── GitHub, and what a token that cannot write looks like ─────────────────────────────────────

class ApiOutage(Exception):
    """GitHub could not be ASKED — rate-limited, 5xx or unreachable past the wait budget.

    Deliberately NOT a `Red`: every `except Red` in this file degrades or re-words what it catches
    (the taxonomy stamp becomes a warning, a job listing becomes a note in the entry, and the
    message names a permission grant), so an outage raised as `Red` was reported as "the CALLER job
    must grant `actions: read`" on a run whose caller DID grant it (MeshWeaver#7416: run
    40598141020, both `Which red is this` and the red ledger answered `API rate limit exceeded for
    installation`). `main` turns this into exit 3 and a NO VERDICT sentence naming the reset; it is
    never a finding about the lane and never a permission defect."""

    def __init__(self, method: str, path: str, reason: str, reset_epoch: int | None = None):
        super().__init__(reason)
        self.method, self.path, self.reason, self.reset_epoch = method, path, reason, reset_epoch

    def sentence(self) -> str:
        if self.reset_epoch:
            until = datetime.fromtimestamp(self.reset_epoch, tz=timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
            when = f"rate-limited until {until}; re-run this job after it"
        else:
            when = "unavailable; re-run this job once it answers"
        return (f"NO VERDICT — GitHub API {when} ({self.reason}; {self.method} {self.path}). Nothing was "
                f"decided: this is an infrastructure outage, not a missing permission and not a finding "
                f"about the run being reported.")


API_OUTAGE_EXIT = 3
DEFAULT_API_WAIT_MINUTES = 8       # inside the report job's cap; MW_API_OUTAGE_WAIT_MINUTES overrides it
MAX_SINGLE_WAIT_SECONDS = 120


def _header(headers, name: str) -> str:
    value = headers.get(name) if headers is not None else None
    return value.strip() if isinstance(value, str) else ""


def classify_answer(status: int, headers, text: str) -> str:
    """'ok', 'rate-limited', 'transient' or 'refused' for one HTTP answer — the same rule as
    scripts/read-current-pr-body.py (this file is fetched ALONE, so it cannot import that one)."""
    if 200 <= status < 300:
        return "ok"
    if status in (403, 429) and (status == 429 or _header(headers, "x-ratelimit-remaining") == "0"
                                 or _header(headers, "retry-after") or "rate limit" in text.lower()):
        return "rate-limited"
    if status >= 500:
        return "transient"
    return "refused"


def outage_wait(attempt: int, headers, now: float) -> tuple[int, int | None]:
    """Seconds to wait before the next try, and the reset epoch the answer named (if any)."""
    retry_after = _header(headers, "retry-after")
    if retry_after.isdigit():
        return int(retry_after), None
    reset = _header(headers, "x-ratelimit-reset")
    if _header(headers, "x-ratelimit-remaining") == "0" and reset.isdigit():
        return max(1, int(reset) - int(now) + 1), int(reset)
    return min(MAX_SINGLE_WAIT_SECONDS, 2 ** (attempt + 1)), None


def _wait_budget_seconds() -> int:
    raw = os.environ.get("MW_API_OUTAGE_WAIT_MINUTES", "")
    return (int(raw) if raw.isdigit() else DEFAULT_API_WAIT_MINUTES) * 60


class GitHub:
    def __init__(self, repo: str, token: str, *, urlopen=None, sleep=time.sleep, clock=time.time,
                 wait_budget_seconds: int | None = None):
        self.repo, self.token = repo, token
        self._urlopen = urlopen or urllib.request.urlopen
        self._sleep, self._clock = sleep, clock
        # ONE budget for the whole script, never one per call: N reads cannot add up to N waits.
        budget = _wait_budget_seconds() if wait_budget_seconds is None else wait_budget_seconds
        self._deadline = clock() + budget

    def call(self, method: str, path: str, params: dict | None = None, body: dict | None = None,
             ok=(200, 201)) -> tuple[int, object]:
        url = f"{API}/repos/{self.repo}/{path}"
        if params:
            url += "?" + urllib.parse.urlencode(params)
        data = json.dumps(body).encode() if body is not None else None
        attempt = 0
        while True:
            req = urllib.request.Request(url, data=data, method=method, headers={
                "Authorization": f"Bearer {self.token}", "Accept": "application/vnd.github+json",
                "X-GitHub-Api-Version": "2022-11-28", "Content-Type": "application/json",
                "User-Agent": "ci-failure-ledger"})
            headers = None
            try:
                with self._urlopen(req, timeout=30) as resp:
                    status, text = resp.status, resp.read().decode()
                kind = classify_answer(status, None, text)
            except urllib.error.HTTPError as e:
                headers = e.headers
                try:
                    status, text = e.code, e.read().decode(errors="replace")
                    kind = classify_answer(status, headers, text)
                except (TimeoutError, OSError) as unread:
                    # A 403 is told apart from a rate limit partly by its BODY: unread, it is an
                    # outage to retry, never a permission verdict taken on a guess.
                    status, text = e.code, f"(error body unreadable: {type(unread).__name__})"
                    kind = "transient"
            except (urllib.error.URLError, TimeoutError, OSError) as e:
                status, kind = 0, "transient"
                text = f"could not reach api.github.com ({getattr(e, 'reason', e)})"
            if kind in ("ok", "refused"):
                break
            reason = (f"HTTP {status} rate limit" if kind == "rate-limited"
                      else f"HTTP {status}" if status else text)
            # A WRITE that met a 5xx or a dropped connection may have been applied: replaying it could
            # file a second issue or comment, so it is not retried. A rate-limited write was refused
            # before it ran, so it is.
            if kind == "transient" and method != "GET":
                raise ApiOutage(method, path, reason + " — a write is not replayed blind")
            delay, reset_epoch = outage_wait(attempt, headers, self._clock())
            if self._clock() + delay > self._deadline:
                # A wait the budget cannot cover is not slept at all: say when to re-run instead.
                raise ApiOutage(method, path, reason, reset_epoch)
            print(f"GitHub API {reason} on {method} {path}; retrying in {delay}s "
                  f"(wait budget {int(self._deadline - self._clock())}s left)", file=sys.stderr)
            self._sleep(delay)
            attempt += 1
        if status == 403 or status == 401:
            raise Red(f"{method} {path} answered HTTP {status}. The token this lane was handed cannot do that: "
                      f"the CALLER job must grant `permissions: {{issues: write, contents: read, actions: read}}` "
                      f"and pass `secrets: github-token: ${{{{ secrets.GITHUB_TOKEN }}}}` — a called workflow can "
                      f"only narrow what its caller granted, never widen it. Body: {text[:300]}")
        if status not in ok:
            raise Red(f"{method} {path} answered HTTP {status}: {text[:300]}")
        return status, (json.loads(text) if text.strip() else None)

    # -- the reads and writes the ledger needs, each one call --
    def ensure_label(self, label: str) -> None:
        status, _ = self.call("POST", "labels", body={
            "name": label, "color": "B60205",
            "description": LABEL_DESCRIPTIONS.get(label, DEFAULT_LABEL_DESCRIPTION)}, ok=(201, 422))
        # 422 = already exists, which is the idempotent path; 201 = created now.
        _ = status

    def stamp_taxonomy(self, number: int, current: tuple[str, ...], severity: str = DEFAULT_SEVERITY) -> str:
        """Give the ledger issue its TYPE and SEVERITY (`taxonomy(severity)`) when it carries neither yet;
        create a missing taxonomy label with core's canonical look first. Returns what it did, in words."""
        if not needs_taxonomy(current):
            return f"#{number} already classified — kept"
        wanted = taxonomy(severity)
        for name in wanted:
            status, _ = self.call("GET", f"labels/{urllib.parse.quote(name, safe='')}", ok=(200, 404))
            if status == 404:
                color, description = TAXONOMY_LOOK[name]
                self.call("POST", "labels", body={"name": name, "color": color, "description": description}, ok=(201, 422))
        self.call("POST", f"issues/{number}/labels", body={"labels": list(wanted)}, ok=(200,))
        return f"#{number} labelled {', '.join(wanted)}"

    def labelled_issues(self, label: str) -> list[Issue]:
        """Every issue (never a pull request) carrying `label`, any state — ownership is judged by `owns`."""
        out: list[Issue] = []
        for page in range(1, 6):
            _, items = self.call("GET", "issues", {"labels": label, "state": "all", "per_page": 100,
                                                   "page": page, "sort": "updated", "direction": "desc"})
            items = items or []
            for it in items:
                if "pull_request" in it:
                    continue
                user = it.get("user") or {}
                out.append(Issue(it["number"], it["title"], it["state"], it.get("body") or "", it["html_url"],
                                 tuple(l["name"] for l in it.get("labels", [])), it.get("closed_at"),
                                 str(user.get("login") or ""), str(user.get("type") or "")))
            if len(items) < 100:
                break
        return out

    def create_issue(self, title: str, label: str, body: str) -> Issue:
        if label == FORBIDDEN_LABEL or LEDGER_MARK not in body:
            raise Red(f"refusing to file a ledger issue under `{label}` / without its ownership mark — that is CD's label, and an unmarked issue is one this ledger could never prove it opened")
        _, it = self.call("POST", "issues", body={"title": title, "labels": [label], "body": body})
        user = it.get("user") or {}
        issue = Issue(it["number"], it["title"], it["state"], it.get("body") or "", it["html_url"],
                      tuple(l["name"] for l in it.get("labels", [])), it.get("closed_at"),
                      str(user.get("login") or ""), str(user.get("type") or ""))
        if not (issue.author == BOT_LOGIN and issue.author_type == BOT_TYPE):
            raise Red(f"the ledger issue #{issue.number} was created as {issue.author!r} ({issue.author_type!r}), "
                      f"not as {BOT_LOGIN!r} — the token is not the caller's GITHUB_TOKEN, so this ledger could "
                      f"never own the issue it just filed and would file a fresh one every run. Pass "
                      f"`secrets: github-token:` = secrets.GITHUB_TOKEN and nothing else.")
        return issue

    def update_issue(self, number: int, **fields) -> Issue:
        _, it = self.call("PATCH", f"issues/{number}", body=fields)
        return Issue(it["number"], it["title"], it["state"], it.get("body") or "", it["html_url"])

    def comment(self, number: int, body: str) -> None:
        self.call("POST", f"issues/{number}/comments", body={"body": body})

    def failed_jobs_of_run(self, run_id: str) -> tuple[dict, ...]:
        """{name, url, step, neverStarted} for every job of the run that concluded failure/timed_out."""
        found: list[dict] = []
        for page in range(1, 11):
            _, data = self.call("GET", f"actions/runs/{run_id}/jobs", {"per_page": 100, "page": page})
            jobs = (data or {}).get("jobs", [])
            for j in jobs:
                if j.get("conclusion") not in FAILED_CONCLUSIONS:
                    continue
                found.append(failed_job_entry(j))
            if len(jobs) < 100:
                break
        return tuple(found)

    def annotations(self, check_run_url: str) -> list[dict]:
        """Every annotation of one job's check run. Needs `checks: read` on the CALLER's job."""
        prefix = f"{API}/repos/{self.repo}/"
        if not check_run_url.startswith(prefix):
            raise Red(f"check_run_url {check_run_url!r} is not under {prefix}")
        out: list[dict] = []
        for page in range(1, 11):
            _, data = self.call("GET", check_run_url[len(prefix):] + "/annotations", {"per_page": 50, "page": page})
            data = data or []
            out.extend(data)
            if len(data) < 50:
                break
        return out

    def failed_jobs_with_causes(self, run_id: str) -> tuple[dict, ...]:
        """`failed_jobs_of_run`, widened to a job cut at its timeout (`cancelled`), each with its
        error-annotation `cause` signature. An unreadable listing or annotation is RED, never a
        job without a cause — a signature computed over nothing would read as "same cause"."""
        found: list[dict] = []
        for page in range(1, 11):
            _, data = self.call("GET", f"actions/runs/{run_id}/jobs", {"per_page": 100, "page": page})
            jobs = (data or {}).get("jobs", [])
            for j in jobs:
                if j.get("conclusion") not in LISTED_CONCLUSIONS:
                    continue
                entry = failed_job_entry(j)
                url = j.get("check_run_url") or ""
                if not url:
                    raise Red(f"job {j.get('name')!r} carries no check_run_url — its annotations cannot be read")
                notes = self.annotations(url)
                entry["cause"] = cause_signature([a.get("message", "") for a in notes
                                                  if a.get("annotation_level") == "failure"])
                found.append(entry)
            if len(jobs) < 100:
                break
        return tuple(found)

    def red_jobs_of_run(self, run_id: str) -> tuple[dict, ...]:
        """The raw `actions/runs/{id}/jobs` items that concluded failure/timed_out/cancelled."""
        found: list[dict] = []
        for page in range(1, 11):
            _, data = self.call("GET", f"actions/runs/{run_id}/jobs", {"per_page": 100, "page": page})
            jobs = (data or {}).get("jobs", [])
            found.extend(j for j in jobs if j.get("conclusion") in LISTED_CONCLUSIONS)
            if len(jobs) < 100:
                break
        return tuple(found)

    def job_log(self, job_id: int) -> str | None:
        """One job's log text, or None when GitHub holds no log for it (404/410: a runner that died
        before uploading one). Needs `actions: read`, which this lane already demands.

        The API answers 302 to signed blob storage; the redirect is followed WITHOUT the token (a
        bearer must never travel to another origin), which is why the opener here does not follow
        redirects itself. Anything else that is not a log is RED or an ApiOutage, never "no miss"."""
        path = f"actions/jobs/{job_id}/logs"

        class _Stop(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                return None

        req = urllib.request.Request(f"{API}/repos/{self.repo}/{path}", headers={
            "Authorization": f"Bearer {self.token}", "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "ci-failure-ledger"})
        try:
            try:
                with urllib.request.build_opener(_Stop).open(req, timeout=60) as resp:
                    data = resp.read(MAX_LOG_BYTES + 1)
            except urllib.error.HTTPError as e:
                if e.code in (404, 410):
                    return None
                location = e.headers.get("Location") if e.code in (301, 302, 303, 307, 308) else None
                if not location:
                    text = e.read().decode(errors="replace")
                    if classify_answer(e.code, e.headers, text) in ("rate-limited", "transient"):
                        raise ApiOutage("GET", path, f"HTTP {e.code}") from e
                    raise Red(f"GET {path} answered HTTP {e.code}: {text[:200]}") from e
                with self._urlopen(urllib.request.Request(location, headers={"User-Agent": "ci-failure-ledger"}),
                                   timeout=60) as resp:
                    data = resp.read(MAX_LOG_BYTES + 1)
        except urllib.error.HTTPError as e:
            raise Red(f"the log of job {job_id} could not be downloaded from storage: HTTP {e.code}") from e
        except (urllib.error.URLError, TimeoutError, OSError) as e:
            raise ApiOutage("GET", path, f"could not read the log ({getattr(e, 'reason', e)})") from e
        if len(data) > MAX_LOG_BYTES:
            raise Red(f"the log of job {job_id} is over {MAX_LOG_BYTES} bytes — refusing a truncated read, "
                      "which could only produce a false 'no miss'")
        return data.decode("utf-8", errors="replace")


# ── apply the verdict ─────────────────────────────────────────────────────────────────────────

def owned_issues(gh, label: str, title: str) -> tuple[list[Issue], list[Issue]]:
    """(the issues this ledger owns, the same-label issues it does NOT and will never touch)."""
    # One entry per issue NUMBER. The listing is several pages sorted by `updated`, so an issue
    # updated WHILE it is read can move pages and appear twice; two copies of #N would make the
    # fold treat #N as its own duplicate and close the only ledger (Copilot on #4434).
    seen = list({i.number: i for i in gh.labelled_issues(label)}.values())
    mine = [i for i in seen if owns(i, label, title)]
    foreign = [i for i in seen if not owns(i, label, title)]
    return mine, foreign


def _owned(gh, label: str, title: str, number: int) -> Issue:
    """The issue, re-read and re-proven ours right before a write — never a number trusted from earlier."""
    mine, _ = owned_issues(gh, label, title)
    for i in mine:
        if i.number == number:
            return i
    raise Red(f"#{number} is not (or no longer) this ledger's issue — it lacks the `{label}` label, the exact "
              f"title or the `{LEDGER_MARK}` mark. A mechanism may only close an issue it opened; nothing written.")


def entries_of(body: str) -> list[str]:
    """Every ledger entry in `body`, oldest first, each starting with ENTRY_HEAD — the inverse of
    `append_entry`'s layout (the head, the mark, `## Ledger`, an optional trim note, the entries)."""
    _, mark, tail = (body or "").partition(LEDGER_MARK)
    if not mark:
        return []
    parts = tail.split("\n" + ENTRY_HEAD)
    lead, rest = parts[0], parts[1:]
    if lead.strip().startswith(ENTRY_HEAD):
        rest = [lead.strip()[len(ENTRY_HEAD):]] + rest
    return [ENTRY_HEAD + e.strip() for e in rest if e.strip()]


def fold_duplicates(gh, matching: list[Issue], title: str, label: str) -> tuple[list[Issue], list[int]]:
    """🚨 ONE open ledger, always — even after the listing lied once.

    `decide` never files a second open issue while it can SEE the first, but it can only see what
    the label listing returns, and that listing is not a transaction: on 2026-09-15 at 14:32Z the
    combo-verify ledger read "0 owned issue(s), 0 foreign" for `ci-combo-unverified` while #4413
    had carried that label, the exact title and the mark since 10:31Z (never closed), and filed
    #4431 beside it; the next run found #4413 again. A retry would only make that rarer. Instead
    the duplicate cannot SURVIVE: whenever this ledger owns more than one OPEN issue, every newer
    one is folded into the oldest — its entries appended there, a comment on each side naming the
    other, the newer one closed as not planned — before the verdict is taken. Both issues are
    re-proven ours at the moment of each write, like every other write here.

    KNOWN LIMIT, not new with the fold: two ledger runs can still interleave a read-then-write on
    the SAME issue — the append path always could — because the Issues API has no compare-and-swap
    on a body. The fold never sets the keeper's state, so it cannot re-open a ledger a green run
    just closed; at worst one entry is lost, which the Actions tab still lists. A `concurrency:`
    group would serialize writers but CANCELS an older pending run, i.e. drops a whole entry, so
    it is not the fix either.

    Returns (the owned issues as they stand after folding, the numbers folded away)."""
    open_ones = sorted((i for i in matching if i.state == "open"), key=lambda i: i.number)
    if len(open_ones) < 2:
        return matching, []
    keep = open_ones[0].number
    folded: list[int] = []
    for dup in open_ones[1:]:
        current_dup = _owned(gh, label, title, dup.number)
        current_keep = _owned(gh, label, title, keep)
        body = current_keep.body
        moved = entries_of(current_dup.body)
        for entry in moved:
            body = append_entry(body, entry)
        gh.update_issue(keep, body=body)
        gh.comment(keep, f"Folded in from #{dup.number}, a second open ledger for this same check "
                         f"(the label listing once returned no issue while this one was open). "
                         f"Its {len(moved)} entr{'y' if len(moved) == 1 else 'ies'} now sit(s) in the ledger above.")
        gh.comment(dup.number, f"Duplicate of #{keep}, the open ledger for this same check — its entries "
                               f"are folded in there. Closing: one outage is one story.")
        gh.update_issue(dup.number, state="closed", state_reason="not_planned")
        folded.append(dup.number)
    mine, _ = owned_issues(gh, label, title)
    return mine, folded


def apply(gh, run: Run, verdict: Verdict, title: str, label: str, now: datetime,
          reopen_window_days: int, severity: str = DEFAULT_SEVERITY) -> tuple[Issue | None, str | None]:
    """Perform the verdict. Returns (the issue touched, the event body to POST or None)."""
    if verdict.action == "noop":
        return None, None
    if verdict.action == "close":
        assert verdict.issue is not None
        _owned(gh, label, title, verdict.issue)          # re-proven at the moment of the write
        gh.comment(verdict.issue, f"green again: {run.run_url}\n\n"
                                  f"Commit `{run.sha}`, trigger `{run.trigger}`. Closing; a red within "
                                  f"{reopen_window_days} days reopens this issue rather than filing a new one.")
        issue = gh.update_issue(verdict.issue, state="closed", state_reason="completed")
        return issue, event_body("ci-green", run, issue)
    gh.ensure_label(label)
    if verdict.action == "create":
        entry = ledger_entry(run, now)
        issue = gh.create_issue(title, label, append_entry(new_body(run.repo, reopen_window_days, title), entry))
    elif verdict.action == "append":
        assert verdict.issue is not None
        current = _owned(gh, label, title, verdict.issue)
        issue = gh.update_issue(verdict.issue, body=append_entry(current.body, ledger_entry(run, now, current.body)))
    elif verdict.action == "reopen":
        assert verdict.issue is not None
        current = _owned(gh, label, title, verdict.issue)
        issue = gh.update_issue(verdict.issue, state="open",
                                body=append_entry(current.body, ledger_entry(run, now, current.body)))
    else:
        raise Red(f"unknown verdict {verdict.action!r}")
    # 🏷️ Type + severity on the issue it filed, reopened or appended to. Decoration on the ledger, never
    # the ledger: a label call that fails is SAID and the event still goes to triage — whose sweep, and
    # core's daily issue-taxonomy lane, name an issue left untyped.
    labels_now = issue.labels if verdict.action == "create" else current.labels
    try:
        print(f"taxonomy: {gh.stamp_taxonomy(issue.number, labels_now, severity)}")
    except (Red, ApiOutage) as e:
        print(f"::warning title=ci-failure-ledger::could not stamp {', '.join(taxonomy(severity))} on #{issue.number} ({e}) — the issue-taxonomy lane will name it")
    return issue, event_body("ci-failure", run, issue)


def printed_a_store_miss(log: str) -> bool:
    """True when a job log carries the miss ci-run-artifacts.py EMITS. Pure."""
    return any(MISS_LINE.search(line) for line in log.splitlines())


def store_miss_jobs(gh, run_id: str) -> tuple[tuple[dict, ...], tuple[str, ...]]:
    """(the run's red jobs that printed the miss, as ledger jobs; the red jobs GitHub holds no log for).

    A log that cannot be read is RED (or an ApiOutage), never "this job did not miss" — the same rule
    as retry-known-transients.py's #4534 guard. A job with NO log held (a runner that died before
    uploading one) cannot have its miss read; it is NAMED, never silently counted as clean."""
    misses: list[dict] = []
    no_log: list[str] = []
    for job in gh.red_jobs_of_run(run_id):
        log = gh.job_log(job["id"])
        if log is None:
            no_log.append(str(job.get("name", "?")))
        elif printed_a_store_miss(log):
            misses.append(failed_job_entry(job))
    return tuple(misses), tuple(no_log)


def artifact_miss_ledger(gh, a, now: datetime) -> tuple[Verdict, Issue | None]:
    """The `--artifact-misses` mode over an argparse namespace (repo, outcome, run_id, run_url, sha,
    trigger, label, reopen_window_days). Returns the verdict it applied. Never POSTs an event: the main
    build ledger already told triage about this run."""
    if a.label != DEFAULT_LABEL:
        print(f"the artifact-store miss ledger rides only on a repository's MAIN BUILD ledger (label "
              f"`{DEFAULT_LABEL}`); this call reports the `{a.label}` lane — nothing read, nothing written")
        return Verdict("noop", None, "not the main build lane"), None
    if a.outcome == "failure":
        misses, no_log = store_miss_jobs(gh, a.run_id)
        for name in no_log:
            print(f"::warning title=ci-failure-ledger::job {name!r} is red and GitHub holds NO log for it — "
                  "whether it missed an artifact is NOT MEASURED (its annotation names why it has no log)")
        if not misses:
            print(f"no red job of run {a.run_id} printed `ci-run-artifacts: NOT MEASURED — artifact store miss "
                  f"(infra)` ({len(no_log)} without a log, named above) — this red is about something else and "
                  "says nothing about the store, so the miss ledger is left as it is")
            return Verdict("noop", None, "no store miss in this red"), None
        print(f"{len(misses)} red job(s) printed the store miss: " + ", ".join(j["name"] for j in misses))
        run = Run(a.repo, "failure", str(a.run_id), a.run_url, a.sha, a.trigger, misses, "")
    else:
        run = Run(a.repo, "success", str(a.run_id), a.run_url, a.sha, a.trigger, (), "")
    matching, foreign = owned_issues(gh, MISS_LABEL, MISS_TITLE)
    for f in foreign:
        print(f"leaving #{f.number} alone ({f.state}, {f.title!r}): it carries `{MISS_LABEL}` but this ledger did "
              f"not open it, so this mechanism will not touch it")
    matching, folded = fold_duplicates(gh, matching, MISS_TITLE, MISS_LABEL)
    for n in folded:
        print(f"FOLDED  #{n} — a second open miss ledger; its entries now sit in the oldest open one")
    verdict = decide(run.outcome, matching, now, a.reopen_window_days)
    if verdict.action == "close":
        verdict = Verdict("close", verdict.issue, "main is green again — every artifact it asked for was read; closing the miss ledger")
    elif verdict.action == "noop":
        verdict = Verdict("noop", None, "main is green and no miss ledger is open — nothing changed")
    print(f"{verdict.action.upper():7} {verdict.reason} (label `{MISS_LABEL}`, title {MISS_TITLE!r}, "
          f"{len(matching)} owned issue(s), {len(foreign)} foreign)")
    issue, _ = apply(gh, run, verdict, MISS_TITLE, MISS_LABEL, now, a.reopen_window_days, MISS_SEVERITY)
    if issue is not None:
        print(f"issue #{issue.number} {issue.state}: {issue.html_url}")
    return verdict, issue


def _emit_outputs(path: str | None, **kv) -> None:
    if not path:
        return
    with open(path, "a", encoding="utf-8") as fh:
        for k, v in kv.items():
            fh.write(f"{k}={v}\n")


def main(argv: list[str]) -> int:
    env = os.environ.get
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--repo", default=env("GITHUB_REPOSITORY"))
    ap.add_argument("--outcome", default=env("LEDGER_OUTCOME"))
    ap.add_argument("--run-id", default=env("LEDGER_RUN_ID"))
    ap.add_argument("--run-url", default=env("LEDGER_RUN_URL"))
    ap.add_argument("--sha", default=env("LEDGER_SHA"))
    ap.add_argument("--trigger", default=env("LEDGER_TRIGGER"))
    ap.add_argument("--failed-jobs", default=env("LEDGER_FAILED_JOBS", "[]"))
    ap.add_argument("--platform-set", default=env("LEDGER_PLATFORM_SET", ""))
    ap.add_argument("--title", default=env("LEDGER_ISSUE_TITLE") or DEFAULT_TITLE)
    ap.add_argument("--label", default=env("LEDGER_LABEL") or DEFAULT_LABEL)
    ap.add_argument("--severity", default=env("LEDGER_SEVERITY") or DEFAULT_SEVERITY,
                    help=f"the ledger issue's severity label (default {DEFAULT_SEVERITY}); one of {', '.join(SEVERITIES)}")
    ap.add_argument("--reopen-window-days", type=int, default=int(env("LEDGER_REOPEN_WINDOW_DAYS") or DEFAULT_REOPEN_WINDOW_DAYS))
    ap.add_argument("--event-out", default=env("LEDGER_EVENT_OUT"), help="file the exact event body is written to")
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--check-url", help="validate this control-webhook-url and exit; nothing else runs")
    ap.add_argument("--expected-host", default=env("LEDGER_CONTROL_HOST") or "control.systemorph.com")
    ap.add_argument("--list-failed-jobs", metavar="RUN_ID",
                    help="print (and write to GITHUB_OUTPUT as `failed-jobs`) the run's failed jobs WITH their "
                         "error-annotation cause signatures, for a caller to pass as the lane's `failed-jobs` "
                         "input. Runs in the CALLER's job, which grants `actions: read` + `checks: read`.")
    ap.add_argument("--artifact-misses", action="store_true",
                    help="keep the artifact-store MISS ledger (label `ci-artifact-store-miss`) for this run "
                         "instead of the main one: a red run whose red jobs printed the miss appends, a green "
                         "one closes. Acts only for the main build lane's label; posts no event.")
    a = ap.parse_args(argv)

    if a.self_test:
        return self_test()
    if a.artifact_misses:
        try:
            token = env("GH_TOKEN") or env("GITHUB_TOKEN") or ""
            if not token:
                raise Red("no GH_TOKEN — refusing to report 'no miss' on no access")
            for name, value in (("repo", a.repo), ("outcome", a.outcome), ("run-id", a.run_id),
                                ("run-url", a.run_url), ("sha", a.sha), ("trigger", a.trigger)):
                if not value:
                    raise Red(f"--{name} is empty — the lane's inputs are incomplete")
            if a.outcome not in ("failure", "success"):
                raise Red(f"outcome must be `failure` or `success`, got {a.outcome!r}")
            verdict, issue = artifact_miss_ledger(GitHub(a.repo, token), a, datetime.now(timezone.utc))
            _emit_outputs(env("GITHUB_OUTPUT"), **{"miss-action": verdict.action,
                                                   "miss-issue-url": issue.html_url if issue else ""})
            return 0
        except ApiOutage as e:
            print(f"::error title=NO VERDICT — GitHub API::{e.sentence()}", file=sys.stderr)
            return API_OUTAGE_EXIT
        except Red as e:
            print(f"::error title=ci-failure-ledger (artifact-store misses)::{e}", file=sys.stderr)
            return 1
    if a.list_failed_jobs is not None:
        try:
            token = env("GH_TOKEN") or env("GITHUB_TOKEN") or ""
            if not token or not a.repo or not a.list_failed_jobs.strip():
                raise Red("--list-failed-jobs needs GH_TOKEN, GITHUB_REPOSITORY (or --repo) and a run id")
            jobs = GitHub(a.repo, token).failed_jobs_with_causes(a.list_failed_jobs.strip())
        except ApiOutage as e:
            print(f"::error title=NO VERDICT — GitHub API::could not list run {a.list_failed_jobs}'s failed jobs: "
                  f"{e.sentence()} The grants are NOT in question; the ledger lists the run's jobs itself.",
                  file=sys.stderr)
            return API_OUTAGE_EXIT
        except Red as e:
            print(f"::error title=ci-failure-ledger::could not list run {a.list_failed_jobs}'s failed jobs with their "
                  f"causes — the job running this must grant `actions: read` and `checks: read`: {e}", file=sys.stderr)
            return 1
        listed = json.dumps(list(jobs), separators=(",", ":"), ensure_ascii=False)
        parse_failed_jobs(listed)            # what the lane will accept, proven before it is handed over
        print(f"{len(jobs)} failed job(s), run cause `{run_cause(jobs)}`:")
        for j in jobs:
            print(f"  {j['name']} — step {j['step'] or '(none)'} — errors {j['cause']}")
        _emit_outputs(env("GITHUB_OUTPUT"), **{"failed-jobs": listed})
        return 0
    if a.check_url is not None:
        problem = validate_control_url(a.check_url, a.expected_host)
        if problem:
            print(f"::error title=ci-failure-ledger::{problem}", file=sys.stderr)
            return 1
        print(f"control-webhook-url is the control portal's inbox ({a.expected_host}, under {INBOX_PATH_PREFIX}) — safe to sign for")
        return 0

    try:
        token = env("GH_TOKEN") or env("GITHUB_TOKEN") or ""
        if not token:
            raise Red("no GH_TOKEN — the caller must forward its secrets.GITHUB_TOKEN as `secrets: github-token:`; refusing to report 'nothing to do' on no access")
        for name, value in (("repo", a.repo), ("outcome", a.outcome), ("run-id", a.run_id),
                            ("run-url", a.run_url), ("sha", a.sha), ("trigger", a.trigger)):
            if not value:
                raise Red(f"--{name} is empty — the lane's inputs are incomplete")
        failed_jobs = parse_failed_jobs(a.failed_jobs)
        taxonomy(a.severity)                 # refused BEFORE any write, like a malformed failed-jobs
        now = datetime.now(timezone.utc)
        gh = GitHub(a.repo, token)

        jobs_note = ""
        listing_error: Red | ApiOutage | None = None
        if a.outcome == "failure" and not failed_jobs:
            try:
                failed_jobs = gh.failed_jobs_of_run(a.run_id)
            except Red as e:
                # Recorded in the entry and then RED below: the ledger stays intact, the mis-grant is named.
                listing_error = e
                jobs_note = f"_(could not list this run's jobs — {e})_"
            except ApiOutage as e:
                # The red is still FILED (the entry says the listing was not measured), then NO VERDICT below.
                listing_error = e
                jobs_note = f"_(this run's jobs were not listed — {e.sentence()})_"
        run = Run(a.repo, a.outcome, str(a.run_id), a.run_url, a.sha, a.trigger, failed_jobs,
                  a.platform_set or "", jobs_note)

        if a.label == FORBIDDEN_LABEL:
            raise Red(f"the ledger label must never be `{FORBIDDEN_LABEL}` — that is main-cd.yml's delivery alert, and sharing it lets a CD heal close this ledger while CI is red")
        matching, foreign = owned_issues(gh, a.label, a.title)
        for f in foreign:
            print(f"leaving #{f.number} alone ({f.state}, {f.title!r}): it carries `{a.label}` but not this ledger's "
                  f"title and `{LEDGER_MARK}` mark, so this mechanism did not open it and will not touch it")
        matching, folded = fold_duplicates(gh, matching, a.title, a.label)
        for n in folded:
            print(f"FOLDED  #{n} — a second open ledger for this check; its entries now sit in the oldest open one")
        verdict = decide(a.outcome, matching, now, a.reopen_window_days)
        print(f"{verdict.action.upper():7} {verdict.reason} (label `{a.label}`, title {a.title!r}, "
              f"{len(matching)} owned issue(s), {len(foreign)} foreign)")
        issue, body = apply(gh, run, verdict, a.title, a.label, now, a.reopen_window_days, a.severity)
        if issue is not None:
            print(f"issue #{issue.number} {issue.state}: {issue.html_url}")
        if body is not None and a.event_out:
            with open(a.event_out, "w", encoding="utf-8") as fh:
                fh.write(body)
        _emit_outputs(env("GITHUB_OUTPUT"), action=verdict.action,
                      **{"issue-number": issue.number if issue else "", "issue-url": issue.html_url if issue else "",
                         "post": "true" if body is not None else "false"})
        if isinstance(listing_error, ApiOutage):
            raise listing_error
        if listing_error is not None:
            raise Red(f"the ledger entry is written, but this run's failed jobs could not be listed: {listing_error}")
        return 0
    except ApiOutage as e:
        print(f"::error title=NO VERDICT — GitHub API::{e.sentence()}", file=sys.stderr)
        return API_OUTAGE_EXIT
    except Red as e:
        print(f"::error title=ci-failure-ledger::{e}", file=sys.stderr)
        return 1


# ── self-test: prove every rule against a fake GitHub ─────────────────────────────────────────

class _Fake:
    """Enough of GitHub to run `apply` end to end and count every write."""

    def __init__(self, issues: list[Issue] | None = None, run_jobs: list[dict] | None = None):
        self.issues = {i.number: i for i in (issues or [])}
        self.next_number = max(self.issues, default=100) + 1
        self.comments: list[tuple[int, str]] = []
        self.writes = 0
        self.labels: set[str] = set()
        self.run_jobs = run_jobs or []

    def ensure_label(self, label):
        self.writes += 1
        self.labels.add(label)

    def labelled_issues(self, label):
        return [i for i in self.issues.values() if label in i.labels]

    def create_issue(self, title, label, body):
        assert label != FORBIDDEN_LABEL and LEDGER_MARK in body, "the fake mirrors the real refusal"
        self.writes += 1
        n = self.next_number
        self.next_number += 1
        self.issues[n] = Issue(n, title, "open", body, f"https://github.com/o/r/issues/{n}", (label,),
                               None, BOT_LOGIN, BOT_TYPE)
        return self.issues[n]

    def update_issue(self, number, **fields):
        self.writes += 1
        i = self.issues[number]
        for k, v in fields.items():
            if k in ("state", "body"):
                setattr(i, k, v)
        return i

    def comment(self, number, body):
        self.writes += 1
        self.comments.append((number, body))

    def stamp_taxonomy(self, number, current, severity=DEFAULT_SEVERITY):
        i = self.issues[number]
        if not needs_taxonomy(tuple(i.labels)):
            return "kept"
        self.writes += 1
        i.labels = tuple(i.labels) + taxonomy(severity)
        return "labelled"

    def failed_jobs_of_run(self, run_id):
        # The SAME transcription the real client uses, so the self-test proves the rule that runs.
        return tuple(failed_job_entry(j) for j in self.run_jobs if j.get("conclusion") in FAILED_CONCLUSIONS)

    def red_jobs_of_run(self, run_id):
        self.reads = getattr(self, "reads", 0) + 1
        return tuple(j for j in self.run_jobs if j.get("conclusion") in LISTED_CONCLUSIONS)

    def job_log(self, job_id):
        # `logs` maps a job id to its text, to None (GitHub holds no log), or to an exception to raise.
        self.reads = getattr(self, "reads", 0) + 1
        value = getattr(self, "logs", {}).get(job_id)
        if isinstance(value, Exception):
            raise value
        return value


def self_test() -> int:
    now = datetime(2026, 9, 12, 10, 0, tzinfo=timezone.utc)
    title, label = DEFAULT_TITLE, DEFAULT_LABEL
    fails: list[str] = []

    def check(name: str, cond: bool, detail: str = "") -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {name}" + (f" — {detail}" if detail and not cond else ""))
        if not cond:
            fails.append(name)

    def run(outcome, failed=(), note=""):
        return Run("o/r", outcome, "555", "https://github.com/o/r/actions/runs/555", "abc123", "schedule",
                   tuple(failed), "3.0.0-ci.4711", note)

    def go(gh, r):
        v = decide(r.outcome, owned_issues(gh, label, title)[0], now, 7)
        issue, body = apply(gh, r, v, title, label, now, 7)
        return v, issue, body

    job = {"name": "Portal hosts (shard 0)", "url": "https://github.com/o/r/actions/runs/555/job/1", "step": "Run tests",
           "neverStarted": False}

    # 1. no issue → create, one entry, a ci-failure event naming the issue
    gh = _Fake()
    v, issue, body = go(gh, run("failure", [job]))
    check("no issue -> create", v.action == "create" and len(gh.issues) == 1 and issue is not None)
    check("created body carries the ledger mark and ONE entry",
          issue is not None and LEDGER_MARK in issue.body and issue.body.count("\n" + ENTRY_HEAD) == 1)
    check("the ledger's issue carries `ci-main-red` and NEVER `ci-failure` (CD's label)",
          issue is not None and label in issue.labels and FORBIDDEN_LABEL not in issue.labels and label != FORBIDDEN_LABEL)
    ev = json.loads(body or "{}")
    check("ci-failure event names repo, run, issue and the failed job",
          ev.get("event") == "ci-failure" and ev.get("run") == 555 and ev.get("issueNumber") == issue.number
          and ev.get("failedJobs") == [job] and ev.get("platformSet") == "3.0.0-ci.4711" and ev.get("trigger") == "schedule"
          and ev.get("neverStarted") == 0)
    check("a job that ran its steps is not marked never-started in the entry",
          issue is not None and "never started" not in issue.body)
    check("event bytes are compact and field-ordered", (body or "").startswith('{"event":"ci-failure","repo":"o/r","sha":"abc123","run":555,'))
    check("🏷️ the ledger it filed carries its type AND severity (bug, sev:H) — never invisible to the release gate",
          issue is not None and all(t in gh.issues[issue.number].labels for t in TAXONOMY))
    typed = _Fake([Issue(77, title, "open", new_body("o/r", 7), "https://github.com/o/r/issues/77", (label, "chore"), None, BOT_LOGIN, BOT_TYPE)])
    go(typed, run("failure", [job]))
    check("🏷️ a classification a person gave the ledger is kept — never a second type or severity",
          typed.issues[77].labels == (label, "chore"))

    # 2. open issue → append, not duplicate
    v, issue2, _ = go(gh, run("failure", [job]))
    check("open issue -> append, never a second open issue",
          v.action == "append" and len(gh.issues) == 1 and issue2 is not None and issue2.number == issue.number)
    check("appended body has TWO entries", issue2 is not None and issue2.body.count("\n" + ENTRY_HEAD) == 2)

    # 3. green with an open issue → comment 'green again: <run URL>' and close; ci-green event
    v, issue3, body3 = go(gh, run("success"))
    check("green -> close", v.action == "close" and issue3 is not None and issue3.state == "closed")
    check("green comment says `green again: <run URL>`",
          any(n == issue.number and c.startswith("green again: https://github.com/o/r/actions/runs/555") for n, c in gh.comments))
    check("ci-green event is posted", json.loads(body3 or "{}").get("event") == "ci-green")

    # 4. green with no open issue → no-op, no write, no event
    gh4 = _Fake()
    v, issue4, body4 = go(gh4, run("success"))
    check("green with no issue -> noop, zero writes, no event", v.action == "noop" and gh4.writes == 0 and body4 is None and issue4 is None)

    # 5. malformed failed-jobs → refused before any write
    for bad in ('{"name": "x"}', "[1]", '[{"url": "https://x"}]', '[{"name": "j", "url": "http://insecure"}]', "not json"):
        try:
            parse_failed_jobs(bad)
            check(f"malformed failed-jobs refused: {bad!r}", False, "was accepted")
        except Red:
            check(f"malformed failed-jobs refused: {bad!r}", True)
    check("empty failed-jobs is the empty list", parse_failed_jobs("") == () and parse_failed_jobs("[]") == ())
    try:
        decide("cancelled", [], now, 7)
        check("an outcome that is neither word is refused", False)
    except Red:
        check("an outcome that is neither word is refused", True)

    # 6. closed 2 days ago → reopened and appended, not duplicated
    closed_recent = Issue(7, title, "closed", new_body("o/r", 7) + "\n\n" + ENTRY_HEAD + "old — **failure** — [run 1](u)\n",
                          "https://github.com/o/r/issues/7", (label,), (now - timedelta(days=2)).strftime("%Y-%m-%dT%H:%M:%SZ"),
                          BOT_LOGIN, BOT_TYPE)
    gh6 = _Fake([closed_recent])
    v, issue6, _ = go(gh6, run("failure", [job]))
    check("closed 2 d ago -> reopen the same issue", v.action == "reopen" and issue6 is not None and issue6.number == 7
          and issue6.state == "open" and len(gh6.issues) == 1)
    check("reopened body gained an entry", issue6 is not None and issue6.body.count("\n" + ENTRY_HEAD) == 2)

    # 7. closed 9 days ago → a fresh issue
    closed_old = Issue(8, title, "closed", new_body("o/r", 7), "https://github.com/o/r/issues/8", (label,),
                       (now - timedelta(days=9)).strftime("%Y-%m-%dT%H:%M:%SZ"), BOT_LOGIN, BOT_TYPE)
    gh7 = _Fake([closed_old])
    v, issue7, _ = go(gh7, run("failure", [job]))
    check("closed 9 d ago -> create a fresh issue", v.action == "create" and issue7 is not None and issue7.number != 8 and len(gh7.issues) == 2)

    # 8. CD's own open `ci-failure` issue (main-cd.yml's shape) is never touched — different label, so
    #    the ledger never even lists it; and the ledger's create never files under that label.
    cd = Issue(9, "CD failed on main: run 42", "open", "CD failed on main: [run 42](u) for commit `abc`.",
               "https://github.com/o/r/issues/9", (FORBIDDEN_LABEL,))
    gh8 = _Fake([cd])
    v, issue8, _ = go(gh8, run("failure", [job]))
    check("CD's `ci-failure` issue is never this ledger: a fresh `ci-main-red` one is filed beside it",
          v.action == "create" and issue8 is not None and issue8.number != 9 and gh8.issues[9].state == "open"
          and gh8.issues[9].body == cd.body and FORBIDDEN_LABEL not in issue8.labels)
    gh8b = _Fake([cd])
    v, _, _ = go(gh8b, run("success"))
    check("green never closes or comments on CD's issue", v.action == "noop" and gh8b.writes == 0 and gh8b.issues[9].state == "open")
    try:
        _Fake().create_issue(title, FORBIDDEN_LABEL, new_body("o/r", 7))
        check("filing under `ci-failure` is refused", False, "was accepted")
    except AssertionError:
        check("filing under `ci-failure` is refused", True)

    # 8b. a `ci-main-red` issue WITHOUT the ownership mark (hand-labelled, or a rewritten body) is never
    #     closed, commented on, reopened or appended to — logged as foreign, left exactly as found.
    unmarked = Issue(10, title, "open", "someone labelled this by hand", "https://github.com/o/r/issues/10", (label,),
                     None, BOT_LOGIN, BOT_TYPE)
    gh8c = _Fake([unmarked])
    mine, foreign = owned_issues(gh8c, label, title)
    check("an unmarked `ci-main-red` issue is FOREIGN, not owned", mine == [] and [f.number for f in foreign] == [10])
    v, _, _ = go(gh8c, run("success"))
    check("green never closes an unmarked `ci-main-red` issue", v.action == "noop" and gh8c.writes == 0 and gh8c.issues[10].state == "open")
    v, issue8c, _ = go(gh8c, run("failure", [job]))
    check("red files a fresh marked issue beside the unmarked one, which is untouched",
          v.action == "create" and issue8c is not None and issue8c.number != 10 and gh8c.issues[10].body == unmarked.body)
    try:
        _owned(gh8c, label, title, 10)
        check("a write aimed at an unmarked issue is refused at the moment of the write", False, "was allowed")
    except Red:
        check("a write aimed at an unmarked issue is refused at the moment of the write", True)

    # 8c. a HUMAN-authored issue carrying label + title + marker — all three are public fields anyone
    #     with triage can set — is FOREIGN: the author is the term that cannot be forged.
    forged = Issue(11, title, "open", new_body("o/r", 7), "https://github.com/o/r/issues/11", (label,),
                   None, "some-human", "User")
    gh8d = _Fake([forged])
    mine, foreign = owned_issues(gh8d, label, title)
    check("a human-authored issue with label, title AND marker is FOREIGN", mine == [] and [f.number for f in foreign] == [11])
    v, _, _ = go(gh8d, run("success"))
    check("green never closes the forged issue", v.action == "noop" and gh8d.writes == 0 and gh8d.issues[11].state == "open")
    v, issue8d, _ = go(gh8d, run("failure", [job]))
    check("red files a bot-authored ledger beside the forged one, which is untouched",
          v.action == "create" and issue8d is not None and issue8d.number != 11 and issue8d.author == BOT_LOGIN
          and gh8d.issues[11].body == forged.body)
    check("a bot-typed login that is not github-actions[bot] (an App token) does not own either",
          not owns(Issue(12, title, "open", new_body("o/r", 7), "u", (label,), None, "meshweaver-cloud[bot]", "Bot"), label, title))

    # 11. the inbox URL: nothing is signed for anything but the control portal's /api/hooks/
    host = "control.systemorph.com"
    good = "https://control.systemorph.com/api/hooks/Hosting/PlatformBuilds"
    check("the control portal's inbox URL is accepted", validate_control_url(good, host) is None)
    check("host comparison is case-insensitive", validate_control_url("https://CONTROL.systemorph.com/api/hooks/x", host) is None)
    for bad, why in [("http://control.systemorph.com/api/hooks/Hosting/PlatformBuilds", "http"),
                     ("https://evil.example/api/hooks/Hosting/PlatformBuilds", "other host"),
                     ("https://control.systemorph.com.evil.example/api/hooks/x", "host suffix trick"),
                     ("https://control.systemorph.com/hooks/Hosting/PlatformBuilds", "path outside /api/hooks/"),
                     ("https://control.systemorph.com:8443/api/hooks/x", "odd port"),
                     ("https://user:pw@control.systemorph.com/api/hooks/x", "userinfo"),
                     ("", "empty"), ("not a url", "garbage")]:
        p = validate_control_url(bad, host)
        check(f"refused: {why}", p is not None and (bad in p or bad == ""), p or "")
    check("an empty expected host refuses everything", validate_control_url(good, "") is not None)

    # 9. the ledger is bounded
    b = new_body("o/r", 7)
    for i in range(MAX_LEDGER_ENTRIES + 3):
        b = append_entry(b, ledger_entry(run("failure", [job]), now + timedelta(minutes=i)))
    check(f"the ledger keeps at most {MAX_LEDGER_ENTRIES} entries and says it trimmed",
          b.count("\n" + ENTRY_HEAD) == MAX_LEDGER_ENTRIES and "3 older entries trimmed" in b)
    check("a hand-mangled body gets its ledger mark back", LEDGER_MARK in append_entry("someone rewrote this", "### e"))

    # 10. an empty failed-jobs on failure lists the run's jobs (the lane does this via the API)
    gh10 = _Fake(run_jobs=[
        {"name": "Build", "html_url": "https://github.com/o/r/actions/runs/555/job/2", "conclusion": "success", "steps": []},
        {"name": "Test (shard 3)", "html_url": "https://github.com/o/r/actions/runs/555/job/3", "conclusion": "failure",
         "steps": [{"name": "Restore", "conclusion": "success"}, {"name": "Run tests", "conclusion": "failure"}]},
        {"name": "Docs", "html_url": "https://github.com/o/r/actions/runs/555/job/4", "conclusion": "timed_out", "steps": []},
    ])
    listed = gh10.failed_jobs_of_run("555")
    check("listing keeps only failed/timed-out jobs and names the failed step",
          [j["name"] for j in listed] == ["Test (shard 3)", "Docs"] and listed[0]["step"] == "Run tests")
    v, issue10, body10 = go(gh10, run("failure", listed))
    check("the listed jobs reach the entry and the event",
          issue10 is not None and "[Test (shard 3)]" in issue10.body and "step `Run tests`" in issue10.body
          and len(json.loads(body10 or "{}")["failedJobs"]) == 2)
    check("`Docs` timed out with no step recorded and is therefore never-started; the test shard is not",
          listed[0]["neverStarted"] is False and listed[1]["neverStarted"] is True)
    check("a MIXED run says N of M never started and keeps the others as verdicts",
          issue10 is not None and "**1 of 2** failed job(s) ran no step" in issue10.body
          and "the failed jobs that ran do" in issue10.body and json.loads(body10 or "{}")["neverStarted"] == 1)

    # 12. Memex#275 — the measured 2026-09-11 shape: an org Actions budget refused every job of the
    #     run; each concluded `failure` in ~2 s with ZERO steps (MeshWeaver.Plugins run 34570627805
    #     attempt 1: 16 failure / 7 skipped / 0 ran). The ledger must say NOTHING ran and point at
    #     the annotation, never list them as ordinary failed jobs — and a `skipped` job (also zero
    #     steps) must not be counted, because it did not fail.
    gh12 = _Fake(run_jobs=[
        {"name": "Resolve the released platform", "html_url": "https://github.com/o/r/actions/runs/556/job/10",
         "conclusion": "failure", "steps": []},
        {"name": "Portal hosts (${{ matrix.shard }})", "html_url": "https://github.com/o/r/actions/runs/556/job/11",
         "conclusion": "failure", "steps": []},
        {"name": "Every gate executed", "html_url": "https://github.com/o/r/actions/runs/556/job/12",
         "conclusion": "skipped", "steps": []},
    ])
    listed12 = gh12.failed_jobs_of_run("556")
    check("a refused run lists its failed jobs, every one never-started, and NOT the skipped one",
          len(listed12) == 2 and all(j["neverStarted"] for j in listed12)
          and all(j["step"] == "" for j in listed12))
    v, issue12, body12 = go(gh12, run("failure", listed12))
    ev12 = json.loads(body12 or "{}")
    check("the entry says EVERY failed job never started (no failing verdict) and names the budget sentence",
          issue12 is not None and "**2 of 2** failed job(s) ran no step" in issue12.body
          and "carries no verdict about the commit" in issue12.body
          and "NOTHING in this run executed" not in issue12.body
          and "Actions budget is preventing further use" in issue12.body
          and issue12.body.count(NEVER_STARTED_NOTE) == 2)
    check("the event carries neverStarted on every job and the run-level count",
          ev12.get("neverStarted") == 2 and all(j.get("neverStarted") is True for j in ev12.get("failedJobs", [])))
    check("a caller-supplied failed-jobs list may carry neverStarted and defaults it to false",
          parse_failed_jobs('[{"name":"a","url":"https://x","step":"","neverStarted":true},{"name":"b","url":"https://y"}]')
          == ({"name": "a", "url": "https://x", "step": "", "neverStarted": True},
              {"name": "b", "url": "https://y", "step": "", "neverStarted": False}))
    try:
        parse_failed_jobs('[{"name":"a","url":"https://x","neverStarted":"yes"}]')
        check("a non-boolean neverStarted is refused", False, "was accepted")
    except Red:
        check("a non-boolean neverStarted is refused", True)

    # 13. a second OPEN ledger (the listing once returned nothing) is folded into the oldest
    def owned_open(n, entry_run):
        return Issue(n, title, "open", append_entry(new_body("o/r", 7), ledger_entry(entry_run, now)),
                     f"https://github.com/o/r/issues/{n}", (label,), None, BOT_LOGIN, BOT_TYPE)
    foreign13 = Issue(300, title, "open", new_body("o/r", 7), "https://github.com/o/r/issues/300", (label,),
                      None, "someone", "User")
    gh13 = _Fake([owned_open(201, run("failure", [job])), owned_open(202, run("failure", [job])), foreign13])
    m13, folded13 = fold_duplicates(gh13, owned_issues(gh13, label, title)[0], title, label)
    v13 = decide("failure", m13, now, 7)
    issue13, _ = apply(gh13, run("failure", [job]), v13, title, label, now, 7)
    check("two open ledgers -> the newer is folded into the oldest and closed",
          folded13 == [202] and v13.action == "append" and v13.issue == 201
          and gh13.issues[202].state == "closed" and gh13.issues[201].state == "open")
    check("the oldest keeps its entry, gains the folded one AND this run's",
          len(entries_of(gh13.issues[201].body)) == 3 and issue13 is not None and issue13.number == 201)
    check("each side of a fold names the other",
          any(n == 201 and "#202" in b for n, b in gh13.comments) and any(n == 202 and "#201" in b for n, b in gh13.comments))
    check("a foreign same-label issue is never folded or touched",
          gh13.issues[300].state == "open" and not any(n == 300 for n, _ in gh13.comments))

    # 14. green with two open ledgers -> fold, then close the oldest: nothing stays open
    gh14 = _Fake([owned_open(401, run("failure", [job])), owned_open(402, run("failure", [job]))])
    m14, folded14 = fold_duplicates(gh14, owned_issues(gh14, label, title)[0], title, label)
    apply(gh14, run("success"), decide("success", m14, now, 7), title, label, now, 7)
    check("green with two open ledgers closes both (one folded, one healed)",
          folded14 == [402] and gh14.issues[401].state == "closed" and gh14.issues[402].state == "closed")

    # 15. one open ledger -> the fold is a no-op (no write at all)
    gh15 = _Fake([owned_open(501, run("failure", [job]))])
    before = gh15.writes
    m15, folded15 = fold_duplicates(gh15, owned_issues(gh15, label, title)[0], title, label)
    check("one open ledger -> nothing folded, nothing written", folded15 == [] and gh15.writes == before)

    # 16. the listing returns one open ledger TWICE (it moved pages while being read)
    class _DupFake(_Fake):
        def labelled_issues(self, label):
            listed = super().labelled_issues(label)
            return listed + listed[:1]
    gh16 = _DupFake([owned_open(601, run("failure", [job]))])
    m16, folded16 = fold_duplicates(gh16, owned_issues(gh16, label, title)[0], title, label)
    apply(gh16, run("failure", [job]), decide("failure", m16, now, 7), title, label, now, 7)
    check("a ledger listed twice is never folded into itself (one open, appended once)",
          folded16 == [] and gh16.issues[601].state == "open" and len(entries_of(gh16.issues[601].body)) == 2)

    # 17. #7416 — WHICH red is this. The measured shape: ten reds of ONE job at ONE step, two causes.
    lock_job = {"name": "Every pinned manifest is locked", "url": "https://github.com/o/r/actions/runs/25/job/1",
                "step": "Lock every manifest the fleet pins", "neverStarted": False}
    timeout_errs = ["installation acme: GET https://acme.example.com/api/version timed out after 10.0s",
                    "Process completed with exit code 1."]
    timeout_errs_later = ["installation acme: GET https://acme.example.com/api/version timed out after 12.5s",
                          "Process completed with exit code 1."]
    registry_errs = ["instances.json does not account for globex.azurecr.io", "Process completed with exit code 1."]
    sig_a, sig_a2, sig_b = cause_signature(timeout_errs), cause_signature(timeout_errs_later), cause_signature(registry_errs)
    check("the same cause with different numbers has the SAME signature", sig_a == sig_a2, f"{sig_a} vs {sig_a2}")
    check("two different causes in the same step have DIFFERENT signatures", sig_a != sig_b)
    check("the generic exit-code line alone is no cause (`-/0`), and never part of a signature",
          cause_signature(["Process completed with exit code 2."]) == "-/0"
          and cause_signature(timeout_errs) == cause_signature(timeout_errs[:1]))
    check("a signature carries no annotation text (the issue is public; the run has the text)",
          "acme" not in sig_a and "globex" not in sig_b and CAUSE_RE.fullmatch(sig_a) is not None)
    red_a, red_b = dict(lock_job, cause=sig_a), dict(lock_job, cause=sig_b)
    # NEGATIVE CONTROL: what the ledger keyed on before — job + step — cannot tell the two apart.
    strip = lambda j: {k: v for k, v in j.items() if k != "cause"}  # noqa: E731 — the pre-#7416 job shape
    check("CONTROL: without the error signature the two incident causes read as ONE red",
          run_cause((strip(red_a),)) == run_cause((strip(red_b),)))
    check("with it they are two", run_cause((red_a,)) != run_cause((red_b,)))
    gh17 = _Fake()
    go(gh17, run("failure", [red_a]))
    n17 = next(iter(gh17.issues))
    check("the first red records a cause and says it is the first", "| cause | `" in gh17.issues[n17].body
          and "first recorded red" in gh17.issues[n17].body)
    go(gh17, run("failure", [dict(lock_job, cause=sig_a2)]))
    newest = entries_of(gh17.issues[n17].body)[-1]
    check("a further red with the same cause is appended to the SAME issue and says `same`",
          len(gh17.issues) == 1 and "**same** as the previous red" in newest and "CHANGED" not in newest)
    go(gh17, run("failure", [red_b]))
    newest = entries_of(gh17.issues[n17].body)[-1]
    check("a further red with a DIFFERENT cause says CHANGED and names the previous identity",
          len(gh17.issues) == 1 and "CHANGED" in newest and f"`{run_cause((red_a,))}`" in newest
          and f"errors `{sig_b}`" in newest)
    check("the event carries the run's cause identity",
          json.loads(event_body("ci-failure", run("failure", [red_b]), gh17.issues[n17]))["cause"] == run_cause((red_b,)))
    check("an unlisted run's cause is `unknown`, and an `unknown` predecessor is never compared",
          "`unknown`" in ledger_entry(run("failure", [], "_(could not list)_"), now)
          and previous_cause(append_entry(new_body("o/r", 7), ledger_entry(run("failure", [], "x"), now))) is None)
    check("a caller-supplied cause signature round-trips through the lane's parser",
          parse_failed_jobs(json.dumps([red_b])) == (red_b,))
    for bad in ("acme.example.com timed out", "abc|def", "`x`/1", 7):
        try:
            parse_failed_jobs(json.dumps([dict(lock_job, cause=bad)]))
            check(f"a cause that is not a signature is refused: {bad!r}", False, "was accepted")
        except Red:
            check(f"a cause that is not a signature is refused: {bad!r}", True)

    # 17b. Copilot on #7422 — no signature, no verdict: a caller that passes no causes (dotnet-test's
    #      main build lists its own jobs) or a job with no error evidence (`-/0`) is never "same".
    plain = run("failure", [job])
    gh17b = _Fake()
    go(gh17b, plain)
    go(gh17b, plain)
    nb = next(iter(gh17b.issues))
    last = entries_of(gh17b.issues[nb].body)[-1]
    check("jobs without a signature: cause `n/a`, never `same` or CHANGED",
          "`n/a`" in last and "same" not in last and "CHANGED" not in last)
    check("a `-/0` job (no error evidence) is not comparable", run_cause((dict(lock_job, cause="-/0"),)) == "n/a")
    check("an n/a entry is not a predecessor to compare against", previous_cause(gh17b.issues[nb].body) is None)
    check("the event carries no cause identity for an n/a red", json.loads(event_body("ci-failure", plain, gh17b.issues[nb]))["cause"] == "")

    # 18. the caller-side listing: causes from annotations, a timed-out (`cancelled`) job included,
    #     and an unreadable annotation is RED — never a job without a cause.
    class _ApiFake(GitHub):
        def __init__(self, jobs, notes, broken=False):
            super().__init__("o/r", "t")
            self.jobs, self.notes, self.broken = jobs, notes, broken

        def call(self, method, path, params=None, body=None, ok=(200, 201)):
            if path.startswith("actions/runs/"):
                return 200, {"jobs": self.jobs}
            if path.endswith("/annotations"):
                if self.broken:
                    raise Red("GET annotations answered HTTP 403")
                return 200, self.notes.get(path.split("/")[1], [])
            raise Red(f"unexpected {method} {path}")

    def api_job(n, name, conclusion, step_conclusion="failure"):
        return {"name": name, "html_url": f"https://github.com/o/r/actions/runs/25/job/{n}", "conclusion": conclusion,
                "check_run_url": f"{API}/repos/o/r/check-runs/{n}",
                "steps": [{"name": "Lock every manifest the fleet pins", "conclusion": step_conclusion}]}
    notes = {"1": [{"annotation_level": "failure", "message": m} for m in registry_errs]
                  + [{"annotation_level": "warning", "message": "a warning is not a cause"}],
             "3": []}
    api = _ApiFake([api_job(1, "protect", "failure"), api_job(2, "preflight", "success", "success"),
                    api_job(3, "slow", "cancelled", "cancelled")], notes)
    listed18 = api.failed_jobs_with_causes("25")
    check("the listing signs each failed job's error annotations (warnings ignored)",
          [j["name"] for j in listed18] == ["protect", "slow"] and listed18[0]["cause"] == sig_b)
    check("a job cut at its timeout (`cancelled`) is listed, with the step it was cut in",
          listed18[1]["step"] == "Lock every manifest the fleet pins" and listed18[1]["cause"] == "-/0")
    try:
        _ApiFake([api_job(1, "protect", "failure")], notes, broken=True).failed_jobs_with_causes("25")
        check("an unreadable annotation is RED", False, "returned a listing")
    except Red:
        check("an unreadable annotation is RED", True)

    # 19. the severity is the caller's: default sev:H (every existing caller unchanged), a scheduled
    #     lane passes sev:M (#7416 owner decision), anything else is refused before a write.
    gh19 = _Fake()
    v19 = decide("failure", [], now, 7)
    i19, _ = apply(gh19, run("failure", [job]), v19, title, label, now, 7, "sev:M")
    check("a lane passing sev:M gets bug + sev:M and NOT sev:H",
          i19 is not None and "sev:M" in gh19.issues[i19.number].labels and "bug" in gh19.issues[i19.number].labels
          and "sev:H" not in gh19.issues[i19.number].labels)
    gh19b = _Fake()
    i19b, _ = apply(gh19b, run("failure", [job]), decide("failure", [], now, 7), title, label, now, 7)
    check("no severity given -> the old default sev:H", i19b is not None and "sev:H" in gh19b.issues[i19b.number].labels)
    for bad in ("sev:X", "", "H", "sev:h"):
        try:
            taxonomy(bad)
            check(f"severity {bad!r} is refused", False, "was accepted")
        except Red:
            check(f"severity {bad!r} is refused", True)
    check("every severity has core's canonical label look", all(sv in TAXONOMY_LOOK for sv in SEVERITIES))

    # 20. an API OUTAGE is NO VERDICT, never a permission finding (#7416, #7448). Run 40598141020 read
    #     `API rate limit exceeded for installation` on both reporter jobs and printed "the CALLER job
    #     must grant `actions: read`" — grants that job already had. Driven through a fake transport:
    #     nothing here reaches GitHub.
    import email.message
    import io

    class _Answer:
        def __init__(self, status, text):
            self.status, self._text = status, text

        def read(self):
            return self._text.encode()

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    def http_error(code, text, **hdrs):
        h = email.message.Message()
        for k, v in hdrs.items():
            h[k.replace("_", "-")] = str(v)
        return urllib.error.HTTPError("https://api.github.com/x", code, "x", h, io.BytesIO(text.encode()))

    class _Transport:
        """Answers scripted per call; records every request and every sleep."""
        def __init__(self, answers, start=1_000_000.0):
            self.answers, self.calls, self.slept, self.now = list(answers), [], [], start

        def urlopen(self, req, timeout=None):
            self.calls.append((req.get_method(), req.full_url))
            a = self.answers.pop(0)
            if isinstance(a, BaseException):
                raise a
            return a

        def sleep(self, seconds):
            self.slept.append(seconds)
            self.now += seconds

        def clock(self):
            return self.now

    rate_body = ('{"message":"API rate limit exceeded for installation ID 12345.",'
                 '"documentation_url":"https://docs.github.com/rest/overview/rate-limits-for-the-rest-api"}')

    def client(t, budget):
        return GitHub("o/r", "t", urlopen=t.urlopen, sleep=t.sleep, clock=t.clock, wait_budget_seconds=budget)

    # (a) the reset is inside the budget: wait for it, then the answer — the red is still filed.
    t20 = _Transport([http_error(403, rate_body, x_ratelimit_remaining=0, x_ratelimit_reset=1_000_030),
                      _Answer(200, '{"jobs": []}')])
    st20, data20 = client(t20, 300).call("GET", "actions/runs/25/jobs")
    check("a rate-limited 403 whose reset is inside the budget waits for the reset, then reads",
          st20 == 200 and data20 == {"jobs": []} and t20.slept == [31] and len(t20.calls) == 2,
          f"slept {t20.slept}, {len(t20.calls)} calls")

    # (b) the reset is beyond the budget: NO VERDICT at once (nothing slept), never `Red`.
    t20b = _Transport([http_error(403, rate_body, x_ratelimit_remaining=0, x_ratelimit_reset=1_003_600)])
    try:
        client(t20b, 300).call("GET", "actions/runs/25/jobs")
        check("a rate limit beyond the budget is an ApiOutage", False, "returned")
    except Red as e:
        check("a rate limit beyond the budget is an ApiOutage, NOT a permission Red", False, str(e)[:120])
    except ApiOutage as e:
        said = e.sentence()
        check("a rate limit beyond the budget is NO VERDICT naming the reset, never a permission finding",
              "NO VERDICT" in said and "rate-limited until" in said and "permissions:" not in said
              and t20b.slept == [], said)

    # (c) the same rate-limit text with NO rate headers (the secondary limit's shape) is still a rate limit.
    t20c = _Transport([http_error(403, '{"message":"You have exceeded a secondary rate limit."}')])
    try:
        client(t20c, 0).call("GET", "issues")
        check("a secondary-limit 403 is an ApiOutage", False, "returned")
    except Red as e:
        check("a secondary-limit 403 is an ApiOutage", False, str(e)[:120])
    except ApiOutage:
        check("a secondary-limit 403 (no headers, 'rate limit' in the body) is an ApiOutage", True)

    # (d) NEGATIVE CONTROL: a real permission 403 stays the permission Red, at once, unretried.
    t20d = _Transport([http_error(403, '{"message":"Resource not accessible by integration"}',
                                  x_ratelimit_remaining=4000)])
    try:
        client(t20d, 300).call("GET", "actions/runs/25/jobs")
        check("a permission 403 is Red", False, "returned")
    except ApiOutage as e:
        check("a permission 403 is NOT an outage", False, e.sentence())
    except Red as e:
        check("a permission 403 is still the permission Red, unretried",
              "permissions:" in str(e) and len(t20d.calls) == 1 and t20d.slept == [], str(e)[:120])

    # (e) a 5xx on a WRITE is not replayed (it may have filed the issue); on a READ it is retried.
    t20e = _Transport([http_error(502, "bad gateway")])
    try:
        client(t20e, 300).call("POST", "issues", body={"title": "x"})
        check("a 5xx on a write is an ApiOutage", False, "returned")
    except ApiOutage:
        check("a 5xx on a write is NO VERDICT, never replayed", len(t20e.calls) == 1)
    t20f = _Transport([http_error(503, "unavailable"), _Answer(200, "[]")])
    check("a 5xx on a read is retried", client(t20f, 300).call("GET", "issues")[1] == [] and len(t20f.calls) == 2)
    t20g = _Transport([http_error(403, rate_body, retry_after=5), _Answer(201, '{"id": 1}')])
    check("a rate-limited WRITE (refused before it ran) is retried after retry-after",
          client(t20g, 300).call("POST", "issues/1/comments", body={"body": "x"})[1] == {"id": 1}
          and t20g.slept == [5])

    # (f) end to end through `main`: both reporter entry points exit 3 with NO VERDICT, not 1 with a
    #     grant to add. `main` builds its own client, so the module's transport is swapped for the call.
    real_urlopen, real_env = urllib.request.urlopen, dict(os.environ)
    try:
        os.environ.update({"GH_TOKEN": "t", "GITHUB_REPOSITORY": "o/r", "MW_API_OUTAGE_WAIT_MINUTES": "0"})
        os.environ.pop("GITHUB_OUTPUT", None)
        urllib.request.urlopen = lambda req, timeout=None: (_ for _ in ()).throw(
            http_error(403, rate_body, x_ratelimit_remaining=0, x_ratelimit_reset=int(time.time()) + 3000))
        import contextlib
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code_list = main(["--list-failed-jobs", "40598141020"])
            code_ledger = main(["--outcome", "failure", "--run-id", "40598141020", "--run-url", "https://x/runs/1",
                                "--sha", "abc", "--trigger", "push", "--label", "ci-lane-red", "--title", "lane is red"])
        said = err.getvalue()
        check("--list-failed-jobs on a rate limit exits 3 (NO VERDICT), not 1", code_list == API_OUTAGE_EXIT, said[-300:])
        check("the ledger on a rate limit exits 3 (NO VERDICT), not 1", code_ledger == API_OUTAGE_EXIT, said[-300:])
        check("neither entry point blames a grant the caller already gave",
              "must grant" not in said and said.count("NO VERDICT") >= 2, said[-400:])
    finally:
        urllib.request.urlopen = real_urlopen
        os.environ.clear()
        os.environ.update(real_env)

    # ── the artifact-store MISS ledger (`--artifact-misses`, MeshWeaver#7656 retired onto it) ──
    import types
    emitted = ("2026-11-01T10:00:00.0000000Z ##[error]ci-run-artifacts: NOT MEASURED — artifact store miss (infra) — "
               "no artifact matches this repository, run, attempt and name after one re-read — the store holds NOTHING")
    echoed = ("2026-11-01T10:00:00.0000000Z \x1b[36;1m  print(f\"::error title={MISS_TITLE}::ci-run-artifacts: "
              "NOT MEASURED — artifact store miss (infra) — {ex}\")\x1b[0m")
    check("the miss is recognised on the line ci-run-artifacts.py EMITS", printed_a_store_miss(emitted))
    check("…and never on the echoed source or another red", not printed_a_store_miss(echoed + "\n2026-11-01T10:00:01Z ##[error]CS1061 boom"))

    def miss_args(outcome, label=DEFAULT_LABEL):
        return types.SimpleNamespace(repo="o/r", outcome=outcome, run_id="777",
                                     run_url="https://github.com/o/r/actions/runs/777", sha="def456",
                                     trigger="push", label=label, reopen_window_days=7)

    def red_job(i, name):
        return {"id": i, "name": name, "html_url": f"https://github.com/o/r/actions/runs/777/job/{i}",
                "conclusion": "failure", "steps": [{"name": "Download build", "conclusion": "failure"}]}

    main_red = Issue(90, title, "open", new_body("o/r", 7), "https://github.com/o/r/issues/90", (label,), None, BOT_LOGIN, BOT_TYPE)
    hand_tracker = Issue(91, "CI artifact-store misses: NOT MEASURED (infra) tracking", "open", "hand-written",
                         "https://github.com/o/r/issues/91", (MISS_LABEL, "sev:M"), None, "someone", "User")
    gm = _Fake([main_red, hand_tracker], run_jobs=[red_job(1, "Portal hosts (leg 0)"), red_job(2, "Compile"),
                                                   red_job(3, "Runner died")])
    gm.logs = {1: emitted, 2: "2026-11-01T10:00:01.0000000Z ##[error]CS1061 boom", 3: None}
    v, iss = artifact_miss_ledger(gm, miss_args("failure"), now)
    miss_issues = [i for i in gm.issues.values() if i.title == MISS_TITLE]
    check("a red whose job printed the miss FILES the miss ledger under its own label and title",
          v.action == "create" and iss is not None and len(miss_issues) == 1 and MISS_LABEL in miss_issues[0].labels)
    check("the miss ledger lists ONLY the job that missed — not the compile red, not the job with no log",
          iss is not None and "Portal hosts (leg 0)" in iss.body and "Compile" not in iss.body.split("## Ledger")[1]
          and "Runner died" not in iss.body)
    check("🏷️ the miss ledger is stamped bug + sev:M (infra, never a release gate by itself)",
          iss is not None and all(t in gm.issues[iss.number].labels for t in taxonomy(MISS_SEVERITY)))
    check("the miss ledger's body says NOT MEASURED (infra) and carries the ownership mark",
          iss is not None and "NOT MEASURED" in iss.body and LEDGER_MARK in iss.body and "POSTed" not in iss.body)
    check("the main build ledger and the hand-written tracker are left exactly as found",
          gm.issues[90].body == main_red.body and gm.issues[91].state == "open" and gm.issues[91].body == "hand-written"
          and not any(n in (90, 91) for n, _ in gm.comments))
    v2, iss2 = artifact_miss_ledger(gm, miss_args("failure"), now)
    check("a second miss APPENDS to the open miss ledger, never a second one",
          v2.action == "append" and len([i for i in gm.issues.values() if i.title == MISS_TITLE]) == 1
          and iss2 is not None and iss2.body.count("\n" + ENTRY_HEAD) == 2)
    v3, _ = artifact_miss_ledger(gm, miss_args("success"), now)
    check("a green main CLOSES the miss ledger with `green again`",
          v3.action == "close" and gm.issues[iss.number].state == "closed"
          and any(n == iss.number and c.startswith("green again:") for n, c in gm.comments))
    v4, _ = artifact_miss_ledger(gm, miss_args("success"), now)
    check("green with no open miss ledger is a no-op", v4.action == "noop")

    other = _Fake(run_jobs=[red_job(2, "Compile")])
    other.logs = {2: "2026-11-01T10:00:01.0000000Z ##[error]CS1061 boom"}
    vo, _ = artifact_miss_ledger(other, miss_args("failure"), now)
    check("a red for another reason leaves the miss ledger alone (no write, no issue)",
          vo.action == "noop" and other.writes == 0 and not other.issues)

    lane = _Fake(run_jobs=[red_job(1, "x")])
    lane.logs = {1: emitted}
    vl, _ = artifact_miss_ledger(lane, miss_args("failure", label="ci-lock-pinned-digests-red"), now)
    check("another lane's ledger call reads nothing and writes nothing for the miss ledger",
          vl.action == "noop" and lane.writes == 0 and getattr(lane, "reads", 0) == 0)

    blind = _Fake(run_jobs=[red_job(1, "x")])
    blind.logs = {1: Red("GET actions/jobs/1/logs answered HTTP 500")}
    try:
        artifact_miss_ledger(blind, miss_args("failure"), now)
        check("an unreadable log is RED, never 'no miss'", False, "returned")
    except Red:
        check("an unreadable log is RED, never 'no miss'", blind.writes == 0)

    if fails:
        print("::error title=ci-failure-ledger self-test::" + "; ".join(fails), file=sys.stderr)
        return 1
    print("✓ ci-failure-ledger self-test: create / append / reopen-recent / create-after-old / close-with-comment / "
          "noop-when-green / refuse-malformed / cd-ci-failure-untouched / unmarked-ci-main-red-untouched / human-authored-is-foreign / "
          "never-labelled-ci-failure / inbox-url-validated / bounded-ledger / jobs-listed / never-started-said / duplicate-folded / "
          "cause-same-or-changed / api-outage-is-no-verdict / artifact-store-miss-ledger — every rule fires and stays silent as designed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
