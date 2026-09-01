---
Name: Silent Completion
Category: Architecture
Description: An observable that completes without emitting is invisible to every timeout. .Timeout faults on silence, not on a clean finish — so an empty completion sails past it, past every Catch, past every SelectMany, and the caller waits forever with nothing wrong anywhere. The shape, its two live instances, and how to guard it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12h5"/><path d="M16 12h5"/><path d="M11 12h2"/><circle cx="12" cy="12" r="9" stroke-dasharray="3 3"/></svg>
---

Everything in the mesh is `IObservable<T>`, and an observable has **three** terminal outcomes, not
two: it can emit, it can fault, or it can **complete having emitted nothing**. The third one is the
dangerous one, because none of the guards you habitually reach for cover it.

> **`.Timeout(...)` faults on SILENCE, not on a clean finish.** A source that completes without
> emitting passes straight through `Timeout`, through any `Catch` behind it, and through every
> `SelectMany` downstream — producing nothing, faulting nothing, bounded by nothing.

`SelectMany`, `Select`, `Where` and `Concat` all propagate an empty completion faithfully: nothing in
maps to nothing out. So an empty completion anywhere in a chain evaporates the whole chain, and the
subscriber's `onNext` and `onError` arms both stay unused. If the reply, the response post, or the
render push lives in one of those two arms, it never happens — and **the caller waits forever with
no error, no log line, and no timeout.**

This is the single hardest failure shape to diagnose in the codebase, because every instrument you
have reports "nothing is wrong": the write succeeded, no exception was thrown, no bound was
exceeded, and the chain is not even running any more.

## Telling the two hangs apart

A hang is either *still running* or *terminated empty*, and they need different evidence and
different fixes. Conflating them is what left several #981 captures unexplained.

| Shape | What the evidence shows | What bounds it | Fix |
|---|---|---|---|
| **Still running** — a slow or never-completing upstream | the last recorded stage is the await; nothing after it | the `.Timeout` | nothing, or a budget that reflects the real wait |
| **Terminated empty** — an upstream completed with no value | a terminal stage recording an empty completion, if anyone wrote one | **nothing at all** | `DefaultIfEmpty` and an explicit empty arm |

The practical tell: a capture whose age is comfortably *under* the chain's own `.Timeout` cannot be
"still running behind the timeout" — the timeout would have fired. An 0.7–3.2 s hang under a 15 s
`.Timeout` is an empty completion until proven otherwise.

## Instance 1 — filtering away a sentinel that means "declined"

`IStorageAdapter.Write` emits `null` as a **documented try-then-claim sentinel**: *"this adapter does
not own this path"*, so `PersistenceService.Write` moves on to the next writable provider. It does
**not** mean "the write succeeded".

`HandleCreateNodeRequest` used to filter it away:

```csharp
// ❌ The null is the DECLINE sentinel, not noise. Filtering it makes the chain complete empty,
//    so the detached observable that owes the reply posts nothing and the caller hangs forever.
persistence.Write(node, options)
    .Where(n => n is not null)
    .Select(n => n!)
```

The asymmetry is what makes this a defect rather than a taste question. The **composite**
`PersistenceService.Write` folds that null across its writable providers and, when every one
declines, *throws* — which the handler's `onError` arm answers correctly. But the resolved
`IStorageAdapter` is not always that composite: the non-partitioned wirings resolve a single
decorated adapter, and several decline by contract — `PathFilteringStorageAdapter` for a non-matching
path, the Postgres/Snowflake path-routing adapters for an unroutable partition, `RoutingProxyAdapter`
when no partition hub claims the path, `StaticNodeStorageAdapter` always. So **one condition either
failed cleanly or hung forever, decided purely by which adapter the hub happened to resolve.**

Whether a caller gets an answer must never depend on the storage wiring underneath it. The fix is to
turn the sentinel into the same speaking fault the composite already raises:

```csharp
private static IObservable<MeshNode> RequireClaimedWrite(
    this IObservable<MeshNode?> save,
    IMessageHub hub, string? requestId, IStorageAdapter adapter, string path)
    => save.SelectMany(saved =>
    {
        if (saved is not null)
            return Observable.Return(saved);
        hub.NoteRequestStage(requestId,
            $"CREATE_SAVE_DECLINED adapter={adapter.GetType().Name} path={path}");
        return Observable.Throw<MeshNode>(new InvalidOperationException(
            $"Could not save '{path}': the storage adapter '{adapter.GetType().Name}' declined the write "
            + "(the try-then-claim null sentinel — no writable storage provider accepted the node)."));
    });
```

Both paths now produce an identically shaped failure response from the one `onError` arm, and the
ledger stage names **which** adapter declined — the piece every earlier capture lacked.

**The general rule this instance teaches:** `.Where(x => x is not null)` over a source whose `null`
carries meaning is not a filter, it is a dropped branch. Before writing one, ask what the `null`
means to the producer. If it means anything other than "nothing to see here", handle it explicitly.

## Instance 2 — a generator that neither emits nor faults

A layout area's render generator is an observable like any other. One that never emits and never
faults leaves the area on its `"Rendering …  awaiting first data"` placeholder **forever**, and the
server logs nothing at all: no exception, no bound exceeded, no completion anyone noticed. From the
client the page is stuck; from the server everything is healthy. Four separate investigations could
not see it.

Nothing here is bounded on purpose — a legitimately slow area must never be cut short, and a timeout
would swap one silence for another while leaving the generator unfixed. What is missing is not a
bound, it is a **statement**. The area knows how many render results it actually delivered, and there
are two moments where zero is knowable **without a clock**:

- a terminal notification arriving having pushed nothing — provably stuck, report at `Error`;
- disposal having pushed nothing — a subscriber navigating away early is legitimate, report at
  `Warning`.

> **Trap for anyone building this kind of diagnostic:** the render observable completing *after* it
> has emitted is the normal shape (`OnCompleted` follows `OnNext`). The signal is the **count of
> results actually pushed**, never the completion itself — a diagnostic keyed on "completed" reports
> every healthy area and is worse than none.

## Instance 3 — a write whose BASE READ ended without the node

A cross-hub `stream.Update` reads the node off this hub's mirror, then diffs, posts and waits for the
owner's verdict — and **every** verdict it can raise lives inside that read's `onNext` arm, because
there is nothing to post until a base arrives. The read was subscribed with two arms:

```csharp
var initialSub = PatchBaseSource(RebaseSource(mirror, …), pendingSelfWrite)
    .Subscribe(current => { /* diff, post, arm every deadline, raise every verdict */ },
               ex      => { /* fail the caller */ });
               // ← no onCompleted
```

A mirror that completes without carrying the node therefore settled nothing at all — and note the
compounding detail that makes this the purest form of the shape: **not even the outer verdict
deadline was armed**, because that timer is created inside the response wait a write with no base
never reaches. Not "bounded by nothing" as an accident of `Timeout` semantics; bounded by nothing
because the bound is downstream of the emission that never came.

The empty completion is routine, not exotic — it is the disposal contract.
`SynchronizationStream.Dispose` **completes** its store rather than disposing it
(`Store.OnCompleted()`, deliberately, per #1170/#1171), so a stream completed before it ever carried
a value replays exactly one thing to a new subscriber: a bare `OnCompleted`.
`AcquireRemoteStreamUnchecked` hands back an
already-dead stream on purpose ("let the caller's subscribe collect the terminal"), `ReclaimIfUnheld`
disposes an evicted mirror the instant its last lease is released, and the write path's own
`Where(change => change.Value is not null)` can drop every emission there was. Under concurrency the
symptom is *N writes started, N−1 finished* — the writer that lost the shared mirror is the one that
never answers.

The guard is `MeshNodeStreamHandle.RequireBaseState`, move 2 below, at the one seam every base read
passes through. It faults rather than substituting a value: no base means no patch was posted, so the
write provably did not land and saying "saved" would be worse than the hang.
See [Write Verdict Totality](/Doc/Architecture/WriteVerdictTotality).

> **What made it survive review:** the rule was already written down *next to the fix*, on the
> conflict re-attempt branch, whose filter makes an empty completion look possible. The first-attempt
> branch was an unfiltered `mirror.Take(1)` and therefore looked like it could not end empty. It can —
> a disposed source ends empty whatever you do to it. Apply the guard at the SEAM, never per branch.

## Guarding a chain

Three moves, in order of preference.

**1. Make the empty case impossible at the source.** If a `null`, an empty sequence, or a "declined"
outcome carries meaning, convert it to a value or a fault at the point where the meaning is still
known — `RequireClaimedWrite` above is exactly this. This is the best fix because it puts the
knowledge where it exists.

**2. `DefaultIfEmpty` where a default is genuinely correct.** Only when there is a value that means
the right thing downstream:

```csharp
probe.Select(x => (Result?)x)
     .DefaultIfEmpty()              // "no result" is now a VALUE (null) the chain can act on
     .SelectMany(HandleOrReject)    // …and HandleOrReject must have a branch for it
```

**3. An explicit empty arm that fails closed.** `Subscribe` takes three arms; the third one is not
"nothing to do". When a handler owes its reply from a detached observable, `onCompleted` is a
terminal arm like any other, and leaving it unhandled is the one path that hangs the caller forever:

```csharp
chain.Subscribe(
    node => Respond(CreateNodeResponse.Ok(node)),
    ex   => Respond(CreateNodeResponse.Fail(ex.Message, …)),
    ()   =>
    {
        // Nothing emitted ⇒ nothing was created ⇒ no Ok can ever be right.
        if (emitted || responded) return;
        hub.NoteRequestStage(request.Id, $"CREATE_CHAIN_COMPLETED_EMPTY path={node.Path}");
        Respond(CreateNodeResponse.Fail("…", …));
    });
```

Both guards on that backstop are load-bearing, and copying the shape without them reintroduces a bug:

- **`emitted`** — the chain produced a node, so the reply is owed by the post-success subscription,
  whose own arms answer. Answering here would race a success into a failure. (This is not
  theoretical: a post-creation handler that completes on a different scheduler posts its `Ok`
  *strictly after* the chain's `onCompleted` runs.)
- **`responded`** — a branch already posted its own, more specific rejection (already-exists,
  validation, unknown node type). Answering again would post a second response for one correlation.

Route every terminal post through **one** local `Respond(...)` that sets `responded`. That is what
makes the backstop exact rather than approximate — and it makes a future branch that posts-and-
returns-`Empty` suppress the backstop automatically, without anyone having to remember which branches
can precede an empty completion.

## Reviewer's checklist

- Does any `.Where(...)` in this chain drop a value whose absence the producer treats as meaningful?
- Is there a `.Take(1)` over a source that can complete without emitting? `Take(1)` on an empty
  source is empty — this is the standing suspect wherever a permission fold or a probe feeds a
  `SelectMany`.
- Does the `Subscribe` have three arms, or two?
- If the chain is bounded by `.Timeout(...)`, is that bound the *only* guard? If so, the empty
  terminal case is unhandled by construction.
- Does the handler owe its reply from work it detached? Then `HANDLER_EXIT state=Processed` tells you
  nothing, and the handler must record and answer on **all three** of its own terminal arms.

## See also

- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — the `RequestFateLedger` fate trail: how to read a capture and see `*_COMPLETED_EMPTY` instead of inferring it.
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — rule 3a, the *other* way a caller waits forever (a response dropped because its subject was registered after the post).
- [Change-Feed Isolation](/Doc/Architecture/ChangeFeedIsolation) — the sibling shape: a notification swallowed in a fan-out.
- [Error Propagation & Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — drive wedges to zero; a silence is a wedge.
- [Negative Controls](/Doc/Architecture/NegativeControls) — how to prove the guard you just added actually catches the case.
