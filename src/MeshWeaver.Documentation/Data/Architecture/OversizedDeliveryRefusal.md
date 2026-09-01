---
Name: Oversized Delivery Refusal
Category: Architecture
Description: A message too large for its transport is refused at the producer, never dispatched. Why an oversized grain frame destroys a shared connection rather than one delivery, why the limit is never the thing to raise, and where the bound still cannot reach.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-6.219-8.56"/><path d="M12 8v4"/><path d="M12 16h.01"/><path d="m16 4 5 5"/><path d="m21 4-5 5"/></svg>
---

# Oversized Delivery Refusal

The mesh puts no inherent bound on the size of a single `IMessageDelivery` payload, but every
transport underneath it does. When a producer builds a message larger than the transport can carry,
the transport does not politely fail that one message — and on the grain path it does not fail it
*visibly* either.

**The rule: refuse at the producer what the transport provably cannot carry. Never raise the
limit.**

## Why an oversized grain frame is not merely a lost message

Orleans refuses the **frame**, not the message, and it does so inside the silo-to-silo connection's
*write loop*:

```text
Orleans.Runtime.Messaging.InvalidMessageFrameException:
  Invalid body size: 149199409 (max configured value is 104857600, see MaxMessageBodySize)
   at Orleans.Runtime.Messaging.MessageSerializer.ThrowInvalidBodyLength(Int32 bodyLength)
   at Orleans.Runtime.Messaging.MessageSerializer.Write(PipeWriter writer, Message message)
   at Orleans.Runtime.Messaging.Connection.ProcessOutgoing()
```

A serializer fault in `ProcessOutgoing` is not recoverable per-message, so Orleans **tears the whole
connection down**. Every unrelated delivery queued on that connection at that moment is collateral.
Orleans then reconnects and re-sends the same body, because nothing about retrying makes a message
smaller — *the size is a property of the message, not of the attempt.*

**The signature to recognise:** repeated occurrences carrying the **identical** byte count, seconds
apart, each from a **new local port**. That is one undeliverable message in a reconnect loop, never
several incidents. Read it that way and the blast radius stops being mysterious: it presents as *a
partition that stopped answering*, not as *one request that timed out*.

## The bounds, and why neither is ours to raise

| Transport | Bound | Configurable? |
|---|---|---|
| Orleans memory stream | 1 MiB — one fixed pooled-cache block per message, hard-coded in `MemoryAdapterFactory` | **No.** A cached message must fit one whole block. |
| Orleans grain call | `SiloMessagingOptions.MaxMessageBodySize` (default 100 MiB) | Yes — and that is exactly the trap. |

Raising `MaxMessageBodySize` moves the wall instead of removing it, and normalises frames that will
destroy a connection at the next size up. It is the textbook band-aid: a bigger bound to make a
symptom stop. The producer is what is wrong.

## The guard

`MessageSizeGuard` (`src/MeshWeaver.Hosting.Orleans/MessageSizeGuard.cs`) measures a routed
delivery's packaged `RawJson` payload against a transport's bound. It is cheap on the hot path — a
UTF-16 char is at most 3 UTF-8 bytes, so `3 × Length < limit` is an O(1) proof that a payload fits
and the common case never scans — and exact only for a delivery that might be over.

It is applied at three producer sites, all in `RoutingGrain`:

- `PostToStream` — the memory-stream leg, against the 1 MiB block.
- `RefuseOversizedGrainDispatch`, on **both** forward grain legs: `BuildGrainRoute`'s
  `IMessageHubGrain` call and `BuildPodHubRoute`'s `IPodHubGrain` call. Guarding only the first
  would leave the stream-routed half of forward traffic on the unguarded path — a control that
  covers one strand and misses the other.

The grain bound is read from the **live** `IOptions<SiloMessagingOptions>`, not from a constant
compiled in here, so a deployment that deliberately tuned its limit is measured against its own
number and can never be falsely refused. The constant is a fallback for a host that registered no
messaging options, and a test pins it against the real `new SiloMessagingOptions().MaxMessageBodySize`
so an Orleans upgrade that moves the default fails loudly instead of silently mis-tuning the router.

**A refusal is terminal** (`ErrorType.Rejected`), so the sender's `Observe(...)` fires `OnError` at
once rather than waiting out its budget, and its recovery machinery stands down instead of retrying
something that cannot converge. The log line names target, byte count, limit, delivery id, sender and
the **head of the payload** — the `$type` discriminator sits at the front of the JSON, which is what
makes the *producer* identifiable. `InvalidMessageFrameException` knows a body length and an endpoint
pair, and nothing about the message; that gap is the whole reason the guard logs what it logs.

> **Refusing can only turn a silent loss into a loud one.** Both bounds are the transport's own, so
> nothing that is delivered today is newly refused. It is deliberately *not* an exact admission
> test: the on-wire form carries an envelope the guard cannot see, so a payload just under the bound
> can still be rejected inside Orleans. That residual band is unchanged — the gross case is what the
> guard exists to attribute.

## 🚨 A NACK about an oversized message must not BE one

`DeliveryFailure` embeds the **original** delivery, payload and all, and travels back to the sender
over the same transport. So a failure report about a 142 MB message is itself a 142 MB message and
dies at exactly the wall it is describing — leaving the producer with neither the message nor the
report. `MessageSizeGuard.WithoutOversizedPayload` replaces the echo with a description of itself
before the report is posted; the sender correlates a `DeliveryFailure` on `RequestId`, never on the
echoed payload. It defaults to the **tighter** of the two bounds, so one call protects a report
whichever way it travels back.

This is a second-order defect of the same shape, and it is why "just NACK it" is not a fix on its
own.

## Where the bound cannot reach

The guard measures a delivery **after** `MeshBuilder` has packaged it (`delivery.Package(...)`),
because `RawJson` is the routed shape. A payload so large that *packaging itself* exhausts memory
never reaches a measurable form:

```text
System.OutOfMemoryException
   at System.Buffers.SharedArrayPool`1.Rent(Int32 minimumLength)
   at System.Text.Json.Utf8JsonWriter.TranscodeAndWriteRawValue(ReadOnlySpan`1 json, ...)
   at MeshWeaver.Messaging.Serialization.MessageDeliveryConverter.Write(...)
```

`MessageDeliveryConverter` is the victim here, not the culprit — transcoding a raw value UTF-16 →
UTF-8 rents a buffer of up to 3× the string length, so the peak is several times the payload. **No
guard placed at the router can fix this.** The answer is at the producer: *do not build the payload
whole.* Bulk producers — imports above all — must stream or batch rather than serialise a tree into
one delivery. Note that bounding *concurrency* (as the static-repo importer already does) is a
different axis and does not help.

## Rules

- **Never raise a transport limit to make a payload fit.** Fix or refuse at the producer.
- **Never retry a size failure.** It cannot converge; classify it terminal so recovery machinery
  stands down.
- **A guard on one transport is not a guard on the system.** When a bound exists on one leg, ask
  which other legs carry the same delivery.
- **Read the identical-size-on-a-new-port signature as one message, not many incidents.**

## See also

- [Durable But Unreadable](/Doc/Architecture/DurableButUnreadable) — the *other* way an acknowledged write goes
  missing, and the three-seam test that tells them apart. A transport refusal is loud and terminal;
  that one is silent and leaves a version row behind. Do not diagnose one as the other.
- [Error Propagation and Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — surfacing rather than swallowing.
- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — finding the exact broken edge.
- [Orleans Stream Pub-Sub Durability](/Doc/Architecture/OrleansStreamPubSubDurability) — the neighbouring case where a
  publish with no subscriber succeeds.
