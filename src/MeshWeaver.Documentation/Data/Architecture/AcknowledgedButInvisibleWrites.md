---
Name: Acknowledged But Invisible Writes
Category: Architecture
Description: The most dangerous shape in the mesh — a write that reports success and cannot be read back. The two independent mechanisms that produce it (an untransportable delivery, and a durable row whose announcement was lost), how to tell them apart, and the producer-side bound that closes the first.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94"/><path d="M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19"/><path d="m1 1 22 22"/></svg>
---

# Acknowledged But Invisible Writes

**A write that reports success and does not land is the most dangerous shape in this codebase.** It
defeats every downstream defence: the caller moves on, an import manifest records the file as done,
a retry short-circuits as "already imported", and the loss is discovered days later by a human who
notices content missing.

This page is the field guide to that shape. It documents **two independent mechanisms** that both
present as *acknowledged, durably versioned, permanently unreadable* — and the measurement that
tells them apart in one step, because they need opposite fixes.

> The invariant this page defends: **an acknowledged write must be readable afterwards, or the
> acknowledgement must not have been given.** Anything else is a lie the rest of the system builds
> on.

## The one measurement that discriminates

Ask the two read surfaces separately. They read *different tables*, and which one is empty names the
mechanism:

| `get_versions {path}` | `get {path}` / children listing | Mechanism |
|---|---|---|
| **no rows** | not found | The write genuinely never happened — an ordinary, honest failure. Look for the NACK. |
| **row at v1** | **not found** | The row is durable and its **announcement was lost** — mechanism B below. |
| row at vN | found, stale content | An ordinary replication lag; wait, or read the node's stream instead of a query. |

The second line is the dangerous one. `get_versions` reads the node **history**, written by a
database trigger on the row itself; `get` and every `scope:children` listing read the **current-state
row** through the live query path. A history row with no readable current row proves the write
committed and the mesh was never told.

## Mechanism A — the delivery could not be transported

**Tell:** an `InvalidMessageFrameException: Invalid body size: … (max configured value is …, see
MaxMessageBodySize)` out of `Connection.ProcessOutgoing`, or an `OutOfMemoryException` beneath
`Utf8JsonWriter` inside `MessageDeliveryConverter.Write`. Repeating occurrences carry the
**identical** byte count, seconds apart, each from a **new local port**.

That signature is one undeliverable message in a reconnect-and-retry loop — never several incidents.
The size is a property of the message, not of the attempt, so the retry cannot converge.

### Why an oversized grain frame is not merely a lost message

Orleans refuses the **frame**, inside the silo-to-silo connection's *write loop*. A serializer fault
there is not recoverable per-message, so Orleans tears the whole connection down and reconnects.
Everything unrelated that was queued on that connection dies with it. That is why a single oversized
payload presents as *a partition that stops answering* rather than as *one request that timed out* —
and why it is easy to misread as a much broader outage.

The memory-stream twin of this defect could only lose itself. The grain-call one takes a shared
connection with it.

### The cure is a producer-side bound, never a bigger limit

`MaxMessageBodySize` is configurable, which is exactly the trap: raising it makes 142 MB frames
normal traffic and moves the wall rather than removing it. The mesh instead **refuses at the
producer** what the transport provably cannot carry:

- `MessageSizeGuard` (`src/MeshWeaver.Hosting.Orleans/MessageSizeGuard.cs`) measures a routed
  delivery's packaged `RawJson` payload against a transport's bound. Cheap on the hot path
  (`3 × Length < limit` is an O(1) proof that a payload fits), exact only when it might be over.
- `RoutingGrain.PostToStream` applies it to the **memory-stream** leg against Orleans' hard-coded
  1 MiB pooled-cache block.
- `RoutingGrain.RefuseOversizedGrainDispatch` applies it to **both forward grain legs** — the
  `IMessageHubGrain` call and the `IPodHubGrain` call — against the **live**
  `SiloMessagingOptions.MaxMessageBodySize`, so a deployment that legitimately tuned the limit is
  measured against its own number and never falsely refused.

A refusal is terminal (`ErrorType.Rejected`): the sender's `Observe(...)` fires `OnError` at once
instead of waiting out its budget on a message that can never land. The log line carries the target,
the byte count, the limit, the delivery id, the sender and the head of the payload — the `$type`
discriminator sits at the front of the JSON, which is what makes the *producer* identifiable. That
is the fact the Orleans-side exception cannot supply: it knows a body length and an endpoint pair,
and nothing about the message.

**Refusing can only turn a silent loss into a loud one.** Both bounds are the transport's own, so
nothing that is delivered today is newly refused. It is deliberately not an exact admission test —
the on-wire form carries an envelope the guard cannot see, so a payload just under the bound can
still be rejected inside Orleans. That residual band is unchanged; the gross case is what the guard
exists to attribute.

### 🚨 A NACK about an oversized message must not BE one

`DeliveryFailure` embeds the **original** delivery, payload and all, and travels back to the sender
over the same transport — so a failure report about a 142 MB message is itself a 142 MB message and
dies at exactly the wall it is describing. `MessageSizeGuard.WithoutOversizedPayload` replaces the
echo with a description of itself before the report is posted; the sender correlates a
`DeliveryFailure` on `RequestId`, never on the echoed payload. It defaults to the **tighter** of the
two transports, so one call protects a report whichever way it travels back.

### Where the bound still does not reach

The guard measures a delivery **after** `MeshBuilder` has packaged it. A payload so large that
*packaging itself* exhausts memory — an `OutOfMemoryException` at `GC.AllocateNewArray` /
`SharedArrayPool.Rent` beneath `Utf8JsonWriter.TranscodeAndWriteRawValue`, inside
`MessageDeliveryConverter.Write` — fails one step earlier, before there is a `RawJson` to measure.
The durable answer for that case is not a bigger buffer but **not building the payload whole**:
bulk producers (imports above all) must stream or batch, not serialise an entire tree into one
`IMessageDelivery`.

## Mechanism B — the row landed and the announcement was lost

**Tell:** the discriminating measurement above — a history row exists, the current-state read says
*not found*, and it stays that way across recycles.

The durable write and the mesh-wide announcement are **two separate steps**. The row goes to storage;
`IMeshChangeFeed` separately tells the running mesh the path now exists. Everything that decides
*reachability* keys off that announcement, not off the row:

- `PathResolutionService` caches path resolution. A path it has cached as a miss **stays** a miss —
  routing answers `No node found at '…'` for the life of the process.
- A live `scope:children` listing runs its SQL **once**, at `Initial`, and thereafter re-queries only
  when a change notification arrives and passes the relevance gate. Consumers share that snapshot
  through `Replay(1)`. One lost notification freezes the listing permanently — a point read can
  return the node while the listing never will.

So an announcement lost anywhere between the commit and the subscriber produces exactly the
signature: durable, versioned, unreadable, and stable under retry. `MeshNodeTypeSource`'s own class
documentation names this the **announce-loss class** and is the best in-repo description of it.

**Consequences worth internalising:**

- **Recycling the owning hub does not heal it.** The state is durable, not per-activation; the
  rebuilt hub re-derives the same missing announcement.
- **A content-addressed import manifest latches the false green.** A per-file record written on an
  acknowledged-but-invisible write means a later re-run reports `Imported 0 node(s)` and `force`
  never retries — the loss becomes permanent *and* invisible to the tool meant to repair it. An
  import must record per-file success on a **read-back-visible** write, never on the acknowledgement
  alone.
- **A wrapper activity must not out-rank its attempt.** `ImportedWithErrors` reported as `Succeeded`
  is the gate-that-cannot-fail shape from [Reading CI Signals](/Doc/Architecture/ReadingCiSignals),
  applied to data.

## Rules that follow

- **Never treat an acknowledgement as proof of visibility.** Where it matters — an import, a
  migration, an install — read the write back through the surface a *reader* uses before recording
  success.
- **Never conclude "no durable row" from the serving API.** `get`/`search` answer from the current
  state; only the history (or SQL) distinguishes "no row" from "row present, unannounced". This is
  the same trap a static node shadowing a durable row sets.
- **Never raise a transport limit to make a payload fit.** Fix the producer, or refuse at the
  producer. See [No band-aids](/Doc/Architecture/ErrorPropagationAndWedges).
- **Never retry a size failure.** It cannot converge; classify it terminal so the caller's recovery
  machinery stands down.
- **Two symptoms in one window are not one mechanism.** A and B co-occurred on the same partition in
  the same incident, three days apart in origin, and needed opposite fixes. Date the *first* write,
  not the loudest log line.

## See also

- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — finding the exact broken edge.
- [Error Propagation and Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — surfacing rather than
  swallowing, and the storms a swallowed failure becomes.
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a query is the wrong read
  for a specific node, and right for a listing.
- [Data Access Patterns](/Doc/Architecture/DataAccessPatterns) — the one mutation API and the read
  surfaces.
