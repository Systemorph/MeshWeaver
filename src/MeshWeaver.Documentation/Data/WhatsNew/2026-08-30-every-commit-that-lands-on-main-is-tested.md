---
Name: Every commit that lands on main is tested again — merges no longer evict each other
Category: Fix
Description: Turning off cancel-in-progress was not enough: GitHub keeps only one pending run per concurrency group, so a burst of merges evicted the queued runs and three landed commits went untested and unpublished. Each main commit now gets its own group.
Icon: Bug
Order: -20260830
---

# Every commit that lands on main is tested again

Merge two or three PRs close together and the middle ones were **never built** — and nothing they
contained was ever published. The runs showed up as `cancelled`, which read like someone had
cancelled them.

## What was actually happening

`Build and Test` already declared `cancel-in-progress: false` for `main`, precisely so merges could
not kill each other's runs. That protects the run that is **already executing** — but GitHub keeps
exactly **one pending run per concurrency group**, and every main push shared one group. So the
third merge evicted the second's *queued* run, which then reported `cancelled` without having run a
single step.

Measured on 2026-08-30, in one burst:

| commit | queued | cancelled |
|---|---|---|
| `dca02bdd7` | 11:42:10 | 11:53:02 — 1 s after the next push |
| `57de73c45` | 11:54:57 | 12:08:54 — 2 s after the next push |
| `637ee3921` | 12:08:52 | 12:09:16 — 1 s after the next push |

Both consequences the setting exists to prevent happened anyway. Nothing compiled the tree that
actually landed (each PR is tested against the main it branched from, so the *merged* combination
is first built by main's own run). And nothing shipped: delivery gates on `Consolidate test results`
succeeding **for that commit**, which an evicted run never produces — three CD runs reported green
with every job skipped, while the newest published image stayed several commits behind.

## What changed

A push to `main` now groups by its **own commit**, so no two main runs ever share a group and none
can be evicted. Pull requests still group by ref and still supersede — that saving is worth keeping.

## The guard that missed it, and why

`MainRunsAreNeverCancelledGuard` was green throughout. It asserted the `cancel-in-progress`
expression evaluates false on main — which was true, and beside the point. The invariant is *"a main
run is never cancelled"*, and eviction violates it without touching that setting. The guard now also
evaluates the **group** expression for two different main commits and requires the results to
differ. It was verified by putting the old grouping back and watching it fail.
