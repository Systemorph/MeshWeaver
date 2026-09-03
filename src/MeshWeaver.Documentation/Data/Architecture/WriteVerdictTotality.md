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

### The owner side has the same seam

The owner's ack watcher (`ApplyMeshNodePatchInTurn`, `DataExtensions.cs`) is a `.Take(1)` over the
owner's reduced stream waiting for the emission that carries the write, and it was subscribed with the
same two arms. A reduced stream that completes without emitting — mirror eviction disposing a
`SynchronizationStream` while the hub lives — ended the watcher with no ack posted, and the writer
reported `OwnerUnreachable` for a write the owner may have committed (issue #3033). It is now armed
through `ArmPatchAckWatcher` over the `WhenCompletesEmpty` operator — "run this callback if the
source completes without ever having emitted" — which is `RequireBaseState`'s idea in operator form,
with one twist the owner side needs and the writer side does not: the callback must fire ONLY for a
completion that no emission preceded, because on the happy path `Take(1)` completes while the durable
flush started in `onNext` is still in flight, and a bare completion arm would NACK the write that is
about to succeed. Which verdict each path posts, and why the code is `OwnerDisposing`, is in
[Reading a Write Verdict](../ReadingAWriteVerdict).

## The FOURTH outcome — never emits, never completes

The three terminations above are the ones Rx *delivers*. There is a fourth outcome, and it delivers
nothing at all: **a source that neither emits nor completes**. `WhenCompletesEmpty` cannot see it —
there is no completion to observe — and neither can an `onError` arm. It is settled as silence by
every guard on this page.

It is not hypothetical. A stream's `Store` is a `ReplaySubject`, so one that has never published and
is never disposed hands `Take(1)` nothing, for the life of the process. Two owner-side legs had it,
in different shapes (#3194, #3195):

| Leg | What it had | What was missing |
|---|---|---|
| The generic patch path's initial base read | `Take(1)` + empty-completion arm | **No bound anywhere.** The only other bounded watcher on that path is armed *inside* the `onNext` arm, so a stream that never emits never arms it |
| The cold-activation defer | a 10 s bound | **No completion arm.** A completing primary store passes `Take(1)` and *cancels the Timeout* — a terminal disposes the timer — so neither arm runs |

Both now compose through one seam, `DataExtensions.ArmedOneShotRead`:

```csharp
source                      // already filtered by the caller
    .Take(1)
    .Timeout(bound, scheduler)
    .WhenCompletesEmpty(onEmptyCompletion);
```

Four outcomes, four terminals. And the seam exists rather than three hand-written chains for a
second reason: **the bound sits below the caller's filter.** A `Timeout` placed *above* an operator
that drops emissions is re-armed by every dropped one, so on a busy stream it can never fire — a
bound that is present, reviewed, and unreachable, which is exactly what
[Bounds must be ordered](../BoundsMustBeOrdered) is about. Handing the seam an already-filtered
source makes that order structural instead of remembered.

Expiry is not a lost write. On both legs the merge provably never ran, so the verdict is the
auto-retried `OwnerNotReady` / `OwnerDisposing` rather than the caller's 31 s `OwnerUnreachable` —
see [Reading a Write Verdict](../ReadingAWriteVerdict).

## A verdict also needs a route that is CHECKED

Totality is about producing a verdict. It is not finished until one has been *delivered*, and the
two ack gates produced theirs and then discarded it (#3196):

```csharp
if (Interlocked.Exchange(ref ackPosted, 1) != 0) return;   // gate claimed HERE
…
hub.Post(resp, o => o.ResponseFor(request));               // result DISCARDED
```

Claiming first is right — two racing legs must not both answer. But claiming is *also* what disables
the fallback: `RegisterOwnerDisposingNack`'s `tryClaimAck()` now returns false, so its
`ILatePatchVerdictSink` dispatch — the one route that still reaches an armed waiter with no message
routed at all — is skipped. And the post can be refused: with the owner past `DisposeHostedHubs` and
its parent past it too, `MessageService.PostImplGeneric` stamps `POST_REFUSED_SHUTTING_DOWN` and
returns a `Failed` delivery, its own comment noting that *"This site does NOT answer the sender
itself"*. The verdict was thrown away and the door shut behind it.

**Claim, then VERIFY.** The gate still latches first; the verdict now falls through to the sink when
the transport refuses it. The post stays first here, unlike in the disposal NACK — this runs on the
live path, where the caller's `Observe` callback is armed and the message *is* the designed seam.
Missing both routes is then a checked fact (nobody in this mesh is armed, and the transport is gone)
and is logged rather than swallowed, because the remaining case — a caller in another process — is
one the registry cannot see.

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
