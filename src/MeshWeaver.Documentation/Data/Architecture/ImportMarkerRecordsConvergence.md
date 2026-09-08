---
Name: The Import Marker Records Convergence
Category: Architecture
Description: A static-repo import short-circuits on a content-addressed marker that the next run reads INSTEAD of the partition. So the marker is a claim about the partition, not about the source — and a run that kept nodes back, or could not prune at all, must not make it. What went wrong when it did, why the ownership test alone could not repair it, and why an absent verdict reads as UNKNOWN.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 12l2 2 4-4"/><path d="M21 12c0 4.97-4.03 9-9 9s-9-4.03-9-9 4.03-9 9-9"/><path d="M16 3h5v5"/><path d="M21 3l-7 7"/></svg>
---

# The Import Marker Records Convergence

A [static-repo import](/Doc/Architecture/StaticRepoImport) is idempotent through a **content-addressed
marker**. The importer hashes every node the source ships into a fingerprint, and writes an activity
at `{Partition}/_Activity/import-{fingerprint}`. A `Succeeded` one short-circuits the next run:

```
marker at import-{fingerprint} is Succeeded
   ⇒ "Skipped", 0 nodes, WITHOUT READING THE PARTITION
```

That is a large saving and it is the right shape. But note what the id is derived from and what the
skip concludes from it:

> The id is a hash of the **source**. What the next run reads out of it is *"the partition holds
> exactly content F"* — and it reads that **instead of** the partition.

**So the marker is a claim about the PARTITION.** Only a run that made the claim true may write it.

## What happened when it didn't

Measured end to end on `memex.systemorph.com`, 2026-09-07/08.

`MeshWeaver.Crm` deleted four files on 09-06. The 09-07 10:15Z import ran, computed the prune set
correctly, and found every one of them — and then kept them all:

```
↩ Kept Crm/Mail (added on the server — commit to sync it back).
↩ Kept Crm/Migration (added on the server — commit to sync it back).
↩ Kept Crm/Source/Mail (added on the server — commit to sync it back).
↩ Kept Crm/Source/MailTests (added on the server — commit to sync it back).
↩ Kept Crm/Source/MailView (added on the server — commit to sync it back).
Imported 23 node(s), kept 5 local change(s), pruned 0, synced 0 content file(s).
```

Two distinct defects produced that state, and only the first is about the prune.

### 1. Ownership — fixed by the predicate, and it was not enough

`ImportConflictPolicy.PreservesFromPruneOf` protects a node **changed on the server since the last
sync**. The horizon (`LastSyncedAt`) is deliberately *held* while anything is preserved (#675/#677),
so it falls further and further behind — and a previous import's OWN writes then sit after it and
read as "newer on the server" for ever. Judged by timestamp alone, the importer was preserving its
own output from itself. `ImportConflictPolicy.IsHumanEdit` is the ownership test:

```csharp
public static bool IsHumanEdit(MeshNode? target) =>
    target?.LastModifiedBy is { Length: > 0 } author
    && !string.Equals(author, WellKnownUsers.System, StringComparison.Ordinal);
```

The measurement bears the predicate out exactly. Of the six retired nodes found across the portal's
fourteen synced partitions, **not one carried a real user id**: three (`Crm/Source/Mail`,
`MailTests`, `MailView`) had *no author field at all* — version 1, never touched by a person — and
the rest read `system-security`. Every one is the import's to retire; none is authored content.

🚨 **But fixing the rule cannot retire what is already there**, and that is the whole reason this
page exists. The 10:15Z run stamped `import-0ff4e895224bfde0` **Succeeded**. Every later trigger
reads that marker and answers `Skipped` before the prune phase — where `IsHumanEdit` lives — is ever
reached, and the webhook does not even get that far: `GitHubWebhookProcessor.SkipReason` answers
*"already at this commit"* because the `Skipped` outcome had advanced the pointer.

**One route does reach the importer, and its coverage is exactly the measure of the gap.**
`SealedPublicationSyncReconciler` (core#3727) re-imports with the content-skip bypassed
(`ImportConflictPolicy.Reconcile`) — but only when all three of: the repository has a publication
**sealed for this instance's framework identity**, the config sits **at** that sealed commit, and at
least one NodeType under the Space was **declined on its source fingerprint** in that sweep. That is
the *bundle-refusal* case, which `Crm` is; it is a symptom-driven trigger, not the mechanism.

**`Edu/Course` is the case it does not reach, and it has had 53 days of boots to prove it.** Deleted
upstream on 2026-07-17, its partition reads `lastSyncOutcome: Skipped`, nothing about it declines a
bundle, and it is still Active — recompiled every boot at `compilationStatus: Error` with
`Matched Code nodes (0)`. No seal fires for it, so nothing reaches the prune, so the ownership test
never runs. **A partition converges only when something makes the importer read it, and until now
the only things that did were a new repository commit or a declined bundle.**

### 2. The marker — the reason the residue is permanent

The 23:11Z sync did exactly that, and the `Skipped` outcome then propagated one level further:

| field | value at 23:11Z | meaning |
|---|---|---|
| `lastSyncOutcome` | `Skipped` | the marker matched; the partition was never read |
| `lastSyncCommitSha` | `76cdb553b` (branch head) | **advanced** |
| `lastSyncedAt` | `2026-08-30T17:06Z` | frozen 8 days back |

`Skipped` reports `Preserved = 0` — **a zero nothing measured** — and
`GitHubSyncService.MayAdvanceBaseline` reads `Preserved == 0` as "the mesh is in sync with the repo
at this commit" and advances the seen commit. `GitHubWebhookProcessor.SkipReason` then answers
*"already at this commit"* for every later green build. The partition is now declared converged to a
commit whose tree it demonstrably does not match, and nothing will look again until the repository
produces new content.

That is why `Edu/Course` had been deleted upstream for **53 days** and was still Active — still being
recompiled every boot, parked at `compilationStatus: Error` with `Matched Code nodes (0)`, and
therefore still polluting the pre-production NodeType sweep.

The two facts are the same fact recorded twice and disagreeing: the run that **refused to advance
the baseline** (`Preserved = 5`) still wrote **the marker that let the next run advance it**. The
marker wins, because it is read first and it is read instead of the partition.

## The rule

> **A run that did not converge does not licence the next skip.** The marker records what the run
> LEFT UNDONE, and the skip requires it to be nothing.

`StaticRepoImportResult.Converged` is the one predicate — four ways a run leaves the partition
unequal to the content its marker names:

```csharp
public bool Converged =>
    Preserved == 0            // kept a live node back: from an overwrite, or from the prune
    && Failed == 0            // some node did not land (#2229 item C)
    && !PruneRefused          // the prune could not run at all (#3614)
    && !string.Equals(Outcome, "Failed", StringComparison.OrdinalIgnoreCase);
```

`ImportMarkerVerdict` carries `preserved` and `pruneRefused` beside the outcome it already recorded,
and `MarkerConverged` is what the skip arm consults before it trusts the claim.

🚨 **`PruneRefused` is the clause a count cannot see.** After a
[refused prune](/Doc/Architecture/PruneRequiresACompleteListing) both `preserved` and `pruned` are
zero, and *"looked and found nothing"* is indistinguishable from *"could not look"* — only the second
means the retired files are still there.

## An absent verdict is UNKNOWN, never zero

Every marker written before 2026-09-08 carries an outcome and no `preserved` field. Reading that
absence as zero would be the same fail-open the fix is about — and it would also leave every
partition **already** in this state stranded, because the residue can only be retired by a run that
reaches the prune.

So `MarkerConverged` answers `false` for an absent verdict, and that branch is what repairs the
fleet: each affected partition pays **one** re-import, which at an unchanged fingerprint matches
every entry of the per-node manifest, writes nothing, recompiles nothing, and then stamps a real
verdict. After that the skip is exactly as cheap as it was.

This is deliberately not "stop trusting the marker". A change that re-imported on every trigger would
trade this defect for #3146 — 19 complete passes in three hours on `memex-cloud`, ≈425 identical
failures and a NodeType compile each — which is the more expensive mistake. A **converged** run's
marker is still a licence to skip, and `AConvergedImport_StillShortCircuits` is the falsifier that
holds the fix to it.

## The opposite danger

🚨 **Propagating a deletion too eagerly destroys authored content, and that is worse than the bug.**
The protection this fix narrows is real: a node a person created on the portal between syncs is a
local addition to be committed back, not a stale extra to mirror away (#604). Nothing here weakens
it — `IsHumanEdit` is the *only* thing that separates the two, the two integration tests carry an
authored node through every pass, and the assertion that matters is that the re-import the fix newly
causes is not the pass that finally deletes it.

The corresponding fail-open would be a node whose author the mesh does not know: an unauthored node
absent from the source reads as the import's. That is the right reading for this fleet — every
retired node measured had no author precisely *because* an import wrote it — but it is a statement
about who writes nodes, not a law of nature, so it is stated here rather than left implicit.

## What this does not cover

- **The overwrite direction on a skip.** A `Skipped` still does not verify that each node the source
  ships matches the repo byte for byte; that is what the fingerprint legitimately claims, and the
  content sentinel plus the per-node manifest cover the rest.
- **`MayAdvanceBaseline` is deliberately unchanged.** It answers a different question — where the
  next git diff starts — and #3614 reasoned the `PruneRefused` case there on its own terms. It still
  reads `Preserved == 0`; what changed is that a `Skipped` now only reaches it after a converged run
  said so.
- **Retiring a NodeType is still not the same as retiring its sources.** See
  [Retiring a NodeType](/Doc/Architecture/RetiringANodeType) for the instances a pruned definition
  strands.

## Related

- [The Prune Requires a Complete Listing](/Doc/Architecture/PruneRequiresACompleteListing) — the two reads the prune's inference rests on
- [Static Repo Import](/Doc/Architecture/StaticRepoImport) — the pipeline, and the marker's place in it
- [Retiring a NodeType](/Doc/Architecture/RetiringANodeType) — what a retirement has to remove beyond the source files
- [GitHub Sync](/Doc/Architecture/GitHubSync) — the route that fetches, imports and records the baseline
- [Sealed Publication Reads](/Doc/Architecture/SealedPublicationReads) — the reconciling import, which bypasses the same short-circuit on measured evidence
