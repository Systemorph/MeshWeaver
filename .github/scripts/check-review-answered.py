#!/usr/bin/env python3
"""check-review-answered.py — a pull request is RED until the automatic review has landed and every thread it opened has a human reply.

(The name on this first line is load-bearing, like every central guard script here: a lane that
fetches this file checks its first 400 bytes name it.)

WHY THIS EXISTS (MeshWeaver#4299, option B, decided by the maintainer 2026-09-17)
--------------------------------------------------------------------------------
The ruleset's `copilot_code_review` rule reviews every pull request into `main`, and `auto-arm.yml`
arms every pull request for the merge queue the moment it opens. Nothing joined the two, so a
review was ADVISORY on exactly the pull requests whose checks are fast:

  * #4310 merged with 14 findings and 0 replies; one of the behaviours it changed reddened a
    dependent repository's suite and stopped the fleet's publication for six hours.
  * 9 of the 31 merges between 2026-09-14T18:00Z and 09-15T07:5xZ carried unanswered findings
    (31 findings, 23 of them still unanswered a day later).
  * #4383 lost the fix for its three findings to the merge by about a minute.

Every one of those reviews had LANDED before the merge; it had not been READ. So the check asks
the question the merge never asked: has each finding been answered?

THE RULE (all three must hold, or the check is RED — it never skips)
--------------------------------------------------------------------
1. The automatic review has LANDED. A review by the reviewer account counts only when its body is
   recognisably a review. The reviewer posts its REFUSALS under the same account and in the same
   endpoint — measured on #645–#654 (2026-07-2x): "Copilot was unable to review this pull request
   because the user who requested the review has reached their quota limit." — so "a review by the
   bot exists" would read a quota outage as "reviewed". A body that is neither a recognised review
   nor a recognised refusal is RED as well: the check cannot tell, and cannot-tell is never a pass.
2. Every inline thread the reviewer STARTED (a comment by the reviewer with no `in_reply_to_id`)
   has at least one reply by a non-bot account (`user.type == "User"`). A reply that says nothing
   counts; that limitation is known and accepted (the decision on #4299).
3. The inputs were read completely. Any failed read is RED, and the comment listing must not be
   shorter than the `review_comments` count the pull request reported BEFORE the listing began
   (measured 2026-09-17: listing == count on 60 of 60 recent pull requests).

`--wait-for-review MINUTES` re-reads while condition 1 is the ONLY thing missing, because the
reviewer's own review event cannot start a run in this repository (measured; see
`waiting_would_help`). Nothing else is ever waited for, and the wait ends RED.

A maintainer WAIVER releases condition 1 and nothing else: the label `review-waived`, attributed
through the REST issue-events API to the account that applied it, and honoured only when that
account's `role_name` on this repository is `admin` or `maintain`. Threads the reviewer DID open
still need replies under a waiver. The waiver is never automatic.

THE REVIEWER, MEASURED (2026-09-17, 50 merged pull requests, #4487–#4568)
----------------------------------------------------------------------
The reviewer posts under TWO logins with ONE account id:
  pulls/{n}/reviews    `copilot-pull-request-reviewer[bot]`  type Bot  id 175728472
  pulls/{n}/comments   `Copilot`                             type Bot  id 175728472
It posted exactly one review per pull request on all 50 (the ruleset carries
`review_on_push: false`), always at state COMMENTED. Recognised review bodies carry the heading
"Pull request overview" — in July as `## Pull request overview`, since September inside
`<summary>Pull request overview</summary>` under a verdict line (🟡/🟢/🔵).

USAGE
-----
  check-review-answered.py --self-test
  check-review-answered.py --repo O/R --pr N [--as-of 2026-09-14T14:04:21Z]
  check-review-answered.py --repo O/R --merge-group-ref refs/heads/gh-readonly-queue/main/pr-N-<sha>

`--as-of` evaluates the pull request as it stood at that instant (reviews, comments and waiver
events created later are ignored) — the controls in Doc/Architecture/ReviewFindingsAnswered use it
to replay a merge. Every GitHub call is `gh api` (REST) with the token in GH_TOKEN. Exit 0 = GREEN,
1 = RED, 2 = usage error.
"""
from __future__ import annotations

import argparse
import dataclasses
import datetime
import json
import os
import re
import subprocess
import sys
import time

REVIEWER_ACCOUNT_ID = 175728472
REVIEWER_LOGINS = frozenset({"copilot-pull-request-reviewer[bot]", "Copilot"})
REVIEW_MARKER = re.compile(r"Pull request overview", re.IGNORECASE)
REFUSAL_MARKERS = (
    re.compile(r"\bCopilot (?:was unable|wasn't able|was not able|could not|couldn't|cannot|can't) (?:to )?review\b", re.IGNORECASE),
    re.compile(r"\bunable to review this pull request\b", re.IGNORECASE),
    re.compile(r"\*\*Files reviewed:\*\*\s*0\s*/", re.IGNORECASE),
)
WAIVER_LABEL = "review-waived"
WAIVER_ROLES = frozenset({"admin", "maintain"})
QUEUE_REF = re.compile(r"^(?:refs/heads/)?gh-readonly-queue/(?P<base>[^/]+)/pr-(?P<pr>[1-9]\d*)-(?P<sha>[0-9a-f]{40})$")


# ─────────────────────────────── the predicate (pure) ───────────────────────────────

def is_reviewer(user: dict | None) -> bool:
    if not user or user.get("type") != "Bot":
        return False
    return user.get("id") == REVIEWER_ACCOUNT_ID or user.get("login") in REVIEWER_LOGINS


def is_person(user: dict | None) -> bool:
    return bool(user) and user.get("type") == "User" and not str(user.get("login", "")).endswith("[bot]")


def first_line(text: str | None) -> str:
    for line in (text or "").splitlines():
        if line.strip():
            return line.strip()[:160]
    return "(empty body)"


def classify_review_body(body: str | None) -> str:
    """'refused' | 'landed' | 'unrecognised'. A refusal marker wins over the review marker."""
    text = body or ""
    if any(m.search(text) for m in REFUSAL_MARKERS):
        return "refused"
    if REVIEW_MARKER.search(text):
        return "landed"
    return "unrecognised"


def not_after(stamp: str | None, as_of: str | None) -> bool:
    """GitHub stamps are ISO-8601 UTC with a Z suffix, so they order lexically."""
    return as_of is None or (stamp is not None and stamp <= as_of)


@dataclasses.dataclass(frozen=True)
class Waiver:
    """What the adapter found about the waiver label. `label_present` is None in --as-of mode,
    where presence is reconstructed from the events alone."""
    label_present: bool | None
    events: tuple  # issue events for the waiver label: ({event, actor{login,type}, created_at, id}, …)
    roles: dict  # login -> role_name, for every person who applied the label


@dataclasses.dataclass(frozen=True)
class Verdict:
    green: bool
    reasons: tuple[str, ...]
    notes: tuple[str, ...]
    unanswered: tuple[dict, ...]


def evaluate(pr: dict, reviews: list, comments: list, waiver: Waiver, as_of: str | None = None) -> Verdict:
    reasons: list[str] = []
    notes: list[str] = []

    # 3 (checked first: an incomplete listing makes every other statement unreliable)
    reported = pr.get("review_comments")
    if not isinstance(reported, int):
        reasons.append("the pull request did not report a `review_comments` count, so the comment listing cannot be proven complete")
    elif len(comments) < reported:
        reasons.append(f"the comment listing returned {len(comments)} comment(s) but the pull request reported {reported} before the listing began — the read is incomplete, so no thread can be called answered")

    # 1 — has the review landed?
    mine = [r for r in reviews
            if is_reviewer(r.get("user")) and r.get("state") != "PENDING" and not_after(r.get("submitted_at"), as_of)]
    kinds = [(classify_review_body(r.get("body")), r) for r in mine]
    landed = [r for k, r in kinds if k == "landed"]
    if landed:
        r = landed[-1]
        notes.append(f"automatic review landed: review {r.get('id')} at {r.get('submitted_at')} on {str(r.get('commit_id'))[:10]}")
    else:
        if not mine:
            why = "the automatic review has not landed — no review by the automatic reviewer on this pull request"
        else:
            parts = [f"review {r.get('id')} at {r.get('submitted_at')} is {'a refusal' if k == 'refused' else 'an unrecognised body'}: \"{first_line(r.get('body'))}\"" for k, r in kinds]
            why = "the automatic review has not landed — the reviewer posted, but not a review: " + "; ".join(parts)
        granted, message = waiver_holder(waiver, as_of)
        if granted:
            notes.append(f"WAIVED: {why}. {message}")
        else:
            reasons.append(why + (f". {message}" if message else ""))

    # 2 — is every thread the reviewer started answered?
    by_id = {c.get("id"): c for c in comments}

    def root_of(c: dict) -> int | None:
        seen = set()
        while c.get("in_reply_to_id") is not None:
            if c["id"] in seen:
                return None
            seen.add(c["id"])
            parent = by_id.get(c["in_reply_to_id"])
            if parent is None:
                return c["in_reply_to_id"]
            c = parent
        return c.get("id")

    visible = [c for c in comments if not_after(c.get("created_at"), as_of)]
    roots = [c for c in visible if c.get("in_reply_to_id") is None and is_reviewer(c.get("user"))]
    answered_roots = {root_of(c) for c in visible if c.get("in_reply_to_id") is not None and is_person(c.get("user"))}
    unanswered = tuple(c for c in roots if c.get("id") not in answered_roots)
    notes.append(f"threads opened by the automatic reviewer: {len(roots)}, answered by a person: {len(roots) - len(unanswered)}")
    if unanswered:
        reasons.append(f"{len(unanswered)} of {len(roots)} thread(s) opened by the automatic reviewer have no reply from a person")

    return Verdict(green=not reasons, reasons=tuple(reasons), notes=tuple(notes), unanswered=unanswered)


def waiver_holder(waiver: Waiver, as_of: str | None) -> tuple[bool, str]:
    """(True, who-waived-and-when) when a maintainer's waiver stands; otherwise (False, why-not, or
    empty when there is no waiver at all)."""
    events = sorted((e for e in waiver.events
                     if e.get("event") in ("labeled", "unlabeled") and not_after(e.get("created_at"), as_of)),
                    key=lambda e: (e.get("created_at") or "", e.get("id") or 0))
    last = events[-1] if events else None
    present = waiver.label_present if waiver.label_present is not None else bool(last and last.get("event") == "labeled")
    if not present:
        return (False, "")
    if not last or last.get("event") != "labeled":
        return (False, f"The `{WAIVER_LABEL}` label is on the pull request but no `labeled` event attributes it to anyone, so it is not honoured — remove and re-apply it")
    actor = last.get("actor") or {}
    login = actor.get("login") or "(unknown)"
    if not is_person(actor):
        return (False, f"The `{WAIVER_LABEL}` label was applied by @{login}, which is not a person — a waiver is a maintainer's decision and is never automatic")
    role = waiver.roles.get(login)
    if role not in WAIVER_ROLES:
        return (False, f"The `{WAIVER_LABEL}` label was applied by @{login}, whose role on this repository is `{role or 'unknown'}` — only `admin` or `maintain` may waive the review")
    return (True, f"Released by the `{WAIVER_LABEL}` label, applied by @{login} (role `{role}`) at {last.get('created_at')}; "
                  "the waiver releases this condition only — every thread the reviewer opened still needs a reply")


def waiting_would_help(verdict: Verdict) -> bool:
    """True when the ONLY thing missing is the reviewer's review — an input that ARRIVES, with no
    action by anyone on the pull request. Everything else (an unanswered thread, an unreadable
    listing) is not waited for: the first needs a person, the second needs another read.

    🚨 WHY A WAIT EXISTS AT ALL — measured on #4575 (2026-09-17T08:05Z, run 35197843933). The
    reviewer's own `pull_request_review` event DOES reach this repository, and GitHub creates a
    workflow run for it — with conclusion `action_required` and ZERO jobs, because the triggering
    actor is `Copilot` and the repository requires approval for runs triggered by a first-time
    contributor. So the event that says "the review has landed" cannot start an evaluation, and a
    pull request whose review raises NO findings would otherwise keep the red from its `opened`
    evaluation until somebody pushed again. The wait is bounded, it ends RED, and it is the only
    reason this check does not need a person to press anything.
    """
    return (not verdict.green
            and len(verdict.reasons) == 1
            and "has not landed" in verdict.reasons[0])


UNANSWERED_REASON = "no reply from a person"


def newest_person_reply(comments: list, as_of: str | None = None) -> str | None:
    """The ISO-8601 stamp of the most recent REPLY written by a person, or None if there is none.

    A reply, not any comment: the reviewer's own root comments are the findings, and their arrival
    is not evidence that anybody is answering."""
    stamps = [c.get("created_at") for c in comments
              if c.get("in_reply_to_id") is not None
              and is_person(c.get("user"))
              and not_after(c.get("created_at"), as_of)]
    stamps = [s for s in stamps if s]
    return max(stamps) if stamps else None


def replies_still_landing(verdict: Verdict, comments: list, settle_seconds: int,
                          as_of: str | None = None, now: str | None = None) -> bool:
    """True when the ONLY thing wrong is unanswered threads AND a person answered one moments ago —
    i.e. this evaluation is a snapshot of a state somebody is still writing.

    🚨 <b>WHY THIS WAIT EXISTS, and why it is not a bound raised to make a red go away</b>
    (MeshWeaver#4299 follow-up; measured on #4649, 2026-09-17).

    Answering N findings posts N separate `pull_request_review_comment` events, so a pull request
    with seven findings fired this workflow eight times on ONE head sha and published eight
    check-runs: six failures while replies were still being written, then two successes. That is not
    a defect in the verdict — each of those reds was TRUE when it was taken — but the pull request
    was then unmergeable, because GitHub's `statusCheckRollup` latched:

        head 94fc73b4fdaa, measured 57 minutes after the last evaluation completed
          rollup state: FAILURE
          it carries 3 of the 8 `Automatic review answered` check-runs — 105349138004,
          105356428131, 105356792897 — ALL FAILURES. Neither success (105356708655,
          105356763402) appears in the rollup at all.

    Eight runs, eight distinct check SUITES, and the rollup's selection is neither the newest, nor
    the oldest, nor one-per-suite. So a later success on the same sha CANNOT be relied on to
    displace an earlier failure, the rollup does not heal with time, and re-arming auto-merge does
    not clear it. Only a new head does — which is why the remedy discovered under pressure that
    night was an empty commit, and why that remedy is written down in
    Doc/Architecture/ReviewFindingsAnswered rather than left to be rediscovered.

    The durable fix therefore cannot be "make the last evaluation win" — we do not control the
    rollup's choice. It has to be <b>make every evaluation on one sha agree</b>, and they disagree
    for exactly one reason: the question is being asked while the answer is being typed. So the
    evaluation waits for the answering to STOP and then judges once, which changes no verdict — an
    unanswered thread that stays unanswered is still red when the wait ends, and the wait is bounded
    and always ends in a verdict. Same shape, and the same justification, as `waiting_would_help`
    above.

    🚨 It waits ONLY while the unanswered threads are the whole complaint. An incomplete comment
    listing or a review that never landed is not a state anybody is mid-way through fixing, and
    delaying those would be the bound-raising this repository forbids.

    <b>Why 60 seconds.</b> Measured over the three pull requests that answered a review that night —
    #4649 (7 replies), #4656 (5) and #4646 (3) — every gap INSIDE an answering burst was 1–11 s
    (#4649: 8, 10, 8, 10, 10, 9). The one long gap in the sample, 954 s on #4656, was a separate
    later round of work rather than a pause in a burst, and deliberately falls outside this window:
    that round gets its own evaluation, as it should. 60 s is ~5× the widest measured intra-burst
    gap and keeps the job inside its 20-minute cap even stacked on the 15-minute review wait.

    <b>It also collapses the burst, through the concurrency group already in the workflow.</b> The
    eight runs above all executed because each finished in ~8 s, so the pending slot was free again
    before the next event arrived. While one run holds the slot for the settle window, every further
    arrival REPLACES the pending one — and a replaced run executes zero jobs, so it publishes NO
    check-run at all and nothing of it can reach the rollup.
    """
    if settle_seconds <= 0 or verdict.green or not verdict.reasons:
        return False
    if any(UNANSWERED_REASON not in r for r in verdict.reasons):
        return False
    newest = newest_person_reply(comments, as_of)
    if newest is None:
        return False
    age = reply_age_seconds(newest, now)
    return age is not None and 0 <= age < settle_seconds


def reply_age_seconds(stamp: str, now: str | None = None) -> float | None:
    """Seconds between `stamp` and now (or `now`, for the self-test). None if either is unreadable —
    an unreadable stamp must never be read as "settled", so the caller treats None as "do not wait"
    and the verdict is published as it stands, which is the conservative direction."""
    try:
        then = datetime.datetime.strptime(stamp, "%Y-%m-%dT%H:%M:%SZ")
        current = (datetime.datetime.strptime(now, "%Y-%m-%dT%H:%M:%SZ") if now
                   else datetime.datetime.now(datetime.UTC).replace(tzinfo=None))
    except (ValueError, TypeError):
        return None
    return (current - then).total_seconds()


def pr_from_queue_ref(ref: str) -> int:
    m = QUEUE_REF.fullmatch(ref or "")
    if not m:
        raise ValueError(f"not a merge-queue ref: {ref!r} (expected gh-readonly-queue/<base>/pr-<N>-<40-hex sha>)")
    return int(m.group("pr"))


# ─────────────────────────────── GitHub adapter (REST only) ───────────────────────────────

class ReadError(RuntimeError):
    pass


class Gh:
    def __init__(self, repo: str):
        self.repo = repo

    def api(self, path: str, paginate: bool = False):
        cmd = ["gh", "api", "-H", "Accept: application/vnd.github+json", f"repos/{self.repo}/{path}"]
        if paginate:
            cmd[2:2] = ["--paginate", "--slurp"]
        p = subprocess.run(cmd, capture_output=True, text=True, check=False)
        if p.returncode != 0:
            raise ReadError(f"GET repos/{self.repo}/{path} failed ({p.returncode}): {p.stderr.strip()[:600]}")
        try:
            data = json.loads(p.stdout)
        except json.JSONDecodeError as e:
            raise ReadError(f"GET repos/{self.repo}/{path} did not return JSON: {e}") from e
        if paginate:
            if not isinstance(data, list) or not all(isinstance(page, list) for page in data):
                raise ReadError(f"GET repos/{self.repo}/{path} did not return a list of pages")
            data = [item for page in data for item in page]
        return data

    def role(self, login: str) -> str:
        data = self.api(f"collaborators/{login}/permission")
        return (data or {}).get("role_name") or ""


def read_inputs(gh: Gh, number: int, as_of: str | None):
    pr = gh.api(f"pulls/{number}")  # FIRST: its review_comments count bounds the listing below
    if not isinstance(pr, dict) or pr.get("number") != number:
        raise ReadError(f"pulls/{number} did not return pull request #{number}")
    reviews = gh.api(f"pulls/{number}/reviews?per_page=100", paginate=True)
    comments = gh.api(f"pulls/{number}/comments?per_page=100", paginate=True)
    events = tuple(e for e in gh.api(f"issues/{number}/events?per_page=100", paginate=True)
                   if e.get("event") in ("labeled", "unlabeled") and (e.get("label") or {}).get("name") == WAIVER_LABEL)
    applied_by = {(e.get("actor") or {}).get("login") for e in events
                  if e.get("event") == "labeled" and is_person(e.get("actor"))}
    roles = {login: gh.role(login) for login in sorted(x for x in applied_by if x)}
    present = None if as_of else any(l.get("name") == WAIVER_LABEL for l in pr.get("labels") or [])
    # The waiver path is read on EVERY run, not only when the label is on: a token that cannot read
    # collaborator roles would otherwise be discovered at the moment a maintainer needs the waiver.
    author = pr.get("user") or {}
    author_role = gh.role(author["login"]) if is_person(author) else None
    return pr, reviews, comments, Waiver(present, events, roles), author_role


def render(number: int, pr: dict, verdict: Verdict, author_role: str | None, as_of: str | None) -> str:
    head = f"#{number} ({'draft' if pr.get('draft') else pr.get('state')}) head {str((pr.get('head') or {}).get('sha'))[:10]}"
    lines = [f"check-review-answered: {head}{' as of ' + as_of if as_of else ''} — {'GREEN' if verdict.green else 'RED'}"]
    if author_role is not None:
        lines.append(f"  waiver path readable: author @{(pr.get('user') or {}).get('login')} holds `{author_role or 'none'}`")
    lines += [f"  {n}" for n in verdict.notes]
    for r in verdict.reasons:
        lines.append(f"::error::{r}")
    for c in verdict.unanswered:
        where = f"{c.get('path')}:{c.get('line') or c.get('original_line') or '?'}"
        lines.append(f"  unanswered: {c.get('html_url') or c.get('id')} ({where}) — {first_line(c.get('body'))}")
    lines += [f"  To go green: {g}" for g in guidance(verdict)]
    if not verdict.green:
        lines.append("  Reference: Doc/Architecture/ReviewFindingsAnswered")
    return "\n".join(lines)


def guidance(verdict: Verdict) -> list[str]:
    """What turns THIS red green — one line per reason, so the reader is never told to reply to
    threads that do not exist or to wait for a review that has already landed."""
    out: list[str] = []
    text = "\n".join(verdict.reasons)
    if "has not landed" in text:
        out.append("the automatic review must land. It usually arrives minutes after the pull request opens; if the "
                   "reviewer refused (quota) or cannot review this change, a maintainer re-requests the review, or applies "
                   f"the `{WAIVER_LABEL}` label to waive it — never an agent, and never automatically.")
    if verdict.unanswered:
        out.append("reply to each unanswered thread (fixed, or why not). Resolving a thread is not a reply, and a waiver "
                   "does not release a finding the reviewer did post.")
    if "comment listing" in text:
        out.append("nothing on the pull request needs to change — the check could not prove it read every comment; "
                   "re-run this check once the read can complete.")
    return out


def summary_markdown(number: int, verdict: Verdict) -> str:
    out = [f"### Automatic review answered — #{number}: {'✅ green' if verdict.green else '❌ red'}", ""]
    out += [f"- {r}" for r in verdict.reasons] + [f"- {n}" for n in verdict.notes]
    guide = guidance(verdict)
    if guide:
        out += ["", "**To go green:**", ""] + [f"- {g}" for g in guide] + ["- reference: `Doc/Architecture/ReviewFindingsAnswered`"]
    if verdict.unanswered:
        out += ["", "| Unanswered finding | Where |", "|---|---|"]
        for c in verdict.unanswered:
            text = first_line(c.get("body")).replace("|", "\\|").replace("<", "&lt;").replace("[", "\\[").replace("]", "\\]")
            out.append(f"| [{text}]({c.get('html_url')}) | `{c.get('path')}:{c.get('line') or c.get('original_line') or '?'}` |")
    return "\n".join(out) + "\n"


POLL_SECONDS = 30
# The settle wait polls faster than the review wait because the thing it is waiting for is seconds
# away, not minutes: the measured gap inside an answering burst is 1–11 s (see replies_still_landing).
SETTLE_POLL_SECONDS = 10
# …and it is capped at this many settle windows in total, so a person answering steadily for longer
# than that is treated as a separate round of work rather than one very long burst. At the workflow's
# 60 s window that is 3 minutes, which stacked on the 15-minute review wait stays inside the job's
# 20-minute cap with room to spare.
SETTLE_ROUNDS = 3


def run(repo: str, number: int, as_of: str | None, wait_minutes: int = 0,
        settle_seconds: int = 0) -> int:
    gh = Gh(repo)
    deadline = time.monotonic() + wait_minutes * 60
    # The settle wait gets its OWN cap, so it can never be spent twice or chain behind the review
    # wait into the job's 20-minute timeout: a person answering steadily for longer than this is a
    # separate round of work and gets its own evaluation.
    settle_deadline = time.monotonic() + max(settle_seconds, 0) * SETTLE_ROUNDS
    settled_for = 0.0
    while True:
        try:
            pr, reviews, comments, waiver, author_role = read_inputs(gh, number, as_of)
        except (ReadError, KeyError) as e:
            print(f"::error::check-review-answered cannot read the review of #{number}, so it cannot say it was answered: {e}")
            return 1
        verdict = evaluate(pr, reviews, comments, waiver, as_of)
        left = deadline - time.monotonic()
        if waiting_would_help(verdict) and left > POLL_SECONDS:
            print(f"  the automatic review has not landed yet; waiting up to {int(left)}s more for it "
                  f"(its own event cannot start a run here — see waiting_would_help)", flush=True)
            time.sleep(POLL_SECONDS)
            continue
        if replies_still_landing(verdict, comments, settle_seconds, as_of) \
                and time.monotonic() < settle_deadline:
            newest = newest_person_reply(comments, as_of)
            age = reply_age_seconds(newest) or 0
            print(f"  a person replied {int(age)}s ago and {len(verdict.unanswered)} thread(s) are "
                  f"still unanswered — the answering is in progress, so this evaluation would be a "
                  f"snapshot of a moving target. Waiting for {settle_seconds}s of quiet before "
                  f"judging (see replies_still_landing).", flush=True)
            time.sleep(SETTLE_POLL_SECONDS)
            settled_for += SETTLE_POLL_SECONDS
            continue
        break
    if wait_minutes and waiting_would_help(verdict):
        print(f"  waited {wait_minutes} minute(s) for the automatic review and it did not land", flush=True)
    if settled_for:
        print(f"  waited {int(settled_for)}s for the replies to settle; judging the state as it now "
              f"stands — the verdict below is not softened by that wait", flush=True)
    print(render(number, pr, verdict, author_role, as_of))
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(summary_markdown(number, verdict))
    return 0 if verdict.green else 1


# ─────────────────────────────── self-test ───────────────────────────────

REVIEWER_REVIEW_USER = {"login": "copilot-pull-request-reviewer[bot]", "type": "Bot", "id": REVIEWER_ACCOUNT_ID}
REVIEWER_COMMENT_USER = {"login": "Copilot", "type": "Bot", "id": REVIEWER_ACCOUNT_ID}
PERSON = {"login": "rbuergi", "type": "User", "id": 6334612}
OTHER_BOT = {"login": "github-actions[bot]", "type": "Bot", "id": 41898282}
REVIEW_BODY_SEPT = "### 🟡 Changes recommended\n\n<details>\n<summary>Pull request overview</summary>\n\n- **Files reviewed:** 3/3 changed files\n</details>"
REVIEW_BODY_JULY = "## Pull request overview\n\nThis PR adjusts the Distributed portal's mesh-level content-collection…"
REFUSAL_QUOTA = "Copilot was unable to review this pull request because the user who requested the review has reached their quota limit."
REFUSAL_NO_FILES = "### 🔵 Needs a closer look\n\n<summary>Pull request overview</summary>\n\n- **Files reviewed:** 0/4 changed files"


def _review(body=REVIEW_BODY_SEPT, at="2026-09-14T12:22:44Z", user=REVIEWER_REVIEW_USER, state="COMMENTED", rid=1):
    return {"id": rid, "user": user, "state": state, "submitted_at": at, "body": body, "commit_id": "753e9dd58c" + "0" * 30}


def _comment(cid, user=REVIEWER_COMMENT_USER, reply_to=None, at="2026-09-14T12:22:40Z"):
    return {"id": cid, "user": user, "in_reply_to_id": reply_to, "created_at": at, "body": f"finding {cid}",
            "path": "src/X.cs", "line": cid, "html_url": f"https://github.com/o/r/pull/1#discussion_r{cid}"}


def _pr(count, labels=()):
    return {"number": 1, "review_comments": count, "labels": [{"name": n} for n in labels], "state": "open",
            "draft": False, "head": {"sha": "a" * 40}, "user": PERSON}


def _labeled(actor, at="2026-09-14T13:00:00Z", event="labeled", eid=1):
    return {"id": eid, "event": event, "actor": actor, "created_at": at, "label": {"name": WAIVER_LABEL}}


NO_WAIVER = Waiver(False, (), {})


def self_test() -> int:
    """Every case names the EXACT reasons it must be red for, in evaluation order (completeness,
    landed, threads). A case that goes red for a different reason — or for an extra one — fails,
    so a fixture cannot pass by being wrong in a second way."""
    failures = 0
    LISTING, NOT_LANDED, UNANSWERED = "comment listing", "has not landed", "have no reply from a person"

    def case(name: str, expect: tuple[str, ...], pr, reviews, comments, waiver=NO_WAIVER, as_of=None, mention: tuple[str, ...] = ()):
        nonlocal failures
        v = evaluate(pr, reviews, comments, waiver, as_of)
        text = "\n".join(v.reasons + v.notes)
        ok = (v.green == (not expect) and len(v.reasons) == len(expect)
              and all(e in r for e, r in zip(expect, v.reasons)) and all(m in text for m in mention))
        failures += 0 if ok else 1
        want = "green" if not expect else "red: " + " + ".join(expect)
        got = "green" if v.green else "red: " + " | ".join(v.reasons)
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={want}" + ("" if ok else f"\n               got={got}\n               notes={v.notes}"))

    three = [_comment(1), _comment(2), _comment(3)]
    answers = [_comment(11, PERSON, 1, "2026-09-14T13:00:00Z"), _comment(12, PERSON, 2, "2026-09-14T13:01:00Z"),
               _comment(13, PERSON, 3, "2026-09-14T13:02:00Z")]
    GREEN: tuple[str, ...] = ()

    # condition 1 — has the review landed?
    case("no review at all", (NOT_LANDED,), _pr(0), [], [])
    case("review landed, no findings (September body)", GREEN, _pr(0), [_review()], [], mention=("automatic review landed",))
    case("review landed, no findings (July body)", GREEN, _pr(0), [_review(REVIEW_BODY_JULY)], [])
    case("quota refusal is not a review (#645 body)", (NOT_LANDED,), _pr(0), [_review(REFUSAL_QUOTA)], [],
         mention=("is a refusal", "reached their quota limit"))
    case("zero files reviewed is not a review", (NOT_LANDED,), _pr(0), [_review(REFUSAL_NO_FILES)], [])
    case("an unrecognised body cannot be called a review", (NOT_LANDED,), _pr(0), [_review("Something new happened.")], [],
         mention=("unrecognised body",))
    case("an empty body cannot be called a review", (NOT_LANDED,), _pr(0), [_review("")], [])
    case("refusal, then a real re-review", GREEN, _pr(0),
         [_review(REFUSAL_QUOTA, rid=1), _review(rid=2, at="2026-09-14T14:00:00Z")], [])
    case("a PENDING reviewer review does not count", (NOT_LANDED,), _pr(0), [_review(state="PENDING")], [])
    case("another bot's overview-shaped review does not count", (NOT_LANDED,), _pr(0), [_review(user=OTHER_BOT)], [])
    case("a person's review does not stand in for the automatic one", (NOT_LANDED,), _pr(0), [_review(user=PERSON)], [])
    case("the reviewer is known by account id after a login rename", GREEN, _pr(0),
         [_review(user={"login": "copilot-reviewer-v2[bot]", "type": "Bot", "id": REVIEWER_ACCOUNT_ID})], [])
    case("a User account named like the reviewer is not the reviewer", (NOT_LANDED,), _pr(0),
         [_review(user={"login": "Copilot", "type": "User", "id": 1})], [])

    # condition 2 — is every thread the reviewer opened answered by a person?
    case("#4310 shape: 3 findings, 0 replies", (UNANSWERED,), _pr(3), [_review()], three, mention=("3 of 3",))
    case("every finding answered by a person", GREEN, _pr(6), [_review()], three + answers)
    case("one finding of three unanswered", (UNANSWERED,), _pr(5), [_review()], three + answers[:2], mention=("1 of 3",))
    case("a reply by another bot does not answer", (UNANSWERED,), _pr(2), [_review()],
         [_comment(1), _comment(11, OTHER_BOT, 1)])
    case("the reviewer replying to itself does not answer", (UNANSWERED,), _pr(2), [_review()],
         [_comment(1), _comment(11, REVIEWER_COMMENT_USER, 1)])
    case("two replies on one thread leave the other unanswered", (UNANSWERED,), _pr(4), [_review()],
         [_comment(1), _comment(2), _comment(11, PERSON, 1), _comment(12, PERSON, 1)])
    case("a person's reply to a reply still answers the root", GREEN, _pr(3), [_review()],
         [_comment(1), _comment(11, REVIEWER_COMMENT_USER, 1), _comment(12, PERSON, 11)])
    case("a thread a person started needs no reply", GREEN, _pr(1), [_review()], [_comment(5, PERSON)])
    case("findings with no landed review are red on BOTH counts", (NOT_LANDED, UNANSWERED), _pr(1), [], [_comment(1)])

    # --as-of replays a merge
    case("as of the merge: the replies came later (#4343 shape)", (UNANSWERED,), _pr(6), [_review()], three + answers,
         as_of="2026-09-14T12:59:00Z")
    case("as of later: the same pull request is answered", GREEN, _pr(6), [_review()], three + answers,
         as_of="2026-09-14T13:30:00Z")
    case("as of before the review: it had not landed", (NOT_LANDED,), _pr(0), [_review()], [], as_of="2026-09-14T12:00:00Z")
    case("a finding posted after as-of is not counted", GREEN, _pr(1), [_review()],
         [_comment(1, at="2026-09-15T00:00:00Z")], as_of="2026-09-14T13:00:00Z")

    # condition 3 — was the input read completely?
    case("listing shorter than the count reported before it", (LISTING,), _pr(7), [_review()], three + answers)
    case("no review_comments count reported", (LISTING,), {"number": 1, "labels": []}, [_review()], [])
    case("listing longer than reported (a comment created mid-read)", GREEN, _pr(0), [_review()], [_comment(5, PERSON)])

    # the waiver releases condition 1 only, and only from a maintainer
    admin = Waiver(True, (_labeled(PERSON),), {"rbuergi": "admin"})
    case("waiver by an admin releases a missing review", GREEN, _pr(0, [WAIVER_LABEL]), [], [], admin,
         mention=("WAIVED", "@rbuergi"))
    case("waiver by a maintainer releases a quota refusal", GREEN, _pr(0, [WAIVER_LABEL]), [_review(REFUSAL_QUOTA)], [],
         Waiver(True, (_labeled(PERSON),), {"rbuergi": "maintain"}), mention=("WAIVED",))
    case("waiver never releases an unanswered finding", (UNANSWERED,), _pr(3, [WAIVER_LABEL]), [_review(REFUSAL_QUOTA)],
         three, admin, mention=("WAIVED",))
    case("waiver by a writer is refused", (NOT_LANDED,), _pr(0, [WAIVER_LABEL]), [], [],
         Waiver(True, (_labeled(PERSON),), {"rbuergi": "write"}), mention=("may waive the review",))
    case("waiver whose applier's role could not be read is refused", (NOT_LANDED,), _pr(0, [WAIVER_LABEL]), [], [],
         Waiver(True, (_labeled(PERSON),), {}), mention=("may waive the review",))
    case("waiver applied by a bot is refused", (NOT_LANDED,), _pr(0, [WAIVER_LABEL]), [], [],
         Waiver(True, (_labeled({"login": "meshweaver-cloud[bot]", "type": "Bot", "id": 9}),), {}),
         mention=("not a person",))
    case("label present with no attributable event is refused", (NOT_LANDED,), _pr(0, [WAIVER_LABEL]), [], [],
         Waiver(True, (), {}), mention=("attributes it",))
    case("label removed: an old labeled event is not a waiver", (NOT_LANDED,), _pr(0), [], [],
         Waiver(False, (_labeled(PERSON),), {"rbuergi": "admin"}))
    case("re-applied by a writer after an admin removed it", (NOT_LANDED,), _pr(0, [WAIVER_LABEL]), [], [],
         Waiver(True, (_labeled(PERSON, eid=1), _labeled(PERSON, "2026-09-14T13:10:00Z", "unlabeled", 2),
                       _labeled({"login": "someone", "type": "User", "id": 7}, "2026-09-14T13:20:00Z", eid=3)),
                {"rbuergi": "admin", "someone": "write"}), mention=("@someone",))
    case("as-of: a waiver applied later does not count", (NOT_LANDED,), _pr(0), [], [],
         Waiver(None, (_labeled(PERSON, "2026-09-15T00:00:00Z"),), {"rbuergi": "admin"}), as_of="2026-09-14T13:00:00Z")
    case("as-of: the waiver in force at that instant counts", GREEN, _pr(0), [], [],
         Waiver(None, (_labeled(PERSON, "2026-09-14T12:00:00Z"),), {"rbuergi": "admin"}), as_of="2026-09-14T13:00:00Z")

    # waiting_would_help — only an ARRIVING input is waited for
    for name, expect, pr_, reviews_, comments_, waiver_ in [
        ("wait: only the review is missing", True, _pr(0), [], [], NO_WAIVER),
        ("no wait: the review refused AND a thread is unanswered", False, _pr(1), [_review(REFUSAL_QUOTA)], [_comment(1)], NO_WAIVER),
        ("no wait: only a thread is unanswered", False, _pr(1), [_review()], [_comment(1)], NO_WAIVER),
        ("no wait: the listing was incomplete", False, _pr(9), [_review()], [], NO_WAIVER),
        ("no wait: already green", False, _pr(0), [_review()], [], NO_WAIVER),
        ("no wait: waived", False, _pr(0, [WAIVER_LABEL]), [], [], Waiver(True, (_labeled(PERSON),), {"rbuergi": "admin"})),
    ]:
        got = waiting_would_help(evaluate(pr_, reviews_, comments_, waiver_))
        ok = got == expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={'wait' if expect else 'no wait'} got={'wait' if got else 'no wait'}")

    # replies_still_landing — wait ONLY while unanswered threads are the whole complaint and a
    # person is visibly mid-answer. Every other red is published at once: delaying those would be
    # the bound-raising this repository forbids, and a predicate that waited for them could hide a
    # genuinely stuck pull request behind a timer.
    NOW = "2026-09-17T19:48:00Z"
    RECENT = "2026-09-17T19:47:55Z"     # 5 s ago — inside a burst (measured gaps are 1–11 s)
    STALE = "2026-09-17T19:38:00Z"      # 10 min ago — nobody is answering now
    for name, expect, pr_, reviews_, comments_, settle_ in [
        ("wait: a thread is unanswered and a person replied 5s ago", True,
         _pr(3), [_review()], [_comment(1), _comment(2), _comment(3, user=PERSON, reply_to=2, at=RECENT)], 60),
        ("no wait: the last reply is 10 minutes old — nobody is answering", False,
         _pr(3), [_review()], [_comment(1), _comment(2), _comment(3, user=PERSON, reply_to=2, at=STALE)], 60),
        ("no wait: nobody has replied at all", False,
         _pr(2), [_review()], [_comment(1), _comment(2)], 60),
        ("no wait: already green", False,
         _pr(2), [_review()], [_comment(1), _comment(2, user=PERSON, reply_to=1, at=RECENT)], 60),
        # 🚨 The two that must NEVER wait, however busy the pull request looks.
        ("no wait: the review never landed, recent reply or not", False,
         _pr(3), [], [_comment(1), _comment(2), _comment(3, user=PERSON, reply_to=2, at=RECENT)], 60),
        ("no wait: the comment listing was incomplete", False,
         _pr(99), [_review()], [_comment(1), _comment(2), _comment(3, user=PERSON, reply_to=2, at=RECENT)], 60),
        # The option off is the option off.
        ("no wait: --settle-replies 0", False,
         _pr(3), [_review()], [_comment(1), _comment(2), _comment(3, user=PERSON, reply_to=2, at=RECENT)], 0),
        # A reviewer comment is not an answer — only a person's REPLY counts as activity.
        ("no wait: the reviewer posted, not a person", False,
         _pr(3), [_review()], [_comment(1), _comment(2), _comment(3, reply_to=2, at=RECENT)], 60),
    ]:
        got = replies_still_landing(evaluate(pr_, reviews_, comments_, NO_WAIVER), comments_, settle_, now=NOW)
        ok = got == expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={'wait' if expect else 'no wait'} got={'wait' if got else 'no wait'}")

    # …and the age arithmetic itself, including the unreadable stamp that must NOT read as settled.
    for name, stamp, expect in [
        ("a stamp 5s old", RECENT, 5.0),
        ("a stamp 10 min old", STALE, 600.0),
        ("an unreadable stamp is None, never 'settled'", "not-a-date", None),
        ("an empty stamp is None", "", None),
    ]:
        got = reply_age_seconds(stamp, now=NOW)
        ok = got == expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={expect} got={got}")

    # the merge-queue ref → pull request number
    sha = "0123456789abcdef0123456789abcdef01234567"
    for name, ref, expect in [
        ("queue ref with refs/heads/", f"refs/heads/gh-readonly-queue/main/pr-4299-{sha}", 4299),
        ("queue ref without refs/heads/", f"gh-readonly-queue/main/pr-7-{sha}", 7),
        ("a short sha is not a queue ref", "refs/heads/gh-readonly-queue/main/pr-7-0123abc", None),
        ("a feature branch is not a queue ref", "refs/heads/feat/pr-7-x", None),
        ("pr-0 is not a pull request", f"refs/heads/gh-readonly-queue/main/pr-0-{sha}", None),
        ("an empty ref", "", None),
    ]:
        try:
            got = pr_from_queue_ref(ref)
        except ValueError:
            got = None
        ok = got == expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={expect} got={got}")

    if failures:
        print(f"::error::check-review-answered.py self-test: {failures} case(s) did not behave — the check is not proven")
        return 1
    print("self-test: every red case went red for exactly its own reason, and every green case went green")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--self-test", action="store_true", help="prove the predicate is non-vacuous and exit")
    ap.add_argument("--repo", default=os.environ.get("GH_REPO"), help="owner/name (default: $GH_REPO)")
    ap.add_argument("--pr", help="pull request number")
    ap.add_argument("--merge-group-ref", help="a merge-queue head ref; the pull request number is read from it")
    ap.add_argument("--as-of", help="evaluate as of this ISO-8601 UTC instant (e.g. a merged_at)")
    ap.add_argument("--settle-replies", type=int, default=0, metavar="SECONDS",
                    help="while unanswered threads are the ONLY complaint and a person replied less "
                         "than this long ago, wait for that many seconds of quiet before judging — so "
                         "every evaluation on one head sha reads the same settled state and they "
                         "cannot disagree (see replies_still_landing)")
    ap.add_argument("--wait-for-review", type=int, default=0, metavar="MINUTES",
                    help="while the ONLY thing missing is the reviewer's review, re-read for up to this "
                         "many minutes (its own event cannot start a run here — see waiting_would_help), "
                         "then answer RED")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    if not args.repo:
        print("::error::--repo (or GH_REPO) is required")
        return 2
    if bool(args.pr) == bool(args.merge_group_ref):
        print("::error::exactly one of --pr or --merge-group-ref is required — refusing to guess which pull request to judge")
        return 2
    if args.merge_group_ref:
        try:
            number = pr_from_queue_ref(args.merge_group_ref)
        except ValueError as e:
            print(f"::error::{e}")
            return 1
        print(f"merge group {args.merge_group_ref} → pull request #{number}")
    else:
        if not re.fullmatch(r"[1-9]\d*", args.pr):
            print(f"::error::--pr must be a pull request number, got {args.pr!r}")
            return 2
        number = int(args.pr)
    if args.as_of and not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", args.as_of):
        print(f"::error::--as-of must be an ISO-8601 UTC instant like 2026-09-14T14:04:21Z, got {args.as_of!r}")
        return 2
    if args.as_of and args.settle_replies:
        print("::error::--as-of replays a past instant; --settle-replies waits for the present to stop moving. Pick one.")
        return 2
    if args.settle_replies < 0 or args.settle_replies > 300:
        print(f"::error::--settle-replies must be between 0 and 300 seconds, got {args.settle_replies}")
        return 2
    if args.as_of and args.wait_for_review:
        print("::error::--as-of replays a past instant; waiting for a review to arrive in it is meaningless")
        return 2
    if args.wait_for_review < 0 or args.wait_for_review > 30:
        print(f"::error::--wait-for-review must be between 0 and 30 minutes, got {args.wait_for_review}")
        return 2
    return run(args.repo, number, args.as_of, args.wait_for_review, args.settle_replies)


if __name__ == "__main__":
    sys.exit(main())
