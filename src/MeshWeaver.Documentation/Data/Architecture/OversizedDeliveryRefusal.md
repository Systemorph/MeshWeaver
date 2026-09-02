---
Name: Oversized Delivery Refusal
Category: Architecture
Description: A message too large for its transport is refused at the producer, never dispatched — and the report about it, the acknowledgement of it and the log line describing it must never carry it. Why an oversized grain frame destroys a shared connection rather than one delivery, why the limit is never the thing to raise, and where the bound still cannot reach.
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

`MessageSizeGuard` (`src/MeshWeaver.Messaging.Hub/Serialization/MessageSizeGuard.cs`) measures a
routed delivery's packaged `RawJson` payload against a transport's bound. It is cheap on the hot
path — a UTF-16 char is at most 3 UTF-8 bytes, so `3 × Length < limit` is an O(1) proof that a
payload fits and the common case never scans — and exact only for a delivery that might be over.

It is applied at **four** producer sites — every leg on which a delivery travels FORWARD:

- `RoutingGrain.PostToStream` — the memory-stream leg, against the 1 MiB block.
- `RoutingGrain.RefuseOversizedGrainDispatch`, on **both** forward grain legs: `BuildGrainRoute`'s
  `IMessageHubGrain` call and `BuildPodHubRoute`'s `IPodHubGrain` call. Guarding only the first
  would leave the stream-routed half of forward traffic on the unguarded path — a control that
  covers one strand and misses the other.
- `OrleansRoutingService.DeliverMessage` — the router's own `IRoutingGrain.RouteMessage` call, which
  is strictly **upstream** of the other three (#2885, below).

Two more legs carry the same payload and are **not** producer legs at all, so no bound placed on the
way out could ever have reached them. Both are covered by rules rather than by measurements, and both
are below: the **failure report** (#3044/#3049) and the **acknowledgement** (#3045).

> **Why the guard lives in `MeshWeaver.Messaging.Hub` and is public.** It began beside the first
> three sites in `MeshWeaver.Hosting.Orleans`, but the fourth site is in
> `MeshWeaver.Connection.Orleans` — and `Hosting.Orleans` *references* that assembly, so the guard
> could not stay there without being copy-pasted into the other. It moved down to the assembly both
> routers already reference, beside the `RawJsonConverter` whose transcode is the allocation that
> fails. It is **public** rather than `internal` + `InternalsVisibleTo` for the reason
> `MessageStormBreaker` already documents: an assembly-wide `InternalsVisibleTo` on
> `MeshWeaver.Messaging.Hub` exposes the internal `IMessageHub.Observe(object, …)` as an *instance*
> method, which then outranks the public generic `Observe<TResponse>` extension in overload
> resolution and silently turns every `response.Message` in the granted assembly into `object`.

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
report. `DeliveryPayloadBounds.WithoutOversizedPayload` replaces the echo with a description of itself
(`MessageSizeGuard.WithoutOversizedPayload` is the same call under the name the routers already use);
the sender correlates a `DeliveryFailure` on `RequestId`, never on the echoed payload. It defaults to
the **tighter** of the two bounds, so one report is protected whichever way it travels back.

This is a second-order defect of the same shape, and it is why "just NACK it" is not a fix on its
own.

### And it is the RECORD's invariant, not a call site's

This rule shipped twice as a hand-applied call at one site each — `RoutingGrain.PostFailure` (#1890),
then `OrleansRoutingService.SendDeliveryFailure` (#2885). On 2026-09-02 it failed at a **third**:
`MessageService.ReportFailure`, the hub's own reporter, which every one of that burst's occurrences
ran through and which neither earlier fix had been told about.

There are around **twenty** `new DeliveryFailure(delivery)` sites in this repository, written over
years by people who had not read this page. "Remember to strip" was never a control — it was a
convention that two sites happened to follow. The strip is now applied inside `DeliveryFailure`'s own
construction, in `MeshWeaver.Messaging.Contract`, so a report cannot be built with an undeliverable
payload attached no matter which site builds it. The two explicit calls remain, idempotent, because
their surrounding comments carry the incident history.

> **Why the measurement moved to the contract assembly.** `MessageSizeGuard` lives in
> `MeshWeaver.Messaging.Hub` because both routers reference it (see the box above). `DeliveryFailure`
> lives one assembly lower, so a helper in the Hub assembly is unreachable from the one type whose
> invariant this is. The measurement itself is now `DeliveryPayloadBounds` in
> `MeshWeaver.Messaging.Contract`; `MessageSizeGuard` delegates to it and keeps its wording, its
> incident history and its public surface. This is the same move #2885 made one assembly earlier, for
> the same reason, and the alternative is the same one it rejected: a copy-paste of the check.

### The invariant was blind to half the traffic (#3104)

Making the strip a construction invariant fixed *which sites* it reaches. It did not fix *which
payloads* it can see, and the two are easy to conflate. `IsOversized` opens:

```csharp
if (delivery?.Message is not RawJson { Content: { } content })
    return false;
```

A payload that is not `RawJson` is never measured and never stripped. That test is **right on the
router hot path**, and its own doc says why: by the time a delivery reaches a transport `MeshBuilder`
has packaged it, so `RawJson` is the routed shape, and guessing at anything else's size would mean
serialising twice to answer a question that is almost always "no".

🚨 **That reasoning does not transfer to the strip.** The strip runs only while a failure report is
being made ready to travel — a rare path, where an exact measurement is the correct thing to pay for.
Inheriting the hot path's excuse there left the entire **pre-packaging** half of the mesh unmeasured,
and the construction invariant inherited the blind spot with it: it covers all ~25 sites, but only for
a payload that is already `RawJson`.

`AccessControlPipeline` is the case that bites, and it is not a corner. `[RequiresPermission]` is an
attribute on the message **type**, so the gate cannot evaluate a `RawJson` at all — its deliveries are
typed by construction. A permission denial therefore echoed a multi-megabyte body back verbatim, and
serialising it at the packaging seam is the same
`Utf8JsonWriter.TranscodeAndWriteRawValue` → `SharedArrayPool.Rent` → `GC.AllocateNewArray`
allocation that threw in #3049. Every in-process NACK is in the same position:
`MessageService.ReportFailure`, `MessageHub`'s unhandled and failed-state answers,
`HierarchicalRouting`'s NotFound.

**The measurement now takes options, and the strip is applied at the packaging seam.**
`DeliveryPayloadBounds.IsOversized(delivery, options, limit, out bytes)` keeps the `RawJson` branch
byte-for-byte as it was and serialises a typed payload only when options are supplied — into a
counting `IBufferWriter` that keeps nothing, because rendering the document to read its `Length` is
the very allocation this exists to prevent. A null `options` reproduces the old behaviour exactly, so
no caller is worse off. `MessageDelivery.Package` applies it to a `DeliveryFailure`'s echo before it
serialises.

Three things about that placement are deliberate:

- **The seam, not the call sites.** `Package` is the one place a delivery becomes its wire form and
  the one place a hub's `JsonSerializerOptions` are in hand. Applying the rule there covers the sites
  nobody has enumerated yet — which is the whole lesson of the section above, applied once more
  rather than re-learned.
- **Packaging, not construction.** A report that never crosses a boundary keeps its full echo. It
  costs nothing to carry in-process and is the better diagnostic; the strip exists to keep a report
  *deliverable*, never to redact it.
- **Unmeasurable is not "too big".** A payload the serializer refuses has an *unknown* size, and the
  honest answer to unknown is to echo it. Treating a failed measurement as oversized would be a
  fail-closed default that silently destroys the content of every NACK whose payload happens to be
  awkward to serialise — and it would buy nothing, since a payload that cannot be serialised cannot
  be packaged either and so never reaches this bound's wall.

Both seams are pinned by `DeliveryFailureEchoStripGuard`, which asserts the *seams* rather than the
call sites — a per-site scan would enforce exactly the convention this page records the failure of,
and could not see a site that reaches a report through a helper. It checks that the construction
invariant still runs the strip, that `Package` strips **before** it serialises (the order is the
assertion: stripping afterwards compiles, passes every functional test, and does nothing), that
`MeshBuilder` still routes through `Package`, and that the seam has exactly one implementation. Each
arm fails when its subject is missing rather than merely non-compliant.

## The fourth leg: the router's OWN grain call (#2885)

The three sites above all live **inside** `RoutingGrain`. But a delivery has to *reach* that grain,
and on a client or co-hosted portal it does so through one more grain call —
`OrleansRoutingService.DispatchObservable` → `IRoutingGrain.RouteMessage(delivery)`. That leg was
unguarded, and it is where production died:

```text
System.OutOfMemoryException
   at System.GC.AllocateNewArray(...)
   at System.Buffers.SharedArrayPool`1.Rent(Int32 minimumLength)
   at System.Text.Json.Utf8JsonWriter.TranscodeAndWriteRawValue(ReadOnlySpan`1 json, ...)
   at System.Text.Json.Serialization.Metadata.JsonPropertyInfo`1.GetMemberAndWriteJson(...)
   at System.Text.Json.Serialization.Converters.ObjectDefaultConverter`1.OnTryWrite(...)
   at System.Text.Json.Serialization.Metadata.JsonTypeInfo`1.SerializeAsObject(...)
   at MeshWeaver.Messaging.Serialization.MessageDeliveryConverter.Write(...)
```

Orleans serialises a grain call's **arguments** with the mesh's own System.Text.Json options — the
JSON codec is registered with `AddJsonSerializer(_ => true, _ => true, …)`, so it claims every type,
including the `IMessageDelivery` envelope. `MessageDeliveryConverter.Write` therefore writes the
whole envelope, one of whose properties is the packaged `RawJson`, and `RawJsonConverter` emits it
with `writer.WriteRawValue(value.Content)` — a UTF-16 `string`, so `Utf8JsonWriter` rents up to
**3 bytes per char** to transcode. That rent is the allocation that threw.

### Read the frame order — it says which half failed

This page previously concluded that *packaging itself* had exhausted memory, and therefore that "no
guard placed at the router can fix this". **That was wrong, and the stack is what refutes it.**
`MessageDelivery.Package(...)` serialises only the *payload* (`JsonSerializer.Serialize(message,
…)` → `new RawJson(serialized)`); it never invokes `MessageDeliveryConverter`, which converts the
`IMessageDelivery` **envelope**. So a stack that shows `MessageDeliveryConverter.Write` above
`GetMemberAndWriteJson` above `TranscodeAndWriteRawValue` proves the payload was **already a
materialised `RawJson` string in memory** when the failing allocation was attempted — packaging had
finished, and what blew up was re-serialising the packaged envelope for transport.

That distinction is the whole difference between "unreachable" and "one line away", because
`MessageSizeGuard.IsOversized` needs nothing but `RawJson.Content.Length`: the fast path is
`3 × Length < limit`, O(1) and **allocation-free**, so the guard can measure a payload that the very
next statement cannot transcode. The bound was never the problem — its **placement** was.

`OrleansRoutingService.DeliverMessage` now refuses at this leg before the dispatch, using the same
`MessageSizeGuard` and the live `IOptions<ClientMessagingOptions>.MaxMessageBodySize` (this assembly
is the client connection, and the client's own limit governs a client→silo call).

### The same asymmetry, one site later

`RoutingGrain.PostFailure` has stripped its NACK's echoed payload since #1890.
`OrleansRoutingService.SendDeliveryFailure` did **not** — it posted `new DeliveryFailure(delivery)`
with the payload attached. On this leg that is worse than a lost report: echoing the payload re-runs
the identical 3×-payload transcode, so the refusal becomes a *second* allocation failure. Both sites
now call `MessageSizeGuard.WithoutOversizedPayload`.

## The fifth leg: the report about the failure (#3044, #3049)

On 2026-09-02 one portal pod logged, four times in fourteen minutes:

```text
fail: MeshWeaver.Messaging.MessageService[0]
      Failed to post DeliveryFailure message for RawJson (ID: …) — breaking error cascade
      System.OutOfMemoryException
```

The sender was left with **neither the message nor the notification**: from its side the delivery
simply vanished and its `Observe(...)` waited out its budget. Two frames were recorded, and they are
two halves of one event — `JsonReaderHelper.TranscodeHelper` beneath `MessageService.Post` (#3044),
and `MessageDeliveryConverter.Write` → `Utf8JsonWriter.TranscodeAndWriteRawValue` →
`SharedArrayPool.Rent` → `GC.AllocateNewArray` (#3049).

Three defects, all on the reporting path, none of them the transport:

1. **The NACK carried the payload it was reporting on** — the section above; the site had never been
   told.
2. **`Post` rendered every delivery to JSON for a log line nobody read.** The line was

   ```csharp
   logger.LogDebug("Posting message {Delivery} …", JsonSerializer.Serialize(ret, LoggingSerializerOptions), …);
   ```

   A method argument is evaluated *before* the call, so the render ran on **every post in the
   process** and was then discarded by the logger whenever Debug was off — which in production it
   always is. This is the exact rule `LogSummary` was written for after the 2026-07-22 allocation
   storm (+3.9 GiB, 14k gen-0 GCs, 2.7 GB of live log strings in one test's heap): the hot Debug
   logs must never serialise a payload. `Post` is the hottest of all the sites that rule governs, and
   it was simply never converted. It now checks `IsEnabled(LogLevel.Debug)` first and renders
   `LogSummary`.
3. **`[PreventLogging]` did not reach `RawJson`.** `RawJson.Content` has carried the attribute since
   it was written, and its doc comment states the intent — "logging it in full is just re-dumping the
   message as a string". `LoggingTypeInfoResolver` implements that by removing properties from a
   resolved `JsonTypeInfo`, which requires `Kind == Object`. **A type claimed by a custom
   `JsonConverter<T>` has `Kind == None` and no properties at all**, so there was nothing to remove
   and `RawJsonConverter` went on emitting the body verbatim. The logging options now register a
   `LoggingRawJsonConverter` ahead of it, which writes the byte count and the head of the payload
   instead — so the cost of a log line no longer depends on the size of the message, and a log
   render can no longer be the thing that exhausts the pod.

> 🚨 **Marking a member of a custom-converted type `[PreventLogging]` silently does nothing.** That
> is the worst kind of control: the declaration reads as though the protection is in place. If a type
> has its own converter, the redaction has to be in a converter too.

**Why none of this is fixed by a bound.** An `OutOfMemoryException` *during serialisation* means the
allocation was the failure, so refusing the delivery afterwards is too late — and a `try/catch`
around it (which the log line above already is) turns a lost message into a lost message *plus* a
lost report. The fix is to stop making the allocation: a log nobody reads is not rendered, a payload
is never a log's content, and a report about a message does not carry the message.

## The sixth leg: the way BACK (#3045)

```text
fail: MeshWeaver.Connection.Orleans.OrleansRoutingService[0]
      Failed to deliver to AppleMusic/_Issue/1059
      System.OutOfMemoryException
         at Orleans.Serialization.JsonCodec…IDeepCopier.DeepCopy(Object input, CopyContext context)
         at Orleans.Serialization.Invocation.PooledResponseCopier`1.DeepCopy(Response`1 input, …)
         at Orleans.Runtime.InsideRuntimeClient.SafeSendResponse(Message message, Response response)
```

Every frame is on the **return** leg. Nothing in the request path failed; the callee could not send
its own answer, and the requesting hub simply never got a reply.

Five bounds existed by then and every one measures a delivery on the way **out**. None had asked what
the way **back** was carrying, and the answer was: the same payload, again. All three of the mesh's
Orleans delivery legs are declared `Task<IMessageDelivery>` and **return the delivery they were
handed, body included**:

| Leg | Returns | What the caller reads |
|---|---|---|
| `IRoutingGrain.RouteMessage` | `delivery.Forwarded(address)` | `State`, `SenderWasNacked`, `GetFailureMessage()` |
| `IPodHubGrain.Deliver` | `delivery.Forwarded(address)` | **nothing** — `BuildPodHubRoute` does `.Select(_ => Unit.Default)` |
| `IMessageHubGrain.DeliverMessage` | the hub's own result | `State`, `SenderWasNacked`, `GetFailureMessage()` |

Orleans copies a call's **result** with the same `JsonCodec` it copies its **arguments** with, so an
*n*-byte body cost *n* bytes outbound and *n* bytes inbound on every hop — to deliver a state word and
two properties. **Not one caller reads `Message`.**

`DeliveryPayloadBounds.WithoutEchoedPayload` replaces the body with a marker at all three return
points. State, id, sender, target, access context and every property survive; only the body — which
nobody reads — does not travel.

> 🚨 **Unconditional, not bound-conditional.** The NACK rule strips only what a transport provably
> cannot carry, because an echoed payload there is at least arguably diagnostic. This one has no such
> excuse: the acknowledgement's body is read by nobody **at any size**. Making the strip conditional
> on a bound would keep paying the full cost for every payload just under it — and just under the
> bound is exactly where this incident sat.

**The generalisable question**, and the one this family keeps re-learning: when a bound exists on one
leg, ask which other legs carry the same delivery. #2885 asked it about the leg that *reaches* the
guarded code. #3045 is the same question asked about the leg that *comes back from* it.

## Read the frame order to tell a copy from a hop

The 2026-09-02 burst was filed as six separate incidents because six different loggers recorded it.
Sorting them takes one rule: **the frames say which leg, and the leg says which fix.**

| Frames | Leg | Where the fix is |
|---|---|---|
| `RawJsonConverter.Read` / `MessageDeliveryConverter.Read` under `JsonCodec.DeepCopy` | the argument copy, inbound | the copy path (below) |
| `ObjectPolymorphicConverter.Write` under `Proxy_*.Deliver` | the argument copy, outbound | the copy path (below) |
| `…Write` under `Orleans.Runtime.Messaging.Connection` | the wire frame | the producer bound |
| `PooledResponseCopier` / `SafeSendResponse` | **the response** | #3045, above |
| `MessageService.Post` / `MessageDeliveryConverter.Write` under `JsonTypeInfo.SerializeAsObject` | **a log render** | #3044/#3049, above |

A stack under `Connection.ProcessOutgoing` and a stack under `PooledResponseCopier` are the same
payload at two different ends of one call, and they need different fixes. So do a serialisation and a
log render, which look identical until you read what is above `MessageDeliveryConverter.Write`.

## The copy path allocated ~6× the payload to copy it once

Both `JsonCodec` directions round-tripped through a **UTF-16 string** that nothing needed:

- **Reading** (`MessageDeliveryConverter.Read`): `JsonDocument.ParseValue` into pooled UTF-8 bytes,
  then `root.GetRawText()` — which transcodes the whole envelope into a fresh string (2× the bytes,
  on the large-object heap; this is `JsonReaderHelper.TranscodeHelper`) — then
  `JsonSerializer.Deserialize(string, …)`, which transcodes it straight **back** to UTF-8 into a
  rented buffer (up to 3×) and parses it a second time.
- **Writing** (`ObjectPolymorphicConverter.Write`, both discriminator branches):
  `JsonSerializer.Serialize(...)` to a string (2×, and its writer rents up to 3× to transcode the
  `RawJson` member into it), then `JsonDocument.Parse(string)` back to UTF-8 (up to 3× again) — all
  to inject one `$type` property ahead of the object's own.

`JsonElement.Deserialize` and `JsonSerializer.SerializeToUtf8Bytes` reach the same values from the
same UTF-8 bytes with **no transcode in either direction**, so a hop now costs about one buffer where
it cost four. That does not make the copy path allocation-safe — see the next section — but a large
share of the gap between "the bound says it fits" and "the pod could not allocate it" was
self-inflicted, and it did not have to be paid.

## Where the bound still cannot reach

Refusing at the frame limit is not the same as making the router allocation-safe, and the difference
is a multiple:

- The **bound** is the transport's frame limit (100 MiB by default).
- The **peak allocation** while transcoding is up to **3×** the payload, per direction.

So a payload comfortably *under* `MaxMessageBodySize` can still fail to allocate on a
memory-pressured pod, and no bound calibrated to the transport can prevent that. The #2885 incident
carries **no payload size at all**, and neither does the 2026-09-02 burst — so it cannot be shown
that any refusal would have caught those deliveries, only that the legs are now bounded and
attributable where before they were neither.

**What the 2026-09-02 work removed from that gap, and what it did not.** The multiple was never only
3×: the copy path spent another ~4× on UTF-16 round trips nothing needed, the acknowledgement made
the payload cross every boundary a second time, and a log render nobody read serialised it once more
per post. Those were self-inflicted and are gone. **The irreducible ~3× transcode remains**, and the
producer axis is untouched.

The remaining work is at the producer, and it is a different axis from everything on this page: *do
not build the payload whole.* Bulk producers — imports above all — must stream or batch rather than
serialise a tree into one delivery. A bound refuses a delivery *after* something has already
allocated it; an OOM during serialisation means the **allocation itself** was the failure, and no
placement of any bound changes that. Note that bounding *concurrency* (as the static-repo importer
already does, `BatchSize = 5`) is **not** that axis and does not help: it limits how many deliveries
are in flight, never how big one of them is.

> **This is why #3046 stays open** while #3047, #3048, #3049 and the rest of that burst do not. Its
> leg — `BuildPodHubRoute`'s `IPodHubGrain.Deliver` to a bulk-import hub — has been guarded since
> #2897 and the delivery went through anyway, which means the payload was *under* the bound and the
> allocation multiple is what killed the pod. Everything above narrows that band; only the producer
> closes it.

## Rules

- **Never raise a transport limit to make a payload fit.** Fix or refuse at the producer.
- **Never retry a size failure.** It cannot converge; classify it terminal so recovery machinery
  stands down.
- **A guard on one transport is not a guard on the system.** When a bound exists on one leg, ask
  which other legs carry the same delivery — *including the leg that reaches the guarded code*.
  Three of these four sites sat downstream of the one that actually failed.
- **Read the identical-size-on-a-new-port signature as one message, not many incidents.**
- **Read the FRAME ORDER before concluding a guard cannot reach a failure.** `Package` serialises
  the payload; `MessageDeliveryConverter` serialises the envelope. A stack showing the latter proves
  the payload was already a measurable `RawJson` string, so "packaging itself ran out of memory" was
  not what happened — and the difference decided whether #2885 was fixable at the router at all.
- **An acknowledgement is not an echo.** A reply carries the verdict; the body goes one way only.
  Before adding a `Task<T>` to a grain interface, ask what the caller reads off `T` — and whether the
  rest of it is worth a second copy on every hop.
- **A rule enforced by remembering is not enforced.** If a payload rule can be violated by
  constructing a type, it belongs in that type's construction, not in the call sites. Two hand-applied
  call sites and a third that had never been told is how #1890 became #3044 two weeks later.
- **Ask what a guard can SEE, not only where it runs.** Moving the strip to every site fixed *which
  sites*; it said nothing about *which payloads*, and the `is not RawJson` test at the bottom of it
  left every pre-packaging NACK unmeasured for as long again (#3104). A hot-path optimisation
  inherited by a rare path is an optimisation nobody chose.
- **A measurement on the error path must cost less than the thing it is deciding.** Count into a
  discarding buffer; never render the document to read its `Length`. The error path is where the
  process can least afford the allocation, which is precisely why the check was skipped there in the
  first place.
- **Never build a log argument eagerly.** `logger.LogDebug("…", Serialize(x), …)` renders `x`
  whether or not anyone is listening. Guard with `IsEnabled`, and on any per-message path render a
  summary rather than a payload.
- **A `[PreventLogging]`-style attribute cannot reach a type with its own converter.** The resolver
  strips *properties*; a custom-converted type has none. Check that a redaction attribute is actually
  read before trusting it.

## See also

- [Durable But Unreadable](/Doc/Architecture/DurableButUnreadable) — the *other* way an acknowledged write goes
  missing, and the three-seam test that tells them apart. A transport refusal is loud and terminal;
  that one is silent and leaves a version row behind. Do not diagnose one as the other.
- [Error Propagation and Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — surfacing rather than swallowing.
- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — finding the exact broken edge.
- [Orleans Stream Pub-Sub Durability](/Doc/Architecture/OrleansStreamPubSubDurability) — the neighbouring case where a
  publish with no subscriber succeeds.
