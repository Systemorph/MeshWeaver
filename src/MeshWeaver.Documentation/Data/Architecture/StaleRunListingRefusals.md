---
Name: A Stale Run Listing Is Not a Broken Main
Category: Architecture
Description: GitHub serves the platform resolver a weeks-old page of workflow runs, per call, with no error — eight measured occurrences (five on the core CD listing, three on the satellite ceiling listing), one of which left `main` without a verdict and one of which resolved an OLD ceiling SILENTLY. The refusal that results is correct; twice its wording sent the reader at a `main` that was fine, and until MeshWeaver#4750 it cost a whole CI cycle every time. What each of the two listings can and cannot check, which branches are re-read and why only those, and how the refusal orders its remedies by the evidence it actually read.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 8v5l3 2"/><path d="M3 4l18 16"/></svg>
---

# A Stale Run Listing Is Not a Broken Main

`.github/scripts/resolve-platform.py` decides which sealed platform set a repository builds and
tests against. It reads **two** listings of GitHub workflow runs, and GitHub has served a
**weeks-old page** of both — per call, with HTTP 200, no error and a correct-looking body.

The refusals that follow are right, and they stay. Two things about them were not, and this page is
about both. **The wording of one of them named the wrong cause** — a refusal that does that spends
the reader's time in the wrong place, and they trust it while doing so. And **one of them refused
on the first read** where a second read, minutes later, would have answered correctly — which cost
a whole CI cycle every time.

**Eight occurrences are measured below, and they are not all the same measurement.** Counting the
table's `×N` rows: **five** are the **core CD** listing (2026-09-15 ×2 and 2026-09-18 ×3) — the
listing MeshWeaver#4750 taught to re-read, and one of the three on 09-18 left `main` itself without a
verdict. The other **three** (2026-09-14, 2026-09-17, 2026-09-23) are the **satellite's own
ceiling** listing. The first two refused, correctly; the third did NOT — its stale page still named
a set, so an old ceiling was resolved silently — and that is what the witness described under
*The silent kind* below now proves and re-reads. (`ceiling_refusal`'s own text says *"measured three times"* for the two refusals: it
counts the 2026-09-17 refusal as two, which the table records as one row.)

## The two listings, what each can be checked against, and which branches are re-read

| | What it reads | Can it be checked? |
|---|---|---|
| **The core CD listing** | `Systemorph/MeshWeaver` `main-cd.yml` runs — the candidate platform sets | **Yes.** Core CD runs on `main` at least hourly (widest gap in 300 measured runs: 1.7 h), and where a ceiling was asked for, a run number this repository has already passed on is a floor the page cannot fake. `stale_listing()` refuses a page failing either. |
| **The satellite's own ceiling listing** | the CALLING repository's successful `ci.yml` runs on `main` (`status=success`), whose `Platform for this run` notice says which set main passed | **Against a WITNESS, never against age.** A repository's `main` may genuinely be quiet or red for days, so age proves nothing. But the same workflow's **unfiltered** `branch=main` listing is a different query, and a run there that is completed, `success`, on `main`, of a vouching event and **newer** (higher run id) than every row the filtered page returned satisfies every filter of the filtered query — a fresh page 1 must contain it. `main_listing_witness()` looks for one. |

**Each listing re-reads only on its PROVABLE branch.** The rule that decides which is
not "retry transients"; it is *what kind of fact is the staleness*:

| The finding | What kind of fact | Re-read? |
|---|---|---|
| The core CD page is missing a run **below the ceiling** this repository's `main` has passed on | **Proven.** A run at least that high exists, so the page is a read inconsistency and nothing else — and "contains a run ≥ the ceiling" is a crisp condition to re-read *toward* | **Yes** — up to `STALE_REREADS` (3) on a 20/40/60 s backoff, then the same refusal (MeshWeaver#4750) |
| The core CD page's newest run is **over 12 h old** | **Inferred** from core CD's measured cadence. A genuinely quiet core serves the same page every time | No — the budget would be spent reaching the same refusal |
| The satellite's **ceiling listing** lacks a newer completed-`success` vouching `main` run that the **unfiltered** listing shows | **Proven**, by the witness run, which satisfies every filter of the query | **Yes** — the same `STALE_REREADS` budget and backoff, then a refusal carrying the #4433 prefix (below) |
| The satellite's **ceiling listing** merely looks old | Nothing to infer *from*: a red or quiet `main` legitimately keeps an old ceiling | No — used as served; see the section below when it names no set at all |

**A resolver that decides its own input must be wrong and asks again is a gate testing its own
inputs** — which is why a re-read is allowed to change only *GitHub's answer*, never this script's
verdict about it: the refusal, its conditions and its strictness are identical, and a page still
stale when the budget is spent is still refused, never resolved from. That is the same argument
already accepted for a 502, and `fetch` already retries every GitHub-side failure that *announces*
itself — a 5xx, a 403/429 rate limit, a transport fault. A stale-but-`200` listing was the one
inconsistency that did not announce itself, and the one whose own refusal text prescribed the
retry.

## What was measured

| When | Where | The page it was served |
|---|---|---|
| 2026-09-14 | MeshWeaver.Plugins run `34822109263` (ceiling listing) | twelve runs from **2026-08-19** — all predating the job whose notice is being read |
| 2026-09-15 ×2 | core `main-cd.yml` listing | page 1 beginning ~**260 runs** behind (`#8423`/`#8420` while `#8676` was sealed) |
| 2026-09-17 | MeshWeaver.Plugins PR #2038, job `105367690432` (ceiling listing) | twelve runs from **2026-08-12/13**, newest `31741597338` created `2026-08-13T20:35:15Z` |
| 2026-09-23 | MeshWeaver.Plugins PR #2307, run `35830062164` (ceiling listing) — **no refusal** | newest vouching run `35587322366` (created 2026-09-21T10:10Z, set `9081`) while `main` had green, annotated runs `35822711093` (05:30Z) and `35821116466` (05:07Z) on set `9218`; the ceiling resolved `9081` SILENTLY and the build failed `CS0246` on a core type newer than 9081. The same script run by hand minutes later printed `9218` |
| 2026-09-18 ×3 | MeshWeaver.Plugins, core CD listing — PR #2071 run `35328406174`, PR #2043 run `35334000904`, **`main`** run `35345612101` | page 1 ~**2,600 runs** behind on the first (newest `main-cd #6215` against a ceiling of `#8892`); `main-cd #8423`, 143 h old, on the third. **17, 17 and 18** downstream jobs red; every hand re-run green minutes later with no code change |

The 2026-09-17 page was not garbage: run `31741597338` is a genuine `success` `push` run of `ci.yml`
on `main`, created exactly when the page said. The rows are strictly descending and contiguous — an
**ordered page of that exact query, about five weeks out of date**. The same query the next morning
answered `total_count: 1024`, newest `35305000971` created `2026-09-18T03:55:16Z`.

Meanwhile, in the same minutes as that refusal, three sibling pull requests resolved
`3.0.0-ci.8820` successfully, and a whole-run re-run resolved it ~22 minutes later with `main`
unchanged. **The listing was the only thing wrong, and it was wrong for one call.**

## The defect this page exists for

The ceiling's refusal read, in full, as one paragraph: the preamble, then twelve identical
`no 'Resolve the released platform' job — skipped` notes, then the stale-listing sentence, and then
its closing instruction:

> …**Fix main**, or set the repo VARIABLE MW_PLATFORM_REF to select one set explicitly.

Three readers met that text. Two spent their time on `main`. The third reported that **no pull
request in the repository could resolve a platform set**, while four were resolving one; a second
investigation was needed to overturn it. The explanatory sentence was present and ahead of the
remedy — and it did not reach anybody, because it sat mid-paragraph behind twelve skip lines while
the *instruction* said something else.

**The sentence was right; its position was wrong.** Documenting the prior occurrence in a code
comment had already been tried, in the same file, predicting this exact cost.

## What the refusal does now

It is the same refusal — same strictness, same conditions, no retry, no fallback. It prints
**what it read** and then **remedies in an order the evidence decides**:

```
WHAT WAS READ (one call, no re-read):
  · 12 successful `ci.yml` run(s) on Systemorph/MeshWeaver.Plugins main (push/…) examined,
    from 12 row(s) listed of N the listing declares
  · newest examined: run 31741597338, created 2026-08-13T20:35:15Z (35 days ago) — <url>
  · 12 carried no `Resolve the released platform` job at all — they predate it, so not one
    of them COULD name a set

  1 · TEST THE LISTING — one click, and it is the cause measured three times.
      Open <the workflow's successful-main-runs list>
      If ANY successful run there is NEWER than 2026-08-13T20:35:15Z, GitHub served THIS CALL
      a stale page: main is fine, and nothing about main needs fixing. …
      REMEDY: `Re-run all jobs` on this run, or push an empty commit. …
  2 · ONLY IF that is really main's newest success … Fix main.
  3 · TO PROCEED WITHOUT EITHER: MW_PLATFORM_REF …
```

Three things make it hold:

- **The discriminator is structural and carries no clock.** The question it asks is *was any
  evidence about `main` read at all* — only a run that **carried** the reporting job and still
  named no set says anything about `main`. Where there is none — every row predating the job, or
  unreadable, **or any mixture of the two** — the listing leads; where there is one, `main` leads,
  citing the count. (Counting the skip reasons separately instead left a mixed page leading with
  `main` while naming zero runs that implicated it; found in review of the fix itself.) No
  threshold was invented to sort the two messages: a bound picked for wording is a bound nobody
  can tune, and it would eventually flip the text on a healthy repository that happened to be
  quiet.
- **The evidence is printed, in the sentence the reader acts on.** The month-old run ids were the
  tell that unlocked the 2026-09-17 investigation, and they were found by digging. The newest run's
  id, date, **age** and URL are now in the refusal itself, not only in the skip notes above it.
- **The remedy is a WHOLE-run re-run or an empty commit**, never `Re-run failed jobs`: a
  failed-jobs re-run can hand a later job the earlier attempt's artefacts.

`MW_PLATFORM_REF` remains the escape hatch and is deliberately **last** — it is an instruction for
an incident, not a way around a red `main`.

### The guard that keeps it that way

`misdirects_to_main()` rejects any refusal that tells a reader to fix `main` without the evidence
that implicates main standing in the same remedy. Its control is `MEASURED_MISDIRECTION` in the
self-test — **the literal message this file produced on 2026-09-17** — which the guard must reject;
without that control the ordering cases would be decorative. Measured: with the old message shape
restored, ten of the self-test's cases go red.

## Still open: why the page is stale

**Unfixed, and GitHub-side.** Both affected calls are the workflow-scoped run listing with filters
(`/actions/workflows/{file}/runs?branch=main[&status=success]`), the observed lag ranges from ~3
days to ~5 weeks, and the correct answer comes back seconds or minutes later from the same query and
the same credential. That spread fits *a stale cached response of unknown age* better than *a
replica with a fixed lag*, but nothing measured so far separates the two.

The next occurrence can separate them, because the refusal now records the envelope's own
`total_count` beside the rows:

- an **old** `total_count` with old rows ⇒ the whole envelope is one stale snapshot;
- the **current** `total_count` with old rows ⇒ the count and the rows come from different places,
  and a different query shape (dropping `status=success`, or paging differently) becomes worth
  testing.

Until one of those is measured, **no query change is warranted** — changing the call on a hypothesis
would be a guess that ships silently. The bounded re-read (#4750) does not settle it either, and is
not meant to: it recovers the *job*, and it neither tells the two apart nor makes the question less
worth answering. It does give the next occurrence three more samples to record.

## The silent kind: a stale ceiling page that still names a set

Every earlier ceiling-listing occurrence served a page so old that **no** row carried the reporting
job, so the reader refused and said why. On 2026-09-23 the stale page was only two days old: its
newest row **did** carry the notice, named set `9081`, and the ceiling resolved from it with no
refusal at all — nothing on the page itself distinguishes that from a `main` that has genuinely
passed nothing newer, which is exactly why the page was trusted.

The guard is a second, independent fact rather than a clock:

- **The witness.** `main_listing_witness()` reads page 1 of the same workflow's **unfiltered**
  `branch=main` listing and keeps a run only if it is `completed`, `success`, on `main`, of a
  vouching event, and newer (higher run id) than every row of the filtered page. That run satisfies
  every filter of the filtered query, so its absence proves the filtered page stale.
- **No witness, no change.** A newer run that failed (a red `main`), is still running, is a
  pull-request run, is on another branch or is a re-run of an older run proves nothing, and the page
  is used as served. A probe that could not be READ is noted in the log and never refused on — an
  unread probe is not evidence of staleness.
- **Proven, so re-read, then refuse.** The same bounded budget as #4750, logged per re-read. The
  probe runs ONCE: after a witness is found, the page counts as settled only when a re-read
  actually CONTAINS a run at least as new as the witness — so a probe that fails on a later read
  cannot end the re-reads and resolve from the stale page (review finding on #5495, pinned by a
  self-test case that fails on the re-probing version). A page still missing the witness is refused with text beginning **`GitHub served a STALE run listing
  (MeshWeaver#4433): page 1 of`** — the prefix MeshWeaver.Plugins' transient-retry steward keys its
  run-void signature on, so the run is re-run in full rather than its cascade classified as real
  reds. The self-test carries that prefix as an independent literal (`STEWARD_4433_PREFIX`).

**What it cannot do:** prove freshness. If the unfiltered listing is served from the same stale
snapshot (whether the two share a cache is **not established**), or its page 1 is all queued runs,
it finds no witness and the old behaviour stands — the guard can only ever turn a silent old
ceiling into a re-read or a red, never the reverse. Its negative control: with the witness disabled,
exactly the four proving self-test cases fail (ceiling `9081` where `9218` or a refusal was
expected) and the six controls pass; with the conclusion and event filters dropped, the red-`main`
and pull-request controls fail.

## The refusal's wording is a KEYED signature — do not re-word it

The refusal's closing sentence is the text a transient-retry steward matches on
(MeshWeaver.Plugins#2077 / #2123, `Hosting/RetryingKnownTransients.md`), so it is kept **verbatim**
including the clause that the re-read made untrue of the ceiling branch:

> Re-run this job; the resolver refuses rather than re-reading, because this red is the harmless
> answer and a silently old platform is not.

The correction is **appended** instead — `reread_note()` adds what the re-reads saw and says in the
same breath why the sentence above it was not edited. Re-wording it would stop the steward
recognising the refusal as the known transient it is, silently, on a red nobody is watching. The
self-test carries that sentence as an **independent literal copy** (`KEYED_4433`) for the same
reason a control has to be independent: a case asserting a module constant against itself would
pass however the refusal were re-worded.

## Related

- [CI Content Bake](/Doc/Architecture/CiContentBake) — the resolver's three questions (the ceiling, the freeze, the floor)
- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — the other ways a CI answer reads like a pass
- [Platform Script Resolution](/Doc/Architecture/PlatformScriptResolution) — how a repository gets this script at all
