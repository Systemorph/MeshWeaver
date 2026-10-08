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
1. The automatic review has LANDED. A review by the reviewer account at a non-PENDING state counts,
   UNLESS its body is a refusal. The reviewer posts its REFUSALS under the same account and in the
   same endpoint — measured on #645–#654 (2026-07-2x): "Copilot was unable to review this pull
   request because the user who requested the review has reached their quota limit." — so "a review
   by the bot exists" would read a quota outage as "reviewed", and that is the ONE thing the body is
   read for. An unfamiliar body is a NEW FORMAT, not an absence: see "PROVENANCE, NOT PRESENTATION"
   below for what requiring a recognisable shape cost on 2026-09-18. Only an EMPTY body is
   unrecognised — there is then nothing to read as either a review or a refusal.
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

THE REVIEWER-UNAVAILABLE DEGRADATION — the governed, non-person exit (MeshWeaver.Feedback#86)
------------------------------------------------------------------------------------------
Without it the gate is circular: when the internal reviewer itself cannot complete a round (on
2026-09-29 rounds aborted at the 30-minute cap — MeshWeaver.Plugins#2564/#2565/#2568), EVERY pull
request is held, including the one that repairs the reviewer, until a person applies the waiver.
So condition 1 is also released — and again NOTHING else — when the pull request's HEAD commit
carries a check run that the reviewer's own App posted to say it could not review:

  name `internal-review`  AND  app slug `systemorph-com` AND app id 4918443 (both; measured on
  check run 109436738198, 2026-09-29T13:45Z)  AND  status `completed`  AND  conclusion `neutral`
  AND  output.title starting `Reviewer unavailable`.

The NEWEST completed `internal-review` run from that App on the head decides (so a later real
round supersedes an earlier degradation). Provenance, not presentation, exactly as for the review
itself: a neutral run from another App, under another name, or with that title at any other
conclusion is NOT a degradation. The Plugins steward posts it only for an INFRASTRUCTURE cause,
after one re-kick, naming the cause in the summary; the review is DEFERRED, not skipped — a
post-merge review is owed on the item. The GREEN verdict names the degradation and its summary, so
it is never silent. A run completed after `--as-of` is ignored.

THE REVIEWER, MEASURED (2026-09-17, 50 merged pull requests, #4487–#4568)
----------------------------------------------------------------------
The reviewer posts under TWO logins with ONE account id:
  pulls/{n}/reviews    `copilot-pull-request-reviewer[bot]`  type Bot  id 175728472
  pulls/{n}/comments   `Copilot`                             type Bot  id 175728472
It posted exactly one review per pull request on all 50 (the ruleset carries
`review_on_push: false`), always at state COMMENTED.

THE SECOND REVIEWER — the internal one (policy `internal-code-review`)
----------------------------------------------------------------------
The PR steward's GLM-5.3 reviewer (MeshWeaver.Plugins `Governance/PullRequestSteward.md`) posts
through the `systemorph-com` GitHub App, under ONE login and ONE account id on both endpoints:
  pulls/{n}/reviews    `systemorph-com[bot]`  type Bot  id 328286035
  pulls/{n}/comments   `systemorph-com[bot]`  type Bot  id 328286035
It reviews every HEAD (not once per pull request), at state COMMENTED or CHANGES_REQUESTED — never
APPROVED. Both reviewers are accepted: a review by EITHER lands condition 1, and condition 2 counts
the threads BOTH opened.

COPILOT IS THE REVIEWER AGAIN — policy `copilot-code-review` (supersedes `internal-code-review`)
-------------------------------------------------------------------------------------------
Every ruleset carries `copilot_code_review` again, and `internal-review` is no longer a required
context anywhere. The stage gate and the arm gate accept a landed Copilot review ahead of an
`internal-review` run, so no head waits for an internal reviewer that has been switched off.
Doc/Architecture/ReviewFindingsAnswered → "Copilot is the reviewer again".

ONE REVIEW PER PULL REQUEST — policy `review-once-per-pull-request`
-------------------------------------------------------------------
With `review_on_push: true` every push bought a full new review round, and each round's threads
blocked the merge again until answered. The rule now runs with `review_on_push: false`, and every
gate in this file counts a landed Copilot review against ANY head of the pull request as "reviewed"
(`copilot_review_of_pull_request`; the merge gate's condition 1 never looked at the head). Condition
2 is unchanged: every reviewer thread on the pull request, whichever head it was opened on, needs a
person's reply. Doc/Architecture/ReviewFindingsAnswered → "One review per pull request".

WHAT MAKES A REVIEW A REVIEW: PROVENANCE, NOT PRESENTATION
----------------------------------------------------------
A review counts as landed because the REVIEWER ACCOUNT posted it at a non-PENDING state — never
because its body contains a particular phrase. The body is read for ONE purpose: to detect a
REFUSAL, which is the reviewer declining to review rather than reviewing.

This was learnt the expensive way. Until 2026-09-18 `landed` required the literal string
"Pull request overview" (July `## Pull request overview`, September
`<summary>Pull request overview</summary>`). That day the reviewer began posting a SHORT verdict
body — `### 🟢 Approval recommended`, `### 🟡 Changes recommended`, `### 🔵 Needs a closer look`
and two or three sentences, with no overview block at all — and the marker matched NOTHING. Six
core pull requests were reviewed and every one of them read as "the automatic review has not
landed", on a REQUIRED context, so answering the findings could not clear it and the only exit
was a maintainer waiver. The self-test could not catch it: all three fixtures carried the marker,
so the suite proved the rule only on the side of the change where it held.

A decorative substring is the reviewer's formatting choice and can be revised without notice; the
account id cannot. So `is_reviewer` + a non-PENDING state establishes the review, REFUSAL_MARKERS
carve out the non-reviews, and a body that is merely unfamiliar is a review with a new format.

USAGE
-----
  check-review-answered.py --self-test
  check-review-answered.py --repo O/R --pr N [--as-of 2026-09-14T14:04:21Z]
  check-review-answered.py --repo O/R --pr N --arm-gate     (the control plane's arm decision: may auto-merge be armed now?)
  check-review-answered.py --repo O/R --merge-group-ref refs/heads/gh-readonly-queue/main/pr-N-<sha>
  check-review-answered.py --repo O/R --pr N --stage-gate --since <run created_at>
        (node-repo-stage-gate.yml: may the expensive suites start for this head?)
  … --stage-gate / --arm-gate … --no-carry
        (the negative control: never carry a review over a clean merge of the base branch — see carry_over)
  check-review-answered.py --repo O/R --stage-advance --workflow ci.yml (--head-sha S | --pr N | --sweep)
        (node-repo-stage-advance.yml: re-run a waiting run's failed jobs now that stage 1 is green)
  check-review-answered.py --repo O/R --pr N --refresh-read-run --event E --run-id R --evaluated-at T
        (node-repo-review-answered.yml: a GREEN verdict on an event branch protection does not read
         re-runs the newest `pull_request` run, whose verdict it does read — see refresh_action)

THE STAGE GATE (Doc/Architecture/StagedPullRequestPipeline)
-----------------------------------------------------------
Stage 0 (cheap static controls) and stage 1 (this head's automatic review landed AND every
reviewer thread answered) must be green before stage 2 (test shards, portal hosts, gate shards,
bundles) spends a runner on the head; arming waits for stage 2. `stage_readiness` is stage 1's
predicate. It shares `internal_review_runs`, `listing_incomplete` and `reviewer_threads` with the
arm gate, so the two can never disagree about "reviewed" or "answered". It differs from the arm
gate in exactly the three places where a stage-1 hold would otherwise freeze the fleet:
  * the reviewer-unavailable degradation RELEASES stage 2 (arming still refuses it);
  * no completed review `--fallback-minutes` after the head's run was created RELEASES stage 2
    (a review OUTAGE posts nothing at all, so no event could ever release it);
  * the label `tests-before-review` RELEASES stage 2 (runner spend only — arming is unaffected).
Each release is LOUD (::warning:: + job summary). Unanswered threads never fall back: that wait is
a person's, not the infrastructure's.

`--stage-advance` is the event half: it re-evaluates `stage_readiness` and, only when it is green
and the head's newest CI run holds a FAILED stage gate, POSTs `rerun-failed-jobs` (the doctrine's
own re-run remedy, review-answered-on-degradation.yml). `--sweep` is the bounded-fallback timer.

`--refresh-read-run` is the SELF-REFRESH (the #4649 shape, done by the check instead of a person).
Answering a thread fires `pull_request_review_comment` (and `pull_request_review`), and branch
protection does not read the `pull_request_review_comment` run's check-run at all: the log said
GREEN while the pull request stayed BLOCKED on the older `pull_request` run's red until somebody
re-ran it by hand. So after a GREEN verdict on any event other than `pull_request` (and never for a
`merge_group` entry, which is its own commit), the lane re-runs the failed jobs of the newest
`pull_request` run of the SAME workflow for the current head — once per (head, answered state):
`refresh_action` (pure, self-tested) skips a run that is in flight, already green, or started
at/after this verdict (it judged a state no older than ours). The re-run decides nothing by itself:
it applies the whole predicate to live state, so a thread that is really unanswered is red there
too. No loop: a re-run of a `pull_request` run is a `pull_request` event, which never refreshes, and
a GITHUB_TOKEN re-run raises no new workflow event.

`--as-of` evaluates the pull request as it stood at that instant (reviews, comments and waiver
events created later are ignored) — the controls in Doc/Architecture/ReviewFindingsAnswered use it
to replay a merge. Every GitHub call is `gh api` (REST) with the token in GH_TOKEN. Exit 0 = GREEN,
1 = RED, 2 = usage error.
"""
from __future__ import annotations

import argparse
import dataclasses
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import urllib.parse

REVIEWER_ACCOUNT_ID = 175728472
# The INTERNAL reviewer — policy `internal-code-review` (Doc/Architecture/PolicyNotProse, the
# register). The GLM-5.3 reviewer of MeshWeaver.Plugins' PR steward posts its review, its inline
# findings and the `internal-review` check run through the `systemorph-com` GitHub App, whose bot
# account is `systemorph-com[bot]`, id 328286035 (`GET /users/systemorph-com%5Bbot%5D`). It is
# ACCEPTED ALONGSIDE Copilot so that no pull request is stranded while the `copilot_code_review`
# rule is retired repository by repository: either reviewer's review lands the review, and every
# thread EITHER one opened needs a reply from a person. Design of record: MeshWeaver.Plugins
# `Governance/PullRequestSteward.md`.
INTERNAL_REVIEWER_ACCOUNT_ID = 328286035
REVIEWER_ACCOUNT_IDS = frozenset({REVIEWER_ACCOUNT_ID, INTERNAL_REVIEWER_ACCOUNT_ID})
REVIEWER_LOGINS = frozenset({"copilot-pull-request-reviewer[bot]", "Copilot", "systemorph-com[bot]"})
REFUSAL_MARKERS = (
    re.compile(r"\bCopilot (?:was unable|wasn't able|was not able|could not|couldn't|cannot|can't) (?:to )?review\b", re.IGNORECASE),
    re.compile(r"\bunable to review this pull request\b", re.IGNORECASE),
    re.compile(r"\*\*Files reviewed:\*\*\s*0\s*/", re.IGNORECASE),
)
# The reviewer-unavailable degradation (MeshWeaver.Feedback#86): the check run the internal
# reviewer's App posts on the head commit when it could not review. Slug AND id, because a slug is
# a display name an App owner can change and an id cannot be claimed by another App. The id was
# read off a real `internal-review` run (109436738198 on d433fc0c10, 2026-09-29T13:45:38Z).
DEGRADATION_CHECK_NAME = "internal-review"
DEGRADATION_APP_SLUG = "systemorph-com"
DEGRADATION_APP_ID = 4918443
DEGRADATION_CONCLUSION = "neutral"
DEGRADATION_TITLE_PREFIX = "Reviewer unavailable"
WAIVER_LABEL = "review-waived"
WAIVER_ROLES = frozenset({"admin", "maintain"})
QUEUE_REF = re.compile(r"^(?:refs/heads/)?gh-readonly-queue/(?P<base>[^/]+)/pr-(?P<pr>[1-9]\d*)-(?P<sha>[0-9a-f]{40})$")


# ─────────────────────────────── the predicate (pure) ───────────────────────────────

def is_reviewer(user: dict | None) -> bool:
    if not user or user.get("type") != "Bot":
        return False
    return user.get("id") in REVIEWER_ACCOUNT_IDS or user.get("login") in REVIEWER_LOGINS


def is_person(user: dict | None) -> bool:
    return bool(user) and user.get("type") == "User" and not str(user.get("login", "")).endswith("[bot]")


def first_line(text: str | None) -> str:
    for line in (text or "").splitlines():
        if line.strip():
            return line.strip()[:160]
    return "(empty body)"


def classify_review_body(body: str | None) -> str:
    """'refused' | 'landed' | 'unrecognised'. A refusal wins; anything else the reviewer says is
    the review.

    The caller has already established provenance (`is_reviewer`, state != PENDING), so the body
    is examined ONLY to separate a review from a refusal to review. An unfamiliar format is a new
    format, not an absence — see "PROVENANCE, NOT PRESENTATION" above for what keying this on a
    presentation substring cost. Only a body with no text at all is unrecognised: there is then
    nothing to read as either a review or a refusal."""
    text = body or ""
    if any(m.search(text) for m in REFUSAL_MARKERS):
        return "refused"
    if text.strip():
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
    """🚨 `refused` is a SEPARATE field and not a substring of `reasons` on purpose (#4730).

    "the reviewer refused", "the reviewer has not posted yet" and "the reviewer posted findings
    nobody answered" are three different states with three different remedies, and until this field
    existed all three printed the same sentence — one that tells the reader the review "usually
    arrives minutes after the pull request opens" and to reply to threads. During the 2026-09-18
    quota outage that sentence was on six pull requests for four hours, and every one of them was
    unreviewable: there was nothing to wait for and nothing to reply to. Deriving it back out of the
    reason text would be the presentation-keyed reading this file's own header warns about."""

    green: bool
    reasons: tuple[str, ...]
    notes: tuple[str, ...]
    unanswered: tuple[dict, ...]
    #: First lines of the reviewer posts that were REFUSALS, when no review landed. Empty when a
    #: review landed, when the reviewer has not posted at all, or when a waiver released the state.
    refused: tuple[str, ...] = ()
    #: The reviewer-unavailable check run that released condition 1, when one did — the sentence
    #: every surface prints so a degraded GREEN is never mistaken for a reviewed one.
    degraded: str = ""


def is_degradation_app(app: dict | None) -> bool:
    return bool(app) and app.get("slug") == DEGRADATION_APP_SLUG and app.get("id") == DEGRADATION_APP_ID


def newest_internal_review_run(check_runs, as_of: str | None) -> dict | None:
    """The newest COMPLETED `internal-review` run the reviewer's own App posted, as of `as_of`.
    Runs from any other App are not looked at at all — they cannot supersede the App's own verdict
    in either direction."""
    mine = [c for c in check_runs or ()
            if c.get("name") == DEGRADATION_CHECK_NAME and is_degradation_app(c.get("app"))
            and c.get("status") == "completed" and c.get("completed_at")
            and not_after(c.get("completed_at"), as_of)]
    return max(mine, key=lambda c: (c.get("completed_at") or "", c.get("id") or 0)) if mine else None


def degradation_of(check_runs, as_of: str | None) -> dict | None:
    """The check run that says the reviewer was unavailable for this head, or None. See
    "THE REVIEWER-UNAVAILABLE DEGRADATION" in the module docstring for the contract."""
    run = newest_internal_review_run(check_runs, as_of)
    if run is None or run.get("conclusion") != DEGRADATION_CONCLUSION:
        return None
    title = ((run.get("output") or {}).get("title") or "").strip()
    return run if title.startswith(DEGRADATION_TITLE_PREFIX) else None


def evaluate(pr: dict, reviews: list, comments: list, waiver: Waiver, as_of: str | None = None,
             check_runs: list | tuple = (), files: list | None = None, commits: list | None = None) -> Verdict:
    reasons: list[str] = []
    notes: list[str] = []
    refused: list[str] = []
    degraded = ""

    # 3 (checked first: an incomplete listing makes every other statement unreliable)
    incomplete = listing_incomplete(pr, comments)
    if incomplete:
        reasons.append(incomplete)

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
        run = degradation_of(check_runs, as_of)
        granted, message = waiver_holder(waiver, as_of)
        # 🚨 A GENERATED-ONLY App pull request owes NO review (Plugins #3044): the same provenance rule
        # the stage gate already applies (`generated_only`, never a title or a branch name). The reviewer
        # REFUSES such a pull request ("Copilot wasn't able to review any files" — locks are excluded),
        # so this condition could only be released by a person's waiver; until one came, the lock settle
        # and the floor stamp sat red, main published, sealed and tagged nothing, and every portal held
        # each module behind its newest prebuilt (Governance 0.10 installed, 0.9.16 serving, 2026-10-08).
        generated, generated_why = generated_only(pr, files, commits) if is_generated_bot(pr.get("user")) else (False, "")
        if generated:
            notes.append(f"NOT OWED: {why}. {generated_why} — nothing to review (generated_only)")
        elif run is not None:
            # Checked BEFORE the waiver: it is the governed exit and needs nobody, and when both
            # stand the log should say the system released it, not that a person had to.
            summary = " ".join((((run.get("output") or {}).get("summary")) or "(no summary)").split())[:400]
            degraded = (f"REVIEWER UNAVAILABLE — condition 1 released by degradation: check run "
                        f"{run.get('id')} `{DEGRADATION_CHECK_NAME}` from the `{DEGRADATION_APP_SLUG}` App "
                        f"(id {DEGRADATION_APP_ID}), {DEGRADATION_CONCLUSION} at {run.get('completed_at')} on the head: "
                        f"\"{((run.get('output') or {}).get('title') or '').strip()}\" — {summary}. "
                        "The review is DEFERRED, not skipped: a post-merge review is owed; every thread "
                        "the reviewer did open still needs a reply")
            notes.append(f"DEGRADED: {why}. {degraded}")
        elif granted:
            notes.append(f"WAIVED: {why}. {message}")
        else:
            reasons.append(why + (f". {message}" if message else ""))
            # Only when the state actually STANDS: a waived refusal is not an unreviewable pull
            # request, it is a reviewed-enough one, and saying otherwise would re-create the
            # confusion in the other direction.
            refused += [first_line(r.get("body")) for k, r in kinds if k == "refused"]

    # 2 — is every thread the reviewer started answered?
    roots, unanswered = reviewer_threads(comments, as_of)
    notes.append(f"threads opened by the automatic reviewer: {len(roots)}, answered by a person: {len(roots) - len(unanswered)}")
    if unanswered:
        reasons.append(f"{len(unanswered)} of {len(roots)} thread(s) opened by the automatic reviewer have no reply from a person")

    return Verdict(green=not reasons, reasons=tuple(reasons), notes=tuple(notes), unanswered=unanswered,
                   refused=tuple(refused), degraded=degraded)


def listing_incomplete(pr: dict, comments: list) -> str | None:
    """Condition 3 — the reason the comment listing cannot be proven complete, or None. ONE
    implementation, shared by the merge gate and the arm gate (`arm_readiness`)."""
    reported = pr.get("review_comments")
    if not isinstance(reported, int):
        return "the pull request did not report a `review_comments` count, so the comment listing cannot be proven complete"
    if len(comments) < reported:
        return (f"the comment listing returned {len(comments)} comment(s) but the pull request reported {reported} "
                "before the listing began — the read is incomplete, so no thread can be called answered")
    return None


def reviewer_threads(comments: list, as_of: str | None = None) -> tuple[list, tuple]:
    """Condition 2 — (threads the automatic reviewer opened, those of them no PERSON replied to).
    A reply is attributed to its thread by following `in_reply_to_id` to the root (`root_id_of`).
    ONE implementation, shared by the merge gate and the arm gate, so the two can never disagree
    about whether a finding was answered."""
    by_id = {c.get("id"): c for c in comments}
    visible = [c for c in comments if not_after(c.get("created_at"), as_of)]
    roots = [c for c in visible if c.get("in_reply_to_id") is None and is_reviewer(c.get("user"))]
    answered_roots = {root_id_of(c, by_id) for c in visible
                      if c.get("in_reply_to_id") is not None and is_person(c.get("user"))}
    return roots, tuple(c for c in roots if c.get("id") not in answered_roots)


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

    🚨 A REFUSAL IS NOT WAITED FOR, and reading that off the prose was wrong (#4730 review). The
    refusal reason is spelled "the automatic review has not landed — the reviewer posted, but not a
    review: …", so the substring test above accepted it and `--wait-for-review 15` slept a quarter
    of an hour printing "waiting for the automatic review" at a reviewer that had already answered:
    it said no. Nothing arrives during that wait by construction, the run then contradicts its own
    summary, and it spends a runner for it. The structured field is the discriminator — the same
    reason `Verdict.refused` exists rather than being derived back out of the text.
    """
    return (not verdict.green
            and not verdict.refused
            and len(verdict.reasons) == 1
            and "has not landed" in verdict.reasons[0])


UNANSWERED_REASON = "no reply from a person"


def root_id_of(c: dict, by_id: dict) -> int | None:
    """The id of the comment at the top of `c`'s thread. Cycle-safe (a malformed chain returns
    None rather than spinning), and it returns the missing parent's id when the chain leaves the
    listing — the ONE implementation, used by the answered-threads rule and by the settle
    predicate, so the two can never disagree about what thread a reply belongs to."""
    seen: set = set()
    while c.get("in_reply_to_id") is not None:
        if c["id"] in seen:
            return None
        seen.add(c["id"])
        parent = by_id.get(c["in_reply_to_id"])
        if parent is None:
            return c["in_reply_to_id"]
        c = parent
    return c.get("id")


def newest_person_reply(comments: list, as_of: str | None = None) -> str | None:
    """The ISO-8601 stamp of the most recent reply by a person ON A THREAD THE AUTOMATIC REVIEWER
    OPENED, or None if there is none.

    🚨 All three qualifiers are load-bearing, and the third was a review finding on this very
    change. A reply, not any comment: the reviewer's own root comments are the findings, and their
    arrival is no evidence that anybody is answering. By a PERSON: the reviewer replying to itself
    answers nothing. And on the REVIEWER'S thread: a conversation between two humans on some other
    thread is not evidence that a finding is being answered, and counting it would let an unrelated
    discussion hold the required check for the whole settle window while the finding sat untouched.
    """
    by_id = {c.get("id"): c for c in comments}
    reviewer_roots = {c.get("id") for c in comments
                      if c.get("in_reply_to_id") is None and is_reviewer(c.get("user"))}
    stamps = [c.get("created_at") for c in comments
              if c.get("in_reply_to_id") is not None
              and is_person(c.get("user"))
              and root_id_of(c, by_id) in reviewer_roots
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


# ─────────────────────────────── the ARM gate (pure) ───────────────────────────────
#
# The ARM decision (made by the control plane's PR steward, MeshWeaver.Plugins `PrArming`; this is
# its reference implementation and these self-test cases its test vectors — `auto-arm.yml` only
# disarms on a push) asks a DIFFERENT, stricter question than the merge gate above: not "may this
# merge" but "may auto-merge be armed NOW". Measured 2026-10-03/04 on MeshWeaver.Plugins, where the review
# is comment-only and nothing required waits for it: the lane re-armed on every push and on undraft,
# BEFORE the internal review had run on the new head, so #2549, #2643, #2647 and Memex#641 merged
# with findings nobody had answered or with no review at all, and agents disarmed by hand after
# every push (#2791 twice in an hour). So auto-merge is armed only when ALL of these hold:
#
#   1. not a draft;
#   2. the CURRENT head has its review — a Copilot review against it (see below), or the internal
#      reviewer's `internal-review` check run on it has COMPLETED, and that run
#      is not the neutral "Reviewer unavailable" degradation. That degradation releases the MERGE
#      gate's review condition (so a down reviewer cannot hold every pull request), but it is not a
#      review, and arming on it would land an unreviewed change with nobody having decided to —
#      a person who wants that merges by hand;
#   3. every thread the automatic reviewer opened has a reply from a person (`reviewer_threads`,
#      the SAME predicate as the merge gate), read from a provably complete listing;
#   4. (policy `suites-parallel-with-review`) every REQUIRED status check of the base branch has COMPLETED
#      with `success` on the current head — the suites ran (on a fresh merge with the current main,
#      policy `suites-test-fresh-merge`) and are green. The review and the suites may finish in either order and the arm waits for
#      BOTH: nothing is armed while a required check is pending, missing or red. The review's own
#      contexts are conditions (2)/(3), never (4). `required_checks_green` is the predicate;
#      MeshWeaver.Plugins' control-plane `PrArming` ports it one for one.
#
# No waiver stands in for (2). 🔁 Policy `copilot-code-review` (Doc/Architecture/PolicyNotProse) puts
# GitHub Copilot back as the fleet's reviewer, and policy `review-once-per-pull-request` reviews each
# pull request ONCE: the ruleset rule runs with `review_on_push: false`, so a push does not start a
# new round. A landed Copilot review of the pull request — against the current head, or failing that
# against ANY earlier head (`copilot_review_of_pull_request`: a landed body, never a refusal) —
# satisfies (2) exactly as a completed `internal-review` run on the head does. It is checked FIRST,
# so a head is never held waiting for an internal reviewer that has been switched off. Every thread
# that review opened still needs a person's reply under (3), whichever head it was opened on.


def copilot_review_on(reviews, head_sha: str, as_of: str | None = None) -> dict | None:
    """The newest Copilot review submitted against `head_sha` that LANDED (a non-PENDING state and a
    body `classify_review_body` reads as a review, never a refusal), or None. Provenance is the
    account id AND type Bot — the internal reviewer's reviews are NOT looked at here: its verdict on
    a head is its `internal-review` check run, which the callers read separately."""
    mine = [r for r in landed_copilot_reviews(reviews, as_of) if head_sha and r.get("commit_id") == head_sha]
    return max(mine, key=lambda r: (r.get("submitted_at") or "", r.get("id") or 0)) if mine else None


def landed_copilot_reviews(reviews, as_of: str | None = None) -> list:
    """Every Copilot review on the pull request that LANDED (provenance: account id AND type Bot; a
    non-PENDING state; a body that is a review, never a refusal), whichever head it was submitted on."""
    return [r for r in reviews or ()
            if (r.get("user") or {}).get("type") == "Bot" and (r.get("user") or {}).get("id") == REVIEWER_ACCOUNT_ID
            and r.get("state") not in (None, "PENDING") and not_after(r.get("submitted_at"), as_of)
            and classify_review_body(r.get("body")) == "landed"]


def copilot_review_of_pull_request(reviews, head_sha: str, as_of: str | None = None) -> dict | None:
    """Policy `review-once-per-pull-request`: the pull request's ONE Copilot review — the newest
    landed review against `head_sha` when there is one, otherwise the newest landed review against
    ANY earlier head. `pulls/{n}/reviews` lists only this pull request's reviews, so every review it
    returns reviewed this pull request, and a later push does not undo it. Its threads stay under the
    thread condition wherever they were opened, so no finding is skipped by a push."""
    on_head = copilot_review_on(reviews, head_sha, as_of)
    if on_head is not None:
        return on_head
    mine = landed_copilot_reviews(reviews, as_of)
    return max(mine, key=lambda r: (r.get("submitted_at") or "", r.get("id") or 0)) if mine else None


def copilot_note(review: dict, short: str) -> str:
    reviewed = str(review.get("commit_id") or "")[:10] or "(unknown)"
    if reviewed == short:
        return (f"Copilot reviewed head {short}: review {review.get('id')} at {review.get('submitted_at')} "
                f"(\"{first_line(review.get('body'))}\") — policy copilot-code-review")
    return (f"Copilot reviewed this pull request at its earlier head {reviewed} (current head {short}): review "
            f"{review.get('id')} at {review.get('submitted_at')} (\"{first_line(review.get('body'))}\") — "
            "one review per pull request, policy review-once-per-pull-request")


@dataclasses.dataclass(frozen=True)
class ArmVerdict:
    ready: bool
    #: ONE line naming the first missing condition; empty when ready. It is what the lane writes to
    #: its job summary, so it must say what would change the answer.
    missing: str
    notes: tuple[str, ...] = ()


def internal_review_runs(check_runs, head_sha: str) -> list:
    """The reviewer App's own `internal-review` runs on `head_sha`. Runs from any other App, under
    any other name, or on another commit are not looked at."""
    return [c for c in check_runs or ()
            if c.get("name") == DEGRADATION_CHECK_NAME and is_degradation_app(c.get("app"))
            and (not c.get("head_sha") or c.get("head_sha") == head_sha)]


#: Contexts the arm gate judges through the REVIEW conditions (2)/(3), never as a suite in (4): the
#: review itself, and the merge-gate check that reports on it (it turns green only after (3) holds,
#: and its lane re-runs on review events — requiring it here would wait on a re-run nobody started).
REVIEW_CONTEXTS = frozenset({DEGRADATION_CHECK_NAME, "Automatic review answered", "lane / Automatic review answered"})


def required_checks_green(required, head_runs, head_sha: str) -> str:
    """'' when every required context (minus REVIEW_CONTEXTS) has a check run on `head_sha` whose
    NEWEST run completed `success`; otherwise ONE line naming the first that is missing, pending or
    red. `required=None` means the caller did not ask (the pure review gate); an EMPTY list is
    refused — 'no required check' must never read as 'all green'."""
    if required is None:
        return ""
    wanted = [c for c in dict.fromkeys(required) if c not in REVIEW_CONTEXTS]
    if not wanted:
        return "no required status check could be read for the base branch, so the suites cannot be shown green — not armed on a guess"
    short = head_sha[:10]
    for ctx in wanted:
        mine = [r for r in head_runs or () if r.get("name") == ctx and (not r.get("head_sha") or r.get("head_sha") == head_sha)]
        if not mine:
            return f"required check `{ctx}` has not reported on head {short} — the suites have not run (or not started) on this head"
        newest = max(mine, key=lambda r: (r.get("started_at") or "", r.get("id") or 0))
        if newest.get("status") != "completed":
            return f"required check `{ctx}` is still {newest.get('status') or 'running'} on head {short} — arming waits for the suites"
        if newest.get("conclusion") != "success":
            return f"required check `{ctx}` concluded {newest.get('conclusion')} on head {short} — arming waits for green suites"
    return ""


# ─────────────────────────────── the CARRY-OVER (pure) ───────────────────────────────
#
# Policy `review-carries-over-clean-base-merge` (Doc/Architecture/StagedPullRequestPipeline → "A clean merge of the base branch carries the review"). Measured
# 2026-10-05 22:38Z: 25 open non-draft pull requests across core, Plugins and Memex and 7 merges in
# 2.5 h, six of them DIRTY at once. The loop: main moves → a PR goes DIRTY → main is merged into the
# branch → the new head has no `internal-review` → the stage gate holds it at stage 1 → a full review
# round queues behind admission → main moves again. A merge of the base branch that applied cleanly
# changes nothing a reviewer read, so it must not cost a review.
#
# A review CARRIES OVER to the new head when, computed from the repository (never from a commit
# message or a branch name):
#   (a) walking back from the new head through MERGE commits only, the first commit with a real
#       (completed, non-degraded) `internal-review` from the reviewer's App is reached — the reviewed
#       head; every merge walked has exactly ONE parent on the pull request's side (the other is in the
#       base branch);
#   (b) every commit that is on the pull request now and was not on it at the reviewed head is a merge
#       commit — any non-merge commit is new content and owes a fresh review;
#   (c) the pull request's OWN diff — merge-base(base, head)..head, as GitHub's compare computes it —
#       is byte-identical to the reviewed head's: the same patch-id (hunk line numbers ignored, as
#       `git patch-id` does; context lines, file status and names kept). A conflict resolution that
#       changed what the pull request does changes the patch-id and is reviewed fresh.
# Anything unreadable or incomplete (a short commit listing, the compare API's 300-file cap, a file
# with neither a patch nor a blob id) does NOT carry: the head is reviewed as it was before.

CARRY_MAX_WALK = 20
COMPARE_FILES_CAP = 300
HUNK_HEADER = re.compile(r"^@@ -\d+(?:,\d+)? \+\d+(?:,\d+)? @@")


@dataclasses.dataclass(frozen=True)
class Carry:
    ok: bool
    #: ONE line: the evidence when it carries (reviewed head, check run, the merges, the patch-ids),
    #: or why it does not. Printed in the check's log and its job summary either way.
    why: str
    from_head: str = ""
    run: dict | None = None


#: Titles of `internal-review` runs that ARE a review — an ALLOW-list, so the carry fails SAFE. The
#: App also posts runs that are not a review (the *Reviewer unavailable* degradation, the steward's
#: *Review not completed* exit — on the replay of the last 60 core pull requests three clean merges sat
#: on that exit), and their titles are owned by MeshWeaver.Plugins. A deny-list would turn a reworded
#: exit into a carried "review" in silence; with an allow-list a reworded REVIEW title only means a
#: fresh round. The shapes are Plugins `PullRequestActions` (`No blocking findings`,
#: `{n} blocking finding(s)`) and `ReviewCarryOver.CarriedTitlePrefix` (a carried review carries on).
REVIEW_CONCLUSIONS = frozenset({"success", "failure"})
REVIEW_TITLES = re.compile(r"^(?:No blocking findings|[1-9]\d* blocking finding\(s\)|Review carried from )")


def real_review_run(check_runs, sha: str) -> dict | None:
    """The newest COMPLETED `internal-review` run of the reviewer's App on `sha` when it is a REVIEW
    (REVIEW_TITLES); None when there is none, or when the newest is anything else."""
    run = newest_internal_review_run(internal_review_runs(check_runs, sha), None)
    title = ((run or {}).get("output") or {}).get("title") or ""
    # The conclusion must be READ as one a review posts: an unread or empty one is never carried.
    return run if run is not None and run.get("conclusion") in REVIEW_CONCLUSIONS and REVIEW_TITLES.match(title.strip()) else None


def pr_diff_id(files) -> tuple[str | None, str]:
    """(patch-id, '') of a compare's `files`, or (None, why it cannot be fingerprinted). Pure.

    Per file: status, previous name, name and the patch with every hunk header reduced to `@@`
    (line numbers move when the base branch changes elsewhere in the file; the change does not).
    A file GitHub sends no patch for (binary, too large, a pure rename) contributes its blob id."""
    if not isinstance(files, list):
        return None, "the file listing was not read"
    if len(files) >= COMPARE_FILES_CAP:
        return None, f"{len(files)} files — the compare API stops at {COMPARE_FILES_CAP}, so the listing cannot be proven complete"
    parts = []
    for f in sorted(files, key=lambda f: (str(f.get("filename")), str(f.get("previous_filename") or ""))):
        patch = f.get("patch")
        if patch is None:
            if not f.get("sha"):
                return None, f"{f.get('filename')} has neither a patch nor a blob id"
            body = "blob " + str(f.get("sha"))
        else:
            # Every byte of every line is hashed — trailing whitespace too (a hard line break in
            # Markdown, content in a docstring or a YAML scalar); only the hunk header is reduced.
            body = "\n".join("@@" if HUNK_HEADER.match(line) else line for line in patch.split("\n"))
        parts.append("\0".join((str(f.get("status")), str(f.get("previous_filename") or ""), str(f.get("filename")), body)))
    return hashlib.sha256("\n\0\n".join(parts).encode("utf-8")).hexdigest(), ""


def find_reviewed_ancestor(head: str, own_commits: dict, runs_of) -> tuple[str | None, dict | None, tuple, str]:
    """(reviewed head, its run, the merges walked, '') or (None, None, (), why not). `own_commits` maps
    sha → commit for every commit on the pull request now (compare(base...head).commits, with
    `parents`); `runs_of(sha)` lists that commit's `internal-review` check runs. Walks MERGES only."""
    walked: list = []
    c = head
    for _ in range(CARRY_MAX_WALK):
        commit = own_commits.get(c)
        if commit is None:
            return None, None, (), f"{c[:10]} is not among the pull request's own commits"
        parents = [str((p or {}).get("sha") or "") for p in commit.get("parents") or []]
        if len(parents) < 2:
            return None, None, (), (f"{c[:10]} is not a merge commit and has no review of its own — "
                                    "a non-merge commit since the last review is new content")
        own = [p for p in parents if p in own_commits]
        if len(own) != 1:
            return None, None, (), (f"merge {c[:10]} has {len(own)} parent(s) on the pull request's side — "
                                    "it is not a merge OF the base branch into the pull request")
        walked.append(c)
        parent = own[0]
        run = real_review_run(runs_of(parent), parent)
        if run is not None:
            return parent, run, tuple(walked), ""
        c = parent
    return None, None, (), f"no reviewed head within {CARRY_MAX_WALK} merges of {head[:10]}"


def carry_over(head: str, new_cmp: dict, old_cmp: dict, old: str, run: dict) -> Carry:
    """Conditions (b) and (c) for a reviewed ancestor `old` that (a) found. `new_cmp`/`old_cmp` are
    compare(base...head) / compare(base...old) with COMPLETE `commits` (the adapter pages them). Pure."""
    def no(why: str) -> Carry:
        return Carry(False, f"no carry-over from reviewed head {old[:10]}: {why} — the head owes a fresh review")
    commits_new = {c.get("sha"): c for c in new_cmp.get("commits") or []}
    commits_old = {c.get("sha") for c in old_cmp.get("commits") or []}
    for name, cmp_, got in (("head", new_cmp, len(commits_new)), ("reviewed head", old_cmp, len(commits_old))):
        if not isinstance(cmp_.get("total_commits"), int) or got < cmp_["total_commits"]:
            return no(f"the {name}'s commit listing returned {got} of {cmp_.get('total_commits')} commits")
    if old not in commits_old:
        return no("the reviewed head is no longer ahead of the base branch, so its diff is not comparable")
    if head not in commits_new:
        return no("the head is not ahead of the base branch")
    fresh = [c for s, c in commits_new.items() if s not in commits_old]
    content = [c for c in fresh if len(c.get("parents") or []) < 2]
    if content:
        return no(f"{len(content)} non-merge commit(s) since it (first: {str(content[0].get('sha'))[:10]})")
    id_old, why_old = pr_diff_id(old_cmp.get("files"))
    id_new, why_new = pr_diff_id(new_cmp.get("files"))
    if id_old is None or id_new is None:
        return no(f"the pull request's diff cannot be fingerprinted ({why_old or why_new})")
    mb_old = str((old_cmp.get("merge_base_commit") or {}).get("sha") or "")[:10]
    mb_new = str((new_cmp.get("merge_base_commit") or {}).get("sha") or "")[:10]
    if id_old != id_new:
        return no(f"the pull request's own diff changed — patch-id {id_old[:16]} ({mb_old}..{old[:10]}) vs "
                  f"{id_new[:16]} ({mb_new}..{head[:10]}): a conflict resolution or a change inside the merge altered it")
    title = ((run.get("output") or {}).get("title") or "").strip()
    merges = ", ".join(str(c.get("sha"))[:10] for c in fresh)
    return Carry(True, (f"review CARRIED from head {old[:10]} (`{DEGRADATION_CHECK_NAME}` check run {run.get('id')}: "
                        f"{run.get('conclusion')} \"{title}\") to {head[:10]}: the {len(fresh)} commit(s) since it are all merges "
                        f"of the base branch ({merges}), and the pull request's own diff is byte-identical — patch-id "
                        f"{id_new[:16]} over {len(new_cmp.get('files') or [])} file(s), {mb_old}..{old[:10]} = {mb_new}..{head[:10]} "
                        "(policy review-carries-over-clean-base-merge)"), old, run)


def arm_readiness(pr: dict, comments: list, check_runs, required=None, head_runs=None, carry: Carry | None = None,
                  reviews=None) -> ArmVerdict:
    number = pr.get("number")
    head = str((pr.get("head") or {}).get("sha") or "")
    short = head[:10] or "(unknown)"
    if pr.get("draft"):
        return ArmVerdict(False, f"#{number} is a draft — a draft is never armed; mark it ready for review")
    copilot = copilot_review_of_pull_request(reviews, head)
    if copilot is not None:
        return _arm_after_review(pr, comments, required, head_runs, head, short, (copilot_note(copilot, short),))
    mine = internal_review_runs(check_runs, head)
    carried = carry is not None and carry.ok and real_review_run(mine, head) is None
    running = [c for c in mine if c.get("status") != "completed"]
    if running and not carried:
        return ArmVerdict(False, f"the `{DEGRADATION_CHECK_NAME}` review of head {short} is still {running[0].get('status') or 'running'} "
                                 f"(check run {running[0].get('id')}) — it re-evaluates when that run completes")
    run = carry.run if carried else newest_internal_review_run(mine, None)
    if run is None:
        return ArmVerdict(False, f"no review has landed on this pull request — neither a Copilot review (of any of its heads) nor a completed "
                                 f"`{DEGRADATION_CHECK_NAME}` run on the current head {short}; the pull request owes ONE review "
                                 "(policy review-once-per-pull-request), and the gate re-evaluates when it lands" + (f" ({carry.why})" if carry is not None else ""))
    if degradation_of([run], None) is not None:
        title = ((run.get("output") or {}).get("title") or "").strip()
        return ArmVerdict(False, f"the reviewer was UNAVAILABLE for head {short} (check run {run.get('id')}: \"{title}\") — "
                                 "that is not a review, so auto-merge stays off; merge by hand once someone has reviewed it, "
                                 "or push/re-kick for a real review")
    notes = ((carry.why,) if carried else
             (f"`{DEGRADATION_CHECK_NAME}` completed on head {short}: {run.get('conclusion')} "
              f"\"{((run.get('output') or {}).get('title') or '').strip()}\" (check run {run.get('id')})",))
    return _arm_after_review(pr, comments, required, head_runs, head, short, notes)


def _arm_after_review(pr: dict, comments: list, required, head_runs, head: str, short: str, notes: tuple) -> ArmVerdict:
    """Conditions (3) and (4), once (2) — this head's review — holds; `notes` names the review."""
    incomplete = listing_incomplete(pr, comments)
    if incomplete:
        return ArmVerdict(False, incomplete + " — it re-evaluates on the next event", notes)
    roots, unanswered = reviewer_threads(comments)
    notes += (f"threads opened by the automatic reviewer: {len(roots)}, answered by a person: {len(roots) - len(unanswered)}",)
    if unanswered:
        first = unanswered[0]
        return ArmVerdict(False, f"{len(unanswered)} of {len(roots)} thread(s) opened by the automatic reviewer have no reply from a person "
                                 f"(first: {first.get('html_url') or first.get('id')}) — reply to each (fixed, or why not)", notes)
    suites = required_checks_green(required, head_runs, head)
    if suites:
        return ArmVerdict(False, suites, notes)
    if required is not None:
        notes += (f"every required check is green on head {short}",)
    return ArmVerdict(True, "", notes)


# ─────────────────────────────── the STAGE gate (pure) ───────────────────────────────
#
# Maintainer, 2026-10-04: "code review must pass and also other controls such as no client etc.
# must pass before we start test. and we arm only at end of test". Stage 1's predicate — the head's
# automatic review landed and every thread answered — asked BEFORE the expensive suites, not after.
# Design of record, with the measurements: Doc/Architecture/StagedPullRequestPipeline.

TESTS_FIRST_LABEL = "tests-before-review"
#: Minutes after the head's CI run was created with NO completed review, after which stage 2
#: starts anyway (loudly). Derived from the measured review latency — see the design doc.
STAGE_FALLBACK_MINUTES = 120
STAGE_GATE_JOB = "Stage gate: may the suites start"
#: Every name the gate job has carried (renamed: with the `review-before-suites: false` opt-out the
#: gate does not wait for the review, so "review landed" on a green tick could lie). Runs held under
#: the OLD name must still be found and released by the event half.
STAGE_GATE_JOBS = (STAGE_GATE_JOB, "Stage 1: review landed and answered")
#: Every hold's error MESSAGE starts with this (stage gate, Plugins `admission`, core `Consolidate test
#: results`), so a hold is recognisable from the annotations alone — Plugins' PrBabysitter reads it.
STAGE_HOLD_MARKER = "STAGE 2 HELD ("


@dataclasses.dataclass(frozen=True)
class StageVerdict:
    ready: bool
    #: reviewed | carried | degraded | fallback | label | draft | waiting | unanswered | unreadable
    mode: str
    #: ONE line naming what holds stage 2 (empty when ready).
    missing: str
    #: When `ready` came from a RELEASE rather than a review: the line every surface prints LOUDLY.
    loud: str = ""
    notes: tuple[str, ...] = ()
    #: True when the review judged was CARRIED from an earlier head (its evidence is notes[0]).
    carried: bool = False


def parse_stamp(stamp: str | None) -> datetime.datetime | None:
    try:
        return datetime.datetime.strptime(stamp or "", "%Y-%m-%dT%H:%M:%SZ")
    except ValueError:
        return None


# 🚨 GENERATED-ONLY BOT PULL REQUESTS (2026-10-04, Plugins #2860): main's own jobs propose generated
# files as pull requests — `settle-locks` (every `manifest.lock`) and `stamp-floors` (`mesh-floor.lock`
# plus each package root's `minMeshVersion`). There is nothing for a reviewer to read, the settle job
# REWRITES the head on every main merge (so a per-head fallback clock restarts forever), and during a
# review outage the stage-1 hold deadlocked module publishing (AI 1.21 stuck behind #2860). Such a
# pull request skips stage 1 — on PROVENANCE, never on a title or a branch name: authored by the App
# that writes them, every commit by that App, and every changed file generated (a lock, or a root
# `index.json` whose changed lines are all `minMeshVersion`). Anything else — a person's PR that
# touches a lock, a bot PR with one hand-written line — is staged like any other.
GENERATED_BOT_IDS = frozenset({300054957})          # meshweaver-cloud[bot] (GET /users/meshweaver-cloud%5Bbot%5D)
GENERATED_BOT_LOGINS = frozenset({"meshweaver-cloud[bot]"})
GENERATED_BASENAMES = frozenset({"manifest.lock", "mesh-floor.lock"})


def is_generated_bot(user: dict | None) -> bool:
    return bool(user) and user.get("type") == "Bot" and user.get("id") in GENERATED_BOT_IDS \
        and user.get("login") in GENERATED_BOT_LOGINS


# A changed line of a floor stamp is NOTHING BUT the key/value — never a line that merely CONTAINS the
# key (`"minMeshVersion": "3.0.0", "requires": [...]`, or a minified single-line index.json), whose
# other content no validator backs (#6097 review).
FLOOR_LINE = re.compile(r'\s*"minMeshVersion"\s*:\s*"[^"]*"\s*,?\s*')


def _floor_only_patch(patch: str | None) -> bool:
    changed = [l for l in (patch or "").splitlines()
               if l[:1] in "+-" and not l.startswith(("+++", "---")) and l[1:].strip()]
    return bool(changed) and all(FLOOR_LINE.fullmatch(l[1:]) for l in changed)


def generated_only(pr: dict, files: list | None, commits: list | None) -> tuple[bool, str]:
    """(True, why) when the pull request is a generated-files proposal by the App. Pure."""
    if not is_generated_bot(pr.get("user")):
        return False, "not authored by the generated-files App"
    if not files:
        return False, "its file listing is empty or unread"
    if len(files) < int(pr.get("changed_files") or 0):
        return False, f"the file listing returned {len(files)} of {pr.get('changed_files')} files"
    if not commits:
        return False, "its commit listing is empty or unread"
    if len(commits) < int(pr.get("commits") or 0):
        return False, f"the commit listing returned {len(commits)} of {pr.get('commits')} commits"
    if not all(is_generated_bot(c.get("author")) for c in commits):
        return False, "a commit on it is not the App's"
    for f in files:
        name = str(f.get("filename") or "")
        base = name.rsplit("/", 1)[-1]
        if base in GENERATED_BASENAMES:
            continue
        if base == "index.json" and name.count("/") == 1 and _floor_only_patch(f.get("patch")):
            continue
        return False, f"{name} is not a generated file"
    return True, f"{len(files)} generated file(s) by {pr['user'].get('login')}, every commit the App's"


def stage_readiness(pr: dict, comments: list, check_runs, now: str, since: str,
                    fallback_minutes: int = STAGE_FALLBACK_MINUTES,
                    files: list | None = None, commits: list | None = None, carry: Carry | None = None,
                    reviews=None) -> StageVerdict:
    """May stage 2 start for this head? Pure; `now` and `since` are ISO-8601 UTC stamps (`since` =
    when the head's CI run was created, i.e. when stage 1 began — for a pull request the App
    authored, when the PULL REQUEST was created: its head is rewritten on every main merge, and a
    per-head clock would restart forever)."""
    number = pr.get("number")
    head = str((pr.get("head") or {}).get("sha") or "")
    short = head[:10] or "(unknown)"
    if is_generated_bot(pr.get("user")):
        since = min(since, str(pr.get("created_at") or since)) if since else str(pr.get("created_at") or "")
        ok, why = generated_only(pr, files, commits)
        # A DRAFT is held like any other (#6097 review): the skip removes the review, never the
        # author's own "not ready yet".
        if ok and not pr.get("draft"):
            return StageVerdict(True, "generated", "", notes=(f"stage 1 skipped: {why} — nothing to review",))
    labels = {l.get("name") for l in pr.get("labels") or []}
    if TESTS_FIRST_LABEL in labels:
        return StageVerdict(True, "label", "", (
            f"STAGE 2 STARTED BEFORE THE REVIEW of {short}: label `{TESTS_FIRST_LABEL}` is on #{number}. "
            "Runner minutes only — arming still waits for this head's review and every answer."))
    if pr.get("draft"):
        return StageVerdict(False, "draft", f"#{number} is a draft — a draft is not reviewed, so stage 2 waits for ready-for-review "
                                            f"(or the label `{TESTS_FIRST_LABEL}` to run the suites before the review)")
    # Policy copilot-code-review: a landed Copilot review AGAINST THIS HEAD is the head's review,
    # checked first so no head waits out the fallback for an internal reviewer that is switched off.
    copilot = copilot_review_of_pull_request(reviews, head)
    if copilot is not None:
        return _stage_after_review(pr, comments, (copilot_note(copilot, short),), False)
    mine = internal_review_runs(check_runs, head)
    run = newest_internal_review_run(mine, None)
    # A clean merge of the base branch over a reviewed head carries that review (policy
    # review-carries-over-clean-base-merge): no wait, no new round. The head's OWN real review, when
    # it has one, always wins; a carry replaces only absent, running or degraded.
    if carry is not None and carry.ok and real_review_run(mine, head) is None:
        run = carry.run
    if run is None:
        t_now, t_since = parse_stamp(now), parse_stamp(since)
        if t_now is None or t_since is None:
            return StageVerdict(False, "unreadable", f"cannot tell how long head {short} has waited (now={now!r}, since={since!r}) — "
                                                     "not released on a guess")
        waited = (t_now - t_since).total_seconds() / 60
        running = [c for c in mine if c.get("status") != "completed"]
        state = (f"is still {running[0].get('status') or 'running'} (check run {running[0].get('id')})" if running
                 else "has not started")
        if waited >= fallback_minutes:
            return StageVerdict(True, "fallback", "", (
                f"REVIEW UNAVAILABLE: no Copilot review on this pull request, and head {short}'s `{DEGRADATION_CHECK_NAME}` review {state}, after {waited:.0f} min "
                f"(fallback {fallback_minutes} min) — stage 2 started WITHOUT it. Arming still waits for the review; "
                "if the reviewer is down, that is the incident to chase."))
        return StageVerdict(False, "waiting", (
            f"no Copilot review on this pull request yet, and head {short}'s `{DEGRADATION_CHECK_NAME}` review {state} ({waited:.0f} of {fallback_minutes} min) — "
            "stage 2 starts once a review lands and every finding is answered (on the event, or the stage-advance sweep for a Copilot review, whose own event starts no run), or at the fallback"),
            notes=(carry.why,) if carry is not None else ())
    title = ((run.get("output") or {}).get("title") or "").strip()
    carried = carry is not None and carry.ok and run is carry.run
    if degradation_of([run], None) is not None:
        return StageVerdict(True, "degraded", "", (
            f"REVIEWER UNAVAILABLE for head {short} (check run {run.get('id')}: \"{title}\") — stage 2 started without a review. "
            "Arming still refuses a degraded head; a person merges, and a post-merge review is owed."))
    notes = ((carry.why,) if carried else
             (f"`{DEGRADATION_CHECK_NAME}` completed on head {short}: {run.get('conclusion')} \"{title}\" (check run {run.get('id')})",))
    return _stage_after_review(pr, comments, notes, carried)


def _stage_after_review(pr: dict, comments: list, notes: tuple, carried: bool) -> StageVerdict:
    """Stage 1's thread condition, once this head's review holds; `notes` names the review."""
    incomplete = listing_incomplete(pr, comments)
    if incomplete:
        return StageVerdict(False, "unreadable", incomplete + " — it re-evaluates on the next event", notes=notes, carried=carried)
    roots, unanswered = reviewer_threads(comments)
    notes += (f"threads opened by the automatic reviewer: {len(roots)}, answered by a person: {len(roots) - len(unanswered)}",)
    if unanswered:
        first = unanswered[0]
        return StageVerdict(False, "unanswered", (
            f"{len(unanswered)} of {len(roots)} reviewer thread(s) have no reply from a person "
            f"(first: {first.get('html_url') or first.get('id')}) — reply to each (fixed, or why not); a fix push restarts at stage 0"),
            notes=notes, carried=carried)
    return StageVerdict(True, "carried" if carried else "reviewed", "", notes=notes, carried=carried)


def advance_action(pr: dict, run: dict | None, gate_job: dict | None, verdict: StageVerdict) -> tuple[str, str]:
    """What the event half does for ONE pull request: ('rerun' | 'wait' | 'none', why). Pure.

    `run` is the newest `pull_request` run of the caller's CI workflow for the PR's CURRENT head,
    `gate_job` that run's stage-gate job. Only a FAILED gate on the current head with a GREEN stage
    1 is re-run — never a run of an older head (no stale verdict carried), never a gate that is
    still evaluating (it reads live state itself), never a gate that already passed."""
    head = str((pr.get("head") or {}).get("sha") or "")
    if pr.get("state") not in (None, "open"):
        return "none", f"#{pr.get('number')} is {pr.get('state')}"
    if run is None:
        return "none", f"no pull_request CI run exists for head {head[:10]} yet — the push's own run evaluates stage 1"
    if run.get("head_sha") != head:
        return "none", f"run {run.get('id')} is for {str(run.get('head_sha'))[:10]}, not the current head {head[:10]} — a new head restarts at stage 0"
    if gate_job is None:
        return "none", f"run {run.get('id')} has no `{STAGE_GATE_JOB}` job — this caller has not adopted the stage gate"
    if gate_job.get("status") != "completed":
        return "none", f"the stage gate of run {run.get('id')} is still {gate_job.get('status')} — it evaluates live state itself"
    if gate_job.get("conclusion") == "success":
        return "none", f"the stage gate of run {run.get('id')} already passed — stage 2 started"
    if gate_job.get("conclusion") != "failure":
        return "none", f"the stage gate of run {run.get('id')} concluded {gate_job.get('conclusion')} — only a failed gate is re-run"
    if not verdict.ready:
        return "none", f"stage 1 still holds head {head[:10]}: {verdict.missing}"
    if run.get("status") != "completed":
        return "wait", f"run {run.get('id')} is still {run.get('status')} — a re-run needs it completed"
    if run.get("conclusion") == "cancelled":
        return "none", f"run {run.get('id')} was cancelled (superseded) — a cancelled run is never revived"
    return "rerun", f"stage 1 is green for {head[:10]} ({verdict.mode}) — re-running the failed jobs of run {run.get('id')}"


def post_rerun(gh, run_id) -> bool:
    """POST rerun-failed-jobs. True when this call re-ran the run; False when it lost a race (the sweep
    and a listener) and another invocation already re-ran it. Any other failure is RAISED: `Gh.post`
    and `Gh.api` both raise ReadError on a failed call, so a refused POST whose run is still
    `completed`, or an unreadable read-back, is never masked. Self-tested with a stub `gh`."""
    try:
        gh.post(f"actions/runs/{run_id}/rerun-failed-jobs")
        return True
    except ReadError:
        if lost_rerun_race(gh.api(f"actions/runs/{run_id}")):
            return False
        raise


def lost_rerun_race(readback) -> bool:
    """After a rerun POST failed: True only when the read-back run is a run object that is no longer
    `completed` — another invocation re-ran it. Anything else (still completed, an unreadable shape)
    is False, so the POST's failure is raised, never masked. Pure."""
    return isinstance(readback, dict) and bool(readback.get("status")) and readback.get("status") != "completed"


#: The event whose run of this check branch protection is KNOWN to read and that is re-run for the
#: pull request's head (measured on #4649; the manual remedy measured on #4652 and #4662).
PROTECTION_READ_EVENT = "pull_request"


def refresh_action(event: str, own_run_id, evaluated_at: str, repo: str, pr: dict,
                   candidate: dict | None) -> tuple[str, str]:
    """After a GREEN verdict: ('rerun' | 'none' | 'manual' | 'fail', why). Pure.

    `candidate` is the newest `pull_request` run of THIS workflow for the pull request's CURRENT
    head. It is re-run only when it can still carry a stale red that this green verdict supersedes —
    completed, not successful, and started BEFORE this verdict was taken. Everything else is a no-op
    that says why, which is what makes the refresh idempotent per (head sha, answered state): once
    re-run, the candidate is in flight (skip), then green (skip) or red on a NEWER read (skip)."""
    head = str((pr.get("head") or {}).get("sha") or "")
    if event == PROTECTION_READ_EVENT:
        return "none", f"this run IS a `{PROTECTION_READ_EVENT}` run — its own verdict is the one branch protection reads"
    if event == "merge_group":
        return "none", "a merge-queue entry is its own commit with its own check-run — nothing on the pull request head to refresh"
    if pr.get("state") not in (None, "open"):
        return "none", f"#{pr.get('number')} is {pr.get('state')}"
    head_repo = str(((pr.get("head") or {}).get("repo") or {}).get("full_name") or "")
    if head_repo and head_repo != repo:
        return "manual", (f"#{pr.get('number')} comes from the fork {head_repo}: a fork's token cannot re-run a workflow, so "
                          f"a maintainer re-runs the newest `{PROTECTION_READ_EVENT}` run of this check for head {head[:10]}")
    if candidate is None:
        return "fail", (f"no `{PROTECTION_READ_EVENT}` run of this workflow exists for head {head[:10]} — there is no "
                        "verdict branch protection reads; push to the pull request to start one")
    if candidate.get("head_sha") != head:
        return "none", f"run {candidate.get('id')} is for {str(candidate.get('head_sha'))[:10]}, not the current head {head[:10]}"
    if str(candidate.get("id")) == str(own_run_id):
        return "none", f"run {candidate.get('id')} is this run"
    if candidate.get("status") != "completed":
        return "none", f"`{PROTECTION_READ_EVENT}` run {candidate.get('id')} is still {candidate.get('status')} — it reads live state itself"
    if candidate.get("conclusion") == "success":
        return "none", f"`{PROTECTION_READ_EVENT}` run {candidate.get('id')} already reads success — nothing to refresh"
    started, judged = parse_stamp(candidate.get("run_started_at")), parse_stamp(evaluated_at)
    if judged is None:
        return "fail", f"the verdict's own time {evaluated_at!r} is unreadable, so staleness cannot be judged"
    if started is not None and started >= judged:
        return "none", (f"`{PROTECTION_READ_EVENT}` run {candidate.get('id')} ({candidate.get('conclusion')}) started at "
                        f"{candidate.get('run_started_at')}, not before this verdict ({evaluated_at}) — it judged a state no older "
                        "than this one, so it is not re-run")
    return "rerun", (f"this `{event}` verdict is GREEN but branch protection reads `{PROTECTION_READ_EVENT}` run "
                     f"{candidate.get('id')}, which concluded {candidate.get('conclusion')} before it — re-running its failed jobs")


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

    def post(self, path: str) -> None:
        p = subprocess.run(["gh", "api", "-X", "POST", "-H", "Accept: application/vnd.github+json", f"repos/{self.repo}/{path}"],
                           capture_output=True, text=True, check=False)
        if p.returncode != 0:
            raise ReadError(f"POST repos/{self.repo}/{path} failed ({p.returncode}): {(p.stderr or p.stdout).strip()[:600]}")

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
    # The degradation is read on EVERY run too, for the same reason: a token that cannot list check
    # runs must be discovered now, not on the day the reviewer is down. It is read off the pull
    # request's HEAD (pr.head.sha) on every path — for a merge-queue entry too, whose own sha is the
    # queue's merge commit, where the reviewer never posts. `filter=all` so `--as-of` can see a run
    # that a newer one has since superseded.
    head_sha = (pr.get("head") or {}).get("sha") or ""
    if not re.fullmatch(r"[0-9a-f]{40}", head_sha):
        raise ReadError(f"pulls/{number} reported no head sha, so its `{DEGRADATION_CHECK_NAME}` check runs cannot be read")
    listing = gh.api(f"commits/{head_sha}/check-runs?check_name={DEGRADATION_CHECK_NAME}&filter=all&per_page=100")
    check_runs = (listing or {}).get("check_runs") if isinstance(listing, dict) else None
    if not isinstance(check_runs, list) or not isinstance(listing.get("total_count"), int) \
            or len(check_runs) < listing["total_count"]:
        raise ReadError(f"commits/{head_sha[:10]}/check-runs did not return the complete `{DEGRADATION_CHECK_NAME}` listing")
    files = commits = None
    if is_generated_bot(pr.get("user")):
        # Only for the generated-files App's own pull requests — what `generated_only` judges provenance by.
        files = gh.api(f"pulls/{number}/files?per_page=100", paginate=True)
        commits = gh.api(f"pulls/{number}/commits?per_page=100", paginate=True)
    return pr, reviews, comments, Waiver(present, events, roles), author_role, check_runs, files, commits


def read_arm_inputs(gh: Gh, number: int, carry: bool = True):
    """The arm gate's reads — the pull request (its `review_comments` count FIRST, so it bounds the
    listing), its review comments, the `internal-review` runs on its head, and — only when that head
    has no real review of its own — the carry-over (`read_carry`; `carry=False` is the negative control)."""
    pr = gh.api(f"pulls/{number}")
    if not isinstance(pr, dict) or pr.get("number") != number:
        raise ReadError(f"pulls/{number} did not return pull request #{number}")
    comments = gh.api(f"pulls/{number}/comments?per_page=100", paginate=True)
    head_sha = (pr.get("head") or {}).get("sha") or ""
    if not re.fullmatch(r"[0-9a-f]{40}", head_sha):
        raise ReadError(f"pulls/{number} reported no head sha")
    check_runs = internal_review_listing(gh, head_sha)
    return pr, comments, check_runs, (read_carry(gh, pr, check_runs) if carry else None)


def read_reviews(gh: Gh, number: int) -> list:
    """Every review on the pull request, all pages — the arm and stage gates look for the pull request's
    Copilot review among them, on any of its heads (`copilot_review_of_pull_request`, policy
    review-once-per-pull-request). A failed
    read raises: the gates then answer "cannot read", never "not reviewed"."""
    reviews = gh.api(f"pulls/{number}/reviews?per_page=100", paginate=True)
    if not isinstance(reviews, list):
        raise ReadError(f"pulls/{number}/reviews did not return a listing")
    return reviews


def read_compare(gh: Gh, base_sha: str, sha: str) -> dict:
    """compare(base...sha) — merge-base(base, sha)..sha, the pull request's own diff — with EVERY commit:
    the compare API sends `files` on the first page only and pages the commits, so the commits are
    paged until `total_commits`; a short listing is a ReadError, never a partial answer."""
    first = gh.api(f"compare/{base_sha}...{sha}?per_page=100")
    if not isinstance(first, dict) or not isinstance(first.get("commits"), list):
        raise ReadError(f"compare/{base_sha[:10]}...{sha[:10]} did not return a comparison")
    commits, total = list(first["commits"]), first.get("total_commits")
    for page in range(2, 21):
        if not isinstance(total, int) or len(commits) >= total:
            break
        more = (gh.api(f"compare/{base_sha}...{sha}?per_page=100&page={page}") or {}).get("commits") or []
        if not more:
            break
        commits += more
    if not isinstance(total, int) or len(commits) < total:
        raise ReadError(f"compare/{base_sha[:10]}...{sha[:10]} returned {len(commits)} of {total} commits")
    return dict(first, commits=commits)


def internal_review_listing(gh: Gh, sha: str) -> list:
    """Every `internal-review` check run on `sha`; a short listing is a ReadError."""
    listing = gh.api(f"commits/{sha}/check-runs?check_name={DEGRADATION_CHECK_NAME}&filter=all&per_page=100")
    runs = (listing or {}).get("check_runs") if isinstance(listing, dict) else None
    if not isinstance(runs, list) or not isinstance(listing.get("total_count"), int) or len(runs) < listing["total_count"]:
        raise ReadError(f"commits/{sha[:10]}/check-runs did not return the complete `{DEGRADATION_CHECK_NAME}` listing")
    return runs


def read_carry(gh: Gh, pr: dict, check_runs) -> Carry | None:
    """The carry-over for the pull request's head (policy review-carries-over-clean-base-merge), or None
    when the head has a real review of its own (nothing to carry). Every input comes from the
    repository through REST — the base branch's tip, compare(base...head), the walked commits'
    `internal-review` runs, compare(base...reviewed head) — never from a commit message. A failed read
    does NOT carry: the head is reviewed fresh, exactly as before this policy."""
    head = str((pr.get("head") or {}).get("sha") or "")
    if real_review_run(check_runs, head) is not None:
        return None
    try:
        base_ref = str((pr.get("base") or {}).get("ref") or "")
        if not base_ref:
            raise ReadError("the pull request names no base branch")
        branch = gh.api(f"branches/{urllib.parse.quote(base_ref, safe='')}")
        base_sha = str(((branch or {}).get("commit") or {}).get("sha") or "")
        if not re.fullmatch(r"[0-9a-f]{40}", base_sha):
            raise ReadError(f"branches/{base_ref} reported no tip sha")
        new_cmp = read_compare(gh, base_sha, head)
        own = {c.get("sha"): c for c in new_cmp["commits"]}
        old, run, _walked, why = find_reviewed_ancestor(head, own, lambda sha: internal_review_listing(gh, sha))
        if old is None:
            return Carry(False, f"no carry-over: {why} — the head owes a fresh review")
        return carry_over(head, new_cmp, read_compare(gh, base_sha, old), old, run)
    except (ReadError, KeyError, TypeError, AttributeError) as e:
        return Carry(False, f"no carry-over: its inputs could not be read ({e}) — the head owes a fresh review")


def read_required_contexts(gh: Gh, base: str) -> list:
    """The base branch's required status-check contexts, from BOTH places protection lives (rulesets
    and classic protection — the fleet is split between them). A 404 from one is 'look in the
    other'; nothing readable from either is an empty list, which the predicate refuses."""
    names = []
    try:
        for rule in gh.api(f"rules/branches/{base}") or []:
            if rule.get("type") == "required_status_checks":
                names += [c.get("context") for c in (rule.get("parameters") or {}).get("required_status_checks") or []]
    except ReadError:
        pass
    try:
        prot = (gh.api(f"branches/{base}") or {}).get("protection") or {}
        names += list((prot.get("required_status_checks") or {}).get("contexts") or [])
    except ReadError:
        pass
    return [n for n in dict.fromkeys(names) if n]


def read_head_runs(gh: Gh, head_sha: str) -> list:
    """Every check run on the head, all pages; a short listing is a ReadError, never a partial answer."""
    runs, total = [], None
    for page in range(1, 21):
        data = gh.api(f"commits/{head_sha}/check-runs?filter=all&per_page=100&page={page}")
        if not isinstance(data, dict) or not isinstance(data.get("check_runs"), list):
            raise ReadError(f"commits/{head_sha[:10]}/check-runs page {page} did not return a listing")
        total = data.get("total_count")
        runs += data["check_runs"]
        if not data["check_runs"] or (isinstance(total, int) and len(runs) >= total):
            break
    if not isinstance(total, int) or len(runs) < total:
        raise ReadError(f"commits/{head_sha[:10]}/check-runs returned {len(runs)} of {total} runs")
    return runs


def run_arm_gate(repo: str, number: int, carry: bool = True) -> int:
    """Prints the verdict, writes ONE line to the job summary and `ready=true|false` to
    $GITHUB_OUTPUT. Exit 0 either way — not-ready is an answer, not a failure; a read that cannot
    complete is NOT ready (never armed on a guess) and says so."""
    try:
        gh = Gh(repo)
        pr, comments, check_runs, carried = read_arm_inputs(gh, number, carry)
        head = (pr.get("head") or {}).get("sha") or ""
        required = read_required_contexts(gh, (pr.get("base") or {}).get("ref") or "main")
        verdict = arm_readiness(pr, comments, check_runs, required, read_head_runs(gh, head), carried,
                                read_reviews(gh, number))
    except (ReadError, KeyError) as e:
        verdict = ArmVerdict(False, f"cannot read #{number}'s review state, so it is not armed on a guess: {e}")
        print(f"::warning::{verdict.missing}")
    for n in verdict.notes:
        print(f"  {n}")
    # Name the review that released the gate from the verdict's own notes — Copilot, carried or
    # internal — so the line never claims a reviewer that did not run.
    released_by = next((n for n in verdict.notes if n.startswith(("review CARRIED", "Copilot reviewed head"))),
                       "internal review completed on the current head")
    line = (f"Ready to arm #{number}: {released_by}; every reviewer thread is answered, and every required check is green."
            if verdict.ready else f"Not armed #{number}: {verdict.missing}")
    print(line)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as f:
            f.write(f"ready={'true' if verdict.ready else 'false'}\n")
    return 0


def utc_now() -> str:
    return datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%dT%H:%M:%SZ")


def _append(env_key: str, text: str) -> None:
    path = os.environ.get(env_key)
    if path:
        with open(path, "a", encoding="utf-8") as f:
            f.write(text)


def read_stage_inputs(gh: Gh, number: int, carry: bool = True):
    """The arm gate's reads (the carry-over included), plus — only for a pull request the
    generated-files App authored — its files (with patches) and commits, so `generated_only` can judge
    provenance. A failed read raises."""
    pr, comments, check_runs, carried = read_arm_inputs(gh, number, carry)
    files = commits = None
    if is_generated_bot(pr.get("user")):
        files = gh.api(f"pulls/{number}/files?per_page=100", paginate=True)
        commits = gh.api(f"pulls/{number}/commits?per_page=100", paginate=True)
    return pr, comments, check_runs, files, commits, carried


def run_stage_gate(repo: str, number: int, since: str, fallback_minutes: int, carry: bool = True) -> int:
    """The stage gate job: exit 0 when stage 2 may start, 1 (RED, named) when it may not. A RED here
    is a HOLD, not a defect: the heavy legs skip, the required aggregators read red naming this line,
    and the event half (`--stage-advance`) re-runs the failed jobs when stage 1 turns green."""
    try:
        gh = Gh(repo)
        pr, comments, check_runs, files, commits, carried = read_stage_inputs(gh, number, carry)
        verdict = stage_readiness(pr, comments, check_runs, utc_now(), since, fallback_minutes, files, commits, carried,
                                  read_reviews(gh, number))
    except (ReadError, KeyError) as e:
        verdict = StageVerdict(False, "unreadable", f"cannot read #{number}'s review state, so stage 2 is not started on a guess: {e}")
    for n in verdict.notes:
        print(f"  {n}")
    if verdict.ready and verdict.loud:
        print(f"::warning title=Stage 2 released without a completed review ({verdict.mode})::{verdict.loud}")
        line = f"⚠️ Stage 2 started for #{number} — {verdict.loud}"
    elif verdict.ready and verdict.mode == "generated":
        line = f"✅ Stage 1 not owed for #{number}: {verdict.notes[0] if verdict.notes else 'generated files only'} — stage 2 starts."
    elif verdict.ready and verdict.carried:
        line = (f"✅ Stage 1 green for #{number} by CARRY-OVER — no new review round: {verdict.notes[0]}; "
                "every reviewer thread is answered — stage 2 starts.")
    elif verdict.ready:
        line = f"✅ Stage 1 green for #{number}: the head's review landed and every reviewer thread is answered — stage 2 starts."
    else:
        # The MESSAGE carries the marker (STAGE_HOLD_MARKER): an annotation's title is not what a reader
        # of the check's annotations sees first, and the PR babysitter keys its "a hold, not a defect"
        # class on the message (MeshWeaver.Plugins PrBabysitter.StageHoldMarker).
        print(f"::error title=Stage 2 held — stage 1 ({verdict.mode})::{STAGE_HOLD_MARKER}stage 1, {verdict.mode}): {verdict.missing}")
        line = f"⏸ Stage 2 held for #{number} ({verdict.mode}): {verdict.missing}"
        carry_notes = [n for n in verdict.notes if n.startswith("no carry-over")]
        if carry_notes:
            line += f"\n\n🔁 {carry_notes[0]}"
    print(line)
    _append("GITHUB_STEP_SUMMARY", "### Staged pipeline — stage 1\n\n" + line + "\n")
    _append("GITHUB_OUTPUT", f"ready={'true' if verdict.ready else 'false'}\nmode={verdict.mode}\n")
    return 0 if verdict.ready else 1


def newest_ci_run(gh: Gh, workflow: str, head_sha: str) -> dict | None:
    listing = gh.api(f"actions/workflows/{workflow}/runs?head_sha={head_sha}&event=pull_request&per_page=50")
    runs = (listing or {}).get("workflow_runs") if isinstance(listing, dict) else None
    if not isinstance(runs, list):
        raise ReadError(f"actions/workflows/{workflow}/runs?head_sha={head_sha[:10]} did not return a run listing")
    return max(runs, key=lambda r: (r.get("created_at") or "", r.get("id") or 0)) if runs else None


def stage_gate_job(gh: Gh, run_id: int) -> dict | None:
    # The jobs listing pages are objects ({total_count, jobs}), not lists, so they are paged by hand;
    # a short listing is an unreadable one (a gate job missing from page 3 must not read as absent).
    flat: list = []
    for page in range(1, 11):
        data = gh.api(f"actions/runs/{run_id}/jobs?filter=latest&per_page=100&page={page}")
        if not isinstance(data, dict) or not isinstance(data.get("jobs"), list) or not isinstance(data.get("total_count"), int):
            raise ReadError(f"actions/runs/{run_id}/jobs page {page} did not return a job listing")
        flat += data["jobs"]
        if len(flat) >= data["total_count"] or not data["jobs"]:
            break
    if len(flat) < (data.get("total_count") or 0):
        raise ReadError(f"actions/runs/{run_id}/jobs returned {len(flat)} of {data.get('total_count')} jobs")
    gates = [j for j in flat if str(j.get("name") or "").endswith(STAGE_GATE_JOBS)]
    return max(gates, key=lambda j: (j.get("started_at") or "", j.get("id") or 0)) if gates else None


def advance_one(gh: Gh, number: int, workflow: str, fallback_minutes: int, wait_minutes: int) -> str:
    pr = gh.api(f"pulls/{number}")
    head = str((pr.get("head") or {}).get("sha") or "")
    run = newest_ci_run(gh, workflow, head) if re.fullmatch(r"[0-9a-f]{40}", head) else None
    job = stage_gate_job(gh, int(run["id"])) if run else None
    verdict = StageVerdict(False, "unread", "not evaluated")
    if job is not None and job.get("status") == "completed" and job.get("conclusion") == "failure":
        _, comments, check_runs, files, commits, carried = read_stage_inputs(gh, number)
        verdict = stage_readiness(pr, comments, check_runs, utc_now(), str(run.get("created_at") or ""), fallback_minutes,
                                  files, commits, carried, read_reviews(gh, number))
    action, why = advance_action(pr, run, job, verdict)
    deadline = time.monotonic() + wait_minutes * 60
    waited = action == "wait"
    while action == "wait" and time.monotonic() < deadline:
        print(f"  #{number}: {why}")
        time.sleep(30)
        run = gh.api(f"actions/runs/{run['id']}")
        action, why = advance_action(pr, run, job, verdict)
    if action == "wait":
        raise ReadError(f"#{number}: {why} — still not completed after {wait_minutes} min; the sweep re-tries")
    if action == "rerun" and waited:
        # 🚨 Everything judged before the wait is stale now (#6070 review): a fix push may have made a
        # new head (and cancelled this run), the PR may be closed, an answer may have been deleted.
        # Re-read the pull request and re-judge stage 1 before POSTing, never on the snapshot.
        pr = gh.api(f"pulls/{number}")
        _, comments, check_runs, files, commits, carried = read_stage_inputs(gh, number)
        verdict = stage_readiness(pr, comments, check_runs, utc_now(), str(run.get("created_at") or ""), fallback_minutes,
                                  files, commits, carried, read_reviews(gh, number))
        action, why = advance_action(pr, run, job, verdict)
    if action == "rerun":
        if not post_rerun(gh, run["id"]):
            print(f"  #{number}: run {run['id']} was already re-run by another invocation")
            return "none"
        if verdict.loud:
            print(f"::warning title=Stage 2 released without a completed review ({verdict.mode})::#{number}: {verdict.loud}")
        print(f"::notice::#{number}: {why} — {run.get('html_url')}")
    else:
        print(f"  #{number}: {why}")
    return action


def run_stage_advance(repo: str, workflow: str, *, pr: int | None, head_sha: str | None, sweep: bool,
                      fallback_minutes: int, wait_minutes: int, max_per_run: int = 25) -> int:
    gh = Gh(repo)
    errors: list[str] = []
    try:
        if pr is not None:
            numbers = [pr]
        elif head_sha:
            pulls = gh.api(f"commits/{head_sha}/pulls?per_page=100")
            numbers = [p["number"] for p in pulls or () if p.get("state") == "open"
                       and (p.get("head") or {}).get("sha") == head_sha]
            if not numbers:
                print(f"  no OPEN pull request has {head_sha[:10]} as its head — nothing to advance (an older head never is)")
        else:
            # The fallback timer: every open pull request whose newest CI run FAILED in the last six
            # hours. One listing of open PRs and one of failed runs; per-PR reads only for those.
            open_heads = {p["head"]["sha"]: p["number"] for p in gh.api("pulls?state=open&per_page=100", paginate=True)
                          if not p.get("draft") and (p.get("head") or {}).get("sha")}
            cutoff = (datetime.datetime.now(datetime.UTC) - datetime.timedelta(hours=6)).strftime("%Y-%m-%dT%H:%M:%SZ")
            # Paged by hand (the pages are objects): a mass outage is exactly when more than one page
            # of failed runs exists, and a head on page 2 must not wait for page 1 to age out.
            failed: set = set()
            for page in range(1, 11):
                listing = gh.api(f"actions/workflows/{workflow}/runs?event=pull_request&status=failure"
                                 f"&created=%3E%3D{cutoff}&per_page=100&page={page}")
                runs = (listing or {}).get("workflow_runs") if isinstance(listing, dict) else None
                if not isinstance(runs, list):
                    raise ReadError(f"the failed-run listing of {workflow} (page {page}) is unreadable")
                failed |= {r.get("head_sha") for r in runs}
                if len(runs) < 100:
                    break
            numbers = sorted({open_heads[s] for s in failed if s in open_heads})[:max_per_run]
            print(f"sweep: {len(open_heads)} open non-draft PR(s), {len(failed)} head(s) with a failed run since {cutoff}; examining {numbers}")
    except ReadError as e:
        print(f"::error::cannot list what to advance: {e}")
        return 1
    for number in numbers:
        try:
            advance_one(gh, number, workflow, fallback_minutes, wait_minutes)
        except (ReadError, KeyError, TypeError) as e:
            errors.append(f"#{number}: {e}")
    for e in errors:
        print(f"::error::stage advance failed — {e}")
    return 1 if errors else 0


def run_refresh(repo: str, number: int, event: str, run_id: str, evaluated_at: str) -> int:
    """The self-refresh job: exit 0 when it re-ran the read run or had nothing to do (both printed),
    1 (RED, named) when it could not read or could not re-run. It never changes a verdict itself."""
    try:
        gh = Gh(repo)
        own = gh.api(f"actions/runs/{run_id}")
        workflow_id = own.get("workflow_id") if isinstance(own, dict) else None
        if not workflow_id:
            raise ReadError(f"actions/runs/{run_id} named no workflow_id")
        pr = gh.api(f"pulls/{number}")
        if not isinstance(pr, dict) or pr.get("number") != number:
            raise ReadError(f"pulls/{number} did not return pull request #{number}")
        head = str((pr.get("head") or {}).get("sha") or "")
        if not re.fullmatch(r"[0-9a-f]{40}", head):
            raise ReadError(f"pulls/{number} reported no head sha")
        listing = gh.api(f"actions/workflows/{workflow_id}/runs?head_sha={head}&event={PROTECTION_READ_EVENT}&per_page=50")
        runs = listing.get("workflow_runs") if isinstance(listing, dict) else None
        if not isinstance(runs, list):
            raise ReadError(f"actions/workflows/{workflow_id}/runs?head_sha={head[:10]} did not return a run listing")
        candidate = max(runs, key=lambda r: (r.get("created_at") or "", r.get("id") or 0)) if runs else None
        action, why = refresh_action(event, run_id, evaluated_at, repo, pr, candidate)
        if action == "rerun" and not post_rerun(gh, candidate["id"]):
            action, why = "none", f"run {candidate['id']} was already re-run by another invocation"
    except (ReadError, KeyError, TypeError) as e:
        print(f"::error::the protection-read run of #{number} was NOT refreshed: {e}")
        return 1
    if action == "rerun":
        print(f"::notice::#{number}: {why} — {candidate.get('html_url')}")
    elif action == "manual":
        print(f"::warning::#{number}: {why}")
    elif action == "fail":
        print(f"::error::#{number}: {why}")
        return 1
    else:
        print(f"  #{number}: {why}")
    _append("GITHUB_STEP_SUMMARY", f"Refresh of the protection-read verdict for #{number}: {action} — {why}\n")
    return 0


def render(number: int, pr: dict, verdict: Verdict, author_role: str | None, as_of: str | None) -> str:
    head = f"#{number} ({'draft' if pr.get('draft') else pr.get('state')}) head {str((pr.get('head') or {}).get('sha'))[:10]}"
    state = (("GREEN — REVIEWER UNAVAILABLE, review deferred (degradation, not a review)" if verdict.degraded else "GREEN")
             if verdict.green else ("RED — UNREVIEWABLE (the reviewer REFUSED to review this pull request)"
                                    if verdict.refused else "RED"))
    lines = [f"check-review-answered: {head}{' as of ' + as_of if as_of else ''} — {state}"]
    if verdict.degraded:
        # A GitHub annotation, so the degradation shows on the run page and not only in its log.
        lines.append(f"::warning::{verdict.degraded}")
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
    if verdict.refused:
        # 🚨 NOT "the review must land" (#4730). The reviewer answered: it said no. Nothing that can
        # be done TO this pull request changes that — there are no findings to reply to, and a push,
        # a re-run or a new commit all re-ask a question that is being refused for a reason outside
        # the pull request. Saying "it usually arrives minutes after the pull request opens" here is
        # how an unreviewable pull request read as one that merely had to wait.
        out.append("nothing on this pull request can answer this — the automatic reviewer REFUSED to review it "
                   f"(\"{verdict.refused[0]}\"). There are no findings to reply to and no commit that changes the "
                   "answer; pushing, re-running this check and replying to threads all leave it exactly here. It "
                   "clears when the reviewer can review again — a maintainer re-requests the review then, or applies "
                   f"the `{WAIVER_LABEL}` label — never an agent, and never automatically.")
    elif "has not landed" in text:
        out.append("the automatic review must land. It usually arrives minutes after the pull request opens; if the "
                   "reviewer refused (quota) or cannot review this change, a maintainer re-requests the review, or applies "
                   f"the `{WAIVER_LABEL}` label to waive it — never an agent, and never automatically. If the internal "
                   f"reviewer is DOWN, its own App posts a `{DEGRADATION_CHECK_NAME}` check run \"{DEGRADATION_TITLE_PREFIX} …\" "
                   "(neutral) on the head, which releases this condition by itself and re-runs this check.")
    if verdict.unanswered:
        out.append("reply to each unanswered thread (fixed, or why not). Resolving a thread is not a reply, and a waiver "
                   "does not release a finding the reviewer did post.")
    if "comment listing" in text:
        out.append("nothing on the pull request needs to change — the check could not prove it read every comment; "
                   "re-run this check once the read can complete.")
    return out


def summary_markdown(number: int, verdict: Verdict) -> str:
    state = (("✅ green — ⚠️ **reviewer unavailable: degraded, review deferred**" if verdict.degraded else "✅ green")
             if verdict.green
             else "❌ red — **unreviewable right now**: the reviewer refused to review this pull request"
             if verdict.refused else "❌ red")
    out = [f"### Automatic review answered — #{number}: {state}", ""]
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
            pr, reviews, comments, waiver, author_role, check_runs, files, commits = read_inputs(gh, number, as_of)
        except (ReadError, KeyError) as e:
            print(f"::error::check-review-answered cannot read the review of #{number}, so it cannot say it was answered: {e}")
            return 1
        verdict = evaluate(pr, reviews, comments, waiver, as_of, check_runs, files, commits)
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
INTERNAL_REVIEWER_USER = {"login": "systemorph-com[bot]", "type": "Bot", "id": INTERNAL_REVIEWER_ACCOUNT_ID}
PERSON = {"login": "rbuergi", "type": "User", "id": 6334612}
OTHER_BOT = {"login": "github-actions[bot]", "type": "Bot", "id": 41898282}
REVIEW_BODY_SEPT = "### 🟡 Changes recommended\n\n<details>\n<summary>Pull request overview</summary>\n\n- **Files reviewed:** 3/3 changed files\n</details>"
REVIEW_BODY_JULY = "## Pull request overview\n\nThis PR adjusts the Distributed portal's mesh-level content-collection…"
REFUSAL_QUOTA = "Copilot was unable to review this pull request because the user who requested the review has reached their quota limit."
REFUSAL_NO_FILES = "### 🔵 Needs a closer look\n\n<summary>Pull request overview</summary>\n\n- **Files reviewed:** 0/4 changed files"
# The three bodies the reviewer actually posted on 2026-09-18 (#4725, #4728, #4721), verbatim in
# shape: a verdict heading and a sentence or two, with NO overview block. Every one of these was a
# real review that the pre-2026-09-18 marker rule classified as "not landed".
REVIEW_BODY_APPROVE_0918 = "### 🟢 Approval recommended\n\nAll reviewed changes are covered and no unresolved blocking issues remain."
REVIEW_BODY_CHANGES_0918 = "### 🟡 Changes recommended\n\nTwo small but concrete correctness issues were found in the updated BuildNodeType comments/logging."
REVIEW_BODY_CLOSER_0918 = "### 🔵 Needs a closer look\n\nConditional `[JsonIgnore]` cases remain insufficiently covered and may omit editor-written values."


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
DEGRADATION_APP = {"id": DEGRADATION_APP_ID, "slug": DEGRADATION_APP_SLUG, "owner": {"login": "Systemorph"}}
DEGRADED_TITLE = "Reviewer unavailable — round aborted at the 30-minute cap twice"
DEGRADED_SUMMARY = "Cause: GLM-5.3 provider timeouts (infrastructure). Re-kicked once at 13:50Z; aborted again."


def _check_run(name=DEGRADATION_CHECK_NAME, app=DEGRADATION_APP, status="completed", conclusion=DEGRADATION_CONCLUSION,
               title=DEGRADED_TITLE, at="2026-09-14T12:40:00Z", crid=900):
    return {"id": crid, "name": name, "app": app, "status": status, "conclusion": conclusion,
            "completed_at": at if status == "completed" else None,
            "output": {"title": title, "summary": DEGRADED_SUMMARY}}


def self_test() -> int:
    """Every case names the EXACT reasons it must be red for, in evaluation order (completeness,
    landed, threads). A case that goes red for a different reason — or for an extra one — fails,
    so a fixture cannot pass by being wrong in a second way."""
    failures = 0
    LISTING, NOT_LANDED, UNANSWERED = "comment listing", "has not landed", "have no reply from a person"

    def case(name: str, expect: tuple[str, ...], pr, reviews, comments, waiver=NO_WAIVER, as_of=None,
             mention: tuple[str, ...] = (), says: tuple[str, ...] = (), never_says: tuple[str, ...] = (),
             check_runs=()):
        """`says`/`never_says` assert what the READER is told — the guidance line and the step
        summary — not just the verdict. #4730 was entirely about those two strings being wrong while
        the verdict was right, so a case that checks only `reasons` cannot see it."""
        nonlocal failures
        v = evaluate(pr, reviews, comments, waiver, as_of, check_runs)
        text = "\n".join(v.reasons + v.notes)
        # 🚨 render() is IN here (#4730 review). Without it the run headline — the first of the
        # three surfaces the doc promises — could be deleted with every case still green.
        told = ("\n".join(guidance(v)) + "\n" + summary_markdown(0, v) + "\n"
                + render(0, pr, v, None, as_of))
        ok = (v.green == (not expect) and len(v.reasons) == len(expect)
              and all(e in r for e, r in zip(expect, v.reasons)) and all(m in text for m in mention)
              and all(t in told for t in says) and not any(t in told for t in never_says))
        failures += 0 if ok else 1
        want = "green" if not expect else "red: " + " + ".join(expect)
        got = "green" if v.green else "red: " + " | ".join(v.reasons)
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={want}"
              + ("" if ok else f"\n               got={got}\n               notes={v.notes}\n               told={told}"))

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
    # 🚨 #4730: the verdict above was always right; what the reader was TOLD was not. A refusal must
    # name itself and must NOT be given the wait-for-it remedy, which is the sentence that put six
    # unreviewable pull requests in a four-hour holding pattern on 2026-09-18.
    case("a refusal TELLS the reader it is unreviewable, not that the review is coming",
         (NOT_LANDED,), _pr(0), [_review(REFUSAL_QUOTA)], [],
         says=("RED — UNREVIEWABLE", "unreviewable right now", "REFUSED to review it",
               "pushing, re-running this check"),
         never_says=("usually arrives minutes after",))
    # The other side of the same change — a pull request the reviewer simply has not reached yet
    # keeps the wait-for-it remedy and must NOT be called unreviewable.
    case("no review yet still says the review is coming, and is NOT called unreviewable",
         (NOT_LANDED,), _pr(0), [], [],
         says=("usually arrives minutes after",),
         never_says=("unreviewable right now", "REFUSED to review it", "RED — UNREVIEWABLE"))
    # …and a WAIVED refusal is not unreviewable either: it is released, so the banner must not fire.
    case("a waived refusal is not reported as unreviewable", GREEN, _pr(0, [WAIVER_LABEL]), [_review(REFUSAL_QUOTA)], [],
         Waiver(True, (_labeled(PERSON),), {"rbuergi": "maintain"}),
         never_says=("unreviewable right now", "RED — UNREVIEWABLE"))
    case("zero files reviewed is not a review", (NOT_LANDED,), _pr(0), [_review(REFUSAL_NO_FILES)], [])
    # POLICY, changed 2026-09-18: an unfamiliar body is a NEW FORMAT, not an absence. This case
    # asserted the opposite until the reviewer dropped its overview block and six reviewed core
    # pull requests all read as "not landed" on a required context.
    case("an unfamiliar body from the reviewer IS a review", GREEN, _pr(0), [_review("Something new happened.")], [],
         mention=("automatic review landed",))
    # The three real 2026-09-18 bodies. Each is RED under the old marker rule and GREEN now.
    case("2026-09-18 body: approval recommended", GREEN, _pr(0), [_review(REVIEW_BODY_APPROVE_0918)], [],
         mention=("automatic review landed",))
    case("2026-09-18 body: changes recommended", GREEN, _pr(0), [_review(REVIEW_BODY_CHANGES_0918)], [],
         mention=("automatic review landed",))
    case("2026-09-18 body: needs a closer look", GREEN, _pr(0), [_review(REVIEW_BODY_CLOSER_0918)], [],
         mention=("automatic review landed",))
    # …and the verdict heading alone must not become a blanket pass: a refusal that happens to
    # carry one is still a refusal, on the SAME heading as the third case above.
    case("a 2026-09-18-shaped body that is a refusal is still refused", (NOT_LANDED,), _pr(0),
         [_review("### 🔵 Needs a closer look\n\n- **Files reviewed:** 0/4 changed files")], [])
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

    # The internal GLM-5.3 reviewer (systemorph-com[bot]) — accepted alongside Copilot (policy `internal-code-review`).
    case("the internal reviewer's review lands the review", GREEN, _pr(0),
         [_review("**Internal review (GLM-5.3)** — no blocking findings.", user=INTERNAL_REVIEWER_USER)], [])
    case("the internal reviewer at CHANGES_REQUESTED still landed", GREEN, _pr(0),
         [_review("**Internal review (GLM-5.3)** — 1 blocking finding.", user=INTERNAL_REVIEWER_USER, state="CHANGES_REQUESTED")], [])
    case("an internal-reviewer thread with no reply is unanswered", (UNANSWERED,), _pr(1),
         [_review(user=INTERNAL_REVIEWER_USER)], [_comment(1, INTERNAL_REVIEWER_USER)], mention=("1 of 1",))
    case("an internal-reviewer thread answered by a person is green", GREEN, _pr(2),
         [_review(user=INTERNAL_REVIEWER_USER)], [_comment(1, INTERNAL_REVIEWER_USER), _comment(11, PERSON, 1, "2026-09-14T13:00:00Z")])
    case("either reviewer lands it; BOTH reviewers' threads need replies", (UNANSWERED,), _pr(3),
         [_review(), _review(user=INTERNAL_REVIEWER_USER, rid=2)],
         [_comment(1), _comment(2, INTERNAL_REVIEWER_USER), _comment(11, PERSON, 1, "2026-09-14T13:00:00Z")], mention=("1 of 2",))
    case("a User account named systemorph-com[bot] is not the reviewer", (NOT_LANDED,), _pr(0),
         [_review(user={"login": "systemorph-com[bot]", "type": "User", "id": 7})], [])

    # the reviewer-unavailable degradation (MeshWeaver.Feedback#86) — releases condition 1 ONLY, and
    # only for the reviewer's own App, under the contract's name, conclusion and title. Every
    # negative control below carries the SAME remaining fields as the positive case, so each one
    # proves that exactly its one differing field is load-bearing.
    DEGRADED = ("REVIEWER UNAVAILABLE", "DEFERRED", DEGRADED_SUMMARY)
    case("degradation: the reviewer's App says it is unavailable → green, and SAYS so", GREEN, _pr(0), [], [],
         check_runs=[_check_run()], mention=("DEGRADED", "check run 900"),
         says=("GREEN — REVIEWER UNAVAILABLE",) + DEGRADED + (DEGRADED_TITLE,))
    case("degradation: a real review needs no degradation and is not called degraded", GREEN, _pr(0), [_review()], [],
         check_runs=[_check_run()], never_says=("REVIEWER UNAVAILABLE",))
    case("degradation: an ordinary green stays an ordinary green", GREEN, _pr(0), [_review()], [],
         never_says=("REVIEWER UNAVAILABLE", "DEGRADED"))
    other_app = {"id": 15368, "slug": "github-actions", "owner": {"login": "github"}}
    case("degradation NEG: the same run from another App", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run(app=other_app)], never_says=("REVIEWER UNAVAILABLE",))
    case("degradation NEG: right slug, wrong app id (an impostor App)", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run(app={"id": 1234, "slug": DEGRADATION_APP_SLUG})])
    case("degradation NEG: right app id, wrong slug", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run(app={"id": DEGRADATION_APP_ID, "slug": "someone-else"})])
    case("degradation NEG: that title at conclusion success", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run(conclusion="success")])
    case("degradation NEG: that title at conclusion failure", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run(conclusion="failure")])
    case("degradation NEG: the right run under another name", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run(name="internal-review-2")])
    case("degradation NEG: neutral with another title", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run(title="No blocking findings")])
    case("degradation NEG: still in progress is not a verdict", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run(status="in_progress")])
    case("degradation NEG: the right run, but a reviewer thread is unanswered", (UNANSWERED,), _pr(1), [],
         [_comment(1, INTERNAL_REVIEWER_USER)], check_runs=[_check_run()], mention=("DEGRADED", "1 of 1"))
    case("degradation: the reviewer's thread answered by a person → green", GREEN, _pr(2), [],
         [_comment(1, INTERNAL_REVIEWER_USER), _comment(11, PERSON, 1, "2026-09-14T13:00:00Z")],
         check_runs=[_check_run()])
    case("degradation NEG: a later real round supersedes it (newest App run decides)", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run(), _check_run(conclusion="success", title="No blocking findings",
                                              at="2026-09-14T13:10:00Z", crid=901)])
    case("degradation: a later degradation after an earlier round still counts", GREEN, _pr(0), [], [],
         check_runs=[_check_run(conclusion="success", title="No blocking findings", crid=899, at="2026-09-14T12:00:00Z"),
                     _check_run()])
    case("degradation: another App's newer run does not supersede the App's own", GREEN, _pr(0), [], [],
         check_runs=[_check_run(), _check_run(app=other_app, conclusion="success", at="2026-09-14T13:10:00Z", crid=902)])
    case("degradation --as-of: completed after the instant is ignored", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run(at="2026-09-14T14:00:00Z")], as_of="2026-09-14T13:00:00Z")
    case("degradation --as-of: completed before the instant counts", GREEN, _pr(0), [], [],
         check_runs=[_check_run(at="2026-09-14T12:40:00Z")], as_of="2026-09-14T13:00:00Z")
    case("degradation beats a waiver: the log credits the system, not a person", GREEN, _pr(0, [WAIVER_LABEL]), [], [],
         Waiver(True, (_labeled(PERSON),), {"rbuergi": "admin"}), check_runs=[_check_run()],
         mention=("DEGRADED",), never_says=("WAIVED",))

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
        # 🚨 THE REGRESSION CASE (#4730 review). A refusal ALONE — the row above it passes for the
        # wrong reason, because the unanswered thread is a second reason and any second reason
        # already defeats the wait. Before the structured check, this one slept 15 minutes.
        ("no wait: the review REFUSED and that is the only reason", False, _pr(0), [_review(REFUSAL_QUOTA)], [], NO_WAIVER),
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
    for name, expect, runs_ in [
        ("no wait: degraded (released, nothing left to arrive)", False, [_check_run()]),
        ("wait: an in-progress internal-review is not a degradation", True, [_check_run(status="in_progress")]),
    ]:
        got = waiting_would_help(evaluate(_pr(0), [], [], NO_WAIVER, None, runs_))
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
        # 🚨 …and the reply must be on a thread the REVIEWER opened. A busy conversation between two
        # people on somebody else's thread is not somebody answering a finding, and counting it
        # would hold the required check for the whole window while the finding sat untouched.
        # (Review finding on this change.)
        ("no wait: the recent reply is on a thread a PERSON opened, not the reviewer's", False,
         _pr(4), [_review()],
         [_comment(1), _comment(2, user=PERSON), _comment(3, user=PERSON, reply_to=2, at=RECENT),
          _comment(4, user=PERSON, reply_to=3, at=RECENT)], 60),
        # The positive twin, so the case above cannot pass by the predicate simply never waiting.
        ("wait: the same reply, but on the REVIEWER's thread", True,
         _pr(3), [_review()],
         [_comment(1), _comment(2), _comment(3, user=PERSON, reply_to=1, at=RECENT)], 60),
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

    # ── the ARM gate (control-plane reference): arms only on a review of the CURRENT head (Copilot's, or a completed internal-review run),
    # with every reviewer thread answered by a person, and never a draft. Each NO case names the
    # condition it must fail ON, so a fixture cannot pass by failing for another reason.
    HEAD = "a" * 40
    def _ir(conclusion="success", title="No blocking findings", status="completed", sha=HEAD, crid=950,
            at="2026-10-04T08:00:00Z"):
        r = _check_run(conclusion=conclusion, title=title, status=status, crid=crid, at=at)
        r["head_sha"] = sha
        return r
    def arm_case(name, ready, pr, comments, runs, says=""):
        nonlocal failures
        v = arm_readiness(pr, comments, runs)
        ok = v.ready == ready and (says in v.missing if not ready else not v.missing)
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} arm: {name:55} expected={'ARM' if ready else 'no arm: ' + says} "
              f"got={'ARM' if v.ready else 'no arm: ' + v.missing}")
    draft_pr = dict(_pr(0), draft=True)
    arm_case("review not run on the head -> no arm", False, _pr(0), [], [], "no review has landed on this pull request")
    arm_case("review only on an OLDER head -> no arm", False, _pr(0), [], [_ir(sha="b" * 40)], "no review has landed on this pull request")
    arm_case("review still in progress -> no arm", False, _pr(0), [], [_ir(status="in_progress", conclusion=None)], "still in_progress")
    arm_case("review neutral 'Reviewer unavailable' -> no arm", False, _pr(0), [], [_ir(conclusion="neutral", title=DEGRADED_TITLE)],
             "UNAVAILABLE")
    arm_case("another App's internal-review does not count", False, _pr(0), [],
             [dict(_ir(), app={"id": 15368, "slug": "github-actions"})], "no review has landed on this pull request")
    arm_case("unanswered bot thread -> no arm", False, _pr(1), [_comment(1, INTERNAL_REVIEWER_USER)], [_ir()],
             "1 of 1 thread(s)")
    arm_case("a bot reply does not answer -> no arm", False, _pr(2),
             [_comment(1, INTERNAL_REVIEWER_USER), _comment(11, OTHER_BOT, 1)], [_ir()], "1 of 1 thread(s)")
    arm_case("one of two threads answered -> no arm", False, _pr(3),
             [_comment(1, INTERNAL_REVIEWER_USER), _comment(2, INTERNAL_REVIEWER_USER), _comment(11, PERSON, 1)], [_ir()],
             "1 of 2 thread(s)")
    arm_case("incomplete comment listing -> no arm", False, _pr(5), [_comment(1, INTERNAL_REVIEWER_USER)], [_ir()],
             "listing returned")
    arm_case("all answered (reply to a reply, via in_reply_to_id) -> ARM", True, _pr(3),
             [_comment(1, INTERNAL_REVIEWER_USER), _comment(11, INTERNAL_REVIEWER_USER, 1), _comment(12, PERSON, 11)], [_ir()])
    arm_case("review completed, no findings -> ARM", True, _pr(0), [], [_ir()])
    arm_case("findings-conclusion still counts as reviewed once answered -> ARM", True, _pr(2),
             [_comment(1, INTERNAL_REVIEWER_USER), _comment(11, PERSON, 1)], [_ir(conclusion="failure", title="1 blocking finding")])
    arm_case("a real round after a degradation -> ARM", True, _pr(0), [],
             [_ir(conclusion="neutral", title=DEGRADED_TITLE, crid=940, at="2026-10-04T07:00:00Z"), _ir()])
    arm_case("a degradation after a real round -> no arm", False, _pr(0), [],
             [_ir(at="2026-10-04T07:00:00Z", crid=940), _ir(conclusion="neutral", title=DEGRADED_TITLE)], "UNAVAILABLE")
    arm_case("draft -> no arm (even when reviewed and answered)", False, draft_pr, [], [_ir()], "is a draft")

    # ── condition 4 (policy suites-parallel-with-review): reviewed AND answered is not enough — the required
    # suites must be green on the SAME head. Each NO names the context it holds on.
    def suite(name, conclusion="success", status="completed", sha=HEAD, at="2026-10-04T09:00:00Z", crid=990):
        return {"name": name, "status": status, "conclusion": conclusion, "head_sha": sha, "started_at": at, "id": crid}
    REQ = ["Consolidate test results", "Automatic review answered"]
    def suites_case(name, ready, required, head_runs, says=""):
        nonlocal failures
        v = arm_readiness(_pr(0), [], [_ir()], required, head_runs)
        ok = v.ready == ready and (says in v.missing if not ready else not v.missing)
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} arm+suites: {name:48} got={'ARM' if v.ready else 'no arm: ' + v.missing}")
    suites_case("reviewed, suites green -> ARM", True, REQ, [suite("Consolidate test results")])
    suites_case("reviewed, suite still running -> no arm", False, REQ,
                [suite("Consolidate test results", conclusion=None, status="in_progress")], "still in_progress")
    suites_case("reviewed, suite red -> no arm", False, REQ, [suite("Consolidate test results", "failure")], "concluded failure")
    suites_case("reviewed, suite never reported -> no arm", False, REQ, [], "has not reported")
    suites_case("suite green only on an OLDER head -> no arm", False, REQ,
                [suite("Consolidate test results", sha="b" * 40)], "has not reported")
    suites_case("newest re-run red after an older green -> no arm", False, REQ,
                [suite("Consolidate test results", crid=1), suite("Consolidate test results", "failure", at="2026-10-04T10:00:00Z", crid=2)],
                "concluded failure")
    suites_case("NO required context readable -> no arm (never vacuous)", False, [], [suite("x")], "no required status check")
    suites_case("only review contexts required -> no arm (never vacuous)", False, ["Automatic review answered", "internal-review"],
                [], "no required status check")
    suites_case("not asked (pure review gate) -> ARM on review alone", True, None, None)

    # ── policy copilot-code-review + review-once-per-pull-request: a landed Copilot review of the
    # pull request — against the current head OR any earlier one — is the review for BOTH the arm and
    # the stage gate, with no `internal-review` run at all. Each NO case names what it must fail on,
    # so the acceptance cannot pass vacuously.
    # The MERGE gate never looked at the head (condition 1 reads every review on the pull request);
    # these pin that, so a later "fix" scoping it to the head reds here instead of re-buying rounds.
    older = dict(_review(rid=60), commit_id="b" * 40)
    case("ONCE merge: review on an OLDER head, every thread answered -> green", (), _pr(2), [older],
         [_comment(1), _comment(11, PERSON, 1)])
    case("ONCE merge: NO review at all -> red", (NOT_LANDED,), _pr(0), [], [])
    case("ONCE merge: review on an OLDER head, a thread unanswered -> red", (UNANSWERED,), _pr(3), [older],
         [_comment(1), _comment(2), _comment(11, PERSON, 1)])
    def _cop(sha=HEAD, body=REVIEW_BODY_SEPT, state="COMMENTED", user=REVIEWER_REVIEW_USER, rid=77):
        return dict(_review(body=body, user=user, state=state, rid=rid), commit_id=sha)
    def cop_arm(name, ready, reviews, runs=(), comments=(), pr=None, required=None, head_runs=None, says="", noted=""):
        nonlocal failures
        v = arm_readiness(pr or _pr(len(comments)), list(comments), list(runs), required, head_runs, None, reviews)
        ok = (v.ready == ready and (says in v.missing if not ready else not v.missing)
              and (not noted or any(noted in n for n in v.notes)))
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} arm+copilot: {name:47} got={'ARM' if v.ready else 'no arm: ' + v.missing}")
    cop_arm("Copilot reviewed the head, no internal run -> ARM", True, [_cop()])
    cop_arm("ONCE: Copilot reviewed only an OLDER head, nothing to answer -> ARM", True, [_cop(sha="b" * 40)],
            noted="earlier head bbbbbbbbbb")
    cop_arm("ONCE: older-head review, its threads all answered -> ARM", True, [_cop(sha="b" * 40)],
            comments=[_comment(1), _comment(2), _comment(11, PERSON, 1), _comment(12, PERSON, 2)], noted="review-once-per-pull-request")
    cop_arm("ONCE: older-head review, a thread unanswered -> no arm", False, [_cop(sha="b" * 40)],
            comments=[_comment(1), _comment(2), _comment(11, PERSON, 1)], says="1 of 2 thread(s)")
    cop_arm("ONCE: an older-head REFUSAL is not a review -> no arm", False, [_cop(sha="b" * 40, body=REFUSAL_QUOTA)],
            says="no review has landed on this pull request")
    cop_arm("ONCE: the head's own review is named over an older one -> ARM", True,
            [_cop(sha="b" * 40, rid=70), _cop(rid=71)], noted="Copilot reviewed head aaaaaaaaaa")
    cop_arm("Copilot REFUSED on the head -> no arm", False, [_cop(body=REFUSAL_QUOTA)], says="no review has landed on this pull request")
    cop_arm("Copilot review still PENDING -> no arm", False, [_cop(state="PENDING")], says="no review has landed on this pull request")
    cop_arm("the internal bot's REVIEW alone is not its verdict -> no arm", False, [_cop(user=INTERNAL_REVIEWER_USER)],
            says="no review has landed on this pull request")
    cop_arm("Copilot on the head, its thread unanswered -> no arm", False, [_cop()], comments=[_comment(1)], says="1 of 1 thread(s)")
    cop_arm("Copilot on the head, its thread answered -> ARM", True, [_cop()], comments=[_comment(1), _comment(11, PERSON, 1)])
    cop_arm("Copilot on the head beats a DEGRADED internal run -> ARM", True, [_cop()],
            runs=[_ir(conclusion="neutral", title=DEGRADED_TITLE)])
    cop_arm("Copilot on the head, required suite red -> no arm", False, [_cop()], required=REQ,
            head_runs=[suite("Consolidate test results", "failure")], says="concluded failure")
    cop_arm("Copilot on the head, required suite green -> ARM", True, [_cop()], required=REQ,
            head_runs=[suite("Consolidate test results")])
    cop_arm("NEGATIVE CONTROL: no reviews passed -> no arm", False, None, says="no review has landed on this pull request")
    for name, ready, mode, reviews, comments, pr in [
        ("Copilot reviewed the head -> stage 2 starts", True, "reviewed", [_cop()], [], None),
        ("ONCE: Copilot reviewed only an OLDER head -> stage 2 starts", True, "reviewed", [_cop(sha="b" * 40)], [], None),
        ("ONCE: older-head review, thread answered -> stage 2 starts", True, "reviewed", [_cop(sha="b" * 40)],
         [_comment(1), _comment(11, PERSON, 1)], None),
        ("ONCE: older-head review, thread unanswered -> held", False, "unanswered", [_cop(sha="b" * 40)], [_comment(1)], None),
        ("ONCE: NEGATIVE CONTROL, no review at all -> waiting", False, "waiting", [], [], None),
        ("Copilot on the head, thread unanswered -> held", False, "unanswered", [_cop()], [_comment(1)], None),
        ("a DRAFT is held even with a Copilot review", False, "draft", [_cop()], [], dict(_pr(0), draft=True)),
    ]:
        v = stage_readiness(pr or _pr(len(comments)), comments, [], "2026-10-04T08:20:00Z", "2026-10-04T08:00:00Z", 60, reviews=reviews)
        ok = v.ready == ready and v.mode == mode
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} stage+copilot: {name:45} expected={mode} got={v.mode}: {v.missing}")

    # ── the STAGE gate (node-repo-stage-gate.yml): stage 2 starts only on a reviewed, answered head —
    # or on one of the three LOUD releases (degradation, fallback, label). Each case names the mode
    # it must land in AND, for a hold, the phrase it must hold on.
    T0, T_EARLY, T_LATE = "2026-10-04T08:00:00Z", "2026-10-04T08:20:00Z", "2026-10-04T09:05:00Z"
    def stage_case(name, mode, pr, comments, runs, now=T_EARLY, says=""):
        nonlocal failures
        v = stage_readiness(pr, comments, runs, now, T0, 60)
        expect_ready = mode in ("reviewed", "degraded", "fallback", "label")
        ok = (v.mode == mode and v.ready == expect_ready and (says in v.missing if not v.ready else True)
              and (bool(v.loud) == (mode in ("degraded", "fallback", "label"))))
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} stage: {name:53} expected={mode} got={v.mode}"
              f"{'' if v.ready else ' — ' + v.missing[:70]}")
    stage_case("no review yet, 20 of 60 min -> waiting", "waiting", _pr(0), [], [], says="has not started")
    stage_case("review running, 20 of 60 min -> waiting", "waiting", _pr(0), [], [_ir(status="in_progress", conclusion=None)],
               says="still in_progress")
    stage_case("review only on an OLDER head -> waiting (no stale verdict)", "waiting", _pr(0), [], [_ir(sha="b" * 40)],
               says="has not started")
    stage_case("another App's internal-review -> waiting", "waiting", _pr(0), [],
               [dict(_ir(), app={"id": 15368, "slug": "github-actions"})], says="has not started")
    stage_case("no review after 65 min -> FALLBACK (loud)", "fallback", _pr(0), [], [], now=T_LATE)
    stage_case("review still running after 65 min -> FALLBACK (loud)", "fallback", _pr(0), [],
               [_ir(status="in_progress", conclusion=None)], now=T_LATE)
    stage_case("reviewer unavailable -> DEGRADED release (loud)", "degraded", _pr(0), [],
               [_ir(conclusion="neutral", title=DEGRADED_TITLE)])
    stage_case("neutral but not the unavailable title -> reviewed, not degraded", "reviewed", _pr(0), [],
               [_ir(conclusion="neutral", title="Nothing to review")])
    stage_case("reviewed, no findings -> stage 2", "reviewed", _pr(0), [], [_ir()])
    stage_case("findings answered by a person -> stage 2", "reviewed", _pr(2),
               [_comment(1, INTERNAL_REVIEWER_USER), _comment(11, PERSON, 1)], [_ir(conclusion="failure", title="1 blocking finding")])
    stage_case("unanswered finding -> held", "unanswered", _pr(1), [_comment(1, INTERNAL_REVIEWER_USER)], [_ir()], says="1 of 1")
    stage_case("unanswered finding after 65 min -> STILL held (no fallback)", "unanswered", _pr(1),
               [_comment(1, INTERNAL_REVIEWER_USER)], [_ir()], now=T_LATE, says="1 of 1")
    stage_case("a bot reply does not answer -> held", "unanswered", _pr(2),
               [_comment(1, INTERNAL_REVIEWER_USER), _comment(11, OTHER_BOT, 1)], [_ir()], says="1 of 1")
    stage_case("incomplete comment listing -> held", "unreadable", _pr(5), [_comment(1, INTERNAL_REVIEWER_USER)], [_ir()],
               says="listing returned")
    stage_case("draft -> held even after the fallback", "draft", dict(_pr(0), draft=True), [], [], now=T_LATE, says="is a draft")
    stage_case("label tests-before-review -> released (loud)", "label",
               dict(_pr(0), draft=True, labels=[{"name": TESTS_FIRST_LABEL}]), [], [])
    # ── generated-only bot PRs (Plugins #2860): skip stage 1 on PROVENANCE, never on a branch name.
    BOT = {"login": "meshweaver-cloud[bot]", "type": "Bot", "id": 300054957}
    HUMAN = {"login": "rbuergi", "type": "User", "id": 6334612}
    def gpr(user=BOT, n_files=2, created="2026-10-04T08:00:00Z"):
        return dict(_pr(0), user=user, changed_files=n_files, created_at=created)
    LOCKS = [{"filename": "AI/manifest.lock", "patch": "-a\n+b"}, {"filename": "Hosting/manifest.lock", "patch": "-a\n+b"}]
    FLOOR = [{"filename": "AI/mesh-floor.lock", "patch": "+x"},
             {"filename": "AI/index.json", "patch": '@@ -3 +3 @@\n-    "minMeshVersion": "3.0.0-ci.9900",\n+    "minMeshVersion": "3.0.0-ci.9939",'}]
    BOT_COMMITS = [{"author": BOT}]
    def gen_case(name, mode, pr, files, commits, now=T_EARLY):
        nonlocal failures
        v = stage_readiness(pr, [], [], now, T0, 60, files, commits)
        ok = v.mode == mode
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} stage: {name:53} expected={mode} got={v.mode}")
    gen_case("settle PR (App, locks only) -> stage 1 not owed", "generated", gpr(), LOCKS, BOT_COMMITS)
    gen_case("floor stamp (App, mesh-floor.lock + minMeshVersion) -> not owed", "generated", gpr(), FLOOR, BOT_COMMITS)
    gen_case("a PERSON's PR touching only locks -> waits for the review", "waiting", gpr(user=HUMAN), LOCKS, [{"author": HUMAN}])
    gen_case("App PR with one non-generated file -> waits", "waiting", gpr(),
             LOCKS[:1] + [{"filename": "Hosting/Deployment/Source/X.cs", "patch": "+x"}], BOT_COMMITS)
    gen_case("App PR whose index.json changes more than the floor -> waits", "waiting", gpr(),
             [{"filename": "AI/index.json", "patch": '-    "minMeshVersion": "a",\n+    "minMeshVersion": "b",\n+    "requires": []'}], BOT_COMMITS)
    gen_case("App PR with a person's commit on it -> waits", "waiting", gpr(), LOCKS, BOT_COMMITS + [{"author": HUMAN}])
    gen_case("App PR whose file listing is short -> waits", "waiting", gpr(n_files=3), LOCKS, BOT_COMMITS)
    gen_case("a look-alike bot (another id) -> waits", "waiting", gpr(user=dict(BOT, id=1)), LOCKS, BOT_COMMITS)
    gen_case("a generated-only App DRAFT -> held as a draft", "draft", dict(gpr(), draft=True), LOCKS, BOT_COMMITS)
    gen_case("App PR whose commit listing is short -> waits", "waiting", dict(gpr(), commits=2), LOCKS, BOT_COMMITS)
    gen_case("a floor line carrying more than the floor -> waits", "waiting", gpr(n_files=1),
             [{"filename": "AI/index.json", "patch": '-    "minMeshVersion": "a",\n+    "minMeshVersion": "b", "requires": ["x"],'}],
             BOT_COMMITS)
    gen_case("a minified index.json change -> waits", "waiting", gpr(n_files=1),
             [{"filename": "AI/index.json", "patch": '-{"minMeshVersion": "a", "id": "AI"}\n+{"minMeshVersion": "b", "id": "AI"}'}],
             BOT_COMMITS)
    # The fallback clock keys on the PULL REQUEST for the App's PRs: a head rewritten 1 min ago on a
    # PR opened 4 h ago is past the fallback (the per-head clock would restart forever).
    v = stage_readiness(gpr(created="2026-10-04T04:00:00Z"), [], [], T_EARLY, "2026-10-04T08:19:00Z", 60,
                        LOCKS[:1] + [{"filename": "Hosting/X.cs"}], BOT_COMMITS)
    ok = v.mode == "fallback"
    failures += 0 if ok else 1
    print(f"self-test {'ok' if ok else 'FAIL':4} stage: {'an App PR falls back on the PR clock, not the head clock':53} got={v.mode}")
    # ── the REQUIRED verdict (`evaluate`) agrees with the stage gate on generated-only App PRs (Plugins #3044):
    # the reviewer refuses a lock-only pull request, and without this the settle/stamp PRs sat red until a
    # person waived them — main published nothing in between. NEGATIVE CONTROLS: the same refusal on a
    # person's lock-only PR, and on an App PR carrying one hand-written file, stays RED (refused).
    LOCK_REFUSAL = ("Copilot wasn't able to review any files in this pull request. Check if the **Files changed** "
                    "in this pull request are included in [default exclusions](https://docs.github.com).")
    def verdict_case(name, want_green, pr, files, commits, comments=(), want_refused=None):
        nonlocal failures
        v = evaluate(pr, [_review(LOCK_REFUSAL)], list(comments), NO_WAIVER, None, (), files, commits)
        ok = v.green == want_green and (want_refused is None or bool(v.refused) == want_refused)
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} verdict: {name:51} green={v.green} refused={bool(v.refused)}")
    verdict_case("settle PR refused by the reviewer -> GREEN (not owed)", True, gpr(), LOCKS, BOT_COMMITS, want_refused=False)
    verdict_case("floor stamp refused by the reviewer -> GREEN (not owed)", True, gpr(), FLOOR, BOT_COMMITS, want_refused=False)
    verdict_case("NEGATIVE CONTROL: a person's lock-only PR -> RED", False, gpr(user=HUMAN), LOCKS, [{"author": HUMAN}], want_refused=True)
    verdict_case("NEGATIVE CONTROL: App PR with a hand-written file -> RED", False, gpr(),
                 LOCKS[:1] + [{"filename": "Hosting/Deployment/Source/X.cs", "patch": "+x"}], BOT_COMMITS, want_refused=True)
    verdict_case("NEGATIVE CONTROL: App PR, files unread -> RED", False, gpr(), None, None, want_refused=True)
    verdict_case("a generated PR's reviewer thread still needs a reply -> RED", False, dict(gpr(), review_comments=1),
                 LOCKS, BOT_COMMITS, comments=[_comment(1)])
    v = stage_readiness(_pr(0), [], [], "garbage", T0, 60)
    ok = (not v.ready) and v.mode == "unreadable"
    failures += 0 if ok else 1
    print(f"self-test {'ok' if ok else 'FAIL':4} stage: {'an unreadable clock is never a fallback':53} got={v.mode}")

    # ── the CARRY-OVER (policy review-carries-over-clean-base-merge): a review carries to a head that
    # differs from the reviewed one only by a clean merge of the base branch — computed from the
    # commits' parents and the pull request's own diff, never from a message. Topology of the fixture:
    #   B0 ── B1 ── B2              (base)
    #    └ P1 ── O ──── M           (O reviewed; M = merge of B2 into O; M's parents [O, B2])
    def sha(c):
        return (c * 40)[:40]
    B0, B2, P1, O, M = sha("0"), sha("2"), sha("3"), sha("4"), sha("5")
    def commit(s, *parents):
        return {"sha": s, "parents": [{"sha": p} for p in parents]}
    PATCH_OLD = "@@ -10,3 +10,4 @@ class X\n context\n-old line\n+new line\n+added line"
    PATCH_SHIFTED = "@@ -42,3 +42,4 @@ class X moved\n context\n-old line\n+new line\n+added line"   # main added 32 lines above
    PATCH_RESOLVED = "@@ -42,3 +42,4 @@ class X\n context\n-old line\n+new line, as the conflict resolution rewrote it\n+added line"
    def files(patch=PATCH_OLD, extra=()):
        return [{"filename": "src/X.cs", "status": "modified", "sha": "f" * 40, "patch": patch}, *extra]
    def cmp_(commits, fs, mb, total=None):
        return {"commits": commits, "total_commits": len(commits) if total is None else total, "files": fs,
                "merge_base_commit": {"sha": mb}, "status": "diverged"}
    OLD_CMP = cmp_([commit(P1, B0), commit(O, P1)], files(), B0)
    NEW_CMP = cmp_([commit(P1, B0), commit(O, P1), commit(M, O, B2)], files(PATCH_SHIFTED), B2)
    REVIEW_O = _ir(sha=O, crid=960, title="No blocking findings")
    RUNS = {O: [REVIEW_O]}
    def carry_of(new_cmp, old_cmp=OLD_CMP, runs=RUNS, head=M):
        own = {c["sha"]: c for c in new_cmp["commits"]}
        old, run, _walked, why = find_reviewed_ancestor(head, own, lambda s: runs.get(s, []))
        return Carry(False, f"no carry-over: {why}") if old is None else carry_over(head, new_cmp, old_cmp, old, run)
    def carry_case(name, expect_ok, c, says):
        nonlocal failures
        ok = c.ok == expect_ok and says in c.why
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} carry: {name:53} expected={'CARRY' if expect_ok else 'fresh'} "
              f"got={'CARRY' if c.ok else 'fresh'}{'' if ok else ' — ' + c.why}")
    PURE = carry_of(NEW_CMP)
    carry_case("pure clean main-merge -> carries (hunk lines moved)", True, PURE, f"review CARRIED from head {O[:10]}")
    carry_case("  ...and the evidence names the merge + the patch-id", True, PURE, f"({M[:10]}), and the pull request's own diff is byte-identical — patch-id")
    carry_case("merge whose conflict resolution changed the PR diff -> fresh", False,
               carry_of(dict(NEW_CMP, files=files(PATCH_RESOLVED))), "own diff changed — patch-id")
    carry_case("merge that ADDED a file to the PR diff -> fresh", False,
               carry_of(dict(NEW_CMP, files=files(PATCH_SHIFTED, [{"filename": "src/Y.cs", "status": "added", "sha": "e" * 40, "patch": "+y"}]))),
               "own diff changed")
    X = sha("6")
    carry_case("an extra (non-merge) commit on top -> fresh", False,
               carry_of(cmp_(NEW_CMP["commits"] + [commit(X, M)], files(PATCH_SHIFTED), B2), head=X), "is not a merge commit")
    M3 = sha("7")
    carry_case("an extra commit, THEN a main-merge -> fresh", False,
               carry_of(cmp_(OLD_CMP["commits"] + [commit(X, O), commit(M3, X, B2)], files(PATCH_SHIFTED), B2), head=M3),
               f"{X[:10]} is not a merge commit")
    F1 = sha("8")
    carry_case("a merge of ANOTHER branch (both parents on the PR side) -> fresh", False,
               carry_of(cmp_(OLD_CMP["commits"] + [commit(F1, B0), commit(M, O, F1)], files(PATCH_SHIFTED), B0)),
               "2 parent(s) on the pull request's side")
    carry_case("an evil merge bringing a non-merge commit (fresh set) -> fresh", False,
               carry_over(M, cmp_(NEW_CMP["commits"] + [commit(F1, B2)], files(PATCH_SHIFTED), B2), OLD_CMP, O, REVIEW_O),
               "non-merge commit(s) since it")
    carry_case("the reviewed head was DEGRADED -> nothing to carry", False,
               carry_of(NEW_CMP, runs={O: [_ir(sha=O, conclusion="neutral", title=DEGRADED_TITLE)]}), "is not a merge commit")
    carry_case("the reviewed head's run is 'Review not completed' -> nothing to carry", False,
               carry_of(NEW_CMP, runs={O: [_ir(sha=O, conclusion="failure", title="Review not completed — a round ended for a cause …")]}),
               "is not a merge commit")
    carry_case("a REWORDED no-review exit is not carried (allow-list fails safe)", False,
               carry_of(NEW_CMP, runs={O: [_ir(sha=O, conclusion="failure", title="Review could not finish — …")]}), "is not a merge commit")
    carry_case("a review title with an UNREAD conclusion is not carried", False,
               carry_of(NEW_CMP, runs={O: [_ir(sha=O, conclusion=None)]}), "is not a merge commit")
    carry_case("a carried review carries on", True,
               carry_of(NEW_CMP, runs={O: [_ir(sha=O, title=f"Review carried from {P1[:10]} — No blocking findings")]}), "CARRIED")
    carry_case("a merge that only changed TRAILING WHITESPACE on a changed line -> fresh", False,
               carry_of(dict(NEW_CMP, files=files(PATCH_SHIFTED.replace("+new line", "+new line  ")))), "own diff changed")
    carry_case("another App's internal-review on the old head -> nothing to carry", False,
               carry_of(NEW_CMP, runs={O: [dict(REVIEW_O, app={"id": 15368, "slug": "github-actions"})]}), "is not a merge commit")
    M2 = sha("9")
    carry_case("two clean main-merges in a row (middle unreviewed) -> carries", True,
               carry_of(cmp_(NEW_CMP["commits"] + [commit(M2, M, sha("a"))], files(PATCH_SHIFTED), sha("a")), head=M2),
               "the 2 commit(s) since it are all merges")
    carry_case("short commit listing -> fresh (never a partial answer)", False,
               carry_of(dict(NEW_CMP, total_commits=4)), "returned 3 of 4 commits")
    carry_case("300 files (the compare cap) -> fresh", False,
               carry_of(dict(NEW_CMP, files=files(PATCH_SHIFTED, [{"filename": f"f{i}", "status": "added", "sha": "e" * 40, "patch": "+"} for i in range(299)]))),
               "cannot be fingerprinted")
    carry_case("a binary file with the same blob -> carries", True,
               carry_of(dict(NEW_CMP, files=files(PATCH_SHIFTED, [{"filename": "a.png", "status": "added", "sha": "d" * 40}])),
                        dict(OLD_CMP, files=files(PATCH_OLD, [{"filename": "a.png", "status": "added", "sha": "d" * 40}]))), "CARRIED")
    carry_case("a binary file whose blob changed -> fresh", False,
               carry_of(dict(NEW_CMP, files=files(PATCH_SHIFTED, [{"filename": "a.png", "status": "added", "sha": "c" * 40}])),
                        dict(OLD_CMP, files=files(PATCH_OLD, [{"filename": "a.png", "status": "added", "sha": "d" * 40}]))), "own diff changed")
    # ...and what the GATES do with it. M's own internal-review: none (the whole point).
    MPR = dict(_pr(0), head={"sha": M})
    def gate_case(name, mode, pr, comments, runs, carry, says=""):
        nonlocal failures
        v = stage_readiness(pr, comments, runs, T_EARLY, T0, 60, carry=carry)
        ok = v.mode == mode and (says in "\n".join(v.notes + (v.missing,)))
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} stage+carry: {name:47} expected={mode} got={v.mode}"
              f"{'' if ok else ' — ' + v.missing + ' ' + str(v.notes)}")
    gate_case("pure merge -> stage 2 starts NOW, no new round", "carried", MPR, [], [], PURE, "patch-id")
    gate_case("NEGATIVE CONTROL: carry disabled -> the pure merge WAITS", "waiting", MPR, [], [], None, "has not started")
    gate_case("conflict-resolved merge -> waits for a fresh review", "waiting", MPR, [], [],
              carry_of(dict(NEW_CMP, files=files(PATCH_RESOLVED))), "own diff changed")
    gate_case("carried, but a thread is unanswered -> still held", "unanswered", dict(MPR, review_comments=1),
              [_comment(1, INTERNAL_REVIEWER_USER)], [], PURE, "1 of 1")
    gate_case("carried, threads answered on the PR -> stage 2", "carried", dict(MPR, review_comments=2),
              [_comment(1, INTERNAL_REVIEWER_USER), _comment(11, PERSON, 1)], [], PURE)
    gate_case("the head's OWN review wins over a carry", "reviewed", MPR, [], [_ir(sha=M)], PURE)
    gate_case("a degraded own run is replaced by the carried real review", "carried", MPR, [],
              [_ir(sha=M, conclusion="neutral", title=DEGRADED_TITLE)], PURE)
    for name, carry, runs, ready, says in [
        ("arm: pure merge carried -> ARM", PURE, [], True, ""),
        ("arm: NEGATIVE CONTROL, no carry -> no arm", None, [], False, "no review has landed on this pull request"),
        ("arm: carried, a fresh round still running on M -> ARM (it reviews the same diff)", PURE,
         [_ir(sha=M, status="in_progress", conclusion=None)], True, ""),
        ("arm: carry refused -> no arm, and says why", carry_of(dict(NEW_CMP, files=files(PATCH_RESOLVED))), [], False, "own diff changed"),
    ]:
        v = arm_readiness(MPR, [], runs, carry=carry)
        ok = v.ready == ready and (says in v.missing) and (not ready or any("CARRIED" in n for n in v.notes))
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} got={'ARM' if v.ready else 'no arm: ' + v.missing[:80]}")
    # The ADAPTER, through a stub REST: read_carry walks, pages and compares — and `carry=False` (the
    # negative control the CLI's --no-carry selects) reads nothing and carries nothing.
    class _RestStub:
        def __init__(self, routes):
            self.routes, self.calls = routes, []
        def api(self, path, paginate=False):
            self.calls.append(path)
            for prefix, value in self.routes.items():
                if path.startswith(prefix):
                    return value
            raise ReadError(f"GET {path} (no stub)")
    def listing(runs):
        return {"total_count": len(runs), "check_runs": runs}
    routes = {
        "pulls/1/comments": [], "pulls/1": dict(MPR, base={"ref": "main"}),
        f"commits/{M}/check-runs": listing([]), f"commits/{O}/check-runs": listing([REVIEW_O]),
        "branches/main": {"commit": {"sha": sha("b")}},
        f"compare/{sha('b')}...{M}": NEW_CMP, f"compare/{sha('b')}...{O}": OLD_CMP,
    }
    stub = _RestStub(routes)
    _, _, _, got = read_arm_inputs(stub, 1)
    ok = got is not None and got.ok and got.from_head == O
    failures += 0 if ok else 1
    print(f"self-test {'ok' if ok else 'FAIL':4} adapter: {'read_carry carries the pure merge (walk + 2 compares)':51} got={got}")
    stub = _RestStub(routes)
    _, _, _, got = read_arm_inputs(stub, 1, carry=False)
    ok = got is None and not any(c.startswith("compare/") for c in stub.calls)
    failures += 0 if ok else 1
    print(f"self-test {'ok' if ok else 'FAIL':4} adapter: {'carry=False (--no-carry) reads and carries nothing':51} got={got}")
    stub = _RestStub({k: v for k, v in routes.items() if k != f"compare/{sha('b')}...{O}"})
    _, _, _, got = read_arm_inputs(stub, 1)
    ok = got is not None and not got.ok and "could not be read" in got.why
    failures += 0 if ok else 1
    print(f"self-test {'ok' if ok else 'FAIL':4} adapter: {'an unreadable compare does NOT carry':51} got={got.why[:60] if got else got}")

    # ── the EVENT half: only a FAILED gate on the CURRENT head with a GREEN stage 1 is re-run.
    def adv_case(name, expect, pr, run, job, verdict, says=""):
        nonlocal failures
        action, why = advance_action(pr, run, job, verdict)
        ok = action == expect and says in why
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} advance: {name:51} expected={expect} got={action} ({why[:60]})")
    GREEN_S = StageVerdict(True, "reviewed", "")
    HELD_S = StageVerdict(False, "unanswered", "1 of 1 reviewer thread(s) have no reply")
    open_pr = dict(_pr(0), state="open")
    done_run = {"id": 7, "head_sha": HEAD, "status": "completed", "created_at": T0}
    failed_gate = {"status": "completed", "conclusion": "failure", "name": f"stage-gate / {STAGE_GATE_JOB}"}
    for legacy, expect in ((f"stage-gate / {STAGE_GATE_JOBS[1]}", True), ("stage-gate / Stage 1: something else", False)):
        ok = legacy.endswith(STAGE_GATE_JOBS) == expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} gate-name: {legacy!r} recognised={expect}")
    adv_case("held run + green stage 1 -> rerun", "rerun", open_pr, done_run, failed_gate, GREEN_S, "re-running")
    adv_case("held run + stage 1 still held -> none", "none", open_pr, done_run, failed_gate, HELD_S, "still holds")
    adv_case("run of an OLDER head -> none (no stale verdict)", "none", open_pr, dict(done_run, head_sha="b" * 40),
             failed_gate, GREEN_S, "new head restarts")
    adv_case("gate already passed -> none", "none", open_pr, done_run, dict(failed_gate, conclusion="success"), GREEN_S, "already passed")
    adv_case("gate still evaluating -> none", "none", open_pr, done_run, dict(failed_gate, status="in_progress"), GREEN_S, "still in_progress")
    adv_case("gate cancelled (superseded) -> none", "none", open_pr, done_run, dict(failed_gate, conclusion="cancelled"), GREEN_S,
             "only a failed gate")
    adv_case("run still finishing -> wait", "wait", open_pr, dict(done_run, status="in_progress"), failed_gate, GREEN_S, "needs it completed")
    adv_case("no stage gate in the caller -> none", "none", open_pr, done_run, None, GREEN_S, "not adopted")
    adv_case("no CI run yet -> none", "none", open_pr, None, None, GREEN_S, "no pull_request CI run")
    adv_case("cancelled (superseded) run -> none, never revived", "none", open_pr, dict(done_run, conclusion="cancelled"),
             failed_gate, GREEN_S, "never revived")
    for name, readback, expect in [
        ("race: the run is in_progress again -> another invocation re-ran it", {"status": "in_progress"}, True),
        ("race: the run is queued again -> another invocation re-ran it", {"status": "queued"}, True),
        ("race: still completed -> the POST really failed, raise", {"status": "completed"}, False),
        ("race: no status -> never masks the failure", {}, False),
        ("race: not a run object -> never masks the failure", None, False),
    ]:
        ok = lost_rerun_race(readback) == expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={expect}")
    class _StubGh:
        def __init__(self, post_fails, readback):
            self.post_fails, self.readback = post_fails, readback
        def post(self, path):
            if self.post_fails:
                raise ReadError(f"POST {path} failed (409)")
        def api(self, path):
            if isinstance(self.readback, Exception):
                raise self.readback
            return self.readback
    for name, stub, expect in [
        ("post_rerun: the POST succeeds -> True", _StubGh(False, None), True),
        ("post_rerun: refused, run in_progress again -> lost race (False)", _StubGh(True, {"status": "in_progress"}), False),
        ("post_rerun: refused, run still completed -> RAISES", _StubGh(True, {"status": "completed"}), "raise"),
        ("post_rerun: refused, read-back fails -> RAISES", _StubGh(True, ReadError("GET failed")), "raise"),
    ]:
        try:
            got = post_rerun(stub, 7)
        except ReadError:
            got = "raise"
        ok = got == expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={expect} got={got}")
    adv_case("closed PR -> none", "none", dict(open_pr, state="closed"), done_run, failed_gate, GREEN_S, "closed")

    # ── the SELF-REFRESH: a green verdict branch protection does not read re-runs the one it does.
    ref_pr = {"number": 4649, "state": "open", "head": {"sha": HEAD, "repo": {"full_name": "Systemorph/MeshWeaver"}}}
    red_read = {"id": 7, "head_sha": HEAD, "status": "completed", "conclusion": "failure",
                "run_started_at": "2026-10-07T09:40:00Z", "html_url": "u"}
    def ref_case(name, expect, event, candidate, pr=None, evaluated_at="2026-10-07T10:00:00Z", own=99, says=""):
        nonlocal failures
        action, why = refresh_action(event, own, evaluated_at, "Systemorph/MeshWeaver",
                                     pr if pr is not None else ref_pr, candidate)
        ok = action == expect and says in why
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} refresh: {name:51} expected={expect} got={action} ({why[:60]})")
    ref_case("#4649: comment-run GREEN, pull_request run RED -> rerun", "rerun", "pull_request_review_comment", red_read,
             says="re-running")
    ref_case("review-run GREEN, pull_request run RED -> rerun", "rerun", "pull_request_review", red_read, says="re-running")
    ref_case("a cancelled (evicted) read run -> rerun", "rerun", "pull_request_review_comment",
             dict(red_read, conclusion="cancelled"), says="cancelled")
    ref_case("this run IS the pull_request run -> none (no loop)", "none", "pull_request", red_read, says="IS a `pull_request`")
    ref_case("merge_group entry -> none", "none", "merge_group", red_read, says="merge-queue")
    ref_case("read run already re-run, in flight -> none (idempotent)", "none", "pull_request_review_comment",
             dict(red_read, status="in_progress", conclusion=None), says="still in_progress")
    ref_case("read run already green -> none", "none", "pull_request_review_comment",
             dict(red_read, conclusion="success"), says="already reads success")
    ref_case("read run red but started AFTER this verdict -> none", "none", "pull_request_review_comment",
             dict(red_read, run_started_at="2026-10-07T10:00:05Z"), says="not before this verdict")
    ref_case("read run started at the verdict instant -> none", "none", "pull_request_review_comment",
             dict(red_read, run_started_at="2026-10-07T10:00:00Z"), says="not before this verdict")
    ref_case("read run is for an older head -> none", "none", "pull_request_review_comment",
             dict(red_read, head_sha="b" * 40), says="not the current head")
    ref_case("candidate is this very run -> none", "none", "pull_request_review_comment", red_read, own=7, says="is this run")
    ref_case("no pull_request run at all -> fail, named", "fail", "pull_request_review_comment", None, says="push to the pull request")
    ref_case("closed pull request -> none", "none", "pull_request_review_comment", red_read, pr=dict(ref_pr, state="closed"),
             says="closed")
    ref_case("fork pull request -> manual, never a silent skip", "manual", "pull_request_review_comment", red_read,
             pr=dict(ref_pr, head={"sha": HEAD, "repo": {"full_name": "someone/MeshWeaver"}}), says="fork")
    ref_case("unreadable verdict time -> fail", "fail", "pull_request_review_comment", red_read, evaluated_at="", says="unreadable")

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
    ap.add_argument("--arm-gate", action="store_true",
                    help="answer the arm decision's question instead — may auto-merge be armed NOW (see arm_readiness); "
                         "writes ready=true|false to $GITHUB_OUTPUT and one line to the job summary, exit 0 either way")
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
    ap.add_argument("--stage-gate", action="store_true",
                    help="the staged pipeline's stage-1 gate: may stage 2 start for this head (stage_readiness)? "
                         "exit 0 = yes, 1 = held (RED, named); takes --repo, --pr, --since, --fallback-minutes")
    ap.add_argument("--stage-advance", action="store_true",
                    help="the event half: re-run a held run's failed jobs once stage 1 is green; takes --repo, "
                         "--workflow and exactly one of --pr / --head-sha / --sweep")
    ap.add_argument("--since", help="--stage-gate: when the head's CI run was created (ISO-8601 UTC)")
    ap.add_argument("--no-carry", action="store_true",
                    help="--stage-gate / --arm-gate: do NOT carry a review over a clean merge of the base branch — the "
                         "negative control of policy review-carries-over-clean-base-merge (see carry_over)")
    ap.add_argument("--fallback-minutes", type=int, default=STAGE_FALLBACK_MINUTES,
                    help="minutes without a completed review after which stage 2 starts anyway, loudly")
    ap.add_argument("--workflow", help="--stage-advance: the caller's pull-request CI workflow FILE (ci.yml, dotnet-test.yml)")
    ap.add_argument("--head-sha", help="--stage-advance: the head a check_run event named")
    ap.add_argument("--sweep", action="store_true", help="--stage-advance: every open PR with a failed run (the fallback timer)")
    ap.add_argument("--refresh-read-run", action="store_true",
                    help="after a GREEN verdict: re-run the newest `pull_request` run of this workflow for the head when it "
                         "still carries an older red (refresh_action); takes --repo, --pr, --event, --run-id, --evaluated-at")
    ap.add_argument("--event", help="--refresh-read-run: the event that produced the GREEN verdict (github.event_name)")
    ap.add_argument("--run-id", help="--refresh-read-run: this workflow run's id (github.run_id)")
    ap.add_argument("--evaluated-at", help="--refresh-read-run: when the GREEN verdict was taken (ISO-8601 UTC)")
    ap.add_argument("--wait-minutes", type=int, default=20,
                    help="--stage-advance: how long to let an in-flight held run finish before re-running it")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    if not args.repo:
        print("::error::--repo (or GH_REPO) is required")
        return 2
    if not 5 <= args.fallback_minutes <= 240:
        print(f"::error::--fallback-minutes must be between 5 and 240, got {args.fallback_minutes}")
        return 2
    if args.refresh_read_run:
        if not args.event:
            print("::error::--refresh-read-run needs --event (github.event_name)")
            return 2
        if args.event == "merge_group" or not args.pr:
            # A merge-queue entry has nothing on a pull request head to refresh (refresh_action says
            # the same); printed rather than failed on a usage error.
            print(f"  event {args.event!r} carries no pull request head to refresh — nothing to do")
            return 0
        if not re.fullmatch(r"[1-9]\d*", args.pr) or not re.fullmatch(r"[1-9]\d*", args.run_id or ""):
            print(f"::error::--refresh-read-run needs --pr <number> and --run-id <number>, got {args.pr!r} / {args.run_id!r}")
            return 2
        return run_refresh(args.repo, int(args.pr), args.event, args.run_id, args.evaluated_at or "")
    if args.stage_advance:
        if not args.workflow or not re.fullmatch(r"[A-Za-z0-9_.-]+\.ya?ml", args.workflow):
            print("::error::--stage-advance needs --workflow <file>.yml (the caller's pull-request CI workflow)")
            return 2
        chosen = [x for x in (args.pr, args.head_sha, args.sweep or None) if x]
        if len(chosen) != 1:
            print("::error::--stage-advance takes exactly one of --pr, --head-sha, --sweep")
            return 2
        if args.pr and not re.fullmatch(r"[1-9]\d*", args.pr):
            print(f"::error::--pr must be a pull request number, got {args.pr!r}")
            return 2
        if args.head_sha and not re.fullmatch(r"[0-9a-f]{40}", args.head_sha):
            print(f"::error::--head-sha must be a full 40-hex sha (an abbreviated one lists nothing, silently), got {args.head_sha!r}")
            return 2
        return run_stage_advance(args.repo, args.workflow, pr=int(args.pr) if args.pr else None, head_sha=args.head_sha,
                                 sweep=args.sweep, fallback_minutes=args.fallback_minutes,
                                 wait_minutes=max(0, min(args.wait_minutes, 30)))
    if args.stage_gate:
        if not args.pr or args.merge_group_ref or not re.fullmatch(r"[1-9]\d*", args.pr):
            print("::error::--stage-gate takes --repo, --pr <number>, --since and --fallback-minutes")
            return 2
        if parse_stamp(args.since) is None:
            print(f"::error::--since must be an ISO-8601 UTC instant like 2026-10-04T08:00:00Z, got {args.since!r}")
            return 2
        return run_stage_gate(args.repo, int(args.pr), args.since, args.fallback_minutes, not args.no_carry)
    if bool(args.pr) == bool(args.merge_group_ref):
        print("::error::exactly one of --pr or --merge-group-ref is required — refusing to guess which pull request to judge")
        return 2
    if args.arm_gate:
        if not args.pr or args.merge_group_ref or args.as_of or args.wait_for_review or args.settle_replies:
            print("::error::--arm-gate takes --repo and --pr only")
            return 2
        if not re.fullmatch(r"[1-9]\d*", args.pr):
            print(f"::error::--pr must be a pull request number, got {args.pr!r}")
            return 2
        return run_arm_gate(args.repo, int(args.pr), not args.no_carry)
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
