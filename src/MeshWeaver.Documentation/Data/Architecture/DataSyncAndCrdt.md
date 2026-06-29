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

## 3. The monotonicity guard — patches *and* Fulls

When a subscriber receives an owner frame (`UpdateStream`):

```
ANY FRAME (Patch or Full) : drop it if  Version < Current.Version
```

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
drop. **The corollary is unforgiving:** if even one frame is stamped from a
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
with a fresh Full. Gated by `_resyncInFlight` so a burst of confusing patches
triggers exactly one resubscribe, not a storm.

---

## 7. Minimal bytes on the wire

We move a *lot* of state, much of it large strings. The transport sends only
what changed:

- **Owner → subscriber:** a **JSON patch** (RFC 6901 / merge-patch RFC 7396)
  for deltas (`ToJsonPatch`); a **Full** for the initial snapshot and roll-backs.
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
| Stale **patch** AND stale **Full** dropped (`Version < Current`) | `SynchronizationStream.UpdateStream` guard |
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
band-aid, and a harmful one: it also swallows a **legitimate** re-assertion (a
roll-back `Full` whose value happens to equal what an upstream stream still
holds), stranding a subscriber that optimistically diverged (`§6`). With a single
source there are no value-equal redundant frames, so no dedup is needed, and the
roll-back `Full` propagates unimpeded.

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
