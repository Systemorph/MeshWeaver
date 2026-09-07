---
Name: The Prune Requires a Complete Listing
Category: Architecture
Description: An import prunes on the inference "absent from the source ⇒ deleted from the source". That inference is only sound when both reads behind it — the repository listing and the mesh snapshot — are COMPLETE. What happens when either fails open, which direction each guard closes, and how to tell "the prune found nothing" from "the prune was refused".
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="m9 12 2 2 4-4"/></svg>
---

# The Prune Requires a Complete Listing

A static-repo import ends with a **prune**: every node in the partition that the source no longer
carries is deleted, so the mesh mirrors the repository. The whole prune rests on one inference:

> **absent from the source ⇒ deleted from the source**

That inference is sound only when the source listing is *complete*. It is a **subtraction**, and a
subtraction against a set you only partly read deletes whatever you failed to read.

The same is true of the other operand. The prune subtracts the source's paths from **the partition's
current nodes**, and a node the mesh snapshot fails to return is silently never a prune candidate.
Both operands are reads, and until MeshWeaver#3589 both of them **failed open** — an indeterminate
answer was laundered into a confident one, in opposite and equally silent directions.

## The two reads, and how each of them used to fail

| Read | What it answers | How it failed | Consequence |
|---|---|---|---|
| The **repository tree** (`OctokitGitHubRepoClient.TreeOf`) | "what does the repo carry at this commit" | GitHub answers **HTTP 200 with a partial list**, flagged only by `truncated: true` in the body — which nothing read | Every omitted file read as a deletion. Under `FullReplace` the import mirrors the Space away |
| The **mesh snapshot** (`StaticRepoImporter.Run`) | "what does the partition currently hold" | a bare `MeshQueryRequest.FromQuery(…)` — no completeness declaration — gating a destructive decision | A node the snapshot omits is never a prune candidate; retired files survive forever |

The second one is the shape MeshWeaver#3589 was *filed* about. On `memex.systemorph.com` the `Crm`
partition had synced to the built commit and still held `Crm/Source/Mail`, `Crm/Source/MailTests`
and `Crm/Source/MailView`, which the repository had retired. Because the source-fingerprint gate
(MeshWeaver#2813) hashes the **live** source set, those three orphans made every `Crm` bundle
refused *forever* and every `Crm` page compile from source instead of adopting one.

🚨 **The hypothesis on the issue is falsified, and it is worth saying why.** #3589 guessed the prune
is "additive — only what the source previously owned". It is not, on this route:
`InMemoryStaticRepoSource` does not override `SyncMode`, so it takes the `IStaticRepoSource` default
`PartitionSyncMode.FullReplace`, and `ImportSource` passes no override. Under `FullReplace`
`previouslyOwnedPaths` is inert. "Ownership was never recorded" therefore cannot be why those nodes
survived — which is exactly why a proposed fix is a hypothesis too, and gets measured before it gets
implemented.

## The completeness declaration

`IStaticRepoSource` carries the verdict, with a **default implementation** so no existing source has
to say anything:

```csharp
public interface IStaticRepoSource
{
    /// <summary>Whether EnumerateSourceNodes() is the source's COMPLETE listing.</summary>
    bool ListingIsComplete => true;
}
```

The default is `true` because a source that materializes from an embedded assembly or a full clone
*cannot* return a partial listing. Only a source whose listing is a **remote read** has anything to
declare, and today that is one: `InMemoryStaticRepoSource`, which carries the flag from
`RepoSnapshot.ListingIsComplete`, which carries it from GitHub's `truncated` flag on the
recursive-tree response.

```
GitHub tree (truncated: true)
   → HeadInfo.ListingIsComplete = false
   → RepoSnapshot.ListingIsComplete = false
   → InMemoryStaticRepoSource.ListingIsComplete = false
   → ComputePrunableNodes(…, listingIsComplete: false) → nothing
```

`ComputePrunableNodes` checks it **first**, ahead of even the sync mode:

```csharp
if (!listingIsComplete)
    return Array.Empty<MeshNode>();
if (mode == PartitionSyncMode.UpsertOnly)
    return Array.Empty<MeshNode>();
```

The position is deliberate. Every other guard — governance, `SyncBehavior`, mesh-minted releases,
`IsExcludedFromMirror`, excluded roots — *refines* the inference by narrowing which absences count.
This one **withdraws** the inference: there is no question to refine, because `sourcePaths` is not a
set anything may be subtracted from.

The blobs that *were* returned are still upserted. Only the deletion half is withheld — the read
failure invalidates "everything else was deleted", not "these files exist".

## Which direction each guard closes

Both guards are deliberately asymmetric, and the asymmetry is the point.

**Truncated listing ⇒ prune nothing.** Closing this way leaves genuinely-retired nodes alive for one
more import: visible, recoverable, and gone the next time the listing reads in full. Closing the
other way *deletes live user data* on the strength of a read that never happened — and the deletion
is indistinguishable, in every log, from one the author intended. A stale extra is a nuisance; a
silent delete is data loss.

**The mesh snapshot is declared `Complete()`.** Here fail-open is the *safe* direction for the prune
itself (an omitted node is merely never deleted), which is precisely why it went unnoticed for so
long. The declaration is not a truncation fix so much as a statement of intent: this read is an
enumeration the caller iterates as the complete set, and no future bound may quietly page it. See
[Query Identity](/Doc/Architecture/QueryIdentity) and
[CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) for the wider rule.

This is the same shape the compare endpoint already used, one file away:
`IGitHubRepoClient.GetChangedPaths` returns `null` when the diff cannot be computed reliably —
"diff unknown → the caller MUST full-import", never "nothing changed". The tree read simply never
got the same treatment.

## Saying the refusal out loud

The prune's activity line (`🗑 Pruned …`) is emitted **only** when at least one node was pruned. So
silence from the prune phase has always been unfalsifiable — it cannot distinguish:

1. the prune ran and found nothing,
2. the prune never reached,
3. the prune found the *wrong* set.

A refusal therefore says so, and names *which* read came back indeterminate rather than emitting a
generic "prune skipped" that every case would share:

```
⛔ Pruned nothing in Crm: the SOURCE listing came back INCOMPLETE (the repository tree was
   truncated), so a node missing from it is an unread file, not a deleted one. Everything that
   was read has been imported; nothing was removed.
```

The terminal summary carries it too, because the summary is the one line always written:
`pruned 0 (REFUSED — the source listing was incomplete)` rather than a bare `pruned 0`. `UpsertOnly`
and an empty candidate set stay silent — they are not failures.

## What this does NOT cover

- **The export side.** `Push` reconstructs the commit tree from `HeadInfo.ExistingBlobs`, which is
  the *same* read. A truncated tree there would drop files from the repository rather than from the
  mesh. The flag now travels on `HeadInfo` so the guard can be added; it has not been.
- **A partition whose orphans are already live.** The guards stop the class of defect from being
  created; they do not retire nodes a previous route already wrote. A partition in that state needs
  a forced re-import once its listing reads in full.
- **The satellite-path question.** Whether `path:{p} scope:descendants` returns a partition's
  `Source/*` satellite rows on a Postgres backend is a separate strand, in a different repository's
  query layer. In the in-memory adapter `Source`/`Test` are *not* satellite paths, so a monolith test
  returns them either way — which means an in-memory test can never discriminate that half.

## Related

- [Static Repo Import](/Doc/Architecture/StaticRepoImport) — the pipeline the prune is the last phase of
- [GitHub Sync](/Doc/Architecture/GitHubSync) — the route that fetches, parses and imports a repo
- [Partition Sync Guide](/Doc/Architecture/PartitionSyncGuide) — the `PartitionSyncMode` the prune reads
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a query never decides a single node's content
