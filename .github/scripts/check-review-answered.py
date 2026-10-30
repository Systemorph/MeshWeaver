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
   🚨 With NO reviewer thread on the pull request, the review must be a COPILOT review of the
   pull request — of ANY of its heads (policy `review-evidence-any-head`, #4730): "reviewed,
   nothing found" and "never reviewed" both read "0 unanswered threads", and the only evidence that
   separates them is a completed Copilot review object with zero findings. A re-review on every
   push is NOT required — a clean review of an earlier head, followed by a push (a merge of main,
   a fix), stays reviewed. The internal reviewer's clean review, a degradation run or elapsed time
   is not that evidence; the waiver label is the escape. With findings, the threads prove the
   review (review-once).
   🚨 NARROWED by policy `review-on-real-change`: an earlier head's Copilot review counts only when it
   COVERS the head — every commit since it a merge of the default branch that left the pull request's
   own diff unchanged (`copilot_currency` over `merge_only_same_diff`, read by `read_copilot_carry`).
   A head whose own diff changed is RED ("THIS HEAD OWES A REVIEW", `Verdict.stale`) until its own
   review lands — the review-on-change lane (`--request-review-on-change`, node-repo-request-review.yml)
   requests it — and is waited for like a review that has not landed. The waiver still releases it.
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

THE REVIEWER-UNAVAILABLE DEGRADATION — RETIRED for this gate (policy review-evidence-any-head)
------------------------------------------------------------------------------------------
🚨 Since #4730 the degradation run no longer releases condition 1 of the MERGE gate: it says the
reviewer did NOT review, and "never reviewed" is exactly what the gate exists to refuse. The stage
gate still starts the suites on it (runner spend only) and the arm gate still refuses it. The
paragraphs below are the record of what it was.

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
It posted exactly one review per pull request on all 50 (the ruleset carried
`review_on_push: false` at the time of that measurement), always at state COMMENTED. The rule runs
with `review_on_push: true` again now (policy `copilot-code-review`, as amended), so Copilot reviews
every pushed head of a non-draft pull request.

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

ANY-HEAD REVIEW EVIDENCE — policy `review-once-per-pull-request` (gate half; its ruleset half is reversed)
-------------------------------------------------------------------
The rulesets run `copilot_code_review` with `review_on_push: true` again (policy
`copilot-code-review`, as amended): every push to a non-draft pull request gets a fresh review. The
gates did not change with that: every gate in this file counts a landed Copilot review against ANY
head of the pull request as "reviewed", so a push never waits for its own new review to land
(`copilot_review_of_pull_request`; the merge gate's condition 1 reads every review on the pull
request — policy `review-evidence-any-head`). Condition
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
  check-review-answered.py --repo O/R --pr N --verdict-check-run "Automatic review answered"
        (policy review-pending-while-answering: judge once and WRITE the required context as one check-run
         per head — PENDING while threads await answers inside the deadlines of policy review-answer-deadline,
         red only on a real failure; see "PENDING WHILE ANSWERING" below)
  check-review-answered.py --repo O/R --verdict-sweep --verdict-check-run NAME   (the schedule: re-judge pending verdicts)
  check-review-answered.py --repo O/R --pr N --arm-gate     (the control plane's arm decision: may auto-merge be armed now?)
  check-review-answered.py --repo O/R --merge-group-ref refs/heads/gh-readonly-queue/main/pr-N-<sha>
  check-review-answered.py --repo O/R --pr N --stage-gate --since <run created_at>
        (node-repo-stage-gate.yml: may the expensive suites start for this head?)
  … --stage-gate / --arm-gate … --no-carry
        (the negative control: never carry a review over a clean merge of the base branch — see carry_over)
  check-review-answered.py --repo O/R --stage-advance --workflow ci.yml (--head-sha S | --pr N | --sweep)
        (node-repo-stage-advance.yml: re-run a waiting run's failed jobs now that stage 1 is green)
  check-review-answered.py --repo O/R --pr N --request-review-on-change [--before <sha>]
        (node-repo-request-review.yml: request ONE Copilot review iff the head changed the pull
         request's own diff since Copilot's last review — see review_request_action)
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
`refresh_action` (pure, self-tested) skips a run that is already green or started at/after this
verdict (it judged a state no older than ours), and WAITS for a run still in flight, then judges
what it left (#7187: an `in_progress` run may already have published its red). The re-run decides nothing by itself:
it applies the whole predicate to live state, so a thread that is really unanswered is red there
too. No loop: a re-run of a `pull_request` run is a `pull_request` event, which never refreshes, and
a GITHUB_TOKEN re-run raises no new workflow event.
And after a GREEN verdict on ANY event (`pull_request` included, never `merge_group`) it re-runs
EVERY other run of this workflow on the head whose review check-run is red and started before the
verdict — `duplicate_red_action` (pure, self-tested): each run publishes its own duplicate of the
required context, and MeshWeaver.Plugins#4276 stayed blocked on three older review/comment reds
while the newest check-run read success.

`--as-of` evaluates the pull request as it stood at that instant (reviews, comments and waiver
events created later are ignored) — the controls in Doc/Architecture/ReviewFindingsAnswered use it
to replay a merge. Every GitHub call is `gh api` (REST) with the token in GH_TOKEN. Exit 0 = GREEN,
1 = RED, 2 = usage error, 3 = API OUTAGE — no verdict was taken (see ApiOutage, MeshWeaver#7187).

`--retry-outage --run-id R --attempt N` is the outage retry listener: it re-runs a completed run of
the review-answered lane only when its red was an outage (`retry_outage_action`), never a verdict.
"""
from __future__ import annotations

import argparse
import contextlib
import dataclasses
import datetime
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import tempfile
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
# The internal reviewer's App is being renamed `systemorph-com` → `systemorph`, in three steps:
# accept both spellings, rename, retire the old spelling. A rename changes the App's slug and its
# bot login; the App id and the bot account id stay. Until the third step BOTH spellings are the
# same App, and this is the one place this script names them.
INTERNAL_REVIEWER_LOGINS = frozenset({"systemorph-com[bot]", "systemorph[bot]"})
REVIEWER_ACCOUNT_IDS = frozenset({REVIEWER_ACCOUNT_ID, INTERNAL_REVIEWER_ACCOUNT_ID})
REVIEWER_LOGINS = frozenset({"copilot-pull-request-reviewer[bot]", "Copilot"}) | INTERNAL_REVIEWER_LOGINS
#: Policy `app-marked-answer-counts`: the control plane's GitHub App (the auto-answer lane, MeshWeaver.Plugins
#: `Hosting/PrThreadAnswerer`) — the internal reviewer's App, under BOTH its logins (INTERNAL_REVIEWER_LOGINS, the
#: rename). Its reply answers a thread ONLY when it carries the lane's marker naming that thread's root.
APP_ANSWER_ACCOUNT_ID = INTERNAL_REVIEWER_ACCOUNT_ID
APP_ANSWER_LOGINS = INTERNAL_REVIEWER_LOGINS
APP_ANSWER_MARKER = re.compile(r"<!-- pr-relay:answer comment=(?P<id>\d+) -->")
# A refusal OF THE PULL REQUEST (quota, outage): never a review, whatever else the body says —
# every spelling of it, so a `Copilot wasn't able to review this pull request because …` followed
# by a generated-file list cannot fall through to the zero-files exception below (#7488 review).
REFUSAL_OF_PR_MARKERS = (
    re.compile(r"\b(?:unable|wasn't able|was not able|could not|couldn't|cannot|can't) (?:to )?review this pull request\b", re.IGNORECASE),
)
# "Reviewed ZERO files": a refusal UNLESS every file it skipped is one Copilot itself lists as a
# `Generated file` — then there was nothing reviewable left out, and the verdict it posted over the
# rest (`### 🟢 Approval recommended …`) IS the review. Measured on MeshWeaver.Plugins#4507
# (2026-10-28, reviews 5679764146 and 5680977145): a lockfile-only pull request drew an overview
# verdict plus "Copilot wasn't able to review any files in this pull request." and a `1 file not
# reviewed` list whose one entry was `package-lock.json: Generated file`; the first marker below read
# that sentence as a refusal and the gate reported a real review as UNREVIEWABLE.
REFUSAL_NO_FILES_MARKERS = (
    re.compile(r"\bCopilot (?:was unable|wasn't able|was not able|could not|couldn't|cannot|can't) (?:to )?review\b", re.IGNORECASE),
    re.compile(r"\*\*Files reviewed:\*\*\s*0\s*/", re.IGNORECASE),
)
REFUSAL_MARKERS = REFUSAL_OF_PR_MARKERS + REFUSAL_NO_FILES_MARKERS
# One entry of the reviewer's "N file(s) not reviewed" list: `<li>…<code>path</code>…: REASON</li>`.
NOT_REVIEWED_ENTRY = re.compile(r"<li>.*?<code>[^<]+</code>.*?:\s*(?P<reason>[^<]+?)\s*</li>", re.IGNORECASE | re.DOTALL)
NOT_REVIEWED_COUNT = re.compile(r"\b(?P<n>\d+) files? not reviewed\b", re.IGNORECASE)
GENERATED_FILE_REASON = "generated file"
# The reviewer-unavailable degradation (MeshWeaver.Feedback#86): the check run the internal
# reviewer's App posts on the head commit when it could not review. Slug AND id, because a slug is
# a display name an App owner can change and an id cannot be claimed by another App. The id was
# read off a real `internal-review` run (109436738198 on d433fc0c10, 2026-09-29T13:45:38Z).
DEGRADATION_CHECK_NAME = "internal-review"
DEGRADATION_APP_SLUG = "systemorph-com"
# Both slugs of the one App across its rename (see INTERNAL_REVIEWER_LOGINS); the id does not change.
DEGRADATION_APP_SLUGS = frozenset({DEGRADATION_APP_SLUG, "systemorph"})
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


def is_app_answer(comment: dict, root_id) -> bool:
    """Policy `app-marked-answer-counts`: a reply by the control plane's App (either login, or its account id; type
    Bot) answers a thread ONLY when its body carries the auto-answer lane's marker naming THAT thread's root. An
    unmarked App reply, a marker naming another thread, and a marked reply from ANY other bot answer nothing. Pure."""
    user = comment.get("user") or {}
    if user.get("type") != "Bot" or not (user.get("id") == APP_ANSWER_ACCOUNT_ID or user.get("login") in APP_ANSWER_LOGINS):
        return False
    m = APP_ANSWER_MARKER.search(str(comment.get("body") or ""))
    return bool(m) and root_id is not None and int(m.group("id")) == root_id


def first_line(text: str | None) -> str:
    for line in (text or "").splitlines():
        if line.strip():
            return line.strip()[:160]
    return "(empty body)"


def only_generated_files_skipped(text: str) -> bool:
    """True iff the body lists the files the reviewer did not review, the list is complete (its
    entry count equals the `N file(s) not reviewed` it announces), and EVERY entry's reason is
    `Generated file`. Fails closed: no list, a short list, or any other reason (a language it does
    not support, a diff too large) keeps the zero-files body a refusal."""
    reasons = [m.group("reason").strip().lower() for m in NOT_REVIEWED_ENTRY.finditer(text)]
    announced = NOT_REVIEWED_COUNT.search(text)
    return (bool(reasons) and announced is not None and int(announced.group("n")) == len(reasons)
            and all(r == GENERATED_FILE_REASON for r in reasons))


def classify_review_body(body: str | None) -> str:
    """'refused' | 'landed' | 'unrecognised'. A refusal wins; anything else the reviewer says is
    the review.

    The caller has already established provenance (`is_reviewer`, state != PENDING), so the body
    is examined ONLY to separate a review from a refusal to review. An unfamiliar format is a new
    format, not an absence — see "PROVENANCE, NOT PRESENTATION" above for what keying this on a
    presentation substring cost. Only a body with no text at all is unrecognised: there is then
    nothing to read as either a review or a refusal."""
    text = body or ""
    if any(m.search(text) for m in REFUSAL_OF_PR_MARKERS):
        return "refused"
    if any(m.search(text) for m in REFUSAL_NO_FILES_MARKERS) and not only_generated_files_skipped(text):
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
    #: Set when the ONLY clean review on file is not a Copilot review of this pull request (the
    #: internal reviewer's; policy review-evidence-any-head, #4730) and that state stands (no
    #: waiver). A structured field for the same reason as `refused`: its remedy is a Copilot review,
    #: which no wait delivers (the ruleset requests one at open), so the wait must not run for it.
    not_copilot: str = ""
    #: Policy `review-on-real-change`: set when a Copilot review LANDED, but on an earlier head, and the
    #: pull request's own diff changed since (anything but a clean merge of the default branch) — the
    #: head owes its own review. Structured for the same reason as `refused`: its remedy is a review
    #: that ARRIVES (the review-on-change lane requests it), so it is waited for, never "reply to threads".
    stale: str = ""
    #: Condition 3 failed — the inputs could not be shown complete. Structured so the pending report
    #: (policy review-pending-while-answering) can never read an unmeasured state as "still answering".
    incomplete: str = ""
    #: Condition 1's reason when it stands (no review, no Copilot review, or this head owes one) — the
    #: input that ARRIVES. Empty when a review covers the head or a waiver / not-owed released it.
    review_missing: str = ""
    #: How many threads the automatic reviewer opened (condition 2's denominator, "N of M unanswered").
    threads: int = 0


def newest_landed_copilot_review(reviews, as_of: str | None = None) -> dict | None:
    """The newest landed Copilot review on the pull request, whichever head it reviewed."""
    mine = landed_copilot_reviews(reviews, as_of)
    return max(mine, key=lambda r: (r.get("submitted_at") or "", r.get("id") or 0)) if mine else None


def copilot_currency(reviews, head: str, carry: Carry | None, as_of: str | None = None) -> tuple[dict | None, str, str]:
    """Policy `review-on-real-change` — (the Copilot review that covers `head`, a note, '') or
    (None, '', why the head owes a review), or (None, '', '') when no Copilot review has landed at all
    (condition 1 decides that). A review covers the head it was submitted on, and a LATER head only
    when `carry` proves — from the repository — that every commit since is a merge of the default
    branch that left the pull request's own diff unchanged (`copilot_carry_over`). Pure."""
    on_head = copilot_review_on(reviews, head, as_of)
    if on_head is not None:
        return on_head, "", ""
    latest = newest_landed_copilot_review(reviews, as_of)
    if latest is None:
        return None, "", ""
    reviewed = str(latest.get("commit_id") or "")
    if carry is not None and carry.ok and carry.from_head == reviewed and reviewed:
        return latest, carry.why, ""
    why = carry.why if carry is not None and carry.from_head in ("", reviewed) else (
        f"Copilot's latest review ({latest.get('id')}) is of {reviewed[:10] or '(unknown)'}, not of head {head[:10]}, "
        "and nothing showed that only the default branch was merged in since")
    return None, "", (f"head {head[:10]} owes its own Copilot review — the pull request's own diff changed since Copilot "
                      f"reviewed it (policy review-on-real-change): {why}")


def is_degradation_app(app: dict | None) -> bool:
    return bool(app) and app.get("slug") in DEGRADATION_APP_SLUGS and app.get("id") == DEGRADATION_APP_ID


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
             check_runs: list | tuple = (), files: list | None = None, commits: list | None = None,
             copilot_carry: Carry | None = None) -> Verdict:
    """`copilot_carry` is the adapter's `copilot_carry_over` from Copilot's newest landed review to the
    head (None when there is nothing to carry, or it was not computed — then an earlier head's review
    does NOT cover the head: policy review-on-real-change fails closed)."""
    reasons: list[str] = []
    notes: list[str] = []
    refused: list[str] = []
    stale_reported = ""
    owes_review = ""
    review_missing = ""

    # 3 (checked first: an incomplete listing makes every other statement unreliable)
    incomplete = listing_incomplete(pr, comments)
    if incomplete:
        reasons.append(incomplete)

    # 1 — has the review landed?
    mine = [r for r in reviews
            if is_reviewer(r.get("user")) and r.get("state") != "PENDING" and not_after(r.get("submitted_at"), as_of)]
    kinds = [(classify_review_body(r.get("body")), r) for r in mine]
    landed = [r for k, r in kinds if k == "landed"]
    # Policy `review-evidence-any-head` (#4730): with NO finding on the pull request, the only
    # evidence that separates "reviewed, nothing found" from "never reviewed" is a completed Copilot
    # review object on the pull request — on ANY of its heads, so a push after the review (a merge
    # of main) does not re-open the question; a re-review per push is not required. The internal
    # reviewer's clean review is not that evidence. With findings, the threads themselves prove a
    # review happened, and condition 2 decides (policy review-once-per-pull-request, unchanged).
    roots, unanswered = reviewer_threads(comments, as_of)
    head = str((pr.get("head") or {}).get("sha") or "")
    clean_copilot = copilot_review_of_pull_request(reviews, head, as_of)
    not_copilot = ""
    if landed and not roots and clean_copilot is None:
        r = landed[-1]
        not_copilot = (f"review {r.get('id')} by @{(r.get('user') or {}).get('login')} at {r.get('submitted_at')} "
                       f"on {str(r.get('commit_id'))[:10]} found nothing, but it is not a Copilot review")
        landed = []
    if landed:
        r = clean_copilot if (clean_copilot is not None and not roots) else landed[-1]
        on = str(r.get("commit_id") or "")[:10]
        notes.append(f"automatic review landed: review {r.get('id')} at {r.get('submitted_at')} on {on}"
                     + ((" — reviewed, nothing found: a Copilot review with zero findings"
                         + ("" if head and on == head[:10] else f" (of an earlier head; current head {head[:10] or '(unknown)'})")
                         + " (policy review-evidence-any-head)") if not roots else ""))
        # 1b — policy review-on-real-change: the review must COVER this head. An earlier head's
        # Copilot review covers it only across clean merges of the default branch; a head whose own
        # diff changed owes its own review (the review-on-change lane requests it), and the
        # threads of the earlier review do not stand in for it.
        _covering, carry_note, stale = copilot_currency(reviews, head, copilot_carry, as_of)
        if carry_note:
            notes.append(carry_note)
        if stale:
            granted, message = waiver_holder(waiver, as_of)
            generated, generated_why = generated_only(pr, files, commits) if is_generated_bot(pr.get("user")) else (False, "")
            if generated:
                notes.append(f"NOT OWED: {stale}. {generated_why} — nothing to review (generated_only)")
            elif granted:
                notes.append(f"WAIVED: {stale}. {message}")
            else:
                reasons.append(stale)
                owes_review = stale
                review_missing = stale
                # A REFUSAL of this very head, after the review that no longer covers it, is terminal:
                # the review-on-change lane requests nothing over a refusal, so waiting for the owed
                # review would only sleep the full window. Report it as unreviewable (#7142 review).
                after = str(newest_landed_copilot_review(reviews, as_of).get("submitted_at") or "")
                refused += [first_line(r.get("body")) for k, r in kinds
                            if k == "refused" and head and str(r.get("commit_id") or "") == head
                            and str(r.get("submitted_at") or "") > after]
    else:
        if not_copilot:
            why = ("no Copilot review of this pull request — \"reviewed, nothing found\" is ONLY a completed Copilot review "
                   f"with zero findings, on any head (policy review-evidence-any-head): {not_copilot}")
        elif not mine:
            why = "the automatic review has not landed — no review by the automatic reviewer on this pull request"
        else:
            parts = [f"review {r.get('id')} at {r.get('submitted_at')} is {'a refusal' if k == 'refused' else 'an unrecognised body'}: \"{first_line(r.get('body'))}\"" for k, r in kinds]
            why = "the automatic review has not landed — the reviewer posted, but not a review: " + "; ".join(parts)
        granted, message = waiver_holder(waiver, as_of)
        # 🚨 A GENERATED-ONLY App pull request owes NO review (Plugins #3044): the same provenance rule
        # the stage gate already applies (`generated_only`, never a title or a branch name). The reviewer
        # REFUSES such a pull request ("Copilot wasn't able to review any files" — locks are excluded),
        # so this condition could only be released by a person's waiver; until one came, the lock settle
        # and the floor stamp sat red, main published, sealed and tagged nothing, and every portal held
        # each module behind its newest prebuilt (Governance 0.10 installed, 0.9.16 serving, 2026-10-08).
        # 🚨 Only on a TERMINAL signal that no review is coming — never before the reviewer could still answer:
        # a pass released on the `opened` evaluation could merge before a late thread arrives, and a thread
        # that opens after the merge can block nothing (#6318 review). Two signals are terminal: the
        # reviewer's REFUSAL, or no review requested at all for GENERATED_SETTLE_MINUTES after the pull
        # request opened — the automatic review is requested at open and does not reach the App's own pull
        # requests (measured 2026-10-08: settle #3191 had no requested reviewer and no review 20 minutes in,
        # so a refusal-only rule held it forever). This is NOT OWED, decided on provenance (every file
        # generated, every commit the App's) — it never claims the pull request was reviewed, so it sits
        # outside policy review-evidence-any-head, which governs what counts as "reviewed".
        # The internal reviewer's "Reviewer unavailable" degradation, by contrast, released condition 1
        # for a person's pull request that NOBODY reviewed — it read "never reviewed" as enough, so it
        # no longer releases this gate (#4730); the `review-waived` label is the escape.
        generated, generated_why = generated_only(pr, files, commits) if is_generated_bot(pr.get("user")) else (False, "")
        # The no-request signal applies only when the reviewer has posted NOTHING: an unrecognised response
        # (an empty body) also removes the pending request, and must stay "not a review" (#6336 review).
        # A generated-only head with only the internal reviewer's clean review is NOT OWED for the same
        # reason: there is still nothing to review.
        if generated and (not_copilot or any(k == "refused" for k, _ in kinds) or (not mine and no_review_coming(pr, as_of))):
            notes.append(f"NOT OWED: {why}. {generated_why} — nothing to review (generated_only)")
        elif granted:
            notes.append(f"WAIVED: {why}. {message}")
        else:
            reasons.append(why + (f". {message}" if message else ""))
            review_missing = reasons[-1]
            stale_reported = not_copilot
            # Only when the state actually STANDS: a waived refusal is not an unreviewable pull
            # request, it is a reviewed-enough one, and saying otherwise would re-create the
            # confusion in the other direction.
            # A review DID land (not_copilot), so an older refusal is history, not this state: the
            # reader must be told "no Copilot review", never "unreviewable" (#6371 review).
            if not not_copilot:
                refused += [first_line(r.get("body")) for k, r in kinds if k == "refused"]

    # 2 — is every thread the reviewer started answered? (`roots`/`unanswered` were read above)
    notes.append(f"threads opened by the automatic reviewer: {len(roots)}, answered by a person: {len(roots) - len(unanswered)}")
    if unanswered:
        reasons.append(f"{len(unanswered)} of {len(roots)} thread(s) opened by the automatic reviewer have no reply from a person")

    return Verdict(green=not reasons, reasons=tuple(reasons), notes=tuple(notes), unanswered=unanswered,
                   refused=tuple(refused), not_copilot=stale_reported, stale=owes_review,
                   incomplete=incomplete or "", review_missing=review_missing, threads=len(roots))


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
                      if c.get("in_reply_to_id") is not None
                      and (is_person(c.get("user")) or is_app_answer(c, root_id_of(c, by_id)))}
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
    # 🚨 A head that OWES its own review (policy review-on-real-change, `Verdict.stale`) is waited for
    # exactly like a review that has not landed: the review-on-change lane requested it on the push
    # that changed the diff, and its arrival starts no run here (the same `action_required` wall).
    return (not verdict.green
            and not verdict.refused
            and not verdict.not_copilot
            and len(verdict.reasons) == 1
            and (bool(verdict.stale) or "has not landed" in verdict.reasons[0]))


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
# GitHub Copilot back as the fleet's reviewer; the ruleset rule runs with `review_on_push: true`, so
# every push starts a new review round, and the gate half of `review-once-per-pull-request` keeps any
# head's review valid while the new one lands. A landed Copilot review of the pull request — against the current head, or failing that
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
    """Policy `review-once-per-pull-request`, gate half (any-head evidence): the Copilot review that
    counts — the newest landed review against `head_sha` when there is one, otherwise the newest
    landed review against ANY earlier head. With `review_on_push: true` every push requests a fresh
    review, so the earlier head's review is what counts while the current head's own review lands. `pulls/{n}/reviews` lists only this pull request's reviews, so every review it
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
            "an earlier head's review counts while this head's own review lands (any-head gate, policy "
            "review-once-per-pull-request)")


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


def merge_only_same_diff(head: str, new_cmp: dict, old_cmp: dict, old: str) -> tuple[str, str]:
    """('', evidence) when every commit on the pull request at `head` that was not on it at `old` is a
    MERGE commit and the pull request's own diff (merge-base(base, x)..x) is byte-identical at both —
    i.e. only the base branch was merged in. (why not, '') otherwise. `new_cmp`/`old_cmp` are
    compare(base...head) / compare(base...old) with COMPLETE `commits` (the adapter pages them).
    ONE implementation, shared by the internal-review carry-over (`carry_over`), the merge gate's
    Copilot-review currency (`copilot_carry_over`) and the review-on-change lane
    (`review_request_action`), so the three can never disagree about what a "real change" is. Pure."""
    commits_new = {c.get("sha"): c for c in new_cmp.get("commits") or []}
    commits_old = {c.get("sha") for c in old_cmp.get("commits") or []}
    for name, cmp_, got in (("head", new_cmp, len(commits_new)), ("reviewed head", old_cmp, len(commits_old))):
        if not isinstance(cmp_.get("total_commits"), int) or got < cmp_["total_commits"]:
            return f"the {name}'s commit listing returned {got} of {cmp_.get('total_commits')} commits", ""
    if old not in commits_old:
        return "the reviewed head is no longer ahead of the base branch, so its diff is not comparable", ""
    if head not in commits_new:
        return "the head is not ahead of the base branch", ""
    if old not in commits_new:
        # A force-push that dropped the reviewed head: whatever replaced it was never reviewed.
        return "the reviewed head is no longer on the pull request (its history was rewritten)", ""
    fresh = [c for s, c in commits_new.items() if s not in commits_old]
    content = [c for c in fresh if len(c.get("parents") or []) < 2]
    if content:
        return f"{len(content)} non-merge commit(s) since it (first: {str(content[0].get('sha'))[:10]})", ""
    id_old, why_old = pr_diff_id(old_cmp.get("files"))
    id_new, why_new = pr_diff_id(new_cmp.get("files"))
    if id_old is None or id_new is None:
        return f"the pull request's diff cannot be fingerprinted ({why_old or why_new})", ""
    mb_old = str((old_cmp.get("merge_base_commit") or {}).get("sha") or "")[:10]
    mb_new = str((new_cmp.get("merge_base_commit") or {}).get("sha") or "")[:10]
    if id_old != id_new:
        return (f"the pull request's own diff changed — patch-id {id_old[:16]} ({mb_old}..{old[:10]}) vs "
                f"{id_new[:16]} ({mb_new}..{head[:10]}): a new commit, a conflict resolution or a change inside the merge altered it"), ""
    merges = ", ".join(str(c.get("sha"))[:10] for c in fresh) or "none"
    return "", (f"the {len(fresh)} commit(s) since it are all merges of the base branch ({merges}), and the pull request's "
                f"own diff is byte-identical — patch-id {id_new[:16]} over {len(new_cmp.get('files') or [])} file(s), "
                f"{mb_old}..{old[:10]} = {mb_new}..{head[:10]}")


def carry_over(head: str, new_cmp: dict, old_cmp: dict, old: str, run: dict) -> Carry:
    """Conditions (b) and (c) for a reviewed ancestor `old` that (a) found (`merge_only_same_diff`). Pure."""
    why, evidence = merge_only_same_diff(head, new_cmp, old_cmp, old)
    if why:
        return Carry(False, f"no carry-over from reviewed head {old[:10]}: {why} — the head owes a fresh review")
    title = ((run.get("output") or {}).get("title") or "").strip()
    return Carry(True, (f"review CARRIED from head {old[:10]} (`{DEGRADATION_CHECK_NAME}` check run {run.get('id')}: "
                        f"{run.get('conclusion')} \"{title}\") to {head[:10]}: {evidence} "
                        "(policy review-carries-over-clean-base-merge)"), old, run)


def copilot_carry_over(head: str, new_cmp: dict, old_cmp: dict, review: dict) -> Carry:
    """Policy `review-on-real-change`: does Copilot's review of `review.commit_id` still cover `head`?
    It does exactly when only the default branch was merged in since (`merge_only_same_diff`). `run`
    of the returned Carry is the review itself. Pure."""
    old = str(review.get("commit_id") or "")
    if not re.fullmatch(r"[0-9a-f]{40}", old):
        return Carry(False, f"Copilot review {review.get('id')} names no reviewed commit, so it cannot be shown to cover {head[:10]}")
    why, evidence = merge_only_same_diff(head, new_cmp, old_cmp, old)
    what = f"Copilot's review {review.get('id')} of {old[:10]}" if review.get("id") else f"the previous head {old[:10]}"
    if why:
        return Carry(False, f"{what} does not cover head {head[:10]}: {why}", old, review)
    return Carry(True, f"{what} COVERS head {head[:10]}: {evidence} (policy review-on-real-change)", old, review)


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


#: How long after it opened a generated-only App pull request with no requested reviewer is taken to get none.
GENERATED_SETTLE_MINUTES = 10


def no_review_coming(pr: dict, as_of: str | None) -> bool:
    """True when no automatic review is requested on the pull request NOW and it opened at least
    GENERATED_SETTLE_MINUTES ago. FAILS CLOSED (#6336 review): `requested_reviewers` is the pull request's
    CURRENT state, so a replay (`as_of`) cannot read what was pending then and never releases; a payload
    without the field is not a proven-empty list; an unreadable stamp is never "no review"."""
    if as_of is not None:
        return False
    requested = pr.get("requested_reviewers")
    if not isinstance(requested, list) or any(is_reviewer(u) for u in requested):
        return False
    opened = parse_stamp(pr.get("created_at"))
    now = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None)
    return opened is not None and (now - opened) >= datetime.timedelta(minutes=GENERATED_SETTLE_MINUTES)


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


# 🚨 ARMING A GENERATED-ONLY PULL REQUEST (`--arm-generated`, called by auto-arm.yml's second job).
# The control plane's PR steward used to be the only thing that armed the settle/stamp pull requests,
# and it is switched off fleet-wide (it spent the model budget); Plugins #3236 then sat green and
# unarmed for ~10.5 h and Plugins published nothing for ~13.5 h. A generated-only pull request owes no
# review (`generated_only`, above — the same provenance rule the stage gate and the required verdict
# apply), so arming one decides nothing a review would have decided; the required checks still gate
# the merge. Everything else stays the control plane's decision: this verdict is FALSE for any pull
# request the rule does not positively prove generated, and for every doubt about the reads.
def arm_generated_verdict(pr: dict, files: list | None, commits: list | None,
                          head_after: str | None) -> tuple[bool, str]:
    """(True, why) only when a pull request may be armed with no review: generated-only by the App,
    open, not a draft, from a branch of this repository, onto the default branch, and its head did not
    move while its files and commits were read. Pure; fails closed."""
    if not is_generated_bot(pr.get("user")):
        return False, "not authored by the generated-files App — arming stays the control plane's decision"
    if pr.get("state") != "open":
        return False, f"it is {pr.get('state')!r}, not open"
    if pr.get("draft") is not False:
        return False, "it is a draft (or its draft flag is unread) — a draft is the author's own hold"
    head, base = pr.get("head") or {}, pr.get("base") or {}
    head_repo = (head.get("repo") or {}).get("full_name")
    base_repo = (base.get("repo") or {}).get("full_name")
    if not head_repo or head_repo != base_repo:
        return False, f"its head is not a branch of this repository ({head_repo!r} vs {base_repo!r})"
    default = (base.get("repo") or {}).get("default_branch")
    if not default or base.get("ref") != default:
        return False, f"it targets {base.get('ref')!r}, not the default branch {default!r}"
    sha = str(head.get("sha") or "")
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        return False, "it reported no head sha"
    # Completeness is PROVEN here, not inferred: `generated_only` reads a missing count as zero, so a
    # partial listing of a payload without `changed_files`/`commits` would pass it (#6412 review).
    for field, listing in (("changed_files", files), ("commits", commits)):
        count = pr.get(field)
        if not isinstance(count, int) or isinstance(count, bool) or count <= 0:
            return False, f"its `{field}` count is {count!r}, not a positive number — the listing cannot be shown complete"
        if not isinstance(listing, list) or len(listing) != count:
            return False, f"the {field} listing returned {len(listing) if isinstance(listing, list) else None} of {count}"
    ok, why = generated_only(pr, files, commits)
    if not ok:
        return False, why
    if head_after != sha:
        return False, f"its head moved from {sha[:10]} to {str(head_after)[:10]} while its files were read"
    return True, why


def run_arm_generated(repo: str, number: int) -> int:
    """Exit 0 with `arm=true|false` (+ `head=`) on $GITHUB_OUTPUT; exit 1 (RED, nothing armed) when an
    App pull request's reads fail. A person's pull request is decided `false` without further reads."""
    head = ""
    try:
        gh = Gh(repo)
        pr = gh.api(f"pulls/{number}")
        if not isinstance(pr, dict) or pr.get("number") != number:
            raise ReadError(f"pulls/{number} did not return pull request #{number}")
        files = commits = None
        head_after = None
        if is_generated_bot(pr.get("user")):
            files = gh.api(f"pulls/{number}/files?per_page=100", paginate=True)
            commits = gh.api(f"pulls/{number}/commits?per_page=100", paginate=True)
            head_after = str(((gh.api(f"pulls/{number}") or {}).get("head") or {}).get("sha") or "")
        arm, why = arm_generated_verdict(pr, files, commits, head_after)
        head = str((pr.get("head") or {}).get("sha") or "") if arm else ""
    except (ReadError, KeyError, TypeError, ValueError) as e:
        print(f"::error::cannot read #{number}, so it is NOT armed: {e}")
        _append("GITHUB_OUTPUT", "arm=false\nhead=\n")
        return 1
    except ApiOutage as e:
        _append("GITHUB_OUTPUT", "arm=false\nhead=\n")
        return report_outage(e)
    print(f"{'ARM' if arm else 'not armed'} #{number}: {why}")
    _append("GITHUB_OUTPUT", f"arm={'true' if arm else 'false'}\nhead={head}\n")
    _append("GITHUB_STEP_SUMMARY", f"{'✅ arming' if arm else 'ℹ️ not arming'} #{number}: {why}\n")
    return 0


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


#: The two re-run endpoints, and which caller needs which. `rerun-failed-jobs` re-runs only the jobs
#: that failed or were cancelled, keeping every green job of an expensive CI run — right for the
#: stage advance, whose held run is a full test run. It is REFUSED (403 "This workflow run cannot be
#: retried") for a run that has no failed job to re-run, and the commonest such run is one CANCELLED
#: BEFORE ANY JOB STARTED: a concurrency group replaced it while pending, so it carries ZERO jobs
#: (measured on #6315, run 37807892371 — `rerun-failed-jobs` 403, `rerun` accepted). `rerun` re-runs
#: the WHOLE run and is accepted for every completed run, zero jobs included.
RERUN_FAILED_JOBS = "rerun-failed-jobs"
RERUN_WHOLE_RUN = "rerun"


def post_rerun(gh, run_id, endpoint: str = RERUN_FAILED_JOBS) -> bool:
    """POST a re-run (`endpoint`: RERUN_FAILED_JOBS by default, RERUN_WHOLE_RUN for the refresh).
    True when this call re-ran the run; False when it lost a race (the sweep and a listener) and
    another invocation already re-ran it. Any other failure is RAISED: `Gh.post` and `Gh.api` both
    raise ReadError on a failed call, so a refused POST whose run is still `completed`, or an
    unreadable read-back, is never masked. Self-tested with a stub `gh`."""
    if endpoint not in (RERUN_FAILED_JOBS, RERUN_WHOLE_RUN):
        raise ValueError(f"not a re-run endpoint: {endpoint!r}")
    try:
        gh.post(f"actions/runs/{run_id}/{endpoint}")
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


#: Run statuses of a run none of whose jobs has started: it read nothing yet (`pending` = held by a
#: concurrency group, `queued` = waiting for a runner, `waiting` = an environment gate, `requested`).
NOT_STARTED_RUN_STATUSES = ("requested", "pending", "queued", "waiting")

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
    re-run, the candidate is in flight (skip), then green (skip) or red on a NEWER read (skip).

    🚨 The re-run is of the WHOLE run (RERUN_WHOLE_RUN), never its failed jobs. What the refresh must
    achieve is a fresh `pull_request` evaluation of live state, and every job of this lane reads live
    state, so re-running the whole run loses nothing. The newest `pull_request` run is very often one
    a newer arrival REPLACED while it was pending — cancelled with ZERO jobs — and GitHub refuses
    `rerun-failed-jobs` for that run (#6315, run 37807892371). Such a run published no check-run, so
    the pull request has no `pull_request` verdict on this head at all until it runs."""
    head = str((pr.get("head") or {}).get("sha") or "")
    if event == PROTECTION_READ_EVENT:
        # Its own verdict is a `pull_request` one; the older reds of OTHER events on the head are the
        # duplicate sweep's (duplicate_red_action — #4276 was exactly this run going green by hand).
        return "none", f"this run IS a `{PROTECTION_READ_EVENT}` run — its own verdict is a `{PROTECTION_READ_EVENT}` one"
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
    if candidate.get("status") in NOT_STARTED_RUN_STATUSES:
        # 🚨 Never "wait" here (MeshWeaver#7203 review): a `pull_request` run that has not STARTED is
        # very often pending in this very run's concurrency group (`…-gate`, review-answered.yml), so it
        # cannot start until this run ends — waiting for it would hold it back for the whole wait and
        # then fail. Nothing needs refreshing: no job of it has read anything yet, so every verdict it
        # publishes reads live state AFTER this verdict was taken.
        return "none", (f"`{PROTECTION_READ_EVENT}` run {candidate.get('id')} is {candidate.get('status')} — it has not "
                        "started, so it reads live state after this verdict; nothing to refresh, and waiting for it "
                        "could hold it back in the shared concurrency group")
    if candidate.get("status") != "completed":
        # 🚨 NOT "it reads live state itself" (MeshWeaver#7187): a run is `in_progress` until its LAST
        # job ends, and the job whose check-run branch protection reads may have finished — red, on a
        # read taken before this verdict's state — while the lane or refresh job of the same run is
        # still going (measured on #7198: run 40194864793's gate job completed 13:00:58Z, the run was
        # still `in_progress` at 13:01:31Z when the refresh skipped it). A skip there left that red
        # standing with nothing to refresh it. So the refresh lets the run finish and then judges it.
        return "wait", f"`{PROTECTION_READ_EVENT}` run {candidate.get('id')} is still {candidate.get('status')} — letting it finish, then judging what it left"
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
                     f"{candidate.get('id')}, which concluded {candidate.get('conclusion')} before it — re-running the whole run")


# ─────────────────────────── EVERY OLDER RED ON THE HEAD (the duplicate sweep) ───────────────────────────
#
# 🚨 `refresh_action` alone does NOT converge (MeshWeaver.Plugins#4276, head 264517aeaf). Every run of
# this workflow publishes its OWN check-run named `… Automatic review answered` on the head, one per
# event (`pull_request`, `pull_request_review`, `pull_request_review_comment`), and the merge refused
# with 405 "Required status check … is failing" while the NEWEST such check-run read success: three
# older runs (40342994248, 40343008573, 40343010678 — a review and two comment events) still carried
# the red each took truthfully at 14:48Z ("head owes its own Copilot review"). Copilot's review landed
# at 18:28Z and started no run; the `pull_request` run was re-run by hand at 18:36Z and went GREEN —
# and its refresh did nothing, because a `pull_request` verdict never refreshed, and no refresh ever
# looked at a run of any other event. So which of the duplicates branch protection reads is not
# assumed here at all (#4649 measured one selection, #4276 another): after a GREEN verdict, EVERY
# completed run on the head whose review check-run is red and that started before the verdict is
# re-run, whatever its event. A re-run applies the whole predicate to live state, so a thread that is
# really unanswered is red there too — the sweep softens nothing.

#: The job (= check-run) name of this check. A reusable call prefixes it with the caller's job id
#: (`review-answered / Automatic review answered` in a satellite, `lane / …` beside core's own job).
REVIEW_CHECK_NAME = "Automatic review answered"

#: Conclusions branch protection accepts for a required context. Everything else — failure,
#: cancelled, timed_out, action_required, startup_failure, stale — is a red that blocks.
PASSING_CONCLUSIONS = ("success", "neutral", "skipped")


def is_review_check_job(name) -> bool:
    """True for the job that publishes this check's required context, under any caller prefix — the
    job-shaped check AND the verdict writer (VERDICT_WRITER_JOB, policy review-pending-while-answering),
    whose run would otherwise read as "has not created its review job yet" and be waited on until the
    refresh deadline (#7653 review). Pure."""
    name = str(name or "")
    return any(name == n or name.endswith(f" / {n}") for n in (REVIEW_CHECK_NAME, VERDICT_WRITER_JOB))


def sweep_precondition(event: str, repo: str, pr: dict) -> str | None:
    """Why the duplicate sweep must NOT run for this verdict (None = it runs). Pure. A merge-queue entry
    is its own commit; a closed pull request needs nothing; a fork's token cannot re-run anything
    (`refresh_action` already says `manual` for it, naming the remedy)."""
    if event == "merge_group":
        return "a merge-queue entry is its own commit — nothing on the pull request head to sweep"
    if pr.get("state") not in (None, "open"):
        return f"#{pr.get('number')} is {pr.get('state')}"
    head_repo = str(((pr.get("head") or {}).get("repo") or {}).get("full_name") or "")
    if head_repo and head_repo != repo:
        return f"#{pr.get('number')} comes from the fork {head_repo} — its token cannot re-run a workflow"
    return None


def in_flight_policy(run: dict, jobs: list | None, own_created_at: str) -> tuple[str, str] | None:
    """THE ONE wait policy for a run that has started and not completed, shared by BOTH decision paths
    (`refresh_action`'s newest `pull_request` run and `duplicate_red_action`'s sweep). None when the run
    is completed (nothing to wait for). Pure.

    A refresh may wait on a REVIEW job still in flight: a review job never waits on a refresh, so that
    wait cannot cycle. Once a run's review jobs are done, what keeps it `in_progress` is its own refresh
    — and waiting on THAT is waited for only when the run is OLDER than this one. Two refreshes then
    never wait on each other, whichever path each is deciding on (#7257 review: an older comment run
    waiting on a newer `pull_request` run, which waited on the older run's refresh, until both hit the
    30-minute wait). A run whose review check-runs are already green is not waited for at all."""
    if run.get("status") == "completed":
        return None
    rid, ev = run.get("id"), run.get("event")
    review = [j for j in (jobs or []) if is_review_check_job(j.get("name"))]
    if not review:
        return "wait", f"run {rid} ({ev}) is {run.get('status')} and has not created its review job yet"
    flying = [j for j in review if j.get("status") != "completed"]
    if flying:
        return "wait", f"run {rid} ({ev}): `{flying[0].get('name')}` is still {flying[0].get('status')} — letting it finish"
    reds = [j for j in review if j.get("conclusion") not in PASSING_CONCLUSIONS]
    if not reds:
        return "none", f"run {rid} ({ev}): its review check-run already reads success; only its other jobs are still running"
    red = f"`{reds[0].get('name')}` concluded {reds[0].get('conclusion')}"
    if str(run.get("created_at") or "") < str(own_created_at or ""):
        return "wait", f"run {rid} ({ev}): {red}, but the run is still {run.get('status')} — a re-run needs it completed"
    if not any(j.get("conclusion") in PASSING_CONCLUSIONS for j in review):
        # Every review job of it is red, so its refresh (`needs` a GREEN verdict) is skipped: it waits on
        # nothing and finishes promptly. Waiting for it cannot cycle.
        return "wait", (f"run {rid} ({ev}): {red}; it is newer, but none of its review jobs is green, so its refresh "
                        "is skipped and it waits on nothing — letting it finish")
    # A newer run whose review jobs DISAGREE (core runs two — `answered` and `lane`): its own refresh is
    # running and may wait on this run, so it is not waited for. It cannot re-run itself either; the
    # retry listener re-runs it when it completes (`self_contradicting`, #7257 review).
    return "none", (f"run {rid} ({ev}): {red} while the run is still {run.get('status')}, and another of its review "
                    "jobs is green; it is NEWER than this run, so it is not waited for (no two refreshes wait on each "
                    "other) — the retry listener re-runs it once it completes")


#: The caller's concurrency group is split by event class (review-answered.yml, and the lane header's
#: caller contract): `…-feedback` for review-comment runs, `…-gate` for every other event. Each group
#: keeps ONE pending run, and a re-run enters the group like a new run.
FEEDBACK_LANE_EVENT = "pull_request_review_comment"


def concurrency_lane(event) -> str:
    """The caller's concurrency-group suffix for a run of this event. Pure."""
    return "feedback" if event == FEEDBACK_LANE_EVENT else "gate"


def plan_reruns(selected: list, runs: list, own_run_id) -> list:
    """Of the runs the two decision paths chose to re-run (`selected`, in priority order: the newest
    `pull_request` run first, then the oldest red), POST at most ONE per concurrency group, and none into
    a group that already holds a pending run. Returns [(run, 'rerun' | 'none', why)] in input order. Pure.

    🚨 Each group keeps ONE pending run (#7257 review), so a second re-run POSTed into it REPLACES the
    first before it evaluates anything: the replaced attempt is cancelled with zero jobs and its red
    stands, while the refresh reports success. So the reds are re-run as a CHAIN: the chosen re-run
    evaluates live state, and on GREEN its own refresh re-runs the next red, which started before that
    verdict too. A red that is still red after its re-run stops the chain, and that red is true. A
    pending run already in the group is the same chain's link: it evaluates after this verdict and its
    GREEN refresh carries on, while a re-run now would replace it."""
    planned, chosen = [], {}
    pending = {}
    for r in runs:
        if str(r.get("id")) != str(own_run_id) and r.get("status") in NOT_STARTED_RUN_STATUSES:
            pending.setdefault(concurrency_lane(r.get("event")), r)
    for r, why in selected:
        lane = concurrency_lane(r.get("event"))
        if lane in pending:
            planned.append((r, "none", (f"deferred: run {pending[lane].get('id')} is already pending in the `…-{lane}` group — it "
                                        "evaluates live state after this verdict and its GREEN refresh re-runs this red; "
                                        "a re-run now would replace it")))
        elif lane in chosen:
            planned.append((r, "none", (f"deferred: one re-run per `…-{lane}` group (it keeps ONE pending run, so a second "
                                        f"would replace the first) — run {chosen[lane].get('id')}'s GREEN refresh re-runs this one next")))
        else:
            chosen[lane] = r
            planned.append((r, "rerun", why))
    return planned


def duplicate_red_action(run: dict, jobs: list | None, own_run_id, own_created_at: str, evaluated_at: str,
                         head: str) -> tuple[str, str]:
    """For ONE other run of this workflow on the head, after a GREEN verdict: ('rerun' | 'wait' | 'none' |
    'fail', why). Pure. `jobs` is that run's (latest attempt's) job listing — read only when the run can
    carry a red at all; `own_created_at` is when THIS run was created.

    Re-run iff its review check-run is RED, the run is completed (GitHub refuses to re-run one in
    flight) and it STARTED before this verdict (it judged an older state). Wait for a review job still
    in flight — it may yet publish a red read before this verdict (#7187), and a review job never waits
    on a refresh, so that wait cannot cycle. A red inside a run that is still finishing (its own refresh
    is running) is waited for only when that run is OLDER than this one: waiting only on older runs is
    what keeps two refreshes from waiting on each other."""
    rid = run.get("id")
    if str(rid) == str(own_run_id):
        return "none", f"run {rid} is this run"
    if run.get("head_sha") != head:
        return "none", f"run {rid} is for {str(run.get('head_sha'))[:10]}, not the current head {head[:10]}"
    if run.get("status") in NOT_STARTED_RUN_STATUSES:
        return "none", f"run {rid} ({run.get('event')}) is {run.get('status')} — it has not started, so it reads live state after this verdict"
    if run.get("status") == "completed" and run.get("conclusion") == "success":
        return "none", f"run {rid} ({run.get('event')}) already reads success"
    review = [j for j in (jobs or []) if is_review_check_job(j.get("name"))]
    if not review:
        if run.get("status") == "completed":
            return "none", (f"run {rid} ({run.get('event')}, {run.get('conclusion')}) published no `{REVIEW_CHECK_NAME}` "
                            "check-run (replaced while pending — zero jobs), so it holds no red")
        return "wait", f"run {rid} ({run.get('event')}) is {run.get('status')} and has not created its review job yet"
    in_flight = in_flight_policy(run, jobs, own_created_at)
    if in_flight is not None:
        return in_flight
    reds = [j for j in review if j.get("conclusion") not in PASSING_CONCLUSIONS]
    if not reds:
        return "none", f"run {rid} ({run.get('event')}): its review check-run already reads success"
    red = f"`{reds[0].get('name')}` concluded {reds[0].get('conclusion')}"
    started, judged = parse_stamp(run.get("run_started_at")), parse_stamp(evaluated_at)
    if judged is None:
        return "fail", f"the verdict's own time {evaluated_at!r} is unreadable, so staleness cannot be judged"
    if started is not None and started >= judged:
        return "none", (f"run {rid} ({run.get('event')}): {red}, but it started at {run.get('run_started_at')}, not before "
                        f"this verdict ({evaluated_at}) — it judged a state no older than this one")
    return "rerun", (f"run {rid} ({run.get('event')}): {red} before this GREEN verdict — branch protection may read "
                     "any duplicate of this check on the head, so re-running the whole run")


# ─────────────────────────────── REVIEW ON REAL CHANGE (pure) ───────────────────────────────
#
# Policy `review-on-real-change` (Doc/Architecture/ReviewFindingsAnswered → "A review on every real
# change, none for a merge of main"). The rulesets' `copilot_code_review` rule runs with
# `review_on_push: false`: Copilot reviews a pull request when it opens, and a push that ONLY merges
# the default branch must not cost a review round (new threads, a red `Automatic review answered`).
# A push that changes the pull request's own diff still gets one — this lane requests it, with ONE
# GraphQL `requestReviews` call (REST `requested_reviewers` refuses bots). What "only merged the
# default branch" means is `merge_only_same_diff`, the SAME predicate the merge gate uses to decide
# whether Copilot's last review still covers the head (`copilot_currency`), so the lane requests a
# review exactly when the gate would otherwise hold the head for one.
#
# It compares the head against Copilot's newest LANDED review of the pull request — not merely the
# previous head — so a real change whose review never landed is not forgotten by a merge-only push
# on top of it. With no landed Copilot review at all (the review requested at open is still pending,
# or was refused) it compares against the previous head (`before` of the push).

#: The reviewer's GraphQL node id, read off `user.node_id` of its reviews (175728472 →
#: BOT_kgDOCnlnWA, measured on #7140, 2026-10-24). A live read from the pull request's own reviews
#: wins; this is the value when the pull request has none.
COPILOT_BOT_NODE_ID = "BOT_kgDOCnlnWA"
REQUEST_REVIEWS_MUTATION = (
    "mutation($pr: ID!, $bot: ID!) { requestReviews(input: {pullRequestId: $pr, botIds: [$bot], union: true}) "
    "{ pullRequest { number reviewRequests(first: 50) { nodes { requestedReviewer { __typename ... on Bot { id login } } } } } } }")


def is_copilot_account(user: dict | None) -> bool:
    """Copilot as a REQUESTED reviewer or a timeline actor: id 175728472, or the login GitHub prints for it
    there (`Copilot`, `copilot-pull-request-reviewer`, with or without `[bot]`)."""
    if not user:
        return False
    login = str(user.get("login") or "").removesuffix("[bot]")
    return user.get("id") == REVIEWER_ACCOUNT_ID or login in ("Copilot", "copilot-pull-request-reviewer")


def copilot_review_pending(timeline) -> bool:
    """True when the newest Copilot event on the pull request's timeline is a `review_requested` — a
    review is asked for and has not come back (no `reviewed`, no `review_request_removed` after it).
    Read off the REST timeline because `requested_reviewers` is not relied on to list a bot. Pure."""
    state = False
    def stamp(e):
        return (str(e.get("created_at") or e.get("submitted_at") or ""), e.get("id") or 0)
    for e in sorted((e for e in timeline or () if isinstance(e, dict)), key=stamp):
        kind = e.get("event")
        if kind == "review_requested" and is_copilot_account(e.get("requested_reviewer")):
            state = True
        elif kind == "review_request_removed" and is_copilot_account(e.get("requested_reviewer")):
            state = False
        elif kind == "reviewed" and is_copilot_account(e.get("user")):
            state = False
    return state


def review_request_action(pr: dict, reviews, carry: Carry | None, repo: str, pending: bool) -> tuple[str, str]:
    """('request' | 'none' | 'manual', why) for the pull request's CURRENT head. Pure; idempotent by
    construction — once requested, `pending` says so until the review lands, and once it lands the
    head is reviewed, so a re-delivered event or a second run requests nothing.

    `carry` is `merge_only_same_diff` from the reference head (Copilot's newest landed review, else
    the push's previous head) to the head, as a Carry; None when there is no reference to compare."""
    number, head = pr.get("number"), str((pr.get("head") or {}).get("sha") or "")
    if pr.get("state") not in (None, "open"):
        return "none", f"#{number} is {pr.get('state')}"
    if pr.get("draft"):
        return "none", (f"#{number} is a draft — its review is the ruleset's when it is marked ready for review "
                        "(`review_draft_pull_requests` decides drafts), never this lane's")
    author = pr.get("user") or {}
    if author.get("type") == "Bot":
        # Measured: Copilot's ruleset review does not reach an App's or Dependabot's own pull requests
        # (generated_only; the standing Dependabot waiver), so a request here would buy a refusal or nothing.
        # Their review condition is the merge gate's (generated_only, or a maintainer's waiver).
        return "none", (f"#{number} was opened by the App @{author.get('login')} — Copilot does not review an App's own pull "
                        "request; the merge gate decides its review condition (generated_only, or the waiver label)")
    head_repo = str(((pr.get("head") or {}).get("repo") or {}).get("full_name") or "")
    if head_repo and head_repo != repo:
        return "manual", (f"#{number} comes from the fork {head_repo}: a fork's token cannot request a review, so a maintainer "
                          f"requests the Copilot review for head {head[:10]} if its own diff changed")
    on_head = copilot_review_on(reviews, head)
    if on_head is not None:
        return "none", f"Copilot already reviewed head {head[:10]} (review {on_head.get('id')})"
    answered = [r for r in reviews or () if (r.get("user") or {}).get("id") == REVIEWER_ACCOUNT_ID
                and (r.get("user") or {}).get("type") == "Bot" and r.get("commit_id") == head and r.get("state") != "PENDING"]
    if answered:
        # A REFUSAL (or an empty body) on this very head is Copilot's answer to a request already made.
        # Re-requesting it on every sweep would loop on a quota refusal; a maintainer re-requests it.
        return "none", (f"Copilot already ANSWERED head {head[:10]} without a review (review {answered[-1].get('id')}: "
                        f"\"{first_line(answered[-1].get('body'))}\") — a maintainer re-requests it, never this lane")
    if pending:
        # 🚨 Not a permanent "nothing to do" (#7142 review): an in-flight review may land on the PREVIOUS
        # head. The sweep (`--request-review-sweep`, on the caller's schedule) re-judges the pull request
        # once it lands and requests again when that review does not cover the head.
        return "none", (f"a Copilot review is already requested and pending on #{number} — if it lands on an earlier head, "
                        "the scheduled sweep requests one for this head")
    if carry is not None and carry.ok:
        return "none", (f"ONLY THE DEFAULT BRANCH WAS MERGED IN — no review requested for head {head[:10]}: {carry.why}")
    why = carry.why if carry is not None else f"there is no earlier head of #{number} to compare head {head[:10]} against"
    return "request", f"head {head[:10]} changed the pull request's own diff — requesting a Copilot review: {why}"


def requested_copilot(response) -> bool:
    """True when the mutation's own response lists Copilot among the pull request's review requests —
    the positive success signal (the call returning is not one). Pure."""
    nodes = ((((((response or {}).get("data") or {}).get("requestReviews") or {}).get("pullRequest") or {})
              .get("reviewRequests") or {}).get("nodes")) or []
    return any(is_copilot_account((n or {}).get("requestedReviewer")) for n in nodes)


def pr_from_queue_ref(ref: str) -> int:
    m = QUEUE_REF.fullmatch(ref or "")
    if not m:
        raise ValueError(f"not a merge-queue ref: {ref!r} (expected gh-readonly-queue/<base>/pr-<N>-<40-hex sha>)")
    return int(m.group("pr"))


# ─────────────────────────────── GitHub adapter (REST only) ───────────────────────────────

class ReadError(RuntimeError):
    pass


# ─────────────────────────────── AN API OUTAGE IS NOT A VERDICT (MeshWeaver#7187) ───────────────────────────────
#
# 🚨 A call GitHub REFUSED for capacity — the installation's rate limit (403/429 "API rate limit
# exceeded for installation"), a secondary limit, a 5xx, a dropped connection — says NOTHING about the
# pull request. Until #7187 it raised the same ReadError as a malformed answer, and every lane turned
# it into its ordinary red: `Automatic review answered` read "cannot read the review … so it cannot say
# it was answered" on pull requests whose every thread WAS answered (core #7173, run 40187547005
# attempt 1, 11:07:30Z; #7182, run 40197206860, 13:21:22Z), the review-on-change lane never requested
# a review (Plugins #4195, 12:44Z) — and nothing re-evaluated any of them, because the self-refresh
# follows only a GREEN verdict. Worse, three readers CATCH ReadError and degrade (`read_diff_carry`,
# `read_carry`, `read_required_contexts`), so an outage in the middle of a read would have become a
# VERDICT ("the head owes a fresh review").
#
# So an outage is its own exception, deliberately NOT a ReadError — no reader that degrades on
# ReadError can swallow it. The adapter RETRIES it, waiting for the limit's own reset (read off
# `GET /rate_limit`, which does not count against the limit) or backing off, inside a bounded
# per-process budget (`MW_API_OUTAGE_WAIT_MINUTES`, default API_OUTAGE_WAIT_MINUTES). Past the budget
# it raises ApiOutage, and `main` reports it as INFRA (exit OUTAGE_EXIT, `outcome=api-outage` on
# $GITHUB_OUTPUT) — never as a verdict — for the lane's automatic later retry (review answered:
# node-repo-review-answered-retry.yml; the request and stage lanes: their scheduled sweeps).
# Doc/Architecture/ReviewFindingsAnswered → "An API outage is not a verdict".

class ApiOutage(Exception):
    """GitHub refused a call for capacity and this run's retry budget ran out. NOT a ReadError, on purpose."""

    def __init__(self, what: str, kind: str, retry_at: float | None):
        super().__init__(what)
        self.kind, self.retry_at = kind, retry_at


API_OUTAGE_WAIT_MINUTES = 5
OUTAGE_EXIT = 3
#: The step every review-answered lane job runs after its verdict step when the verdict step reported an
#: outage. The retry listener finds an outage by this exact step name (`outage_marked`), so the self-test
#: asserts the workflows carry it whenever they are beside this file.
OUTAGE_STEP_NAME = "API outage: no verdict was taken (retried automatically)"

_OUTAGE_SIGNATURES = (
    ("secondary-rate-limit", re.compile(r"secondary rate limit|abuse detection", re.IGNORECASE)),
    ("rate-limit", re.compile(r"API rate limit exceeded|HTTP 429", re.IGNORECASE)),
    ("server", re.compile(r"\bHTTP 5\d\d\b", re.IGNORECASE)),
    ("network", re.compile(r"connection reset by peer|connection refused|i/o timeout|TLS handshake timeout|"
                           r"timeout awaiting response headers|unexpected EOF", re.IGNORECASE)),
)


def classify_api_failure(text: str | None) -> str | None:
    """The outage kind a failed `gh api` call's output names, or None for a real answer (404, 422, a
    plain 403 permission refusal, a malformed request). Pure. A 403 is an outage ONLY when its text
    names a rate limit."""
    for kind, pattern in _OUTAGE_SIGNATURES:
        if pattern.search(text or ""):
            return kind
    return None


def outage_delay(kind: str, attempt: int, reset_epoch: float | None, now_epoch: float) -> float:
    """Seconds to wait before retry `attempt` (0-based) of an outage of `kind`. Pure.
    A primary limit waits for its own reset (+5 s). Without a readable reset, and for a secondary limit
    (GitHub: "wait at least one minute"), 60 s doubling to 300 s; a 5xx or a dropped connection 10 s
    doubling to 120 s."""
    if kind == "rate-limit" and reset_epoch is not None and reset_epoch > now_epoch:
        return reset_epoch - now_epoch + 5
    if kind in ("rate-limit", "secondary-rate-limit"):
        return float(min(60 * 2 ** attempt, 300))
    return float(min(10 * 2 ** attempt, 120))


def outage_budget_seconds() -> float:
    """`MW_API_OUTAGE_WAIT_MINUTES` (0–40), else API_OUTAGE_WAIT_MINUTES — in seconds."""
    raw = os.environ.get("MW_API_OUTAGE_WAIT_MINUTES", "")
    try:
        minutes = float(raw) if raw.strip() else API_OUTAGE_WAIT_MINUTES
    except ValueError:
        minutes = API_OUTAGE_WAIT_MINUTES
    return max(0.0, min(minutes, 40.0)) * 60


class Gh:
    """REST over `gh api`. `runner`, `sleep`, `clock` and `budget_s` are injectable so the self-test
    drives the outage path through a FAKE `gh` — nothing in a self-test reaches GitHub."""

    def __init__(self, repo: str, runner=None, sleep=None, clock=None, budget_s: float | None = None):
        self.repo = repo
        self._run = runner or (lambda cmd: subprocess.run(cmd, capture_output=True, text=True, check=False))
        self._sleep, self._clock = sleep or time.sleep, clock or time.time
        self.budget_s = outage_budget_seconds() if budget_s is None else budget_s
        self.waited_s = 0.0

    def sleep(self, seconds: float) -> None:
        self._sleep(seconds)

    def now(self) -> float:
        return self._clock()

    def rate_limit(self, resource: str = "core") -> tuple[int | None, float | None]:
        """(remaining, reset epoch) of a primary limit, or (None, None) when unreadable.
        `GET /rate_limit` does not count against the limit it reports."""
        p = self._run(["gh", "api", "rate_limit"])
        if p.returncode != 0:
            return None, None
        try:
            r = ((json.loads(p.stdout) or {}).get("resources") or {}).get(resource) or {}
            return int(r.get("remaining")), float(r.get("reset"))
        except (ValueError, TypeError, AttributeError):
            return None, None

    def _call(self, cmd: list, what: str, resource: str):
        attempt = 0
        while True:
            p = self._run(cmd)
            if p.returncode == 0:
                return p
            text = ((p.stderr or "") + "\n" + (p.stdout or "")).strip()
            kind = classify_api_failure(text)
            if kind is None:
                return p
            now = self._clock()
            reset = self.rate_limit(resource)[1] if kind == "rate-limit" else None
            delay = outage_delay(kind, attempt, reset, now)
            if self.waited_s + delay > self.budget_s:
                at = datetime.datetime.fromtimestamp(now + delay, datetime.UTC).strftime("%H:%M:%SZ")
                raise ApiOutage(f"{what}: GitHub refused it for capacity ({kind}) — {text[:300]} — after "
                                f"{int(self.waited_s)}s of this run's {int(self.budget_s)}s outage budget; "
                                f"the next attempt can succeed from ~{at}", kind, now + delay)
            print(f"  {what}: GitHub API outage ({kind}) — retrying in {int(delay)}s (attempt {attempt + 2}; "
                  f"{int(self.waited_s)}s of {int(self.budget_s)}s spent). Not a verdict.", flush=True)
            self._sleep(delay)
            self.waited_s += delay
            attempt += 1

    def api(self, path: str, paginate: bool = False):
        cmd = ["gh", "api", "-H", "Accept: application/vnd.github+json", f"repos/{self.repo}/{path}"]
        if paginate:
            cmd[2:2] = ["--paginate", "--slurp"]
        p = self._call(cmd, f"GET repos/{self.repo}/{path}", "core")
        if p.returncode != 0:
            raise ReadError(f"GET repos/{self.repo}/{path} failed ({p.returncode}): {(p.stderr or '').strip()[:600]}")
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
        p = self._call(["gh", "api", "-X", "POST", "-H", "Accept: application/vnd.github+json", f"repos/{self.repo}/{path}"],
                       f"POST repos/{self.repo}/{path}", "core")
        if p.returncode != 0:
            raise ReadError(f"POST repos/{self.repo}/{path} failed ({p.returncode}): {(p.stderr or p.stdout).strip()[:600]}")

    def write(self, method: str, path: str, body: dict):
        """A REST write with a JSON body (the verdict check-run — policy review-pending-while-answering).
        The body goes through a file (`--input`), never `-f`/`-F`, so no value is ever read as `@path`.
        A refusal is a ReadError naming it; a capacity refusal is an outage like any other."""
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as f:
            json.dump(body, f)
            path_in = f.name
        try:
            p = self._call(["gh", "api", "-X", method, "-H", "Accept: application/vnd.github+json",
                            f"repos/{self.repo}/{path}", "--input", path_in],
                           f"{method} repos/{self.repo}/{path}", "core")
        finally:
            with contextlib.suppress(OSError):
                os.remove(path_in)
        if p.returncode != 0:
            raise ReadError(f"{method} repos/{self.repo}/{path} failed ({p.returncode}): {(p.stderr or p.stdout).strip()[:600]}")
        try:
            return json.loads(p.stdout) if (p.stdout or "").strip() else {}
        except json.JSONDecodeError as e:
            raise ReadError(f"{method} repos/{self.repo}/{path} did not return JSON: {e}") from e

    def graphql(self, query: str, variables: dict) -> dict:
        """ONE GraphQL call — used for what REST cannot express (requesting a BOT reviewer), never in a
        loop. gh exits non-zero when the response carries `errors`; that, and an `errors` key on a
        zero exit, are both a ReadError. A capacity refusal is an outage like any other."""
        cmd = ["gh", "api", "graphql", "-f", f"query={query}"]
        for k, v in variables.items():
            cmd += ["-f", f"{k}={v}"]
        p = self._call(cmd, "GraphQL call", "graphql")
        if p.returncode != 0:
            raise ReadError(f"GraphQL call failed ({p.returncode}): {(p.stderr or p.stdout).strip()[:600]}")
        try:
            data = json.loads(p.stdout)
        except json.JSONDecodeError as e:
            raise ReadError(f"GraphQL call did not return JSON: {e}") from e
        if not isinstance(data, dict) or data.get("errors"):
            raise ReadError(f"GraphQL call answered errors: {str((data or {}).get('errors'))[:600]}")
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
    return (pr, reviews, comments, Waiver(present, events, roles), author_role, check_runs, files, commits,
            read_copilot_carry(gh, pr, reviews, as_of))


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


def base_tip(gh: Gh, pr: dict) -> str:
    """The pull request's base branch's CURRENT tip sha; a failed or shapeless read raises."""
    base_ref = str((pr.get("base") or {}).get("ref") or "")
    if not base_ref:
        raise ReadError("the pull request names no base branch")
    branch = gh.api(f"branches/{urllib.parse.quote(base_ref, safe='')}")
    base_sha = str(((branch or {}).get("commit") or {}).get("sha") or "")
    if not re.fullmatch(r"[0-9a-f]{40}", base_sha):
        raise ReadError(f"branches/{base_ref} reported no tip sha")
    return base_sha


def read_diff_carry(gh: Gh, pr: dict, old: str, review: dict) -> Carry:
    """`copilot_carry_over` from `old` (a reviewed — or, for the review-on-change lane, the previous —
    head) to the pull request's head, every input read through REST: the base tip, compare(base...head)
    and compare(base...old). A failed read is a Carry that does NOT hold, never an exception: an
    unprovable "only main was merged" is a real change (policy review-on-real-change fails closed)."""
    head = str((pr.get("head") or {}).get("sha") or "")
    try:
        base_sha = base_tip(gh, pr)
        return copilot_carry_over(head, read_compare(gh, base_sha, head), read_compare(gh, base_sha, old),
                                  dict(review, commit_id=old))
    except (ReadError, KeyError, TypeError, AttributeError) as e:
        return Carry(False, f"whether only the default branch was merged in since {old[:10]} could not be read ({e})", old, review)


def read_copilot_carry(gh: Gh, pr: dict, reviews, as_of: str | None = None) -> Carry | None:
    """The merge gate's input for policy review-on-real-change: None when Copilot reviewed the head
    itself or has not landed a review at all (nothing to carry); otherwise whether its newest landed
    review covers the head (`read_diff_carry`). `--as-of` replays compare against the base branch as
    it is NOW — the commits and diffs are immutable, only the merge base may differ."""
    head = str((pr.get("head") or {}).get("sha") or "")
    if copilot_review_on(reviews, head, as_of) is not None:
        return None
    latest = newest_landed_copilot_review(reviews, as_of)
    if latest is None:
        return None
    return read_diff_carry(gh, pr, str(latest.get("commit_id") or ""), latest)


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
        base_sha = base_tip(gh, pr)
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
    except ApiOutage as e:
        # Not ready is still the answer (never armed on a guess) — but named as an OUTAGE, not a review
        # state, so a reader does not go looking for an unanswered thread (#7187).
        verdict = ArmVerdict(False, f"API outage, not a review state — #{number} is not armed on a guess and is judged again on the next pass: {e}")
        print(f"::warning title=API outage — no verdict was taken::{verdict.missing}")
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
        if not post_rerun(gh, run["id"], RERUN_FAILED_JOBS):
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


#: How long the refresh lets an in-flight read run finish (`refresh_action` → "wait"). The read run
#: started BEFORE this green verdict, whose review had landed, so it is past its review wait within one
#: POLL_SECONDS; what remains is at most the settle wait (SETTLE_ROUNDS minutes), an outage budget and
#: its lane job. 30 minutes covers that inside the refresh job's 45-minute cap.
REFRESH_WAIT_SECONDS = 30 * 60
#: How many EARLIER attempts the refresh reads back past attempts that created no job (a re-run replaced
#: while pending) to find the attempt whose check-run still stands.
MAX_ATTEMPT_LOOKBACK = 5
REFRESH_POLL_SECONDS = 30


def run_refresh(repo: str, number: int, event: str, run_id: str, evaluated_at: str, gh=None,
                wait_s: float = REFRESH_WAIT_SECONDS) -> int:
    """The self-refresh job: exit 0 when it re-ran what carried a stale red or had nothing to do (every
    decision printed), 1 (RED, named) when it could not read, could not re-run, or could not judge.
    It never changes a verdict itself. Two halves, decided off ONE listing of this workflow's runs on
    the head per poll:
      * `refresh_action` — the newest `pull_request` run (#4649; a zero-job one too, #6315);
      * `duplicate_red_action` — EVERY other run of this workflow on the head, whatever its event, whose
        review check-run is red and older than this verdict (MeshWeaver.Plugins#4276).
    `gh` is the REST adapter (`Gh(repo)` when omitted); the self-test injects a stub so the POSTs this
    caller actually issues — the whole-run `rerun`, never `rerun-failed-jobs` — are asserted end to end.
    Anything still in flight is waited for (up to `wait_s`), then judged — never skipped (#7187)."""
    sweep: list = []
    candidate = None
    try:
        gh = gh if gh is not None else Gh(repo)
        sleep, clock = getattr(gh, "sleep", time.sleep), getattr(gh, "now", time.monotonic)
        own = gh.api(f"actions/runs/{run_id}")
        workflow_id = own.get("workflow_id") if isinstance(own, dict) else None
        if not workflow_id:
            raise ReadError(f"actions/runs/{run_id} named no workflow_id")
        own_created = str(own.get("created_at") or "")
        pr = gh.api(f"pulls/{number}")
        if not isinstance(pr, dict) or pr.get("number") != number:
            raise ReadError(f"pulls/{number} did not return pull request #{number}")
        head = str((pr.get("head") or {}).get("sha") or "")
        if not re.fullmatch(r"[0-9a-f]{40}", head):
            raise ReadError(f"pulls/{number} reported no head sha")
        # A completed attempt's jobs never change, so they are read once per (run, attempt).
        jobs_cache: dict = {}

        def review_jobs(run: dict):
            if run.get("status") in NOT_STARTED_RUN_STATUSES or (
                    run.get("status") == "completed" and run.get("conclusion") == "success"):
                return []
            key = (run.get("id"), run.get("run_attempt"))
            if key in jobs_cache:
                return jobs_cache[key]
            listing = gh.api(f"actions/runs/{run.get('id')}/jobs?per_page=100")
            jobs = listing.get("jobs") if isinstance(listing, dict) else None
            if not isinstance(jobs, list):
                raise ReadError(f"actions/runs/{run.get('id')}/jobs did not return a job listing")
            if run.get("status") == "completed" and not jobs:
                # 🚨 A RE-RUN replaced while pending (#7257 review): the latest attempt was cancelled with
                # ZERO jobs, so it published no check-run — and the EARLIER attempt's red check-run still
                # stands on the head. Judge the newest attempt that did create jobs.
                attempt = int(run.get("run_attempt") or 1)
                for k in range(attempt - 1, max(0, attempt - 1 - MAX_ATTEMPT_LOOKBACK), -1):
                    earlier = gh.api(f"actions/runs/{run.get('id')}/attempts/{k}/jobs?per_page=100")
                    jobs = earlier.get("jobs") if isinstance(earlier, dict) else None
                    if not isinstance(jobs, list):
                        raise ReadError(f"actions/runs/{run.get('id')}/attempts/{k}/jobs did not return a job listing")
                    if jobs:
                        break
            if run.get("status") == "completed":
                jobs_cache[key] = jobs
            return jobs

        def judge(pr_now: dict, runs: list) -> tuple:
            pr_runs = [r for r in runs if r.get("event") == PROTECTION_READ_EVENT]
            cand = max(pr_runs, key=lambda r: (r.get("created_at") or "", r.get("id") or 0)) if pr_runs else None
            act, reason = refresh_action(event, run_id, evaluated_at, repo, pr_now, cand)
            if act == "wait" and cand is not None:
                # The SAME acyclic wait policy as the sweep's — never a wait on a newer run's refresh.
                act, reason = in_flight_policy(cand, review_jobs(cand), own_created) or (act, reason)
            head_now = str((pr_now.get("head") or {}).get("sha") or "")
            decisions = []
            if sweep_precondition(event, repo, pr_now) is None:
                # The newest `pull_request` run is refresh_action's — unless this run IS a
                # `pull_request` one, when refresh_action stands aside and the sweep covers it too.
                skip = {str(run_id)}
                if cand is not None and event != PROTECTION_READ_EVENT:
                    skip.add(str(cand.get("id")))
                for r in runs:
                    if str(r.get("id")) in skip:
                        continue
                    decisions.append((r,) + duplicate_red_action(r, review_jobs(r), run_id, own_created,
                                                                 evaluated_at, head_now))
            return cand, act, reason, decisions

        deadline = clock() + wait_s
        while True:
            listing = gh.api(f"actions/workflows/{workflow_id}/runs?head_sha={head}&per_page=100")
            runs = listing.get("workflow_runs") if isinstance(listing, dict) else None
            if not isinstance(runs, list):
                raise ReadError(f"actions/workflows/{workflow_id}/runs?head_sha={head[:10]} did not return a run listing")
            total = listing.get("total_count")
            if isinstance(total, int) and total > len(runs):
                # Never judge a partial listing: an unread page could hold the red that blocks.
                raise ReadError(f"the run listing for head {head[:10]} is truncated ({len(runs)} of {total})")
            candidate, action, why, sweep = judge(pr, runs)
            waits = ([why] if action == "wait" else []) + [w for _, a, w in sweep if a == "wait"]
            if not waits:
                break
            if clock() + REFRESH_POLL_SECONDS > deadline:
                late = (f" — and it did not finish within {int(wait_s)}s, so whether it carries a red older than "
                        "this verdict could not be judged; re-run this run once it has finished")
                if action == "wait":
                    action, why = "fail", why + late
                sweep = [(r, "fail", w + late) if a == "wait" else (r, a, w) for r, a, w in sweep]
                break
            for w in waits:
                print(f"  #{number}: {w}", flush=True)
            sleep(REFRESH_POLL_SECONDS)
        if action == "rerun" or any(a == "rerun" for _, a, _ in sweep):
            # 🚨 Re-read the pull request and judge again immediately before the POST (MeshWeaver#7203
            # review): the snapshot above can be 30 minutes old, and a push or a closure since then
            # makes a red run an OBSOLETE evaluation — re-running it would queue it in the pull
            # request's group, where it can displace the current head's pending run.
            pr = gh.api(f"pulls/{number}")
            if not isinstance(pr, dict) or pr.get("number") != number:
                raise ReadError(f"pulls/{number} did not return pull request #{number}")
            if action == "rerun":
                action, why = refresh_action(event, run_id, evaluated_at, repo, pr, candidate)
            head_now = str((pr.get("head") or {}).get("sha") or "")
            gone = sweep_precondition(event, repo, pr)
            rejudged = []
            for r, a, w in sweep:
                if a == "rerun" and gone:
                    rejudged.append((r, "none", gone))
                elif a == "rerun":
                    rejudged.append((r,) + duplicate_red_action(r, jobs_cache.get((r.get("id"), r.get("run_attempt"))),
                                                                run_id, own_created, evaluated_at, head_now))
                else:
                    rejudged.append((r, a, w))
            sweep = rejudged
        # At most ONE re-run per concurrency group (plan_reruns): the rest chain through its GREEN refresh.
        selected = [(candidate, why)] if action == "rerun" else []
        reds = sorted((i for i, (_, a, _) in enumerate(sweep) if a == "rerun"),
                      key=lambda i: (sweep[i][0].get("run_started_at") or "", sweep[i][0].get("id") or 0))
        selected += [(sweep[i][0], sweep[i][2]) for i in reds]
        plan = plan_reruns(selected, runs, run_id)
        if action == "rerun":
            _, action, why = plan.pop(0)
        for i, planned in zip(reds, plan):
            sweep[i] = planned
        if action == "rerun" and not post_rerun(gh, candidate["id"], RERUN_WHOLE_RUN):
            action, why = "none", f"run {candidate['id']} was already re-run by another invocation"
        for i, (r, a, w) in enumerate(sweep):
            if a == "rerun" and not post_rerun(gh, r["id"], RERUN_WHOLE_RUN):
                sweep[i] = (r, "none", f"run {r['id']} was already re-run by another invocation")
    except (ReadError, KeyError, TypeError) as e:
        print(f"::error::the protection-read run of #{number} was NOT refreshed: {e}")
        return 1
    rc = 0
    rows = [("the newest pull_request run", action, why, (candidate or {}).get("html_url"))]
    rows += [(f"run {r.get('id')}", a, w, r.get("html_url")) for r, a, w in sweep]
    for label, a, w, url in rows:
        if a == "rerun":
            print(f"::notice::#{number}: {w} — {url}")
        elif a == "manual":
            print(f"::warning::#{number}: {w}")
        elif a == "fail":
            print(f"::error::#{number}: {w}")
            rc = 1
        else:
            print(f"  #{number}: {w}")
        _append("GITHUB_STEP_SUMMARY", f"Refresh for #{number} ({label}): {a} — {w}\n")
    return rc


# ─────────────────────────────── THE OUTAGE RETRY (MeshWeaver#7187) ───────────────────────────────
#
# A run of `Review answered` whose verdict step reported an API outage (OUTAGE_STEP_NAME failed) took NO
# verdict, and nothing else re-evaluates it: the refresh follows only a GREEN verdict, and a person was
# the only remedy (#7173, #7180, #7178 re-run by hand at 11:47Z on 2026-10-25). The retry listener
# (node-repo-review-answered-retry.yml, on `workflow_run: completed`) re-runs exactly such a run once
# the limit has reset. It is bounded: an outage that survives MAX_OUTAGE_ATTEMPTS attempts is RED here,
# named, and a run whose red was a VERDICT is never touched.

MAX_OUTAGE_ATTEMPTS = 3
#: Remaining primary-limit calls below which the retry waits for the reset before re-running: one
#: evaluation costs ~10 calls per job, two jobs, plus the refresh.
RETRY_MIN_REMAINING = 50
RETRY_WAIT_SECONDS = 35 * 60


def outage_marked(jobs) -> list:
    """The names of the jobs whose OUTAGE_STEP_NAME step FAILED — the marker that the job's red is an
    API outage and not a verdict. Pure. A job red for any other step is not listed."""
    return [j.get("name") for j in jobs or () if isinstance(j, dict)
            and any(isinstance(s, dict) and s.get("name") == OUTAGE_STEP_NAME and s.get("conclusion") == "failure"
                    for s in j.get("steps") or ())]


def outage_streak(marked_by_attempt) -> int:
    """How many attempts IN A ROW, newest first, met an outage. Pure. `marked_by_attempt` is one boolean
    per attempt, newest FIRST (`bool(outage_marked(jobs))`). Only outage-marked attempts count: a verdict
    red or a refresh re-run before them consumes none of the retry allowance (MeshWeaver#7203 review —
    `run_attempt` alone counted those too, so a FIRST outage on attempt 3 was never retried)."""
    n = 0
    for marked in marked_by_attempt:
        if not marked:
            break
        n += 1
    return n


def self_contradicting(jobs) -> bool:
    """True when one attempt's review jobs DISAGREE — one red, one green, all completed. Pure. Only core
    runs two (`Automatic review answered` and `lane / …`), each judging live state at its own instant.
    The green one's refresh cannot re-run its own run, and an older run's refresh does not wait on it
    (`in_flight_policy`), so without this its red stands on the head (#7257 review). Re-running it
    judges live state again; the outage bound limits how often."""
    review = [j for j in jobs or () if isinstance(j, dict) and is_review_check_job(j.get("name"))]
    if len(review) < 2 or any(j.get("status") not in (None, "completed") for j in review):
        return False
    passing = [j.get("conclusion") in PASSING_CONCLUSIONS for j in review]
    return any(passing) and not all(passing)


def retry_outage_action(run: dict, attempt: int, jobs, pr: dict | None, streak: int | None = None) -> tuple[str, str]:
    """('rerun' | 'none' | 'manual' | 'fail', why) for a completed run of `Review answered`. Pure.
    `streak` is `outage_streak` over this run's attempts ending at `attempt` (1 when omitted — this
    attempt alone); the bound MAX_OUTAGE_ATTEMPTS applies to it, never to `attempt`. Besides an
    outage, a SELF-CONTRADICTING attempt (`self_contradicting`) is retried under the same bound."""
    rid = run.get("id")
    if run.get("conclusion") != "failure":
        return "none", f"run {rid} concluded {run.get('conclusion')} — only a failed run can carry an outage"
    marked = outage_marked(jobs)
    if not marked and self_contradicting(jobs):
        marked = ["its review jobs DISAGREE (one red, one green) — a verdict its own refresh cannot re-take"]
    if not marked:
        return "none", f"run {rid} attempt {attempt} is red on a VERDICT (no `{OUTAGE_STEP_NAME}` step failed) — never retried"
    if run.get("status") != "completed" or int(run.get("run_attempt") or attempt) != attempt:
        return "none", f"run {rid} has been re-run since attempt {attempt} (now attempt {run.get('run_attempt')}, {run.get('status')})"
    if pr is None:
        return "manual", (f"run {rid} names no pull request of this repository (a fork's run lists none) — a maintainer "
                          f"re-runs it once the API answers")
    head = str((pr.get("head") or {}).get("sha") or "")
    if pr.get("state") != "open":
        return "none", f"#{pr.get('number')} is {pr.get('state')}"
    if run.get("head_sha") != head:
        return "none", f"run {rid} judged {str(run.get('head_sha'))[:10]}, the head is now {head[:10]} — a run of its own judges it"
    streak = max(1, streak or 1)
    if streak >= MAX_OUTAGE_ATTEMPTS:
        return "fail", (f"run {rid} met an API outage on {streak} attempts in a row, up to attempt {attempt} "
                        f"({', '.join(marked)}) — the "
                        "automatic retry stops here; re-run it by hand once the API answers")
    return "rerun", f"run {rid} attempt {attempt} took no verdict (API outage in {', '.join(marked)}) — re-running the whole run"


def wait_for_capacity(gh, deadline: float) -> None:
    """Sleep until the primary limit has RETRY_MIN_REMAINING calls left, its reset has passed, or
    `deadline`. Reads only `GET /rate_limit`, which does not count against the limit it reports."""
    while True:
        remaining, reset = gh.rate_limit("core")
        if remaining is None or remaining >= RETRY_MIN_REMAINING or reset is None:
            return
        pause = min(reset + 10, deadline) - gh.now()
        if pause <= 0:
            return
        print(f"  {remaining} calls left until the limit resets at "
              f"{datetime.datetime.fromtimestamp(reset, datetime.UTC):%H:%M:%SZ} — waiting {int(pause)}s before reading",
              flush=True)
        gh.sleep(pause)


def run_retry_outage(repo: str, run_id: str, attempt: int, gh=None, wait_s: float = RETRY_WAIT_SECONDS) -> int:
    """The retry listener: exit 0 when every outage run it judged was re-run or needed nothing (each
    printed), 1 when it could not read, could not re-run, or an outage outlasted MAX_OUTAGE_ATTEMPTS
    (named).

    🚨 ORDER (MeshWeaver#7203 review). It FIRST waits for capacity — through the free `GET /rate_limit`
    only — and only THEN makes its metered reads, decides, and posts at once. Reading first failed both
    ways: with the limit spent the very first read raised ApiOutage under the five-minute budget, so the
    listener died and nothing ever re-ran the run; and a decision read BEFORE a wait of up to 35 minutes
    re-ran a run whose pull request had since closed, moved its head or been re-run by a newer attempt.

    🚨 SCOPE: EVERY outage-failed run of this check on the head, from ANY event — not only the run that
    woke it. Branch protection keeps reading a FAILED check-run of a `pull_request_review` or
    `pull_request_review_comment` run that met the limit, and a newer success from another run does not
    clear it (measured on two plugin-repository pull requests: reds taken 12:48–12:52Z still blocked the
    merge after successes at 13:53Z and 14:27Z). So the listener lists this workflow's FAILED runs for
    the pull request's current head and applies `retry_outage_action` — the same per-run outage streak
    bound and the same fresh read — to each. A listener that died, or a run whose listener never ran,
    is recovered by the next failure on that head."""
    try:
        gh = gh if gh is not None else Gh(repo)
        wait_for_capacity(gh, gh.now() + wait_s)
        run = gh.api(f"actions/runs/{run_id}")
        if not isinstance(run, dict) or str(run.get("id")) != str(run_id):
            raise ReadError(f"actions/runs/{run_id} did not return that run")

        def attempt_jobs(rid, n: int) -> list:
            listing = gh.api(f"actions/runs/{rid}/attempts/{n}/jobs?per_page=100")
            jobs_ = listing.get("jobs") if isinstance(listing, dict) else None
            if not isinstance(jobs_, list):
                raise ReadError(f"actions/runs/{rid}/attempts/{n}/jobs did not return a job listing")
            return jobs_

        def judge(run_: dict, n: int, pr_: dict | None) -> tuple[str, str]:
            rid = run_.get("id")
            jobs = attempt_jobs(rid, n)
            streak = 1
            def retryable(jobs_) -> bool:
                return bool(outage_marked(jobs_)) or self_contradicting(jobs_)
            if retryable(jobs):
                # The earlier attempts, newest first, only as far as the bound needs and only while
                # they were retryable too: a verdict attempt ends the streak.
                marks = [True]
                for k in range(n - 1, max(0, n - MAX_OUTAGE_ATTEMPTS), -1):
                    marks.append(retryable(attempt_jobs(rid, k)))
                    if not marks[-1]:
                        break
                streak = outage_streak(marks)
            action_, why_ = retry_outage_action(run_, n, jobs, pr_, streak)
            if action_ == "rerun" and not post_rerun(gh, rid, RERUN_WHOLE_RUN):
                action_, why_ = "none", f"run {rid} was already re-run by another invocation"
            return action_, why_

        numbers = [p.get("number") for p in run.get("pull_requests") or () if isinstance(p, dict) and p.get("number")]
        pr = None
        if numbers:
            pr = gh.api(f"pulls/{numbers[0]}")
            if not isinstance(pr, dict) or pr.get("number") != numbers[0]:
                raise ReadError(f"pulls/{numbers[0]} did not return pull request #{numbers[0]}")
        results = [(run_id, attempt) + judge(run, attempt, pr)]
        head = str(((pr or {}).get("head") or {}).get("sha") or "")
        workflow_id = run.get("workflow_id")
        if pr is not None and pr.get("state") == "open" and re.fullmatch(r"[0-9a-f]{40}", head) and workflow_id:
            listing = gh.api(f"actions/workflows/{workflow_id}/runs?head_sha={head}&status=failure&per_page=100")
            others = listing.get("workflow_runs") if isinstance(listing, dict) else None
            if not isinstance(others, list):
                raise ReadError(f"actions/workflows/{workflow_id}/runs?head_sha={head[:10]} did not return a run listing")
            for other in sorted(others, key=lambda r: (r.get("created_at") or "", r.get("id") or 0), reverse=True):
                if not isinstance(other, dict) or str(other.get("id")) == str(run_id) or other.get("head_sha") != head:
                    continue
                if other.get("status") != "completed" or other.get("conclusion") != "failure":
                    continue
                n = int(other.get("run_attempt") or 1)
                results.append((other.get("id"), n) + judge(other, n, pr))
    except ApiOutage as e:
        print(f"::error::the outage retry of run {run_id} could not read even after waiting up to {int(wait_s)}s "
              f"for the limit to reset: {e} — re-run run {run_id} by hand once the API answers")
        return 1
    except (ReadError, KeyError, TypeError, ValueError) as e:
        print(f"::error::the outage retry of run {run_id} could not complete: {e}")
        return 1
    failed = False
    for rid, n, action, why in results:
        if action == "fail":
            failed = True
            print(f"::error::{why}")
        else:
            print(f"::notice::{why}" if action == "rerun" else (f"::warning::{why}" if action == "manual" else f"  {why}"))
        _append("GITHUB_STEP_SUMMARY", f"Outage retry of run {rid} attempt {n}: {action} — {why}\n")
    return 1 if failed else 0


def run_request_review(repo: str, number: int, before: str | None, gh=None, sweep: bool = False) -> int:
    """The review-on-change lane (policy review-on-real-change). Exit 0 when it requested a review and
    GitHub's answer lists Copilot as requested, or had nothing to do (both printed, with why); 1 (RED,
    named) when a read, the request or its confirmation failed. `gh` defaults to `Gh(repo)`; the
    self-test injects a stub and asserts the ONE mutation it issues."""
    try:
        gh = gh if gh is not None else Gh(repo)
        pr = gh.api(f"pulls/{number}")
        if not isinstance(pr, dict) or pr.get("number") != number:
            raise ReadError(f"pulls/{number} did not return pull request #{number}")
        head = str((pr.get("head") or {}).get("sha") or "")
        if not re.fullmatch(r"[0-9a-f]{40}", head):
            raise ReadError(f"pulls/{number} reported no head sha")
        reviews = read_reviews(gh, number)
        timeline = gh.api(f"issues/{number}/timeline?per_page=100", paginate=True)
        if not isinstance(timeline, list):
            raise ReadError(f"issues/{number}/timeline did not return a listing")
        before = before if before and re.fullmatch(r"[0-9a-f]{40}", before) and before != head else ""
        latest = newest_landed_copilot_review(reviews)
        if sweep and (latest is None or copilot_review_on(reviews, head) is not None):
            # The sweep exists for ONE state (#7142 review): a review requested for this head landed on an
            # EARLIER one. No landed review at all is the opening review's business (the ruleset's), and a
            # reviewed head needs nothing — so the sweep never re-asks those, every 15 minutes, forever.
            print(f"  #{number}: {'no Copilot review has landed yet' if latest is None else 'the head is reviewed'} — nothing for the sweep")
            return 0
        if latest is not None and copilot_review_on(reviews, head) is None:
            reference, ref_kind = str(latest.get("commit_id") or ""), f"Copilot's newest review ({latest.get('id')})"
        elif latest is None and before:
            reference, ref_kind = before, "the push's previous head (no Copilot review has landed yet)"
        else:
            reference, ref_kind = "", ""
        carry = read_diff_carry(gh, pr, reference, latest or {}) if reference else None
        if reference:
            print(f"  compared head {head[:10]} against {ref_kind}: {reference[:10]}")
        if before and before != reference:
            # Logged, not decided on: this push's own before -> after reading, for the reader.
            print(f"  this push: {before[:10]} -> {head[:10]}: {read_diff_carry(gh, pr, before, {}).why}")
        action, why = review_request_action(pr, reviews, carry, repo, copilot_review_pending(timeline))
        if action == "request":
            now = str(((gh.api(f"pulls/{number}") or {}).get("head") or {}).get("sha") or "")
            if now != head:
                action, why = "none", (f"the head moved from {head[:10]} to {now[:10]} while this run read it — the run "
                                       "for that push judges it")
        if action == "request":
            node = str(pr.get("node_id") or "")
            if not node:
                raise ReadError(f"pulls/{number} reported no node_id, so the review cannot be requested")
            bot = next((str((r.get("user") or {}).get("node_id")) for r in reviews
                        if (r.get("user") or {}).get("id") == REVIEWER_ACCOUNT_ID and (r.get("user") or {}).get("node_id")),
                       COPILOT_BOT_NODE_ID)
            response = gh.graphql(REQUEST_REVIEWS_MUTATION, {"pr": node, "bot": bot})
            if not requested_copilot(response):
                raise ReadError(f"requestReviews for #{number} returned, but its answer lists no Copilot review request "
                                f"(bot {bot}) — the review was NOT requested: {json.dumps(response)[:400]}")
    except (ReadError, KeyError, TypeError) as e:
        print(f"::error::the Copilot review of #{number} was NOT requested (policy review-on-real-change): {e}")
        return 1
    if action == "request":
        print(f"::notice::#{number}: {why} — requested; GitHub's answer lists Copilot as requested")
    elif action == "manual":
        print(f"::warning::#{number}: {why}")
    else:
        print(f"  #{number}: {why}")
    _append("GITHUB_STEP_SUMMARY", f"Review on real change — #{number}: {action} — {why}\n")
    return 0


#: At most this many pull requests are judged per sweep run (each costs ~5 REST reads, two compares more).
SWEEP_MAX_PRS = 40


def sweep_window(numbers: list, now_epoch: int, size: int = SWEEP_MAX_PRS, period_s: int = 900) -> list:
    """The slice of `numbers` (sorted) this sweep run judges. Pure. At most `size`; with more open
    pull requests than that, the window ROTATES with the 15-minute schedule slot, so every open pull
    request is judged within ceil(n / size) runs — a fixed "newest 40" never reconsidered the oldest
    (#7142 review)."""
    if len(numbers) <= size:
        return list(numbers)
    windows = -(-len(numbers) // size)
    start = (now_epoch // period_s) % windows * size
    return list(numbers[start:start + size])


def run_request_review_sweep(repo: str, gh=None, now_epoch: int | None = None) -> int:
    """The scheduled half of the review-on-change lane (#7142 review). A `synchronize` that finds a
    Copilot review already PENDING requests nothing — and that pending review may then land on the
    PREVIOUS head, leaving the current one stale with no further event: Copilot's own review event
    starts no run here (`waiting_would_help`). So the caller's `schedule` re-judges every open,
    non-draft pull request with `run_request_review(..., sweep=True)`, which acts only when a landed
    review does not cover the head and nothing is pending. Exit 1 when the listing or any one
    pull request's judgement failed (every failure named); 0 otherwise."""
    gh = gh if gh is not None else Gh(repo)
    try:
        pulls = gh.api("pulls?state=open&per_page=100", paginate=True)
        if not isinstance(pulls, list):
            raise ReadError("pulls?state=open did not return a listing")
    except ReadError as e:
        print(f"::error::the review-on-change sweep cannot list open pull requests: {e}")
        return 1
    numbers = sorted(p["number"] for p in pulls if isinstance(p, dict) and not p.get("draft") and p.get("number"))
    if len(numbers) > SWEEP_MAX_PRS:
        window = sweep_window(numbers, int(time.time()) if now_epoch is None else now_epoch)
        print(f"::notice::{len(numbers)} open pull requests — this sweep judges {len(window)} ({window[0]}..{window[-1]}); "
              f"the window rotates every run, so each is judged within {-(-len(numbers) // SWEEP_MAX_PRS)} runs")
        numbers = window
    print(f"sweep: judging {len(numbers)} open non-draft pull request(s): {numbers}")
    failed = [n for n in numbers if run_request_review(repo, n, None, gh=gh, sweep=True) != 0]
    if failed:
        print(f"::error::the review-on-change sweep could not judge {failed}")
    return 1 if failed else 0


# ─────────────────────────── PENDING WHILE ANSWERING (policy review-pending-while-answering) ───────────────────────────
#
# 🚨 WHY. Measured on 2026-10-30: about 60% of that day's red pull requests were `Automatic review answered`
# reading RED while Copilot's threads simply had not been answered YET — a state that is not a failure, that
# nothing but a person's reply can change, and that a red X sends everybody to investigate. So the required
# context now has THREE readings instead of two, and only a real failure is red:
#
#   success   — every reviewer thread answered and a review covers the head (the verdict is GREEN);
#   pending   — the verdict is RED only because threads are not answered yet, or the head's review has not
#               landed yet, and neither has outlived its deadline (policy review-answer-deadline): the
#               check-run stays `in_progress`, its title says "N of M threads unanswered" and when it turns
#               red, its summary names every unanswered thread;
#   failure   — a thread unanswered past ANSWER_DEADLINE_HOURS after the review that opened it, a head that
#               owes its review past REVIEW_DEADLINE_HOURS after it was first judged, a refusal (nothing that
#               arrives can change it), or a state the gate could not MEASURE (an incomplete listing, a failed
#               read) — fail closed.
#
# 🚨 IT STILL BLOCKS, EXACTLY AS BEFORE. A required context that is `in_progress` is not satisfied: branch
# protection and rulesets accept only success / neutral / skipped, and auto-merge waits for a pending
# required check. `pending` is never a skipped or neutral conclusion — it is a check-run with NO conclusion.
#
# 🚨 HOW A WORKFLOW CAN SAY "PENDING". A job's own check-run always ends with a conclusion when the job
# ends (and every job is hard-cut at 45 minutes), so the verdict is written through the Checks API instead:
# ONE check-run per head, named exactly as the required context, created by the first evaluation and
# PATCHed by every later one (`write_verdict_check_run`). The job that writes it is RENAMED
# (VERDICT_WRITER_JOB) so its own check-run never carries the required name. Every evaluation now updates
# the SAME check-run, whichever event started it — which is also what the #4649 duplicate problem (one
# check-run per run, and branch protection reading an older red) needed. Ordering: the check-run's
# `external_id` carries when the writing evaluation READ the pull request; an evaluation whose read is
# older than the one on file writes nothing (`verdict_write_plan`).
#
# 🚨 WHAT RE-EVALUATES A PENDING VERDICT WITH NO EVENT. Copilot's own review event cannot start a run here
# (`waiting_would_help`), and a deadline passes without any event at all — so the caller's `schedule` runs
# `--verdict-sweep`, which re-judges every open pull request whose verdict check-run is pending or
# unmeasured (and nothing else). That is also why the writer takes NO review wait and NO settle wait: a
# pending verdict is not red, so there is nothing to wait out, and the sweep flips it within 15 minutes.
#
# 🚨 WHERE IT DOES NOT APPLY — the job keeps its old name and its old red/green: a FORK pull request (its
# token is read-only, so it could write no verdict at all), a Dependabot pull request (its token is
# read-only unless granted), a merge-queue entry (a queue commit is judged once, at admission), and every
# caller of the fleet lane that has not opted in (`verdict-check-run` input). In those shapes nothing changed.

VERDICT_EXTERNAL_PREFIX = "mw-review-answered:v1:"
#: The job name the verdict WRITER runs under, so its own check-run never carries the required name.
VERDICT_WRITER_JOB = "Review answered — verdict writer"
#: The App every GITHUB_TOKEN-written check-run belongs to (GitHub Actions, app id 15368 — the id
#: MeshWeaver.Plugins' classic protection pins its required review context to).
ACTIONS_APP_SLUG = "github-actions"
#: Policy review-answer-deadline. A thread unanswered this long after the review that opened it is RED.
ANSWER_DEADLINE_HOURS = 24
#: Policy review-answer-deadline. A head whose review has not landed this long after the gate first judged
#: it is RED (Copilot measured median 4.1 min, max 17.2 min; the 2026-09-18 quota refusal lasted 4 h+).
REVIEW_DEADLINE_HOURS = 6
#: The verdict kinds the sweep re-judges: a pending one (a review or a deadline can arrive with no
#: event) and an unmeasured one (the next read can complete). Every other kind changes only on an event.
#: `review-overdue` and `refused` are swept too (#7653 review): a late review or a maintainer's re-request
#: clears them and raises no event here, so without the sweep their red would outlive its cause.
SWEEP_KINDS = frozenset({"answering", "awaiting-review", "unmeasurable", "review-overdue", "refused"})
#: run_verdict's exit when the verdict could NOT be published (a failed write, or a failed read whose failure
#: could not be written either) — distinct from 1, a red verdict that WAS published (#7653 review).
VERDICT_UNPUBLISHED_EXIT = 4
#: The kind of a completed verdict check-run GitHub would not re-open, closed as `failure` so it can never
#: stand as an old success beside a newer pending one.
SUPERSEDED_KIND = "superseded"
CHECK_RUN_SUMMARY_MAX = 60000


@dataclasses.dataclass(frozen=True)
class Reported:
    """What the verdict check-run says. `state` is `success`, `pending` or `failure`; `kind` names why."""
    state: str
    kind: str
    title: str
    red_at: str = ""


def _stamp(dt: datetime.datetime) -> str:
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")


def reported_state(verdict: Verdict, now: str, first_seen: str,
                   answer_hours: float = ANSWER_DEADLINE_HOURS, review_hours: float = REVIEW_DEADLINE_HOURS) -> Reported:
    """Policy review-pending-while-answering + review-answer-deadline. Pure. `now` and `first_seen` (when
    the gate first judged this head) are ISO-8601 UTC. Fails CLOSED: an unparseable clock, an incomplete
    read, a refusal, or a red reason this function does not recognise is `failure`, never `pending`."""
    roots = len(verdict.unanswered)
    if verdict.green:
        return Reported("success", "answered", "Answered — every thread the automatic reviewer opened has a person's reply")
    if verdict.incomplete:
        return Reported("failure", "unmeasurable", "Could not measure — " + verdict.incomplete[:180])
    if verdict.refused:
        return Reported("failure", "refused", "Unreviewable — the automatic reviewer refused to review this pull request")
    t_now, t_seen = parse_stamp(now), parse_stamp(first_seen)
    if t_now is None or t_seen is None:
        return Reported("failure", "unmeasurable", f"Could not measure — no readable clock (now={now!r}, first judged={first_seen!r})")
    # Every reason must be one the pending reading knows: condition 1 (review_missing) or condition 2
    # (unanswered threads). Anything else is unmeasured, and stays red.
    known = (1 if verdict.review_missing else 0) + (1 if verdict.unanswered else 0)
    if known != len(verdict.reasons):
        return Reported("failure", "unmeasurable", "Could not classify the verdict — " + "; ".join(verdict.reasons)[:180])
    answer_due = []
    for c in verdict.unanswered:
        opened = parse_stamp(c.get("created_at"))
        if opened is None:
            return Reported("failure", "unmeasurable", f"Could not measure — thread {c.get('id')} carries no readable created_at")
        answer_due.append((opened + datetime.timedelta(hours=answer_hours), c))
    overdue = [c for due, c in answer_due if due <= t_now]
    if overdue:
        return Reported("failure", "answer-overdue",
                        f"{len(overdue)} of {roots} unanswered thread(s) open longer than {answer_hours:g} h — answer each ON its thread")
    review_due = t_seen + datetime.timedelta(hours=review_hours) if verdict.review_missing else None
    if review_due is not None and review_due <= t_now:
        return Reported("failure", "review-overdue",
                        f"The automatic review of this head has not landed in {review_hours:g} h — a maintainer re-requests it or waives it")
    first_answer_due = min((due for due, _ in answer_due), default=None)
    # The title names the deadline that ACTUALLY applies first — the review's or a thread's, never a guess.
    if review_due is not None and (first_answer_due is None or review_due <= first_answer_due):
        red_at, which = _stamp(review_due), f"if this head's review has not landed ({review_hours:g} h after it was first judged)"
    else:
        red_at, which = _stamp(first_answer_due), f"if a thread is still unanswered ({answer_hours:g} h after the review that opened it)"
    if verdict.unanswered:
        return Reported("pending", "answering",
                        f"Pending — {roots} of {verdict.threads} thread(s) unanswered; red at {red_at} {which}", red_at)
    return Reported("pending", "awaiting-review", f"Pending — awaiting the automatic review of this head; red at {red_at} {which}", red_at)


def verdict_summary(number: int, verdict: Verdict, reported: Reported) -> str:
    """The check-run's summary: the reading, every unanswered thread by link, then the verdict's own
    step summary (reasons, notes, guidance)."""
    icon = {"success": "✅", "pending": "⏳"}.get(reported.state, "❌")
    out = [f"### {icon} {reported.title}", ""]
    if reported.state == "pending":
        out += ["This check is **pending, not red**: it still blocks the merge (a required context with no conclusion is "
                "never satisfied), and it turns green the moment the last thread has a reply. Policy "
                "`review-pending-while-answering`; deadlines: policy `review-answer-deadline`.", ""]
    body = summary_markdown(number, verdict).split("\n", 1)
    out.append(body[1] if len(body) > 1 else "")
    text = "\n".join(out)
    return text if len(text) <= CHECK_RUN_SUMMARY_MAX else text[:CHECK_RUN_SUMMARY_MAX] + "\n\n…(truncated)"


def verdict_external_id(read_at: str, kind: str) -> str:
    return f"{VERDICT_EXTERNAL_PREFIX}{read_at}:{kind}"


def parse_verdict_external_id(external_id) -> tuple[str, str] | None:
    """(read_at, kind) of a check-run this file wrote, or None for any other check-run. Pure."""
    m = re.fullmatch(re.escape(VERDICT_EXTERNAL_PREFIX) + r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z):([a-z-]+)", str(external_id or ""))
    return (m.group(1), m.group(2)) if m else None


def verdict_runs(check_runs, name: str) -> list:
    """The verdict check-runs THIS file wrote for `name` — provenance (the Actions App) AND the
    external_id this file stamps. A job's own check-run of the same name (a fork's, an older shape's)
    carries no such id and is never touched. Pure."""
    return [c for c in check_runs or () if isinstance(c, dict) and c.get("name") == name
            and ((c.get("app") or {}).get("slug") == ACTIONS_APP_SLUG)
            and parse_verdict_external_id(c.get("external_id")) is not None]


def verdict_write_plan(existing: list, read_at: str) -> tuple[str, list, str]:
    """('create', [], why) | ('patch', [ids], why) | ('skip', [], why). Pure. A verdict whose READ is
    older than the one already on file writes nothing: two evaluations of one head race only across the
    `gate` and `feedback` concurrency lanes, and the one that read LAST must be the one that stands.
    Every verdict check-run of the name is PATCHed, so a duplicate a race created converges."""
    if not existing:
        return "create", [], "no verdict check-run on this head yet"
    newest = max((parse_verdict_external_id(c.get("external_id"))[0] for c in existing))
    if newest > read_at:
        return "skip", [], f"a verdict read at {newest} is already on file — this read ({read_at}) is older, so it writes nothing"
    return "patch", sorted(c.get("id") for c in existing), f"updating {len(existing)} verdict check-run(s) (newest read {newest})"


def first_judged(existing: list, now: str) -> str:
    """When the gate first judged this head: the oldest verdict check-run's `started_at`, else now. Pure."""
    starts = sorted(str(c.get("started_at")) for c in existing if parse_stamp(c.get("started_at")))
    return starts[0] if starts else now


def verdict_body(name: str, head: str, reported: Reported, summary: str, read_at: str, now: str,
                 details_url: str = "") -> dict:
    """The Checks API body. Pure. `pending` is `in_progress` with NO conclusion — never `neutral` or
    `skipped`, which branch protection would accept as satisfied."""
    body = {"name": name, "head_sha": head, "external_id": verdict_external_id(read_at, reported.kind),
            "output": {"title": reported.title[:250], "summary": summary}}
    if details_url:
        body["details_url"] = details_url
    if reported.state == "pending":
        body["status"] = "in_progress"
    else:
        body.update(status="completed", conclusion=reported.state, completed_at=now)
    return body


def read_verdict_runs(gh, head: str, name: str) -> list:
    listing = gh.api(f"commits/{head}/check-runs?check_name={urllib.parse.quote(name)}&filter=all&per_page=100")
    runs = (listing or {}).get("check_runs") if isinstance(listing, dict) else None
    if not isinstance(runs, list) or not isinstance(listing.get("total_count"), int) or len(runs) < listing["total_count"]:
        raise ReadError(f"commits/{head[:10]}/check-runs did not return the complete `{name}` listing")
    return verdict_runs(runs, name)


def write_verdict_check_run(gh, head: str, name: str, reported: Reported, summary: str, read_at: str, now: str,
                            existing: list | None = None) -> str:
    """Create or PATCH the head's verdict check-run. Returns what was done. A failed write raises ReadError
    (an outage raises ApiOutage) — the caller turns either into a RED job, never a silent pass."""
    # 🚨 Read the head's verdicts AGAIN, immediately before the write, whatever the caller passed (#7653 review): the
    # listing taken before the evaluation can be seconds old, and a newer evaluation may have written meanwhile — an
    # older read must never overwrite it (newest READ stands). The residual window is this one read-then-write.
    existing = read_verdict_runs(gh, head, name)
    action, ids, why = verdict_write_plan(existing, read_at)
    if action == "skip":
        return f"skip: {why}"
    run_id = os.environ.get("GITHUB_RUN_ID", "")
    details = f"{os.environ.get('GITHUB_SERVER_URL', 'https://github.com')}/{gh.repo}/actions/runs/{run_id}" if run_id else ""
    body = verdict_body(name, head, reported, summary, read_at, now, details)
    patch = {k: v for k, v in body.items() if k != "head_sha"}
    if action == "create":
        made = gh.write("POST", "check-runs", body)
        return f"created verdict check-run {(made or {}).get('id')} ({reported.state}): {why}"
    if reported.state != "pending":
        for cid in ids:
            gh.write("PATCH", f"check-runs/{cid}", patch)
        return f"patched verdict check-run(s) {ids} to {reported.state}: {why}"
    # PENDING over a head whose verdict had COMPLETED (a new thread on an answered head, a read that now
    # completes): a completed check-run is re-opened when GitHub allows it; when it refuses, the old one is
    # closed as `failure` ("superseded") — never left standing at its old success — and a new pending
    # check-run carries the verdict. A superseded run is never re-opened again, so nothing accumulates.
    done, open_runs = [], 0
    for c in existing:
        kind = parse_verdict_external_id(c.get("external_id"))[1]
        if c.get("status") != "completed":
            gh.write("PATCH", f"check-runs/{c.get('id')}", patch)
            done.append(f"{c.get('id')} updated")
            open_runs += 1
        elif kind != SUPERSEDED_KIND:
            try:
                gh.write("PATCH", f"check-runs/{c.get('id')}", patch)
                done.append(f"{c.get('id')} re-opened")
                open_runs += 1
            except ReadError as e:
                gh.write("PATCH", f"check-runs/{c.get('id')}", {
                    "status": "completed", "conclusion": "failure", "completed_at": now,
                    "external_id": verdict_external_id(read_at, SUPERSEDED_KIND),
                    "output": {"title": "Superseded — a newer, pending verdict is on this head",
                               "summary": f"GitHub refused to re-open this check-run ({str(e)[:200]}), so it was closed and "
                                          "a new one carries the verdict. Policy review-pending-while-answering."}})
                done.append(f"{c.get('id')} superseded")
    if not open_runs:
        made = gh.write("POST", "check-runs", body)
        done.append(f"created {(made or {}).get('id')}")
    return f"pending verdict: {', '.join(done)}: {why}"


def epoch_stamp(gh) -> str:
    """`gh.now()` (epoch seconds) as ISO-8601 UTC — the fake adapter's clock in the self-test."""
    t = getattr(gh, "now", time.time)()
    return _stamp(datetime.datetime.fromtimestamp(t, datetime.UTC).replace(tzinfo=None))


def run_verdict(repo: str, number: int, name: str, gh=None) -> int:
    """The verdict WRITER (policy review-pending-while-answering): judge the pull request ONCE (no review
    wait, no settle wait — a pending verdict is not red), write the head's verdict check-run, exit 0 for
    success or pending and 1 for failure. A read that fails is UNMEASURABLE: written as `failure` when the
    head is known, and the job is red either way."""
    gh = gh if gh is not None else Gh(repo)
    read_at = epoch_stamp(gh)
    head = ""
    try:
        pr, reviews, comments, waiver, author_role, check_runs, files, commits, copilot_carry = read_inputs(gh, number, None)
        head = str((pr.get("head") or {}).get("sha") or "")
        existing = read_verdict_runs(gh, head, name)
    except (ReadError, KeyError) as e:
        print(f"::error::check-review-answered cannot read the review of #{number}, so it cannot say it was answered: {e}")
        if not head:
            # The failed read may have come after the pull request itself was read: one more read for the
            # head, so an unmeasured state is WRITTEN as failure rather than left at an older verdict.
            with contextlib.suppress(ReadError, KeyError, TypeError, AttributeError):
                head = str(((gh.api(f"pulls/{number}") or {}).get("head") or {}).get("sha") or "")
            head = head if re.fullmatch(r"[0-9a-f]{40}", head) else ""
        if head:
            unmeasured = Reported("failure", "unmeasurable", f"Could not measure — {str(e)[:200]}")
            try:
                print("  " + write_verdict_check_run(gh, head, name, unmeasured, f"### ❌ {unmeasured.title}\n", read_at, epoch_stamp(gh)))
            except ReadError as w:
                print(f"::error::and the verdict check-run `{name}` could not be written either: {w}")
                _append("GITHUB_OUTPUT", "verdict-state=failure\n")
                return VERDICT_UNPUBLISHED_EXIT
            _append("GITHUB_OUTPUT", "verdict-state=failure\n")
            return 1
        _append("GITHUB_OUTPUT", "verdict-state=failure\n")
        return VERDICT_UNPUBLISHED_EXIT
    verdict = evaluate(pr, reviews, comments, waiver, None, check_runs, files, commits, copilot_carry)
    now = epoch_stamp(gh)
    reported = reported_state(verdict, now, first_judged(existing, now))
    print(render(number, pr, verdict, author_role, None, reported))
    summary = verdict_summary(number, verdict, reported)
    try:
        done = write_verdict_check_run(gh, head, name, reported, summary, read_at, now, existing)
    except ReadError as e:
        print(f"::error::the verdict ({reported.state}) was taken, but the check-run `{name}` could not be written on "
              f"{head[:10]}: {e}. The required context is NOT updated — this run is red so the gap is seen.")
        _append("GITHUB_OUTPUT", "verdict-state=failure\n")
        return VERDICT_UNPUBLISHED_EXIT
    print(f"  verdict check-run `{name}`: {done}")
    _append("GITHUB_OUTPUT", f"verdict-state={reported.state}\n")
    _append("GITHUB_STEP_SUMMARY", summary)
    return 1 if reported.state == "failure" else 0


def run_verdict_sweep(repo: str, name: str, gh=None, now_epoch: int | None = None) -> int:
    """The scheduled half of the verdict writer: re-judge every open, non-draft, same-repository pull
    request whose verdict check-run on its CURRENT head is pending or unmeasured (SWEEP_KINDS) — a review
    that landed, or a deadline that passed, raises no event here. Every other pull request costs one
    listing read and nothing else. Exit 1 when a read failed (named), 0 otherwise — a red pull request is
    the pull request's verdict, not the sweep's."""
    gh = gh if gh is not None else Gh(repo)
    try:
        pulls = gh.api("pulls?state=open&per_page=100", paginate=True)
        if not isinstance(pulls, list):
            raise ReadError("pulls?state=open did not return a listing")
    except ReadError as e:
        print(f"::error::the verdict sweep cannot list open pull requests: {e}")
        return 1
    mine = sorted((p for p in pulls if isinstance(p, dict) and p.get("number") and not p.get("draft")
                   and ((p.get("head") or {}).get("repo") or {}).get("full_name") == repo),
                  key=lambda p: p["number"])
    numbers = [p["number"] for p in mine]
    window = set(sweep_window(numbers, int(time.time()) if now_epoch is None else now_epoch))
    failed, judged = [], []
    for p in mine:
        if p["number"] not in window:
            continue
        head = str((p.get("head") or {}).get("sha") or "")
        try:
            existing = read_verdict_runs(gh, head, name)
        except ReadError as e:
            print(f"::error::#{p['number']}: {e}")
            failed.append(p["number"])
            continue
        if not existing:
            # 🚨 An ABSENT required context may read as satisfied under a ruleset (Doc/Architecture/ReadingCiSignals),
            # and the head's own event can have died before its first write (cancelled, a failed checkout) — so an
            # absent verdict is UNMEASURED and judged now (#7653 review). Dependabot's pull requests keep the
            # job-shaped check (read-only token), so the writer never owns their context.
            if ((p.get("user") or {}).get("login") or "") == "dependabot[bot]":
                print(f"  #{p['number']}: Dependabot — its context is the job-shaped check, not the writer's")
                continue
            judged.append(p["number"])
            if run_verdict(repo, p["number"], name, gh=gh) == VERDICT_UNPUBLISHED_EXIT:
                print(f"::error::#{p['number']}: no verdict on {head[:10]}, and it could NOT be published")
                failed.append(p["number"])
            continue
        live = [c for c in existing if parse_verdict_external_id(c.get("external_id"))[1] != SUPERSEDED_KIND] or existing
        newest = max(live, key=lambda c: parse_verdict_external_id(c.get("external_id"))[0])
        kind = parse_verdict_external_id(newest.get("external_id"))[1]
        if kind not in SWEEP_KINDS:
            print(f"  #{p['number']}: verdict `{kind}` on {head[:10]} changes only on an event — not re-judged")
            continue
        judged.append(p["number"])
        rc = run_verdict(repo, p["number"], name, gh=gh)
        if rc == VERDICT_UNPUBLISHED_EXIT:
            print(f"::error::#{p['number']}: the verdict could NOT be published")
            failed.append(p["number"])
        elif rc != 0:
            print(f"  #{p['number']}: re-judged — the verdict is red (that is the pull request's state, not a sweep failure)")
    print(f"verdict sweep: re-judged {len(judged)} pull request(s) {judged} of {len(numbers)} open")
    if failed:
        print(f"::error::the verdict sweep could not read or publish {failed}")
    return 1 if failed else 0


def render(number: int, pr: dict, verdict: Verdict, author_role: str | None, as_of: str | None,
           reported: Reported | None = None) -> str:
    """`reported` is the verdict writer's reading (policy review-pending-while-answering): a PENDING
    verdict is headed PENDING and its reasons print as notices, never as `::error::` annotations on a
    run that did not fail."""
    head = f"#{number} ({'draft' if pr.get('draft') else pr.get('state')}) head {str((pr.get('head') or {}).get('sha'))[:10]}"
    pending = reported is not None and reported.state == "pending"
    state = (f"PENDING — {reported.title}" if pending else
             f"RED — {reported.title}" if reported is not None and reported.state == "failure" else
             "GREEN" if verdict.green else
             "RED — UNREVIEWABLE (the reviewer REFUSED to review this pull request)" if verdict.refused else
             "RED — NO COPILOT REVIEW (the clean review on file is not a Copilot review of this pull request)"
             if verdict.not_copilot else
             "RED — THIS HEAD OWES A REVIEW (its own diff changed since Copilot's last review)"
             if verdict.stale else "RED")
    lines = [f"check-review-answered: {head}{' as of ' + as_of if as_of else ''} — {state}"]
    if author_role is not None:
        lines.append(f"  waiver path readable: author @{(pr.get('user') or {}).get('login')} holds `{author_role or 'none'}`")
    lines += [f"  {n}" for n in verdict.notes]
    for r in verdict.reasons:
        lines.append(f"{'::notice::' if pending else '::error::'}{r}")
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
    elif verdict.not_copilot:
        # 🚨 Policy review-evidence-any-head (#4730). The internal reviewer's clean review is not
        # Copilot's. The ruleset requests a Copilot review on every push (review_on_push: true), so one
        # may already be pending — re-requesting it on top would only duplicate it.
        out.append("a Copilot review of this pull request — \"reviewed, nothing found\" is only ever a completed Copilot "
                   "review with zero findings (on any head), never the internal reviewer's clean review or elapsed time. "
                   "The ruleset requests a Copilot review on every push to a non-draft pull request, so one may already be "
                   "pending after the latest push — let it land; do not re-request it on top. Only if none arrives (the "
                   "reviewer refused or cannot review this change), a maintainer then re-requests the Copilot review, or applies "
                   f"the `{WAIVER_LABEL}` label — never an agent, and never automatically.")
    elif verdict.stale:
        # Policy review-on-real-change. Pushes that only merge the default branch never owe a review;
        # this head changed the pull request's own diff, so it does — and the review-on-change lane
        # requested it on that push (request-review-on-change.yml / node-repo-request-review.yml).
        out.append("a Copilot review of THIS head — the pull request's own diff changed since Copilot's last review, so the "
                   "review-on-change lane requested one on the push that changed it; let it land and answer its threads. "
                   "A push that only merges the default branch never owes one. If none arrives (the lane's run is red, or "
                   "the reviewer refused), a maintainer re-requests the Copilot review, or applies "
                   f"the `{WAIVER_LABEL}` label — never an agent, and never automatically.")
    elif "has not landed" in text:
        out.append("the automatic review must land. It usually arrives minutes after the pull request opens or after each push; if the "
                   "reviewer refused (quota) or cannot review this change, a maintainer re-requests the review, or applies "
                   f"the `{WAIVER_LABEL}` label to waive it — never an agent, and never automatically. Elapsed time and "
                   "a \"Reviewer unavailable\" run release nothing (policy review-evidence-any-head).")
    if verdict.unanswered:
        out.append("reply to each unanswered thread (fixed, or why not). Resolving a thread is not a reply, and a waiver "
                   "does not release a finding the reviewer did post.")
    if "comment listing" in text:
        out.append("nothing on the pull request needs to change — the check could not prove it read every comment; "
                   "re-run this check once the read can complete.")
    return out


def summary_markdown(number: int, verdict: Verdict) -> str:
    state = ("✅ green" if verdict.green
             else "❌ red — **unreviewable right now**: the reviewer refused to review this pull request"
             if verdict.refused
             else "❌ red — **no Copilot review**: the clean review on file is not a Copilot review of this pull request"
             if verdict.not_copilot
             else "❌ red — **this head owes a review**: the pull request's own diff changed since Copilot's last review"
             if verdict.stale else "❌ red")
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


def review_wait_fingerprint(reviews, check_runs, labels=()) -> tuple:
    """What can END the review wait, and nothing else: the pull request's reviews (one landing, or a
    refusal), the head's `internal-review` check runs (the degradation) and whether the WAIVER_LABEL is
    on the pull request (a maintainer's waiver). Pure. `labels` are label objects (`{"name": …}`).

    🚨 The waiver is probed too (MeshWeaver#7203 review): its own `labeled` run cannot judge sooner in
    core, because it queues in this run's non-cancelling `…-gate` concurrency group, so without it a
    valid waiver applied during the wait was honoured only at the final read, ~15 minutes later.

    🚨 WHY THE WAIT PROBES INSTEAD OF RE-READING (MeshWeaver#7187). The wait used to re-run the WHOLE
    read every POLL_SECONDS — the pull request, reviews, review comments, issue events, collaborator
    roles, the degradation listing and, once Copilot had reviewed an earlier head, the carry-over
    compares: 7–10 calls a poll, 30 polls in a 15-minute wait, and core runs the evaluation TWICE per
    event (the gate job and the lane) — up to ~600 calls of the repository's shared 5,000/hour
    installation budget for ONE push whose review is slow, while its answer can change in only the
    inputs fingerprinted here (a push or a reply each start a run of their own). The probe reads those
    three (3 calls) and the full read runs again only when one changed, and once
    more at the deadline — so the final verdict is still taken on a fresh, complete read."""
    rv = tuple(sorted((r.get("id"), r.get("state"), r.get("submitted_at"), r.get("commit_id"))
                      for r in reviews if isinstance(r, dict)))
    cr = tuple(sorted((c.get("id"), c.get("status"), c.get("conclusion")) for c in check_runs if isinstance(c, dict)))
    waived = any(isinstance(l, dict) and l.get("name") == WAIVER_LABEL for l in labels or ())
    return rv, cr, waived


def probe_review_wait(gh, number: int, head_sha: str) -> tuple:
    """The three reads `review_wait_fingerprint` needs — reviews and check runs from the SAME endpoints
    `read_inputs` reads them from, and the pull request's labels (the issue's label listing, one call)."""
    reviews = gh.api(f"pulls/{number}/reviews?per_page=100", paginate=True)
    listing = gh.api(f"commits/{head_sha}/check-runs?check_name={DEGRADATION_CHECK_NAME}&filter=all&per_page=100")
    check_runs = (listing or {}).get("check_runs") if isinstance(listing, dict) else None
    labels = gh.api(f"issues/{number}/labels?per_page=100")
    if not isinstance(reviews, list) or not isinstance(check_runs, list) or not isinstance(labels, list):
        raise ReadError(f"the review-wait probe of #{number} did not return its three listings")
    return review_wait_fingerprint(reviews, check_runs, labels)


def run(repo: str, number: int, as_of: str | None, wait_minutes: int = 0,
        settle_seconds: int = 0, gh=None) -> int:
    """`gh` defaults to `Gh(repo)`; the self-test injects a fake adapter (its `sleep`/`now` included)."""
    gh = gh if gh is not None else Gh(repo)
    sleep, clock = getattr(gh, "sleep", time.sleep), getattr(gh, "now", time.monotonic)
    deadline = clock() + wait_minutes * 60
    # The settle wait gets its OWN cap, so it can never be spent twice or chain behind the review
    # wait into the job's timeout: a person answering steadily for longer than this is a
    # separate round of work and gets its own evaluation.
    settle_deadline = clock() + max(settle_seconds, 0) * SETTLE_ROUNDS
    settled_for = 0.0
    while True:
        try:
            pr, reviews, comments, waiver, author_role, check_runs, files, commits, copilot_carry = read_inputs(gh, number, as_of)
        except (ReadError, KeyError) as e:
            print(f"::error::check-review-answered cannot read the review of #{number}, so it cannot say it was answered: {e}")
            return 1
        verdict = evaluate(pr, reviews, comments, waiver, as_of, check_runs, files, commits, copilot_carry)
        left = deadline - clock()
        if waiting_would_help(verdict) and left > POLL_SECONDS:
            print(f"  the automatic review has not landed yet; waiting up to {int(left)}s more for it "
                  f"(its own event cannot start a run here — see waiting_would_help); probing reviews and "
                  f"the degradation only, the full read runs again when either changes", flush=True)
            seen = review_wait_fingerprint(reviews, check_runs, pr.get("labels"))
            head_sha = str((pr.get("head") or {}).get("sha") or "")
            while deadline - clock() > POLL_SECONDS:
                sleep(POLL_SECONDS)
                try:
                    if probe_review_wait(gh, number, head_sha) != seen:
                        break
                except (ReadError, KeyError) as e:
                    print(f"  the review-wait probe failed ({e}) — taking the full read now", flush=True)
                    break
            continue
        if replies_still_landing(verdict, comments, settle_seconds, as_of) \
                and clock() < settle_deadline:
            newest = newest_person_reply(comments, as_of)
            age = reply_age_seconds(newest) or 0
            print(f"  a person replied {int(age)}s ago and {len(verdict.unanswered)} thread(s) are "
                  f"still unanswered — the answering is in progress, so this evaluation would be a "
                  f"snapshot of a moving target. Waiting for {settle_seconds}s of quiet before "
                  f"judging (see replies_still_landing).", flush=True)
            sleep(SETTLE_POLL_SECONDS)
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
# MeshWeaver.Plugins#4507 (2026-10-28), verbatim in shape: an overview verdict over a pull request
# whose only file is one the reviewer itself classes `Generated file`. A REVIEW, not a refusal.
REVIEW_BODY_GENERATED_ONLY = ("### 🟢 Approval recommended\n\nNo active review issues were identified for the undici "
                              "dependency lockfile update.\n\nCopilot wasn't able to review any files in this pull request.\n"
                              "<details>\n<summary><strong>1 file not reviewed</strong></summary>\n\n<ul>\n<li><strong><code>"
                              "clients/portal-next/package-lock.json</code></strong>: Generated file</li>\n</ul>\n</details>\n")
# Negative controls: the same shape with a skipped file that is NOT generated, and one whose list
# is shorter than the count it announces — both stay refusals.
REFUSAL_SKIPPED_UNSUPPORTED = REVIEW_BODY_GENERATED_ONLY.replace(
    "1 file not reviewed", "2 files not reviewed").replace(
    "</ul>", "<li><strong><code>tools/x.bin</code></strong>: Language not supported</li>\n</ul>")
REFUSAL_SKIPPED_SHORT_LIST = REVIEW_BODY_GENERATED_ONLY.replace("1 file not reviewed", "3 files not reviewed")
# A PR-level refusal in another spelling, followed by a complete generated-only list: still a refusal.
REFUSAL_PR_LEVEL_WITH_GENERATED_LIST = REVIEW_BODY_GENERATED_ONLY.replace(
    "Copilot wasn't able to review any files in this pull request.",
    "Copilot wasn't able to review this pull request because the service is unavailable.")
# The three bodies the reviewer actually posted on 2026-09-18 (#4725, #4728, #4721), verbatim in
# shape: a verdict heading and a sentence or two, with NO overview block. Every one of these was a
# real review that the pre-2026-09-18 marker rule classified as "not landed".
REVIEW_BODY_APPROVE_0918 = "### 🟢 Approval recommended\n\nAll reviewed changes are covered and no unresolved blocking issues remain."
REVIEW_BODY_CHANGES_0918 = "### 🟡 Changes recommended\n\nTwo small but concrete correctness issues were found in the updated BuildNodeType comments/logging."
REVIEW_BODY_CLOSER_0918 = "### 🔵 Needs a closer look\n\nConditional `[JsonIgnore]` cases remain insufficiently covered and may omit editor-written values."


def _review(body=REVIEW_BODY_SEPT, at="2026-09-14T12:22:44Z", user=REVIEWER_REVIEW_USER, state="COMMENTED", rid=1):
    return {"id": rid, "user": user, "state": state, "submitted_at": at, "body": body, "commit_id": "a" * 40}


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
             check_runs=(), carry=None):
        """`says`/`never_says` assert what the READER is told — the guidance line and the step
        summary — not just the verdict. #4730 was entirely about those two strings being wrong while
        the verdict was right, so a case that checks only `reasons` cannot see it."""
        nonlocal failures
        v = evaluate(pr, reviews, comments, waiver, as_of, check_runs, copilot_carry=carry)
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
    # Policy review-on-real-change: what the adapter's `copilot_carry_over` hands the gate for a head
    # that differs from Copilot's reviewed head `b…` — only by a clean main-merge (COVERS), or by a
    # real change (NOT_COVERED). The carry's own derivation is proven further down, from commits.
    OWES = "owes its own Copilot review"
    COVERS = Carry(True, "Copilot's review 61 of bbbbbbbbbb COVERS head aaaaaaaaaa: the 1 commit(s) since it are all merges "
                         "of the base branch (cccccccccc) … (policy review-on-real-change)", "b" * 40)
    NOT_COVERED = Carry(False, "Copilot's review 61 of bbbbbbbbbb does not cover head aaaaaaaaaa: 1 non-merge commit(s) since it",
                        "b" * 40)

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
    # MeshWeaver.Plugins#4507: an overview verdict whose every skipped file is `Generated file` is a
    # review — even after earlier quota refusals, and the reader is not told "unreviewable".
    case("overview over generated-only files is a review (Plugins#4507)", GREEN, _pr(0),
         [_review(REFUSAL_QUOTA, at="2026-10-28T06:32:04Z", rid=1), _review(REFUSAL_QUOTA, at="2026-10-28T08:00:01Z", rid=2),
          _review(REVIEW_BODY_GENERATED_ONLY, at="2026-10-28T10:12:44Z", rid=3)], [],
         mention=("automatic review landed: review 3",), never_says=("UNREVIEWABLE",))
    case("quota refusals alone stay unreviewable (negative control for Plugins#4507)", (NOT_LANDED,), _pr(0),
         [_review(REFUSAL_QUOTA, at="2026-10-28T06:32:04Z", rid=1), _review(REFUSAL_QUOTA, at="2026-10-28T08:00:01Z", rid=2)], [],
         says=("RED — UNREVIEWABLE",))
    case("zero files reviewed with a NON-generated file skipped is not a review", (NOT_LANDED,), _pr(0),
         [_review(REFUSAL_SKIPPED_UNSUPPORTED)], [], mention=("is a refusal",))
    case("zero files reviewed with a list shorter than its count is not a review", (NOT_LANDED,), _pr(0),
         [_review(REFUSAL_SKIPPED_SHORT_LIST)], [], mention=("is a refusal",))
    case("a PR-level refusal stays a refusal even over a generated-only list (#7488 review)", (NOT_LANDED,), _pr(0),
         [_review(REFUSAL_PR_LEVEL_WITH_GENERATED_LIST)], [], mention=("is a refusal",))
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
    # …but with NO finding it is not "reviewed, nothing found" (policy review-evidence-any-head, #4730).
    case("the internal reviewer's clean review alone is not the evidence", ("no Copilot review of this pull request",), _pr(0),
         [_review("**Internal review (GLM-5.3)** — no blocking findings.", user=INTERNAL_REVIEWER_USER)], [])
    case("the internal reviewer at CHANGES_REQUESTED with no thread is not the evidence either", ("no Copilot review of this pull request",), _pr(0),
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

    # The App rename `systemorph-com` → `systemorph` (step 1 of 3: accept both). The renamed bot keeps
    # its account id, so the id alone already recognises it; the login-only cases prove the LOGIN set
    # carries the new spelling as well, and the controls prove nothing else rides on it.
    RENAMED_REVIEWER_USER = {"login": "systemorph[bot]", "type": "Bot", "id": INTERNAL_REVIEWER_ACCOUNT_ID}
    RENAMED_LOGIN_ONLY = {"login": "systemorph[bot]", "type": "Bot", "id": 900000001}
    OLD_LOGIN_ONLY = {"login": "systemorph-com[bot]", "type": "Bot", "id": 900000002}
    STRANGER_BOT = {"login": "some-other-app[bot]", "type": "Bot", "id": 900000003}
    case("RENAME: a systemorph[bot] thread with no reply is unanswered", (UNANSWERED,), _pr(1),
         [_review(user=RENAMED_REVIEWER_USER)], [_comment(1, RENAMED_REVIEWER_USER)], mention=("1 of 1",))
    case("RENAME: a systemorph[bot] thread answered by a person is green", GREEN, _pr(2),
         [_review(user=RENAMED_REVIEWER_USER)],
         [_comment(1, RENAMED_REVIEWER_USER), _comment(11, PERSON, 1, "2026-09-14T13:00:00Z")])
    case("RENAME: the new login is the reviewer by LOGIN alone", (UNANSWERED,), _pr(1),
         [_review(user=RENAMED_LOGIN_ONLY)], [_comment(1, RENAMED_LOGIN_ONLY)], mention=("1 of 1",))
    case("RENAME: the old login stays the reviewer by LOGIN alone", (UNANSWERED,), _pr(1),
         [_review(user=OLD_LOGIN_ONLY)], [_comment(1, OLD_LOGIN_ONLY)], mention=("1 of 1",))
    case("RENAME NEG: another App's bot is not the reviewer", (NOT_LANDED,), _pr(0), [_review(user=STRANGER_BOT)], [])
    case("RENAME NEG: a User account named systemorph[bot] is not the reviewer", (NOT_LANDED,), _pr(0),
         [_review(user={"login": "systemorph[bot]", "type": "User", "id": 8})], [])
    for name, app, expect in [
        ("RENAME: the degradation App under its old slug", {"id": DEGRADATION_APP_ID, "slug": "systemorph-com"}, True),
        ("RENAME: the degradation App under its new slug", {"id": DEGRADATION_APP_ID, "slug": "systemorph"}, True),
        ("RENAME NEG: the new slug on another App id", {"id": 900000004, "slug": "systemorph"}, False),
        ("RENAME NEG: another App entirely", {"id": 15368, "slug": "github-actions"}, False),
    ]:
        got = is_degradation_app(app)
        ok = got == expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={expect} got={got}")

    # 🚨 Policy review-evidence-any-head (#4730): "reviewed, nothing found" is ONLY a completed
    # Copilot review object on the pull request with zero findings — on ANY of its heads, because a
    # re-review per push is not required (a merge of main after a clean review stays reviewed). The
    # internal reviewer's "Reviewer unavailable" degradation (MeshWeaver.Feedback#86) used to release
    # condition 1 — it read "never reviewed" as enough. Each case below pins one of those, with a
    # control beside it that differs in exactly the field under test.
    EVIDENCE = "review-evidence-any-head"
    older_clean = dict(_review(rid=61), commit_id="b" * 40)
    case("EVIDENCE: a clean Copilot review of the CURRENT head -> green, and named as such", GREEN, _pr(0), [_review()], [],
         mention=("reviewed, nothing found", EVIDENCE), never_says=("NO COPILOT REVIEW", "earlier head"))
    # 🔁 Policy review-on-real-change (supersedes "no re-review per push" for a REAL change): an earlier
    # head's review covers the head only across clean merges of the default branch.
    case("REAL CHANGE: a clean review of an EARLIER head, then a MAIN-ONLY merge -> green, the cover named", GREEN,
         _pr(0), [older_clean], [], carry=COVERS,
         mention=("reviewed, nothing found", "earlier head", "bbbbbbbbbb", EVIDENCE, "COVERS head aaaaaaaaaa"),
         never_says=("NO COPILOT REVIEW", "OWES A REVIEW", "re-requests the Copilot review"))
    case("REAL CHANGE NEG: a clean review of an EARLIER head, then a REAL change -> red, the head owes a review", (OWES,),
         _pr(0), [older_clean], [], carry=NOT_COVERED, mention=("1 non-merge commit(s)",),
         says=("RED — THIS HEAD OWES A REVIEW", "this head owes a review", "review-on-change lane"),
         never_says=("RED — UNREVIEWABLE", "reply to each unanswered thread"))
    case("REAL CHANGE NEG: an earlier head's review and NO carry computed -> red (fails closed)", (OWES,),
         _pr(0), [older_clean], [], carry=None, mention=("nothing showed that only the default branch",))
    case("REAL CHANGE NEG: a carry from ANOTHER head does not cover -> red", (OWES,),
         _pr(0), [older_clean], [], carry=Carry(True, "covers", "c" * 40))
    case("REAL CHANGE: the real change's OWN review lands -> green (what the red waits for)", GREEN,
         _pr(0), [older_clean, _review(rid=62, at="2026-09-14T14:00:00Z")], [], carry=NOT_COVERED,
         never_says=("OWES A REVIEW",))
    case("REAL CHANGE NEG: an earlier head's review, then a REFUSAL of the current head -> red, unreviewable, not waited for",
         (OWES,), _pr(0), [older_clean, _review(REFUSAL_QUOTA, rid=63, at="2026-09-14T14:00:00Z")], [], carry=NOT_COVERED,
         says=("RED — UNREVIEWABLE",))
    v_refused_head = evaluate(_pr(0), [older_clean, _review(REFUSAL_QUOTA, rid=63, at="2026-09-14T14:00:00Z")], [], NO_WAIVER,
                              copilot_carry=NOT_COVERED)
    ok = bool(v_refused_head.refused) and not waiting_would_help(v_refused_head)
    failures += 0 if ok else 1
    print(f"self-test {'ok' if ok else 'FAIL':4} {'REAL CHANGE: a refusal of the owing head is not waited for':60} "
          f"refused={bool(v_refused_head.refused)} waits={waiting_would_help(v_refused_head)}")
    case("REAL CHANGE: the waiver releases a head that owes a review", GREEN, _pr(0, [WAIVER_LABEL]), [older_clean], [],
         Waiver(True, (_labeled(PERSON),), {"rbuergi": "admin"}), carry=NOT_COVERED, mention=("WAIVED", OWES))
    v_owes = evaluate(_pr(0), [older_clean], [], NO_WAIVER, copilot_carry=NOT_COVERED)
    v_three = evaluate(_pr(3), [older_clean], three, NO_WAIVER, copilot_carry=NOT_COVERED)
    ok = waiting_would_help(v_owes) and not waiting_would_help(v_three)
    failures += 0 if ok else 1
    print(f"self-test {'ok' if ok else 'FAIL':4} {'REAL CHANGE: a head owing its review is waited for; owing + unanswered is not':60} "
          f"owes={waiting_would_help(v_owes)} owes+threads={waiting_would_help(v_three)}")
    case("EVIDENCE NEG: no review EVER (no review object at all) -> red", (NOT_LANDED,), _pr(0), [], [],
         never_says=("reviewed, nothing found",))
    case("EVIDENCE NEG: a REFUSAL on an earlier head, then a push -> still red (a refusal is no review)", (NOT_LANDED,),
         _pr(0), [dict(_review(REFUSAL_QUOTA, rid=66), commit_id="b" * 40)], [], says=("RED — UNREVIEWABLE",))
    case("EVIDENCE: refusal, then a clean review of an EARLIER head (main merged since) -> green, the refusal is history", GREEN, _pr(0),
         [_review(REFUSAL_QUOTA, rid=64, at="2026-09-14T11:00:00Z"), dict(_review(rid=65), commit_id="b" * 40)], [], carry=COVERS,
         never_says=("RED — UNREVIEWABLE", "unreviewable right now", "REFUSED to review it"))
    case("EVIDENCE: an earlier clean review AND one of the current head -> green, the current one named", GREEN, _pr(0),
         [older_clean, _review(rid=62, at="2026-09-14T14:00:00Z")], [], mention=("reviewed, nothing found", "review 62"),
         never_says=("earlier head",))
    case("EVIDENCE NEG: the internal reviewer's clean review is not Copilot's -> red", ("no Copilot review of this pull request",),
         _pr(0), [_review("**Internal review (GLM-5.3)** — no blocking findings.", user=INTERNAL_REVIEWER_USER)], [],
         mention=("@systemorph-com[bot]",), says=("RED — NO COPILOT REVIEW", "re-requests the Copilot review"),
         never_says=("usually arrives minutes after", "RED — UNREVIEWABLE"))
    case("EVIDENCE NEG: the internal reviewer's clean review of an EARLIER head is not Copilot's either -> red",
         ("no Copilot review of this pull request",), _pr(0),
         [dict(_review("**Internal review (GLM-5.3)** — no blocking findings.", user=INTERNAL_REVIEWER_USER), commit_id="b" * 40)], [])
    case("EVIDENCE NEG: refusal, then the internal reviewer's clean review -> 'no Copilot review', not 'unreviewable'",
         ("no Copilot review of this pull request",), _pr(0),
         [_review(REFUSAL_QUOTA, rid=64, at="2026-09-14T11:00:00Z"),
          _review("**Internal review (GLM-5.3)** — no blocking findings.", user=INTERNAL_REVIEWER_USER, rid=65)], [],
         says=("RED — NO COPILOT REVIEW",), never_says=("RED — UNREVIEWABLE", "REFUSED to review it"))
    case("EVIDENCE: the waiver still releases an internal-only clean review", GREEN, _pr(0, [WAIVER_LABEL]),
         [_review("**Internal review (GLM-5.3)** — no blocking findings.", user=INTERNAL_REVIEWER_USER)], [],
         Waiver(True, (_labeled(PERSON),), {"rbuergi": "admin"}), mention=("WAIVED",), never_says=("NO COPILOT REVIEW",))
    case("EVIDENCE: WITH findings, an earlier head's review + every thread answered + main-only merge -> green", GREEN, _pr(2),
         [older_clean], [_comment(1), _comment(11, PERSON, 1)], carry=COVERS)
    case("REAL CHANGE NEG: WITH findings all answered, a REAL change since -> red (the threads do not stand in)", (OWES,), _pr(2),
         [older_clean], [_comment(1), _comment(11, PERSON, 1)], carry=NOT_COVERED)
    case("EVIDENCE NEG: the 'Reviewer unavailable' degradation no longer releases condition 1", (NOT_LANDED,), _pr(0), [], [],
         check_runs=[_check_run()], says=("release nothing",), never_says=("REVIEWER UNAVAILABLE —",))
    case("EVIDENCE NEG: degradation + the internal reviewer's thread answered -> still red (no review object)", (NOT_LANDED,),
         _pr(2), [], [_comment(1, INTERNAL_REVIEWER_USER), _comment(11, PERSON, 1, "2026-09-14T13:00:00Z")], check_runs=[_check_run()])
    case("EVIDENCE: degradation + the waiver -> green, credited to the person", GREEN, _pr(0, [WAIVER_LABEL]), [], [],
         Waiver(True, (_labeled(PERSON),), {"rbuergi": "admin"}), check_runs=[_check_run()], mention=("WAIVED",))

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
    case("no review_comments count reported", (LISTING,), {"number": 1, "labels": [], "head": {"sha": "a" * 40}}, [_review()], [])
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
        ("wait: a degradation releases nothing, the review may still arrive (#4730)", True, [_check_run()]),
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
    # 🔁 Policy review-on-real-change narrows this: an OLDER head's review counts across a main-only merge.
    case("ONCE merge: review on an OLDER head, main-only merge since, every thread answered -> green", (), _pr(2), [older],
         [_comment(1), _comment(11, PERSON, 1)], carry=COVERS)
    case("ONCE merge: NO review at all -> red", (NOT_LANDED,), _pr(0), [], [])
    case("ONCE merge: review on an OLDER head, main-only merge since, a thread unanswered -> red", (UNANSWERED,), _pr(3), [older],
         [_comment(1), _comment(2), _comment(11, PERSON, 1)], carry=COVERS)
    case("ONCE merge NEG: review on an OLDER head, a REAL change AND a thread unanswered -> red for both", (OWES, UNANSWERED), _pr(3),
         [older], [_comment(1), _comment(2), _comment(11, PERSON, 1)], carry=NOT_COVERED)
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
    # ── arming a generated-only App PR with no review (`--arm-generated`): ARM only on positive proof.
    SHA = "a" * 40
    def apr(**kw):
        repo = {"full_name": "Systemorph/MeshWeaver.Plugins", "default_branch": "main"}
        base = dict(gpr(), state="open", draft=False, commits=1, head={"sha": SHA, "repo": repo},
                    base={"ref": "main", "repo": repo})
        base.update(kw)
        return base
    def arm_case(name, expect, pr, files, commits, head_after=SHA):
        nonlocal failures
        got, why = arm_generated_verdict(pr, files, commits, head_after)
        ok = got is expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} arm: {name:55} expected={expect} got={got} ({why})")
    arm_case("settle PR (App, locks only) -> ARM", True, apr(), LOCKS, BOT_COMMITS)
    arm_case("floor stamp (App, floor lines only) -> ARM", True, apr(), FLOOR, BOT_COMMITS)
    arm_case("a PERSON's PR touching only locks -> never", False, apr(user=HUMAN), LOCKS, [{"author": HUMAN}])
    arm_case("another bot's PR (systemorph-com) -> never", False,
             apr(user={"login": "systemorph-com[bot]", "type": "Bot", "id": 1}), LOCKS, BOT_COMMITS)
    arm_case("App PR with one hand-written file (mixed) -> never", False, apr(),
             LOCKS[:1] + [{"filename": "Hosting/X.cs", "patch": "+x"}], BOT_COMMITS)
    arm_case("App PR with a person's commit -> never", False, apr(commits=2), LOCKS, BOT_COMMITS + [{"author": HUMAN}])
    arm_case("changed_files count missing, partial list -> never", False,
             {k: v for k, v in apr().items() if k != "changed_files"}, LOCKS[:1], BOT_COMMITS)
    arm_case("commits count missing -> never", False, {k: v for k, v in apr().items() if k != "commits"}, LOCKS, BOT_COMMITS)
    arm_case("more files listed than reported -> never", False, apr(changed_files=1), LOCKS, BOT_COMMITS)
    arm_case("unreadable file list (None) -> never", False, apr(), None, BOT_COMMITS)
    arm_case("empty file list -> never", False, apr(), [], BOT_COMMITS)
    arm_case("short file list (2 of 3) -> never", False, apr(changed_files=3), LOCKS, BOT_COMMITS)
    arm_case("unreadable commit list -> never", False, apr(), LOCKS, None)
    arm_case("index.json with no patch (too large to diff) -> never", False, apr(changed_files=1),
             [{"filename": "AI/index.json"}], BOT_COMMITS)
    arm_case("draft -> never", False, apr(draft=True), LOCKS, BOT_COMMITS)
    arm_case("draft flag missing -> never", False, {k: v for k, v in apr().items() if k != "draft"}, LOCKS, BOT_COMMITS)
    arm_case("closed -> never", False, apr(state="closed"), LOCKS, BOT_COMMITS)
    arm_case("head from a fork -> never", False,
             apr(head={"sha": SHA, "repo": {"full_name": "someone/MeshWeaver.Plugins"}}), LOCKS, BOT_COMMITS)
    arm_case("base is not the default branch -> never", False,
             apr(base={"ref": "release", "repo": {"full_name": "Systemorph/MeshWeaver.Plugins", "default_branch": "main"}}),
             LOCKS, BOT_COMMITS)
    arm_case("head moved while the files were read -> never", False, apr(), LOCKS, BOT_COMMITS, head_after="b" * 40)
    arm_case("head unread after the files -> never", False, apr(), LOCKS, BOT_COMMITS, head_after=None)
    # ── the REQUIRED verdict (`evaluate`) agrees with the stage gate on generated-only App PRs (Plugins #3044):
    # the reviewer refuses a lock-only pull request, and without this the settle/stamp PRs sat red until a
    # person waived them — main published nothing in between. NEGATIVE CONTROLS: the same refusal on a
    # person's lock-only PR, and on an App PR carrying one hand-written file, stays RED (refused).
    LOCK_REFUSAL = ("Copilot wasn't able to review any files in this pull request. Check if the **Files changed** "
                    "in this pull request are included in [default exclusions](https://docs.github.com).")
    def verdict_case(name, want_green, pr, files, commits, comments=(), want_refused=None, reviews=None, carry=None):
        nonlocal failures
        v = evaluate(pr, [_review(LOCK_REFUSAL)] if reviews is None else reviews, list(comments), NO_WAIVER, None, (), files, commits,
                     copilot_carry=carry)
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
    verdict_case("NEGATIVE CONTROL: settle PR, reviewer requested and not answered yet -> RED", False,
                 dict(gpr(created="2026-10-04T08:00:00Z"), requested_reviewers=[REVIEWER_COMMENT_USER]), LOCKS, BOT_COMMITS,
                 want_refused=False, reviews=[])
    verdict_case("NEGATIVE CONTROL: settle PR opened 1 min ago, nothing requested -> RED (may still be requested)", False,
                 dict(gpr(created=datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")), requested_reviewers=[]),
                 LOCKS, BOT_COMMITS, want_refused=False, reviews=[])
    verdict_case("settle PR, no reviewer requested 10+ min after open -> GREEN (no review is coming)", True,
                 dict(gpr(created="2026-10-04T08:00:00Z"), requested_reviewers=[]), LOCKS, BOT_COMMITS, want_refused=False, reviews=[])
    verdict_case("NEGATIVE CONTROL: a PERSON's lock PR, nothing requested for hours -> RED", False,
                 dict(gpr(user=HUMAN, created="2026-10-04T08:00:00Z"), requested_reviewers=[]), LOCKS, [{"author": HUMAN}], reviews=[])
    verdict_case("NEGATIVE CONTROL: settle PR, an UNRECOGNISED (empty) reviewer response, nothing requested -> RED", False,
                 dict(gpr(created="2026-10-04T08:00:00Z"), requested_reviewers=[]), LOCKS, BOT_COMMITS, reviews=[_review("")])
    stale = [dict(_review(rid=63), commit_id="b" * 40)]
    verdict_case("settle PR rewritten after a clean review of an EARLIER head -> GREEN (not owed)", True, gpr(), LOCKS, BOT_COMMITS,
                 want_refused=False, reviews=stale)
    verdict_case("a person's lock PR after a clean earlier-head review + main-only merge -> GREEN", True, gpr(user=HUMAN), LOCKS,
                 [{"author": HUMAN}], want_refused=False, reviews=stale,
                 carry=Carry(True, "covers", "b" * 40))
    verdict_case("NEGATIVE CONTROL: a person's lock PR, a REAL change after the earlier-head review -> RED", False, gpr(user=HUMAN), LOCKS,
                 [{"author": HUMAN}], want_refused=False, reviews=stale)
    verdict_case("NEGATIVE CONTROL: a person's lock PR, only the internal reviewer's clean review -> RED", False, gpr(user=HUMAN), LOCKS,
                 [{"author": HUMAN}], want_refused=False,
                 reviews=[_review("**Internal review (GLM-5.3)** — no blocking findings.", user=INTERNAL_REVIEWER_USER)])
    verdict_case("NEGATIVE CONTROL: settle PR, payload without requested_reviewers -> RED (not proven empty)", False,
                 gpr(created="2026-10-04T08:00:00Z"), LOCKS, BOT_COMMITS, reviews=[])
    v_replay = evaluate(dict(gpr(created="2026-10-04T08:00:00Z"), requested_reviewers=[]), [], [], NO_WAIVER, "2026-10-04T09:00:00Z", (), LOCKS, BOT_COMMITS)
    ok = not v_replay.green
    failures += 0 if ok else 1
    print(f"self-test {'ok' if ok else 'FAIL':4} verdict: {'NEGATIVE CONTROL: a replay (--as-of) never releases on requested_reviewers':51} green={v_replay.green}")
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
        def role(self, login):
            return (self.api(f"collaborators/{login}/permission") or {}).get("role_name") or ""
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

    # ── policy review-on-real-change, derived from COMMITS (the same fixture topology): Copilot reviewed
    # O; M merges B2 into O. Does Copilot's review of O cover M? Only when nothing but main came in.
    COP_O = dict(_review(rid=81), commit_id=O)
    def cop_carry_case(name, expect_ok, c, says):
        nonlocal failures
        ok = c.ok == expect_ok and says in c.why
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} real-change: {name:53} expected={'COVERS' if expect_ok else 'OWES'} "
              f"got={'COVERS' if c.ok else 'OWES'}{'' if ok else ' — ' + c.why}")
    cop_carry_case("main-only merge after the review -> covered", True, copilot_carry_over(M, NEW_CMP, OLD_CMP, COP_O),
                   f"COVERS head {M[:10]}")
    cop_carry_case("NEG: a real (non-merge) commit after the review -> owes", False,
                   copilot_carry_over(X, cmp_(NEW_CMP["commits"] + [commit(X, M)], files(PATCH_SHIFTED), B2), OLD_CMP, COP_O),
                   "non-merge commit(s) since it")
    cop_carry_case("NEG: a real commit, THEN a main-only merge -> still owes", False,
                   copilot_carry_over(M3, cmp_(OLD_CMP["commits"] + [commit(X, O), commit(M3, X, B2)], files(PATCH_SHIFTED), B2),
                                      OLD_CMP, COP_O), "non-merge commit(s) since it")
    cop_carry_case("NEG: a main merge whose conflict resolution changed the diff -> owes", False,
                   copilot_carry_over(M, dict(NEW_CMP, files=files(PATCH_RESOLVED)), OLD_CMP, COP_O), "own diff changed")
    cop_carry_case("NEG: the reviewed head was force-pushed away -> owes", False,
                   copilot_carry_over(X, cmp_([commit(P1, B0), commit(X, P1)], files(PATCH_SHIFTED), B0), OLD_CMP, COP_O),
                   "no longer on the pull request")
    cop_carry_case("NEG: a review naming no commit -> owes", False,
                   copilot_carry_over(M, NEW_CMP, OLD_CMP, dict(COP_O, commit_id=None)), "names no reviewed commit")
    # ...and the merge gate's ADAPTER end to end: read_inputs computes the carry from the base tip and
    # two compares, and evaluate turns it into the verdict.
    cop_routes = {
        "pulls/1/reviews": [COP_O], "pulls/1/comments": [], "issues/1/events": [],
        "pulls/1": dict(MPR, base={"ref": "main"}), f"commits/{M}/check-runs": listing([]),
        "branches/main": {"commit": {"sha": sha("b")}},
        f"compare/{sha('b')}...{M}": NEW_CMP, f"compare/{sha('b')}...{O}": OLD_CMP,
        "collaborators/": {"role_name": "admin"},
    }
    def gate_e2e(name, routes, want_green, says):
        nonlocal failures
        got = read_inputs(_RestStub(routes), 1, None)
        v = evaluate(*got[:3], got[3], None, got[5], got[6], got[7], got[8])
        ok = v.green == want_green and says in "\n".join(v.reasons + v.notes)
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} real-change gate: {name:48} green={v.green}"
              + ("" if ok else f" {v.reasons} {v.notes}"))
    gate_e2e("main-only merge after Copilot's review -> GREEN", cop_routes, True, "COVERS head")
    gate_e2e("NEG: same, the merge changed the diff -> RED", dict(cop_routes, **{f"compare/{sha('b')}...{M}": dict(NEW_CMP, files=files(PATCH_RESOLVED))}),
             False, "owes its own Copilot review")
    gate_e2e("NEG: same, a compare unreadable -> RED (fails closed)",
             {k: v for k, v in cop_routes.items() if k != f"compare/{sha('b')}...{O}"}, False, "could not be read")
    gate_e2e("Copilot reviewed the head itself -> GREEN", dict(cop_routes, **{"pulls/1/reviews": [dict(COP_O, commit_id=M)]}),
             True, "automatic review landed")
    gate_e2e("main-only merge covered, a thread unanswered -> RED", dict(cop_routes, **{
        "pulls/1/comments": [_comment(1)], "pulls/1": dict(MPR, base={"ref": "main"}, review_comments=1)}), False, "1 of 1 thread(s)")
    gate_e2e("NEG: main-only merge covered, the thread answered -> GREEN", dict(cop_routes, **{
        "pulls/1/comments": [_comment(1), _comment(11, PERSON, 1)], "pulls/1": dict(MPR, base={"ref": "main"}, review_comments=2)}),
        True, "answered by a person: 1")

    # ── the REVIEW-ON-CHANGE lane (step 1): request ONE review exactly when the head owes one.
    RPR = dict(MPR, node_id="PR_node", head={"sha": M, "repo": {"full_name": "Systemorph/MeshWeaver"}})
    COVERED = copilot_carry_over(M, NEW_CMP, OLD_CMP, COP_O)
    CHANGED = copilot_carry_over(M, dict(NEW_CMP, files=files(PATCH_RESOLVED)), OLD_CMP, COP_O)
    def req_case(name, expect, pr, reviews, carry, pending=False, says=""):
        nonlocal failures
        action, why = review_request_action(pr, reviews, carry, "Systemorph/MeshWeaver", pending)
        ok = action == expect and says in why
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} request: {name:51} expected={expect} got={action} ({why[:70]})")
    req_case("main-only merge since the review -> NO request, logged", "none", RPR, [COP_O], COVERED, says="ONLY THE DEFAULT BRANCH")
    req_case("NEG: a real change since the review -> request", "request", RPR, [COP_O], CHANGED, says="own diff changed")
    req_case("NEG: no reference head at all -> request", "request", RPR, [], None, says="no earlier head")
    req_case("already requested and pending -> none (idempotent)", "none", RPR, [COP_O], CHANGED, pending=True, says="already requested")
    req_case("the head is already reviewed -> none (idempotent)", "none", RPR, [dict(COP_O, commit_id=M)], CHANGED, says="already reviewed")
    req_case("a refusal ON the head is Copilot's answer -> none (never re-asks a refusal)", "none", RPR,
             [COP_O, dict(_review(REFUSAL_QUOTA, rid=82), commit_id=M)], CHANGED, says="ANSWERED head")
    req_case("draft -> none (the ruleset decides drafts)", "none", dict(RPR, draft=True), [COP_O], CHANGED, says="draft")
    req_case("closed -> none", "none", dict(RPR, state="closed"), [COP_O], CHANGED, says="closed")
    req_case("an App's own pull request -> none (Copilot does not review it)", "none",
             dict(RPR, user={"login": "dependabot[bot]", "type": "Bot", "id": 49699333}), [], CHANGED, says="opened by the App")
    req_case("fork -> manual, never a silent skip", "manual",
             dict(RPR, head={"sha": M, "repo": {"full_name": "someone/MeshWeaver"}}), [COP_O], CHANGED, says="fork")
    COP_ACTOR = {"login": "Copilot", "type": "Bot", "id": REVIEWER_ACCOUNT_ID}
    for name, timeline, expect in [
        ("pending: requested, nothing since", [{"event": "review_requested", "requested_reviewer": COP_ACTOR, "created_at": "2026-10-24T10:00:00Z"}], True),
        ("pending: requested, then reviewed -> not pending",
         [{"event": "review_requested", "requested_reviewer": COP_ACTOR, "created_at": "2026-10-24T10:00:00Z"},
          {"event": "reviewed", "user": REVIEWER_REVIEW_USER, "submitted_at": "2026-10-24T10:02:00Z"}], False),
        ("pending: reviewed, then requested again -> pending",
         [{"event": "reviewed", "user": REVIEWER_REVIEW_USER, "submitted_at": "2026-10-24T10:02:00Z"},
          {"event": "review_requested", "requested_reviewer": COP_ACTOR, "created_at": "2026-10-24T10:05:00Z"}], True),
        ("pending: requested, then removed -> not pending",
         [{"event": "review_requested", "requested_reviewer": COP_ACTOR, "created_at": "2026-10-24T10:00:00Z"},
          {"event": "review_request_removed", "requested_reviewer": COP_ACTOR, "created_at": "2026-10-24T10:01:00Z"}], False),
        ("pending: a PERSON requested -> not Copilot's", [{"event": "review_requested", "requested_reviewer": PERSON, "created_at": "2026-10-24T10:00:00Z"}], False),
        ("pending: empty timeline", [], False),
    ]:
        ok = copilot_review_pending(timeline) == expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={expect}")
    ANSWER_OK = {"data": {"requestReviews": {"pullRequest": {"number": 1, "reviewRequests": {"nodes": [
        {"requestedReviewer": {"__typename": "Bot", "id": COPILOT_BOT_NODE_ID, "login": "copilot-pull-request-reviewer"}}]}}}}}
    ANSWER_EMPTY = {"data": {"requestReviews": {"pullRequest": {"number": 1, "reviewRequests": {"nodes": []}}}}}
    class _ReqGh(_RestStub):
        def __init__(self, routes, answer):
            super().__init__(routes)
            self.answer, self.mutations = answer, []
        def graphql(self, query, variables):
            self.mutations.append((query, dict(variables)))
            if isinstance(self.answer, Exception):
                raise self.answer
            return self.answer
    req_routes = dict(cop_routes, **{"pulls/1": dict(RPR, base={"ref": "main"}), "issues/1/timeline": []})
    for name, routes, answer, before, expect_rc, expect_mutations in [
        ("lane: real change -> ONE requestReviews with the bot id, rc 0",
         dict(req_routes, **{f"compare/{sha('b')}...{M}": dict(NEW_CMP, files=files(PATCH_RESOLVED))}), ANSWER_OK, O, 0, 1),
        ("lane NEG: main-only merge -> no mutation, rc 0", req_routes, ANSWER_OK, O, 0, 0),
        ("lane: answer lists no Copilot request -> RED", dict(req_routes, **{f"compare/{sha('b')}...{M}": dict(NEW_CMP, files=files(PATCH_RESOLVED))}),
         ANSWER_EMPTY, O, 1, 1),
        ("lane: the mutation refused -> RED, named", dict(req_routes, **{f"compare/{sha('b')}...{M}": dict(NEW_CMP, files=files(PATCH_RESOLVED))}),
         ReadError("GraphQL call failed (1): Resource not accessible by integration"), O, 1, 1),
        ("lane: no landed review, previous head main-only -> no mutation", dict(req_routes, **{"pulls/1/reviews": []}), ANSWER_OK, O, 0, 0),
        ("lane NEG: no landed review, no previous head -> request", dict(req_routes, **{"pulls/1/reviews": []}), ANSWER_OK, None, 0, 1),
        ("lane: already pending -> no mutation (idempotent)", dict(req_routes, **{
            f"compare/{sha('b')}...{M}": dict(NEW_CMP, files=files(PATCH_RESOLVED)),
            "issues/1/timeline": [{"event": "review_requested", "requested_reviewer": COP_ACTOR, "created_at": "2026-10-24T10:00:00Z"}]}),
         ANSWER_OK, O, 0, 0),
    ]:
        stub = _ReqGh(routes, answer)
        # Captured, so a red case's `::error::` line is asserted here instead of becoming an annotation
        # on the self-test step of every run.
        printed = io.StringIO()
        with contextlib.redirect_stdout(printed):
            rc = run_request_review("Systemorph/MeshWeaver", 1, before, gh=stub)
        if rc and "::error::the Copilot review of #1 was NOT requested" not in printed.getvalue():
            rc = -1
        bot_ok = all(v.get("bot") == COPILOT_BOT_NODE_ID and v.get("pr") == "PR_node" and "botIds: [$bot]" in q and "union: true" in q
                     for q, v in stub.mutations)
        ok = rc == expect_rc and len(stub.mutations) == expect_mutations and bot_ok
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} rc={rc} mutations={len(stub.mutations)}")
    # ── the SWEEP (#7142 review): a real-change push H2 found a review pending, so it requested nothing;
    # that review then landed on the EARLIER head O. No event follows — the sweep is what re-judges it.
    RACE = [{"event": "review_requested", "requested_reviewer": COP_ACTOR, "created_at": "2026-10-24T10:00:00Z"},
            {"event": "reviewed", "user": REVIEWER_REVIEW_USER, "submitted_at": "2026-10-24T10:06:00Z"}]
    race_routes = dict(req_routes, **{f"compare/{sha('b')}...{M}": dict(NEW_CMP, files=files(PATCH_RESOLVED)),
                                      "issues/1/timeline": RACE})
    for name, routes, expect_mutations in [
        ("sweep: pending review landed on the EARLIER head -> requests for this head", race_routes, 1),
        ("sweep NEG: the review is still pending -> no mutation", dict(race_routes, **{"issues/1/timeline": RACE[:1]}), 0),
        ("sweep NEG: the landed review covers the head (main-only) -> no mutation", dict(req_routes, **{"issues/1/timeline": RACE}), 0),
        ("sweep NEG: no Copilot review has landed at all -> no mutation", dict(race_routes, **{"pulls/1/reviews": []}), 0),
        ("sweep NEG: the head is reviewed -> no mutation", dict(race_routes, **{"pulls/1/reviews": [dict(COP_O, commit_id=M)]}), 0),
    ]:
        stub = _ReqGh(dict(routes, **{"pulls?state=open": [{"number": 1, "draft": False}, {"number": 2, "draft": True}]}), ANSWER_OK)
        printed = io.StringIO()
        with contextlib.redirect_stdout(printed):
            rc = run_request_review_sweep("Systemorph/MeshWeaver", gh=stub)
        ok = rc == 0 and len(stub.mutations) == expect_mutations and not any(c.startswith("pulls/2") for c in stub.calls)
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} rc={rc} mutations={len(stub.mutations)}")
    # The rotation: 41 open pull requests -> two windows; across consecutive slots every number is
    # judged (a fixed "newest 40" never reached #1 — the negative control is the old slice).
    many = list(range(1, 42))
    seen = set(sweep_window(many, 0)) | set(sweep_window(many, 900))
    ok = (seen == set(many) and len(sweep_window(many, 0)) <= SWEEP_MAX_PRS
          and 1 not in set(many[-SWEEP_MAX_PRS:]) and sweep_window(many[:5], 900) == many[:5])
    failures += 0 if ok else 1
    print(f"self-test {'ok' if ok else 'FAIL':4} {'sweep: >40 open PRs -> the window rotates, every PR judged':60}")
    stub = _ReqGh({}, ANSWER_OK)
    printed = io.StringIO()
    with contextlib.redirect_stdout(printed):
        rc = run_request_review_sweep("Systemorph/MeshWeaver", gh=stub)
    ok = rc == 1 and "cannot list open pull requests" in printed.getvalue()
    failures += 0 if ok else 1
    print(f"self-test {'ok' if ok else 'FAIL':4} {'sweep: an unreadable listing -> RED, named':60} rc={rc}")

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
        def __init__(self, post_fails, readback, refuse_suffix=None):
            self.post_fails, self.readback, self.refuse_suffix, self.posted = post_fails, readback, refuse_suffix, []
        def post(self, path):
            self.posted.append(path)
            if self.post_fails or (self.refuse_suffix and path.endswith(self.refuse_suffix)):
                raise ReadError(f"POST {path} failed (403)")
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
    # The ENDPOINT each caller posts. A zero-job run (cancelled while pending) refuses
    # `rerun-failed-jobs` with 403 and accepts `rerun` (#6315, run 37807892371): the stub refuses
    # exactly what GitHub refused, and the read-back says the run is still completed — so the wrong
    # endpoint RAISES, as it did in production, and the right one re-runs.
    zero_job = {"status": "completed", "conclusion": "cancelled"}
    for name, endpoint, expect_path, expect in [
        ("endpoint: default is rerun-failed-jobs (the stage advance)", None, "actions/runs/7/rerun-failed-jobs", True),
        ("endpoint: the refresh re-runs the WHOLE run", RERUN_WHOLE_RUN, "actions/runs/7/rerun", True),
    ]:
        stub = _StubGh(False, None)
        got = post_rerun(stub, 7) if endpoint is None else post_rerun(stub, 7, endpoint)
        ok = got == expect and stub.posted == [expect_path]
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} posted={stub.posted}")
    for name, endpoint, expect in [
        ("zero-job run: rerun-failed-jobs is refused -> RAISES (the #6315 red)", RERUN_FAILED_JOBS, "raise"),
        ("zero-job run: the whole-run rerun is accepted -> True", RERUN_WHOLE_RUN, True),
    ]:
        try:
            got = post_rerun(_StubGh(False, zero_job, refuse_suffix="/rerun-failed-jobs"), 7, endpoint)
        except ReadError:
            got = "raise"
        ok = got == expect
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} expected={expect} got={got}")
    try:
        post_rerun(_StubGh(False, None), 7, "rerun-everything")
        got = "accepted"
    except ValueError:
        got = "refused"
    failures += 0 if got == "refused" else 1
    print(f"self-test {'ok' if got == 'refused' else 'FAIL':4} {'endpoint: an unknown endpoint is refused':60} got={got}")
    adv_case("closed PR -> none", "none", dict(open_pr, state="closed"), done_run, failed_gate, GREEN_S, "closed")

    # ── the SELF-REFRESH: a green verdict branch protection does not read re-runs the one it does.
    ref_pr = {"number": 4649, "state": "open", "head": {"sha": HEAD, "repo": {"full_name": "Systemorph/MeshWeaver"}}}
    red_read = {"id": 7, "event": "pull_request", "head_sha": HEAD, "status": "completed", "conclusion": "failure",
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
    ref_case("#6315: read run cancelled while pending (zero jobs) -> rerun the WHOLE run", "rerun",
             "pull_request_review", dict(red_read, conclusion="cancelled", run_started_at="2026-10-07T09:59:59Z"),
             says="whole run")
    ref_case("this run IS the pull_request run -> none (no loop)", "none", "pull_request", red_read, says="IS a `pull_request`")
    ref_case("merge_group entry -> none", "none", "merge_group", red_read, says="merge-queue")
    ref_case("#7187: read run in flight -> WAIT for it, never skip", "wait", "pull_request_review_comment",
             dict(red_read, status="in_progress", conclusion=None), says="letting it finish")
    for status in NOT_STARTED_RUN_STATUSES:
        ref_case(f"read run {status} (not started, maybe held by THIS run's group) -> none, never wait", "none",
                 "pull_request_review", dict(red_read, status=status, conclusion=None, run_started_at=None),
                 says="has not started")
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

    # ── the refresh CALLER end to end (#6329 review): drive run_refresh itself through a stub adapter and
    # assert the POST it issues. Asserting refresh_action's wording alone would stay green if the call
    # site dropped RERUN_WHOLE_RUN and fell back to `rerun-failed-jobs` — the #6315 403. The stub refuses
    # exactly that endpoint, as GitHub did for a zero-job run.
    class _RefreshGh:
        def __init__(self, candidate):
            self.candidate, self.posted = candidate, []
        def api(self, path):
            if path == "actions/runs/99":
                return {"id": 99, "workflow_id": 5}
            if path == "pulls/4649":
                return ref_pr
            if path.startswith("actions/workflows/5/runs?"):
                return {"workflow_runs": [self.candidate]}
            if path == f"actions/runs/{self.candidate['id']}":
                return self.candidate
            raise ReadError(f"unexpected GET {path}")
        def post(self, path):
            self.posted.append(path)
            if path.endswith("/rerun-failed-jobs"):
                raise ReadError(f"POST {path} failed (403)")
    for name, candidate, expect_rc, expect_posted in [
        ("run_refresh posts the WHOLE-run rerun for a red read run", red_read, 0, ["actions/runs/7/rerun"]),
        ("run_refresh: zero-job cancelled read run -> whole-run rerun",
         dict(red_read, conclusion="cancelled"), 0, ["actions/runs/7/rerun"]),
        ("run_refresh: read run already green -> posts nothing", dict(red_read, conclusion="success"), 0, []),
    ]:
        stub = _RefreshGh(candidate)
        rc = run_refresh("Systemorph/MeshWeaver", 4649, "pull_request_review_comment", "99", "2026-10-07T10:00:00Z", gh=stub)
        ok = rc == expect_rc and stub.posted == expect_posted
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {name:60} rc={rc} posted={stub.posted}")

    failures += duplicate_sweep_self_test(HEAD, ref_pr, red_read)
    failures += outage_self_test(HEAD, ref_pr, red_read)
    failures += pending_self_test(HEAD)

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


def duplicate_sweep_self_test(HEAD: str, ref_pr: dict, red_read: dict) -> int:
    """MeshWeaver.Plugins#4276 — an OLDER failed duplicate of this check on the same head is re-run after
    a GREEN verdict, whatever event produced either; the negative controls prove the sweep can say no."""
    failures = 0

    def check(name, ok, detail=""):
        nonlocal failures
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} sweep: {name:70} {detail}")

    verdict_at = "2026-10-26T18:37:30Z"
    own_created = "2026-10-26T14:47:41Z"
    red_job = {"name": "review-answered / Automatic review answered", "status": "completed", "conclusion": "failure"}
    green_job = dict(red_job, conclusion="success")
    refresh_job = {"name": "review-answered / Refresh the verdict branch protection reads", "status": "completed",
                   "conclusion": "skipped"}
    dup = {"id": 40343008573, "event": "pull_request_review", "head_sha": HEAD, "status": "completed",
           "conclusion": "failure", "run_attempt": 1, "created_at": "2026-10-26T14:47:52Z",
           "run_started_at": "2026-10-26T14:47:52Z", "html_url": "u"}
    for name, run, jobs, expect, says in [
        ("#4276: older review-event red, verdict GREEN later -> rerun", dup, [red_job, refresh_job], "rerun", "re-running"),
        ("older comment-event red -> rerun", dict(dup, event="pull_request_review_comment"), [red_job], "rerun", "re-running"),
        ("core's lane-prefixed and bare job names both count", dup,
         [dict(red_job, name="Automatic review answered"), dict(green_job, name="lane / Automatic review answered")],
         "rerun", "re-running"),
        ("a timed-out review job is a red too", dup, [dict(red_job, conclusion="timed_out")], "rerun", "timed_out"),
        ("NEG: duplicate already green -> none", dict(dup, conclusion="success"), [green_job], "none", "already reads success"),
        ("NEG: run red only in its REFRESH job (review check green) -> none", dup,
         [green_job, dict(refresh_job, conclusion="failure")], "none", "already reads success"),
        ("NEG: red but started AFTER the verdict -> none", dict(dup, run_started_at="2026-10-26T18:40:00Z"), [red_job],
         "none", "not before this verdict"),
        ("NEG: red on an OLDER head -> none", dict(dup, head_sha="b" * 40), [red_job], "none", "not the current head"),
        ("NEG: zero-job cancelled run (replaced while pending) -> none", dict(dup, conclusion="cancelled"), [],
         "none", "zero jobs"),
        ("NEG: this very run -> none", dict(dup, id=99), [red_job], "none", "is this run"),
        ("review job still in flight -> wait", dict(dup, status="in_progress", conclusion=None),
         [dict(red_job, status="in_progress", conclusion=None)], "wait", "letting it finish"),
        ("red in an OLDER run still finishing -> wait (acyclic)", dict(dup, status="in_progress", conclusion=None,
                                                                   created_at="2026-10-26T14:40:00Z"), [red_job],
         "wait", "needs it completed"),
        ("red+green in a NEWER run still finishing -> none, never a mutual wait",
         dict(dup, status="in_progress", conclusion=None),
         [red_job, dict(green_job, name="lane / Automatic review answered")], "none", "retry listener"),
        ("only-red NEWER run still finishing (its refresh is skipped) -> wait, cannot cycle",
         dict(dup, status="in_progress", conclusion=None), [red_job], "wait", "waits on nothing"),
        ("#7257: a re-run replaced while pending — the EARLIER attempt's red is judged -> rerun",
         dict(dup, run_attempt=2, conclusion="cancelled"), [red_job], "rerun", "re-running"),
        ("not started -> none", dict(dup, status="pending", conclusion=None), [], "none", "has not started"),
    ]:
        action, why = duplicate_red_action(run, jobs, 99, own_created, verdict_at, HEAD)
        check(name, action == expect and says in why, f"expected={expect} got={action} ({why[:50]})")

    # end to end: the #4276 head replayed through run_refresh — a GREEN `pull_request` verdict (the run a
    # person re-ran by hand at 18:36Z) re-runs the three older reds of the other events, and only those.
    own = {"id": 99, "workflow_id": 5, "created_at": own_created}
    zero = dict(dup, id=40343005656, event="pull_request_review_comment", conclusion="cancelled")
    reds = [dict(dup, id=40342994248, event="pull_request_review_comment"), dup,
            dict(dup, id=40343010678, event="pull_request_review_comment")]
    green_pr = dict(red_read, id=99, conclusion="success", created_at=own_created)

    class _SweepGh:
        def __init__(self, runs, jobs_by_id, pr=None):
            self.runs, self.jobs_by_id, self.pr, self.posted, self.reads = runs, jobs_by_id, pr or ref_pr, [], []
        def api(self, path):
            self.reads.append(path)
            if path == "actions/runs/99":
                return own
            if path == "pulls/4649":
                return self.pr
            if path.startswith("actions/workflows/5/runs?"):
                return {"total_count": len(self.runs), "workflow_runs": self.runs}
            for rid, jobs in self.jobs_by_id.items():
                if path.startswith(f"actions/runs/{rid}/jobs"):
                    return {"jobs": jobs}
            raise ReadError(f"unexpected GET {path}")
        def post(self, path):
            self.posted.append(path)
    all_red = {r["id"]: [red_job, refresh_job] for r in reds}
    all_red[zero["id"]] = []
    for name, event, runs, jobs, pr, expect_rc, expect_posted in [
        ("#4276: pull_request verdict GREEN -> ONE re-run per group (oldest comment red + the review red)",
         "pull_request", [green_pr, zero] + reds, all_red, None, 0,
         ["actions/runs/40342994248/rerun", "actions/runs/40343008573/rerun"]),
        ("comment verdict GREEN -> the pull_request red first; the review red (same group) deferred",
         "pull_request_review_comment", [red_read, dup, dict(dup, id=99, event="pull_request_review_comment")],
         {dup["id"]: [red_job]}, None, 0, ["actions/runs/7/rerun"]),
        ("#7257: a run already PENDING in the gate group -> no re-run into it (it would be replaced)",
         "pull_request", [green_pr, dup, dict(dup, id=555, status="pending", conclusion=None, run_started_at=None)],
         {dup["id"]: [red_job]}, None, 0, []),
        ("NEG: every duplicate already green -> posts nothing", "pull_request",
         [green_pr] + [dict(r, conclusion="success") for r in reds], {}, None, 0, []),
        ("NEG: a fork pull request -> no sweep, posts nothing", "pull_request", [green_pr] + reds, all_red,
         dict(ref_pr, head={"sha": HEAD, "repo": {"full_name": "someone/MeshWeaver"}}), 0, []),
        ("NEG: closed pull request -> posts nothing", "pull_request", [green_pr] + reds, all_red,
         dict(ref_pr, state="closed"), 0, []),
    ]:
        stub = _SweepGh(runs, jobs, pr)
        with contextlib.redirect_stdout(io.StringIO()):
            rc = run_refresh("Systemorph/MeshWeaver", 4649, event, "99", verdict_at, gh=stub, wait_s=0)
        check(name, rc == expect_rc and sorted(stub.posted) == sorted(expect_posted), f"rc={rc} posted={stub.posted}")
    stub = _SweepGh([green_pr] + reds, all_red)
    with contextlib.redirect_stdout(io.StringIO()):
        run_refresh("Systemorph/MeshWeaver", 4649, "pull_request", "99", verdict_at, gh=stub, wait_s=0)
    check("no job listing is read for a run that is already green",
          not any(p.startswith("actions/runs/99/jobs") for p in stub.reads), f"reads={len(stub.reads)}")
    # A truncated run listing is never judged — an unread page could hold the blocking red.
    stub = _SweepGh([green_pr] + reds, all_red)
    stub.api_orig = stub.api
    stub.api = lambda path: ({"total_count": 101, "workflow_runs": stub.runs}
                             if path.startswith("actions/workflows/5/runs?") else stub.api_orig(path))
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_refresh("Systemorph/MeshWeaver", 4649, "pull_request", "99", verdict_at, gh=stub, wait_s=0)
    check("NEG: truncated run listing -> RED, nothing posted", rc == 1 and stub.posted == [], f"rc={rc} posted={stub.posted}")
    # A duplicate still in flight past the wait -> RED, named; the decided reds are still re-run.
    flying = dict(dup, id=40342994248, status="in_progress", conclusion=None)
    stub = _SweepGh([green_pr, flying, reds[2]], {flying["id"]: [dict(red_job, status="in_progress", conclusion=None)],
                                                   reds[2]["id"]: [red_job]})
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_refresh("Systemorph/MeshWeaver", 4649, "pull_request", "99", verdict_at, gh=stub, wait_s=0)
    check("in flight past the wait -> RED, the decided red still re-run",
          rc == 1 and stub.posted == [f"actions/runs/{reds[2]['id']}/rerun"], f"rc={rc} posted={stub.posted}")

    # ── #7257 review, thread 1: BOTH refreshes active — no cycle between the two decision paths.
    # X = an OLDER comment run (core shape): its bare review job RED, its lane review job GREEN, its lane
    # refresh running. P = a NEWER `pull_request` run: review jobs GREEN, refresh running. P may wait on X
    # (older, red, still finishing); X must NOT wait on P, whose review check-runs are done.
    class _TwoGh(_SweepGh):
        def __init__(self, own_run, runs, jobs_by_id):
            super().__init__(runs, jobs_by_id)
            self.own_run = own_run
        def api(self, path):
            if path == f"actions/runs/{self.own_run['id']}":
                return dict(self.own_run, workflow_id=5)
            return super().api(path)
    x = dict(dup, id=601, event="pull_request_review_comment", status="in_progress", conclusion=None,
             created_at="2026-10-26T14:40:00Z")
    p_run = dict(red_read, id=602, status="in_progress", conclusion=None, created_at="2026-10-26T14:50:00Z",
                 run_started_at="2026-10-26T14:50:00Z")
    lane_refresh = {"name": "lane / Refresh the verdict branch protection reads", "status": "in_progress", "conclusion": None}
    x_jobs = [dict(red_job, name="Automatic review answered"), dict(green_job, name="lane / Automatic review answered"),
              lane_refresh]
    p_jobs = [dict(green_job, name="Automatic review answered"), dict(green_job, name="lane / Automatic review answered"),
              lane_refresh]
    x_side = in_flight_policy(p_run, p_jobs, x["created_at"])
    p_side = in_flight_policy(x, x_jobs, p_run["created_at"])
    check("cycle: X (older) does NOT wait on P's refresh", x_side is not None and x_side[0] == "none", f"{x_side}")
    check("cycle: P (newer) may wait on X (older, red, finishing)", p_side is not None and p_side[0] == "wait", f"{p_side}")
    check("NEG cycle: a review job still in flight IS waited for", (in_flight_policy(
        p_run, [dict(green_job, status="in_progress", conclusion=None)], x["created_at"]) or ("",))[0] == "wait")
    check("NEG cycle: a NEWER run whose review jobs disagree is NOT waited for", (in_flight_policy(
        p_run, [red_job, dict(green_job, name="lane / Automatic review answered")], x["created_at"]) or ("",))[0] == "none")
    check("a NEWER run whose review jobs are ALL red is waited for (its refresh is skipped)", (in_flight_policy(
        p_run, [red_job], x["created_at"]) or ("",))[0] == "wait")
    # The INVERSE (#7257 review): X older with both review jobs GREEN, P newer with bare RED + lane GREEN.
    # X's refresh must neither wait on P nor pretend P is fine: it decides at once, and P is the retry
    # listener's on completion (`self_contradicting`, covered under `retry:`).
    p_inv = [dict(red_job, name="Automatic review answered"), dict(green_job, name="lane / Automatic review answered"),
             lane_refresh]
    x_green = dict(x, id=611)
    stub = _TwoGh(x_green, [x_green, dict(p_run, id=612)], {612: p_inv})
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_refresh("Systemorph/MeshWeaver", 4649, "pull_request_review_comment", "611", verdict_at, gh=stub, wait_s=0)
    check("inverse: X's refresh decides at once (P is the retry listener's)", rc == 0 and stub.posted == [],
          f"rc={rc} posted={stub.posted}")
    check("inverse: P completed with its jobs disagreeing IS what the listener re-runs",
          self_contradicting(p_inv[:2]) and not self_contradicting([red_job, red_job]) and not self_contradicting([green_job]))

    # The cancelled SECOND attempt end to end: the latest attempt has zero jobs, attempt 1 is red.
    replaced = dict(dup, id=620, run_attempt=2, conclusion="cancelled")
    stub = _SweepGh([green_pr, replaced], {620: []})
    stub.api_base = stub.api
    stub.api = lambda path: ({"jobs": [red_job]} if path.startswith("actions/runs/620/attempts/1/jobs")
                             else stub.api_base(path))
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_refresh("Systemorph/MeshWeaver", 4649, "pull_request", "99", verdict_at, gh=stub, wait_s=0)
    check("#7257: cancelled 2nd attempt, red 1st attempt -> re-run", rc == 0 and stub.posted == ["actions/runs/620/rerun"],
          f"rc={rc} posted={stub.posted}")
    stub = _SweepGh([green_pr, dict(replaced, run_attempt=1)], {620: []})
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_refresh("Systemorph/MeshWeaver", 4649, "pull_request", "99", verdict_at, gh=stub, wait_s=0)
    check("NEG: a cancelled FIRST attempt (never ran) holds no red -> nothing re-run", rc == 0 and stub.posted == [],
          f"rc={rc} posted={stub.posted}")

    # X's refresh, end to end with NO wait budget: before the fix it waited on P and went RED at the deadline.
    stub = _TwoGh(x, [x, p_run], {602: p_jobs, 601: x_jobs})
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_refresh("Systemorph/MeshWeaver", 4649, "pull_request_review_comment", "601", verdict_at, gh=stub, wait_s=0)
    check("cycle end to end: X's refresh decides at once, never waits on P", rc == 0 and stub.posted == [],
          f"rc={rc} posted={stub.posted}")

    # ── #7257 review, thread 2: ONE re-run per concurrency group, never into an occupied pending slot.
    g1, g2 = dict(dup, id=701), dict(dup, id=702, event="pull_request")
    f1, f2 = dict(dup, id=703, event="pull_request_review_comment"), dict(dup, id=704, event="pull_request_review_comment")
    plan = plan_reruns([(g1, "a"), (g2, "b"), (f1, "c"), (f2, "d")], [], 99)
    check("plan: one re-run per group (gate + feedback), the rest deferred",
          [a for _, a, _ in plan] == ["rerun", "none", "rerun", "none"], f"{[a for _, a, _ in plan]}")
    plan = plan_reruns([(g1, "a"), (f1, "c")], [dict(dup, id=705, status="queued", conclusion=None)], 99)
    check("plan: a pending gate run occupies the slot -> no gate re-run; feedback still re-run",
          [a for _, a, _ in plan] == ["none", "rerun"], f"{[a for _, a, _ in plan]}")
    plan = plan_reruns([(g1, "a")], [dict(dup, id=99, status="pending", conclusion=None)], 99)
    check("NEG plan: this run itself is not a pending occupant", [a for _, a, _ in plan] == ["rerun"])

    # The CHAIN converges: every red eventually gets a fresh evaluation, one re-run per group per refresh.
    def chain(red_ids, stays_red=()):
        world = {r: dict(dup, id=r, event=("pull_request_review_comment" if r % 2 else "pull_request_review"))
                 for r in red_ids}
        world[900] = dict(green_pr, id=900, created_at="2026-10-26T18:36:00Z", run_started_at="2026-10-26T18:36:00Z")
        jobs = {r: [red_job] for r in red_ids}
        refreshers, posted_all, worst, step = [900], [], 0, 0
        while refreshers and step < 20:
            own_id = refreshers.pop(0)
            step += 1
            stub = _TwoGh(world[own_id], list(world.values()), jobs)
            with contextlib.redirect_stdout(io.StringIO()):
                run_refresh("Systemorph/MeshWeaver", 4649, world[own_id]["event"], str(own_id),
                            f"2026-10-26T19:{step:02d}:30Z", gh=stub, wait_s=0)
            ids = [int(path.split("/")[2]) for path in stub.posted]
            lanes = [concurrency_lane(world[i]["event"]) for i in ids]
            worst = max([worst] + [lanes.count(lane) for lane in set(lanes)])
            for i in ids:
                posted_all.append(i)
                world[i] = dict(world[i], run_attempt=2, run_started_at=f"2026-10-26T19:{step:02d}:40Z",
                                conclusion="failure" if i in stays_red else "success")
                if i not in stays_red:
                    jobs.pop(i, None)
                    refreshers.append(i)
        return posted_all, worst
    red_ids = [801, 802, 803, 804, 805]
    posted_all, worst = chain(red_ids)
    check("chain: every red is re-run exactly once, at most one per group per refresh",
          sorted(posted_all) == red_ids and worst <= 1, f"posted={posted_all} worst={worst}")
    posted_all, worst = chain([802, 804, 806], stays_red={802})
    check("NEG chain: a re-run that stays RED (truly unanswered) refreshes nothing — the chain stops there",
          posted_all == [802], f"posted={posted_all}")
    return failures


def pending_self_test(HEAD: str) -> int:
    """Policy review-pending-while-answering + review-answer-deadline: the verdict writer reports PENDING
    (an in_progress check-run, no conclusion) while threads await answers or the head awaits its review
    within the deadlines, FAILURE past a deadline or when it cannot measure, SUCCESS when answered. Every
    case drives the code through a FAKE GitHub adapter — nothing here reaches GitHub. Returns the number
    of failed cases."""
    failures = 0

    def check(name, ok, detail=""):
        nonlocal failures
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} pending: {name:62} {'' if ok else detail}")

    T0 = "2026-10-30T10:00:00Z"            # when Copilot's thread opened
    FRESH = "2026-10-30T12:00:00Z"         # 2 h later
    LATE = "2026-10-31T10:00:01Z"          # 24 h + 1 s later
    ONE, ANSWER = _comment(1, at=T0), _comment(11, PERSON, 1, "2026-10-30T13:00:00Z")
    TWO = _comment(2, at=T0)
    review = dict(_review(at=T0), commit_id=HEAD)

    def verdict_of(comments, reviews=(review,), count=None):
        pr = dict(_pr(len(comments) if count is None else count), head={"sha": HEAD})
        return evaluate(pr, list(reviews), comments, NO_WAIVER)

    # ── the pure reading
    unanswered = verdict_of([ONE, TWO, ANSWER])
    r = reported_state(unanswered, FRESH, T0)
    check("unanswered + fresh -> PENDING (not failure)", r.state == "pending" and r.kind == "answering"
          and "1 of 2 thread(s) unanswered" in r.title and r.red_at == "2026-10-31T10:00:00Z", f"{r}")
    check("the pending title names the deadline that applies (a thread's, not the review's)",
          "if a thread is still unanswered (24 h" in r.title and "review has not landed" not in r.title, r.title)
    owes = dataclasses.replace(unanswered, review_missing="head owes its own Copilot review", reasons=("head owes …",) + unanswered.reasons)
    r2 = reported_state(owes, FRESH, T0)
    check("NEG: when the head's review is due first, the title names THAT deadline",
          r2.state == "pending" and "review has not landed (6 h" in r2.title and r2.red_at == "2026-10-30T16:00:00Z", r2.title)
    # Policy app-marked-answer-counts: the App's MARKED reply answers its own thread; nothing else a bot posts does.
    APP = {"login": "systemorph-com[bot]", "type": "Bot", "id": APP_ANSWER_ACCOUNT_ID}
    APP_RENAMED = {"login": "systemorph[bot]", "type": "Bot", "id": APP_ANSWER_ACCOUNT_ID}
    OTHER = {"login": "some-other-app[bot]", "type": "Bot", "id": 99}
    marked = "The null check is upstream.\n<!-- pr-relay:answer comment=1 -->"

    def app_reply(cid, user, body):
        return dict(_comment(cid, user, 1, "2026-10-30T13:00:00Z"), body=body)
    for name, reply, want in [
        ("the App's MARKED reply answers its thread", app_reply(21, APP, marked), True),
        ("the App's marked reply under its NEW login answers too", app_reply(21, APP_RENAMED, marked), True),
        ("NEG: the App's UNMARKED reply answers nothing", app_reply(21, APP, "The null check is upstream."), False),
        ("NEG: a marker naming ANOTHER thread answers nothing", app_reply(21, APP, marked.replace("comment=1", "comment=2")), False),
        ("NEG: a MARKED reply from any other bot answers nothing", app_reply(21, OTHER, marked), False),
        ("NEG: a marked reply from a bot posing as the App's login with a different type answers nothing",
         app_reply(21, dict(APP, type="User"), marked), False),
    ]:
        _r, open_ = reviewer_threads([ONE, reply])
        check(name, (not open_) == want, f"unanswered={[c.get('id') for c in open_]}")
    r = reported_state(unanswered, LATE, T0)
    check("unanswered past the 24 h deadline -> FAILURE", r.state == "failure" and r.kind == "answer-overdue", f"{r}")
    r = reported_state(verdict_of([ONE, ANSWER]), LATE, T0)
    check("all answered -> SUCCESS (even long after)", r.state == "success", f"{r}")
    incomplete = verdict_of([ONE], count=5)
    r = reported_state(incomplete, FRESH, T0)
    check("unmeasurable (short listing) -> FAILURE, never pending", r.state == "failure" and r.kind == "unmeasurable", f"{r}")
    no_review = verdict_of([], reviews=())
    r = reported_state(no_review, "2026-10-30T15:59:59Z", T0)
    check("review not landed, inside 6 h -> PENDING awaiting review", r.state == "pending" and r.kind == "awaiting-review", f"{r}")
    r = reported_state(no_review, "2026-10-30T16:00:00Z", T0)
    check("review not landed past 6 h -> FAILURE (the head owes its review)", r.state == "failure" and r.kind == "review-overdue", f"{r}")
    refused = verdict_of([], reviews=(dict(review, body=REFUSAL_QUOTA),))
    r = reported_state(refused, FRESH, T0)
    check("a refusal -> FAILURE (nothing that arrives can change it)", r.state == "failure" and r.kind == "refused", f"{r}")
    odd = dataclasses.replace(unanswered, reasons=unanswered.reasons + ("a reason nobody classified",))
    r = reported_state(odd, FRESH, T0)
    check("NEG: an unrecognised red reason -> FAILURE (fails closed)", r.state == "failure" and r.kind == "unmeasurable", f"{r}")
    r = reported_state(unanswered, "not-a-time", T0)
    check("NEG: an unreadable clock -> FAILURE (fails closed)", r.state == "failure", f"{r}")
    pend_body = verdict_body("Automatic review answered", HEAD, reported_state(unanswered, FRESH, T0), "s", FRESH, FRESH)
    check("a PENDING body is in_progress with NO conclusion (never neutral/skipped)",
          pend_body["status"] == "in_progress" and "conclusion" not in pend_body, f"{pend_body}")
    fail_body = verdict_body("Automatic review answered", HEAD, reported_state(unanswered, LATE, T0), "s", LATE, LATE)
    check("a FAILURE body is completed/failure", fail_body["status"] == "completed" and fail_body["conclusion"] == "failure",
          f"{fail_body}")
    told = verdict_summary(7, unanswered, reported_state(unanswered, FRESH, T0))
    check("the summary names every unanswered thread and says it still blocks",
          "discussion_r2" in told and "discussion_r1" not in told and "still blocks the merge" in told, told[:300])

    # ── the write plan
    mine = lambda cid, read_at, kind="answering", status="in_progress": {   # noqa: E731
        "id": cid, "name": "Automatic review answered", "app": {"slug": ACTIONS_APP_SLUG}, "status": status,
        "started_at": "2026-10-30T10:05:00Z", "external_id": verdict_external_id(read_at, kind)}
    job_shaped = {"id": 9, "name": "Automatic review answered", "app": {"slug": ACTIONS_APP_SLUG}, "status": "completed",
                  "conclusion": "failure", "started_at": "2026-10-30T09:00:00Z", "external_id": ""}
    other_app = dict(mine(8, FRESH), app={"slug": "someone-else"})
    check("verdict_runs: a job's own check-run and another App's are never touched",
          [c["id"] for c in verdict_runs([job_shaped, other_app, mine(5, FRESH)], "Automatic review answered")] == [5])
    check("plan: none on the head -> create", verdict_write_plan([], FRESH)[0] == "create")
    check("plan: an older read on file -> patch it", verdict_write_plan([mine(5, T0)], FRESH)[:2] == ("patch", [5]))
    check("plan: a NEWER read on file -> skip (the last read stands)", verdict_write_plan([mine(5, LATE)], FRESH)[0] == "skip")
    check("first judged = the oldest verdict check-run's start", first_judged([mine(5, T0)], FRESH) == "2026-10-30T10:05:00Z")

    # ── end to end through a fake GitHub
    class _VerdictGh:
        repo = "o/r"

        def __init__(self, comments, reviews=(review,), runs=(), now=FRESH, count=None, refuse_reopen=False,
                     fail_reviews=False, fail_write=False, pulls=()):
            self.comments, self.reviews, self.runs = list(comments), list(reviews), [dict(c) for c in runs]
            self.t = datetime.datetime.strptime(now, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.UTC).timestamp()
            self.count = len(self.comments) if count is None else count
            self.refuse_reopen, self.fail_reviews, self.fail_write = refuse_reopen, fail_reviews, fail_write
            self.writes, self.pulls, self.next_id = [], list(pulls), 500

        def now(self):
            return self.t

        def sleep(self, s):
            self.t += s

        def role(self, login):
            return "write"

        def api(self, path, paginate=False):
            if path == "pulls?state=open&per_page=100":
                return self.pulls
            m = re.fullmatch(r"pulls/(\d+)", path)
            if m:
                return dict(_pr(self.count), number=int(m.group(1)), head={"sha": HEAD})
            if re.fullmatch(r"pulls/\d+/reviews\?per_page=100", path):
                if self.fail_reviews:
                    raise ReadError(f"GET {path} failed (1): gh: Not Found (HTTP 404)")
                return self.reviews
            if re.fullmatch(r"pulls/\d+/comments\?per_page=100", path):
                return self.comments
            if re.fullmatch(r"issues/\d+/events\?per_page=100", path):
                return []
            if path.startswith(f"commits/{HEAD}/check-runs?check_name={DEGRADATION_CHECK_NAME}&"):
                return {"total_count": 0, "check_runs": []}
            if path.startswith(f"commits/{HEAD}/check-runs?check_name=Automatic%20review%20answered&"):
                return {"total_count": len(self.runs), "check_runs": self.runs}
            raise ReadError(f"unexpected GET {path}")

        def write(self, method, path, body):
            self.writes.append((method, path, body))
            if self.fail_write:
                raise ReadError(f"{method} {path} failed (1): gh: Resource not accessible by integration (HTTP 403)")
            if method == "PATCH":
                cid = int(path.rsplit("/", 1)[1])
                run_ = next(c for c in self.runs if c["id"] == cid)
                if self.refuse_reopen and run_["status"] == "completed" and body.get("status") == "in_progress":
                    raise ReadError(f"PATCH {path} failed (1): gh: Validation Failed (HTTP 422)")
                run_.update({k: v for k, v in body.items() if k != "output"})
                return run_
            self.next_id += 1
            made = dict(body, id=self.next_id, app={"slug": ACTIONS_APP_SLUG}, started_at=epoch_stamp(self))
            self.runs.append(made)
            return made

    def drive(g, number=7):
        out = os.path.join(os.environ.get("RUNNER_TEMP") or tempfile.gettempdir(), f"cra-pending-{os.getpid()}.out")
        saved = {k: os.environ.get(k) for k in ("GITHUB_OUTPUT", "GITHUB_STEP_SUMMARY")}
        os.environ["GITHUB_OUTPUT"] = out
        os.environ.pop("GITHUB_STEP_SUMMARY", None)
        try:
            with contextlib.redirect_stdout(io.StringIO()) as printed:
                rc = run_verdict("o/r", number, "Automatic review answered", gh=g)
            written = open(out, encoding="utf-8").read() if os.path.exists(out) else ""
        finally:
            with contextlib.suppress(OSError):
                os.remove(out)
            for k, v in saved.items():
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v
        return rc, written, printed.getvalue()

    g = _VerdictGh([ONE, TWO, ANSWER])
    rc, out, text = drive(g)
    check("e2e: unanswered + fresh -> creates an in_progress check-run, job green",
          rc == 0 and "verdict-state=pending" in out and len(g.writes) == 1 and g.writes[0][0] == "POST"
          and g.writes[0][2]["status"] == "in_progress" and "conclusion" not in g.writes[0][2]
          and "::error::" not in text and "PENDING" in text, f"rc={rc} out={out!r} writes={g.writes}")
    g = _VerdictGh([ONE, TWO, ANSWER, _comment(12, PERSON, 2, "2026-10-30T13:01:00Z")], runs=[mine(5, T0)])
    rc, out, _ = drive(g)
    check("e2e: all answered -> PATCHes the SAME check-run to success",
          rc == 0 and "verdict-state=success" in out and [w[:2] for w in g.writes] == [("PATCH", "check-runs/5")]
          and g.writes[0][2]["conclusion"] == "success", f"rc={rc} writes={g.writes}")
    g = _VerdictGh([ONE, TWO, ANSWER], runs=[mine(5, T0)], now=LATE)
    rc, out, _ = drive(g)
    check("e2e: unanswered past the deadline -> PATCH failure, job RED",
          rc == 1 and "verdict-state=failure" in out and g.writes[0][2]["conclusion"] == "failure", f"rc={rc} writes={g.writes}")
    g = _VerdictGh([ONE], count=4)
    rc, out, _ = drive(g)
    check("e2e: unmeasurable listing -> failure written, job RED",
          rc == 1 and g.writes and g.writes[0][2].get("conclusion") == "failure", f"rc={rc} writes={g.writes}")
    g = _VerdictGh([ONE], fail_reviews=True)
    rc, out, text = drive(g)
    check("e2e: a failed read -> 'could not measure' failure written, job RED",
          rc == 1 and g.writes and g.writes[0][2].get("conclusion") == "failure"
          and "unmeasurable" in g.writes[0][2]["external_id"], f"rc={rc} writes={g.writes}")
    g = _VerdictGh([ONE, TWO, ANSWER], runs=[mine(5, LATE)])
    rc, out, _ = drive(g)
    check("e2e: a newer verdict on file -> writes nothing", rc == 0 and g.writes == [], f"rc={rc} writes={g.writes}")
    g = _VerdictGh([ONE, TWO, ANSWER], fail_write=True)
    rc, out, text = drive(g)
    check("NEG e2e: the check-run cannot be written -> UNPUBLISHED exit, named", rc == VERDICT_UNPUBLISHED_EXIT and "could not be written" in text,
          f"rc={rc} text={text[-300:]}")
    g = _VerdictGh([ONE, TWO, ANSWER], runs=[dict(mine(5, T0, "answered", "completed"), conclusion="success")],
                   refuse_reopen=True)
    rc, out, _ = drive(g)
    old = next(c for c in g.runs if c["id"] == 5)
    check("e2e: success -> pending when GitHub will not re-open: old closed as failure, a new pending one",
          rc == 0 and old["conclusion"] == "failure" and SUPERSEDED_KIND in old["external_id"]
          and any(c["status"] == "in_progress" for c in g.runs if c["id"] != 5), f"runs={g.runs}")
    rc, out, _ = drive(g)
    check("e2e: ... and the next pending verdict PATCHes the new one, creating nothing more",
          rc == 0 and len(g.runs) == 2, f"runs={[(c['id'], c['status']) for c in g.runs]}")

    # #7653 review: the write plan reads AGAIN right before writing — a newer verdict written DURING the evaluation
    # (after the first listing) is never overwritten by this older read.
    class _RacingGh(_VerdictGh):
        def __init__(self, *a, **k):
            super().__init__(*a, **k)
            self.listings = 0

        def api(self, path, paginate=False):
            if path.startswith(f"commits/{HEAD}/check-runs?check_name=Automatic%20review%20answered&"):
                self.listings += 1
                if self.listings == 2:    # a competing evaluation wrote a NEWER pending verdict meanwhile
                    self.runs = [mine(5, LATE)]
            return super().api(path, paginate)
    g = _RacingGh([ONE, TWO, ANSWER, _comment(12, PERSON, 2, "2026-10-30T13:01:00Z")], runs=[mine(5, T0)])
    rc, out, _ = drive(g)
    check("race: a newer verdict written during the evaluation is never overwritten by an older read",
          rc == 0 and g.writes == [] and g.listings == 2, f"rc={rc} writes={g.writes} listings={g.listings}")
    # ── the sweep: only a pending/unmeasured verdict of a same-repo, non-draft pull request is re-judged
    def pull(n, draft=False, repo="o/r"):
        return {"number": n, "draft": draft, "head": {"sha": HEAD, "repo": {"full_name": repo}}}
    g = _VerdictGh([ONE, ANSWER], runs=[mine(5, T0)], pulls=[pull(7), pull(8, draft=True), pull(9, repo="fork/r")])
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        rc = run_verdict_sweep("o/r", "Automatic review answered", gh=g, now_epoch=0)
    check("sweep: a pending verdict is re-judged (its review answered -> success); draft and fork are not",
          rc == 0 and len(g.writes) == 1 and g.writes[0][2].get("conclusion") == "success", f"rc={rc} writes={g.writes}")
    g = _VerdictGh([ONE], runs=[dict(mine(5, T0, "answered", "completed"), conclusion="success")], pulls=[pull(7)])
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_verdict_sweep("o/r", "Automatic review answered", gh=g, now_epoch=0)
    check("sweep NEG: a completed success is not re-judged (it changes only on an event)", rc == 0 and g.writes == [],
          f"writes={g.writes}")
    absent = _VerdictGh([ONE, ANSWER], runs=[], pulls=[pull(7)])
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_verdict_sweep("o/r", "Automatic review answered", gh=absent, now_epoch=0)
    check("sweep: a head with NO verdict (its event died before writing) is judged and gets one",
          rc == 0 and len(absent.writes) == 1 and absent.writes[0][0] == "POST", f"rc={rc} writes={absent.writes}")
    bot = _VerdictGh([ONE, ANSWER], runs=[], pulls=[dict(pull(7), user={"login": "dependabot[bot]", "type": "Bot"})])
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_verdict_sweep("o/r", "Automatic review answered", gh=bot, now_epoch=0)
    check("sweep NEG: a Dependabot pull request with no writer verdict is left to its job-shaped check",
          rc == 0 and bot.writes == [], f"writes={bot.writes}")
    late = _VerdictGh([], runs=[dict(mine(5, T0, "review-overdue", "completed"), conclusion="failure")], pulls=[pull(7)])
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_verdict_sweep("o/r", "Automatic review answered", gh=late, now_epoch=0)
    check("sweep: a review-overdue red is re-judged — the late review clears it", rc == 0 and late.writes
          and late.writes[0][2].get("conclusion") == "success", f"rc={rc} writes={late.writes}")
    broken = _VerdictGh([ONE, TWO, ANSWER], runs=[mine(5, T0)], pulls=[pull(7)], fail_write=True)
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run_verdict_sweep("o/r", "Automatic review answered", gh=broken, now_epoch=0)
    check("sweep NEG: a verdict it could not publish makes the sweep RED (never a green no-op)", rc == 1, f"rc={rc}")
    check("the verdict writer is a review job (bare and caller-prefixed), so no refresh waits on its run as jobless",
          is_review_check_job(VERDICT_WRITER_JOB) and is_review_check_job(f"lane / {VERDICT_WRITER_JOB}")
          and in_flight_policy({"id": 1, "status": "in_progress", "event": "pull_request_review_comment", "created_at": "b"},
                               [{"name": VERDICT_WRITER_JOB, "status": "completed", "conclusion": "success"}], "a")[0] == "none"
          and not is_review_check_job("Refresh the verdict branch protection reads"))

    # ── the workflows: the writer's job name and its check-run name are chosen by ONE condition
    here = os.path.dirname(os.path.abspath(__file__))
    wf_dir = os.path.join(os.path.dirname(here), "workflows")
    if os.path.isdir(wf_dir) and os.path.exists(os.path.join(wf_dir, "node-repo-review-answered.yml")):
        for wf in ("review-answered.yml", "node-repo-review-answered.yml"):
            with open(os.path.join(wf_dir, wf), encoding="utf-8") as f:
                text = f.read()
            named = re.findall(r"\$\{\{\s*\((.+?)\)\s*&&\s*'" + re.escape(VERDICT_WRITER_JOB) + "'", text)
            chosen = re.findall(r"VERDICT_CHECK_RUN:\s*\$\{\{\s*\((.+?)\)\s*&&", text)
            check(f"{wf}: writer job name and VERDICT_CHECK_RUN share one condition",
                  len(named) == 1 and len(chosen) == 1 and named[0] == chosen[0], f"name={named} env={chosen}")
            check(f"{wf}: the condition excludes forks, Dependabot and the merge queue",
                  bool(named) and "head.repo.full_name == github.repository" in named[0]
                  and "dependabot[bot]" in named[0], f"{named}")
    else:
        print(f"self-test note pending: the workflows are not beside this copy ({wf_dir}) — the workflow check runs in core's tree")
    return failures


def outage_self_test(HEAD: str, ref_pr: dict, red_read: dict) -> int:
    """MeshWeaver#7187 — an API outage is retried, then reported as INFRA, never as a verdict; the review
    wait probes instead of re-reading; the refresh waits for an in-flight read run; the retry listener
    re-runs an outage run and never a verdict. Every case drives the code through a FAKE `gh` (a scripted
    runner or a stub adapter): nothing here reaches GitHub. Returns the number of failed cases."""
    failures = 0

    def check(name, ok, detail=""):
        nonlocal failures
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} outage: {name:62} {detail}")

    # ── classification, on the texts GitHub actually returned
    RATE_403 = ("gh: API rate limit exceeded for installation. If you reach out to GitHub Support for help, please include "
                "the request ID EC20:BE7EC:3A01:C326:6ADDE2F2 and timestamp 2026-10-25 11:07:30 UTC. (HTTP 403)")
    for name, text, expect in [
        ("#7173's 403 'API rate limit exceeded for installation'", RATE_403, "rate-limit"),
        ("a 429", "gh: Too Many Requests (HTTP 429)", "rate-limit"),
        ("a secondary limit", "gh: You have exceeded a secondary rate limit. (HTTP 403)", "secondary-rate-limit"),
        ("a 502", "gh: HTTP 502: Bad Gateway", "server"),
        ("a dropped connection", "Post \"https://api.github.com/graphql\": read tcp: connection reset by peer", "network"),
        ("NEG: a 404 is an answer", "gh: Not Found (HTTP 404)", None),
        ("NEG: a permission 403 is an answer", "gh: Resource not accessible by integration (HTTP 403)", None),
        ("NEG: a refused re-run 403 is an answer", "gh: This workflow run cannot be retried (HTTP 403)", None),
    ]:
        got = classify_api_failure(text)
        check(f"classify: {name}", got == expect, f"expected={expect} got={got}")
    for name, args, expect in [
        ("primary limit waits for its own reset (+5 s)", ("rate-limit", 0, 1000.0, 400.0), 605.0),
        ("primary limit, reset unreadable -> 60 s", ("rate-limit", 0, None, 400.0), 60.0),
        ("secondary limit backs off 60, 120, 240, 300", ("secondary-rate-limit", 3, None, 0.0), 300.0),
        ("a 5xx backs off from 10 s", ("server", 0, None, 0.0), 10.0),
    ]:
        got = outage_delay(*args)
        check(f"delay: {name}", got == expect, f"expected={expect} got={got}")

    # ── the adapter, through a scripted fake `gh`
    class _Clock:
        def __init__(self, t=1000.0):
            self.t, self.slept = t, []
        def now(self):
            return self.t
        def sleep(self, s):
            self.slept.append(s)
            self.t += s

    def runner_of(script, reset_in=120):
        """`script`: a list of (rc, stdout, stderr) the fake answers in order to every NON-rate_limit call."""
        calls = []
        def run_(cmd):
            if cmd[-1] == "rate_limit":
                return subprocess.CompletedProcess(cmd, 0, json.dumps({"resources": {"core": {
                    "remaining": 0, "reset": clock.t + reset_in}, "graphql": {"remaining": 0, "reset": clock.t + reset_in}}}), "")
            calls.append(cmd)
            rc, out, err = script.pop(0) if script else (0, "{}", "")
            return subprocess.CompletedProcess(cmd, rc, out, err)
        return run_, calls

    clock = _Clock()
    run_, calls = runner_of([(1, "", RATE_403), (0, json.dumps({"number": 7}), "")])
    gh = Gh("o/r", runner=run_, sleep=clock.sleep, clock=clock.now, budget_s=300)
    try:
        got = gh.api("pulls/7")
    except (ReadError, ApiOutage) as e:
        got = e
    check("a rate-limited GET waits for the reset, then answers", got == {"number": 7} and clock.slept == [125.0] and len(calls) == 2,
          f"got={got!r} slept={clock.slept}")
    clock = _Clock()
    run_, calls = runner_of([(1, "", RATE_403)] * 5, reset_in=1800)
    gh = Gh("o/r", runner=run_, sleep=clock.sleep, clock=clock.now, budget_s=300)
    try:
        gh.api("pulls/7")
        got = "answered"
    except ApiOutage as e:
        got = f"ApiOutage({e.kind})"
    except ReadError:
        got = "ReadError"
    check("NEG: a reset beyond the budget is an ApiOutage, never a ReadError", got == "ApiOutage(rate-limit)" and clock.slept == [],
          f"got={got} slept={clock.slept}")
    clock = _Clock()
    run_, calls = runner_of([(1, "", "gh: Not Found (HTTP 404)")])
    gh = Gh("o/r", runner=run_, sleep=clock.sleep, clock=clock.now, budget_s=300)
    try:
        gh.api("pulls/7")
        got = "answered"
    except ApiOutage:
        got = "ApiOutage"
    except ReadError:
        got = "ReadError"
    check("NEG: a 404 is a ReadError at once — never waited for", got == "ReadError" and clock.slept == [], f"got={got} slept={clock.slept}")
    # The degrading reader cannot swallow it: read_diff_carry turns a ReadError into "the head owes a review"
    # (a VERDICT) — an outage must pass through it instead.
    clock = _Clock()
    run_, calls = runner_of([(1, "", RATE_403)] * 5, reset_in=1800)
    gh = Gh("o/r", runner=run_, sleep=clock.sleep, clock=clock.now, budget_s=0)
    try:
        got = read_diff_carry(gh, {"head": {"sha": HEAD}, "base": {"ref": "main"}}, "b" * 40, {"id": 1})
        got = f"a Carry ({got.why[:60]}…) — the outage became a verdict"
    except ApiOutage:
        got = "ApiOutage"
    check("an outage passes THROUGH a reader that degrades on ReadError", got == "ApiOutage", f"got={got}")

    # ── the verdict path end to end: run() on an outage -> exit OUTAGE_EXIT + outcome=api-outage, never 1
    out_path = os.path.join(os.environ.get("RUNNER_TEMP") or "/tmp", f"cra-selftest-{os.getpid()}.out")
    saved = {k: os.environ.get(k) for k in ("GITHUB_OUTPUT", "GITHUB_STEP_SUMMARY")}
    os.environ["GITHUB_OUTPUT"] = out_path
    os.environ.pop("GITHUB_STEP_SUMMARY", None)
    try:
        clock = _Clock()
        run_, calls = runner_of([(1, "", RATE_403)] * 5, reset_in=1800)
        gh = Gh("o/r", runner=run_, sleep=clock.sleep, clock=clock.now, budget_s=300)
        with contextlib.redirect_stdout(io.StringIO()) as printed:
            try:
                rc = run("o/r", 7173, None, 0, 0, gh=gh)
            except ApiOutage as e:
                rc = report_outage(e)
        written = ""
        if os.path.exists(out_path):
            with open(out_path, encoding="utf-8") as f:
                written = f.read()
    finally:
        with contextlib.suppress(OSError):
            os.remove(out_path)
        for k, v in saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
    text = printed.getvalue()
    check("#7173: an outage on the verdict read -> INFRA exit 3, not RED 1",
          rc == OUTAGE_EXIT and "outcome=api-outage" in written and "no verdict was taken" in text
          and "cannot say it was answered" not in text, f"rc={rc} output={written.strip()!r}")

    # ── the review wait PROBES (2 calls) and re-reads in full only when the review lands
    MAINTAINER = {"login": "maintainer-x", "type": "User", "id": 7}
    class _WaitGh:
        def __init__(self, land_after_probes, waive_after_probes=10_000):
            self.clock, self.calls, self.probes, self.land = _Clock(), [], 0, land_after_probes
            self.waive = waive_after_probes
        def waived(self):
            return self.probes >= self.waive
        def sleep(self, s):
            self.clock.sleep(s)
        def now(self):
            return self.clock.now()
        def reviews(self):
            landed = dict(_review(), id=5, commit_id=HEAD, submitted_at="2026-10-25T11:00:00Z")
            return [landed] if self.probes >= self.land else []
        def role(self, login):
            return self.api(f"collaborators/{login}/permission")["role_name"]
        def api(self, path, paginate=False):
            self.calls.append(path)
            if path == "pulls/7":
                return dict(_pr(0, [WAIVER_LABEL] if self.waived() else []), number=7, base={"ref": "main"})
            if path.startswith("pulls/7/reviews"):
                if self.calls[-2:-1] != ["pulls/7"]:   # a probe, not the full read (which reads pulls/7 first)
                    self.probes += 1
                return self.reviews()
            if path.startswith("pulls/7/comments"):
                return []
            if path.startswith("issues/7/events"):
                return [_labeled(MAINTAINER)] if self.waived() else []
            if path.startswith("collaborators/"):
                return {"role_name": "admin" if path.startswith(f"collaborators/{MAINTAINER['login']}/") else "write"}
            if path.startswith(f"commits/{HEAD}/check-runs"):
                return {"total_count": 0, "check_runs": []}
            if path.startswith("issues/7/labels"):
                return [{"name": WAIVER_LABEL}] if self.waived() else []
            raise ReadError(f"unexpected GET {path}")
    wg = _WaitGh(land_after_probes=3)
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run("o/r", 7, None, 15, 0, gh=wg)
    full_reads = wg.calls.count("pulls/7")
    check("review wait: probes 3 calls a poll, re-reads in full when it lands -> GREEN",
          rc == 0 and full_reads == 2 and wg.probes == 3, f"rc={rc} full reads={full_reads} probes={wg.probes} calls={len(wg.calls)}")
    wg = _WaitGh(land_after_probes=10_000)
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run("o/r", 7, None, 15, 0, gh=wg)
    full_reads = wg.calls.count("pulls/7")
    check("review wait NEG: no review in 15 min -> RED after ONE final full read",
          rc == 1 and full_reads == 2, f"rc={rc} full reads={full_reads} calls={len(wg.calls)}")
    wg = _WaitGh(land_after_probes=10_000, waive_after_probes=2)
    with contextlib.redirect_stdout(io.StringIO()):
        rc = run("o/r", 7, None, 15, 0, gh=wg)
    check("#7203: a maintainer's waiver during the wait ends it at the next probe -> GREEN",
          rc == 0 and wg.probes == 2 and wg.clock.t - 1000.0 <= 2 * POLL_SECONDS,
          f"rc={rc} probes={wg.probes} waited={wg.clock.t - 1000.0}s")

    # ── the refresh WAITS for an in-flight read run, then re-runs the red it left
    class _FlightGh:
        def __init__(self, states, pr_after_wait=None):
            self.states, self.posted, self.clock = list(states), [], _Clock()
            self.pr, self.pr_after_wait = ref_pr, pr_after_wait
        def sleep(self, s):
            self.clock.sleep(s)
            self.pr = self.pr_after_wait or self.pr
        def now(self):
            return self.clock.now()
        def api(self, path):
            if path == "actions/runs/99":
                return {"id": 99, "workflow_id": 5}
            if path == "pulls/4649":
                return self.pr
            if path.startswith("actions/workflows/5/runs?"):
                state = self.states.pop(0) if len(self.states) > 1 else self.states[0]
                self.last = state
                return {"workflow_runs": [state]}
            if path.startswith("actions/runs/7/jobs"):
                # The run is in flight because its REVIEW job is (a review job never waits on a refresh).
                done = self.last.get("status") == "completed"
                return {"jobs": [{"name": "Automatic review answered", "status": "completed" if done else "in_progress",
                                  "conclusion": self.last.get("conclusion") if done else None}]}
            raise ReadError(f"unexpected GET {path}")
        def post(self, path):
            self.posted.append(path)
    flying = dict(red_read, status="in_progress", conclusion=None)
    for name, states, wait_s, expect_rc, expect_posted, pr_after in [
        ("#7203: in flight, a PUSH during the wait, then RED -> the obsolete run is NOT re-run",
         [flying, flying, red_read], 600, 0, [], dict(ref_pr, head=dict(ref_pr["head"], sha="c" * 40))),
        ("#7203: in flight, PR CLOSED during the wait, then RED -> not re-run",
         [flying, red_read], 600, 0, [], dict(ref_pr, state="closed")),
        ("#7198: in flight, then RED -> waits, then re-runs it", [flying, flying, red_read], 600, 0, ["actions/runs/7/rerun"], None),
        ("in flight, then GREEN -> waits, posts nothing", [flying, dict(red_read, conclusion="success")], 600, 0, [], None),
        ("NEG: never finishes within the wait -> RED, named, nothing posted", [flying], 90, 1, [], None),
    ]:
        stub = _FlightGh(states, pr_after)
        with contextlib.redirect_stdout(io.StringIO()):
            rc = run_refresh("Systemorph/MeshWeaver", 4649, "pull_request_review_comment", "99", "2026-10-07T10:00:00Z",
                             gh=stub, wait_s=wait_s)
        check(f"refresh: {name}", rc == expect_rc and stub.posted == expect_posted, f"rc={rc} posted={stub.posted}")

    # ── the retry listener: an outage run is re-run after the reset; a verdict never is
    outage_job = {"name": "Automatic review answered", "conclusion": "failure", "steps": [
        {"name": "Judge the pull request's automatic review", "conclusion": "failure"},
        {"name": OUTAGE_STEP_NAME, "conclusion": "failure"}]}
    verdict_job = {"name": "Automatic review answered", "conclusion": "failure", "steps": [
        {"name": "Judge the pull request's automatic review", "conclusion": "failure"},
        {"name": OUTAGE_STEP_NAME, "conclusion": "skipped"}]}
    failed_run = {"id": 40187547005, "workflow_id": 55, "status": "completed", "conclusion": "failure", "run_attempt": 1,
                  "head_sha": HEAD, "pull_requests": [{"number": 7173}]}
    open_pr = {"number": 7173, "state": "open", "head": {"sha": HEAD}}

    class _RetryGh:
        """A fake adapter. While the primary limit is spent (`remaining` 0 and no wait has happened
        yet) every METERED read raises ApiOutage, as `Gh._call` does past its five-minute budget; a low
        but non-zero `remaining` still answers. `rate_limit` is free. `after_wait` replaces the run / pull
        request once the listener has slept — the world moving on during the wait. `others` are further
        runs of the workflow on the head, as (run, jobs by attempt); the failed-run listing returns the
        waking run and those, as GitHub does."""
        def __init__(self, run_, jobs, pr=open_pr, remaining=0, after_wait=None, refuse_after_wait=False, others=()):
            self.run_, self.pr, self.remaining, self.posted, self.clock = run_, pr, remaining, [], _Clock()
            self.jobs = {(run_["id"], k): v for k, v in
                         (jobs if isinstance(jobs, dict) else {int(run_.get("run_attempt") or 1): jobs}).items()}
            self.others = [o for o, _ in others]
            for o, by_attempt in others:
                self.jobs.update({(o["id"], k): v for k, v in by_attempt.items()})
            self.after_wait, self.refuse_after_wait, self.reads = after_wait or {}, refuse_after_wait, []
        def sleep(self, s):
            self.clock.sleep(s)
            self.run_ = self.after_wait.get("run", self.run_)
            self.pr = self.after_wait.get("pr", self.pr)
        def now(self):
            return self.clock.now()
        def rate_limit(self, resource="core"):
            return (self.remaining if not self.clock.slept else 4000), self.clock.t + 600
        def api(self, path):
            self.reads.append(path)
            if self.refuse_after_wait or (self.remaining == 0 and not self.clock.slept):
                raise ApiOutage(f"GET {path}: API rate limit exceeded (HTTP 403)", "rate-limit", self.clock.t + 600)
            runs = {r["id"]: r for r in [self.run_] + self.others}
            m = re.fullmatch(r"actions/runs/(\d+)", path)
            if m and int(m.group(1)) in runs:
                return runs[int(m.group(1))]
            m = re.fullmatch(r"actions/runs/(\d+)/attempts/(\d+)/jobs\?per_page=100", path)
            if m:
                return {"jobs": self.jobs[(int(m.group(1)), int(m.group(2)))]}
            if path.startswith(f"actions/workflows/{self.run_.get('workflow_id')}/runs?head_sha="):
                head = path.split("head_sha=")[1].split("&")[0]
                return {"workflow_runs": [r for r in runs.values() if r.get("head_sha") == head
                                          and r.get("conclusion") == "failure"]}
            if path == "pulls/7173":
                return self.pr
            raise ReadError(f"unexpected GET {path}")
        def post(self, path):
            self.posted.append(path)
    RERUN = ["actions/runs/40187547005/rerun"]
    for name, stub, attempt, expect_rc, expect_posted, expect_slept in [
        ("#7173: outage run, limit spent -> waits for the reset BEFORE reading, re-runs the WHOLE run",
         _RetryGh(failed_run, [outage_job]), 1, 0, RERUN, [610.0]),
        ("outage run, budget left -> re-runs at once", _RetryGh(failed_run, [outage_job], remaining=900), 1, 0, RERUN, []),
        ("NEG: a VERDICT red (marker skipped) -> never re-run", _RetryGh(failed_run, [verdict_job], remaining=900), 1, 0, [], []),
        ("NEG: the head moved -> not re-run", _RetryGh(failed_run, [outage_job], pr=dict(open_pr, head={"sha": "c" * 40})), 1, 0, [], [610.0]),
        ("#7203: head moved DURING the wait -> decided on the fresh read, not re-run",
         _RetryGh(failed_run, [outage_job], after_wait={"pr": dict(open_pr, head={"sha": "c" * 40})}), 1, 0, [], [610.0]),
        ("#7203: limit LOW (reads still answer), head moved during the wait -> not re-run",
         _RetryGh(failed_run, [outage_job], remaining=20, after_wait={"pr": dict(open_pr, head={"sha": "c" * 40})}),
         1, 0, [], [610.0]),
        ("#7203: PR closed DURING the wait -> not re-run",
         _RetryGh(failed_run, [outage_job], after_wait={"pr": dict(open_pr, state="closed")}), 1, 0, [], [610.0]),
        ("#7203: a newer attempt started DURING the wait -> not re-run",
         _RetryGh(failed_run, [outage_job], after_wait={"run": dict(failed_run, run_attempt=2, status="in_progress")}),
         1, 0, [], [610.0]),
        ("#7203: the limit is still spent after the wait -> RED, named, no traceback",
         _RetryGh(failed_run, [outage_job], refuse_after_wait=True), 1, 1, [], [610.0]),
        ("#7203: FIRST outage on attempt 3 (1-2 were verdicts) -> re-run, not 'three in a row'",
         _RetryGh(dict(failed_run, run_attempt=3), {3: [outage_job], 2: [verdict_job], 1: [verdict_job]}, remaining=900),
         3, 0, RERUN, []),
        ("outage on attempts 2-3 after a verdict on 1 -> re-run (streak 2)",
         _RetryGh(dict(failed_run, run_attempt=3), {3: [outage_job], 2: [outage_job], 1: [verdict_job]}, remaining=900),
         3, 0, RERUN, []),
        ("NEG: outage on 3 attempts in a row -> RED, named, stops",
         _RetryGh(dict(failed_run, run_attempt=3), {3: [outage_job], 2: [outage_job], 1: [outage_job]}, remaining=900),
         3, 1, [], []),
        ("NEG: outage streak of 3 after a verdict (attempt 4) -> RED, stops",
         _RetryGh(dict(failed_run, run_attempt=4), {4: [outage_job], 3: [outage_job], 2: [outage_job], 1: [verdict_job]},
                  remaining=900), 4, 1, [], []),
    ]:
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                rc = run_retry_outage("Systemorph/MeshWeaver", str(stub.run_["id"]), attempt, gh=stub)
        except Exception as e:  # the negative control of the bootstrap case: an uncaught ApiOutage
            rc = f"raised {type(e).__name__}"
        check(f"retry: {name}", rc == expect_rc and stub.posted == expect_posted and stub.clock.slept == expect_slept,
              f"rc={rc} posted={stub.posted} slept={stub.clock.slept}")
    # ── the listener re-runs EVERY outage-failed run of the check on the head, from any event: branch
    # protection kept reading the failed check-runs of review and review-comment runs that met the limit
    # although newer runs had succeeded (measured on two plugin-repository pull requests).
    review_run = dict(failed_run, id=501, event="pull_request_review", created_at="2026-10-25T12:48:00Z")
    comment_run = dict(failed_run, id=502, event="pull_request_review_comment", created_at="2026-10-25T12:52:00Z")
    verdict_pr_run = dict(failed_run, id=503, event="pull_request", created_at="2026-10-25T12:40:00Z")
    green_run = dict(failed_run, id=504, conclusion="success", created_at="2026-10-25T13:53:00Z")
    for name, stub, expect_rc, expect_posted in [
        ("#7203: woken by one outage run, a SECOND outage run of another event on the head -> both re-run",
         _RetryGh(review_run, [outage_job], remaining=900, others=[(comment_run, {1: [outage_job]}),
                                                                    (verdict_pr_run, {1: [verdict_job]}),
                                                                    (green_run, {1: []})]),
         0, ["actions/runs/501/rerun", "actions/runs/502/rerun"]),
        ("#7203: woken by a VERDICT red, two stranded outage runs on the head -> both re-run, the verdict never",
         _RetryGh(verdict_pr_run, [verdict_job], remaining=900, others=[(review_run, {1: [outage_job]}),
                                                                         (comment_run, {1: [outage_job]})]),
         0, ["actions/runs/502/rerun", "actions/runs/501/rerun"]),
        ("#7203: a stranded run at its 3rd outage in a row -> RED, named; the others are still re-run",
         _RetryGh(review_run, [outage_job], remaining=900,
                  others=[(dict(comment_run, run_attempt=3), {3: [outage_job], 2: [outage_job], 1: [outage_job]})]),
         1, ["actions/runs/501/rerun"]),
        ("NEG: the head moved -> the waking run is not re-run and no other run is swept",
         _RetryGh(review_run, [outage_job], remaining=900, pr=dict(open_pr, head={"sha": "c" * 40}),
                  others=[(comment_run, {1: [outage_job]})]), 0, []),
        ("NEG: closed pull request -> nothing swept",
         _RetryGh(review_run, [outage_job], remaining=900, pr=dict(open_pr, state="closed"),
                  others=[(comment_run, {1: [outage_job]})]), 0, []),
    ]:
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                rc = run_retry_outage("Systemorph/MeshWeaver", str(stub.run_["id"]), 1, gh=stub)
        except Exception as e:
            rc = f"raised {type(e).__name__}"
        check(f"retry sweep: {name}", rc == expect_rc and stub.posted == expect_posted,
              f"rc={rc} posted={stub.posted}")
    # ── #7257 review, the INVERSE case: a run whose two review jobs DISAGREE (core: bare red, lane green).
    # Its own refresh cannot re-run it and an older refresh does not wait on it, so the listener does,
    # on its completion — under the same bound as an outage.
    lane_green = {"name": "lane / Automatic review answered", "status": "completed", "conclusion": "success"}
    for name, stub, attempt, expect_rc, expect_posted in [
        ("#7257: review jobs DISAGREE (bare red, lane green) -> re-run on completion",
         _RetryGh(failed_run, [verdict_job, lane_green], remaining=900), 1, 0, RERUN),
        ("NEG: both review jobs red (a verdict) -> never re-run",
         _RetryGh(failed_run, [verdict_job, dict(verdict_job, name="lane / Automatic review answered")], remaining=900),
         1, 0, []),
        ("NEG: a disagreement on 3 attempts in a row -> RED, stops",
         _RetryGh(dict(failed_run, run_attempt=3), {k: [verdict_job, lane_green] for k in (1, 2, 3)}, remaining=900),
         3, 1, []),
    ]:
        with contextlib.redirect_stdout(io.StringIO()):
            rc = run_retry_outage("Systemorph/MeshWeaver", str(stub.run_["id"]), attempt, gh=stub)
        check(f"retry: {name}", rc == expect_rc and stub.posted == expect_posted, f"rc={rc} posted={stub.posted}")
    for marks, want in [([True], 1), ([True, True, False, True], 2), ([False, True], 0), ([], 0)]:
        check(f"outage_streak({marks}) = {want}", outage_streak(marks) == want, f"got {outage_streak(marks)}")

    # ── the marker step the listener keys on is in every workflow that judges, by this exact name
    here = os.path.dirname(os.path.abspath(__file__))
    wf_dir = os.path.join(os.path.dirname(here), "workflows")
    if os.path.isdir(wf_dir) and os.path.exists(os.path.join(wf_dir, "node-repo-review-answered.yml")):
        for wf, need in [("node-repo-review-answered.yml", 2), ("review-answered.yml", 1)]:
            with open(os.path.join(wf_dir, wf), encoding="utf-8") as f:
                n = f.read().count(f'name: "{OUTAGE_STEP_NAME}"')
            check(f"marker step in {wf}", n == need, f"expected {need} got {n}")
        # The listener reads `pulls/{n}`; with an explicit `permissions:` block `pull-requests` is `none`
        # unless granted, and a private repository then refuses the read (MeshWeaver#7203 review). The
        # lane's job, its caller contract (workflow + job level) and core's caller all grant it.
        for wf, need in [("node-repo-review-answered-retry.yml", 3), ("review-answered-retry.yml", 2)]:
            with open(os.path.join(wf_dir, wf), encoding="utf-8") as f:
                n = len(re.findall(r"^[#\s]*pull-requests:\s*read\b", f.read(), re.MULTILINE))
            check(f"pull-requests: read granted in {wf}", n == need, f"expected {need} got {n}")
    else:
        print(f"self-test note outage: the workflows are not beside this copy ({wf_dir}) — the marker-step check runs in core's tree")
    return failures


def report_outage(e: ApiOutage) -> int:
    """An outage that outlasted this run's retry budget: INFRA, never a verdict (MeshWeaver#7187).
    Exit OUTAGE_EXIT and `outcome=api-outage` on $GITHUB_OUTPUT, which the lane's marker step
    (OUTAGE_STEP_NAME) reads and the retry listener keys on."""
    print(f"::error title=API outage — no verdict was taken::INFRA, not a finding about this pull request: {e}. "
          "Nothing was judged — no thread was found unanswered and no review was found missing. This run is "
          "re-run automatically once the API answers again (review answered: the retry listener; the "
          "request and stage lanes: their scheduled sweeps).")
    _append("GITHUB_OUTPUT", f"outcome=api-outage\noutage-kind={e.kind}\n")
    _append("GITHUB_STEP_SUMMARY", f"⚠️ **API outage — no verdict was taken** ({e.kind}): {e}\n")
    return OUTAGE_EXIT


def main(argv=None) -> int:
    try:
        return _main(argv)
    except ApiOutage as e:
        return report_outage(e)


def _main(argv=None) -> int:
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
    ap.add_argument("--arm-generated", action="store_true",
                    help="decide whether --pr is a generated-only App pull request that may be armed with no review; "
                         "writes arm=true|false and head=<sha> to $GITHUB_OUTPUT")
    ap.add_argument("--stage-gate", action="store_true",
                    help="the staged pipeline's stage-1 gate: may stage 2 start for this head (stage_readiness)? "
                         "exit 0 = yes, 1 = held (RED, named); takes --repo, --pr, --since, --fallback-minutes")
    ap.add_argument("--stage-advance", action="store_true",
                    help="the event half: re-run a held run's failed jobs once stage 1 is green; takes --repo, "
                         "--workflow and exactly one of --pr / --head-sha / --sweep")
    ap.add_argument("--request-review-on-change", action="store_true",
                    help="the review-on-change lane (policy review-on-real-change): request ONE Copilot review for --pr's "
                         "head when its own diff changed since Copilot's last review (or since --before), and nothing "
                         "when only the default branch was merged in (review_request_action)")
    ap.add_argument("--request-review-sweep", action="store_true",
                    help="the scheduled half of the review-on-change lane: re-judge every open non-draft pull request and "
                         "request a review where a landed one no longer covers the head (run_request_review_sweep)")
    ap.add_argument("--before", help="--request-review-on-change: the push's previous head (github.event.before)")
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
    ap.add_argument("--retry-outage", action="store_true",
                    help="the outage retry listener: re-run a completed `Review answered` run (--run-id, --attempt) "
                         "iff its red was an API outage, never a verdict (retry_outage_action)")
    ap.add_argument("--attempt", type=int, help="--retry-outage: the run attempt that completed (workflow_run.run_attempt)")
    ap.add_argument("--verdict-check-run", metavar="NAME",
                    help="policy review-pending-while-answering: judge --pr ONCE and write the verdict as the check-run NAME "
                         "(the required context's exact name) — success, PENDING (in_progress) while threads await answers or "
                         "the head awaits its review within the deadlines, failure otherwise; exit 1 only on failure")
    ap.add_argument("--verdict-sweep", action="store_true",
                    help="the scheduled half: re-judge every open pull request whose --verdict-check-run is pending or "
                         "unmeasured (a review landing or a deadline passing raises no event here)")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    if not args.repo:
        print("::error::--repo (or GH_REPO) is required")
        return 2
    if args.retry_outage:
        if not re.fullmatch(r"[1-9]\d*", args.run_id or "") or not args.attempt or args.attempt < 1:
            print(f"::error::--retry-outage needs --run-id <number> and --attempt <n>, got {args.run_id!r} / {args.attempt!r}")
            return 2
        return run_retry_outage(args.repo, args.run_id, args.attempt)
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
    if args.verdict_sweep or args.verdict_check_run:
        name = (args.verdict_check_run or "").strip()
        if not name:
            print("::error::--verdict-sweep needs --verdict-check-run <the required context's exact name>")
            return 2
        if args.merge_group_ref or args.as_of or args.wait_for_review or args.settle_replies:
            print("::error::--verdict-check-run takes --repo and --pr (or --verdict-sweep) only — a merge-queue entry, a "
                  "replay and the review/settle waits all belong to the job-shaped check (policy review-pending-while-answering)")
            return 2
        if args.verdict_sweep:
            if args.pr:
                print("::error::--verdict-sweep re-judges every open pull request; it takes no --pr")
                return 2
            return run_verdict_sweep(args.repo, name)
        if not args.pr or not re.fullmatch(r"[1-9]\d*", args.pr):
            print(f"::error::--verdict-check-run needs --pr <number>, got {args.pr!r}")
            return 2
        return run_verdict(args.repo, int(args.pr), name)
    if args.request_review_sweep:
        if args.pr or args.merge_group_ref or args.before:
            print("::error::--request-review-sweep takes --repo only")
            return 2
        return run_request_review_sweep(args.repo)
    if args.request_review_on_change:
        if not args.pr or args.merge_group_ref or not re.fullmatch(r"[1-9]\d*", args.pr):
            print("::error::--request-review-on-change takes --repo, --pr <number> and optionally --before <sha>")
            return 2
        if args.before and not re.fullmatch(r"[0-9a-f]{40}", args.before):
            print(f"::error::--before must be a full 40-hex sha (github.event.before), got {args.before!r}")
            return 2
        return run_request_review(args.repo, int(args.pr), args.before)
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
    if args.arm_generated:
        if not args.pr or args.merge_group_ref or not re.fullmatch(r"[1-9]\d*", args.pr):
            print("::error::--arm-generated takes --repo and --pr <number> only")
            return 2
        return run_arm_generated(args.repo, int(args.pr))
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
