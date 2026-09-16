---
Name: A Content Verdict Is Per Node
Category: Architecture
Description: A single unstorable byte in one source file took every instance action on the control instance offline for an evening. The import refused that one row, earned a partition-wide "these bytes cannot import" verdict on the evidence of it, and every later import answered Skipped with a sentence claiming the content had been recorded. Why the rule was right and its granularity was not, where the refusal is remembered now, and what an import must name when it loses a node.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 9v4"/><path d="M12 17h.01"/><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/></svg>
---

# A Content Verdict Is Per Node

A [static-repo import](/Doc/Architecture/StaticRepoImport) writes each source node through the
canonical upsert, and a node whose write the owner refuses is **isolated**: the failure is logged
against that file, every other node still lands, and the pass reports how many did not.

Some of those refusals are about the **moment** — a store briefly unreachable, an owner that did not
answer — and some are about the **bytes**: a validator rule, an invalid path, a NodeType the mesh
does not know, an RLS denial. The second kind is deterministic. Re-reading the same bytes at the
same fingerprint re-derives the identical refusal, so re-issuing that write can accomplish nothing,
and doing it anyway cost `memex-cloud` **19 full import passes in three hours** — roughly 425
identical failing upserts plus a NodeType compile each, on a portal already at 8/8 replicas. That is
issue #3146, and its rule is right.

**What was wrong is where the rule was recorded.**

## The shape of the defect

The refusal was recorded on the **content-addressed marker** — the activity at
`{Partition}/_Activity/import-{fingerprint}` whose whole purpose is to say something about the
*partition*. A pass whose every failure was a content verdict stamped that marker `Failed` with the
outcome `ImportedWithContentErrors`, and the next import read it and answered `Skipped` for the
whole partition without reading anything.

A content verdict, though, is earned by **one node**. Forty files can land and one be refused, and
the pass still earns that word — it means *"every failure was deterministic"*, not *"everything
failed"*. Recording a per-node fact as a per-partition verdict is the entire defect:

> The marker said *"an earlier FULL import already recorded this exact content"* about content the
> import demonstrably lost.

## What it cost

**Measured on `memex.systemorph.com`, 2026-09-15.**

One file — `Hosting/Deployment/Source/SelfUpdateRouting.cs` — contained a **literal NUL byte
(0x00)** inside a char literal. PostgreSQL cannot store a NUL in a text column, so the write of that
one row was refused while the rest of the `Hosting` tree imported normally. The pass reported:

```
Re-imported ImportedWithErrors (41 node(s)) at 85515f62.
```

— a count, no paths, at `Information`, on an activity whose terminal status was `Succeeded`. Then:

- **Five NodeTypes parked**, each on the symbol whose file had not landed: `Hosting/Backup`,
  `Hosting/Deployment`, `Hosting/InstanceAction`, `Hosting/InstanceRequest`,
  `Hosting/PlatformBuildInbox`, with `CS0246: The type or namespace name 'SelfUpdateRouting' could
  not be found`.
- **`Hosting/InstanceAction` is the control instance's entire action surface**, so while it was
  parked *no instance action could run at all* — no `Sample`, no `Logs`, no `HelmRelease`, no
  `Roll`. Operations on the fleet were offline.
- The readiness gate on the new replica correctly refused (`5 NodeType(s) regressed on this image`),
  so the rollout could not complete either.
- The operator's one repair — re-import — was **refused**:

```
Imported Skipped — an earlier FULL import already recorded this exact content at
fingerprint bb9801101e859a21 (Hosting/_Activity/import-bb9801101e859a21), so the
partition was not re-read.
```

The visible symptom was a compile error on a symbol whose source file is plainly in git, with
nothing anywhere naming the node that had not landed. The issues are
[#4459](https://github.com/Systemorph/MeshWeaver/issues/4459) and
[#4456](https://github.com/Systemorph/MeshWeaver/issues/4456); #4456 reads the same evidence as
*"applies modifications but not additions"*, which is what one dropped file looks like from outside
when nothing names it.

## The fix: the memory moves to the node

The per-node **import manifest** (`{Partition}/_Activity/import-manifest`, a `{path → token}` map)
already exists and is already the thing that decides, per node, whether a write is worth issuing.
Two changes make it carry the refusal too.

**1. A run may only claim what it WROTE.** The manifest used to record a node's source token even
when its write had been refused — the ledger asserting the partition held content that is provably
not in it. It is the same false claim the marker made, one layer down, and it had to become honest
first. Now:

| what happened to the node | what the manifest records |
|---|---|
| written | its source token |
| refused, deterministically | `!` + its source token — *evaluated, and these bytes were refused* |
| failed, retryably | nothing at all — which is what makes the next pass look again |

The `!` prefix is a prefix rather than a schema change on purpose: a reader that does not know it
compares `!abc` against the token it computes (`abc`), sees a mismatch, and re-evaluates the node —
the safe direction, and exactly the behaviour before the field existed.

**2. The skip moves with it.** The import loop skips a node whose manifest entry is a refusal *of
this exact token* — reported, with no write attempted — and `ImportedWithContentErrors` stops being
a fingerprint-recording outcome. The marker keeps it as a **record** of what the pass found; it is
no longer a licence to answer `Skipped` for the partition without reading it.

Both properties then hold at once:

- the write that provably fails is **not re-issued** — #3146's storm stays prevented, and its test
  now pins the quantity that storm was actually made of (`WriteRequests == 0`) rather than the word
  `Skipped`;
- every **other** node is still evaluated, so a later import can create, repair and prune — and a
  partition that has drifted from its marker (a cross-partition prune, a manual delete, a botched
  migration) can heal, which is the repair the whole-partition skip refused.

A `Force` or `Reconcile` import bypasses the refusal memory, exactly as it bypasses the marker:
re-applying regardless is what those are for.

### What it costs

A partition holding a refused node now re-evaluates on every trigger instead of short-circuiting.
That pass issues **no node writes and no recompiles** — every landed node is manifest-skipped, the
refused one is refusal-skipped — so what it costs is one subtree read, the prune evaluation and two
bookkeeping writes. That is the same cost the marker already accepts whenever a verdict is absent or
records a non-converged run; see
[The Import Marker Records Convergence](/Doc/Architecture/ImportMarkerRecordsConvergence).

## An import that loses a node has to NAME it

The second half of both issues is reporting, and it is the half that decides how long an outage
lasts. Every other thing an import can leave behind already named its paths — written, pruned,
blocked creates, held NodeTypes, refused content. **A failure was the one category reported as a
bare number.**

- `StaticRepoImportResult.FailedPaths` carries each failed node with the write path's own reason and
  whether it was deterministic — the same argument `RefusedContent` won for content files in #3101:
  *"refused"* alone cannot separate a byte the store will not take from a validator rule from an RLS
  denial, and those have three different fixes.
- The import's terminal summary names them, bounded.
- The GitSync activity logs them as their own line, **and the outcome line's level now follows the
  outcome**: `Failed` is an error, any `ImportedWith…` a warning. Every line here used to be written
  at `Information` whatever the import did, and `RunActivity` derives the activity's severity and
  terminal status from those levels — which is why an import that dropped a source node showed up as
  `maxSeverity: Information`, `status: Succeeded`.

## What this does not cover

🚨 **An operator who starts from the COMPILE ERROR still has nothing pointing at the import.** Both
issues list this as their third item, and it is deliberately not fixed here — it is
[#4469](https://github.com/Systemorph/MeshWeaver/issues/4469).

A partition left incomplete still hands the compiler source files that reference a symbol whose own
file did not land, so the NodeType fails on `CS0246` / `CS0103` for a symbol that is plainly in git,
and nothing in that message connects it to the import. What changed is the other direction: an
operator reading the **sync activity** now learns which file is missing and why, and the activity is
no longer green. The facts a fix needs are all recorded — the manifest's `!<token>` entries say which
declared nodes the partition is missing — so #4469 is a wiring problem, not a measurement one.

Note the granularity trap in it: refusing to compile a whole partition because one unrelated node was
refused would re-create exactly the over-broad reading this page is about, so any fix has to be
scoped to the types that actually reference the missing node.

## What this does not change

- **`MayAdvanceBaseline` is untouched.** It already requires `Failed == 0`, so a partial import never
  advanced the git baseline; that guard was working and is not what broke. A remembered refusal is
  still reported as a failure, so the baseline is still held — the mesh genuinely is not at that
  commit. `VerdictIsFinal` is also unchanged, so the delivery stays cheap
  ([What a Green Build Costs a Synced Space](/Doc/Architecture/GitSyncTriggerCost)).
- **A refusal is still a refusal.** If the bytes genuinely cannot be stored, no import creates the
  node — the repair is to fix the source file, which moves the fingerprint and the token. What
  changes is that the partition is no longer frozen around it, and that the file is *named*.
- **The green short-circuit is unchanged.** A clean import still stamps a `Succeeded` marker and the
  next run still skips on it.

## Related

- [The Import Marker Records Convergence](/Doc/Architecture/ImportMarkerRecordsConvergence) — what the content-addressed marker may claim, and why an absent verdict reads as UNKNOWN
- [Static Repo Import](/Doc/Architecture/StaticRepoImport) — the pipeline the marker and the manifest sit in
- [The Prune Requires a Complete Listing](/Doc/Architecture/PruneRequiresACompleteListing) — the other way a pass leaves the partition unequal to the source
- [What a Green Build Costs a Synced Space](/Doc/Architecture/GitSyncTriggerCost) — the trigger rate this cost is paid at
- [Syncing a Space with GitHub](/Doc/Architecture/GitHubSync) — the route that fetches, imports and reports
