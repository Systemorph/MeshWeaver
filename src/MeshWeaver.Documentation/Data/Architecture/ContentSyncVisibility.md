---
Name: Content Sync Visibility
Category: Architecture
Description: A Space whose assets the transport refuses says so — on the Space itself, naming the file, its packaged size and the limit it exceeds. Why "refused" and "has no content" reported identically for months, why the durable signal belongs on the node and not only on the partition's import log, and what still needs an out-of-band content store.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15V6a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h9"/><circle cx="9" cy="10" r="2"/><path d="m5 18 4-4 3 3"/><path d="M18 16v3"/><path d="M18 22h.01"/></svg>
---

# Content Sync Visibility

A GitSync import carries a Space's `content/**` binaries **inline**, as bytes on a message. Every
transport underneath the mesh bounds how large one message may be, so some of those deliveries are
refused — correctly, by the guards described in
[Oversized Delivery Refusal](../OversizedDeliveryRefusal).

**A refusal that is correct at the transport is still a delivery failure at the content layer.** For
months it was reported as nothing at all.

## The defect: "refused" and "has no content" were the same answer

Both content passes in `StaticRepoImporter` folded an unsuccessful sync into `0` files:

```csharp
.Select(r => r.Success ? r.FilesImported : 0)   // no log at all on the false arm
```

Zero files is exactly what a Space with no content reports. So the import summed zero, returned
`"Imported"`, and stamped **Succeeded** at that fingerprint — and Succeeded at a fingerprint *is* the
durable short-circuit, so every later import of the same repo content skipped the Space **without
reading it**. A Space whose assets were refused on every attempt was indistinguishable from one
fully in sync, permanently. The person who found out was a learner opening a course page with a
missing video.

That is the same shape as a claim that blocks a create (#2211), one layer down: *the source declares
it, something refuses it, and no boot can change that.*

## The three things a refusal now says

| Question | Where it is answered |
|---|---|
| **Did this pass leave the Space in the state the marker claims?** | `StaticRepoImportResult.Outcome` = `ImportedWithRefusedContent`, which becomes a **Warning** marker — so the next boot re-attempts instead of short-circuiting on green. |
| **Why?** | `StaticRepoImportResult.RefusedContent` — one `RefusedContentSync(NodePath, Reason)` per owning node, carrying the transport's own verdict plus the producer's measurement. Named in the import activity's summary too. |
| **How does the author of the content find out?** | An `_Activity/content-sync` ledger **on the node itself**. |

### Why the reason had to be carried, not guessed

The response already knew. `ImportContentResponse.Error` and the `DeliveryFailureException` message
were discarded at both call sites, leaving the activity to write prose in their place — *"most often
a delivery over the transport's size budget"*. Three very different problems reach that arm:

- an asset tree the transport provably cannot carry (raise nothing — the file needs a content store);
- a content collection that is not configured on the node (a wiring bug);
- a path that tried to escape the collection root (a bug or an attack).

Only the first is about size, and a report that guesses at the cause sends every reader after the
wrong one.

### Why the producer names the size

`SyncContentFilesBuilder` already measures every file in order to split the write across deliveries
(#2885). It threw the numbers away. `ContentDeliveryBudget` is now the one place that answers *what
a file weighs* and *against which limit*, so the partitioner and the failure report can never
describe different deliveries:

```csharp
ContentDeliveryBudget.BudgetBytes                  // 1,048,576 — Orleans' memory-stream block
ContentDeliveryBudget.PackagedCost(file)           // 4 × ⌈len/3⌉ + path.Length — never touches the bytes
ContentDeliveryBudget.DescribeOverBudget(files)    // null when every file fits
```

When a sync fails **and** a file is individually over budget, `Post()` folds that sentence into the
reason:

```text
Refused to dispatch delivery '…' to grain 'messagehub/AgenticEngineering': its payload is
149,199,409 bytes, at or over the 104,857,600-byte Orleans MaxMessageBodySize … —
12 of 25 file(s) exceed the 1,048,576-byte per-delivery content budget ON THEIR OWN, and a file is
never split — so the delivery carrying one is over the budget however the set is partitioned.
Largest: 'content/videos/module1-intro.mp4' at 13,188,820 packaged bytes (12.6× the budget).
```

🚨 **`ContentDeliveryBudget` measures; it never refuses.** The budget is the Orleans memory-stream
block size, which binds only where that transport is in the path — a monolith carries an over-budget
file perfectly well. A producer-side rejection would stop content that works today from syncing,
which is the opposite of the defect: the bug is a refusal nobody can see, not a delivery nobody
refused.

## The ledger: one node, updated in place

Each owning node whose content this pass tried to sync gets an Activity satellite at
`{nodePath}/_Activity/content-sync`:

| State | Status | Message |
|---|---|---|
| Refused | `Warning` | 📦 CONTENT SYNC REFUSED — this node's assets are NOT in the mesh. *(reason, with sizes and limits named)* |
| Delivered | `Succeeded` | ✔ Content assets in sync |

Three deliberate choices:

- **The id is deterministic** — the opposite of the import *attempt* node, which gets a fresh id per
  run. An attempt is history; a ledger is a **state** ("are this node's assets in the mesh right
  now?"), and a state that accumulates one row per boot is a state nobody reads.
- **Success is written too.** A warning that cannot clear is worse than none: once the transport
  problem is fixed the Space would keep claiming staleness for ever and readers would learn to
  ignore it. Writing the green state is what makes the red one worth believing.
- **Only within the importing source's own partition.** A sync aimed at another partition is a
  mis-declared source, and a ledger write there would provision a schema for a partition nobody
  asked for as a side effect of reporting an error. Those refusals still reach the outcome, the
  summary and the log; only the satellite is withheld.

The whole ledger path is best-effort: observability must never break the import it observes.

## What this does NOT fix

**A single file over the budget still travels whole.** #3097 partitions by *aggregate*; a file is
the atom the receiving handler writes and is never split, so the guarantee is
`delivery ≤ budget + largest single file`. Measured 2026-09-03 against
`Systemorph/MeshWeaver.Education@61cbbac`, every Space in that repo has at least one file over the
1 MiB budget — 25 files in total, the largest packaging to 12.6 MB:

| Space | files | over budget | largest packaged |
|---|---:|---:|---:|
| AgenticEngineering | 25 | 12 | 12.6 MB |
| AgenticBusiness | 9 | 4 | 10.4 MB |
| AgenticPrimerDe | 7 | 3 | 4.1 MB |
| AgenticPrimer | 7 | 3 | 3.7 MB |
| AdvancedBusinessRules | 2 | 1 | 12.0 MB |
| DataModeling | 3 | 1 | 10.4 MB |
| AgenticOffice | 3 | 1 | 9.8 MB |

🚨 **The axis is "has a video", not "is large".** `AdvancedBusinessRules` totals 9.5 MB — one of the
smallest Spaces there — and carries the second-largest single file. Sorting Spaces by total size
does not identify the affected set.

The durable cure is **out-of-band asset transfer**: a file that size belongs behind a content-store
handle (upload the bytes once, ship a reference), not inline on a message. That is a design change
with its own delivery, and it is tracked separately. What this page describes is the change that
makes the gap **observable in the meantime** — the difference between a learner reporting a missing
video and the sync reporting its own state.

## Related

- [Oversized Delivery Refusal](../OversizedDeliveryRefusal) — the producer-side bounds, why neither
  is ours to raise, and why an oversized grain frame destroys a shared connection.
- [Static Repo Import](../StaticRepoImport) — the import pipeline these passes belong to.
- [Activity Control Plane](../ActivityControlPlane) — how an Activity node is read and rendered.
