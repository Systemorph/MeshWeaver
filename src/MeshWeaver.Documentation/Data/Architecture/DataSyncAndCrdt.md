---
NodeType: Markdown
Name: "Data Synchronization and CRDT"
Abstract: "The synchronization-stream contract: who assigns the version (the owning hub, in its queue), the patches-only monotonicity guard, version + string-splice conflict resolution, reject→rollback via Full, and the minimal-bytes (JSON-patch + string-delta) transport."
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

---

## 3. The monotonicity guard — patches only

When a subscriber receives an owner frame (`UpdateStream`):

```
PATCH  : drop it if  Version < Current.Version   (a reordered/stale delta would corrupt the mirror)
FULL   : ALWAYS apply, regardless of Version
```

A **Patch** is a delta computed against a *specific base version*; applying a
reordered older patch corrupts the mirror, so it is version-guarded.

A **Full** is the owner's *complete authoritative state*. It is always applied,
no matter the version — it cannot be a harmful straggler (it's the whole truth),
and **a Full is normally a ROLL-BACK** (§6). Letting Fulls through
unconditionally is what makes reject→rollback work and what lets a re-sync
recover a confused subscriber.

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
roll-back: **the owner re-asserts its authoritative state as a FULL**, which —
per §3 — is always applied and overwrites the subscriber's optimistic bump even
though the subscriber had locally advanced its version. No re-version gymnastics
needed; "Full always lands" is precisely the property that makes the undo clean.

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
- **Big strings → `StringDeltaPatch`:** a changed string field travels as its
  splice (`{ "$sd": [start, removed, "inserted"] }`), never the whole value — a
  100 KB body that gained one character is a few bytes on the wire. The owner
  replays the splice onto its current text (same merge semantics as §5).
  (`StringDeltaPatch`, `StringDeltaPatchTest`.)

---

## 8. Reading & writing a mesh node (the public surface)

Application code never touches `GetRemoteStream<MeshNode>` (forbidden — it does
not converge; see [CqrsAndContentAccess.md](CqrsAndContentAccess.md)). The one
API is `hub.GetMeshNodeStream(path)` / `workspace.GetMeshNodeStream(path)`, which
routes every cross-hub read and write through the shared `IMeshNodeStreamCache`
— one process-wide upstream per path, so reads and writes share the same live
mirror and the convergence rules above hold.

---

## 9. Invariants (the test ledger)

| Invariant | Guard / Test |
|---|---|
| Owner assigns strictly increasing versions per stream | `StreamVersionMonotonicityTest` |
| A subscriber never mints a version | `UpdateStream` adopt-only; `StreamUpdateIdentityTest` |
| Stale **patch** dropped; **Full** always applied | `SynchronizationStream.UpdateStream` guard |
| Disjoint concurrent string edits merge | `StringDeltaTest`, `StreamConflictResolutionTest` |
| Big string field ships only its splice | `StringDeltaPatchTest` |
| Out-of-sync subscriber can request a Full | `RequestFreshSnapshot` |

---

## 10. Mistakes this design exists to prevent

- **Stamping `Host.Version`** (or a subscriber's sync-hub) instead of the
  owner's stream clock → non-monotonic versions → the guard drops real updates
  → "view doesn't refresh / blank layout".
- **Guarding Fulls** → a roll-back / re-sync Full is dropped → a confused
  subscriber stays confused forever.
- **Sending whole entities / whole strings** → bandwidth blowup on large content.
- **Letting a subscriber mint versions** → two mirrors fight over ordering; last
  write wins on the whole node instead of a field-wise merge.
