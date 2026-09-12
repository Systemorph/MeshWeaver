#!/usr/bin/env python3
"""ci-failure-ledger.py — ONE `ci-failure` issue per repository, kept true by every run of `main`.

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
labelled `ci-failure`, whose BODY is a dated ledger with one entry per red run. The lane then POSTs
a signed event to the control portal, whose triage agent opens a thread on it.

THE FOUR RULES, each covered by --self-test
--------------------------------------------
  * `failure` + no matching issue        → CREATE one (label + exact title), first ledger entry.
  * `failure` + a matching OPEN issue    → APPEND an entry to its body. Never a second open issue.
  * `failure` + a matching issue CLOSED
    within the last REOPEN_WINDOW_DAYS   → REOPEN it and append (flapping: one story, one issue).
    Closed longer ago                    → CREATE a fresh one (its age is the outage's age, #3176).
  * `success` + a matching OPEN issue    → comment `green again: <run URL>` and CLOSE it.
    `success` + none                     → no-op, and NO event (nothing changed).

"Matching" is label AND exact title. The title is what keeps this ledger apart from another
`ci-failure` writer in the same repository (core's main-cd.yml files "CD failed on main: run N"
under the same label): listing by label alone would append this story onto that issue and close it
on the next green.

IT CANNOT PASS SILENTLY. A malformed `failed-jobs` input, an outcome that is neither word, a token
that cannot write issues (403 → the message names the `issues: write` grant on the CALLER job), an
API failure — each is RED naming the cause, never a quiet "nothing to do". The one deliberate
"nothing to do" (green, no open issue) SAYS so with the denominator.

    python3 ci-failure-ledger.py                 # in Actions: everything from the environment
    python3 ci-failure-ledger.py --self-test     # prove every rule, against a fake GitHub
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone

API = "https://api.github.com"
DEFAULT_LABEL = "ci-failure"
DEFAULT_TITLE = "ci-failure: main is red"
DEFAULT_REOPEN_WINDOW_DAYS = 7
MAX_LEDGER_ENTRIES = 40          # ~500 bytes an entry; GitHub caps a body at 65,536 characters
LEDGER_MARK = "<!-- ci-failure-ledger -->"
ENTRY_HEAD = "### "              # every ledger entry starts with this at column 0
FAILED_CONCLUSIONS = ("failure", "timed_out")


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


@dataclass
class Verdict:
    action: str                  # create | append | reopen | close | noop
    issue: int | None
    reason: str


def parse_failed_jobs(raw: str) -> tuple[dict, ...]:
    """The caller's `failed-jobs` input as a tuple of {name, url, step}. Anything else is refused.

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
        out.append({"name": name.strip(), "url": url, "step": step.strip()})
    return tuple(out)


def _parse_iso(ts: str) -> datetime:
    return datetime.fromisoformat(ts.replace("Z", "+00:00")).astimezone(timezone.utc)


def decide(outcome: str, matching: list[Issue], now: datetime, reopen_window_days: int) -> Verdict:
    """The rule table above, over the issues that carry the label AND the exact title."""
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


def ledger_entry(run: Run, now: datetime) -> str:
    stamp = now.strftime("%Y-%m-%dT%H:%M:%SZ")
    if run.failed_jobs:
        jobs = "<br>".join(
            f"[{j['name']}]({j['url']})" + (f" — step `{j['step']}`" if j["step"] else "")
            for j in run.failed_jobs)
    else:
        jobs = run.jobs_note or "_(none listed)_"
    return "\n".join([
        f"{ENTRY_HEAD}{stamp} — **{run.outcome}** — [run {run.run_id}]({run.run_url})",
        "",
        "| | |",
        "|---|---|",
        f"| commit | `{run.sha}` |",
        f"| trigger | `{run.trigger}` |",
        f"| platform set | {('`' + run.platform_set + '`') if run.platform_set else '_(none)_'} |",
        f"| failed jobs | {jobs} |",
    ])


def new_body(repo: str, reopen_window_days: int) -> str:
    return "\n".join([
        f"`main` of **{repo}** is red. This issue is the ledger: every red run on `main` appends a dated "
        f"entry below, and the run that goes green again closes it. A red within {reopen_window_days} days "
        f"of the close REOPENS this issue rather than filing a second one, so one outage is one story.",
        "",
        "Each entry is also POSTed, signed, to the control portal's triage inbox "
        "(`Hosting/PlatformBuilds` on memex.systemorph.com), whose triage agent opens a thread on it. "
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
        obj = {"event": "ci-failure", "repo": run.repo, "sha": run.sha, "run": int(run.run_id),
               "runUrl": run.run_url, "trigger": run.trigger, "failedJobs": list(run.failed_jobs),
               "platformSet": run.platform_set, "issueUrl": issue.html_url, "issueNumber": issue.number}
    elif kind == "ci-green":
        obj = {"event": "ci-green", "repo": run.repo, "sha": run.sha, "run": int(run.run_id),
               "runUrl": run.run_url, "issueNumber": issue.number}
    else:
        raise Red(f"unknown event kind {kind!r}")
    return json.dumps(obj, separators=(",", ":"), ensure_ascii=False)


# ── GitHub, and what a token that cannot write looks like ─────────────────────────────────────

class GitHub:
    def __init__(self, repo: str, token: str):
        self.repo, self.token = repo, token

    def call(self, method: str, path: str, params: dict | None = None, body: dict | None = None,
             ok=(200, 201)) -> tuple[int, object]:
        url = f"{API}/repos/{self.repo}/{path}"
        if params:
            url += "?" + urllib.parse.urlencode(params)
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(url, data=data, method=method, headers={
            "Authorization": f"Bearer {self.token}", "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28", "Content-Type": "application/json",
            "User-Agent": "ci-failure-ledger"})
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                status, text = resp.status, resp.read().decode()
        except urllib.error.HTTPError as e:
            status, text = e.code, e.read().decode(errors="replace")
        except urllib.error.URLError as e:
            raise Red(f"{method} {path}: could not reach api.github.com ({e.reason})") from e
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
            "description": "main is red — the ledger issue every red run on main appends to"}, ok=(201, 422))
        # 422 = already exists, which is the idempotent path; 201 = created now.
        _ = status

    def matching_issues(self, label: str, title: str) -> list[Issue]:
        out: list[Issue] = []
        for page in range(1, 6):
            _, items = self.call("GET", "issues", {"labels": label, "state": "all", "per_page": 100,
                                                   "page": page, "sort": "updated", "direction": "desc"})
            items = items or []
            for it in items:
                if "pull_request" in it or it.get("title") != title:
                    continue
                out.append(Issue(it["number"], it["title"], it["state"], it.get("body") or "", it["html_url"],
                                 tuple(l["name"] for l in it.get("labels", [])), it.get("closed_at")))
            if len(items) < 100:
                break
        return out

    def create_issue(self, title: str, label: str, body: str) -> Issue:
        _, it = self.call("POST", "issues", body={"title": title, "labels": [label], "body": body})
        return Issue(it["number"], it["title"], it["state"], it.get("body") or "", it["html_url"])

    def update_issue(self, number: int, **fields) -> Issue:
        _, it = self.call("PATCH", f"issues/{number}", body=fields)
        return Issue(it["number"], it["title"], it["state"], it.get("body") or "", it["html_url"])

    def comment(self, number: int, body: str) -> None:
        self.call("POST", f"issues/{number}/comments", body={"body": body})

    def failed_jobs_of_run(self, run_id: str) -> tuple[dict, ...]:
        """{name, url, step} for every job of the run that concluded failure/timed_out."""
        found: list[dict] = []
        for page in range(1, 11):
            _, data = self.call("GET", f"actions/runs/{run_id}/jobs", {"per_page": 100, "page": page})
            jobs = (data or {}).get("jobs", [])
            for j in jobs:
                if j.get("conclusion") not in FAILED_CONCLUSIONS:
                    continue
                step = next((s.get("name", "") for s in j.get("steps", [])
                             if s.get("conclusion") in FAILED_CONCLUSIONS), "")
                found.append({"name": j.get("name", "?"), "url": j.get("html_url", ""), "step": step})
            if len(jobs) < 100:
                break
        return tuple(found)


# ── apply the verdict ─────────────────────────────────────────────────────────────────────────

def apply(gh, run: Run, verdict: Verdict, title: str, label: str, now: datetime,
          reopen_window_days: int) -> tuple[Issue | None, str | None]:
    """Perform the verdict. Returns (the issue touched, the event body to POST or None)."""
    if verdict.action == "noop":
        return None, None
    if verdict.action == "close":
        assert verdict.issue is not None
        gh.comment(verdict.issue, f"green again: {run.run_url}\n\n"
                                  f"Commit `{run.sha}`, trigger `{run.trigger}`. Closing; a red within "
                                  f"{reopen_window_days} days reopens this issue rather than filing a new one.")
        issue = gh.update_issue(verdict.issue, state="closed", state_reason="completed")
        return issue, event_body("ci-green", run, issue)
    entry = ledger_entry(run, now)
    gh.ensure_label(label)
    if verdict.action == "create":
        issue = gh.create_issue(title, label, append_entry(new_body(run.repo, reopen_window_days), entry))
    elif verdict.action == "append":
        assert verdict.issue is not None
        current = next(i for i in gh.matching_issues(label, title) if i.number == verdict.issue)
        issue = gh.update_issue(verdict.issue, body=append_entry(current.body, entry))
    elif verdict.action == "reopen":
        assert verdict.issue is not None
        current = next(i for i in gh.matching_issues(label, title) if i.number == verdict.issue)
        issue = gh.update_issue(verdict.issue, state="open", body=append_entry(current.body, entry))
    else:
        raise Red(f"unknown verdict {verdict.action!r}")
    return issue, event_body("ci-failure", run, issue)


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
    ap.add_argument("--reopen-window-days", type=int, default=int(env("LEDGER_REOPEN_WINDOW_DAYS") or DEFAULT_REOPEN_WINDOW_DAYS))
    ap.add_argument("--event-out", default=env("LEDGER_EVENT_OUT"), help="file the exact event body is written to")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args(argv)

    if a.self_test:
        return self_test()

    try:
        token = env("GH_TOKEN") or env("GITHUB_TOKEN") or ""
        if not token:
            raise Red("no GH_TOKEN — the caller must pass `secrets: github-token: ${{ secrets.GITHUB_TOKEN }}`; refusing to report 'nothing to do' on no access")
        for name, value in (("repo", a.repo), ("outcome", a.outcome), ("run-id", a.run_id),
                            ("run-url", a.run_url), ("sha", a.sha), ("trigger", a.trigger)):
            if not value:
                raise Red(f"--{name} is empty — the lane's inputs are incomplete")
        failed_jobs = parse_failed_jobs(a.failed_jobs)
        now = datetime.now(timezone.utc)
        gh = GitHub(a.repo, token)

        jobs_note = ""
        listing_error: Red | None = None
        if a.outcome == "failure" and not failed_jobs:
            try:
                failed_jobs = gh.failed_jobs_of_run(a.run_id)
            except Red as e:
                # Recorded in the entry and then RED below: the ledger stays intact, the mis-grant is named.
                listing_error = e
                jobs_note = f"_(could not list this run's jobs — {e})_"
        run = Run(a.repo, a.outcome, str(a.run_id), a.run_url, a.sha, a.trigger, failed_jobs,
                  a.platform_set or "", jobs_note)

        matching = gh.matching_issues(a.label, a.title)
        verdict = decide(a.outcome, matching, now, a.reopen_window_days)
        print(f"{verdict.action.upper():7} {verdict.reason} (label `{a.label}`, title {a.title!r}, "
              f"{len(matching)} matching issue(s) seen)")
        issue, body = apply(gh, run, verdict, a.title, a.label, now, a.reopen_window_days)
        if issue is not None:
            print(f"issue #{issue.number} {issue.state}: {issue.html_url}")
        if body is not None and a.event_out:
            with open(a.event_out, "w", encoding="utf-8") as fh:
                fh.write(body)
        _emit_outputs(env("GITHUB_OUTPUT"), action=verdict.action,
                      **{"issue-number": issue.number if issue else "", "issue-url": issue.html_url if issue else "",
                         "post": "true" if body is not None else "false"})
        if listing_error is not None:
            raise Red(f"the ledger entry is written, but this run's failed jobs could not be listed: {listing_error}")
        return 0
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

    def matching_issues(self, label, title):
        return [i for i in self.issues.values() if label in i.labels and i.title == title]

    def create_issue(self, title, label, body):
        self.writes += 1
        n = self.next_number
        self.next_number += 1
        self.issues[n] = Issue(n, title, "open", body, f"https://github.com/o/r/issues/{n}", (label,))
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

    def failed_jobs_of_run(self, run_id):
        return tuple({"name": j["name"], "url": j["html_url"],
                      "step": next((s["name"] for s in j.get("steps", []) if s.get("conclusion") == "failure"), "")}
                     for j in self.run_jobs if j.get("conclusion") in FAILED_CONCLUSIONS)


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
        v = decide(r.outcome, gh.matching_issues(label, title), now, 7)
        issue, body = apply(gh, r, v, title, label, now, 7)
        return v, issue, body

    job = {"name": "Portal hosts (shard 0)", "url": "https://github.com/o/r/actions/runs/555/job/1", "step": "Run tests"}

    # 1. no issue → create, one entry, a ci-failure event naming the issue
    gh = _Fake()
    v, issue, body = go(gh, run("failure", [job]))
    check("no issue -> create", v.action == "create" and len(gh.issues) == 1 and issue is not None)
    check("created body carries the ledger mark and ONE entry",
          issue is not None and LEDGER_MARK in issue.body and issue.body.count("\n" + ENTRY_HEAD) == 1)
    ev = json.loads(body or "{}")
    check("ci-failure event names repo, run, issue and the failed job",
          ev.get("event") == "ci-failure" and ev.get("run") == 555 and ev.get("issueNumber") == issue.number
          and ev.get("failedJobs") == [job] and ev.get("platformSet") == "3.0.0-ci.4711" and ev.get("trigger") == "schedule")
    check("event bytes are compact and field-ordered", (body or "").startswith('{"event":"ci-failure","repo":"o/r","sha":"abc123","run":555,'))

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
                          "https://github.com/o/r/issues/7", (label,), (now - timedelta(days=2)).strftime("%Y-%m-%dT%H:%M:%SZ"))
    gh6 = _Fake([closed_recent])
    v, issue6, _ = go(gh6, run("failure", [job]))
    check("closed 2 d ago -> reopen the same issue", v.action == "reopen" and issue6 is not None and issue6.number == 7
          and issue6.state == "open" and len(gh6.issues) == 1)
    check("reopened body gained an entry", issue6 is not None and issue6.body.count("\n" + ENTRY_HEAD) == 2)

    # 7. closed 9 days ago → a fresh issue
    closed_old = Issue(8, title, "closed", "x", "https://github.com/o/r/issues/8", (label,),
                       (now - timedelta(days=9)).strftime("%Y-%m-%dT%H:%M:%SZ"))
    gh7 = _Fake([closed_old])
    v, issue7, _ = go(gh7, run("failure", [job]))
    check("closed 9 d ago -> create a fresh issue", v.action == "create" and issue7 is not None and issue7.number != 8 and len(gh7.issues) == 2)

    # 8. another writer's open ci-failure issue (different title) is neither appended to nor closed
    other = Issue(9, "CD failed on main: run 42", "open", "cd", "https://github.com/o/r/issues/9", (label,))
    gh8 = _Fake([other])
    v, issue8, _ = go(gh8, run("failure", [job]))
    check("a same-label issue with another title is not this ledger", v.action == "create" and issue8 is not None and issue8.number != 9)
    v, _, _ = go(_Fake([other]), run("success"))
    check("green never closes another writer's issue", v.action == "noop")

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

    if fails:
        print("::error title=ci-failure-ledger self-test::" + "; ".join(fails), file=sys.stderr)
        return 1
    print("✓ ci-failure-ledger self-test: create / append / reopen-recent / create-after-old / close-with-comment / "
          "noop-when-green / refuse-malformed / other-writer-untouched / bounded-ledger / jobs-listed — every rule fires and stays silent as designed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
