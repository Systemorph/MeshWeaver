---
Name: A Stale Run Listing Is Not a Broken Main
Category: Architecture
Description: GitHub serves the platform resolver a weeks-old page of workflow runs, per call, with no error — three measured occurrences. The refusal that results is correct; twice its wording sent the reader at a `main` that was fine. What each of the two listings can and cannot check, why neither retries, and how the refusal now orders its remedies by the evidence it actually read.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 8v5l3 2"/><path d="M3 4l18 16"/></svg>
---

# A Stale Run Listing Is Not a Broken Main

`.github/scripts/resolve-platform.py` decides which sealed platform set a repository builds and
tests against. It reads **two** listings of GitHub workflow runs, and GitHub has served a
**weeks-old page** of both — per call, with HTTP 200, no error and a correct-looking body.

The refusals that follow are right. **The wording of one of them was not**, and that is what this
page is about: a refusal that names the wrong cause spends the reader's time in the wrong place,
and they trust it while doing so.

## The two listings, and why only one can be checked

| | What it reads | Can it be checked? |
|---|---|---|
| **The core CD listing** | `Systemorph/MeshWeaver` `main-cd.yml` runs — the candidate platform sets | **Yes.** Core CD runs on `main` at least hourly (widest gap in 300 measured runs: 1.7 h), and where a ceiling was asked for, a run number this repository has already passed on is a floor the page cannot fake. `stale_listing()` refuses a page failing either. |
| **The satellite's own ceiling listing** | the CALLING repository's successful `ci.yml` runs on `main`, whose `Platform for this run` notice says which set main passed | **No, and not by omission.** A repository's `main` may genuinely be quiet for days, so age proves nothing; and the ceiling is the very thing being established, so it cannot check itself. |

Neither re-reads. **A resolver that decides its own input must be wrong and asks again is a gate
testing its own inputs** — the red is the harmless answer, and any re-run reads the listing again.

## What was measured

| When | Where | The page it was served |
|---|---|---|
| 2026-09-14 | MeshWeaver.Plugins run `34822109263` (ceiling listing) | twelve runs from **2026-08-19** — all predating the job whose notice is being read |
| 2026-09-15 ×2 | core `main-cd.yml` listing | page 1 beginning ~**260 runs** behind (`#8423`/`#8420` while `#8676` was sealed) |
| 2026-09-17 | MeshWeaver.Plugins PR #2038, job `105367690432` (ceiling listing) | twelve runs from **2026-08-12/13**, newest `31741597338` created `2026-08-13T20:35:15Z` |

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

- **The discriminator is structural and carries no clock.** A page whose every row lacks the job
  that publishes the verdict cannot have been read for its content at all; a page whose rows *do*
  carry that job is `main` answering for itself, and then the refusal leads with `main` — citing
  the runs that implicate it. No threshold was invented to sort the two messages: a bound picked
  for wording is a bound nobody can tune, and it would eventually flip the text on a healthy
  repository that happened to be quiet.
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
would be a guess that ships silently. What is *not* warranted in either case is a retry: see the
first section.

## Related

- [CI Content Bake](/Doc/Architecture/CiContentBake) — the resolver's three questions (the ceiling, the freeze, the floor)
- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — the other ways a CI answer reads like a pass
- [Platform Script Resolution](/Doc/Architecture/PlatformScriptResolution) — how a repository gets this script at all
