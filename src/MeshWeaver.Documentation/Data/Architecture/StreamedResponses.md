---
NodeType: Markdown
Name: "Streamed Responses — a reply that arrives in parts"
Abstract: "hub.ObserveStreamed posts a request and emits every response part the handler sends, in sequence order, then the terminal reply, then completion. Parts are correlated like the reply, never consume the callback, restart the silence deadline, and are re-ordered by their number because a transport (a SignalR participant connection) may not preserve posting order. Opt-in on both ends: a handler sends parts only to a request stamped AcceptsResponseParts."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#1565c0'/><path d='M4 7h4M4 12h7M4 17h10' stroke='white' stroke-width='1.8' stroke-linecap='round'/><path d='M16 14l3 3-3 3' stroke='white' stroke-width='1.6' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Messaging"
---

> **Read first:** [Message Based Communication](../MessageBasedCommunication). A plain
> `hub.Observe(request, …)` waits for exactly ONE reply. This page is the other shape: a reply that
> arrives in parts.

## Why

The model relay (MeshWeaver.Plugins `Hosting/ModelRelay`) runs a language model on a person's own
machine. The machine answers over its participant connection, and a chat completion is a stream of
tokens that may take minutes. One reply at the end would make the reader wait for the whole answer,
and a total-duration bound would cut a long answer that is still arriving. The mesh's request/response
contract had one terminal reply per request (an `AsyncSubject`), with `RequestProgress` as the only
non-terminal message — and progress carries no payload.

## The contract

```csharp
// Caller — cold: the request is posted on Subscribe.
hub.ObserveStreamed(new MyRequest(…), o => o.WithTarget(address))
   .Subscribe(delivery =>
   {
       if (delivery.IsResponsePart()) Append((MyPart)delivery.Message);
       else Finish((MyReply)delivery.Message);          // the terminal reply, emitted last
   }, error => …);                                       // DeliveryFailureException / TimeoutException

// Handler
.WithHandler<MyRequest>((hub, request) =>
{
    if (!request.AcceptsResponseParts())                 // a single-reply caller: answer ONCE
    {
        hub.Post(WholeReply(), o => o.ResponseFor(request));
        return request.Processed();
    }
    var n = 0L;
    produce.Subscribe(
        part => hub.PostResponsePart(request, part, n++),
        error => hub.Post(new DeliveryFailure(…), o => o.ResponseFor(request)),
        () => hub.PostFinalResponse(request, new MyReply(…), n));
    return request.Processed();
});
```

| Piece | What it does |
|---|---|
| `PostOptions.AcceptsResponseParts` | Stamped by `ObserveStreamed` on the request. A handler sends parts only to a request that carries it, so a caller that predates the feature (an older image during a roll) is never sent a part it would take for its reply. |
| `PostOptions.ResponsePart` | The part's zero-based number. A part is correlated like the reply (`RequestId`), never consumes the callback, and restarts the request's silence deadline. |
| `PostOptions.ResponsePartCount` | On the terminal reply: how many parts came before it. The stream completes only once that many parts were emitted, so a part that arrives after the reply is still delivered, in order. |

## The rules it keeps

- **Order is by number, not by arrival.** The SignalR participant forwards each delivery through the
  Http `IIoPool`, so two posts can overtake each other on the wire. The caller holds early parts until
  the gap closes and drops a duplicate (a number below the next one expected).
- **Silence, not duration.** The bound is `RequestTimeout` (set per hub with `WithRequestTimeout`)
  of SILENCE between elements. A long answer that keeps arriving is never cut; one that stops ends in
  a `TimeoutException` naming the request. It is not a raised bound: a stream that goes quiet is cut
  exactly as early as a single reply that never comes.
- **A broken part is a failure.** A part whose number cannot be read, or a reply whose part count
  cannot be read, faults the stream at once — it is never placed by guess or read as "no parts".
- **Batch the parts.** The storm breaker counts parts per REQUEST, so many streams at once between
  two hubs are fine; one stream above its threshold (2,000 within a second) is a storm and is cut. A
  producer sends parts of a useful size (the model relay's device sends up to 16 KB of the response
  per part), never one per byte.
- **A failure is a reply.** A `DeliveryFailure` answer faults the stream with
  `DeliveryFailureException`, after the parts that arrived before it.
- **The callback lives as long as the stream.** A streamed wait is not consumed by its reply (a part
  may still be in flight behind it); the entry is removed when the caller's stream completes, faults
  or is disposed.

## Not built

- **Cancellation towards the handler.** Disposing the caller's stream removes its callback, and every
  later part is consumed with nothing to deliver to; the handler is not told. A handler that produces
  for a long time bounds itself (the model relay's device does, with a declared maximum duration).
- **Back-pressure.** Parts are pushed as they are produced; a slow caller buffers them in memory for
  the life of one stream.

Pinned by `StreamedResponseTest` (MeshWeaver.Messaging.Hub.Test): parts in order then the reply,
parts posted in reverse re-ordered, a single-reply caller answered once and never sent a part, and a
stream that goes quiet before its reply timed out after the parts that did arrive.
