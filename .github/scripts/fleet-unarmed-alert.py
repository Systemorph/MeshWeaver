#!/usr/bin/env python3
"""fleet-unarmed-alert.py — ONE `fleet-unarmed` issue, kept true by every run of main-cd's `arm` job.

    python3 .github/scripts/fleet-unarmed-alert.py --reading unarmed.json   # in Actions (env below)
    python3 .github/scripts/fleet-unarmed-alert.py --self-test

WHY (MeshWeaver#6042, policy `one-promotion-gate`)
---------------------------------------------------
`promote` makes every new set usable by CI at once; `arm` hands a set to the FLEET only on its
platform verdict (`arm-promoted-set.py judge`): a ladder still running or a control that has not
taken the set WAITS, a red / missing / ambiguous ladder is REFUSED, a newer green set supersedes
both. That is correct, and it is asynchronous by construction — so a standing red ladder, or a
platform-side outage, freezes fleet updates while every main-cd run stays green and says
`nothing to arm`. `arming_frozen` (policy `control-first-never-silent`) turns ONE such case red: control
behind past its bound with nothing armed. Every other stall — a refused ladder above all — was
silent. This script is the signal for all of them.

THE RULE
--------
The reading (`arm-promoted-set.py select --unarmed-out`) lists every promoted set NEWER than the
newest armed one (after this run's own arming, if it armed), each with its verdict state and run
link. The fleet is STALE when the OLDEST of them was promoted more than THRESHOLD hours ago — the
fleet has had a newer platform available, and unarmed, for that long. Measuring from the oldest
unarmed set (never the newest) is what makes it a bound: a new set every ~20 minutes would reset a
clock measured from the newest one, and the stall it exists to catch would never fire.

  * stale + no owned open issue  → CREATE one (body = the reading: every unarmed set, its state —
                                   `waiting` / `refused` / `not examined` — its sentence and its run).
  * stale + an owned open issue  → REWRITE its body to the current reading; COMMENT only when the
                                   reading CHANGED (a new set, a state flip) — never one per tick.
  * not stale + an owned open    → comment `armed again` naming the armed set, and CLOSE it.
  * not stale + none             → nothing, said with the denominator.
  * two or more owned open       → the OLDEST survives; every newer one is closed as a duplicate
                                   pointing at it (the label listing is not a transaction).

THRESHOLD: `FLEET_UNARMED_ALERT_HOURS`, the one knob — set literally (6) in main-cd's `arm` job env.
A missing, non-numeric or non-positive value is RED, never a silent default in this script.

OWNERSHIP — the ci-main-red shape (ci-failure-ledger.py): an issue is this script's only when it
carries the label `fleet-unarmed`, the exact title, the hidden mark `<!-- fleet-unarmed alert -->`
AND its author is the Actions bot. Label, title and body are public fields anyone with triage can
set; the author is not. An issue lacking any of the four is never edited, commented on or closed.

IT CANNOT PASS SILENTLY. A missing or malformed reading, an unarmed set without a promotion time,
a token that cannot write issues — each is RED naming the cause. Its alarm is the ISSUE, not a red
run: `arm` stays green on a stale reading (a `::warning::` names it), because main-cd red would
page `alert-on-failure` for a delivery that did succeed — what stalled is the arming verdict.
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
LABEL = "fleet-unarmed"
TITLE = "fleet-unarmed: the promotion gate has left the fleet unarmed"
MARK = "<!-- fleet-unarmed alert -->"
FINGERPRINT = re.compile(r"<!-- fleet-unarmed reading: (\S+) -->")
BOT_LOGIN = "github-actions[bot]"
STATES = ("waiting", "refused", "not examined")


class Red(Exception):
    """A condition the alert must not pass over silently."""


@dataclass(frozen=True)
class Issue:
    number: int
    title: str
    state: str
    body: str
    labels: tuple
    author: str
    author_type: str


@dataclass
class Decision:
    stale: bool
    age_hours: float | None
    oldest: dict | None
    unarmed: list
    armed: str
    fingerprint: str


def parse_threshold(raw: str | None) -> float:
    if raw is None or not str(raw).strip():
        raise Red("FLEET_UNARMED_ALERT_HOURS is empty — the threshold is declared in main-cd's `arm` job; "
                  "refusing to fall back to a value nobody set")
    try:
        h = float(raw)
    except ValueError:
        raise Red(f"FLEET_UNARMED_ALERT_HOURS={raw!r} is not a number of hours") from None
    if not h > 0:
        raise Red(f"FLEET_UNARMED_ALERT_HOURS={raw!r} must be positive")
    return h


def _when(ts: str) -> datetime:
    return datetime.fromisoformat(ts.replace("Z", "+00:00"))


def check_reading(reading: object) -> None:
    if not isinstance(reading, dict):
        raise Red("the reading is not a JSON object")
    if not isinstance(reading.get("unarmed"), list):
        raise Red("the reading carries no `unarmed` list")
    if not str(reading.get("armed") or ""):
        raise Red("the reading names no newest armed set — cannot say what the fleet runs")
    for s in reading["unarmed"]:
        if not isinstance(s, dict):
            raise Red(f"an unarmed entry is not an object: {s!r}")
        for k in ("v_portal", "run_number", "state", "sentence", "run_url", "promoted_at"):
            if not str(s.get(k) or ""):
                raise Red(f"unarmed set {s.get('v_portal') or '?'} lacks `{k}` — refusing to judge staleness on it")
        if s["state"] not in STATES:
            raise Red(f"unarmed set {s['v_portal']} has state {s['state']!r}, not one of {STATES}")
        try:
            _when(str(s["promoted_at"]))
        except ValueError:
            raise Red(f"unarmed set {s['v_portal']} has an unreadable promoted_at {s['promoted_at']!r}") from None


def decide(reading: dict, now: datetime, threshold_hours: float) -> Decision:
    """Pure. Stale when the OLDEST unarmed promoted set is older than the threshold."""
    check_reading(reading)
    unarmed = sorted(reading["unarmed"], key=lambda s: int(s["run_number"]), reverse=True)
    fp = ",".join(f"{s['run_number']}:{s['state'].replace(' ', '-')}" for s in unarmed) or "none"
    if not unarmed:
        return Decision(False, None, None, [], str(reading["armed"]), fp)
    oldest = unarmed[-1]
    age = (now - _when(str(oldest["promoted_at"]))).total_seconds() / 3600
    return Decision(age > threshold_hours, age, oldest, unarmed, str(reading["armed"]), fp)


def render_body(d: Decision, threshold_hours: float, run_url: str, now: datetime) -> str:
    lines = [
        f"🚨 The promotion gate has left the fleet unarmed for **{d.age_hours:.1f} h** "
        f"(threshold {threshold_hours:g} h, `FLEET_UNARMED_ALERT_HOURS` in main-cd's `arm` job).",
        "",
        f"- newest ARMED set: **{d.armed}** — what SelfUpdateHostedService rolls the fleet to",
        f"- promoted but NOT armed: **{len(d.unarmed)}** set(s); the oldest, `{d.oldest['v_portal']}`, "
        f"was promoted {d.oldest['promoted_at']}",
        f"- reading: [{run_url or 'this run'}]({run_url or '#'}) at {now.strftime('%Y-%m-%dT%H:%M:%SZ')}",
        "",
        "| set | core | verdict | why | run |",
        "|---|---|---|---|---|",
    ]
    for s in d.unarmed:
        why = str(s["sentence"]).replace("|", "\\|").replace("\n", " ")
        lines.append(f"| `{s['v_portal']}` | `{str(s.get('core_sha') or '')[:9]}` | **{s['state']}** | {why} "
                     f"| [run #{s['run_number']}]({s['run_url']}) |")
    lines += [
        "",
        "Read it by state: **refused** — the set's compatibility ladder is red, missing or ambiguous; fix "
        "compatibility (or declare an epoch bump), a newer green set then arms past it. **waiting** — the ladder "
        "is still running, or control has not taken the set; a control behind past its bound is the open "
        "`control-lag` issue. **not examined** — older than the ten newest sets `select` judges. The incident "
        "override (main-cd `workflow_dispatch` with `arm_override` + `arm_reason`) exists and stays the "
        "maintainer's call; it is not the remedy this issue suggests.",
        "",
        "This issue is rewritten on every `arm` run while the fleet stays unarmed and closes itself on the first "
        "run that finds it armed again (policy `one-promotion-gate`, Doc/Architecture/OnePromotionGate).",
        "",
        MARK,
        f"<!-- fleet-unarmed reading: {d.fingerprint} -->",
    ]
    return "\n".join(lines) + "\n"


def owns(issue: Issue) -> bool:
    return (LABEL in issue.labels and issue.title == TITLE and MARK in (issue.body or "")
            and issue.author == BOT_LOGIN and issue.author_type == "Bot")


@dataclass
class Plan:
    action: str                       # create / update / close / none
    target: int | None = None
    duplicates: list = field(default_factory=list)
    comment: bool = False


def plan(d: Decision, issues: list[Issue]) -> Plan:
    """Pure. What to do with the owned OPEN issues given the decision."""
    mine = sorted((i for i in issues if i.state == "open" and owns(i)), key=lambda i: i.number)
    survivor, dups = (mine[0], [i.number for i in mine[1:]]) if mine else (None, [])
    if d.stale:
        if survivor is None:
            return Plan("create", None, dups)
        m = FINGERPRINT.search(survivor.body or "")
        return Plan("update", survivor.number, dups, comment=(m is None or m.group(1) != d.fingerprint))
    if survivor is not None:
        return Plan("close", survivor.number, dups)
    return Plan("none", None, dups)


# ───────────────────────────────── GitHub (REST only) ─────────────────────────────────────

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
            "User-Agent": "fleet-unarmed-alert"})
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                status, text = resp.status, resp.read().decode()
        except urllib.error.HTTPError as e:
            status, text = e.code, e.read().decode(errors="replace")
        except urllib.error.URLError as e:
            raise Red(f"{method} {path}: could not reach api.github.com ({e.reason})") from e
        if status in (401, 403):
            raise Red(f"{method} {path} answered HTTP {status} — the `arm` job must grant `issues: write`. "
                      f"Body: {text[:300]}")
        if status not in ok:
            raise Red(f"{method} {path} answered HTTP {status}: {text[:300]}")
        return status, (json.loads(text) if text.strip() else None)

    def ensure_label(self) -> None:
        # An UNKNOWN label folds a listing to `[]`, byte-identical to "no issue yet" (sentinel-issue.py),
        # so existence is read on the single-label endpoint first and created when it 404s.
        status, _ = self.call("GET", f"labels/{urllib.parse.quote(LABEL)}", ok=(200, 404))
        if status == 404:
            self.call("POST", "labels", body={
                "name": LABEL, "color": "B60205",
                "description": "Policy one-promotion-gate: promoted platform sets stay unarmed past the bound"},
                ok=(201, 422))

    def open_labelled(self) -> list[Issue]:
        out: list[Issue] = []
        for page in range(1, 6):
            _, items = self.call("GET", "issues", {"labels": LABEL, "state": "open", "per_page": 100, "page": page})
            items = items or []
            for it in items:
                if "pull_request" in it:
                    continue
                u = it.get("user") or {}
                out.append(Issue(it["number"], it["title"], it["state"], it.get("body") or "",
                                 tuple(l["name"] for l in it.get("labels", [])),
                                 str(u.get("login") or ""), str(u.get("type") or "")))
            if len(items) < 100:
                break
        return out

    def create(self, body: str) -> int:
        _, it = self.call("POST", "issues", body={"title": TITLE, "body": body, "labels": [LABEL]})
        return int(it["number"])  # type: ignore[index]

    def edit(self, number: int, **fields) -> None:
        self.call("PATCH", f"issues/{number}", body=fields)

    def comment(self, number: int, body: str) -> None:
        self.call("POST", f"issues/{number}/comments", body={"body": body})


def apply(gh, d: Decision, p: Plan, body: str, run_url: str) -> str:
    """Execute the plan; returns one sentence for the log."""
    for n in p.duplicates:
        gh.comment(n, f"Duplicate of #{p.target or '?'} — this alert keeps ONE issue; closing.")
        gh.edit(n, state="closed", state_reason="not_planned")
    if p.action == "create":
        n = gh.create(body)
        return f"STALE — filed #{n}"
    if p.action == "update":
        gh.edit(p.target, body=body)
        if p.comment:
            gh.comment(p.target, f"Reading changed ([run]({run_url})): {len(d.unarmed)} unarmed set(s), oldest "
                                 f"`{d.oldest['v_portal']}` {d.age_hours:.1f} h — the body carries the table.")
        return f"STALE — #{p.target} rewritten{' and commented (reading changed)' if p.comment else ' (reading unchanged, no comment)'}"
    if p.action == "close":
        gh.comment(p.target, f"✅ Armed again ([run]({run_url})): the fleet runs **{d.armed}**"
                             + (f"; {len(d.unarmed)} newer set(s) unarmed for {d.age_hours:.1f} h, inside the bound."
                                if d.unarmed else "; no promoted set is newer.") + " Closing.")
        gh.edit(p.target, state="closed", state_reason="completed")
        return f"armed — #{p.target} closed"
    return ("not stale — nothing open, nothing to do (" + (f"{len(d.unarmed)} unarmed, oldest {d.age_hours:.1f} h"
            if d.unarmed else "no promoted set newer than the armed one") + ")")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--reading", help="the JSON arm-promoted-set.py select wrote with --unarmed-out")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    env = os.environ.get
    try:
        if not a.reading:
            raise Red("--reading is required")
        try:
            with open(a.reading) as f:
                reading = json.load(f)
        except (OSError, ValueError) as e:
            raise Red(f"the unarmed reading {a.reading} cannot be read ({e}) — `select` wrote none, so "
                      "nothing can be said about the fleet; refusing to call that armed") from None
        threshold = parse_threshold(env("FLEET_UNARMED_ALERT_HOURS"))
        token, repo = env("GH_TOKEN") or env("GITHUB_TOKEN") or "", env("GITHUB_REPOSITORY") or ""
        if not token or not repo:
            raise Red("GH_TOKEN / GITHUB_REPOSITORY are empty — cannot read or write the alert issue")
        now = datetime.now(timezone.utc)
        d = decide(reading, now, threshold)
        run_url = env("RUN_URL") or ""
        gh = GitHub(repo, token)
        gh.ensure_label()
        p = plan(d, gh.open_labelled())
        body = render_body(d, threshold, run_url, now) if d.stale else ""
        said = apply(gh, d, p, body, run_url)
    except Red as e:
        print(f"::error title=Fleet-unarmed alert::{e}")
        return 1
    print(f"fleet-unarmed: {said}")
    if d.stale:
        print(f"::warning title=Fleet unarmed for {d.age_hours:.1f} h::{len(d.unarmed)} promoted set(s) newer than "
              f"the armed {d.armed}; oldest {d.oldest['v_portal']} ({d.oldest['state']}) — see the `{LABEL}` issue")
    summary = env("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a") as f:
            f.write(f"### Fleet-unarmed alert (threshold {threshold:g} h): {said}\n")
    return 0


# ───────────────────────────────── self-test ──────────────────────────────────────────────

class FakeGitHub:
    def __init__(self, issues: list[Issue] | None = None):
        self.issues = {i.number: i for i in (issues or [])}
        self.comments: list[tuple[int, str]] = []
        self.next = 100

    def open_labelled(self):
        return [i for i in self.issues.values() if i.state == "open" and LABEL in i.labels]

    def create(self, body):
        self.next += 1
        self.issues[self.next] = Issue(self.next, TITLE, "open", body, (LABEL,), BOT_LOGIN, "Bot")
        return self.next

    def edit(self, number, **fields):
        i = self.issues[number]
        self.issues[number] = Issue(i.number, i.title, fields.get("state", i.state), fields.get("body", i.body),
                                    i.labels, i.author, i.author_type)

    def comment(self, number, body):
        self.comments.append((number, body))


def self_test() -> int:
    failures = 0

    def check(name: str, ok: bool, detail: str = "") -> None:
        nonlocal failures
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ("" if ok else f" — {detail}"))
        failures += 0 if ok else 1

    now = datetime(2026, 10, 9, 18, 0, tzinfo=timezone.utc)

    def at(hours_ago: float) -> str:
        return (now - timedelta(hours=hours_ago)).strftime("%Y-%m-%dT%H:%M:%SZ")

    def s(n, state, hours_ago, sentence="its compatibility ladder has not finished"):
        return {"v_portal": f"3.1.{n}", "run_number": n, "core_sha": f"{n:07d}" + "a" * 33, "state": state,
                "sentence": sentence, "run_url": f"https://github.com/o/r/actions/runs/{n}",
                "promoted_at": at(hours_ago)}

    def reading(*sets, armed="3.1.10000"):
        return {"armed": armed, "unarmed": list(sets)}

    def go(gh, r, hours=6.0):
        d = decide(r, now, hours)
        p = plan(d, gh.open_labelled())
        return d, p, apply(gh, d, p, render_body(d, hours, "https://run/1", now) if d.stale else "", "https://run/1")

    # 1. stale → opens ONE issue naming every unarmed set, its state and its run
    refused = s(10001, "refused", 7.5, "its compatibility ladder ended `failure` — fix compatibility")
    waiting = s(10003, "waiting", 1.0, "control runs abc, which does not contain 10003 yet")
    gh = FakeGitHub()
    d, p, said = go(gh, reading(waiting, refused))
    body = next(iter(gh.issues.values())).body if gh.issues else ""
    check("stale (oldest unarmed 7.5 h > 6 h) OPENS one issue", p.action == "create" and len(gh.issues) == 1, said)
    check("the age is measured from the OLDEST unarmed set, never the newest",
          d.oldest["run_number"] == 10001 and 7.4 < (d.age_hours or 0) < 7.6, str(d.age_hours))
    check("the body names the REFUSED set with its state, sentence and run link",
          "`3.1.10001`" in body and "**refused**" in body and "ended `failure`" in body
          and "actions/runs/10001" in body, body)
    check("the body names the WAITING set with its state and run link",
          "`3.1.10003`" in body and "**waiting**" in body and "actions/runs/10003" in body, body)
    check("refused and waiting are told apart (each set carries its OWN state)",
          [ln for ln in body.splitlines() if "3.1.10001" in ln and "**refused**" in ln]
          and [ln for ln in body.splitlines() if "3.1.10003" in ln and "**waiting**" in ln]
          and not [ln for ln in body.splitlines() if "3.1.10003" in ln and "**refused**" in ln], body)
    check("the issue carries the ownership mark and the newest armed set", MARK in body and "3.1.10000" in body)
    num = next(iter(gh.issues))

    # 2. still stale, same reading → body rewritten, NO comment (no noise per tick)
    d, p, said = go(gh, reading(waiting, refused))
    check("still stale, unchanged reading → rewrite, no comment", p.action == "update" and not gh.comments, said)
    check("never a second issue while one is open", len(gh.issues) == 1)
    # 3. still stale, reading changed (a new set / a state flip) → one comment
    d, p, said = go(gh, reading(s(10005, "waiting", 0.2), waiting, refused))
    check("still stale, CHANGED reading → rewrite AND one comment", p.action == "update" and len(gh.comments) == 1, said)

    # 4. fresh (the fleet armed again: nothing newer, or newer sets inside the bound) → clears
    d, p, said = go(gh, reading(s(10007, "waiting", 0.5), armed="3.1.10006"))
    check("armed again (oldest unarmed 0.5 h) CLOSES the issue",
          p.action == "close" and gh.issues[num].state == "closed", said)
    check("the closing comment names the armed set", "3.1.10006" in gh.comments[-1][1], gh.comments[-1][1])
    d, p, said = go(gh, reading(armed="3.1.10007"))
    check("fresh and nothing open → no-op", p.action == "none" and len(gh.issues) == 1, said)

    # 5. NEGATIVE CONTROL on the threshold — just inside it is NOT stale; just past it IS
    check("negative control: 5.9 h with a 6 h threshold is NOT stale",
          decide(reading(s(1, "refused", 5.9)), now, 6.0).stale is False)
    check("positive twin: 6.1 h with a 6 h threshold IS stale", decide(reading(s(1, "refused", 6.1)), now, 6.0).stale)
    check("the threshold is the knob: the same 6.1 h is NOT stale at 12 h",
          decide(reading(s(1, "refused", 6.1)), now, 12.0).stale is False)
    check("nothing unarmed is never stale, however old the armed set", decide(reading(), now, 6.0).stale is False)

    # 6. ownership — a mechanism may only touch an issue it opened
    foreign = [Issue(7, TITLE, "open", "a human wrote this " + MARK, (LABEL,), "rbuergi", "User"),
               Issue(8, TITLE, "open", "no mark", (LABEL,), BOT_LOGIN, "Bot")]
    gh = FakeGitHub(foreign)
    d, p, said = go(gh, reading(refused))
    check("an issue without the mark, or not authored by the bot, is never adopted (a fresh one is filed)",
          p.action == "create" and gh.issues[7].body.startswith("a human") and gh.issues[8].body == "no mark", said)
    gh = FakeGitHub(foreign)
    d, p, said = go(gh, reading(armed="3.1.10009"))
    check("…and never closed when the fleet is armed again",
          p.action == "none" and gh.issues[7].state == "open" and gh.issues[8].state == "open", said)

    # 7. duplicates fold into the OLDEST
    mine = lambda n: Issue(n, TITLE, "open", "x " + MARK, (LABEL,), BOT_LOGIN, "Bot")  # noqa: E731
    gh = FakeGitHub([mine(21), mine(20)])
    d, p, said = go(gh, reading(refused))
    check("two owned open issues → the oldest survives, the newer is closed as a duplicate",
          p.target == 20 and gh.issues[21].state == "closed" and gh.issues[20].state == "open", said)

    # 8. it cannot pass silently
    for bad, why in (({"unarmed": []}, "no armed set named"),
                     ({"armed": "3.1.1"}, "no unarmed list"),
                     (reading({**refused, "promoted_at": ""}), "a set without promoted_at"),
                     (reading({**refused, "state": "green"}), "an unknown state"),
                     (reading({**refused, "promoted_at": "yesterday"}), "an unreadable promoted_at")):
        try:
            decide(bad, now, 6.0)
            check(f"a malformed reading ({why}) is RED", False)
        except Red:
            check(f"a malformed reading ({why}) is RED, never a pass", True)
    for raw in ("", None, "six", "0", "-1"):
        try:
            parse_threshold(raw)
            check(f"threshold {raw!r} is RED", False)
        except Red:
            check(f"threshold {raw!r} is RED, never a silent default", True)
    check("threshold '6' reads as 6 h", parse_threshold("6") == 6.0)

    print(f"fleet-unarmed-alert self-test: {failures} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
