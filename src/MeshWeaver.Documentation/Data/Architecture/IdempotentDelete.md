---
NodeType: Markdown
Name: "Deleting What Is Already Gone"
Abstract: "The delete verb's postcondition is 'no node exists at that path', so an already-absent node satisfies it and the delete succeeds — and says it removed nothing. Why the race cannot be closed by checking first, why the answer is a discriminator rather than a swallow, and which absences are still failures."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#2e7d32'/><path d='M7 8h10M9.5 8V6.5h5V8M8.5 8l.7 9.5h5.6L15.5 8' stroke='white' stroke-width='1.6' fill='none' stroke-linecap='round' stroke-linejoin='round'/><path d='M16.5 16.5l2 2 3.5-4' stroke='white' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Nodes"
  - "Delete"
---

# Deleting What Is Already Gone

`DeleteNodeRequest(path)` promises one thing: **when it answers success, nothing exists at
`path`.** A node that was already gone when the request arrived satisfies that promise without the
handler doing anything at all. So the delete **succeeds**, and the only question left is what it
tells the caller.

Until [#4668](https://github.com/Systemorph/MeshWeaver/issues/4668) it told the caller
`InvalidOperationException: Node not found: <path>`. That sentence is a contradiction — the user
asked for the node to be gone, and was told that it could not be got rid of *because it was already
gone*.

## The occurrence

memex, 2026-09-17T16:54:55Z:

```text
fail: MeshWeaver.Blazor.Components.CollaborativeMarkdownView[0]
      Deleting comment on CollaborationNotus/PrereadToNotus20260918/_Comment/b1bbf6a2 …
      System.InvalidOperationException: Node not found:
          CollaborationNotus/PrereadToNotus20260918/_Comment/b1bbf6a2
```

Somebody clicked Delete on a comment. The comment satellite was already gone — deleted by the
other person on the document, or by the same person's first click.

The view is not at fault, and it is worth being precise about why. Its comment list is a live
`IMeshService.Query` subscription: a `Removed` frame rebuilds the marker map, so the view
*does* reconcile — a beat later. The click landed inside that beat. Nothing the view can do
shortens it to zero.

## Why checking first is not the fix

A client-side existence check has a **stale negative**, and the delete is exactly where that
matters:

- a query's answer can be minutes old, so "it's still there" is not evidence that it will be there
  when the delete lands;
- a point read of an *absent* node is itself a framework defect — the owner answers a routing
  NotFound that terminates the stream and arms the storm-breaker for that path (see
  [CQRS — Queries, Reads, Writes, Operations](/Doc/Architecture/CqrsAndContentAccess));
- and the window between the check and the write is the race, not a smaller version of it.

This is the same reasoning that produced `CreateOrUpdateNodeRequest` on the create side: the
platform's answer to "it may not exist" is to make **the operation** tolerant on the owning hub,
which serialises it — never to make every caller replay an existence dance that cannot be made
race-free.

## What the handler does now

`HandleDeleteNodeRequest`'s stage 1 reads the root straight from storage. When that read comes back
empty:

| | before | now |
|---|---|---|
| response | `Fail(NodeNotFound)` | `DeleteNodeResponse.NothingToDelete()` — `Success`, `AlreadyAbsent = true` |
| activity log | `Failed`, `AffectedPaths = [path]` | `Succeeded`, `AffectedPaths` **empty**, one keyed `Information` message |
| pod log | `LogDebug` "not-found" | `LogInformation` "already-absent … nothing to delete" |
| `IMeshService.DeleteNode` | `InvalidOperationException` | emits `false` |

The `bool` on `IMeshService.DeleteNode` was a **dead channel** before this — the method emitted
`true` or threw, so no caller had ever seen `false`. It now carries the distinction:

```csharp
meshService.DeleteNode(path).Subscribe(
    removed => { /* true: this call removed it. false: it was already gone. */ },
    ex      => { /* a denial, a validator refusal, a timeout — a REAL failure */ });
```

A caller that only wants the node gone ignores the value. That is the idempotent read, and it is
what the comment-delete handler wanted all along.

## 🚨 Idempotent is not the same as silent

The temptation here is a `catch` around the delete, and it is forbidden for the usual reason: it
would make this symptom disappear *and* hide every other reason a delete can fail. The distinction
that makes this a correctness decision rather than a band-aid is that **the two outcomes stay
distinguishable**:

- a prune that expected to remove something and removed nothing is still visible — `false`, empty
  `AffectedPaths`, its own log line and its own catalog key (`activity.delete.alreadyAbsent`);
- a mistyped path does not read as work done;
- and nothing else was widened. A permission denial, a validator refusal, a warnings-require-confirmation
  hold, a stage timeout, a cancellation and a drain that could not finish all fail exactly as
  loudly as before.

`DeletingAnAlreadyDeletedNodeIsNotAnErrorTest` carries that as a negative control: a validator that
refuses one node, asserting the delete still errors with the validator's own sentence. Without it,
every other assertion in the file would also pass on a `DeleteNode` that had merely stopped being
able to fail.

## The absence that is still a failure

`NodeDeletionRejectionReason.NodeNotFound` did not go away.

`ValidateDeleteRequest` — the pre-flight *query*, "would this delete be allowed?" — still answers
`NodeNotFound` for an absent node. It is answering a question about the node, not carrying out a
delete, and "there is no such node" is the correct answer to that question. What a recursive delete
then *does* with that answer is the subject of the next section.

And a `NodeNotFound` still reaches a caller whenever the absence is not the plain one: a node the
operation cannot see and the store still holds. That is a different fact — the subtree is not in the
state the caller asked for — and the caller is told.

## The same defect one level down — #4680

A **recursive** delete plans its subtree by enumerating storage ONCE (stage 3), asks every planned
descendant in a bulk-atomic pre-flight whether it may be deleted (stage 3b), and only then commits
bottom-up (stage 4). **Both stages address that one snapshot**, so a concurrent delete that removes
one of the planned leaves — at any moment after the enumeration is taken — left the operation
holding a path that really was gone. The whole subtree delete was then refused over a node that was
already in exactly the state the caller asked for: this page's own decision, one level down.

### Why the obvious fix does not apply

Relaxing the pre-flight's `NodeNotFound` verdict is **dead code for the dominant shape**. A repro —
a storage adapter that removes one planned descendant after the enumeration is taken and before it
is answered — showed the leg never reaches a `ValidateDeleteResponse` at all: with no row at the
address and no activated per-node hub to short-circuit on, the post does not ROUTE, and the leg's
fall-through reports

```text
Cannot delete 'X/gone': No node found at 'X/gone'. Closest ancestor is 'X' (remainder='gone').
This usually means the node is missing, has no NodeType, or has an invalid NodeType.
```

The verdict half is not dead in general — routing short-circuits on an address whose hub is still
activated, so such a descendant *is* delivered, reads null and answers `NodeNotFound` — but it is
one of two vocabularies for the same fact, and **both of them are ambiguous**. The routing sentence
names three different situations in one breath, and the last two ("has no NodeType", "has an invalid
NodeType") are precisely what the bulk-atomic pre-flight exists to refuse *before any storage side
effect fires* — the partially destroyed subtree of #1198 / #1446. A verdict, likewise, says what the
handler could read, not that the row is gone.

### The discriminator is the store of record

`MeshExtensions.ConfirmDescendantGone` asks `IStorageAdapter.Exists(path)` and emits **confirmed
gone**, never "exists":

- **confirmed gone** — the descendant blocks nothing. The pre-flight passes it, and the commit leg
  completes having removed nothing.
- **still stored** — the original refusal stands, reason and message unchanged.
- **the store could not answer** (an error, or no answer inside its own bound) — also "not confirmed
  gone", so the refusal stands. The return value is *confirmed gone* rather than *exists* exactly so
  that failing closed is structural: "I could not tell" has no way to read as "it was gone".

It is a read on an **error path only** — taken for a leg that has already failed, never once per
planned descendant — and it is bounded one rung inside the leg it runs in (it runs in that leg's
`.Catch`, past the point the leg's own bound still covers), derived by `MeshOperationOptions.Nest`
like every other rung on this path.

### Both stages, because both address the same snapshot

Fixing the pre-flight alone moves the failure one stage later, and that was measured too: the
recursive delete then failed at the commit with `[DeleteNode] not-found … partial-deleted=0`. The
commit leg takes the same reading, and when the leaf is confirmed gone it **emits nothing** — so the
path is not recorded as removed, and the operation still says truthfully what *it* took away rather
than claiming somebody else's removal.

That cannot make a delete report success over a live node, and the reason is structural: "drained"
is decided by [the drain](/Doc/Architecture/RecursiveDeleteDrain)'s own storage RE-ENUMERATION,
which is independent of every leg's verdict. A path that is still there comes back as a survivor and
is deleted in a follow-up pass; a subtree that never drains still fails loudly at
`MaxDeleteDrainPasses`.

`RecursiveDeleteVanishedDescendantTest` carries the pair. The second test is the discriminator: the
same leaf, the same unroutable address, the same refusal wording — and a store of record that says
the node IS there. The delete must still be refused and nothing removed. Without it the first test
would pass equally well on a pre-flight that had simply been taught to ignore routing failures.

This is **not** on #4668's own path: `IMeshService.DeleteNode` leaves `IncludeSatellites` false, so a
`_Comment` satellite is never part of a recursive plan. It was found while fixing that one, and is
tracked as [#4680](https://github.com/Systemorph/MeshWeaver/issues/4680).

## See also

- [The Recursive-Delete Drain](/Doc/Architecture/RecursiveDeleteDrain) — what "drained" means once
  the root *was* there, and why success has to mean the node is gone.
- [CQRS — Queries, Reads, Writes, Operations](/Doc/Architecture/CqrsAndContentAccess) — why a
  point read of a node that may not exist is a framework defect.
- [Data Access Patterns](/Doc/Architecture/DataAccessPatterns) — the one table of read/write/delete
  surfaces.
