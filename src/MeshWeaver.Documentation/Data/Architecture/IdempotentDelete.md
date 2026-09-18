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

`NodeDeletionRejectionReason.NodeNotFound` did not go away. It is now reserved for an absence
discovered **mid-operation**: a leaf that vanished under a cascade already in flight, surfacing as a
routing failure from the commit stage. That is a different fact — the subtree may be partially
removed, so it is not in the state the caller asked for, and the caller is told.

`ValidateDeleteRequest` — the pre-flight *query*, "would this delete be allowed?" — also still
answers `NodeNotFound` for an absent node. It is answering a question about the node, not carrying
out a delete, and "there is no such node" is the correct answer to that question.

## 🚧 The same defect one level down, measured and NOT fixed here

A **recursive** delete plans its subtree by enumerating storage once, then asks every planned
descendant, in a bulk-atomic pre-flight, whether it may be deleted. A leaf that a concurrent delete
removes in that window still aborts the whole operation.

This was reproduced while fixing #4668 — a storage adapter that removes one planned descendant after
the plan is taken and before it is answered — and the repro **falsified the obvious fix**. The leg
never reaches a `ValidateDeleteResponse` at all: the post to the now-absent address fails to route,
and the refusal that comes out is

```text
Cannot delete 'X/gone': No node found at 'X/gone'. Closest ancestor is 'X' (remainder='gone').
This usually means the node is missing, has no NodeType, or has an invalid NodeType.
```

That message is ambiguous by its own wording. Telling "already gone" apart from "its type will not
load" needs a storage read the pre-flight does not take today, and that is a change to the
bulk-atomic refusal semantics built by #1198 and #1446 — not a relaxed verdict. It is tracked
separately as [#4680](https://github.com/Systemorph/MeshWeaver/issues/4680).

It is **not** on #4668's own path: `IMeshService.DeleteNode` leaves `IncludeSatellites` false, so a
`_Comment` satellite is never part of a recursive plan.

## See also

- [The Recursive-Delete Drain](/Doc/Architecture/RecursiveDeleteDrain) — what "drained" means once
  the root *was* there, and why success has to mean the node is gone.
- [CQRS — Queries, Reads, Writes, Operations](/Doc/Architecture/CqrsAndContentAccess) — why a
  point read of a node that may not exist is a framework defect.
- [Data Access Patterns](/Doc/Architecture/DataAccessPatterns) — the one table of read/write/delete
  surfaces.
