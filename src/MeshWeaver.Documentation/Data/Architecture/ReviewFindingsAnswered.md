---
Name: Review Findings Answered
Category: Architecture
Description: >-
  A pull request into main reads RED until the automatic review has landed and every thread it opened
  has a reply from a person. Why review was advisory, the reviewer as measured (two logins, one
  account, and a quota refusal posted as if it were a review), the maintainer waiver, the controls
  replayed on real merges, and what the check still cannot see.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/><polyline points="8 10 11 13 16 8"/></svg>
---

# Review Findings Answered

**The check `Automatic review answered` is RED until the automatic review has landed on a pull
request and every inline thread that review opened has a reply from a person.** It never skips, it
never passes on no evidence, and a pull request the reviewer could not review is released only by a
maintainer's visible waiver. It is the build of option B on #4299, decided by the maintainer on
2026-09-17.

- Workflow: `.github/workflows/review-answered.yml`
- Predicate and self-test: `.github/scripts/check-review-answered.py`
- Status: **observed, not required** — see Rollout below.

## Why: a review was advisory

The ruleset `main pr protection` (2128472) carries a `copilot_code_review` rule, so every pull
request into `main` is reviewed. `auto-arm.yml` arms every pull request for the merge queue the
moment it opens. Nothing joined the two, so a merge waited for the required checks and never for
the review — and on the pull requests whose checks are fast, the checks usually won.

| Instance | What merged past the review | Cost |
|---|---|---|
| #4310 (2026-09-14) | 14 findings, 0 replies | a behaviour change reddened a dependent repository's suite; the fleet's publication stopped for six hours |
| 2026-09-14T18:00Z → 09-15T07:5xZ | 9 of 31 merges carried unanswered findings (31 findings) | 23 of the 31 were still unanswered a day later |
| #4366 (2026-09-15) | 14 findings on the bundle-publication path, 26 minutes standing | the same lane whose break cost six hours the day before |
| #4383 (2026-09-15) | three findings read and fixed; the merge took the pre-fix head a minute earlier | the fixes landed separately as #4386 |

The instances on the filing (#4269, #4291, 2026-09-14) were admitted before the reviewer had
finished; condition 1 below holds those. In every miss measured after it — #4310, #4343, #4347,
#4366, #4371, #4383 — **the review had landed; it had not been read.** A check that waits only for
the review to *complete* (option A) would have held none of those, which is why option B asks
whether each finding was *answered*.

The rate at the time this page was written, replaying the predicate over 60 merged pull requests
(2026-09-16T08:48Z → 09-17T07:41Z), each as of its own `merged_at`: **32 of 60 would have been red
at merge**, all of them for unanswered threads; 126 findings, 86 of them unanswered at merge. None was
red for a missing review. That is the cost of making the check required, stated before anyone
decides to.

## The rule

All three hold, or the check is RED:

1. **The automatic review has landed** — a review by the reviewer account whose body is recognisably
   a review.
2. **Every thread the reviewer started has a person's reply** — for every comment by the reviewer
   with no `in_reply_to_id`, at least one comment in that thread by an account of `type: User`
   (following `in_reply_to_id` to the root, so a reply to a reply counts).
3. **The inputs were read completely** — every read succeeded, and the comment listing is not shorter
   than the `review_comments` count the pull request reported *before* the listing began. Measured
   on 60 of 60 recent pull requests: listing and count agree.

A reply that says nothing still counts as answered. That limitation is known and accepted on #4299;
the predicate is still strictly better than none.

## The reviewer, as measured

Measured 2026-09-17 over 50 merged pull requests (#4487–#4568) through the REST endpoints the check
uses:

| Endpoint | Login | Type | Account id |
|---|---|---|---|
| `pulls/{n}/reviews` | `copilot-pull-request-reviewer[bot]` | Bot | 175728472 |
| `pulls/{n}/comments` | `Copilot` | Bot | 175728472 |

**One account, two logins.** A predicate keyed on either login alone sees half of the reviewer. The
check matches on the account id or either login, and only for `type: Bot`, so a person who names
their account `Copilot` is not the reviewer.

**Exactly one review per pull request** on all 50, always `COMMENTED`, usually on the first commit
rather than the head. The ruleset carries `review_on_push: false` and `review_draft_pull_requests:
true`: the reviewer reviews once, on open, drafts included, and never again unless someone asks.

**A refusal is posted as a review.** On #645–#654 (2026-07-25/26) the reviewer answered every
pull request with a review, from the same account, whose whole body was:

```text
Copilot was unable to review this pull request because the user who requested the review has reached their quota limit.
```

So "a review by the reviewer exists" reads a quota outage as "reviewed". The check therefore
classifies the body:

| Body | Reading |
|---|---|
| names a refusal (`Copilot was unable to review …`, or `**Files reviewed:** 0/…`) | **refused** — not landed |
| carries the heading `Pull request overview` (July: `## Pull request overview`; September: inside `<summary>`) | **landed** |
| anything else, including an empty body | **unrecognised** — not landed, and the first line is printed |

A refusal marker wins over the review marker. An unrecognised body is red rather than green because
the check cannot tell, and cannot-tell is never a pass. If the reviewer changes its format, every pull
request reads red naming the new first line, and the fix is one marker in the script.

## When it is evaluated

| Event | Types | Why |
|---|---|---|
| `pull_request` | opened, synchronize, reopened, ready_for_review, labeled, unlabeled | a new head; the waiver is a label |
| `pull_request_review` | submitted, dismissed | the reviewer's review; a person's reply also creates a review |
| `pull_request_review_comment` | created, deleted | a finding or a reply arriving, or a reply going |
| `merge_group` | checks_requested | the queue entry is judged again; the number comes from `gh-readonly-queue/main/pr-<N>-<sha>` |

There is no job-level `if:`, no path or branch filter and no `continue-on-error`: a skipped required
context counts as satisfied, so every event evaluates the predicate in full. The script's self-test
runs before the verdict in the workflow, and again in `dotnet-test.yml`'s CI-shell lane so a pull
request that breaks the predicate goes red on itself.

Evaluations of one pull request share a concurrency group and are never cancelled mid-read. A review
arrives as one `submitted` event and one `created` event per inline comment; GitHub keeps one running
and one pending run per group, so a burst of fourteen collapses to two evaluations, and the one that
runs last read last.

Every read is REST — `pulls/{n}`, `pulls/{n}/reviews`, `pulls/{n}/comments`, `issues/{n}/events`,
`collaborators/{login}/permission` — with the job's read-only `GITHUB_TOKEN`. The collaborator read
is taken on every run for the pull request's author (when that is a person), so a token that cannot verify a waiver is
discovered on an ordinary pull request rather than when a maintainer needs the waiver.

## The waiver

A pull request the reviewer cannot review — a quota refusal, an outage, a change with no reviewable
files — is released by exactly one thing: **the label `review-waived`, applied by a maintainer.**

- The label's presence is read from the pull request; **who applied it** is read from the REST
  issue events (the latest `labeled`/`unlabeled` event for that label).
- It is honoured only when that account is a person and its `role_name` on the repository is
  `admin` or `maintain`. A writer's label, a bot's label, or a label with no attributable event is
  refused, and the refusal names the account.
- **It releases condition 1 only.** Every thread the reviewer did open still needs a reply, waiver or
  not.
- It is never automatic, and the log names who waived and when.

The first remedy for a refusal is not the waiver: a maintainer re-requests the review, and a real
review replaces the refusal.

🚨 **An agent never applies the waiver.** Agent sessions here run under the maintainer's own GitHub
account, and the check reads an account's role, not who was at the keyboard — it cannot tell a
maintainer's waiver from an agent's. The label event in the pull request's timeline is the audit.

## What it does not see

- **A reply that says nothing** counts as an answer (accepted on #4299).
- **Resolution.** REST has no resolved state for a review thread, so a thread resolved without a
  reply stays red. Reply; resolving is optional.
- **Suppressed comments.** The reviewer lists some findings only inside its review body
  (`Suppressed comments (N)`). Those are not threads and are not held.
- **A human reviewer's threads.** Only the automatic reviewer's threads are held.
- **A finding that lands after the queue entry was judged.** The `merge_group` evaluation reads the
  review once per queue build. Because the reviewer posts once, on open, and the pull-request check
  cannot be green before that review has landed, a new thread during a queue build needs someone to
  re-request the review while the entry is building.
- **Its own edits.** Like every check in this repository it runs the pull request's copy of the
  script, so a pull request that changes `check-review-answered.py` is judged by its own change.
  Read that file's diff yourself.
- **Pull requests into other branches.** The ruleset reviews only the default branch, so a pull
  request into another branch reads red; the check is not required there.

## Controls

The predicate, replayed through the real REST adapter with `--as-of` on pull requests whose outcome
is known:

| Pull request | As of | Verdict | Reading |
|---|---|---|---|
| #4310 | its merge, 2026-09-14T14:04:21Z | RED | 14 of 14 threads unanswered |
| #4366 | its merge, 2026-09-15T06:07:05Z | RED | 14 of 14 threads unanswered |
| #4343 | its merge, 2026-09-14T18:59:36Z | RED | 4 of 4 unanswered (the replies came 13 minutes later) |
| #4343 | now | GREEN | 4 of 4 answered |
| #4556 | its merge, 2026-09-17T05:59:08Z | GREEN | 5 of 5 answered before merge |
| #4568 | now | GREEN | review landed, no threads |
| #645 | now | RED | the review is the quota refusal |

The self-test (`--self-test`) states, for each of its fixture cases, the exact reasons the case must
be red for, so a fixture cannot pass by being red for a second, unintended reason. Its negative
controls were watched failing: twelve mutations of the predicate — a bot's reply counted as an answer,
a refusal counted as a review, an unrecognised body counted as a review, any bot taken for the
reviewer, the completeness check removed, a waiver releasing threads, a writer or a bot allowed to
waive, `--as-of` ignored for comments, only direct replies followed, a `PENDING` review counted, a
short sha accepted as a queue ref — and each one turned the self-test red.

To replay a pull request yourself:

```bash
python3 .github/scripts/check-review-answered.py --repo Systemorph/MeshWeaver --pr 4310 --as-of 2026-09-14T14:04:21Z
```

## Rollout

The check lands **non-required**. The context to add to ruleset 2128472, beside
`Consolidate test results`, is:

```text
Automatic review answered
```

Before adding it:

1. **Watch it on live pull requests** — red on open, green after the review lands on a pull request
   with no findings, green after replies on one with findings, and a run on the reviewer's own review
   event.
2. **Teach the merge-queue steward this check.** `merge-queue-steward.py` reads only the failed
   `merge_group` run of `MeshWeaver Build and Test` under the pull request's queue prefix. An ejection
   caused by this check would find no such run (and be rejected as unclassifiable) or find an older
   failed build of the same pull request and classify that instead.
3. **Decide the cost.** 32 of the last 60 merges would have waited for replies.

## What an author does

Reply to every thread the reviewer opened — "fixed in `<sha>`", or why not — through the pull
request page or REST:

```bash
gh api "repos/Systemorph/MeshWeaver/pulls/<PR>/comments?per_page=100"
gh api -X POST "repos/Systemorph/MeshWeaver/pulls/<PR>/comments/<comment id>/replies" -f body='Fixed in <sha>: …'
```

Never hand-request the review to turn the check green, and never apply `review-waived` yourself.

## Related

- [The Merge Queue](../MergeQueue) — the queue this check runs in, and the steward named in the rollout
- [Reading CI Signals](../ReadingCiSignals) — why a skipped required context counts as satisfied
