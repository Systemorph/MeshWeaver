---
Name: A Space that never finishes syncing re-downloads its repository every time
Category: Feature
Description: A green build of a repository imports into every Space that syncs it, and a Space already sitting on the built commit is skipped for free. But the field that earns that skip is deliberately held back whenever an import does not fully reconcile — so a Space that never reconciles is the one that re-clones the repository on every build, at the repository's CI cadence rather than its own. The cost model, the two-line check for a stuck source, and the measurements are now written down.
Icon: DocumentBulletList
Order: -20260910
---

A Space connected to GitHub stays current without polling: every green build of its repository is a
webhook delivery, and the delivery imports. A Space that already sits on the built commit is skipped
at no cost, which is what makes the arrangement affordable — most deliveries do nothing at all.

That skip is earned by a single field on the sync source, `lastSyncCommitSha`. And the same field is
deliberately **held back** whenever an import did not fully reconcile: when two-way protection kept
server-newer nodes, when a node did not land, or when the import failed outright. Each of those
holds exists because advancing past it once lost content. Together they mean something nobody had
written down:

> A sync source that never reconciles pays a **full download and parse of its whole repository on
> every delivery**, for as long as it does not reconcile — and the rate is set by that
> repository's CI schedule, not by anything about the Space.

On `Systemorph/MeshWeaver` that schedule is 254 green publish signals in 24 hours (measured
2026-09-10), 87 of them from a probe that runs every fifteen minutes and builds nothing. For the
Spaces that reconcile, all 254 cost nothing. For one that does not, all 254 are a clone.

## How to tell whether a source is stuck

Open the Space's **GitHub Sync** settings, or `get @{Space}/_GitSync`, and compare two of the three
clocks:

- `lastSyncedAt` ≈ `lastSyncAttemptAt` → the source is reconciling. Nothing to do.
- `lastSyncedAt` **days behind** `lastSyncAttemptAt`, with outcome `Imported` → it runs, it reports
  that it imported, and it still has not reconciled. That is the stuck shape.

A frozen `lastSyncedAt` beside outcome `Skipped` is *not* this — that is the sanctioned no-op, and a
`lastSyncCommitSha` a few hours behind the branch tip is normal too, because a delivery imports at
the commit the build proved rather than at the tip.

Two live examples measured on 2026-09-10: `Essentials` last reconciled 2026-08-10 while attempting
that same afternoon, 436 commits behind; `Deployments` on the staff portal last reconciled
2026-08-19 while attempting the same afternoon. Both were reported as `Imported` every time.

## What is now written down

[What a Green Build Costs a Synced Space](/Doc/Architecture/GitSyncTriggerCost) carries the whole
model: which workflow runs count as a publish signal, the four reasons a delivery is skipped, what a
non-skipped delivery actually transfers (a shallow clone of the **entire** repository — a configured
subdirectory filters what is parsed, never what is downloaded), the three ways the baseline freezes
and why each one is correct on its own, what the content-addressed import marker already narrows,
and the check above.
