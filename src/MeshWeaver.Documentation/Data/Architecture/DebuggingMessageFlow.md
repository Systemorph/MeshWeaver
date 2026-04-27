# Debugging Message Flow & Hangs

When a hub-handler "looks like a deadlock" (test times out, response never comes back), don't bisect blindly and **don't rerun the test 2-3 times to see if it sticks**. The framework already emits a structured trace at `Trace` level — turn it on, run **once**, grep, fix.

## One-shot recipe

1. **Crank logging to Trace** in `test/appsettings.json`:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "Default": "Information",
         "MeshWeaver.Messaging": "Trace",
         "MeshWeaver.Data.Serialization": "Trace",
         "MeshWeaver.Mesh": "Debug",
         "MeshWeaver.Hosting.Persistence": "Debug",
         "Microsoft": "Warning",
         "System": "Warning"
       }
     }
   }
   ```

2. **Run the failing test once**, redirecting all output to a file:
   ```bash
   dotnet test test/<Suite> --no-build --filter "FullyQualifiedName~<TestName>" \
       > /tmp/trace.log 2>&1
   ```

3. **Grep the structured tags**:
   ```bash
   grep -E "MESSAGE_FLOW|SYNC_STREAM|exception occurred|deliveryId" /tmp/trace.log
   ```

If the test hangs in the test runner you also have per-test files at
`test/<Suite>/bin/Debug/net10.0/test-logs/<TestClass>_<TestMethod>.log` — the
xUnit test logger captures everything that happened during that test method.

## Where the structured tags come from

| Tag | Source | What it tells you |
|---|---|---|
| `MESSAGE_FLOW: Unpacking message …` | `MessageService.cs:238` | A message was unpacked (decoded) at this hub |
| `MESSAGE_FLOW: ROUTING_TO_HIERARCHICAL …` | `MessageService.cs:247` | Hub is delegating routing to its parent / hierarchy |
| `MESSAGE_FLOW: HIERARCHICAL_ROUTING_RESULT …` | `MessageService.cs:251` | Routing returned — see the `State` to know if the message was forwarded, processed, or failed |
| `MESSAGE_FLOW: ROUTING_TO_LOCAL_EXECUTION …` | `MessageService.cs:303` | Hub recognised itself as the target and is invoking handlers |
| `Buffering message …` | `MessageService.cs:184` | Hub isn't initialised yet — message went into the deferred buffer |
| `Deferring on-target message …` | `MessageService.cs:294` | Hub got the message but a `WithInitializationGate` is still closed |
| `Allowing message … through gate …` | `MessageService.cs:282` | Specific gate predicate let the message through pre-init |
| `Cancelling execution pipeline …` | `MessageService.cs:373` | Hub is shutting down — anything in flight gets cancelled |
| `An exception occurred during the processing of MessageDelivery …` | `MessageHub.cs` | A handler threw — full delivery payload + stack are dumped here. **This is your prime suspect when a request seems to vanish.** |
| `No handler found for request <T> in <Address>` | `MessageHub.cs:369` | Hub received the message, no handler matched — DeliveryFailure is sent back |
| `DeserializeDelivery: Could not deserialize message in hub <addr> — type '<T>' is not registered in this hub's TypeRegistry.` | `MessageService.cs:DeserializeDelivery` | Receiving hub fell back to `JsonElement` because the inbound `$type` discriminator isn't registered. A `DeliveryFailure` is posted back to the sender with the same text. Fix: add `WithType(typeof(T), nameof(T))` to the receiving hub's config. |
| `SYNC_STREAM …` | `JsonSynchronizationStream.cs` | Cross-hub workspace-stream traffic. Look here when a `GetRemoteStream<>` subscription seems to never emit. |

## Reading the trace

For a request-response hang, the canonical timeline looks like:

```
[T0]  MESSAGE_FLOW: Unpacking …                           (request arrives at Hub A)
[T0]  MESSAGE_FLOW: ROUTING_TO_HIERARCHICAL Target=B …
[T1]  MESSAGE_FLOW: Unpacking … Hub=B
[T1]  MESSAGE_FLOW: ROUTING_TO_LOCAL_EXECUTION Hub=B
[T2]  <handler logs at Information level>
[T3]  MESSAGE_FLOW: Unpacking …                           (response arrives back at Hub A)
[T3]  hub.RegisterCallback fires → IObservable completes  (caller's await returns)
```

When the test times out, find the **last** `MESSAGE_FLOW:` line that fired and look at what should have happened next:

- **No "Unpacking" at the target hub at all** — message was lost in routing. Check `HIERARCHICAL_ROUTING_RESULT` for the state.
- **"Unpacking" but no "ROUTING_TO_LOCAL_EXECUTION"** — message got deferred (gate closed) or buffered. Check for `Deferring on-target` / `Buffering`.
- **"ROUTING_TO_LOCAL_EXECUTION" but no handler logs** — handler ran but threw. Check for `An exception occurred during the processing of MessageDelivery` — the framework prints the full delivery + stack.
- **Handler logged success but caller still hangs** — the response wasn't routed back. Look for the response posted with `o.ResponseFor(request)` and find a matching `MESSAGE_FLOW: Unpacking` at the original Sender's address.

## The cross-hub border — `JsonSynchronizationStream`

`workspace.GetRemoteStream<TReduced, TReference>(addr, ref)` subscribes via a
`SubscribeRequest` posted to the owning hub. If the owning hub doesn't have a
handler for `SubscribeRequest` (or the corresponding reducer), the subscription
sends `DeliveryFailure` back instead of `SubscribeResponse`. The
`SynchronizationStream` then errors out — look for:

```
[Warning] [MeshWeaver.Data.Serialization.SynchronizationStream] Stream <id>
   received DeliveryFailure: No handler found for message type SubscribeRequest
```

This is the smoking gun for the "remote read returns nothing" class of hang.
The fix is on the owning side (register `SubscribeRequest` handler / register a
`MeshDataSource` so the hub has the reducer), not on the caller side.

## Type-registry mismatch (sender ≠ receiver)

The handler is registered, but the message arrives at the receiving hub as
`JsonElement` because the receiver's `ITypeRegistry` is missing
`WithType(typeof(T), nameof(T))`. Symptom on the sender today is a clean
`DeliveryFailureException`:

```
DeliveryFailureException: Could not deserialize message in hub <addr> —
    type 'MyRequest' is not registered in this hub's TypeRegistry.
```

`MessageService.DeserializeDelivery` catches the JsonElement fallback and
calls `ReportFailure(delivery.Failed(...))`, which posts the
`DeliveryFailure` back via the standard `ResponseFor(delivery)` path — the
sender's `hub.Observe(...)` surfaces it through `OnError`.

**Fix:** add the type to the receiving hub's config, e.g.

```csharp
hub.WithTypes(typeof(MyRequest), typeof(MyResponse));
```

For Orleans deployments, register on **both** sides — the silo's hub config
**and** any client/portal hub that posts the request.

A discriminator-level ping-pong guard suppresses the `DeliveryFailure`
response when the inbound `$type` itself was `DeliveryFailure` (both ends
missing the type), so a misconfigured pair won't spin forever.

## Common gotchas

- **A handler that throws an exception that isn't caught reactively** (e.g. `workspace.GetStream<T>(reference)` where the reducer isn't registered → throws `InvalidOperationException("Failed to create stream")`) crashes the delivery pipeline. The exception is logged in the `MessageService` `An exception occurred …` line. The original caller does **not** receive a response and times out. Wrap the call or check upstream that the stream exists.
- **`Take(1).Timeout(15s)` on a never-emitting source** is a 15 s wait, then `TimeoutException`. Always pair with `Catch` so the chain emits a sentinel value rather than dying.
- **`Subscribe` callbacks run on arbitrary threads.** State updates that need the hub scheduler must happen in the **handler body**, not inside the Subscribe callback. (See [AsynchronousCalls.md](AsynchronousCalls).)

## Process

> **Run once. Grep the trace. Fix the root cause. Don't rerun "to see if it
> still sticks" — it will, and you'll waste minutes per cycle. The trace tells
> you exactly which message went missing and why.**

When you find the broken edge, leave the relevant `LogTrace` / `LogDebug` lines
in place — they cost nothing at higher log levels and are the only way to debug
the next analogous failure without re-instrumenting the code each time.
