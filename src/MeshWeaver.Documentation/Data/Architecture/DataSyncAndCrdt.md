---
NodeType: Markdown
Name: "Data Synchronization and CRDT"
Abstract: "The synchronization-stream contract: who assigns the version (the owning hub, in its queue, via the single OwnerVersion() clock — init frame included), the monotonicity guard over patches AND Fulls, version + string-splice conflict resolution, reject→rollback via a current-versioned Full, and the minimal-bytes (JSON-patch + string-delta) transport."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00897b'/><path d='M7 9a6 6 0 0 1 10-1' stroke='white' stroke-width='2' fill='none' stroke-linecap='round'/><path d='M17 16a6 6 0 0 1-10 1' stroke='white' stroke-width='2' fill='none' stroke-linecap='round'/><path d='M17 5v3h-3' stroke='white' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/><path d='M7 19v-3h3' stroke='white' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Synchronization"
  - "CRDT"
  - "Streams"
  - "Consistency"
---

# Data Synchronization & CRDT

How MeshWeaver keeps a piece of state consistent across hubs — the version
model, the monotonicity rules, conflict resolution, and the minimal-bytes
transport. This is the contract every `ISynchronizationStream<T>` obeys.

---

## 1. The actors

A `SynchronizationStream<TStream>` is one synchronized value (a `MeshNode`, a
collection, an `EntityStore`, a layout-area control tree, …). It has three hub
references that are easy to confuse — and the version model hinges on the
difference:

| Member | What it is | Role |
|---|---|---|
| `Owner` | `Address` | **Who owns the truth.** Writes are *requested* of the owner. |
| `Host` | `IMessageHub` | The hub that **hosts** this stream instance (the local node). |
| `Hub` | `IMessageHub` | A **per-stream sync sub-hub** — `Host.GetHostedHub(sync/<clientId>)`. Its single-threaded action block serializes this stream's updates. |

`Hub` is a *child* of `Host` (`Host.GetHostedHub(...)`). **They are different
clocks.** `Host.Version` is the host's global message counter — it can sit
still for many stream updates, or jump by hundreds between two of them.
`Hub.Version` is *this stream's* counter — it ticks once per message the stream
processes. **For per-stream ordering, only `Hub.Version` is meaningful.**

> The stream IS the owner ⇔ `Owner.Equals(Host.Address)`.
> - **Layout areas** are owned by their own stream/hub → the stream assigns its own version.
> - **MeshNodes / domain data** are owned by the *host* node → a write must travel to that host's queue (below).

---

## 2. Version: assigned by the owning hub, in the owning hub's queue

**The `Version` is the one reliable ordering signal.** DateTime is not — there
is no universal clock across hubs. So:

1. **The owner assigns the version, inside its execution queue.** A normal
   update is `hub.Post(UpdateStreamRequest)`; the handler runs on the stream's
   sync-hub action block (serialized — one update at a time), and *there* the
   version is stamped from the hub that just ticked. Reading a version outside
   the lock would race; reading it inside the serialized handler is monotonic by
   construction.
2. **A subscriber never mints a version.** It either:
   - **adopts** `delivery.Message.Version` verbatim when it applies an
     owner frame (Full or Patch), or
   - **carries the BASE version it last observed** (`Current.Version`) on the
     change it *requests* of the owner — so the owner knows what the writer saw.
3. **No "other" hub stamps it.** Not the sync sub-hub of a *subscriber*, not
   `Host` when `Host` isn't the owner.

Net effect: every frame on a given stream carries a **strictly increasing**
version assigned by one clock — the owner's. (Pinned by
`StreamVersionMonotonicityTest`.)

**One helper, every emission path.** `SynchronizationStream.OwnerVersion()` is
the single place that picks the clock — `Owner.Equals(Host.Address) ? Hub.Version
: (Current?.Version ?? 0L)`. **Every** frame an owned stream emits funnels
through it: a value `Update` (`BuildChangeItem`), a full overwrite
(`BuildFullChangeItem`), **and the init/base frame** (`Initialize` — the layout
area's "Building layout…" shell, a data source's initial snapshot). The init
frame used to read `Host.Version` directly; because `Host.Version` (the parent
host hub) runs hundreds of ticks ahead of a freshly-created `sync/<id>` sub-hub,
the base frame outranked the render content that followed on `Hub.Version`, and
§3's guard dropped the content. Funnelling the init frame through `OwnerVersion()`
keeps it on the same clock as the renders, so `base.Version < content.Version`
always holds.

---

## 3. The monotonicity guard — patches *and* Fulls, but OWNER frames only

When a subscriber receives an **owner frame** (`UpdateStream`):

```
ANY OWNER FRAME (Patch or Full) : drop it if  Version < Current.Version
                                  …unless a Full is consuming the resubscribe latch (below)
```

### 🚨 `Version` means two different things, and the guard reads only one of them

`UpdateStream` handles messages travelling in **both** directions, and the meaning of `Version`
flips with the direction:

| Message | Direction | What `Version` is | Guard? | Adopt as `Current.Version`? |
|---|---|---|---|---|
| `DataChangedEvent` | owner → mirror | the **owner's clock** | **yes** — comparable | **yes** |
| `PatchDataChangeRequest` | subscriber → owner | the **base the writer last applied** | **no** — not comparable | **no** — it would rewind the owner |

A subscriber's write is optimistic by definition: `StandardReducers.PatchJsonElement` stamps it
with `stream.Current?.Version` — the frame the writer had in hand — so it is **below the owner's
clock by construction** whenever an owner frame is in flight. §4 already names that case as the
one the owner must *merge*. Comparing it against the owner's clock and dropping it therefore
discards a legitimate write, silently: no rollback Full, no `DeliveryFailure`, one `Debug` line.

That was Systemorph/MeshWeaver#2701. The measured shape is an editor whose re-render takes 100 ms:
a burst of `UpdatePointer` writes lands while that render's frame is still on the wire, every one
of them is dropped, and the control stream that should have carried the result **emits nothing at
all** — indistinguishable from a slow machine, which is why it read as a flake for months. It is a
data-loss bug, not a timing one: a user's edit typed while the server pushes an unrelated
re-render was thrown away. Pinned by `StaleBaseSubscriberWriteTest`
(`test/MeshWeaver.Layout.Test`).

The same asymmetry was already drawn one block further down in the *frame-loss* check, which
applies "to `DataChangedEvent` (owner→mirror) only: a `PatchDataChangeRequest`'s chain is stamped
by the SENDING mirror and is not comparable to this stream's applied version". The version
handling simply never got it.

**Having applied a subscriber's write, the owner keeps its own clock.** Adopting the writer's base
would move `Current.Version` *backwards*, and the frame the owner then broadcasts would be stamped
below what its other subscribers already hold — so *their* (correct) guards would drop it, and the
same loss reappears one hop out. The owner's version is a **floor**: applying a subscriber write
can only ever move it forward.

A **Patch** is a delta computed against a *specific base version*; applying a
reordered older patch corrupts the mirror, so it is version-guarded.

A **Full** is the owner's *complete authoritative state* — but it is **also
version-guarded**. The guard once let every Full through unconditionally; that
let a resubscribe's point-in-time Full, snapshotted *before* a write the mirror
had already applied, overwrite the newer state (the lost-message data loss). So
a Full whose `Version < Current.Version` is a **stale snapshot** and is dropped
too. (`SynchronizationStream.UpdateStream`.)

This is safe **only because every frame the owner emits rides one clock** — the
owner's `Hub.Version` (§2). A legitimate re-assertion can never carry a version
*below* `Current`: a reject→ROLLBACK Full re-asserts the owner's CURRENT state,
stamped with its current (higher-or-equal) version, so it still lands (§6); only
a genuinely older snapshot can be below `Current`, and that is exactly what we
drop.

**The one sanctioned exception: the rebased-resubscribe Full.** A grain that
idle-recycles resets its `Hub.Version` to ~0, so the fresh snapshot it sends to a
mirror that *asked* for one carries a frame version below the mirror's cached
(pre-recycle) value — and the guard above would drop it, orphaning the mirror
(#325 symptom 2, multi-replica only). So a version-gated resubscribe — issued
**only** when the change feed announced a node version *higher* than the mirror
holds — arms a one-shot latch (`SynchronizationStream.ExpectResubscribeFull`).
The next `Full` consumes the latch, is accepted despite the regression, and the
mirror adopts the owner's re-based clock. Only a `Full` consumes it (a stray
reordered patch is still dropped), and because the latch is armed only when the
mirror is genuinely behind, it can never let a stale snapshot clobber a newer
optimistic write. **The corollary is unforgiving:** if even one frame is stamped from a
*different* clock — e.g. the init/base frame stamped with `Host.Version` while
the render content rides `Hub.Version` — the version order breaks and the guard
discards real content. That was the layout-area "stuck on *Building layout…*"
non-emission; the fix is `OwnerVersion()` (§2, §11), which forces every frame
onto the owner's stream clock.

---

## 4. Where a write goes (ownership routing)

### Self-owned (layout area): `Owner == Host.Address`
The stream is the owner. `stream.Update(...)` posts an `UpdateStreamRequest` to
its own sync hub; the handler validates, applies, **assigns the version**, emits.
Done — no network hop.

### Host-owned (MeshNode / data): `Owner != Host.Address`
The subscriber CANNOT assign a version. It must transfer the change to the
owner:

1. The subscriber's local change is converted to a **`DataChangeRequest`**
   (`ToDataChangeRequest`) and `hub.Post(..., WithTarget(Owner).WithAccessContext(caller))`.
2. The request lands on the **owner's execution queue**.
3. Inside that queue the owner **validates (RLS) → accepts or rejects**.
4. On accept it **applies the change and assigns a fresh version** off its own
   sync-stream clock.
5. The new state streams back to every subscriber (the requester sees its own
   optimistic change reconciled; others see the merge).

This is the canonical cross-hub write — `JsonSynchronizationStream` lines
~179–219. The version is born in the owner's queue; the subscriber only ever
*proposed* a change.

---

## 5. CRDT — conflict resolution by version + string splice

Because the request carries the **base version it was computed from**, the owner
can resolve concurrency without a universal clock:

| Incoming base vs owner's current | Action |
|---|---|
| `base >= current` | **Fast-forward** — take the change as-is. |
| `base < current`, **Patch** | **Merge** — re-derive what the writer actually changed (`base → incoming`) and replay THAT onto current, so a writer who touched a different field/region doesn't clobber the concurrent edit. |
| `base < current`, **Full** | A stale full snapshot it can't merge — keep current (a Full from the *owner* is always trusted; a stale full *into* the owner is rejected). |

(`StreamConflictResolution.Resolve`.)

### String fields merge by splice, not clobber
A string field changed by **both** sides is reconciled with **`StringDelta`**:
the writer's splice (`Start, RemovedLength, Inserted`) is replayed onto the
*current* text. Disjoint edits to the same big string both survive — "The **VERY**
quick brown fox" + "…fox **leaps**" → "The VERY quick brown fox leaps".
(`StringDelta`, `StreamConflictResolutionTest`.)

---

## 6. Roll-back / undo

When the owner **rejects** a proposed change (validation/RLS fails), the
subscriber holds an *optimistic* value the owner never accepted. The fix is a
roll-back: **the owner re-asserts its authoritative state as a FULL**. That Full
carries the owner's **current** version (≥ the subscriber's optimistic bump, which
was only ever a *base* the subscriber carried — a subscriber never mints a version,
§2), so it passes §3's guard and overwrites the optimistic value. The undo is clean
because the rollback Full is *current*, not because Fulls bypass the guard — they
no longer do.

**Request a Full when unsure.** A subscriber that detects it is out of sync (a
patch arrived with no base, a patch failed to apply, a write was rejected) calls
`RequestFreshSnapshot()` — it re-`SubscribeRequest`s the owner, which replies
with a fresh Full. Gated by `resyncInFlight` so a burst of confusing patches
triggers exactly one resubscribe, not a storm.

### The frame chain, and how to read `Frame loss detected`

The transport under the fan-out is **at-most-once** — a frame published before a
subscriber's stream subscription attached, or dropped under pressure, simply never
arrives and nothing re-sends it. Before this was detectable, that loss was *silent*:
later patches kept applying cleanly (they touch other entities), so the mirror tracked
the owner forever at a constant deficit with no error anywhere.

So every frame the owner emits carries **`BasedOnVersion`** — the version of the frame
*this same forwarding subscription* sent immediately before it (`-1` for the first).
A mirror compares an incoming Patch's `BasedOnVersion` against the version it last
applied; a mismatch proves the gap, and the only sound reaction is a fresh
authoritative snapshot: `RequestFreshSnapshot()` (above). Frames the owner *skips*
(value-equal, no updates, an echo-suppressed patch) never enter the chain, so a
legitimate version gap cannot false-trigger a resync.
(`JsonSynchronizationStream.ToDataChanged` → `SynchronizationStream.UpdateStream`;
test `StreamFrameLossResyncTest`.)

🚨 **`[SYNC_STREAM] Frame loss detected …` is a RESYNC counter, not a data-loss
counter.** Every line is a gap that was *detected and answered*; the mirror converges
on the Full that follows. A raw count therefore means nothing on its own — the two
numbers that do are **per-stream count** and **whether a Full ever follows**. Thousands
spread over hundreds of streams is the recovery working; a stream that logs the line
repeatedly and never converges is the defect (that one is #2654 — a layout area stuck
on its `NamedAreaControl` placeholder).

🚨 **The driver is almost always upstream of this file.** Anything that repeatedly ends
and re-establishes a subscriber's server-side stream costs one gap per cycle, so read
these lines from the *same window* before blaming the sync protocol:

| line in the same window | what it means for the count |
|---|---|
| `Orleans '…' stream subscription could not be attached … cross-process routing … DISABLED` | the hub is reachable in-process only; the router will call it unserved (#2633 / #2692, fixed by #2645) |
| `[ROUTE] Stream-routed delivery to '…' has no live subscriber` + `ClientSubscriptionEviction` | the owner evicted that subscriber's server-side streams on the router's `TargetUnserved` verdict (#2620). Correct when the subscriber is dead — one gap per cycle when it is not |
| `Stream {StreamId}: owner {Owner} … — resubscribing for fresh snapshot` | an owner recycle / `StreamEndedEvent`; the re-assert re-bases the chain |

That correlation is the recorded disposition of the memex-cloud storm on **#2641**
(847 lines / 30 min): the frame-loss count was the *symptom* of an attach latch and the
eviction cycle it caused, not a defect of the chain. See also
[Durable Streams Are Mesh Nodes](/Doc/Architecture/DurableStreamsViaMeshNodes) — the
version chain **is** the durable stream, which is why no durable stream provider is
bought to stop these lines.

### The convergence contract — what re-opens the resync gate (#2654)

Detecting a gap is only half the protocol. **The re-ask travels the same leg that just
lost a frame**, so the design question is what happens when the re-ask, or its answer,
does not arrive. That is the *whole* of #2654: the detector was right, the recovery was
not, and the failure was silent — a layout area on its `NamedAreaControl` placeholder
while the breadcrumb, banner and menus around it rendered fine.

`resyncInFlight` bounds **one re-ask OUTSTANDING**, and it is released by that re-ask's
**round trip**:

| release | meaning |
|---|---|
| the fresh **Full** lands | the mirror has its base — the success case; also resets the did-not-converge counter |
| the owner's **`SubscribeAck`** | the owner has processed the re-subscribe and done whatever it is going to do |
| a **verdict** on the request (`DeliveryFailure`, or the hub's own no-response terminal) | the request cannot be answered — `ResyncRefused` |

🚨 **Releasing the gate asks for nothing.** Nothing polls, retries or runs on a timer:
only the *next frame that proves the mirror still has no base* drives a new re-ask, so
the rate is bounded by the round trip **and** by the owner actually emitting — the same
bound `JsonSynchronizationStream.Resubscribe`'s in-flight flag has always lived with.
Because the owner ACKs before its re-assert reaches the wire, the common case costs at
most **one redundant round trip** per gap, and that redundant re-ask is itself answered
with a Full, which ends the cycle.

Three properties this contract needs, each of which was missing:

1. **The re-ask is `Observe`d, never `Post`ed.** `SubscribeRequest` is an
   `IRequest<SubscribeAck>` and `DataExtensions.HandleSubscribeRequest` answers every one
   of them, so a verdict always exists — fire-and-forget threw it away. `ResyncRefused`
   applies the **same** classification the stream's own `DeliveryFailure` handler does, one
   policy per type: `ShuttingDown` is transient and is ridden out; every other verdict is
   terminal and **faults the stream**, so the subscriber sees a failure rather than an
   eternal placeholder. A verdict that never arrives at all (the request was undeliverable)
   is neither — Warning, recoverable. The `Observe` is wrapped in `Observable.Defer` so a
   *synchronous* post throw reaches the same arm instead of escaping `UpdateStream` with the
   gate already shut.

   🚨 **Classify on `ErrorType`, never on `TargetUnserved`.** That stamp is the *owner-side*
   eviction gate (`DataExtensions.HandleTargetUnservedFailure`, #2426/#2546), and the router
   deliberately puts it on **both** of its "nobody serves that address" verdicts — the
   terminal no-live-subscriber refusal (`RefuseNoSubscriber`, `NotFound`) **and** the
   transient pod-hub refusal a rolling deploy produces while a silo's claim has not landed
   (`AnswerPodHubNotHere`, `ShuttingDown`, #2745). Reading the stamp as "terminal" faults
   every mirror in that overlap window. `RoutingGrain` states the rule itself: the stamp is
   the eviction gate, the `ErrorType` beside it says whether the *sender* keeps its recovery
   armed, and the two are independent.
2. **A mirror holding no cached JSON accepts a Full at any frame version.**
   `RequestFreshSnapshot` discards the snapshot before re-asking, so there is nothing a
   rebased Full could clobber, and refusing it leaves the mirror with nothing at all. This
   matters because an owner that has to *rebuild* the server-side stream to answer (the
   subscriber was evicted on the router's `TargetUnserved` verdict, #2620; the owner grain
   recycled) stamps that stream's first Full on a **reset** clock — so §3's monotonicity
   guard used to throw away the very snapshot the mirror had asked for.
3. **The gate cannot wait on the chain to notice a lost answer.** A re-assert Full carries
   the version of the *state* it re-asserts (`BuildReassertFrame`, §6 / #945), not a new
   one — so it shares a version with the frame before it, and the `BasedOnVersion` chain
   reads `v4 → Full v4 → v5(basedOn 4)` exactly like `v4 → v5(basedOn 4)`. **Losing a
   re-assert Full is invisible to the chain.** Measured, not assumed
   (`StreamResyncConvergenceTest`).

**The operator signal** is `[SYNC_STREAM] Resync has not converged for {StreamId}: asking
{Owner} for a fresh snapshot again (attempt N)` at **Warning**. Attempt 1 is the ordinary
recovery and stays at Debug; anything above 1 is a mirror that asked and was not answered.
That is the line to grep for when reading a portal log — it separates "gaps that were
answered" (the healthy shape above) from "a stream that keeps asking and never converges".

**What a re-subscribe is owed by the owner.** `CreateSynchronizationStream`'s
`alreadyServing` branch re-asserts the current snapshot as a Full. When there is nothing to
assert yet — the initial subscribe is still hydrating, `Current` is null — it returns
without sending, and that is correct rather than a hole: `Current` and the outbound JSON
cursor are set by the *same* emission, so a stream with no `Current` has an empty cursor,
and `ToDataChanged`'s `currentJson is null` branch makes its **first** frame a `Full` by
construction. The subscriber is therefore always answered with a Full; if that Full is lost
in transport, the gate above — not the chain — is what recovers it.

Pinned by `StreamResyncConvergenceTest`: the answer lost in transport, the answer arriving
on a rebased clock, the re-ask refused **terminally** (must fault), and the re-ask refused
**transiently** with the identical `TargetUnserved` stamp (must be ridden out and still
converge). Each fails on the pre-#2654 tree.

---

## 7. Minimal bytes on the wire

We move a *lot* of state, much of it large strings. The transport sends only
what changed:

- **Owner → subscriber:** a **JSON patch** (RFC 6901 / merge-patch RFC 7396)
  for deltas (`ToJsonPatch`); a **Full** for the initial snapshot and roll-backs.
- **Big strings, owner → subscriber → a `splice` operation, NEGOTIATED.** A `replace`
  of a string leaf at or above `PatchStringSplice.MinSpliceLength` travels as
  `{"op":"splice","path":…,"value":{"$sd":[start,removed,"inserted"],"$sdb":[baseLength,"fingerprint"]}}`
  — the changed span plus a fingerprint of the text it was diffed against, so a streaming
  cell costs `O(chunk)` per frame instead of `O(length)` per frame **per subscriber**
  (measured: a 20 kB answer over 200 frames, 1.93 MB → 42 kB). The subscriber applies it
  only when the fingerprint proves its text IS that base; otherwise it refuses and takes
  the ordinary stale-patch route, `RequestFreshSnapshot()` (§6) — never a blind splice.
  🚨 **Emitted only to a subscriber that set `SubscribeRequest.AcceptsStringSplice`.**
  Unlike the write direction, this fan-out is consumed by hand-rolled appliers in
  `clients/grpc-web`, `clients/react` and `clients/python` that this repo's CI does not
  build, and each of them fails *silently* on a shape it does not know — the JS ones skip
  an unknown `op`, the Python one applies it as a replace. So the capability is declared,
  not assumed, and everyone who does not declare it receives byte-identical bytes to
  before. (`PatchStringSplice.Compress`; test `FanOutStringSpliceTest`.)
- **Subscriber → owner:** a `DataChangeRequest` carrying the **changed entities
  only** (per `(Collection, Id)`), not the whole store.
- **Big strings → `EntityDeltaUpdate` (recursive string-delta):** a changed string
  field travels as its splice (`{ "$sd": [start, removed, "inserted"] }`) —
  *recursively*, so a string buried in a nested object splices too
  (`{ "$nd": {…} }`, e.g. the markdown inside `MeshNode.Content.Content`) — never the
  whole value. A 100 KB body that gained one character is a few bytes on the wire.
  **Wiring:** the subscriber's `ToDataChangeRequest` emits an `EntityDeltaUpdate`
  (carrying `Collection`, `Id`, `Partition`, and the splice) in place of the full
  entity — **gated** to entities ≥ `EntityDelta.MinDeltaSize` whose delta is actually
  smaller and whose **partition resolves** (so the owner routes it to the same stream;
  otherwise it falls back to a full re-send, unchanged whole-replace). The owner
  (`WorkspaceOperations.ResolveDelta`) replays the splice onto its CURRENT value
  before the normal apply, so a disjoint concurrent edit on the owner survives (same
  merge semantics as §5). (`StringDeltaPatch`, `EntityDelta`; tests
  `StringDeltaPatchTest`, `EntityDeltaTest`, `StringDeltaTransportTest`.)

---

## 8. Reading & writing a mesh node (the public surface)

Application code never touches `GetRemoteStream<MeshNode>` (forbidden — it does
not converge; see [CqrsAndContentAccess.md](/Doc/Architecture/CqrsAndContentAccess)). The one
API is `hub.GetMeshNodeStream(path)` / `workspace.GetMeshNodeStream(path)`, which
routes every cross-hub read and write through the shared `IMeshNodeStreamCache`
— one process-wide upstream per path, so reads and writes share the same live
mirror and the convergence rules above hold.

---

## 9. Invariants (the test ledger)

| Invariant | Guard / Test |
|---|---|
| Owner assigns strictly increasing versions per stream — including the init/base frame | `SynchronizationStream.OwnerVersion`; `StreamVersionMonotonicityTest` |
| A subscriber never mints a version | `UpdateStream` adopt-only; `StreamUpdateIdentityTest` |
| Stale **patch** AND stale **Full** dropped (`Version < Current`) — **owner frames only** | `SynchronizationStream.UpdateStream` guard |
| …except a Full consuming the version-gated resubscribe latch | `SynchronizationStream.ExpectResubscribeFull`; `TwoSiloRecycleConvergenceTest` |
| A subscriber's write based on an EARLIER owner frame is merged, never dropped | `StaleBaseSubscriberWriteTest.ASubscriberWriteBasedOnAnEarlierOwnerFrame_IsMerged_NotDroppedAsStale` |
| Applying a subscriber's write never rewinds the owner's clock | `StaleBaseSubscriberWriteTest.ApplyingASubscriberWrite_DoesNotRewindTheOwnersClock` |
| A late layout-area subscriber gets its render content, not just the base frame | `DataChangeStreamUpdateTest.DataChangeRequest_ShouldUpdateLayoutAreaViews` |
| Disjoint concurrent string edits merge | `StringDeltaTest`, `StreamConflictResolutionTest` |
| A changed string field (incl. nested) ships only its splice | `StringDeltaPatchTest` |
| Cross-hub: subscriber sends a delta, owner reconstructs the exact entity | `EntityDeltaTest`, `StringDeltaTransportTest` |
| A value-equal **Full** still applies (no dedup) — rollback / resync lands | `SynchronizationStream.SetCurrent` Fulls-bypass |
| Out-of-sync subscriber can request a Full | `RequestFreshSnapshot` |

---

## 10. Single source — the owning hub, and why there is no dedup

**Every synchronized value has exactly ONE authoritative source: its owning hub.**

- **Mesh nodes** → the per-node hub at the node's path address (`§1` `Owner`).
- **Layout areas** → their own sync hub.

A synced type (agents, language models, any live collection) is sourced **only**
from those owning hubs' sync streams. It is **not** *also* loaded from
persistence, *not* re-published by routing, and **not** returned as a second
authoritative copy by mesh queries. A query may tell you *which* paths are in a
collection (membership), but the **content** of each comes from that path's
owning hub — never a parallel persistence/query mirror.

**Why this matters: it removes the need for dedup.** When the same entity arrives
through two sources (its sync stream *and* a query/persistence mirror), the
workspace sees two value-equal frames and something downstream must suppress the
redundant one. That suppression — a value-equality check in `SetCurrent` — is a
band-aid, and it once swallowed a **legitimate** re-assertion: a roll-back `Full`
whose value happened to equal what an upstream stream still held, stranding a
subscriber that had optimistically diverged (`§6`). That specific hole is closed
— `SetCurrent` now value-dedups **patches only**, and a `Full` always applies —
but the dedup itself is still the symptom of a double-source. With a single
source there are no value-equal redundant frames at all.

> **Rule.** If you find yourself adding (or relying on) a value-equality dedup on
> a sync stream, you have a **double-source** — fix the source, not the symptom.
> Route the read through the owning hub (`workspace.GetMeshNodeStream(path)`),
> and keep the synced collection's content single-sourced from there.

---

## 11. Mistakes this design exists to prevent

- **Stamping `Host.Version`** (or a subscriber's sync-hub) on *any* frame instead
  of the owner's stream clock → non-monotonic versions → the guard drops real
  updates → "view doesn't refresh / blank layout". The trap is the **init/base
  frame**: it is easy to stamp it from the surrounding `Host` while the content
  frames correctly ride `Hub.Version`. Funnel **every** emission through
  `OwnerVersion()` (§2). *(This is the exact defect behind the 2026-06 layout-area
  "stuck on Building layout…" non-emission — latent until §3 began guarding Fulls.)*
- **Guarding Fulls *without* the one-clock guarantee** → a real Full looks stale
  and is dropped. Guarding Fulls (§3) is correct and necessary, but it is only
  safe because every owner frame rides `OwnerVersion()`; break that and the guard
  turns on you. A genuine roll-back/re-sync Full always carries the owner's
  *current* version, so it is never below `Current` — see §6.
- **Sending whole entities / whole strings** → bandwidth blowup on large content.
- **Letting a subscriber mint versions** → two mirrors fight over ordering; last
  write wins on the whole node instead of a field-wise merge.
