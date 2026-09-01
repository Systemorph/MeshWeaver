---
Name: Write Verdict Totality
Category: Architecture
Description: Every cross-hub stream.Update must reach a terminal. The base read is the seam where a write can silently reach none — a source that completes with no value settles nothing, posts no patch, and arms no deadline.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
---

# Write Verdict Totality

**A writer must always be told what happened.** `GetMeshNodeStream(path).Update(...)` is the only
mutation API, and every caller composes on its terminal: `.Subscribe(onNext, onError)`, a queue that
advances when the write settles, a test that waits for it, a hub whose disposal drains it. A write
that reaches **no** terminal is not a slow write — it is a permanent, silent hang of everything
downstream of it, and nothing is logged, because from the message layer's point of view the request
that started it succeeded long ago.

This page is about the one seam where that used to be possible, and the rule that closes it.

## The three terminations, and the one that had no handler

A cross-hub write (`MeshNodeStreamHandle.UpdateRemote`, reached through
[the one mutation API](../DataAccessPatterns)) is built in two stages:

1. **Read the base** — the node's current state, off this hub's mirror of it.
2. **Diff, post, and wait for the owner's verdict.**

Stage 2 is where every verdict the caller can receive is raised: the no-op short-circuits, the
owner's ack (early or late), each NACK, the delivery failure, and the outer
`VERDICT_TIMEOUT` deadline. All of them live **inside stage 1's `onNext` callback**, because there is
nothing to post until a base has arrived.

Stage 1 subscribed with two of Rx's three terminations:

```csharp
var initialSub = PatchBaseSource(RebaseSource(mirror, …), pendingSelfWrite)
    .Subscribe(
        current => { /* …diff, post, arm every deadline, raise every verdict… */ },
        ex      => { /* …fail the caller… */ });
        // ← no onCompleted
```

So a base read that **completes without a value** settled nothing at all:

- no `OnNext` and no `OnError` on the caller's observer;
- no `PatchDataRequest` posted, so no owner verdict could ever arrive;
- and **not even the outer verdict deadline**, which is armed inside the response wait that a write
  with no base never reaches.

The caller waits for the life of the process.

## Why a mirror ends without a value

This is not a theoretical Rx shape — it is the documented disposal contract. `SynchronizationStream`
does **not** dispose its store; it completes it:

```csharp
// COMPLETE the store — deliberately do NOT dispose it (#1170/#1171). OnCompleted
// detaches every subscriber, and per the Rx grammar a completed subject silently
// ignores any further OnNext/OnError/OnCompleted …
Store.OnCompleted();
```

So a stream that was completed **before it ever carried a value** replays exactly one thing to a new
subscriber: a bare `OnCompleted`. (A stream that did carry one replays value-then-completion, and
`onNext` runs normally — this is only about the empty case.) Three routine paths produce it:

| Path | What happens |
|---|---|
| `Workspace.AcquireRemoteStreamUnchecked` resolves a stream that was **already dead** | It deliberately returns it with an empty lease — "let the caller's subscribe collect the terminal". The terminal is a bare completion. |
| The last **lease** on an evicted mirror is released mid-write | `ReclaimIfUnheld` disposes the stream there and then. |
| The write path's own `Where(change => change.Value is not null)` drops every emission | Filtering introduces a completion the unfiltered source could not produce. |

The first two are load-dependent, and they are why this appeared under **concurrency**: N writers to
one path share one leased mirror, so one of them collecting the bare completion reads as *"N writes
started, N−1 finished"* — with nothing in the log to distinguish it from work still in flight.

## The rule

> **A source that cannot answer must SAY so.**

Every base read passes through one guard, `MeshNodeStreamHandle.RequireBaseState`, which converts an
empty completion into a terminal error:

```csharp
baseRead
    .Select(node => (MeshNode?)node)
    .DefaultIfEmpty(null)
    .SelectMany(node => node is not null
        ? Observable.Return(node)
        : Observable.Throw<MeshNode>(new InvalidOperationException(
            "Update aborted: this hub's mirror ended without ever carrying the node's state, so no "
            + "patch was built and none was posted. The write did NOT land; re-issue it.")));
```

**An error, not a value** — and that direction is the whole point. No base means no diff, which means
no `PatchDataRequest` was ever posted: the write *provably* did not happen. Emitting the unchanged
node instead would report "saved" for a write nobody attempted, which is the fail-open that
[the owner's commit verdict](../CqrsAndContentAccess) exists to prevent.

The guard is invisible to every write that has a base — which is all of them, normally. It only adds
a terminal to the case that had none, so it can turn a hang into an error and nothing else.

### Where it was, and why one branch was not enough

The rule was already written down — beside the **conflict re-attempt** branch of `RebaseSource`,
which filters the mirror for a version newer than the one the owner refused and can therefore end
empty. That branch got a `DefaultIfEmpty` guard and a comment stating the rule in general terms. The
**first-attempt** branch — the ordinary write, every caller write in the mesh — was a bare
`mirror.Take(1)` and got none, because the un-filtered shape *looks* like it cannot end empty.

It can: a disposed mirror ends empty whatever you do to it. The guard now wraps both branches at one
seam, and the two diagnostics stay distinct — "the mirror never carried the node" and "the owner
refused this at version N and the mirror never moved past it" send an operator to different places.

The same two-callback subscribe existed on the sync-stream write path
(`WriteViaSyncStream`, the Overwrite/bypass route) and carries the same guard for the same reason.

## What this is NOT

**It is not a timeout, a retry, or a watchdog.** No bound moves; nothing is re-attempted; no poller is
introduced. The change is that a source termination which previously reached no handler now reaches
one. If you find yourself widening `LateResponseWatchBound` or adding a re-subscribe to "recover" a
write that never answered, the question to ask first is which of the three terminations your
subscribe does not handle.

**It does not make a no-op write fail.** A lambda that returns the node unchanged, or whose diff is
empty, is a legitimate and *successful* write: it completes the caller with the current node and
writes nothing. That has always been handled, on both the own and the remote path — "nothing changed"
is an outcome, not a reason to withhold a verdict. The gap was narrower and stranger: a write with no
base at all.

## Diagnosing a suspected case

1. **Symptom**: N concurrent writes to one path, N−1 completions, no error anywhere, and a
   settle/drain signal that never fires. The process is otherwise healthy.
2. **Look for the missing pair.** In the `MeshWeaver.Mesh.MeshNodeStreamHandle` channel a write logs
   `[UpdateRemote] BEGIN` and then either `POST-PATCH`, `NO-OP`, or an error. A `BEGIN` with **none of
   the three** after it is a write that never got a base.
3. **Confirm the mirror.** `Workspace` logs `disposed superseded remote stream … no declared holder
   remains` when it reclaims one. A reclaim interleaved with a `BEGIN` for the same path is the
   shape above.
4. Since the guard landed, this surfaces as a normal `OnError` naming the path and saying the write
   did not land — so a caller can re-issue it, and `ActivityWriteTracker` / the per-path queue
   release as they should.

## Related

- [Silent Completion](../SilentCompletion) — the general shape this is instance 3 of: an observable
  that completes without emitting is invisible to `.Timeout`, to `Catch`, and to every `SelectMany`.
- [Data Access Patterns](../DataAccessPatterns) — the one mutation API and its read counterparts.
- [CQRS and Content Access](../CqrsAndContentAccess) — why a write's verdict is the owner's commit,
  never a bound expiring.
- [Asynchronous Calls](../AsynchronousCalls) — `.Subscribe(onNext, onError)` as the house shape, and
  what a cold observable owes its subscriber.
- [Bounds Must Be Ordered](../BoundsMustBeOrdered) — the sibling rule for nested deadlines: an inner
  bound must be able to fire first, because it is the only one that knows what starved.
