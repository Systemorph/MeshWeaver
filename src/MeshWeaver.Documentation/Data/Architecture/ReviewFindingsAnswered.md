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
- Status: 🚨 **REQUIRED on `main` since 2026-09-17** — measured that evening on ruleset 2128472,
  which now lists `Automatic review answered` beside `Consolidate test results`. The Rollout section
  below is kept as the record of how it got there; it is no longer the current state.

🚨 **What being required FEELS like, because it is not obvious from the outside.** An unanswered
thread leaves the pull request `mergeable_state: blocked` with every build green — and `blocked` is
the same word REST returns for a pull request merely waiting its turn in the queue. Three pull
requests sat green, armed and silently OUTSIDE the merge queue for ~45 minutes on the evening this
landed, and nothing in the REST view distinguished that from progress. If a green, armed pull
request is not merging, read this check before anything else, and read the merge queue ITSELF rather than
`mergeable_state` — the queue is one of the two things REST cannot express:

```bash
gh api graphql -f query='{repository(owner:"Systemorph",name:"MeshWeaver"){
  mergeQueue(branch:"main"){entries(first:20){totalCount nodes{position state
  pullRequest{number}}}}}}'
```

A `totalCount` that does not contain your pull request, while other pull requests merge through it,
is the reading that separates "held" from "waiting".

🚨 **It is answered by replying ON the thread**, and nothing else does it:

```bash
gh api "repos/Systemorph/MeshWeaver/pulls/<n>/comments/<comment-id>/replies" -f body='…'
```

🚨 Quote the path. Unquoted, the shell reads `<n>` as a redirection and the command fails
before `gh` runs — which looks like a broken instruction rather than a quoting mistake.

🚨 **And answering every thread is NECESSARY, NOT SUFFICIENT.** The verdict branch protection
reads can still be an older failure taken on the same head, while the check's own newest run says
GREEN. The remedy — re-run the check's `pull_request` run — the measurement behind it, and what was
changed so it stops happening are below, under **"The check's log says GREEN and the pull request
is still BLOCKED"**.

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

1. **The automatic review has landed** — a review by the reviewer account, at a non-`PENDING` state,
   whose body is not a refusal. What makes it a review is *who posted it*, not how it is worded — see
   **"Provenance, not presentation"** below for why, and what requiring a recognisable shape cost.
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
| any other text | **landed** |
| no text at all | **unrecognised** — not landed |

A refusal wins. Everything else the reviewer says is the review.

### Provenance, not presentation — and what the other way cost

**The body is read for one purpose: to separate a review from a refusal to review.** What makes a
review a review is that the *reviewer account* posted it at a non-`PENDING` state. That is
`is_reviewer` plus the state check, and the body has no part in it.

Until 2026-09-18 `landed` additionally required the literal string `Pull request overview`, and this
page said so, adding: *"If the reviewer changes its format, every pull request reads red naming the
new first line, and the fix is one marker in the script."* The failure was therefore **foreseen and
accepted** — sound reasoning for an *advisory* check, where a false red costs a reader a glance.

Two things then happened within a day of each other. The check became a **required context**
(ruleset `2128472`, 2026-09-17T21:15Z), and the reviewer dropped its overview block, posting a short
verdict body instead — `### 🟢 Approval recommended`, `### 🟡 Changes recommended`,
`### 🔵 Needs a closer look`, plus a sentence or two. The marker matched nothing. Six core pull
requests were genuinely reviewed and every one read *"the automatic review has not landed"*.

Promotion changed the cost of "cannot-tell is never a pass" from a glance to a **hard stop on every
open pull request**, with no exit but a maintainer waiver — and answering the findings could not
clear it, because the unanswered-threads check is a *second* reason and the review-landed reason
stood regardless. A whole lane was blocked while the reviewer had in fact reviewed everything.

**So the marker was removed rather than updated.** Chasing the format would have re-armed the same
trap on the reviewer's next revision. A decorative substring is the reviewer's choice and can change
without notice; the account id cannot.

🚨 **The self-test could not have caught it.** All three review fixtures carried the marker, so the
suite proved the rule only on the side of the change where it held — a control with no case on the
other side. The fixtures now include the three bodies measured on 2026-09-18, and each is verified
to go **red** if the marker rule is restored.

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

Evaluations are serialised per **pull request** for pull-request events and per **queue entry** for
`merge_group` — the queue head ref carries the entry's sha, so a re-queue of the same pull request
may evaluate alongside an older entry. That is the right granularity: what must not race is two
evaluations publishing the same check-run *name* on the same commit, and two queue entries of one
pull request are two commits with two check-runs. Nothing is cancelled mid-read, which also matters
because cancelling a `merge_group` run ejects the entry rather than retrying it. A review arrives as
one `submitted` event and one `created` event per inline comment; GitHub keeps one running and one
pending run per group, so a burst of fourteen collapses to two evaluations, and the one that runs
last read last.

### 🚨 The reviewer's own event cannot start a run here — so the check waits, briefly

Measured on this check's own pull request, #4575 (2026-09-17T08:05Z): the reviewer's
`pull_request_review` event **does** reach the repository and GitHub **does** create a workflow run
for it — run `35197843933`, conclusion **`action_required`, zero jobs**. The triggering actor is
`Copilot`, and the repository requires approval for runs triggered by a first-time contributor
(`actions/permissions/fork-pr-contributor-approval` → `first_time_contributors`). The two
`pull_request_review_comment` runs the review's own comments raised were `action_required` too.

So the event that would say *"the review has landed"* cannot evaluate anything, and a pull request
whose review raises **no** findings would keep the red from its `opened` evaluation until somebody
pushed again — a permanent red over a review that did land.

The check closes that itself: `--wait-for-review 15`. While the **only** thing missing is the review,
the step re-reads every 30 seconds for up to fifteen minutes and then answers RED (the job's cap is
20 minutes). Nothing else is ever waited for — an unanswered thread needs a person, an incomplete
listing needs another read. On #4575 the review landed 4m38s after the pull request opened; the range
on #4299's thread is 3–12 minutes.

A **person's** reply is not gated: it re-runs the check within seconds, which is what turns a red
into a green once the findings are answered.

The cheaper mechanism is a repository setting, not a wait: if runs triggered by `Copilot` no longer
need approval, the review's own event evaluates the check the moment it lands and the wait becomes
dead weight. That is a maintainer decision about the Actions approval policy, and it is the first item
in Rollout.

Every read is REST — `pulls/{n}`, `pulls/{n}/reviews`, `pulls/{n}/comments`, `issues/{n}/events`,
`collaborators/{login}/permission` — with the job's read-only `GITHUB_TOKEN`. The collaborator read
is taken on every run for the pull request's author (when that is a person), so a token that cannot verify a waiver is
discovered on an ordinary pull request rather than when a maintainer needs the waiver.

## 🚨 Three ways to be red, and only one of them is something you can do anything about

The check has one red and three causes, and until 2026-09-18 it printed **one sentence for all
three** — *"the automatic review must land. It usually arrives minutes after the pull request
opens…"*. That sentence is correct for exactly one of them.

| the state | what the reader is told now | what actually clears it |
|---|---|---|
| the reviewer has not posted yet | "the automatic review must land … usually arrives minutes after the pull request opens" | time |
| the reviewer posted a **refusal** | "**unreviewable right now** … nothing on this pull request can answer this" | the reviewer becoming able to review, then a maintainer's re-request — or the waiver |
| the reviewer posted **findings** nobody answered | "reply to each unanswered thread (fixed, or why not)" | a reply ON each thread |

**The middle row is the one that cost something** (#4730). On 2026-09-18 the reviewer refused for
quota from 11:39Z, and six pull requests — every one green on `Consolidate test results`, every one
with auto-merge armed — sat blocked for over four hours reading *"it usually arrives minutes after
the pull request opens"*. Nothing was arriving. There were no findings to answer, and no push,
re-run or new commit could change the answer, because the refusal is about the reviewer and not
about the pull request.

So a refusal now names itself in all three places a reader looks — the run's headline
(`RED — UNREVIEWABLE (the reviewer REFUSED to review this pull request)`), the step summary's
heading, and the *To go green* line, which for a refusal says explicitly that pushing, re-running
and replying all leave it exactly where it is.

🚨 **And the run no longer WAITS for it.** `waiting_would_help` existed for a real case — the
reviewer's own event cannot start an evaluation here, so a review that raises no findings needs a
bounded wait to be seen at all — but it asked *"does the reason contain `has not landed`?"*, and the
refusal reason is spelled *"the automatic review has not landed — the reviewer posted, but not a
review: …"*. So `--wait-for-review 15` slept a quarter of an hour printing *"waiting for the
automatic review"* at a reviewer that had already answered: **it said no.** Nothing arrives in that
window by construction, the run then contradicts its own summary, and it spends a runner doing it.
That is the same defect as the guidance line, one layer down, and it is why the discriminator has to
be the field rather than the prose.

🚨 **The VERDICT did not change and is not meant to.** A refusal was red before and is red now; the
check still fails in the safe direction, and the remedy is still a maintainer's. `refused` is a
field on the verdict rather than a substring of the reason text, for the reason this page's own
header gives about presentation-keyed reading — and the self-test asserts what the reader is *told*
(`says` / `never_says`, over the guidance, the summary **and the run headline**), not only what the
verdict *is*, because that is precisely the half that was wrong while the verdict was right. Each
half has a negative control: remove the wait's `not verdict.refused` and the refusal-only wait case
goes red; delete the headline and the refusal case goes red.

**Still open, and deliberately not decided here:** what a structurally unavailable reviewer should
do to the merge gate — hold as today, retry on a schedule once quota resets, or a time-boxed
maintainer waiver. That is a policy call (#4730's second ask), and naming the state does not make
it.

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
- 🚨 **Every other repository in the fleet.** This check exists here and nowhere else. See below.

## 🚨 This is a CORE-ONLY gate, and the rest of the fleet shows what that costs

Measured 2026-09-19, from `branches/main/protection` and every active ruleset:

| repo | mechanism | requires `Automatic review answered`? | carries `review-answered.yml`? |
|---|---|---|---|
| MeshWeaver | ruleset `2128472` | **yes** | yes |
| MeshWeaver.Plugins | classic, 8 contexts | no | **no** |
| MeshWeaver.Reinsurance | classic, 6 contexts | no | no |
| MeshWeaver.Crm | classic, 6 contexts | no | no |
| MeshWeaver.SocialMedia | classic, 7 contexts | no | no |
| MeshWeaver.Manufacturing | classic, 6 contexts | no | no |
| MeshWeaver.Education | ruleset, 4 contexts | no | no |

🚨 **MeshWeaver.Plugins is NOT covered**, against the common assumption that it is. Every satellite
carries a `Copilot review for default branch` ruleset, so the review is *requested* everywhere —
nothing outside core requires it to be *answered*.

What that produces, over the last 20 merged pull requests of each of six repositories:

| repo | merged sampled | carried findings | merged with ≥1 unanswered |
|---|---|---|---|
| MeshWeaver.Reinsurance | 20 | 11 | 7 |
| MeshWeaver.Crm | 20 | 13 | 11 |
| MeshWeaver.SocialMedia | 20 | 11 | 10 |
| MeshWeaver.Manufacturing | 20 | 11 | 9 |
| MeshWeaver.Education | 20 | 11 | 11 |
| MeshWeaver.Plugins | 20 | 15 | 7 |
| **total** | **120** | **72** | **55** |

**In all 55 the ratio is N of N** — not one finding answered on any of them, never a partial. So
outside core, findings are not *occasionally* missed, they are *structurally not read*: about twice
the rate this repository measured before the gate landed (32 of 60, above).

It is not cosmetic. On 2026-09-18, five satellite pull requests merged with 13 unanswered findings
between them. Assessed on the code: **11 of the 13 were real**, and only two could be declined — one
whose premise `git` itself rules out, one whose failure branch is unreachable. Those 11 reduce to
**6 distinct defects**, because three were raised twice (independently, on two repositories' copies
of one file) and three were three sites of one root. **Three of the six are in the platform's own
canonical `gen-manifests.py`**, replicated byte-identically into four repositories — including a
`--resolve` that reported `✓ … the merge can be committed` whenever git could not answer. Answered
and fixed in #4775 after the merges; the remaining vintages in #4777.

**Porting it is a workflow_call lane, never six copies** — the predicate is ~900 lines with its own
self-test, the settle wait and the event-class concurrency split derived from #4649. Six hand-copies
is how `gen-manifests.py` reached five vintages (#1426). 🚨 And the rollout order is not optional:
five of the six use CLASSIC protection, where an *absent* required context blocks every pull request
in the repository forever (measured on Plugins#1453), so the lane lands observe-only, is watched
publishing its context on live pull requests in that repo, and only then is the context added.
Proposed with the full measurement in #4776.

### The same gap, measured wider — 240 merged pull requests

The 120-PR sample above was extended on 2026-09-19 to the **30 most recently updated merged pull
requests in each of eight repositories** (adding Memex, and deepening the six): **240 merged, 325
findings, 182 never answered, across 93 pull requests.**

| repository | merged swept | carried findings | ≥1 unanswered | findings | unanswered |
|---|---:|---:|---:|---:|---:|
| MeshWeaver | 30 | 25 | **0** | 59 | **0** |
| MeshWeaver.Plugins | 30 | 17 | 7 | 56 | 23 |
| MeshWeaver.Education | 30 | 17 | 13 | 31 | 23 |
| MeshWeaver.Crm | 30 | 21 | 16 | 39 | 26 |
| MeshWeaver.Manufacturing | 30 | 17 | 15 | 28 | 25 |
| MeshWeaver.SocialMedia | 30 | 15 | 14 | 29 | 26 |
| MeshWeaver.Reinsurance | 30 | 14 | 10 | 30 | 21 |
| Memex | 30 | 22 | 18 | 53 | 38 |
| **total** | **240** | **148** | **93** | **325** | **182** |

Core is **0 of 59**, which is the check working — and it is also the control that makes every other
row readable, because the same instrument finds both states. A zero here is a measurement, not a
broken query. Memex is the worst of the eight and was not in the earlier sample.

A further **45 unanswered findings sit on 24 open DRAFTS** (Plugins 34, of which #1910 alone is 13 of
13; Memex 11). Those describe code that never shipped and are deliberately left; several drafts are
abandoned.

### Treating the backlog: bound it, and state the bound

There are ~5,700 merged pull requests fleet-wide, so every sweep is partial. **Report the denominator
you actually swept and what is left**, or the next session cannot tell a treated repository from an
untreated one. Note that `sort=updated` is not `sort=created`: an older pull request that received a
comment recently enters the window, which is why the windows differ in span per repo.

```bash
gh api "repos/Systemorph/<repo>/pulls?state=closed&per_page=100&sort=updated&direction=desc" \
  --jq '.[]|select(.merged_at!=null)|.number'
gh api "repos/Systemorph/<repo>/pulls/<n>/comments?per_page=100" \
  --jq '{findings:[.[]|select(.user.login=="Copilot" and .in_reply_to_id==null)]|length,
         replies:[.[]|select(.in_reply_to_id!=null)]|length}'
```

A finding counts as answered only when some comment's `in_reply_to_id` is that root's `id`. A
PR-level comment answers nothing, and neither does resolving the thread.

**Reply on every thread whatever the verdict.** Four verdicts, and the declines need their reason on
the record *more* than the acceptances do: *real* (fix it), *real but cosmetic*, *obsolete* (verify
against the current file and quote the evidence), *wrong* (say so, with the measurement). A merged
finding nobody answered and nobody declined is indistinguishable from one nobody read.

#### 🚨 `-F`, never `-f` — the reply that posts its own filename

```bash
gh api -X POST repos/Systemorph/<repo>/pulls/<n>/comments/<id>/replies -F body=@reply.md
```

**`-f body=@reply.md` sends the literal seven-to-twenty-character string `@reply.md` as the comment
body**, and the POST returns a normal comment id with a 201. So it reads as a successful reply; the
thread's root now has a comment whose `in_reply_to_id` points at it; and **every detector built on
`in_reply_to_id` — including the sweep query above, and `check-review-answered.py`'s own predicate —
counts the finding as answered.** The finding is untreated and nothing says so.

Measured on 2026-09-19: of 208 replies posted during one sweep, **74 were these stubs** (38 in Memex,
23 in Crm, 13 in Manufacturing), each one a `@`-prefixed filename or absolute path. Three of the four
sessions that hit it believed they had replied and reported thread counts to prove it — the proof
being the very field that cannot distinguish the two. The fourth caught it only by reading one reply
back.

So the verification is **read the body back and check its length**, never the reply's existence:

```bash
gh api "repos/Systemorph/<repo>/pulls/<n>/comments?per_page=100" \
  --jq '.[]|select(.in_reply_to_id!=null)|"\(.id) \(.body|length)"'
```

A length near 20 is the bug. **Repair with `PATCH /repos/{o}/{r}/pulls/comments/{reply_id}`, not a
second reply** — re-posting leaves the stub standing beside the real answer, and the thread then reads
as two answers, one of them noise.

This is the sweep's own instance of the defect class it exists to find: an answer that reads like a
pass. The question to ask of any reply mechanism is the one that applies to a gate — *if this had
failed, would the output differ?* Here it would not have.

### 🚨 One defect, five copies — fix the canonical, then RE-COPY IMMEDIATELY

Findings cluster hard on vendored files, because the reviewer reads each repository's copy
independently. Alongside the `gen-manifests.py` collapse above, the 2026-09-19 sweep found **25 of
the 182 against five satellites' copies of `scripts/resolve-platform.py`**, collapsing to **five
distinct defects** in core's canonical — one of them reported three times over (fixed in #4779; the
one deferred as needing a design decision is #4780). Patching a vendored copy alone is how this
fleet reached five vintages of one script (#1426).

🚨 **A canonical fix REDS EVERY SATELLITE THE MOMENT IT MERGES, so the re-copy is part of the same
piece of work — not a follow-up.** `node-repo-validate.yml` declares `platform-ref` with
**`default: main`**, and `scripts-ref` falls back to it. A satellite whose `validate:` job passes no
inputs — which is every one of them — therefore has the canonical fetched at core **`main`, live**,
and `check-resolver-copy.py` has been hard-red since `RED_FROM 2026-09-15`. Measured 2026-09-19:
core #4773 merged at 08:02:23Z and by 08:16Z every satellite's `validate / Validate node repos` — a
**required** context in all of them — was failing with

```
scripts/resolve-platform.py has DRIFTED from the platform's canonical: 32 code line(s) differ
(76 raw), RED since 2026-09-15T00:00:00Z
```

**Do not reason about this from a `platform-ref:` literal in a satellite's `ci.yml`.** Those literals
pin *other* jobs (`compile-check`, `tag-modules`, the pack lanes); the `validate:` job passes nothing
and takes the default. Reading the wrong job's input produces the confident and wrong conclusion that
a canonical fix is invisible to the satellites — it is the opposite, and the guard's own docstring
says so ("a canonical fetched at `@main` is live on merge for every caller", MeshWeaver#4027). Read
the **run**: the job log prints `SCRIPTS_REF: main`.

So the shape of the work is: fix the canonical, merge it, and re-copy into every satellite in the
same sitting —

```bash
gh api repos/Systemorph/MeshWeaver/contents/.github/scripts/resolve-platform.py --jq .content \
  | base64 -d > scripts/resolve-platform.py
```

— verifying the guard then reports `CODE-IDENTICAL` (exit 0). A re-run does not help an open pull
request, because the guard reads the **branch's** copy: that branch needs `git merge origin/main`
after the re-copy lands on the satellite's `main`.

### 🚨 And while the copy is behind, the rest of the lane is silently ungated

This is the more expensive half, and it is a **skip-trapdoor made by step ordering rather than by an
`if:`**. The drift check sits mid-job, so its failure skipped **16 subsequent steps** of the same job
(measured on MeshWeaver.SocialMedia#210, job 105867277548) — among them:

- `Every PR-reachable secret in this repo is asserted by a preflight`
- `Every manifest.lock is current (and carries a version)`
- `Every module's version matches its content`
- `No mapping in this repo's workflows writes a key twice`
- `No pin comment names a commit this repo no longer pins`

Each reported `skipped`, which under both protection mechanisms counts as satisfied. So for as long
as a satellite's vendored resolver is behind, every pull request in it is **unchecked by all of
those**, and the only visible symptom is one red about an unrelated file. Filed as #4784; the durable
fix is `if: ${{ !cancelled() }}` on each independent guard, or one job per guard family, so that a
single red reports rather than masks.

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

### Observed end to end on #4575

The check's own pull request, in order, with the run that did the work:

| When | Event | What happened |
|---|---|---|
| 08:01Z | `pull_request` opened | RED — *"the automatic review has not landed"*; check-run on the PR head, beside `Consolidate test results` |
| 08:05Z | the reviewer's review (2 findings) | run `35197843933` created and **`action_required`, zero jobs** — no evaluation |
| 11:09Z | a push, then two replies | the `synchronize` run and the person's `pull_request_review_comment` run both executed and read **2 threads, 2 answered** → GREEN |
| 11:09Z | the same burst | three sibling runs `cancelled` by the concurrency group, exactly as intended: one evaluation ran and it read last |

The review that counts was submitted on the **previous** head (`c496a0bffb`) and the check is green on
the new one (`bf37e92a09`): condition 1 asks whether the review landed, never whether it landed on the
head, because the reviewer reviews once. And the 15-minute wait was exercised against #645 — three
polls, then RED naming the quota refusal.

## Rollout — done; kept as the record

🚨 **This section is history, not instructions.** The context **was** added to ruleset 2128472
beside `Consolidate test results` on 2026-09-17, and the check is required today (see Status above).
Read the steps below as what was confirmed before the switch was thrown — do not re-run them as a
plan, and do not read "the check lands non-required" as the current state.

```text
Automatic review answered
```

What was confirmed before adding it:

1. **Decide the approval policy for `Copilot`-triggered runs.** Today they are `action_required`
   with zero jobs (measured, above), so the bounded wait is what makes the check self-sufficient. If
   the policy is relaxed, drop `--wait-for-review` to 0 and the job's cap to 5 minutes.
2. **Watch it on live pull requests** — red on open, green once the review lands on a pull request
   with no findings (through the wait), and green after the replies on one with findings.
3. ✅ **Teach the merge-queue steward this check — done 2026-09-17.** It read only the failed
   `merge_group` run of `MeshWeaver Build and Test` under the pull request's queue prefix, so an
   ejection caused by this check reached it with **both** outcomes wrong: no such run → rejected as
   *unclassifiable*, naming the wrong workflow; or an **older** failed build of the same pull
   request → it classifies a failure that is not why the entry was removed, and if that stale
   failure is a catalogued flake it **re-queues a pull request whose findings are unanswered**.
   `merge-queue-steward.py` now reads the other `merge_group` workflows first and rejects with
   `kind=gate`, naming the workflow — keyed on *"not the test workflow"* rather than on this check
   by name, so a gate added later is covered the day it runs rather than the day somebody remembers
   that file.
4. **Decide the cost.** 32 of the last 60 merges would have waited for replies.

### The timing, re-measured 2026-09-17 (39 merged pull requests)

The original framing — *"checks outran the reviewer"* — is **no longer the live mechanism**, and the
numbers say so:

| | median | p90 | max |
|---|--:|--:|--:|
| reviewer latency (open → first review submitted) | **4.1 min** | 7.8 | 17.2 |
| required gate (open → `Consolidate test results`) | **17.9 min** | 53.8 | 1074.5 |

**The required gate finished before the reviewer submitted on 0 of 37.** The denominator is 37 of the
39 because **two merged pull requests carry no automatic review at all** — #4584 and #4582, both with
**zero** reviews of any kind, checked rather than inferred from the gap in the counts. With no review
there is no submission time to compare against, so they are excluded from the comparison rather than
counted as a win for either side. They are also exactly the pull requests this check would hold: no
review landed, condition 1 unmet — so the two numbers disagreeing is itself a measurement, not an
inconsistency.

The reviewer is reliably *first*. So what merges past a finding today is not a race — it is that the
review **lands, and nothing requires it to be read**. That is precisely what this check asserts, and
it is why the remaining step is the ruleset edit rather than any further engineering.

## What an author does

Reply to every thread the reviewer opened — "fixed in `<sha>`", or why not — through the pull
request page or REST:

```bash
gh api "repos/Systemorph/MeshWeaver/pulls/<PR>/comments?per_page=100"
gh api -X POST "repos/Systemorph/MeshWeaver/pulls/<PR>/comments/<comment id>/replies" -f body='Fixed in <sha>: …'
```

Never hand-request the review to turn the check green, and never apply `review-waived` yourself.

### 🚨 The check's log says GREEN and the pull request is still BLOCKED

**The remedy first, because this is found under pressure. Re-run the check's `pull_request` run:**

```bash
# the run whose verdict branch protection actually reads
gh api "repos/Systemorph/MeshWeaver/actions/workflows/review-answered.yml/runs?head_sha=<HEAD SHA>" \
  --jq '.workflow_runs[] | select(.event=="pull_request") | .id'
gh api -X POST "repos/Systemorph/MeshWeaver/actions/runs/<ID>/rerun"
```

An empty commit does the same thing by a longer road — it gives the check a head sha it has never
judged — but 🚨 **`--allow-empty` does not mean "empty"**: it means "allow a commit that *would* be
empty", and anything already staged rides along. Under pressure, with a half-staged tree, that
pushes an unreviewed change while you are trying to repair a check. Refuse a dirty index first:

```bash
git diff --cached --quiet || { echo "REFUSING: the index is not empty — this would commit staged work"; exit 1; }
git commit --allow-empty -m "chore: one clean head for the review gate"
git push
```

Prefer the re-run above: it changes no history and cannot carry anything with it.

**Measured on #4652** (2026-09-17), the first time the remedy was used: `BLOCKED` / rollup
`FAILURE` → `CLEAN` / rollup `SUCCESS` / `isInMergeQueue: true`, within a minute of the re-run.
Re-arming auto-merge first did **not** clear it — the rollup was not stale in the usual sense, it
was citing a real, still-current failure taken in a suite nobody reads. Measured again on #4662
(2026-09-18): two `pull_request` runs re-run, both green, `blocked` → `clean` and into the queue.

🚨 **This re-run is legitimate, and it is not "re-run and see".** The check reads LIVE state, and
that state genuinely changed when the replies landed: the first evaluation was correct when it ran,
and so is the second. This is the one case where re-running a red is the right response rather than
a way of hiding one — and it is safe to write down because **if a thread is genuinely unanswered the
re-run is red too.** (Every other red in this repository still means investigate, not re-run.)

**What is actually happening** (measured on #4649, head `94fc73b4fdaa`, 2026-09-17). Answering a
thread fires **both** a `pull_request_review` and a `pull_request_review_comment` event. Fifteen runs
of this workflow resulted; eight published a check-run; and the `statusCheckRollup` that branch
protection consumes carried exactly **three**:

| check-run | triggering event | conclusion | in the rollup? |
|---|---|---|---|
| 105349138004 | `pull_request` | failure | **yes** |
| 105356428131 | `pull_request_review` | failure | **yes** |
| 105356609073 | `pull_request_review` | failure | **yes** |
| 105356478806 | `pull_request_review_comment` | failure | no |
| 105356544187 | `pull_request_review_comment` | failure | no |
| 105356665266 | `pull_request_review_comment` | failure | no |
| 105356708655 | `pull_request_review_comment` | **success** | no |
| 105356763402 | `pull_request_review_comment` | **success** | no |

So the mechanism is **not** a latched rollup and **not** "the newest run loses":

1. **A `pull_request_review_comment` run's verdict is invisible to branch protection.** Both
   successes were from that event, so they were never candidates — by construction, not by timing.
2. **The read-eligible runs that arrived after the replies were EVICTED.** `pull_request_review`
   runs at 19:48:19Z and 19:48:28Z were cancelled in the one pending slot they shared with the
   comment-driven runs, so they published nothing and the last *read* verdict on that sha stayed an
   early failure.
3. **Hence the symptom:** the newest check-run reads success, the check's own log says
   "GREEN — 5 of 5 answered by a person", and the rollup correctly reports a real failure in a suite
   nobody was looking at. Re-arming auto-merge does nothing, because the rollup is not stale.
4. **It is invisible in REST**, because `mergeable_state: blocked` is also what a PR waiting its
   turn reports. The rollup is the only place the difference shows.

**What was done about it.** Two changes, and one option that is not ours to take:

- **The concurrency group is split by event class** (`gate` for `pull_request` /
  `pull_request_review` / `merge_group`, `feedback` for `pull_request_review_comment`), so the
  survivor of the gate lane is always a run whose verdict is read. That removes cause 2
  deterministically.
- **`--settle-replies 60`** makes that surviving run judge a state that has stopped moving, so it
  publishes the settled verdict rather than a snapshot taken mid-answer. Every gap measured *inside*
  an answering burst was 1–11 s (#4649, #4656, #4646); 60 s is ~5×, and the one 954 s gap in that
  sample was a separate later round that rightly gets its own evaluation. It softens no verdict — an
  unanswered thread that stays unanswered is still red when the wait ends — and it waits only while
  unanswered threads are the whole complaint, never for a missing review or an incomplete listing.
- **Not taken, because it needs the maintainer:** reporting the verdict as a commit **status**
  against the head sha (`POST /repos/{o}/{r}/statuses/{sha}`) instead of as a job's check-run. That
  is the only shape correct *by construction* — one context, last write wins, whatever event
  produced it — but it is a required-context rename plus a ruleset edit on `2128472`, i.e. a change
  to how everything merges. Until that is decided, the two changes above plus the re-run remedy are
  what stands.

🚨 **And a reply must be ON THE THREAD.** A pull-request-level comment does not answer anything: the
check counts replies whose `in_reply_to_id` is the reviewer's root comment, and its log names every
thread it still considers unanswered. If the log disagrees with what you think you answered, read
that list before reaching for the re-run.

## Related

- [The Merge Queue](../MergeQueue) — the queue this check runs in, and the steward named in the rollout
- [Reading CI Signals](../ReadingCiSignals) — why a skipped required context counts as satisfied
