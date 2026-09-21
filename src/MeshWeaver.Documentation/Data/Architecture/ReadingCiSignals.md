---
Name: Reading CI Signals
Category: Architecture
Description: >-
  What a check's colour actually means — why a SKIPPED required context counts as satisfied everywhere
  while a NEVER-REPORTED one blocks forever under classic protection yet merges under a ruleset, why a
  red on a non-required check does not block, the i18n mirror that reds every downstream PR until it
  lands, and the shape most of these share: a narrow instrument answering correctly while the reader
  generalises it into a claim it never measured.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>
---

# Reading CI Signals

**A check's colour is not its authority.** Every rule here was learned by getting it wrong in
production or in a merge, several of them on the same day. They are cheap to apply and expensive to
rediscover.

## 🚨 The one sentence

**The absence of a red is not evidence of green.**

`SKIPPED`, `CANCELLED`, `NEUTRAL` and an empty conclusion are all "not FAILURE" — and **GitHub
counts a required context that REPORTED one of them as SATISFIED**. A PR can therefore merge through
a required gate that never ran, with a full wall of ticks.

Measured: **Plugins #862** merged with

```
Validate node repos:                     SUCCESS
Compile every NodeType (vs core):        SKIPPED     <- required
Compile + render node repos (from ACR):  SKIPPED     <- required
Module bundle (MeshWeaver.AI):           FAILURE
```

Branch protection was satisfied and auto-merge fired. The compile gate carries **no `if:`** — it is
deliberately unconditional — and it skipped anyway. So *"it skipped, therefore skipping was safe"*
is not an inference you may make.

**The rule:** a required context counts only when its conclusion is literally `SUCCESS`.

### 🚨 SKIPPED and ABSENT are NOT the same reading — and they fail in OPPOSITE directions

The sentence above used to say "skipped **or absent**". That is wrong, and the two halves are not
even the same kind of mistake:

| the required context… | classic protection | ruleset |
|---|---|---|
| **reported** `SKIPPED` / `NEUTRAL` / `CANCELLED` | **satisfied** — merges | **satisfied** — merges |
| was **never reported at all** (no check-run, no commit status) | **BLOCKS, forever** | satisfied — merges |

A skipped context is a check-run that *exists* and carries a non-failure conclusion; GitHub has an
answer and accepts it. A context that was never published has no row at all, and classic protection
renders it *"Expected — Waiting for status to be reported"* and refuses the merge until the end of
time. Nothing retries it, because nothing is going to publish it.

**Measured 2026-09-08, `MeshWeaver.Plugins#1453`.** That PR adopts the shared `node-repo-validate`
lane, which renames the job's context from `Validate node repos` to `validate / Validate node
repos` and splits the repo-specific half out as `Repo policy gates`. Its run
[`34191771207`](https://github.com/Systemorph/MeshWeaver.Plugins/actions/runs/34191771207) is
`success` across **all 106 jobs**, `validate / Validate node repos` included, and the PR still reads:

```
mergeable_state: blocked
required contexts (classic, MeshWeaver.Plugins):
  Validate node repos                            <- NO check-run, NO commit status  ← the block
  Compile every NodeType (vs core)               success
  Build + test the portal hosts                  success
  Module bundles / All selected bundles built    success
  Module bundles (floor) / …                     success
```

Confound ruled out before concluding: `required_pull_request_reviews` is `null`, `strict` is
`false`, and the repo's one ruleset (`Copilot review for default branch`) carries only `deletion`,
`non_fast_forward` and `copilot_code_review` — none of which blocks a merge. A required status check
is the only thing left that can.

**Why it matters in both directions.** Read as *"absent counts as satisfied"*, a repo that has
quietly stopped publishing a gate looks safe — that is the failure the rest of this page is about,
and it is real for a **ruleset** repo. Read the same way in a **classic-protection** repo, a PR that
is structurally stuck looks merely slow, and someone waits days for a run that can never appear. The
cure for the second is a branch-protection edit, never patience.

🚨 **Adopting a reusable lane is exactly this event**, which is why AGENTS.md says the context rename
happens *in the same change*. Sequence it deliberately: dropping the old context and adding the new
ones in one step blocks **every** open PR in that repo that has not yet merged the adopting change,
because none of them publishes the new names. Dropping the old name first, merging the adopting PR,
then adding the new names is the order that blocks nobody — and the gap between step two and step
three is a window in which the gate is advisory, so it is a step to finish, not to leave.

```bash
gh pr view <N> --repo <repo> --json statusCheckRollup \
  --jq '[.statusCheckRollup[]? | select(.name | IN("<required>","<contexts>","<here>"))
         | "\(.name)=\(.conclusion)"]'
```
Every required name must be **present** and read `=SUCCESS`. Count them — a missing row is a fail,
not an absence.

### 🚨 A skip-trapdoor made by STEP ORDERING — the first failing step silences every guard behind it

A gate that carries no `continue-on-error:` and no `if:` can still stop enforcing, and nothing in the
file looks wrong. **GitHub's implicit condition on a step is `success()`**, so in a job that runs many
*independent* guards as consecutive steps, the first failure skips all of them — and a `skipped` step
publishes no failure, while the job's one required context reports a single red about whichever guard
happened to be first.

**Measured 2026-09-19 on MeshWeaver.SocialMedia#210**, run `35431670104`, job `105867277548`. The
shared `validate` lane's vendored-resolver drift check failed with `32 code line(s) differ` and **16
steps reported `skipped` behind it**, among them:

| step | what stopped being enforced |
|---|---|
| `Every PR-reachable secret in this repo is asserted by a preflight` | the gate for the shape that bit Reinsurance#128 |
| `Every manifest.lock is current (and carries a version)` | a stale lock reaching a publish |
| `Every module's version matches its content` | a feature shipping to nobody (#878) |
| `No mapping in this repo's workflows writes a key twice` | the duplicate-key guard |
| `No pin comment names a commit this repo no longer pins` | abbreviated-sha / pin drift |
| `This repo's no-op set agrees with the platform's` | no-op parity |

`validate / Validate node repos` is a **required** context in all five satellites. Because
`platform-ref` defaults to `main` and the drift check fetches the canonical live, *every* satellite is
drifted from the instant a canonical change merges — so for the length of each re-copy wave, every
pull request in that repository was unguarded by all six of those checks, with one red about an
unrelated file as the only symptom.

**The reading to take from it:** a red does not tell you what a job *checked*. Only the steps that
reported a verdict were checked, and in a long guard job the count of `skipped` steps is the count of
guards that said nothing. `.../actions/jobs/<id>` lists each step's own conclusion — read that, not
the job's.

**The fix is two halves, and `!cancelled()` alone is only the first.** Dropping the implicit
`success()` also stops the *prerequisites* from masking, so a failed checkout would let every guard run
against an empty workspace — a wall of secondary reds, and for any guard that passes on an empty tree a
vacuous pass. So the last prerequisite publishes one output and every guard requires it:

```yaml
- name: The workspace and the tools are present — the ONE prerequisite every guard shares
  id: ready
  run: echo "ok=true" >> "$GITHUB_OUTPUT"
- name: <any independent guard>
  if: ${{ !cancelled() && steps.ready.outputs.ok == 'true' }}
```

That keeps the two failure modes apart, which is the whole property:

| what failed | what happens |
|---|---|
| a **prerequisite** (checkout, its history fetch, the tool/Python setup) | `ready` is skipped, its output is empty, every guard is skipped, and the prerequisite's own red is the verdict |
| a **guard** | `ready` is untouched, so every other guard still reports |

`.github/scripts/check-guard-step-masking.py` enforces both halves on two declared subjects —
`node-repo-validate.yml`'s `validate` (39 guards) and `dotnet-test.yml`'s `workflow-shell` (61 guards,
the job that gates `main-cd.yml`, the module lanes and every script a satellite fetches). It also
requires the prerequisites to *be* a prefix, matched exactly (a prefix comparison let
`actions/checkout-foo` satisfy `uses:actions/checkout`), refuses a job whose guard list is empty so it
cannot pass by having nothing to check, and refuses a readiness step that stopped publishing `ok=true`.

A guard's own *fetch* is deliberately **not** a prerequisite: its consumers run and fail naming the file
they could not open, which is a second red rather than a silent skip, and the fetch's `::error::` is the
root.

## Required ≠ meaningful, in both directions

Two independent facts, and confusing them costs time in both directions:

| | |
|---|---|
| **Required, and red** | blocks the merge |
| **Required, and it REPORTED skipped/neutral/cancelled** | **does not block** — GitHub treats it as satisfied |
| **Required, and never reported at all** | **blocks forever** under classic protection; does **not** block under a ruleset — see *SKIPPED and ABSENT are NOT the same reading* above |
| **Not required, and red** | does **not** block — but it is still evidence, and may be a real defect |
| **Not required, and it is the job your diff changes** | do not arm auto-merge: the PR can land *before* that job finishes, putting a broken gate on `main` where it renders as a green tick |

**Measure protection per repo; never trust a table, including this one.** And check *both* mechanisms:
classic branch protection **and rulesets** — `GET /repos/{owner}/{repo}/branches/main/protection`
answers `404 Branch not protected` for a repo governed by a ruleset, which reads as "no protection
at all" and is wrong.

```bash
gh api repos/<owner>/<repo>/branches/main/protection --jq '.required_status_checks.contexts'
gh api repos/<owner>/<repo>/rulesets --jq '.[]|"\(.id) \(.name) \(.enforcement)"'
gh api repos/<owner>/<repo>/rulesets/<id> \
  --jq '.rules[]|select(.type=="required_status_checks").parameters.required_status_checks[]?.context'
```

A **dynamic matrix cannot be a required context** — the shard names change. Require a single
**collector** job that `needs:` every shard and fails if any did not succeed (core does this with
`Consolidate test results`). Requiring shard names by hand orphans a required context the moment the
shard count changes, and it then waits forever.

## 🚨 A narrow instrument does not answer a wide question — and it is RIGHT while you misread it

Most rules on this page are instances of one shape, and it is worth naming because the instances
keep arriving in new costumes. **In every case below the instrument was working correctly.** There
was no bug to find, no error to notice, and no amount of care would have helped: the *reading* was
wrong, not the measurement. That is exactly why this class needs a control rather than diligence.

Five were measured in one session (2026-09-12, while triaging #890 and #2543), by **two different
readers** — so this is not one person's blind spot:

| the instrument | what it actually answers | what it was read as | what falsified it |
|---|---|---|---|
| `pendingWork=0` on **one** `[STALE-CALLBACK]` record | the pool's depth at that instant, for that correlation | *"the pool is idle"* — a live starved-pool hypothesis declared dead | max `pendingWork` in the **same log** is 606; across the 15 jobs, **2,501** |
| a `GATE FAILED — tests:` filter | how many failures matched **that stage** | *"8 occurrences"* | all 15 carry the same 120 s bound and the same `CreateOrUpdateNodeRequest@portal/nodeops` stall trail — **15**; the filter set the count, not the phenomenon |
| `gh api … 2>/dev/null` across eight repos | whatever survived a **discarded** stderr | *"0 issues, 0 PRs — every repo clean"* | every call was being refused `403`; a total refusal read as a clean sweep |
| `gh api rate_limit` → `core: 5000/5000` | the **PRIMARY** quota | *"not rate limited"* | the refusals were the **SECONDARY** limit, which that endpoint does not report |
| `gh api /apps/<slug>` permissions | what the App **declared** | *"the App can write issues and workflows"* | the **installation** carries `contents`, `metadata`, `pull_requests` only — a token minted from it can do neither |

### The test to apply before you publish a claim

**Name the question the instrument actually answers, then say why that is the same as the question
you asked.** Where the two differ, either widen the instrument or narrow the claim. Both are cheap.
Publishing the gap is not — each row above was repaired only after someone else re-measured.

### The tell, per family

- **A spot value standing in for a distribution.** State the **maximum and the count**, never the
  sample you happened to read. One record describes a moment; a hypothesis about a process needs the
  shape of the whole series.
- **A filtered count standing in for an occurrence count.** State the **filter** beside the number.
  *"8 matched `tests:`"* and *"8 occurred"* are different sentences, and only the first is a
  measurement.
- **A suppressed error standing in for a measurement.** `2>/dev/null` turns a refusal into a zero.
  **Check the exit code**, or do not suppress. A zero that cannot distinguish *"none"* from *"could
  not ask"* is not a result.
- **A healthy meter standing in for the thing that actually refused.** Read the **refusal**, not the
  meter. `5000/5000 remaining` while every call is refused is the documented signature of the
  secondary limit — the meter is honest and answering a different question.
- **A WINDOW standing in for a result set.** `grep … | head -N`, `| tail`, `--per_page`, a listing's
  first page: each shows a window, and an absence read off one is not a measurement. Measured
  2026-09-18 on MeshWeaver.Plugins, asserting its `ci.yml` never calls the shared validate lane —
  `grep -nE '…|node-repo-|…' ci.yml | head -20`. **The pattern matched.** `ci.yml:2745` is
  `uses: …/node-repo-validate.yml@main`, and it was **match 36 of 50**; `head -20` cut at match 20
  (line 1039) of a 4,983-line file. 🚨 **A CORRECT pattern is the dangerous case** — a wrong one
  announces itself by returning nothing plausible, while a right one in a truncated window returns
  real, on-topic hits, so the window looks like the answer. The habit is `grep -c` **before**
  `grep | head` — if the count exceeds the window, the window is not the answer — and to
  positive-control the *window*, not the query: grep for something the subject is KNOWN to contain
  and confirm that hit lands **inside the window you are actually reading**. State it as *N of M*,
  never as *N*. It is the same shape as
  [Adoption and the Sweep Count Different Things](/Doc/Architecture/AdoptionAndTheSweepCountDifferentThings),
  arriving by a different road: 20 shown, 50 matched, and nobody asked how many there were.
- **A SUBSET standing in for the sweep.** In the same measurement, five of six vendored copies were
  compared by **blob sha** against the canonical and correctly reported byte-identical; the sixth was
  settled by a *title search for an open PR* instead. The five were a set being compared and the
  sixth was a question about existence, so it felt like a different kind of question — **it was
  not**, and the weaker check was the one load-bearing for the conclusion. A sweep gets its hole
  exactly where a cheaper instrument was substituted, so name the check that decides and run *that*
  one on every member. Here the blob comparison would have caught the gap whatever the other answer
  had been: the sixth copy had been merged and never applied (`changed_files=0`).
- **A declaration standing in for an effective capability.** An App's own page lists what it *asked
  for*; the **installation** lists what it was *granted*. Measured 2026-09-12: `meshweaver-cloud`
  declares `contents, emails, issues, metadata, pull_requests, workflows`, and its installation on
  this org carries `contents, metadata, pull_requests` — so `issues`, `workflows` and `emails` are
  declared and absent. Read `orgs/<org>/installations`, not `/apps/<slug>`. This one is not a CI
  instrument at all, which is the point: the shape is about how an answer is read, not about CI.

### Why a control catches this and care does not

A broken instrument announces itself: an exception, a mismatch, a number that cannot be right. A
narrow one does none of that — it returns a true value, promptly, in the expected shape. The only
thing that separates *"what I measured"* from *"what I claimed"* is an artefact that would have come
out differently had the claim been false, so the discipline is the same one this repository applies
to gates: **an assertion that cannot fail is not an assertion.** Before a number becomes a verdict,
say aloud what reading would have refuted it, and go and look for that reading.

Two neighbouring pages carry the same lesson from other directions:
[Adoption and the Sweep Count Different Things](/Doc/Architecture/AdoptionAndTheSweepCountDifferentThings)
(two instruments, neither wrong, neither a census) and
[The Release Gate's Denominator](/Doc/Architecture/ReleaseGateDenominator) (a rate is meaningless
until you state what it is over).

## 🚨 An annotation belongs to an ATTEMPT, not to a run — a partial re-run erases it

**`GET /actions/runs/{id}/jobs` answers with the LATEST attempt's job records.** After
`rerun-failed-jobs`, GitHub re-creates a record for *every* job of the new attempt — including the
ones it did not re-run — and **those records carry none of the earlier attempt's annotations**. The
run still reads `success`. Anything that reads a fact out of an annotation therefore loses it, with
no error and no red anywhere.

Measured 2026-09-16 on MeshWeaver.Plugins (#4491):

```
main run 35073843357 resolved 3.0.0-ci.8721, died on an artifact-service 403
(FinalizeArtifact; tests failed: 0), was re-run, concluded SUCCESS

attempt 1, job 104721425856 (Resolve the released platform)  ->  annotation present
attempt 2, job 104732056546 (same job, NOT re-run)           ->  0 annotations
a PR's resolver, 6 minutes later                             ->  "no 'Platform for this run'
                                                                  annotation — skipped"
                                                             ->  pinned every PR to #8716
```

`main` had demonstrably passed on 8721, and every open pull request in the repo went on resolving
8716 — including the one adopting a core capability that only exists from 8721 onwards. The
resolver's sentence for this was *"`main` has not passed on it yet"*, which was **false**.

**If you read an annotation, say which attempt you mean.** `/actions/runs/{id}/attempts/{n}/jobs`
serves one attempt's records. Walk attempts **newest-first** and take the first that carries what
you are looking for: a genuine re-resolution (a full re-run, or a re-run *of that job*) then still
decides, and an older attempt is consulted only where the newer record is silent — which is exactly
the carried-over case. Taking the oldest instead would let a stale verdict outrank a fresh one.

This is the same attempt-scoping trap as `rerun-failed-jobs` reusing the previous attempt's
artefact (#4303): a re-run is not a re-execution of the run, and the parts it did not re-run keep
neither their outputs nor their annotations in the new attempt's records.

🚨 **An attempt you could not READ is not an attempt that was SILENT — and only silence licenses
the walk.** The fallback above is sound because "this attempt's records came back, and carried no
such annotation" is a fact about the attempt. An HTTP failure is a fact about the *network*: it
proves nothing, and the newer attempt is precisely the one that may hold a genuine re-resolution.
Treating the two the same publishes an older attempt's stale verdict under a note asserting the
newer attempt carried none — false in exactly the way the sentence this page opens with was false,
and harder to catch because it now cites an attempt number. So an unreadable attempt **stops the
walk and skips the run**, and the note says which of the two happened. A reader who cannot tell
"nothing was there" from "I could not look" has the same defect as a sweep that reports `0` without
its denominator.

The second-order version bit the same change: the fallback added a **second** note for each run it
rescued, and the loop's bound was `len(notes) >= limit` — a proxy for "runs examined" that was only
ever true while every run emitted exactly one note. Twelve rescued runs reached the bound after
six, halving the evidence and answering with a lower ceiling. **A bound must count the thing it
names.**

---

## The same trap in the tools you write to watch CI

Two bugs that make a monitor lie, both hit in one session:

- **`jq`'s `//` does not fall through on `""`.** Only `null` and `false` trigger it, and an empty
  string is truthy — so `.conclusion // .status` yields `""` for a queued check, and "not yet run"
  becomes indistinguishable from "no failure".
- **An empty or partial rollup is vacuously green.** "No failures and nothing incomplete" is *true*
  of a PR with zero checks. Decide readiness by asserting the **required set is present and
  SUCCESS**, never by the absence of failures.
- **A PAGE of check-runs is not the commit's check-runs.** `commits/<sha>/check-runs` caps at
  `per_page=100`, and a main commit here carries far more than that — every workflow, every matrix
  leg, both synthetic probes, the combo verdict. Measured 2026-09-17 on `be8f452c79`: **278**
  check-runs, page 1 holding **zero** named `Consolidate test results` and page 3 holding its two,
  both green since the evening before. CD's gate filtered page 1 in `jq`, read `absent/none` for a
  green commit, and walked delivery back to its parent for six hours until the parent's heal budget
  ran out and CD reported delivery STUCK with main green (#4526). **Name the check in the request —
  `&check_name=<name>` — so the API filters server-side**; `CheckRunReadsAreServerFilteredGuard`
  holds every workflow to that. `--paginate` is not the alternative it looks like: the flag says
  pages were requested, not consumed, and `gh api --paginate --jq …` piped into a `read -r` still
  takes the first line. The failure grows with the repository's check volume, so a reader that
  works today starts lying later, silently.

### 🚨 A comparison against an UNVALIDATED read turns a refusal into "ACT NOW"

The watcher bugs above withhold an action. This one **manufactures** one, which makes it strictly
worse: it arrives wearing urgency and a ready-made remedy.

A watcher polled a file's sha and compared it to a baseline:

```bash
gh api "repos/.../contents/scripts/resolve-platform.py?ref=main" --jq '.sha' || true
```

Measured 2026-09-19 09:08:35Z, under a secondary rate limit, `--jq '.sha'` yielded the **refusal
body**, `!=` against the 40-hex baseline was therefore true, and the watcher announced:

```
ACT NOW: main's resolve-platform.py MOVED (3361378ce5e2… -> {"message":"API rate limit exceeded
for user ID …","status":"403"}) — merge origin/main into <branch> and push
```

Nothing had moved; the file was byte-for-byte unchanged, confirmed locally with no network. Acting on
it would have merged `main` without the awaited fix and spent a CI run during the limit.

Two individually-correct decisions compose into it. `|| true`, so one transient refusal cannot kill a
long watch — right. `!=` against the previous value as the change test — right. Together they mean
**any failed read is a positive result.**

**The question to ask before arming any watcher: what does this print on a 403? If that is its success
branch, it is not a watcher.** Three guards, and the first is the one that matters:

- **Validate the value's SHAPE before comparing it.** A sha must match `^[0-9a-f]{40}$`, an md5
  `^[0-9a-f]{32}$`, a count must be all digits. Anything else is not a value.
- **Make "could not read" its own printed outcome**, distinct from both *changed* and *unchanged*, and
  back off after it. Three states, never two.
- **Dry-run it against a forced failure** — unset the token, or point it at a 404 path — and read what
  it says. A watcher whose failure branch has never been exercised is a guess.

The same hole is easy to leave in an equality test rather than an inequality one: a monitor comparing
two digests and announcing agreement on equality would, under a total refusal, digest two error bodies
and declare them identical. On this occasion they differed only because each 403 carries a distinct
request id. That is luck, not a design.

### 🚨 The CREATION limit is a second secondary limit, and `gh` porcelain exits 0 under it

The primary/secondary table above concerns reads. There is a **separate** secondary limit on content
creation — issues, pull requests, comments, and review-thread replies — and it is reached
independently. Measured 2026-09-19 08:51:44Z:

```
$ gh issue create --repo … --title … --body-file …  > out.txt 2> err.txt
exit=0        out.txt: empty        err.txt: empty        issue: DOES NOT EXIST
```

**Exit 0, both streams empty, nothing created.** The same request over REST named it at once:

```
$ gh api --method POST repos/…/issues --input payload.json
{"message":"You have exceeded a secondary rate limit and have been temporarily blocked from
content creation. …","status":"403"}
```

So create over REST, and build the JSON with `python3 json.dumps` rather than interpolating a body
into a shell string. Two consequences worth stating plainly:

- **A lost review-thread reply is invisible in exactly the way that matters.** The review gate stays
  red, `mergeable_state` stays `blocked`, and the agent that "replied" has no signal it did not. Then
  re-running the gate looks like the gate is broken when the thread is genuinely unanswered.
- **The two limits are not ordered.** Creation was refused at 08:51Z while reads still worked; reads
  were refused at 09:11Z with `/rate_limit` reporting `core: 5000/5000`. Neither one predicts the
  other, and the read meter reports neither.

🚨 **And the refusal wears a DIFFERENT SHAPE per endpoint, so never key a retry or a watcher on its
text.** Measured 2026-09-19, one limiter with three faces:

| what you called | how it refuses |
|---|---|
| `POST …/issues` over REST | `403`, and the body names the secondary limit |
| `POST …/pulls/{n}/comments/{id}/replies` | **`422 {"resource":"PullRequestReview","code":"abuse","field":"base"}`** — not a 403, and it names no rate limit at all |
| `gh issue create` (GraphQL porcelain) | exit 0, both streams empty, nothing created |

A review reply was refused four times over 43 minutes that way and landed on the fifth attempt. A
watcher grepping for *"secondary rate limit"* sees nothing in that case, and a watcher checking only for
`403` sees nothing either. **So key the decision on whether the `id` or `number` you asked for came
back, not on what the refusal said.**

🚨 **But an absent id means NOT CONFIRMED, never "it did not happen"** — and that difference decides
whether a retry is safe. A response can be lost or suppressed *after* the server has committed, so
retrying on the absent id is how a duplicate gets created. It matters most in the case this section is
about: a stub reply is repaired with `PATCH`, and a second `POST` leaves the first standing beside it.
**Before retrying, re-read the collection and look for your own content** — the same baseline rule
stated below for auditing somebody else's posts, applied to your own retry.

The two API-boundary shapes above, the `403` and the `422`, genuinely did create nothing. It is the
porcelain's silent exit 0 that is ambiguous, because silence from a wrapper says nothing about what the
server did.

The read limit is independent of the creation one and can land immediately after a successful write — it
did, six seconds after that reply finally posted, delaying its verification by eight minutes. So budget
for the verification read as well as the write, and do not treat a failed read-back as a failed write.

**Verify every creation by reading it back — and note that this is TWO questions, not one.**

- **Did THIS write create a comment?** Only the `id` (or `number`) in the write's own REST response
  answers it, re-fetched by that id. Nothing derived from the body can: a stub, or an earlier session's
  reply, matches a length as easily as it matches an `in_reply_to_id`.
- **Does that comment carry the content intended?** A byte-compare of the fetched body against the file
  that was posted.

So an existence predicate such as `select(.in_reply_to_id == <ID>)` proves neither — it passes on a
stub, and a retrier keyed that way reported success while a wrong reply sat there untouched. A length
check proves neither either. Where no response id is available, for instance when auditing somebody
else's earlier posts, an existence check is sound **only** against a baseline measured *before* the
write; that baseline is what makes it proof, not the read-back.

### 🚨 A lookup that cannot reach its target answers the DEFAULT, forever, on every machine

The other direction of the same defect: the gate runs, its input is a constant, and the constant is
one the gate refuses. Between 21:37Z and 05:05Z on 2026-09-12 **no MeshWeaver.Plugins bundle
reached the registry** — every module of every batch failed the module-pack lane's publish
stand-down with

```
✗ matrix entry missing package/module/project: {}
##[error]could not decide whether a newer trunk commit reaches this module (exit 1) — 'cannot tell' never publishes
```

The step asked its state tool for the whole matrix entry as `bk get --module "$MODULE" entry
--default '{}'`. But `get` resolves a recorded fact, **else a dotted path *inside* the entry** — so
the key `entry` asked for a field *named* `entry` inside the matrix entry, which no matrix entry
carries. It therefore answered `{}` for every module, on every runner, deterministically. There was
no way to ask that tool for the whole entry at all.

**Two readings this produced, both wrong, and both cheap to rule out:**

- *"It is the runner image."* The window coincided exactly with those legs running on a different
  runner set, and the same legs published fine on the next run — so the shape read as "the tool sees
  a different state file or checkout on that image". It is not: the call is machine-independent and
  reproduces in one command on a laptop. **The run that "worked" never executed the line** — it sat
  in the `else` branch of a three-way check, reached only when the trunk tip differs from this run's
  commit *and* the two trees differ; that run's commit WAS the tip, and its log says so
  (`this run's commit … IS the trunk tip — nothing newer; publishing`).
- *"Something merged in between fixed it."* Nobody touched the tool or the step. A branch that is
  not taken is not a fix.

**The generalisation.** A default is only a default if the lookup can also *succeed*. Where the key
can never resolve, `--default X` is not a fallback — it is a hard-coded X wearing a lookup's
clothes, and it turns the gate downstream into a constant. So: give a reader that must return a
whole structure **its own subcommand with no default**, which reds naming what it wanted and the
phase that wanted it; keep `--default` for keys that name a real field, where absence is a genuine
state. The pin is a self-test case that round-trips the structure and asserts the absent case is a
red — added first, and **watched to fail** against the unfixed tool (#4140).

### 🚨 A diagnostic printed on a DATA channel is eaten by the consumer

The same step pipes the reader into `jq`. The tool wrote its `::error::` to **stdout**, so on the
absent case `jq` consumed the message and answered `parse error`, naming no module — the red was
real, and said nothing about which module or why. Any subcommand whose stdout a caller pipes or
captures (`PACKAGE="$(bk get …)"`) must write every diagnostic to **stderr**: on stdout it is either
swallowed, or assigned into the variable as though it were the value. The runner annotates
`::error::` from stderr, and the log shows stderr regardless.

## A green gate can be answering with evidence it did not produce

The traps above are about a check that never *ran*. This one is worse: the check runs, does its
accounting, and reports SUCCESS **from another job's evidence**.

**Artifacts are RUN-wide, not call-wide.** A reusable workflow that a repo invokes twice in one run
— `MeshWeaver.Plugins` calls `node-repo-module-pack.yml` as both `modules-floor` and `modules-rest`,
on every run — puts both calls' artifacts in ONE namespace. Two uploads of the same name are
*accepted*: Plugins run `33487032213` carried two artifacts literally named `workspace-build`
(25.8 MB and 950 kB), and `download-artifact` resolved the name to one of them. The floor's pack job
read the other call's workspace and died on *"the global build wrote no closure manifest for
MeshWeaver.Markdown.Collaboration"* — a REQUIRED gate flipping green/red on `main` with **no source
change**, alternating by which call won the name (Plugins#1077).

**A discriminator with a shared default is not a discriminator.** The `lane-id` input existed for
exactly this and did not close it, because its default was the same literal for both calls and
neither caller set it. The fix is to *derive* the key rather than trust the caller: `select` now
computes a lane key from the call's own `modules:` matrix — unique per call by construction, with no
input a caller can forget — and every artifact the call drops is named and stamped with it.

**Three rules generalise out of it:**

1. **Name every artifact for the CALL, not the workflow.** If two invocations can coexist in one
   run, the name must carry something that differs between them, derived — not an input someone
   remembers to pass.
2. **Scope the download to match, and check the stamp anyway.** A `pattern: foo-*` glob is one edit
   from being widened back; the producer stamping its lane INTO the file means the consumer can
   still refuse foreign evidence when it is.
3. **An answer a gate composes must fail closed.** `bundles-built` is what a required gate depends
   on, so zero evidence, foreign evidence and evidence that does not record what it attests are all
   *false* — never a silent true. A marker saying "an artifact was uploaded" leaves
   present-but-uncomposable reading as green, so the marker records the bundle it attests **and**
   the closure the build resolved, and the verifier requires both.

🚨 **Preserve the reason the evidence is dropped EARLY.** The built marker lands before the module's
tests deliberately, so a red suite cannot read as "bundle missing" to a gate that only *composes*
bundles (#2710, Plugins#937). Strengthening what the marker claims must not drag test results into
it — "the bundle is complete and usable" and "the module's tests passed" are different questions,
answered by different contexts.

**And the acceptance criterion for a fix in this class is REPEATED green.** A defect that alternates
run to run produces single greens by itself; one green run is what it looks like, not evidence it is
gone.

### 🚨 A green PRODUCER that SKIPPED its upload — the red lands four jobs downstream, naming an artifact

The section above is about a consumer reading the *wrong* evidence. This is its mirror: the
producer runs, decides it has nothing to hand over, **skips its upload step**, and is painted
green — while every consumer computes the opposite answer and dies on `Artifact not found`. The
failing job is not the job that is wrong, and its message names a file, not a predicate.

🚨 **The two share a symptom and need opposite fixes**, so the reading below starts by telling
them apart rather than assuming this one. `Artifact not found` is a *symptom of at least four*
faults; what follows is ONE measured incident of ONE of them.

Measured on Plugins#1853, run `34842094978`, on a **docs-only diff** that reddened 14 jobs across
five required gates:

```
select:            scope: narrowed — 7 of 41 module bundle(s) are reachable from this diff
                   publication reuse: 7 unchanged gate dependencies      ⇒ build-modules = []
build-workspace:   MODULES: []
                   ✓ the selection carries no `build: container` entry — nothing to emit
                   Hand the workspace to the matrix ................... SKIPPED
                   job conclusion .................................... success        ← green here
pack (each leg):   plan: BUILD MeshWeaver.AI (ledger off)             ← fetch_workspace = true
                   ##[error]Unable to download artifact(s): Artifact not found for name:
                            workspace-build-catalog-f42e4aa2ddd9                      ← red here
```

The two sides computed the same predicate from two different lists: the global build from
`steps.reuse.outputs.*` (post-annotation) and the pack matrix from `steps.ledger.outputs.modules`
(pre-annotation), so a reused entry's leg still planned `BUILD`. Fixed in #4312 by cutting the
matrix from the annotated list.

**How to read one of these, in order:**

1. **Read the FAILING step's own annotation first, and do not skip to a cause.**
   `Artifact not found for name: X` says only that *this download did not resolve `X`* — it is not
   by itself evidence that an upload was skipped. **Four different faults print it**, and they need
   opposite fixes:

   | cause | how to tell | fix |
   |---|---|---|
   | the producer **skipped** its upload | producer job green, its upload step `skipped` | make the two guards read one list (this section) |
   | the producer **failed** before uploading | producer job red | triage the producer's own failure |
   | the name is **scoped to the wrong call** | the artifact EXISTS in the run under a name that differs — or two artifacts share one name | derive the key per call (the section above) |
   | it **expired or was deleted** | the artifact is absent from the run and `retention-days` has passed, or the run is old | re-run the producing lane; do not chase the consumer |

   Answer that first with `gh api repos/{o}/{r}/actions/runs/<id>/artifacts --jq '.artifacts[].name'`
   — the run's own artifact list separates "never produced" from "produced under another name"
   in one call. Only the first row is the shape documented here. Do not re-run before you know
   which row you are in.
2. **Once it is row 1 or 2: find the producer and read its STEP LIST, not its conclusion.** A
   `skipped` upload under a `success` job is the whole bug, and the job summary will not mention
   it. Read `actions/jobs/<id>` and look at `steps[].conclusion`.
3. **Then compare the two `if:` expressions.** Here: `if: steps.build.outputs.built == 'true'` on
   the upload against `if: steps.plan.outputs.fetch_workspace == 'true'` on the download. Two
   independently computed guards over one fact is the defect; making both read the same list is
   the fix.
4. **Count the cascade before you believe the blast radius.** One skipped upload produced
   `All selected bundles built` = red, then `The Tests-area gate's inputs are present`
   (*"a needed job did not succeed … module result=failure"*), the Tests-area ratchet, four
   `test-repos / Gate shard N/4` (*"external modules were requested but none were assembled"*),
   `Compile + render node repos`, `Assemble the compile-check reference set`,
   `Compile every NodeType` and `Every gate executed`. Nine of the fourteen red jobs were reporting
   the same fact. Triage the earliest, never the loudest.

**Two things this shape is NOT, and both were guessed before it was measured.** It is not the
self-hosted pool dropping jobs — that shape is **zero steps and no log**, and this job ran 16 of
its steps on a named live runner. And it is not the diff: a docs-only change cannot reach a module
bundle, which is the signal to go measure rather than to re-run. `rerun-failed-jobs` would not have
helped either, since it reuses the run's original resolution; only a whole-workflow re-run picks up
a lane fixed on `main`.

**The generalisation.** A producer whose hand-over is conditional owes its consumers a *reason*, not
a silence. Either the guard is shared (one computed output both sides read), or the consumer's
failure has to name the predicate — `the workspace build emitted nothing for this lane because the
selection carried no container entry` beats `Artifact not found` by the whole distance between a
diagnosis and a symptom.

## Reading a RED shard: the exit marker classifies it, the log text does not

A red shard says *why* in exactly one place — the **exit marker** printed by "Fail on non-zero
project exit". Read that line; do not count words in the log.

```
[CI] MeshWeaver.Hosting.Monolith.Test exit=1 TESTFAIL (1 failing test(s) recorded in trx —
     xunit v3 exits with the failure count; the host completed normally) elapsed=402s

[CI] MeshWeaver.Hosting.Monolith.Test (part 1/2) exit=1 MASKED (trx records 2 failing test(s),
     which does not explain exit=1 — host crashed after streaming results) elapsed=462s
```

| classification | what the marker CLAIMS | what to do |
|---|---|---|
| `TESTFAIL` | ordinary failing tests; the host completed normally | read the named tests — a flake cluster or a real regression |
| `MASKED` | the exit code is not explained by the trx, so the host is presumed to have died after streaming results | **verify before believing it** — see below |
| `TIMEOUT` / `SIGNAL` | the host died mid-run | the host, not the test — [Debugging Native Crashes](/Doc/Architecture/DebuggingNativeCrashes) |

**These attribute differently, so the classification has to be right.** A test failure is a problem
in that test's own path; a crash is a process-level failure that can take unrelated tests down with
it. Treating a failing test as a crash invents a trunk emergency; treating a crash as a flake hides
one.

🚨 **`MASKED` is DERIVED, not observed — and it has been wrong.** The marker infers "the host died"
from `rc != <failures recorded in trx>`, which assumes xUnit v3 exits with the failure *count*. It
does not always: on 2026-08-30 a `MeshWeaver.Hosting.Monolith.Test` shard recorded 2 failures and
exited **1**, and the harness called that `MASKED (host crashed after streaming results)` — but the
assembly had printed its own summary and completed:

```
=== TEST EXECUTION SUMMARY ===
   MeshWeaver.Hosting.Monolith.Test  Total: 331, Errors: 0, Failed: 2, Skipped: 0, Not Run: 0, Time: 461.193s
```

**That line is the decisive evidence, and it outranks the marker.** An assembly that prints its
execution summary ran to completion, whatever the classification says. A genuine crash looks like
the *absence* of it — no summary for that assembly, a signal exit, or a non-zero exit with nothing
recorded at all. (The classifier rule itself is being corrected in #2738; until that lands, and for
reading any older run, check the summary yourself.)

🚨 **`HOST_CRASHED` appearing in the log is NOT evidence of a crash.** The "Summarize test failures"
step *echoes its own script*, including the sentence explaining the mechanism —
*"…a crashed/killed host is NOT covered by this silence: since #2495 it is written INTO the trx as a
`<project>.HOST_CRASHED` failure by `.github/scripts/record-host-crash.py`…"*. A `grep -c HOST_CRASHED`
over the log therefore returns hits on runs where **no host crashed at all**, and the count scales
with the number of steps that echoed the sentence, not with crashes. Measured 2026-08-30: two main
reds were reported as "2× then 4× HOST_CRASHED" when the markers said one `TESTFAIL` (a
`SilentReadNackTest` flake) and one `MASKED` that the execution summary then showed was **also just
failing tests**. Neither run crashed. It is the same family as the monitor traps above — a line a
*script* printed is not a measurement.

**Then attribute by REACHABILITY before by adjacency.** The merge that happens to sit under a red is
the first suspect and usually the wrong one. Open the failing test project's `.csproj` and ask
whether the suspect diff is even on its reference graph: on 2026-08-30 an Orleans silo-stop change
(#2726) was suspected for a `MeshWeaver.Hosting.Monolith.Test` crash, and
`MeshWeaver.Hosting.Monolith.Test.csproj` references no Orleans project at all — the monolith host
runs no Orleans. One `grep` closed it. The same check exonerated a CI/tooling diff (#2721) whose own
issue body had already said so.

### 🚨 When reachability CANNOT exonerate you: ask whether your path EMITTED anything

Reachability only ever answers *no*. When the failing test's project **does** reference everything
your diff touched — and a plausible mechanism exists — the graph says nothing, and this is exactly
the moment the temptation to re-run "to see" is strongest.

The decisive question is cheaper than the reasoning: **did the code you changed produce any output
in the failing test?** Every interesting path in this codebase logs a tag. Grep the failing test's
own lines for it:

```bash
gh run view <run-id> --repo Systemorph/MeshWeaver --job <job-id> --log > job.log
grep "<FailingTestName>" job.log | grep -cE "LATE_NACK|OwnerDisposing"   # a tag YOUR change emits
```

Measured 2026-08-31 on #2868, which changed four assemblies that `MeshWeaver.Graph.Test` depends on,
with a real mechanism to worry about (the change moved a callback onto a different thread):

```
the changed path's tags in the failing test's own output:   0
the same tags elsewhere in that shard:                     30
```

**A change cannot hang a test through a branch the test never takes.** Zero-against-thirty is
positive evidence that the branch was not entered, not an absence of evidence — the thirty prove the
grep works. Re-running then confirmed it, but the attribution was already sound before the re-run,
which is the point: a re-run you cannot predict the result of is a coin toss, and a re-run you can is
a confirmation.

**Corroboration worth checking in the same breath:** two *unrelated* PRs going red in the same window
on different tests and different shards is the signature of an ambient population, not of either
diff. That happened here — the sibling PR's diff was CI YAML and a plugin catalog, and it failed on a
chart-render flake.

### 🚨 A flake's PASSING re-run is the CONTROL ARM — do not throw it away

When a flake is re-run and goes green on the same commit, you own something CI almost never gives
you: **a controlled experiment.** Same commit, same shard, same job, same runner generation — one
variable, the outcome. It is routinely discarded as "the problem went away".

**Method.** Count each candidate tag inside the FAILING test's own log lines, then inside the
PASSING run's lines for the same test, and compare:

```bash
gh run view <run> --repo Systemorph/MeshWeaver --attempt 1 --log > fail.log   # ← see the trap below
gh run view <run> --repo Systemorph/MeshWeaver --log            > pass.log
for t in "TagA" "TagB"; do
  printf '%-34s fail=%s pass=%s\n' "$t" \
    "$(grep '<TestName>' fail.log | grep -c "$t")" \
    "$(grep '<TestName>' pass.log | grep -c "$t")"
done
```

**Measured 2026-08-31 on the `FutuReAnalysisTest` flake:**

| tag | FAIL (50 s) | PASS (7.7 s) |
|---|---:|---:|
| `$type … NOT registered` | 66 | **122** |
| `is not registered` (the upsert refusal) | 0 | 0 |
| `Dropping StreamEndedEvent` | **8** | 2 |

**The suspected cause was MORE frequent in the run that passed.** A separate issue had inferred that
this flake was "the test-visible face" of *its* defect, on the strength of that shared
`NOT registered` wall. The wall is ambient in that suite — heavier in the PASS — so it cannot be
causal, and one table retired a cross-issue link that had stood for days and would have sent someone
to the wrong subsystem.

**The rule:** a tag present in BOTH windows is ambient. Only a tag whose count moves *with* the
outcome is a candidate; absent-in-pass plus present-in-fail is the shape worth chasing.

🚨 **The trap that makes this fail silently:** `gh run view --job <id> --log` returns the **LATEST
attempt**. After a re-run, that is the transcript that **passed** — same job id, no marker saying so.
Pass `--attempt 1` for the failure. The tell is inside the data: the window ends `[PASS]`, or its
span is far shorter than the reported failure duration. Check the span before analysing anything.

**Corollary:** never re-run a flake and move on without first pulling `--attempt 1`. The failing
window has a retention shelf life, and once it is gone the control arm is worthless — there is
nothing left to compare it against.

### 🚨 A host-cap kill DESTROYS the hung test's transcript; a `methodTimeout` kill KEEPS it

The two ways a hang ends a test are not equally useful, and the difference decides where to look:

| how it dies | transcript of the test that hung | usable? |
|---|---|---|
| host cap (`exit=124`, `HOST_CRASHED`) | **destroyed** — no trx entry, no captured stdout | no |
| xUnit `methodTimeout` (30 s, `test/xunit.runner.json`) | **written in full** | yes |

So the ambient hang family **erases its own defining artefact**: an investigation that waits for a CI
occurrence and reads the artifacts is reading everything *except* the thing that hung. Measured
2026-08-31 — a crashed shard held 21 `Dropping StreamEndedEvent` and 3 `ADVANCE_WITHOUT_HANDOFF`,
while the hung test's name appeared **nowhere**, so nothing could be established about whether the
two co-occurred.

**Hunt the method-timeout instances instead.** They carry the window in full, and they are routinely
discarded as ordinary flakes. One found the same night showed the pair 12 ms apart on the same path,
inside the hang:

```
02:46:41.286  Dropping StreamEndedEvent for stream _FIj…
02:46:41.298  [UpdateQueue] ADVANCE_WITHOUT_HANDOFF path=logonuser
02:46:41.300  Dropping StreamEndedEvent for stream P3X…
              ── 25 s of complete silence ──
02:47:06.218  TEST FAILED: The operation has timed out
```

A burst of work followed by *total* silence to the deadline is
[/debug](/Doc/Architecture/DebuggingMessageFlow)'s signature for a **dropped reactive emission**, not
a lock — idle cores and silence are never a hot loop.

## 🌍 The i18n mirror — deal with it routinely, not as an incident

Core owns `src/MeshWeaver.Messaging.Hub/Localization/strings.{en,de}.json`. MeshWeaver.Plugins
mirrors them at `clients/react/src/i18n/strings.{en,de}.json`, and its `RN app + web clients` job
asserts the mirror matches core — **at a PINNED commit**, recorded in
`clients/react/src/i18n/catalog-source.json`, not core's `main`.

🚨 **READ THAT LAST CLAUSE BEFORE THE ROUTINE BELOW — this page said "matches core `main`" until
2026-09-07, and that is the belief #3596 is about.** The pin changes the signal completely, and in
the direction that hurts: a core catalog change reds **nothing**, anywhere, and the mirror goes
stale in silence until somebody moves the pin. Measured 2026-09-07 — 1372 keys at the pin against
1399 on core's `main`, **27 behind**, and value drift over the 1372 shared keys of **exactly zero**.
That zero is the mechanism: the guard compares values, every shared value matched, both repos green,
and the React/RN clients rendered a raw key in both languages for 27 strings. `AGENTS.md` records
the same shape at 70 keys on 2026-09-04, so it recurs.

**What that means for reading a red.** The loud failure below is the *unpinned* behaviour and it
still describes what you see once the pin MOVES — a mirror sync PR reds every open Plugins PR while
it lands. What it no longer describes is the steady state: between pin moves there is no red at all,
so **absence of an i18n red is not evidence the mirror is current**. Check it, do not infer it:

```bash
gh api repos/Systemorph/MeshWeaver.Plugins/contents/clients/react/src/i18n/catalog-source.json \
  --jq '.content' | base64 -d | jq '{ref, keys}'
```
against `jq 'length' src/MeshWeaver.Messaging.Hub/Localization/strings.en.json` on core `main`.
Core's `i18n catalog (mirror sync handed over)` gate now refuses a catalog change that does not
declare the handover, so the debt is at least recorded where it is created — see
[Localization](/Doc/Architecture/Localization).

**When the pin does move, the old routine applies in full.** Measured 2026-08-29: eleven PRs red at
once, on diffs that could not reach the RN app — a lockfile override, a Store C# change. The guard
is correct; the gap between the two merges is the problem.

**The routine — do this every time, not as a fix afterwards:**

1. Adding a key to core's catalog? **Open the Plugins mirror PR in the same session**, and land it
   immediately after the core PR merges. Core must go first: the guard compares against core, so a
   mirror that leads *is* the drift it exists to catch.
2. 🚨 **Never patch the mirror on the individual red branches.** That creates competing edits to the
   same two files and a conflict for the real mirroring PR. **One landing clears them all**; the
   others need only a re-run, no code change.
3. Recognise it instantly: **a diff that provably cannot reach the RN app is failing the RN job.**
   Do not debug the PR — compare the catalogs:

```bash
git -C <core>    grep -c '<newKey>' origin/main -- 'src/MeshWeaver.Messaging.Hub/Localization/strings.en.json'
git -C <plugins> grep -c '<newKey>' origin/main -- clients/react/src/i18n/strings.en.json
```
Core `1` / Plugins `0` is this, every time.

🚨 **The guard asserts the mirror is IDENTICAL to the whole server catalog — not that particular
keys exist.** So a mirror PR that copies "the keys that broke it" is still red, one key short, and
looks like the fix failing:

```
FAIL  catalog drift guard > strings.en.json is identical to the server catalog
AssertionError: expected [ 'about.buildCommit', …(1043) ] to deeply equal [ …(1044) ]
```

Mirror by **diffing the key sets**, never by copying the keys you happened to notice — a second,
unrelated key added to core in the meantime is exactly what you will miss. And insert at the
**text level**: re-serialising the JSON rewrites unrelated `\uXXXX` escapes across the whole file
and buries the real change.

Note the RN job is **not** a required context in Plugins, so this reds PRs without blocking them —
which is its own hazard: eleven PRs red on a known-benign check is exactly the noise a *real*
failure hides in.

## 🔗 Reading a red that came from ANOTHER repository

The hardest reds to attribute are the ones a repo did not cause. On 2026-09-01 a single carve-out
wave in core produced **five distinct failures in MeshWeaver.Plugins**, each hiding the next, and
only one of them was an API change:

| # | what broke | what a public-API gate would have said |
|---|---|---|
| 1 | a quoted phrase inside a C# `//` comment parsed as a canonical assembly | green — the API is unchanged |
| 2 | a package's `category` disagreeing with its 11 siblings | green — not an API |
| 3 | moved test suites arrived without their Testcontainers pre-pull | green — not code |
| 4 | pin-vs-source skew: a type moved to a new assembly hours after the pinned image was cut | green — the type exists, just not in *that* image |
| 5 | a type moving assemblies without a forwarder | red — the one shape a surface gate catches |

**So the class is not "core changed its public surface." It is "anything downstream reads out of
core's tree, at a version it does not control."** Prose, package data, CI infrastructure and image
timing all belong to it.

### 🚨 A red with ZERO jobs and ZERO contexts is a `startup_failure` — the workflow file is invalid

Every other red on this page names something: a job to open, a log to read, an annotation on the
commit. **A `startup_failure` names nothing.** GitHub rejected the workflow file before it created a
single job, so the run's job list is empty, it posts no check contexts, and the commit shows an
ordinary red tick with nothing behind it. The run's `conclusion` is `failure` like any other; only
`gh run view <id> --json jobs` distinguishes it, by answering `[]`.

Measured 2026-09-03. MeshWeaver#3225 inserted an input into `node-repo-module-pack.yml` between the
previous input's `required:` line and its `default:` line. The orphaned `default` attached to the
**new** input — a duplicate `default` key — and the input above silently lost its default. GitHub
refuses such a workflow outright. But these are `workflow_call` lanes that five satellite repos pin,
so the break was invisible in core (core does not call them) and surfaced **two repos away**, as
MeshWeaver.Plugins#1268 run `33777768374`: `failure`, zero jobs, zero contexts, on a change that
repo did not make. Attributing it took comparing job counts across two heads in two repos — the
prior head ran 29 jobs, this one ran 0 — and then linting the lane by hand.

**The diagnostic, in order:**

```bash
gh run view <run-id> --repo Systemorph/<repo> --json conclusion,jobs --jq '{conclusion, jobs: (.jobs|length)}'
# {"conclusion":"failure","jobs":0}   ⇒ startup_failure: the WORKFLOW FILE is invalid, not the code
# then, in that repo's tree — actionlint takes FILES, not a directory:
actionlint -shellcheck= -pyflakes= -oneline .github/workflows/*.yml   # names the defect at its line
```

🚨 **And the reason nothing caught it upstream: `yaml.safe_load` accepts a duplicate mapping key and
silently keeps the last one.** #3225 validated the edited lane by parsing it with PyYAML and
asserting the required inputs were unchanged; the parse succeeded, the spec read back correctly, and
the assertion passed. **Any workflow validation built on a permissive YAML load is blind to this
class by construction** — the same shape as every other gate on this page that passes on evidence it
could not produce. Core's `CI's own shell` job now runs `actionlint` over every workflow for exactly
this reason (MeshWeaver#3228), with the embedded shellcheck/pyflakes integrations disabled so the
gate is about structure only; `check-workflow-timeouts.py` and `check-workflow-shell.py` both stay
green on the defect, which is the measurement that says the new check is not redundant.

### 🚨 A red with ZERO STEPS in ~2 s is an org BUDGET refusal wearing a workflow-defect costume

The shape above has a twin that is not a defect at all, and the two are easy to confuse because both
produce a red that names nothing. **When the organisation's GitHub Actions spending limit is
reached, GitHub refuses to START jobs**, and the refusal is dressed as a failure:

| Tell | What you see |
|---|---|
| Duration | jobs fail in **~2 seconds** |
| Steps | the job has **zero steps** — not a failed step, none at all |
| Matrix | matrix job names appear **unexpanded** (the literal `${{ matrix.… }}`, because nothing evaluated them) |
| Logs | `GET /actions/jobs/<id>/logs` answers **`BlobNotFound`** — there is no log, because nothing ran |
| Scope | **several repos at once, in one window**, while a repo whose runs started before the window keeps going green |

**The only place the real reason appears is the job's ANNOTATION**, and nothing in the run's own JSON
says it:

```bash
gh api "repos/Systemorph/<repo>/check-runs/<check-run-id>/annotations" \
  --jq '.[] | "\(.annotation_level): \(.message)"'
# failure: The job was not started because an Actions budget is preventing further use.
```

Measured 2026-09-11 across three repos inside one 18-minute window, with core unaffected.

🚨 **Re-running IS the correct action here, once the window has passed** — and that is the one place
on this page where that is true. Everywhere else a re-run without a code change hides a race
(AGENTS.md → "NEVER re-run a test unless code under test has changed"). This is not a flake and not a
race: it is a **refused gate**, a job that never executed, so there is no observation to preserve and
nothing was measured that a re-run could paper over. The distinction is the same one this whole page
turns on — *"it did not run"* and *"it ran and failed"* are different claims — so **read the
annotation before you re-run**, and if it names no budget, treat the red as real.

🚨 **No job inside the refused repository can report the refusal while it is in force — the
reporter is a job too.** Measured on the same window (MeshWeaver.Plugins run 34570627805, attempt
1): 16 jobs concluded `failure`, 7 `skipped`, **0 ran** — so a `ci-failure` job wired at the end of
that run, a `workflow_run`-triggered retrier, anything that needs a runner, is refused with the
gates. A refusal reaches an in-repo reporter only when the window closes between the gates' refusal
and the reporter's start. Two consequences, both built in now:

- The `ci-main-red` ledger (`node-repo-ci-failure.yml`) marks every failed job that ran **no step**
  as *never started* — `neverStarted` on the job and a run-level count in the signed event — and
  its entry says *"N of M failed job(s) ran no step"*, or that **nothing** in the run executed. It
  does not read the annotation: that needs `checks: read`, which the lane does not demand and every
  fleet caller would have to grant (the roster in `.github/lane-caller-grants.yml` pairs them). The
  entry names the budget sentence to look for instead.
- The instrument that sees a refusal **as it happens** runs outside the refused repository's
  Actions: the control instance's **Repo Health** scan (`Hosting/RepoHealth` in MeshWeaver.Plugins)
  reads every fleet repository's `main` runs through the `systemorph-com` App, which holds
  `actions: write` and `checks: write` on all repositories (measured 2026-09-14,
  `GET /orgs/Systemorph/installations`) — so it can read the annotation the ledger cannot. Core's
  own runs are never refused (public repository, free minutes), which is also why the merge-queue
  steward never meets this shape.

### A verdict about an unpinned checkout is a function of wall-clock time

The cross-repo gates check core out with **no `ref:`**. Two people therefore measured the same
downstream branch on the same day and got **opposite answers**, both correct — core's tip had moved
between them. Failure #1 above landed in core at 08:01:19Z; every downstream run before that passed
and every run after it failed, with no downstream commit in between.

**The rule:** record the **core tip** alongside any cross-repo verdict, and resolve it once per run
rather than per checkout step. A verdict without that stamp cannot be reproduced or disputed.

### 🚨 A sweep goes blind when its SUBJECT moves repos — not when its detector breaks

The failure this page keeps naming is *"the sink appeared later than the window"*. There is a second
shape, and it is harder to see because nothing about the detector changes: **the suite that carries
the defect leaves the repository, and the sweep keeps returning a confident, calibrated zero.**

Measured on **#890** (a Roslyn `Emit` that poisons a test host process, so every later NodeType
compile NREs). Swept core CI on 2026-09-02 across 958 `dotnet-test.yml` runs:

| window | occurrences |
|---|---|
| 2026-08-30T00:05Z → 2026-09-01T11:34Z | **11** — 1.76 % of non-cancelled runs |
| 2026-09-01T11:34Z → 2026-09-02T10:27Z (201 runs) | **0** |

The clean tail reads like a fix, and at that rate a 201-run gap is a ~4 % coincidence. It is neither.
**PR #2847 merged at 2026-09-01T14:06:31Z and deleted `MeshWeaver.Hosting.Monolith.Test` and
`MeshWeaver.PluginCatalog.Test` from core**; they now live in `MeshWeaver.Plugins/src/`. Every one of
the 11 occurrences fired inside one of those two suites. The last one is 2 h 35 m before the removal.

Be precise about what left, because "the suite moved" and "the repo is now immune" are different
claims. Core still emits NodeType assemblies in `Compiler.Pipeline.Test` and `Graph.Test` — but those
run in **9 s** and **100 s**, against the **13 m 38 s** of compile-heavy integration work that walked
out. What the removal took was not the possibility, it was **the exposure that made the rate
measurable** — which is enough to make a post-removal null uninformative either way. (An earlier
version of this paragraph justified that with *"the onset in every measured occurrence was ~2 minutes
into such an assembly"*. **That is now falsified** — the 2026-09-03 Plugins occurrence fired **33 s**
in — so the argument rests on suite RUNTIME, which is what the 9 s / 100 s vs 13 m 38 s comparison
actually measures, not on a warm-up threshold.)

**The rule:** before believing a null, ask *"does the code that produces this signal still run in
this repository, in this window?"* Confirm it positively — name a run and the suite verdict line it
printed (`Passed! - … - <Suite>.dll`) — rather than inferring it from the sweep's own silence. A
subject that moved and a defect that stopped are indistinguishable from inside one repo, and
[deleted and relocated look identical too](/Doc/Architecture/CrossRepoPairGate).

### The same fault surfaces through a DIFFERENT sink in each repo

Having found where the suites went, the follow-up sweep in the other repo is not the same command.
Core streams a test's log output into the job log as live `[OUTPUT]` lines — **5 852** of them in the
calibrated #890 occurrence. The equivalent MeshWeaver.Plugins job, running the very same suite,
carries **0**: it dumps `Standard Output Messages:` post-hoc, per *failing* test, and keeps the
`_meshweaver-test-trace.log` phase trace only inside a `teardown-stragglers-*` artifact.

A detector keyed on the framing (`[OUTPUT]`, a project-name-plus-`exit=124` string, a `(part N/M)`
suffix) therefore returns a clean, fast, entirely vacuous zero. **Grep the signature the code emits**
— here `canary=`, `PROCESS CANNOT EMIT`, `GetConsolidatedTypeParameters` — **never the harness's
packaging around it**, and prove the sink is alive in the target repo with a positive control before
reporting the null (17 of 43 Plugins trace artifacts carried real `Compile failure for …` records,
which is what made that null worth stating).

#### Two sinks per repo, and only one of them is complete

**Read both, and know which is authoritative.** A post-hoc `dotnet test` job log carries only the
output of tests that **FAILED** — and the fault you are hunting is often captured by a test that
*passed*. In the calibrated core occurrence the very first canary record is printed under
`CreateLayoutAreaIntegrationTest.CreateArea_WithoutTypeParam_ShowsTypeSelection`, which is **not** one
of that shard's five failures: on a `dotnet test`-driven harness that record would never have reached
the job log at all. The complete sink is the phase trace — `_meshweaver-test-trace.log` inside
`teardown-stragglers-<run>-<attempt>-shard<N>` — which takes an unconditional `[FAULT]` record for
every `ILogger` call carrying an exception at Warning or worse, whatever test was running and whether
or not it failed. (It is collected only when a suite failed, which is why it covers this defect: the
poisoned process always reds its suite.)

**Measured 2026-09-04 over 2026-09-03T06:04Z → 2026-09-04T06:34Z**: 122 non-cancelled
`Plugin Catalog CI` runs, all 58 non-success `Portal hosts (shard N)` job logs fetched (0
unfetchable, 50 of them running the exposure suite) plus 57 trace-log artifacts carrying 13,460
`[FAULT]` records — **one occurrence**, run `33760859754` shard 3, on `main`, and it surfaced in
**both** sinks (56 `canary=BELOW-ROSLYN` and 20 `PROCESS CANNOT EMIT` in the job log; 119 signature
lines and 39 `Compile failure for …` records in the trace). That closes the blind spot the previous
sweep recorded as open — *"I have not proved the canary's log line reaches job-log stdout in a
Plugins shard"* — with a real occurrence rather than an argument.

**Calibrate before believing a null.** The four patterns above, run over the known core occurrence
(job `99288463497`), return **77** signature lines and **38** `canary=BELOW-ROSLYN` — the counts that
occurrence's own report published. A detector that cannot reproduce those numbers is not entitled to
report a zero.

🚨 **One of those four patterns was a guaranteed ZERO on the authoritative sink, and the loudest line
was the least durable one.** The trace log takes a record if and only if
`exception is not null && logLevel >= Warning` (`XUnitFileLogger.Log` → `TestTraceLog.AppendFault`).
`PROCESS CANNOT EMIT (#890)` — the `LogError` whose entire job is *"attribute the failures that
follow to this line, not to the change under test"* — was logged with **no exception object**, so it
could never reach the trace **by construction**, while its quieter `LogWarning` sibling
(`Compile failure for {HubPath}`, which passes the error and carries the whole `canary=`/`dissect=`
verdict in brackets) always did. Measured on both 2026-09-05/06 occurrences:

| pattern | job log | `_meshweaver-test-trace.log` |
|---|---|---|
| `PROCESS CANNOT EMIT` | 25 · 25 | **0 · 0** |
| `canary=` | 78 · 62 | 55 · 28 |
| `Compile failure for` | — | 54 · 31 |

Since the job log carries only FAILED tests' output and this defect's first canary record is
routinely logged under a test that PASSED, an occurrence could reach **neither** sink with its
attribution intact. Fixed by passing the exception (the same thing #612 already did to the sibling
call three lines away), so the record now satisfies the trace sink's gate — the general lesson being
that **for a fault the trace log is the sink, and reaching it is a property of the CALL, not of the
level or the wording**. An `ILogger.LogError` about an exception that does not pass the exception is
invisible there.

🚨 **The same blind spot has a SECOND live instance, and it is the reason "the observable emitted
nothing at all" is undiagnosable.** `LayoutAreaHost` carries two diagnostics written for exactly the
wedge where a layout area never delivers a frame — `COMPLETED WITHOUT RENDERING` (`LogError`) and
`was torn down having never rendered` (`LogWarning`). Both pass **no exception**, so both are
refused by the trace sink's `exception is not null && logLevel >= Warning` gate, and both fire on a
pool thread or during teardown, where `outputHelper` is null / `IsInTestMethod()` is false and the
trx sink is closed too. Measured on the #3413 occurrence (run `34034828870`, shard 5): across all
**450** trx results and the whole 4,050-line trace, `never rendered` → **0** and
`COMPLETED WITHOUT` → **0**. In a `HubTestBase` test the disposal one is `LogDebug` regardless — the
`Hub.IsDisposing` branch (#2679) — and mesh teardown is exactly how such a test disposes its layout
host. So a layout area that renders nothing is silent in **every** sink, which is what made #1081
cost four sessions and what leaves #3413 open.

🚨 **The blind spot's THIRD instance disarmed a GATE, which is a strictly worse outcome than losing
one explanation.** `MeshNodeStreamCache` logs `Content for {Path} stayed an untyped JsonElement`
from both read seams, and `check-untyped-content.sh` reds a shard that finds that phrase in
`collected-logs/`. Both warnings passed **no exception**, so neither could reach
`_meshweaver-test-trace.log`; the only other route into that directory (`*/bin/*/test-logs/*.log`)
is opt-in via `MESHWEAVER_TEST_FILE_LOGS`, which `node-repo-module-pack.yml` sets and
`dotnet-test.yml` does not. Measured on a run that really did degrade content: **817** trace records
naming the test class, **0** occurrences of the phrase it emitted. The gate was therefore
permanently green having matched nothing — indistinguishable from a gate that passed, which is the
one reading AGENTS.md singles out as forbidden.

Its control test did not catch it because it asked the wrong question: it pinned that the source
still *emits* the phrase and that the script still *greps* it — both true throughout — and never
whether the record could **reach the directory the script scans**. *A guard whose subject moved and
whose roots did not passes having checked nothing.*

Two things changed. The warnings now carry `MeshNodeContentDegradedException`, an exception object
constructed and never thrown whose whole job is to satisfy the sink's predicate; and the gate keys
on that **type name** first, with the prose phrase kept only as a second net. A message is a
*description* of an event, the type is the event's *identity*, bound by the compiler at every
construction site — so the coupling now has two independent bindings (a rename is a repo-wide
compile change, **and** `UntypedContentDegradationGate` pins the script's key to `nameof(...)`)
where the phrase had only the one. `UntypedContentDegradationReachesTheTraceSinkTest` supplies the
half that was missing: it drives the production converter and evaluates the sink's own condition
against the captured record.

🚨 **The gate has no allow-list, and a test fixture is where the pressure for one comes from.** A
test that proves a write REFUSES unreadable content has to seed unreadable content, and once these
records reach the sink such a fixture reds its own shard. The answer is the fixture, not an
exemption — an exemption here would be a permanently green check wearing a reason, which is the
state this whole section is about. Model *"present but unreadable as `T`"* the way a running mesh
actually produces it: **seed a value of a DIFFERENT, REGISTERED type.** The stream cache types it
happily, so nothing degrades and nothing is recorded, while `ContentAs<T>` / `As<T>` still answers
`null` — it recovers a foreign runtime type ONLY when the short name matches. That is closer to the
production case (a same-named record from another collectible assembly, a foreign type) than
malformed JSON is, so the fixture gets stronger. Malformed JSON with no resolvable `$type` models a
*different* defect — content nothing can read — which is exactly what the gate exists to report.

**Falsified end to end, exit codes read directly.** Same degradation, same real sink
(`XUnitFileLogger` → `TestTraceLog.AppendFault`), three runs: with the exception argument reverted
the trace file was **never created**, and the gate answered `No content-type degradation` with
**exit 0** — the defect, reproduced; with the fix the record landed in the trace and the gate exited
**1** naming `Space/ARenderedEmptyPage` twice; and against a trace carrying a real, unrelated
`[FAULT]` record the gate exited **0**, so the pass is a verdict rather than an empty scan.

🚨 **What the repaired gate found on its FIRST working run — and the classification that matters.**
A gate that fires on the wrong thing is no better than one that cannot fire, so every occurrence was
classified before anything was changed. Two node paths, two opposite verdicts:

| occurrence | verdict |
|---|---|
| `Ops/Modules/{deployment}` (`Hosting/ModuleInventory`) | **TRUE POSITIVE — a live defect in `src/`.** `DeploymentReportService` stamped `$type = "ModuleInventoryContent"`, a literal naming **no CLR type in the fleet**, while the real record `DeploymentReport` was registered nowhere. Its own comment said the stamp existed because *"content without the discriminator … materialises as NOTHING"* — and it materialised as nothing anyway. Every instance's self-reported module inventory read back untyped. Fixed: the record is registered and the constant is `nameof(DeploymentReport)`. |
| `{partition}/Live` (`LateContentTypeRegistrationTest`) | **A true degradation, but NOT the gate's subject.** That test asserts content *stays* untyped when an unrelated type registers; its `$type` is literally `AContentTypeTheMeshNeverCompiled`. The keying was not at fault — the event really happened — but the gate could not distinguish a degradation that is a test's SUBJECT from one nobody intended, nor a transient one from a final one. The second half is answered by #3645 (see *TRANSIENT from FINAL* below); the first is still answered by capture-and-assert. |

🚨 **The guard on the true positive was asserting the defect.** `AnInstanceReportsWhatItRunsTest`
checked `content.GetProperty("$type") == InventoryContentType` — and a `$type` **property** is only
there to read when the content is raw JSON, i.e. when it has *not* materialised. The assertion
passed *because of* the bug. It now asserts the materialised runtime type.

🚨 **And the obvious replacement assertion also could not fail.** `ContentAs<T>` is the bad-data
TOLERANT accessor: handed a raw `JsonElement` it deserialises anyway, so it answers a
`DeploymentReport` whether or not the discriminator resolved. Measured, not assumed — the
`ContentAs` version passed against the reverted fix. The property that actually breaks is
MATERIALISATION at the read seam (`node.Content is DeploymentReport`), which is what every ordinary
reader does and what the degradation warning is about. **When a diagnostic says a value "reads as
absent", assert the runtime TYPE, never a tolerant accessor.**

🚨 **How a test declares that a degradation is its own SUBJECT — answered, and the answer is not an
allow-list.** The fixture advice above (seed a registered foreign type) covers the case where a test
only needs *"unreadable as `T`"*; it fixed `UnreadablePolicyRecordIsNotClobberedTest`, 8 records → 0.
It cannot cover a test whose assertion IS that **nothing can resolve the content**.
`LateContentTypeRegistrationTest` is that case three times over: #2952 is about a NodeType compiled
at RUNTIME into a collectible assembly, so *"registered nowhere yet"* is the premise, and seeding a
resolvable type deletes the subject. Measured on the repaired gate's second working run: that one
class put **3** records into the shared trace and red shard 4; nothing else in core did.

| candidate | verdict |
|---|---|
| An allow-list in the gate | **Refused.** An exemption in the scanner is a permanently green check wearing a reason — and these node paths carry a per-run GUID, so the entry would have to be a *pattern*, i.e. an exemption that widens by itself. |
| Silencing the emitter's category for that test (a level, an `appsettings`) | **Refused twice over.** AGENTS.md forbids dialling a log level for a CI reason, and a suppression matches *nothing* exactly as happily as it matches its subject. |
| **Capture the record in the test and ASSERT it** | **Held.** |

**What it is.** The emitter takes its logger from DI, so a test substitutes the CLOSED
`ILogger<MeshNodeStreamCache>` for its own mesh: a decorator that takes records carrying
`MeshNodeContentDegradedException` and forwards everything else untouched. That is the same move
`UntypedContentDegradationReachesTheTraceSinkTest` already makes one test over — the only new thing
is doing it for a whole mesh instead of a direct call.

**Why it is not a skip-trapdoor**, which is the only question that matters:

1. **Nothing is silenced.** No level is dialled and no category is muted; every other record the
   cache logs reaches the real logger at its own level.
2. 🚨 **The diversion IS an assertion.** Each test asserts that the record it declared as its
   subject *was produced*, that it names the expected node, and that it **satisfies the trace
   sink's own predicate** (`exception is not null && level >= Warning`). A declaration that matches
   nothing is therefore a RED — the one property an allow-list can never have. It is also strictly
   more than these tests asserted before, when the record went to a file nobody read.
3. **The exemption is exactly one node wide.** The same assertion pins that *everything* captured
   names the declared path, so an unintended degradation of any other node in that mesh fails the
   test rather than being swallowed.
4. `UntypedContentDegradationGate.ADivertedDegradationIsAssertedWhereItIsDiverted` is the control
   arm: a test that substitutes that logger without asserting what it caught fails the build, and
   the guard reds if it ever examines **zero** files.

**Falsified, not assumed.** With the `MeshNodeContentDegradedException` argument reverted at the
`GetStream` seam — the #3625 defect itself — all three tests FAIL on *"the platform must REPORT the
degradation this test reproduces"*. So the regression is now caught by a **test**, not only by a
gate that must first see a whole shard's logs. With the fix in place: 3/3 pass, marker count in the
shared trace **3 → 0**, `check-untyped-content.sh` **exit 1 → exit 0**.

🚨 **TRANSIENT from FINAL — the gate's denominator, answered by #3645.** `MeshNodeStreamCache` warns
at the instant of a read, and at that instant it cannot know the type will register moments later —
which is the normal state during portal boot, before the NodeType compiles land, and is exactly the
race #2952 fixed by re-typing every live reader when the registration arrives. The sibling seam one
layer down already said so in its own message: `MeshNodeTypeSource` prints **both** causes — *"(a)
the NodeType's runtime compile has not registered it YET, which is TRANSIENT … (b) no declaration
will ever claim this discriminator"* — and it passes **no exception**, so it never reaches the trace
file and reds nothing. One event, two descriptions, opposite CI consequences: the cache's was a
shard failure, the type source's invisible. Two of `LateContentTypeRegistrationTest`'s three cases
are the transient one, and they degrade *and recover* inside a single test — so a gate hit meant
*"content was unreadable at a read"*, never *"content is unreadable"*.

**What closed it was not a window; it was asking again.** The platform already kept the state
(`ContentDegradationRegistry`, one entry per node type, added for `/health`), and the content-type
registry already answers *"is this resolvable"* as a pure map lookup by either of the two routes
`TryRecoverForNodeType` takes. So the verdict is computed by **re-asking at report time**:
`ContentDegradationRegistry.Unresolved(registry)` drops every entry whose type has since
registered, and `MeshNodeStreamCache.Dispose` logs one `MeshNodeContentUnresolvedException` per
entry that survives. `check-untyped-content.sh` keys on **that** type now, not on the per-read
`MeshNodeContentDegradedException`.

| record | when | meaning | reds a shard? |
|---|---|---|---|
| `MeshNodeContentDegradedException` | at each degraded read | *this read was untyped* — an EVENT, possibly the boot race | **no** (since #3645) |
| `MeshNodeContentUnresolvedException` | once per node type, at cache teardown | *the registry was re-asked and still says no* — a VERDICT | **yes** |

So a boot that reads a runtime-compiled NodeType before its compile lands now produces **no** gate
hit, while a discriminator nothing will ever claim produces exactly one, naming the node type and
the discriminator. The per-read records stay in the logs and stay useful: once the verdict names a
type, they are how you find which reads degraded on it.

🚨 `/health` was wrong the same way and is fixed in the same change: `ContentTypeHealthCheck` fed
`Snapshot()`, so a replica stayed `Degraded` after a boot race until some later read of that type
happened to clear the entry — a probe answering about the past. It reads `Unresolved(...)` now.

**Falsified in both directions, as the issue asked.** With `Unresolved` reduced to `Snapshot()`, the
recovered case is reported and `ARecoveredDegradation_IsNotTheVerdict` fails; with the teardown
report removed, `AnUnrecoverableDegradation_IsTheVerdict_AndReachesTheSink` fails on *"the teardown
must REPORT what never resolved"*. `UntypedContentDegradationGate` pins the script's key to
`nameof(MeshNodeContentUnresolvedException)`, that the emitter constructs it, that the report is
computed from `Unresolved(`, and that both read seams still emit the per-read record the verdict is
derived from.

**The instrument that does answer it** is `MessageTrace`
(`MeshWeaver.Messaging.Hub/MessageService.cs`): `MESHWEAVER_MSG_TRACE=1` makes every delivery write
a `ROUTED` / `DEFERRED gates=[…]` / `GATE_FAILED` / `DROPPED_GATE_STUCK` line to
`Path.Combine(Path.GetTempPath(), "meshweaver-msg-trace.log")` — enough to say whether the sync
sub-hub processed its
`InitializeHubRequest`, whether `PushRenderResult` ever posted an `UpdateStreamRequest`, and whether
anything was deferred. It is off by default (a lock + file append per message). Both
`node-repo-module-pack.yml` **and** `dotnet-test.yml` now copy the file into the shard artifact when
it exists, so turning it on for a run is a one-variable change and needs no workflow edit.

🚨 **Write `Path.GetTempPath()`, never `$TMPDIR`, when you say where it lands.** They coincide on the
runners today and are not the same thing: on Unix `GetTempPath()` returns `$TMPDIR` *when that is
set* and `/tmp` when it is not, and on Windows it is `%TEMP%`. The collector derives the directory
the same way (`"${TMPDIR:-/tmp}"`) rather than hard-coding `/tmp`, because a literal would work by
the coincidence that GitHub's ubuntu images leave `TMPDIR` unset — and on the day something sets it,
the `-f` test answers false, the copy is skipped, and the step stays **green having collected
nothing**. That is the failure class this whole instrument exists to expose, so it must not be the
shape of the collector.

🚨 **And know which questions the sinks cannot answer.** *Neither* Plugins sink carries a compile
**success**: `Compile success for …` is `LogInformation`, so the trace log (faults only) never sees
it and the post-hoc job log never sees it either unless the test that logged it failed. Core's live
`[OUTPUT]` stream does carry it — which is why the "**none** succeeded" half of the
[total-and-permanent measurement](/Doc/Architecture/NodeTypeCompilation) is stated for the core
occurrence and **not** for the Plugins one. Asserting it there would be inferring an absence from a
sink that never records the presence. Before writing "and X never happened", check that the sink you
read would have shown X happening.

### A grep hit is not a binder

Twice in one day, prose was mistaken for the thing it described. A quoted phrase in a `//` comment
became a demanded assembly; and two types looked *bound* by exactly one caller each — one a doc
comment naming an example subscriber, the other a markdown page that `MeshWeaver.Documentation.dll`
embeds as a **resource**, so it appears in a *binary* grep.

**Before concluding a symbol is used, declared or bound, classify each hit.** Strip comments in the
language actually being parsed, not another language's — a `<!--.*?-->` stripper is a complete no-op
on C#, and that is precisely how a comment became a canonical assembly.

### 🚨 Mutually blocking PRs — when "one concern per PR" inverts

Two open PRs each failed a **required** context on exactly what the other fixed:

| PR | required check A | required check B |
|---|---|---|
| the pin bump | ❌ the phantom assembly | ✅ fixes the build |
| the parser fix | ✅ | ❌ the build the pin fixes |

Neither could go green alone, so **a correct PR could not land however correct it was.** The house
rule that a pin bump is *"deliberate, in its own PR"* is right for a healthy trunk and inverts the
moment the pin is itself part of the breakage.

**The rule:** when two required contexts each fail on the other's fix, separation is the thing
preventing the repair — merge one branch into the other and say why in the body. Check for this
explicitly before concluding a PR is "just flaky"; the signature is *two* red PRs whose failures are
each other's subject.

### The container lane sees a WIDER namespace surface than any local build

A build that composes its references from the platform image's `/app` gives source a **strictly
wider** namespace surface than a project-referenced build. Core compiles a project against its
`ProjectReference` graph, so a transitive package's extension methods are visible only where
something references it; the container build composes *everything in `/app`*, so every module sees
them whether it references them or not.

The visible consequence is an ambiguity that **cannot be reproduced locally**:

```
CS0121  The call is ambiguous between
        'System.Linq.Enumerable.TakeLast<T>(IEnumerable<T>, int)' and
        'System.Linq.EnumerableEx.TakeLast<T>(IEnumerable<T>, int)'
```

Two extension methods, same signature, same namespace — one from the BCL, one from Ix.NET, which is
a legitimate pinned platform dependency that in-mesh source uses deliberately. Core never sees it;
the container lane always does.

**The rule:** a green local build is not evidence about the container lane, and "it compiles in core"
is not an argument that a module will compile. When a compile error appears only in that lane,
suspect the reference set's *width* before suspecting the source. And do not "fix" it by pruning the
platform assembly — qualify the call, and only where the receiver's type actually makes it ambiguous
(an `IObservable` receiver resolving through `System.Reactive.Linq` is not).

### Fixing the first red can reveal a second that never ran

A job stops at its first failing step, so every later step reports **SKIPPED** — which reads as
"fine". Repairing failure #1 above let the job reach a step that had never executed on any branch,
and it failed immediately on a real defect. **"Red for a known reason" is the cheapest state in
which to miss a second regression**, and it is an argument for fixing the first red rather than
routing around it. Expect the count of problems to go *up* when you fix one.

### 🔗 A platform break shows up in the DEPENDENT's release-follow run, not on the core pull request

Core emits a `repository_dispatch` to every node repository when it promotes a platform image set;
each dependent builds and tests against that release in ITS repository. A removed member, an
ambiguous overload or a changed envelope therefore reads as a red `repository_dispatch` run in
MeshWeaver.Plugins (or another node repo) shortly after core published — with that repository's
test names — and is fixed by a pull request there. Core carries no context for it by design
(maintainer, 2026-09-03: the integration is event-based; no top-level repository depends on another).

### 🚨 Several satellites red in the same second on a missing `plugins` publication: find out WHICH seal is missing before blaming anyone

When several satellites' `main` runs fail within a second of each other, they share a trigger, so
compare their failed steps before reading any one of them. The shape measured on 2026-09-16:
Reinsurance, Crm, SocialMedia and Manufacturing all red at 20:14:20Z, each on `compile-check`'s
*Add external modules to the reference set* and the gate shard's *Fetch the upstream publications*:

```
upstream 'plugins' answers 404 for the module set of identity s5ec352bb… :
{"error":"no sealed publication for source 'plugins' under framework identity 's5ec352bb…'"}
```

A `plugins` publication has **two producers**: core CD's `Plugins: bake + seal the publication for
this identity` job, and MeshWeaver.Plugins' own `publish-bake` lane. The same 404 appears whichever
one failed. A red in Plugins' own lane is a Plugins defect, and
[CI Content Bake](/Doc/Architecture/CiContentBake) treats it as a wait for the upstream. So read the
producers before deciding where the fault is:

1. **The satellite's `Platform pins name one build` job.** The resolver prints which set it took
   and what it found for Plugins. On 2026-09-16 it said `main-cd #8765 (core 836d4472b): its own
   Plugins seal is skipped — the platform is taken anyway`, then `plugins publication: main-cd #8760 …
   — an older set than the platform chosen`. That means the gate is about to ask for a publication
   that does not exist. `resolve-platform.py` does this on purpose: a terminal Plugins seal does not
   hold the platform back. [Framework Identity Churn](/Doc/Architecture/FrameworkIdentityChurn)
   explains why walking back to an older set is refused.
2. **Core CD's Plugins legs for that set.** In #8765, `Warm the shared build environment (once)`
   died on `dial tcp 51.12.25.82:443: connect: connection refused` (ACR). Promote had already sealed
   the platform, so `Plugins: bake + seal …` was skipped. Here the missing seal was core's, and the
   cause was infrastructure, not Plugins.
3. **MeshWeaver.Plugins' own `publish-bake` for the same identity.** Its main run had resolved the
   older set 8760 at 19:28Z, before 8765 was sealed. It published for identity `sd94ee1d…` and
   registered at 20:14:18Z, and that registration was the wake. So neither producer had published
   for `s5ec352bb…`.

**The wake names an image, but only one lane uses it.** Per CI Content Bake, the `publish-bake` lane
reads `client_payload` and bakes against the image the wake names. The gate and compile path does
not read the payload: `Platform pins name one build`, `compile-check` and the gate shards resolve
the newest sealed set again. So a wake for an older set can start a run whose gates ask about a
newer identity. That is how a Plugins publication for 8760 produced four reds about 8765.

**What healed it:** a `plugins` publication for that identity, followed by another wake. On
2026-09-16 both publications arrived by coincidence. MeshWeaver.Plugins merged, so core's hourly
reconcile saw a missing pair tag and rebuilt the set (#8767, Plugins seal at 21:30:24Z). Plugins'
next main run also resolved 8765 and published for `s5ec352bb…`. The runs they woke went green at
21:52–21:58Z. **The reconciler did not re-attempt a failed core Plugins seal on its own.** Its
`bake_only` path re-ran only the platform bake, so without a Plugins merge the red would have lasted
until core merged again. Do not re-run the satellites' failed jobs: the resolver reads the same set
again and the registry still answers 404.

**Since MeshWeaver#4539 it does.** On every `bake_only` tick `gate` asks
`check-release-availability.sh <version> plugins` — naming the set's framework identity, resolved
from the `_releases/<version>` marker — and re-runs the three `plugins-*` legs when, and only when,
that answer is a definite absence. It is bounded at three attempts per (core sha, plugins sha) pair
and it refuses rather than acts when the store cannot be read. The hold on the satellite side is
unchanged and still correct; what changed is that something now clears it. See
[CD Reconciles the Plugins Seal](../CdReconcilesThePluginsSeal).

**A second red can follow a satellite that has two upstreams.** Reinsurance declares `plugins crm`.
Its 21:19Z run passed every gate, then its `publish-bake` went red: `crm — no sealed publication
under prebuilt-bundles/s5ec352bb…/crm`. That is the documented wait. Crm sealed its publication
for the identity at 21:52Z. Reinsurance's 21:30Z run reached the same gate at 21:52:26Z, printed
`all 2 source(s) are published`, and finished green at 22:02Z.

**The retry steward is a separate outage, not the cause.** The 404 is not one of the steward's
named transient signatures, so it would correctly decline this red even with a readable log. But
right now its "no retry" line tells you nothing about ANY red. In core and MeshWeaver.Plugins, every
`Retry known transients` run sampled from 2026-09-10 to 2026-09-16 printed `log unreadable (the
response contains terminal escape sequences; pass --allow-escape-sequences …) — cannot prove a
transient`. The hosted runner's `gh` refuses any response body with escape sequences, and every
Actions log has them.

**Crm, Reinsurance and SocialMedia are no longer unsampled — measured 2026-09-17, and it is worse
there.** Every one of their newest `Retry known transients` runs declined on its **first** failed job
with that same sentence and then `exit 0`, so the shell steward never looked at the rest; and
MeshWeaver.Plugins' steward declined **22 jobs in one run** (`35245934949`, 16:21Z) the same way.

### What `/actions/jobs/<id>/logs` actually serves

Read straight from REST — 30 failed jobs across MeshWeaver.Plugins, .Crm, .Reinsurance, .SocialMedia
and MeshWeaver, deliberately not one payload:

| outcome | n | shape |
|---|--:|---|
| served a log | **23** | `200 text/plain`; **all 23** with a UTF-8 BOM; **all 23** carrying ANSI escapes (30–1,932 each); all strict UTF-8; all with the timestamped line; **none** carrying any other control character |
| `404` *"The specified blob does not exist"* | 1 | the job uploaded no log at all |
| `410 Gone` | 6 | the log outlived its retention |

So there was never anything exotic to parse. **Three** rules follow — and read the next paragraph
before assuming any given steward keeps them:

- 🚨 **Three outcomes, not one.** `404` and `410` are **facts about the job** — nothing was uploaded
  (what a runner that died before writing one looks like), or the log expired — and they *decline*,
  naming which. **Everything else** (5xx, a permission refusal, a transport failure, a non-UTF-8
  body, a body with no timestamped line) is the steward **blind to its own input** and is a **RED**.
  Collapsing those into one "unreadable, no retry" is what made a week-long outage look like a
  judgement.
- 🚨 **Strip the BOM and the escapes before matching.** The raw bytes are not what anyone reading the
  run sees: every echoed `run:` line arrives wrapped in `\e[36;1m…\e[0m`, so a pattern anchored near
  the start or end of such a line cannot match the raw form.
- 🚨 **A decline that happens 22 times in one run may not live only in a green job's log.** That is
  the same defect shape as a scheduled lane whose honest red goes into an empty room. The decision
  belongs in the job **summary**, and a job judged without a log should raise a `::warning::` — which
  is visible on the run without pretending the steward itself failed.

🚨 **WHICH STEWARD KEEPS WHICH — do not read the three rules as a description of the fleet.** They are
what the four stewards fixed on 2026-09-17 do (MeshWeaver.Plugins and the three satellites). **Core's
`#4554` fix implements the READ and none of the three**, verified against
`.github/scripts/retry-known-transients.py` on `main`:

| | core (`#4554`) | Plugins + the three satellites |
|---|---|---|
| reads the log at all | ✅ REST, no `gh` | ✅ REST / `curl`, no `gh` |
| `404`/`410` distinguished from a blind read | ❌ `read_job_log` maps **every** `HTTPError` to one `LogUnreadable` → RED | ✅ they decline, naming which |
| BOM and escapes stripped before matching | ❌ the raw decoded body is matched directly | ✅ |
| the decision reaches the summary / a `::warning::` | ❌ neither appears in the script | ✅ |

Core's choice is defensible on its own terms — its contract is *"I cannot see my input ⇒ RED"* and it
has no annotation path to fall back on — but it means a **runner death in core reds the steward job**,
because a job that uploaded no log is indistinguishable there from an API failure. That is named
follow-up, not a claim about today.

> **Fixed in core, 2026-09-17 (#4534).** Core's steward is now
> `.github/scripts/retry-known-transients.py`: it reads the logs endpoint over plain REST — no `gh`,
> so no dependency on a CLI *output* policy — and an unreadable log is a LOUD exit 1, never a
> decline. Measured while fixing it: a plain REST GET of the identical URL answers HTTP 200 with
> 7,251 bytes over 46 escape-bearing lines, so the refusal was never the API's. `--allow-escape-sequences`
> was rejected as the fix because the flag does not exist on older `gh` (the local 2.95.0 has
> neither the refusal nor the flag), which would have traded one silent breakage for another.
>
> 🚨 **The other four copies are STILL BLIND, on purpose.** Crm, Reinsurance and SocialMedia carry
> the inline shell; MeshWeaver.Plugins has its own `scripts/retry-known-transients.py`. Nothing
> propagates a core change to them and nothing reds if they drift — `check-resolver-copy.py` covers
> exactly `scripts/resolve-platform.py` (one constant, and it parses Python, so it cannot see a
> workflow), no `uses:` couples them to core, and no copy is fetched at a pin. The proof is
> historical: core's `block_signatures` clause, added 2026-09-13, reached no satellite and reddened
> nobody. Reviving them is **not** a mechanical re-copy, because `POST …/rerun-failed-jobs` on a
> SATELLITE's `main` run erases the `Platform for this run` annotation its PR ceiling reads
> (#4491, open) — so a satellite steward that works again would silently freeze that repo's
> platform ceiling until the next merge. Core has no such ceiling (it only ever runs the resolver's
> `--self-test`), which is why core could be fixed first and alone. Fix #4491, then re-enable the
> satellites.
>
> **2026-09-17, the other four: the reader is fixed in all of them, and the #4491 hold turns out to
> be narrower than "all four".**
> * **MeshWeaver.Plugins** (Plugins#2042) is *not* held by #4491, because **its retry was never
>   disabled**: its annotation path — runner death, budget refusal — reads no log at all, so
>   `rerun-failed-jobs` has been reachable there throughout. The log fix adds a second proof shape
>   to a lane that already acts.
> * **Crm / Reinsurance / SocialMedia** (Crm#125, Reinsurance#218, SocialMedia#202) were the ones the
>   hold applied to — their shell stewards could retry **nothing**, so merging is the moment retries
>   begin there. They were parked as **drafts** while that call was open, because each of those
>   repos runs `auto-arm.yml` and would otherwise have merged them on green with the call never
>   made. **The maintainer took the call on 2026-09-17 and it is MERGE**, on this reasoning: humans
>   press re-run today *because* the steward is blind, so an evidence-gated automatic retry is
>   narrower than the status quo. The `#4491`/`#4493` wave that closes the annotation erasure is
>   being landed separately.
>
> 🚨 **And the hazard belongs to `rerun-failed-jobs`, not to the steward.** #4491's own measurement
> is a re-run of an infrastructure death — *the ordinary response* — and a human pressing the same
> button erases the same annotation. Today humans press it **because** the steward is blind, so a
> working reader *reduces* hand re-runs rather than adding a hazard class. That is an argument for
> sequencing, not for leaving a reader that cannot read.
>
> 🚨 **The signature lists are a separate question, and they are thin.** The most frequent transient
> on the wall right now is `GitHub served a STALE run listing (MeshWeaver#4433)` from
> `Resolve the released platform`, whose own annotation ends *"Re-run this job"* — measured on
> MeshWeaver.Plugins job `105249591894` and MeshWeaver.SocialMedia job `105239967968` the same day,
> and it is in **no** steward's list. Adding it is a curation decision with its own evidence, not
> something to fold into a reader fix.

## 🚨 A check that is red on EVERY pull request is not telling you about any of them

A signal carries information only to the extent that it *varies*. A check that fails on every open
PR at the same instant has variance zero: it partitions nothing, exonerates nothing, and the
correct reading of it on any individual PR is "ignore this". That is a strictly worse state than
having no check at all, because the red is still spent — it dilutes every other red on the wall,
and it trains readers to skip the one place they are supposed to look.

**Measured here, 2026-09-01/02.** `auto-arm.yml` asserted that the `meshweaver-cloud` App could
mint a token carrying `Contents: write` + `Pull requests: write`. The grant was missing, so
`Arm auto-merge on this PR` failed — on **every PR in the repository, simultaneously and
permanently**. The wall read as four red PRs. Two of them were entirely green on the required
check; the other two had genuine, unrelated shard failures that the noise was actively hiding.

### The discriminator: is the fact a property of the PR, or of the repository?

That is the whole test, and it is mechanical:

| the assertion is about… | where it belongs | what its red means |
|---|---|---|
| this branch's code, tests, build, contracts | a per-PR check | *this* PR is not ready |
| the org installation, a secret, a registry grant, a quota | **one repo-scoped lane** | the *repository* is degraded; every PR is equally affected |

A repository-scoped fact asserted per-PR is duplicated N times and actionable in none of them —
nobody fixes an org installation from a pull request's Checks tab. Hoisting it does not weaken it:
`arm-credential.yml` fails exactly as red, on a schedule, naming the grant and the acceptance step,
in the one place where the fix is the obvious next action.

### This is NOT the skip-trapdoor exemption, and the difference is worth stating precisely

AGENTS.md forbids `continue-on-error` on a gate's input step, because a verification that silently
does not run is indistinguishable from one that passed. `auto-arm.yml` now carries exactly that
`continue-on-error` — and it is not the forbidden shape, because **arming is an action, not a
gate**. It asserts nothing about the pull request. Every required context still runs, `Consolidate
test results` still decides, and the honest per-PR consequence of a missing grant is "this PR was
not armed, merge it by hand" — a lost convenience, not a lost check. Reporting that as a *failure
of the pull request* was a false statement about the pull request.

The load-bearing part is that the assertion was **moved, not deleted**. A tolerance whose companion
assertion is gone *is* the trapdoor, so the two files are coupled by a test:
`ArmedMergeMustTriggerMainsPushLanesGuard.ToleratingAFailedMintRequiresARepoScopedAssertion` fails
the build if `auto-arm.yml` tolerates a failed mint while `arm-credential.yml` is missing, tolerates
its own failure, or loses its schedule. **When you hoist an assertion out of a hot lane, guard the
hoist** — otherwise the next person sees only the tolerance and reasonably concludes the check was
abandoned.

### Before you conclude "all the PRs are red"

Read *which* context is red, not the rollup colour. The required check is the only one that gates,
and a non-required red that is identical everywhere is the signature of a repo-scoped fact in the
wrong place:

```bash
gh api graphql -f query='query{repository(owner:"Systemorph",name:"MeshWeaver"){
  pullRequests(states:OPEN,first:20){nodes{number mergeStateStatus
    commits(last:1){nodes{commit{statusCheckRollup{contexts(first:80){
      nodes{... on CheckRun{name conclusion}}}}}}}}}}}'
```

If the same check name appears in every PR's failure list, stop triaging PRs and go fix the
repository. `UNSTABLE` means the required set passed and something non-required did not — it is
mergeable, and it is the state a hoistable assertion leaves behind.

### 🚨 The third case: the fact is repo-scoped, but the FIX is per-PR

The table above splits facts into "property of the PR" and "property of the repository", and tells
you that for the second kind you should stop triaging PRs and go fix the repository. That is right
about where the *cause* lives and wrong about what *clearing* it takes, whenever the assertion is
written in a workflow file — because **a gate defined in `.github/workflows/*.yml` evaluates the
version of that file in the pull request's own tree, never `main`'s.** Landing the fix on `main`
changes nothing for anything already open.

Measured twice on 2026-09-07, on two gates with nothing else in common:

**The platform staleness gate.** Three sessions concurrently held the belief that merging the pin
pull request would unblock `MeshWeaver.Plugins`. It would not have: 19 open PRs each carried their
own stale `MW_PLATFORM_REF`, and the gate read each PR's own copy. The remedy was to merge the pin
branch **into all 19**, then verify per branch. A pin move on `main` unblocks exactly zero open PRs.

**The Dependabot secret preflight — the more dangerous shape.** `MeshWeaver.Crm` added its
`MW_REGISTRY_KEY` assertion to the preflight on `main` at 12:45Z. Its five open Dependabot PRs carry
`ci.yml` from before that:

```bash
gh api "repos/Systemorph/MeshWeaver.Crm/contents/.github/workflows/ci.yml?ref=$(
  gh api repos/Systemorph/MeshWeaver.Crm/pulls/59 --jq .head.sha)" --jq .content \
  | base64 -d | grep -c 'MW_REGISTRY_KEY:-'
# 0   ← on main this reads 1
```

So `Required CI inputs` **ran and passed, having never asked about that secret**, and the run died
forty seconds later inside the consuming job with a message that names no secret at all:

```
##[error]compose-sealed-modules.sh: --registry-url needs --registry-key
```

A green preflight reads as *"the inputs are present"*. Here it meant *"this tree's preflight asked
for less"* — and a reader who trusts it concludes the repository is provisioned when it is not.
This is the concrete form of the rule AGENTS.md already states as **the preflight list is not the
denominator**: prove a secret exists from the job that consumes it, never from a preflight's colour.

### The discriminator, and the measurement

| question | how to answer it |
|---|---|
| is the gate's *verdict* stale? | read the gate's **definition at the PR's head sha**, not at `main` |
| will merging the fix help the open PRs? | only if you merge it **into** them — test `git merge-base --is-ancestor <fix> origin/<branch>` per branch |
| did the input actually get provisioned? | read the **consuming** job, not the preflight |

```bash
# the gate's definition as THIS pull request will run it
sha=$(gh api repos/Systemorph/<repo>/pulls/<n> --jq .head.sha)      # full 40 chars, always
gh api "repos/Systemorph/<repo>/contents/.github/workflows/ci.yml?ref=$sha" --jq .content | base64 -d
```

**The rule.** A workflow-defined gate is versioned with the branch it judges, so its fix propagates
like code, not like configuration. Before announcing that a gate fix unblocks anything, name the
branches it reaches — and reach them.

### Reading the wall over REST

The GraphQL query above answers the question, but AGENTS.md reserves GraphQL for what REST cannot
express, because the **secondary** limit it exhausts takes GitHub access away from every concurrent
agent while `/rate_limit` still reads full. The same wall over REST, one PR at a time, pacing:

```bash
gh api "repos/Systemorph/<repo>/pulls?state=open&per_page=100" --jq '.[].number' |
while read -r n; do
  sha=$(gh api "repos/Systemorph/<repo>/pulls/$n" --jq .head.sha)   # full sha; an abbreviated
  sleep 2                                                           # one silently matches NOTHING
  gh api "repos/Systemorph/<repo>/actions/runs?head_sha=$sha&per_page=100" \
    --jq --arg n "$n" '.workflow_runs[]|select(.conclusion!="success" and .conclusion!=null)
                       |"#\($n) \(.name): \(.conclusion)"'
  sleep 2
done
```

🚨 `head_sha=` matches only the **full 40-character** sha. An abbreviated one does not error — it
returns an empty `workflow_runs` array, which is byte-identical to "this commit has no runs" and
reads as *"CI never fired"*. That false negative is about the one thing a PR watcher exists to
detect, so print the sha's length before concluding anything from a zero.

## 🚨 A lane hand-copied into N repos is N lanes, and N−1 of them are stale

The arm lane is a single file, `.github/workflows/auto-arm.yml` in this repository, and every
satellite reaches it through `workflow_call`:

```yaml
# MeshWeaver.<Satellite>/.github/workflows/auto-arm.yml — the whole file
on:
  pull_request_target:
    types: [opened, reopened, ready_for_review, synchronize]
permissions:
  contents: write
  pull-requests: write
jobs:
  arm:
    uses: Systemorph/MeshWeaver/.github/workflows/auto-arm.yml@<sha>
    secrets:
      MESHWEAVER_APP_ID: ${{ secrets.MESHWEAVER_APP_ID }}
      MESHWEAVER_APP_PRIVATE_KEY: ${{ secrets.MESHWEAVER_APP_PRIVATE_KEY }}
```

It was not always. Until 2026-09-02 the file was hand-copied into every repo in the fleet with a
comment at the top asserting the copies were identical, and **they were not** — a comment claiming
"the single implementation, so they cannot drift" is a hypothesis, and this one was false in three
separate ways at once. Core had been moved to a minted App installation token; every satellite copy
was still arming with `secrets.GITHUB_TOKEN`. Only `.Crm` carried the `landed:` read-back branch.
Some copies had no `timeout-minutes` at all. (Figures below are a record of that day's measurement,
not a live inventory of the fleet — the roster is read with
`gh search code --owner Systemorph "auto-arm.yml@"`, never from prose.)

**The consequence is the #2916 outage, running unnoticed in every satellite.** An auto-merge is
performed as the identity that armed it; a push created with `GITHUB_TOKEN` does not trigger
workflow runs; so each satellite's `main` was accumulating merges that started nothing.
MeshWeaver.Reinsurance's `main` had no `push`-event run after 09:11Z that day, while PRs merged all
afternoon. Nothing was red anywhere, because the evidence that would have been red is precisely the
run that never existed.

**How to see it — ask main whether its last commits produced runs, not whether the runs passed:**

```bash
gh api repos/Systemorph/<repo>/actions/runs --jq \
  '[.workflow_runs[] | select(.event=="push" and .head_branch=="main")][0]
   | "\(.created_at)  \(.name)  \(.conclusion)"'
gh api repos/Systemorph/<repo>/commits/main --jq '.commit.committer.date'
```

A last-push-run timestamp older than main's HEAD commit is the signature. `github-actions[bot]` as
the merging identity on recent merges is the cause. Neither is visible from any pull request.

**The rule this generalises to** is already in AGENTS.md and `/ci`: a satellite's CI *calls* this
repository's reusable workflows, it does not copy them. A copied lane costs nothing on the day it
is copied and diverges silently forever after — and the divergence is invisible from inside any one
repo, because each copy is self-consistent. Only a fleet-wide read finds it.

## 🚨 Delivery can stop for hours with every dashboard green — look for CANCELLED, not failed

A cancelled run is not a failed run, and nothing alerts on it. `alert-on-failure` keys on failure;
the delivery verdict never runs; the required check on main is green because the *tests* passed. The
only symptom is a registry that stops receiving digests, which nobody watches minute to minute.

**Measured 2026-09-02.** `main-cd` promoted nothing between 03:33:50 and 07:50 — over four hours —
while the repository looked entirely healthy. In one ten-minute window, six CD runs were created and
**every one was cancelled**:

```
07:35  PR fix/arm-red-is-repo-scoped           -> CD run, cancelled
07:37  PR ci/hard-cap-every-job-at-45-minutes  -> CD run, cancelled
07:39  main push                               -> CD run, cancelled   <- real delivery
07:40  PR fix/oversized-pod-hub-delivery       -> CD run, cancelled
07:41  PR fix/3022-identity-fork-remainder     -> CD run, cancelled
07:43  main push                               -> CD run, cancelled   <- real delivery
```

### The mechanism: an unfiltered `workflow_run` plus a shared concurrency group

`main-cd` triggers on `workflow_run` of *MeshWeaver Build and Test*. That fires on completions from
**every branch**, pull-request builds included. All of those runs key into the same `concurrency`
group, and GitHub keeps exactly **one pending run per group** — each arrival cancels the pending one.
So a genuine push-path delivery run, sitting pending behind whatever is in flight, is evicted by the
next PR build that happens to finish.

**The failure scales with pull-request throughput.** Delivery stops hardest exactly when the repo is
busiest, and it presents as "CD is slow today". Draining a PR queue makes it strictly worse, which
inverts the usual intuition that merging more is progress.

### Why this survives review, and why the run list actively misleads

For a `workflow_run` event, the resulting run's `head_branch` is the **workflow file's** ref — always
the default branch. A CD run started by a PR build therefore reports `head_branch=main`, exactly like
a real one. Querying the CD runs *confirms* the wrong hypothesis. The triggering branch survives in
only one place: the trigger's own `branches:` filter.

To see what actually started them, list the *triggering* workflow's runs in the window instead:

```bash
gh run list --repo <owner>/<repo> --workflow "MeshWeaver Build and Test" --limit 20   --json headBranch,event,conclusion,updatedAt   --jq '.[] | select(.updatedAt >= "<from>" and .updatedAt <= "<to>") |
        "\(.updatedAt[11:19]) \(.headBranch) \(.event)"'
```

### The rule

**A `workflow_run` trigger on a workflow that declares a `concurrency` group MUST carry
`branches:`.** Without the concurrency group an unfiltered trigger is usually harmless and sometimes
intended — `retry-known-transients.yml` deliberately retries transients on PR builds and evicts
nothing. The two together are what makes off-branch triggers destructive.

Guarded by `WorkflowRunTriggerBranchFilterGuard`, which carries a control arm: if its block matcher
ever stops recognising `workflow_run:`, it fails rather than passing having examined nothing.

### 🚨 A CD run cancelled with ZERO jobs was evicted from the pending slot — the delivery it superseded is the run still in flight

The section above is the *unfiltered* trigger, fixed on 2026-09-02. What remains — by design — is
GitHub's own rule inside the filtered group: **one run executes, one waits, and every further
arrival REPLACES the one waiting.** The replaced run reports `cancelled` with `run_attempt: 1` and
**zero jobs**; the run in flight is untouched (`cancel-in-progress: false`). Read as "CD is being
killed", this shape has now held a roll twice — 2026-08-30 on Build and Test (#2412, where it WAS a
defect and got a per-commit group) and 2026-09-08 on `main-cd` (where it is the design) — each time
while the run that mattered was executing normally.

**Measured 2026-09-08, every `main-cd` run created between 09:00Z and 10:09Z** (REST:
`actions/runs/<id>` for the timestamps and attempt, `…/runs/<id>/jobs` → `total_count`):

| run | commit | conclusion | jobs | cancelled at | next arrival, created |
|---|---|---|---|---|---|
| 34207602172 | 3ee8dda6e | cancelled | 0 | 09:01:46 | 34207717540 @ 09:01:45 |
| 34207717540 | 3ee8dda6e | cancelled | 0 | 09:04:10 | 34207938426 @ 09:04:08 |
| 34207938426 | 3ee8dda6e | cancelled | 0 | 09:29:19 | 34210258845 @ 09:29:18 |
| 34210258845 | 4e1a1b633 | cancelled | 0 | 09:33:37 | 34210667593 @ 09:33:36 |
| 34210667593 | 4e1a1b633 | cancelled | 0 | 09:41:59 | 34211432040 @ 09:41:58 |
| 34213195672 | 1c61ed3ae | cancelled | 0 | 10:06:05 | 34213625327 @ 10:06:03 |
| 34213625327 | 6b2fe2a10 | cancelled | 0 | 10:08:46 | 34213872077 @ 10:08:44 |
| 34206854855 | 765fb7f52 | **failure**, at its seal | 25 | — | in flight 08:52–09:45, never cancelled |
| 34211432040 | 60449106f | in progress | > 0 | — | started 09:45:52, the second the run above completed |

Seven cancellations, seven arrivals, each 1–2 s apart, `run_attempt: 1` on every one; the two runs
that were executing ran to their seal. Nothing was starved: the pending slot always held the
**newest** commit, and the commit order on `main` (3ee8dda6e → 4e1a1b633 → 60449106f → 1c61ed3ae →
6b2fe2a10) is the arrival order, so every evicted run was an ancestor of the run that replaced it.
(Three runs for one commit is the merge queue: Build and Test completes on the queue branch and on
`main` for the same sha, and each completion is an arrival — no-ops that still hold the slot,
#2490.)

**The arithmetic under continuous merging.** A run holding the slot seals in roughly 50 minutes
(#8103, 60449106f: created 09:41:58, `completed/success` 10:31:23; the next run, #8108, started its
first job at 10:31:25 — two seconds later, off the group slot, not a runner). In the same window
`main` merged about every seven minutes, so every arrival that landed while a run held the slot was
evicted by the one after it (10:01, 10:06, 10:08, 10:21, 10:38, 10:43 — all `jobs=0`), and only an
arrival that happened to be the LAST before the slot freed got through. The commit that ships is
therefore "newest at the moment the slot frees", and a commit that lands in the middle of a burst
is delivered only as part of a later run's superset — never as its own. **The lever is the merge
gap**, not the workflow: a deliberate merge-quiet window when a specific commit must seal is the
maintainer's call, and cancelling other runs to make room is not a lever at all — the run in flight
is the one doing the delivering.

**The three things a cancelled run can be, told apart from the record alone:**

| cause | `jobs` | timing | corroboration |
|---|---|---|---|
| evicted from the pending slot (this shape) | **0** | `updated_at` = the next arrival's `created_at` ± 2 s | the in-flight run of the same group is untouched |
| the org's Actions budget or job ceiling | > 0, jobs cut mid-flight | no correlation with arrivals; runs in **every** repository of the org stop in the same minutes | a billing banner on the org — and here, `MeshWeaver.Plugins` runs in the same minutes executed normally |
| a session or a person | any | no correlation with arrivals | seven cancellations 1–2 s after seven arrivals is not a hand |

🚨 **The instrument rule — decide "started nothing" from the run's OWN record, never from the list's
`status`.** The list endpoint's `status` is not monotonic: at 10:57Z it reported #8108 and #8109
(both 765cd55e6, created 10:30) as `queued`, which read as "waiting 40 minutes for a runner — the
in-flight slot is starving every seal behind it, the #888 shape by another mechanism". The runs'
own `jobs` endpoint at 11:12Z said 23 jobs, 23 started, 22 completed — 42 minutes of real work
against a 50-minute bake, no starvation, and a retraction. `jobs.total_count`, each job's
`started_at`, and the per-run `conclusion` are the readings; `queued` on a listing is not one.

**What to do with one: nothing.** The commit it would have built is an ancestor of the commit the
slot now holds, and the push lane builds that one next; if the push lane ever falls behind, the
hourly reconcile targets the newest commit CI has vouched for (#3077). A cancelled run with zero
jobs cannot have torn an image set or a publication — it never ran a step. **The conclusion that
matters is the OTHER one in the same list**: a `failure` on a run that DID execute (34206854855
above, red at its seal), which no amount of "the runs keep getting cancelled" explains.

**Why `main-cd` keeps ONE group on the ref while Build and Test gives every main commit its own** —
`MainRunsAreNeverCancelledGuard` holds both shapes, deliberately opposite, each with a control arm
that fires on the other's expression: a test run for a landed commit is evidence nothing else
produces, so evicting it loses the only build of that tree; a delivery run for a superseded commit
produces images nobody will pull and a publication the next run re-seals, and two CD runs
publishing the same framework identity at once is the overlap #3461 describes. So on `main-cd` the
eviction IS the supersede rule, applied by GitHub in arrival order. The one thing GitHub does not
check is ancestry: if Build and Test ever completed out of commit order, an older commit's run
could evict a newer one's — and the reconcile's next tick covers that hour.

### 🚨 Count the SEALS, not the cancellations — the eviction rate answers no question you have

The rule above has now been re-derived as a defect a third time (#4322, 2026-09-14: *"half of
today's 30 CD runs were cancelled … every one is a seal that did not happen"*). The reading survives
because **the cancellation count is easy to get and answers nothing**, while the question behind it —
*is delivery happening?* — has its own instrument that nobody reaches for.

**The census, run the same day the claim was filed** (snapshot 2026-09-14T17:43Z — a day still in
progress, which is why the totals are stated with their clock). Every `main-cd` run created since
00:00Z, each one's `…/runs/<id>/jobs` read:

| | |
|---|---|
| runs | **58** (not 30 — the claim's window missed the schedule lane) |
| cancelled | 25 — **every single one with `jobs.total_count == 0` and `run_attempt: 1`** |
| cancelled runs holding a `Promote` job in ANY state | **0** |
| runs that reached `Register the publication with memex = success` | **17** |

So no seal was lost: not one evicted run had reached — or even started — the job that seals. And the
claim's second half (*"the last non-schedule run that could seal was #8584 at 12:30Z"*) was already
stale when it was written: **#8587 sealed at 13:27Z** and **#8596 at 14:50Z**, both `workflow_run`.

**The instrument for "is delivery happening" is the seal**, and it fits on a screen:

```bash
day=2026-09-14; repo=Systemorph/MeshWeaver
gh api --paginate "repos/$repo/actions/workflows/main-cd.yml/runs?per_page=100&created=%3E%3D$day" \
  --jq '.workflow_runs[] | "\(.run_number) \(.id)"' \
| while read -r number id; do
    sealed=$(gh api --paginate "repos/$repo/actions/runs/$id/jobs?per_page=100" \
               --jq '.jobs[] | select(.name | test("Register the publication")) | .conclusion' \
             < /dev/null)
    printf '%s %s\n' "$number" "${sealed:-—}"
  done
```

🚨 **Three ways this very recipe under-reports, all of them silent, and this section is about
exactly that — so they are named rather than trusted:**

- **`--paginate` on BOTH calls.** A day busier than 100 runs, or a run with more than 100 jobs,
  otherwise drops rows and the seal count comes out low with nothing saying so.
- **`created=%3E%3D$day` bounds the first call** so `--paginate` walks the day rather than the
  workflow's entire history.
- **`< /dev/null` on the inner call.** Without it the inner `gh` reads the LOOP's stdin and eats the
  remaining run lines. Measured while writing this: **51 of 58 runs listed**, exit 0, no error
  anywhere — the seal count silently 2 short. (Same shape as the harness case
  "the read-back loop cannot be truncated by something eating its stdin".)

Read the OUTPUT, not the exit code: `—` for a run that sealed nothing is the common, healthy line
(a reconcile that found the set complete), and a run whose overall conclusion is `failure` can still
show `success` here — #8593 did, red on a later step, with its publication registered. The seal is
the question; the run's colour is not.

A day with seals every 30–90 minutes is a healthy lane however many evictions it shows, because the
eviction rate rises with the MERGE rate and the seal rate does not fall with it.

**And when there IS a gap, the cause is on a run that executed.** The longest gap that same day was
08:40Z → 11:28Z, and it was not eviction: `#8577` (09:12Z) and `#8581` (09:53Z) both promoted their
image set and baked the platform content successfully, then went red on
`Plugins: pack the module bundles … / Module tests` → `Run the module's tests`. The two hourly
reconciles in between (`#8580`, `#8582`) ran three jobs each and correctly declined to publish — the
image set for the target commit was complete, because those two runs HAD promoted it. Eviction
explains none of that, and the module-test failure explains all of it.

### The other direction: a CD red on main that means nothing

The section above is delivery stopping behind green ticks. The inverse cost the same evening: the
hourly `main-cd` reconcile fired **"main `<sha>` is GREEN and has an INCOMPLETE image set, and this
reconcile published nothing — delivery is stuck"** twice inside forty minutes (runs 7963 and 7965,
2026-09-07), on two commits that both sealed shortly afterwards. The set was incomplete because the
reconcile's own `workflow_run` twin was **mid-publish** — a scheduled job reasoning about a state
its sibling was halfway through creating, and reporting the intermediate as a defect.

**A red that means nothing is expensive in the same way a green that means nothing is**: it is
mixed in with reds that mean everything, and people were reading CD conclusions to decide a release
that night. `delivery-verdict` now probes the run list before making the claim and reaches a **third
verdict** — *incomplete because a publication is in flight* — distinct from *incomplete and nobody
is fixing it*; only the second is a defect, and it is still red. Before treating any CD red on main
as an incident, read whether the run named one. Mechanism, and why "skip while a run is in flight"
would have been the wrong fix:
[Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract).

## 🚨 A queue ejection is not a red on the PR — read the steward's comment, not the PR's checks

With the merge queue on, a pull request's own checks can be entirely green while the PR is *not
landing*: the queue built it on top of the entries ahead of it, that group build failed, and the
entry was ejected. The red lives on a `merge_group` run whose head branch is
`gh-readonly-queue/main/pr-<N>-<sha>`, not on the PR's commit — `gh pr checks` shows nothing.

The merge-queue steward acts on every ejection and leaves a comment saying what it found and did:
re-queued (a catalogued flake, an infrastructure death, a timeout, or a bisect of a multi-PR group),
or left out with the failing assertion, the run URL and the `queue-rejected` label. Read that
comment first. Never re-run the failed queue build and never re-queue by hand — a re-run hides the
bug and destroys the control arm; the steward re-queues on evidence and records the attempt. The
whole protocol is [The Merge Queue](/Doc/Architecture/MergeQueue).

## 🚨 GitHub's run LISTING can be served stale — judge page 1 against a fact it cannot fake

A run listing (`actions/workflows/<wf>/runs?branch=main&per_page=…`) is sometimes answered from an
old snapshot. The page is well-formed, correctly ordered and internally consistent — it is simply
days behind — and the same query issued a minute later is correct. Measured instances:

| when | query | what came back |
|---|---|---|
| 2026-09-14 | a satellite's `ci.yml` runs, `status=success` | a page from 2026-08-19, twelve runs that predate the job being looked for |
| 2026-09-15 13:03Z | core `main-cd.yml` runs, page 1 | began at #8423 while #8676 was sealed (~260 runs behind) |
| 2026-09-15 14:07Z | the same | began at #8420 — the same stale point, an hour later |
| 2026-09-15 ~14:2xZ | Plugins `ci.yml` runs, `per_page=1` | a run from 2026-08-10 as the newest |

A reader that takes "the first matching run on page 1" as the newest acts on the stale answer
without a trace. `resolve-platform.py` did exactly that (#4433): with a declared floor it went red
naming the floor, and without one — every satellite but Plugins — it would have compiled, tested
and published against a three-day-old platform while reporting it as the newest.

**The rule: make the wrong answer harmless, never retry it away.** Check page 1 against a fact the
listing cannot supply itself, and refuse — red, naming the staleness — when it fails:

- **the clock**, when the workflow runs on a known cadence: core CD runs on `main` at least hourly
  (an hourly `schedule` plus every main build; the widest gap in 300 measured runs was 1.7 h), so a
  page whose newest main run is over 12 h old is not the newest page;
- **a run number known to exist** from another read: a set the caller's own `main` already passed
  on cannot be missing from a fresh listing.

A retry keyed on "this answer is inconvenient" is a gate testing its own inputs; the refusal is
the answer, and a re-run of the job reads the listing again. A freeze is exempt — it names one set,
and an incident is when it must keep working.

The same family has two more readings that look like verdicts and are not: `workflow_runs[0]` for
a head sha can be a run **cancelled** by its concurrency group while a sibling run for the same
sha is live (judge the highest-id non-cancelled run), and a `head_sha` filter given a SHORT sha
matches nothing, silently.

## Related

[The Merge Queue](/Doc/Architecture/MergeQueue) · [Module Versioning](/Doc/Architecture/ModuleVersioning)
· [Modules](/Doc/Architecture/Modules) · [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges)
· [The Dependabot Secret Store](/Doc/Architecture/DependabotSecretStore) — a red that names a secret
you can see provisioned, and why the exemption that would silence it is only safe in core
· [Duplicate Keys in Workflow YAML](/Doc/Architecture/WorkflowDuplicateKeys) — a workflow can
parse, pass every shape gate and run with a value the diff does not show; the loader keeps the LAST
of two identical keys and says nothing
