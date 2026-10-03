---
Name: Durable Streams
Category: Architecture
Description: "A durable stream is a set of saved mesh nodes — a stream node that orders and leases it, one item node per appended item — consumed inside a hub with an acknowledged checkpoint. Exactly once relative to acknowledged items across recycles, rolls, takeovers and races; the lease IS the subscription; an orphaned stream wakes its owner. The hub pattern as code, why there is no separate queue store, the guarantees and the limits."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16M4 12h16M4 18h10"/><circle cx="19" cy="18" r="2"/></svg>
---

# Durable Streams

> **Read with:** [Durable Streams Are Mesh Nodes](../DurableStreamsViaMeshNodes) (why the mesh, not a
> stream provider, is the durable channel) · [Hub Disposal Model](../HubDisposalModel) (why an item
> in flight survives a recycle) · [Asynchronous Calls](../AsynchronousCalls) (everything below is
> `IObservable`, nothing is `async`).

A **durable stream** is an append-only, per-stream ordered sequence of items that survives a process
restart, a silo roll and a hub recycle, and is **consumed inside a hub**: a hub declares in its
configuration that it consumes a stream, every activation of that hub resumes where the last one
stopped, and each item is handed to the handler on the hub's own message loop.

**A durable stream IS saved mesh nodes.** Nothing about it lives anywhere else:

```
{Key}/_DurableStream/{Namespace}                                     DurableStream      owner's mesh_nodes
{Key}/_DurableStream/{Namespace}/_DurableStreamItem/000000000001     DurableStreamItem  owner's durable_stream_items
{Key}/_DurableStream/{Namespace}/_DurableStreamItem/000000000002     DurableStreamItem
…
```

- `DurableStreamId(Namespace, Key)` — `Key` is the mesh path of the node that **owns** the stream (a
  document, a job); `Namespace` is the stream family (`DocumentLog`). Instance-wide streams use a key
  under `Admin`.
- The **stream node** (`DurableStreamState`) carries the order and the ownership: `LastSequence`,
  the lease (`Subscriber`, `LeaseEpoch`, `SubscribedAt`), the acknowledged `Checkpoint` and
  `CheckpointAt`, and the orphan stamp (`OrphanedAt`, `LastSubscriber`).
- Each **item** is a write-once node whose id is its sequence, zero-padded (`000000000042`), carrying
  `Sequence`, `PayloadJson` (the producer's runtime type, kept as text so a polymorphic `$type` stays
  first) and `AppendedAt`.
- Both are **satellites of the owner**, so they live in the owner's partition — a Space's streams in
  that Space's schema, instance-wide streams in `Admin`'s — and are readable exactly by those who may
  read the owner. Items get their own table, `durable_stream_items`, via
  `SatelliteTableMapping("_DurableStreamItem", …)`; every partition schema gets it from
  `public.ensure_partition_schema`, which is generated from the mapping list. The segment is 18
  characters on purpose: placement picks the **longest** mapped segment in a path, and the items of a
  stream owned by a thread message (`_ThreadMessage`, 14) or a document part (`_DocumentPart`, 13)
  must still land in `durable_stream_items`.

## Producing

```csharp
hub.PublishDurable(new DurableStreamId("DocumentLog", documentPath), new DocumentLogAppend(text, offset))
   .Subscribe(sequence => …, ex => logger.LogWarning(ex, "append failed"));
```

`PublishDurable` is cold. It sends `AppendDurableStreamItemRequest` to the **stream node's hub**, which
runs every append, claim, acknowledgement and release through **one serial chain**: it creates the item
node `{seq:D12}`, records `LastSequence`, and only then answers with the sequence. When the publish
emits, the item is a saved mesh node. The stream node is created on first use, once per stream per
process (`DurableStreamDirectory`).

🚨 **Ensure before you route.** A request routed to a stream node that does not exist yet is answered
NotFound, and on the Orleans route that refusal outlived the node's creation: a resend right after a
successful create was refused the same way (measured on the Orleans TestCluster). That is why the
directory creates the stream node *before* the first request instead of discovering it by routing to
it.

## Consuming inside a hub

```csharp
// in the NodeType's (or any hub's) configuration
config.WithDurableStreamConsumer<DocumentLogAppend>(
    "DocumentLog",
    hub => hub.Address.Path,                                // the owner whose stream this hub consumes
    (hub, item) => hub.GetMeshNodeStream(hub.Address.Path)  // runs ON THE HUB'S LOOP
        .Update(node => Append(node, item.Payload, item.Sequence))
        .Select(_ => Unit.Default));                        // complete = done; then it is acknowledged
```

On every activation the consumer:

1. **Claims** the stream (`ClaimDurableStreamRequest`). The stream node's hub grants the lease when the
   stream is free or the claimant already holds it. Every grant starts a **new epoch**.
2. **Reads** the items strictly after the acknowledged `Checkpoint`. One synced query over the items'
   namespace provides both halves: its initial result is the backlog, its later `Added` changes are
   the live tail (the mesh change feed, Postgres `LISTEN` across pods). Items are released strictly
   in sequence. One that arrives ahead of a missing predecessor waits for it; one at or below the
   cursor is a duplicate and is dropped.
3. **Hands each item to the hub as a request to itself** (`DurableStreamTurn`), so the handler runs on
   the hub's action block, serially with its messages. Once the handler's observable completes, the
   turn **acknowledges** the sequence (`AckDurableStreamRequest`, carrying the epoch) and replies. Only
   then is the next item handed over.
4. **Releases** the lease when the hub shuts down, once the item in flight is done. That is the
   **orphan event**: the stream node's `Subscriber` clears, `OrphanedAt` is stamped and
   `LastSubscriber` is named.

A document's existing `_DocumentPart/{index:D6}` children can be consumed as a stream **without a
second copy**, by passing a `DurableStreamSource<T>` (items namespace, id → sequence, node → payload):

```csharp
config.WithDurableStreamConsumer<DocumentPart>(
    "DocumentLog",
    hub => hub.Address.Path,
    hub => new DurableStreamSource<DocumentPart>(
        DocumentPartPaths.PartNamespace(hub.Address.Path),
        node => DocumentPartPaths.TryParsePartIndex(node.Id, out var i) ? i + 1 : null,
        (node, options) => node.ContentAs<DocumentPart>(options)!),
    (hub, part) => …);
```

The lease and the checkpoint still live on `{document}/_DurableStream/DocumentLog`. Only the items are
read from where they already are.

A plain, unleased reader is `hub.ReadDurable<T>(stream, afterSequence)`: same ordering, no
acknowledgement.

## The lease IS the subscription

| question | where the answer is |
|---|---|
| does the stream have a live subscriber, and who? | `DurableStreamState.Subscriber` (+ `SubscribedAt`, `LeaseEpoch`) |
| how far has it got, and when did it last advance? | `Checkpoint`, `CheckpointAt` |
| did the last subscriber leave? | `Subscriber == null`, `OrphanedAt`, `LastSubscriber`. **Watch the stream node**: its change is the event |
| who decides between two would-be subscribers? | the stream node's own hub. Claims are serialized and exactly one is granted |
| can a subscriber that lost the lease still advance the checkpoint? | no. An acknowledgement carrying an old epoch is **fenced** |

**Takeover.** A claim against a held lease makes the stream's hub ask the holder for a sign of life
(`PingRequest`, `DurableStreamHub.HolderProbeBudget` = 5 s). It grants the lease to the new claimant
(new epoch, logged as `TOOK OVER`) only when the holder cannot be reached. Reaching a **node** address
activates its hub, so a node that owns a lease always answers. Ownership stays with the address, and
only an address no silo can serve any more (a gone portal or worker) loses it. A refused consumer
claims again on the orphan event, on a wake, or every `ReclaimInterval` (30 s). That interval is the
upper bound on taking over from a holder that died without releasing.

**Wake.** An append to an orphaned stream, or a release that leaves unacknowledged items, makes the
stream's hub **wake the owner** (`Key`). The wake is a **read of the owner node**, not a message: a
recycled owner is still quiescing for a moment after it released, and refuses every new message then.
The mesh read re-probes that refusal until a fresh activation answers, and the activation starts the
consumer. A `DurableStreamWake` then nudges a consumer that was waiting for the lease.

## Guarantees

- **Durable on append**: the producer hears the sequence only after the item node is saved.
- **Ordered, contiguous sequences per stream**: the stream's hub assigns them serially. After its own
  recycle it re-derives the next one from **both** `LastSequence` and the highest stored item, so an
  activation that died between creating an item and recording it never hands a sequence out twice.
- **Exactly once relative to acknowledged items**: an acknowledged item is never handed over again, and
  that holds across a recycle of the consumer, a recycle of the stream's hub, a takeover from a dead
  subscriber and a race between two subscribers.
- **A recycle stops at an item boundary**: no new item starts once the hub is shutting down. The item
  in flight is a pending request, so the hub's quiesce phase waits for its handler and its
  acknowledgement. The acknowledgement and the release are issued from the **mesh's read-issuing hub**
  (`hub.GetMeshHub().ReadIssuingHub()`), never from the quiescing consumer, because a quiescing hub
  refuses to send new requests (measured: `cannot process AckDurableStreamRequest … RunLevel=Quiescing`).
- **Failures stop at the item**: a handler that errors, a payload that cannot be read, a refused
  acknowledgement. The consumer logs it naming the stream and the sequence, does not acknowledge, and
  starts over from the claim after a backoff (1 s doubling to 60 s). The item is retried, never
  skipped.

## Limits

- **The at-least-once window.** A process that dies between a handler completing and its
  acknowledgement landing, or a handler still running when a recycle's quiesce budget (2 s by
  default) runs out, hands that one item over again. A handler whose effect must be exactly once even
  then records `item.Sequence` in the same write as its effect and skips a sequence it has applied.
- **One subscriber per stream.** The lease is exclusive by design. Fan-out to several independent
  consumers is several stream families, or plain readers (`ReadDurable`).
- **Retention.** Items are kept. Nothing trims a stream yet, so a stream's item table grows with it.
  Acknowledged items are safe to delete; a trim policy is follow-up work.
- **Catch-up reads the whole items namespace.** The resume filters by sequence after the query, so
  catching up a very long stream costs one full read of it.
- **Access.** Appends and claims are not yet checked against the caller's rights on the owner. The
  stream's nodes are written as the platform, and readers are gated by the owner's grants. Producers
  are platform code today.
- **A consumer runs only while its hub is active.** An item for a stream nobody holds waits in the
  mesh until the wake activates the owner. For a stream whose `Key` is not an activatable node, the
  consumer has to be on an always-on hub.

## Why nodes and not a stream provider

Every alternative needs a second store with its own sequence, retention, access model and recovery
path. Those are exactly the properties the mesh already has for nodes. The durable part is the item
node; ordering and ownership are decided by one hub. Delivery rides the change feed every pod already
runs. An Orleans persistent-stream provider over a separate queue table was built first and dropped in
favour of this: it duplicated the store, needed its own offsets per cluster and queue, and still needed
the store for catch-up. On Orleans the stream node and the consuming node are grain-hosted hubs like
any other, so the same hub code runs unchanged on the Monolith and the Distributed portal.

## How to check a live cluster

```
# lease grants, takeovers, orphans — the stream hub's own sentences
{namespace="<namespace>"} |= "[DurableStreams]"

# a consumer that stopped on an item (it retries; a repeating line is a poison item or a broken handler)
{namespace="<namespace>"} |= "Durable stream consumer" |= "was NOT acknowledged"
```

A stream's state is its node: `get @{Key}/_DurableStream/{Namespace}`. Its items are
`search namespace:{Key}/_DurableStream/{Namespace}/_DurableStreamItem`.

## Pinned by

`DurableStreamConsumerTest` (Monolith, six cases) and `OrleansDurableStreamConsumerTest` (Orleans
TestCluster: grain-hosted consumer and stream hub, client producer). Both were falsified when they
landed. Without the acknowledgement the recycle case re-processed items, and with live-only delivery
(the memory-stream shape) every case lost items.
