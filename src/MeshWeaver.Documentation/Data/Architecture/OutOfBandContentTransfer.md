---
Name: Out-of-Band Content Transfer
Category: Architecture
Description: A content file larger than one delivery's budget travels through the content store, not on the message — the producer stages the bytes into the destination collection's reserved staging folder and the delivery carries a content-addressed handle. Where the bytes land, who owns their lifetime, how the receiver resolves a handle, and why a transfer that fails still says so.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z"/></svg>
---

# Out-of-Band Content Transfer

A GitSync content sync mirrors a Space's git-committed `content/**` binaries into the Space's
content collection. Until now the bytes travelled **inline**, on the `SyncContentFilesRequest`
itself.

[#2885](/Doc/Architecture/OversizedDeliveryRefusal) stopped the producer building one delivery per
Space, so a delivery is now `≤ budget + largest single file`. That closed the *aggregate* axis and
left one residual, which that page records against itself:

> A single file larger than the budget travels whole. […] A file that large belongs behind a
> content-store handle rather than inline.

**This page is that handle.** A file whose packaged cost alone exceeds
`ContentDeliveryBudget.BudgetBytes` no longer rides the message: its bytes go into the destination
collection once, and the delivery carries a content-addressed reference to them.

## Why the residual could not be closed by moving a number

`ContentDeliveryBudget.BudgetBytes` is `DeliveryPayloadBounds.MemoryStreamBlockBytes` = **1,048,576**
— Orleans' memory-stream block size, hard-coded in `MemoryAdapterFactory` with no configuration
surface. It is not a knob, and even if it were, raising it is the move
[Oversized Delivery Refusal](/Doc/Architecture/OversizedDeliveryRefusal) exists to forbid.

The scale is not an edge case. Measured on `Systemorph/MeshWeaver.Education@f7ae723` (2026-09-04,
unchanged from the 2026-09-03 measurement on `61cbbac`):

| Space | files | **over budget** | total | largest packaged |
|---|---:|---:|---:|---:|
| AgenticEngineering | 25 | **12** | 101.2 MB | 13,188,871 |
| AgenticBusiness | 9 | **4** | 27.2 MB | 10,910,243 |
| AgenticPrimerDe | 7 | **3** | 10.9 MB | 4,291,888 |
| AgenticPrimer | 7 | **3** | 9.8 MB | 3,873,652 |
| DataModeling | 3 | **1** | 8.6 MB | 10,929,144 |
| AdvancedBusinessRules | 2 | **1** | 9.5 MB | 12,540,448 |
| AgenticOffice | 3 | **1** | 8.2 MB | 10,224,527 |

**Every Space in the repo has at least one file over budget — 25 in total.** 🚨 The axis is *"has a
video"*, not *"is large"*: `AdvancedBusinessRules` totals 9.5 MB — one of the smallest Spaces there
— and carries the second-largest single file. Sorting Spaces by total size does not identify the
affected set.

## Where the bytes land

**In the destination collection itself, under a reserved staging folder** —
`ContentStaging.Folder` (`_staging/`), at the collection root.

That choice is not arbitrary; it is the only location that needs **no new configuration and no new
assumption about the deployment**:

- The producer (the bulk-import hub) and the receiver (the Space-root node hub) are different hubs,
  and in the Distributed portal they can be different silos. Anything the receiver can read, the
  producer must be able to write.
- The content store is **already** required to be reachable from a hub other than the collection's
  owner: `/api/content/{node}/{collection}/{file}` resolves the owning node's collection *config*
  and then serves the bytes from the web pod. On AKS that is the `memex-content` RWX Azure Files
  share mounted at `/mnt/content` on every replica. A store that is not shared has a broken content
  route already.
- So "stage in the destination collection" adds nothing to what content collections already need.
  A mesh-level staging collection would have added a well-known name, a deployment key, and a
  second thing to provision — for the same physical bytes on the same share.

The producer reaches the destination collection the way `MeshOperations.Upload` and the content
route already do: it asks the owning node's hub for the collection **config** with a
`GetDataRequest(ContentCollectionReference)` — a few hundred bytes — registers it locally under the
qualified name `{nodePath}/{collection}`, and resolves a provider over it. **Only the config
crosses the mesh; the bytes never do.**

```text
producer (import hub)                       receiver (Space-root node hub)
──────────────────────                      ──────────────────────────────
GetDataRequest(collection) ───────────────▶ config   (a few hundred bytes)
       ◀─────────────────────────────────── ContentCollectionConfig
write _staging/{sha256}  ══════▶ content store ◀══════ read _staging/{sha256}
SyncContentFilesRequest{ StagedFiles:[…] } ▶ SaveFile(videos/intro.mp4)
       ◀─────────────────────────────────── ImportContentResponse
delete _staging/{sha256} ══════▶ content store
```

## What the handle is

```csharp
public record StagedContentFile(string Path, string Handle, long Length);
```

- `Path` — the file's path relative to the request's `TargetPath`, exactly as an inline
  `InlineContentFile.Path` is. The receiver writes it to the same place it would have written the
  inline file.
- `Handle` — the lowercase hex **SHA-256 of the bytes**. The staged blob lives at
  `_staging/{Handle}` within the collection.
- `Length` — the raw byte count. The receiver **verifies it** against the staged stream before
  writing, so a truncated or half-written blob is a loud failure rather than a corrupt asset.

`SyncContentFilesRequest` carries them in a new `StagedFiles` list beside `Files`. A sync with no
over-budget file produces a request byte-for-byte identical to what it produced before — `StagedFiles`
is null and nothing else changes.

**Content-addressing is what makes the transfer idempotent.** Two files with identical bytes stage
once. A sync that runs twice writes the same blob at the same key and the same file at the same
destination path — no duplication anywhere. A staged blob that is already present with the right
length is not rewritten, so a retry after a partial run does not re-copy 100 MB over SMB.

## Who owns the lifetime

**The producer owns every blob it stages, from `Post()` to the last delivery's answer.**

- The staged blobs are deleted when the post sequence terminates — success *or* failure — because
  the deliveries are posted with `Concat` and the last answer is proof that nothing still references
  a handle. There is no window in which a live delivery names a deleted blob.
- A **crashed producer** (a pod that dies mid-import) is the only way a blob outlives its sync. That
  is reclaimed by an age sweep: before staging, the producer deletes `_staging/` entries older than
  `ContentStaging.StaleAfter` (24 h). The window is far wider than any sync, so the sweep can never
  race a live transfer, and the folder holds at most one run's assets in the normal case.
- **The mirror never prunes the staging folder and never prunes a staged file.** `_staging/` is
  excluded from the prune enumeration outright — it is framework state, not content — and staged
  paths are part of `MirrorKeepPaths`, the full keep set the one authoritative prune pass is
  measured against. Without that, the prune (which rides the *first* delivery) would delete the
  blobs the following deliveries are about to read.

## How the receiver resolves a handle

`ContentImportExtensions.SyncFiles` writes the inline files exactly as before, then writes the
staged ones:

```csharp
target.GetContent($"{ContentStaging.Folder}/{staged.Handle}")
    .SelectMany(stream => stream is null
        ? Observable.Throw<int>(new InvalidOperationException(
            $"Staged content '{staged.Handle}' for '{staged.Path}' is not in the collection's "
            + "staging area — the out-of-band transfer did not complete."))
        : target.SaveFile(dir, name, () => stream).Select(_ => 1))
```

Every leaf runs on the collection's own `IIoPool`; the hub action block only subscribes and returns.
The bytes are streamed from the staging blob into the destination file — they are never materialised
as a `byte[]` on the receiver, which is the whole point.

## Failure behaviour stays honest

[Content Sync Visibility](/Doc/Architecture/ContentSyncVisibility)'s entire contribution was making a
refused sync **observable**. This change must not trade a loud refusal for a quiet success, so:

- **A staged blob the receiver cannot find or whose length does not match is a failure**, named as
  itself, with the handle and the path in the message. It is never treated as "zero files".
- **When staging is unavailable the sync falls back to inline** — which is exactly today's behaviour,
  including today's refusal where the transport binds — and the reason staging was unavailable is
  appended to the failure. A monolith, where an over-budget file travels perfectly well inline, keeps
  working; a portal that cannot stage says so instead of silently dropping the assets.
- **The `#3101` budget description now describes the files that actually travelled inline.** A file
  that went out of band is not reported as an over-budget inline payload, because it was not one.
  A refusal that had nothing to do with size still reports only its own reason.
- `StaticRepoImporter` is unchanged: it reads `Success`/`Error` off the response and writes the
  Space's `_Activity/content-sync` ledger from it. A transfer that fails still lands `Warning` on
  that ledger with the reason, and the partition fingerprint still does not stamp `Succeeded`.

## What this does not change

- **The budget is not raised.** `ContentDeliveryBudget.BudgetBytes` is untouched; the inline
  partitioning of #2885 is untouched. The only thing that changed is *which* files are inline.
- **A file is still never split.** There is no chunking, no reassembly, and no partial-write story
  on the receiver — the atom is still one file, it simply arrives by a different road.
- **Authorization is unchanged.** The content write still happens on the owning node's hub under
  `[SyncContentFilesPermission]` (Create, plus Delete when mirroring). Staging writes to the
  framework's reserved folder, never to a content path, and no content lands in the collection
  before that check runs.
- **Nothing new is provisioned.** No new collection, no new configuration key, no new volume.

## Rules

- **The bytes go where they are going, once.** A transfer that copies a payload into a holding area
  outside the destination store pays for the same bytes twice and needs a second thing provisioned.
- **A handle is content-addressed or it is not idempotent.** A per-attempt id makes a retry a
  duplicate; a hash makes it a no-op.
- **The producer owns the staged bytes for exactly as long as a delivery can name them** — which is
  until the last answer, and no longer.
- **A staging area is excluded from a mirror by name, not by luck.** The prune enumerates everything
  under the folder it mirrors; framework state living there must be named and skipped.
- **An out-of-band transfer that cannot start must fall back to the loud path, never to a quiet
  one.** "We could not stage, so we reported success with zero files" is the defect
  [Content Sync Visibility](/Doc/Architecture/ContentSyncVisibility) was written about.

## Related

- [Oversized Delivery Refusal](/Doc/Architecture/OversizedDeliveryRefusal) — the transport bounds,
  why the limit is never the thing to raise, and the residual this page closes.
- [Content Sync Visibility](/Doc/Architecture/ContentSyncVisibility) — the ledger a refused sync
  writes, which this change preserves.
- [Static Repo Import](/Doc/Architecture/StaticRepoImport) — the import that posts the sync.
- [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) — where every leaf on this path
  runs.
