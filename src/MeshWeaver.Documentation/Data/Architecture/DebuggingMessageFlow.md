---
Name: Debugging Message Flow
Description: "Step-by-step guide to diagnosing hub-handler hangs using structured MESSAGE_FLOW and SYNC_STREAM trace tags, with log-level and grep recipes."
---

# Debugging Message Flow & Hangs

When a hub handler looks like a deadlock — a test times out, a response never arrives — resist the urge to bisect blindly or rerun the test two or three times to see if it sticks. The framework already emits a structured trace at `Trace` level. Turn it on, run **once**, grep, fix.

---

## Log Levels — Edit the Test appsettings, Not the Source

> **ABSOLUTE: Never flip `LogInformation` ↔ `LogDebug` ↔ `LogTrace` in `src/` for a debugging session.** Log levels in source code are a production cost contract — every `Information` line ships to App Insights and is billed per ingest. Toggling them temporarily silently bleeds budget the next time the branch is deployed.

Two sanctioned paths to raise verbosity:

| Context | What to edit |
|---|---|
| Test debugging session | `test/<Suite>/bin/Debug/net10.0/appsettings.json` (or the shared `test/appsettings.json` at the runtime location). `reloadOnChange: true` is wired, so the level flips mid-run without a rebuild. **Revert before committing.** |
| Production debugging session | `memex/aspire/Memex.Portal.Distributed/appsettings.json` under `Logging:ApplicationInsights:LogLevel`. That section gates App Insights ingest; Console/stdout uses the top-level `Logging:LogLevel`. |

If a `Log*` call is genuinely too noisy or too quiet at its current level, fix it permanently with a commit explaining the cost/value trade-off — never sneak it in alongside an unrelated change.

---

## One-Shot Recipe

**1. Crank logging to Trace** in the test's runtime appsettings:

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

**2. Run the failing test once**, capturing all output:

```bash
dotnet test test/<Suite> --no-build --filter "FullyQualifiedName~<TestName>" \
    > /tmp/trace.log 2>&1
```

**3. Grep the structured tags:**

```bash
grep -E "MESSAGE_FLOW|SYNC_STREAM|exception occurred|deliveryId" /tmp/trace.log
```

If the test hangs inside the runner, per-test log files are captured here:
`test/<Suite>/bin/Debug/net10.0/test-logs/<TestClass>_<TestMethod>.log`

---

## Structured Trace Tag Reference

Every tag below is emitted as a single structured log line. The source column shows the exact file and approximate line so you can jump straight to the code.

| Tag | Source | What it tells you |
|---|---|---|
| `MESSAGE_FLOW: Unpacking message …` | `MessageService.cs:238` | A message was decoded at this hub |
| `MESSAGE_FLOW: ROUTING_TO_HIERARCHICAL …` | `MessageService.cs:247` | Hub is delegating routing to its parent / hierarchy |
| `MESSAGE_FLOW: HIERARCHICAL_ROUTING_RESULT …` | `MessageService.cs:251` | Routing returned — check `State` to see if the message was forwarded, processed, or failed |
| `MESSAGE_FLOW: ROUTING_TO_LOCAL_EXECUTION …` | `MessageService.cs:303` | Hub recognised itself as the target and is invoking handlers |
| `Buffering message …` | `MessageService.cs:184` | Hub isn't initialised yet — message went into the deferred buffer |
| `Deferring on-target message …` | `MessageService.cs:294` | Hub received the message but a `WithInitializationGate` is still closed |
| `Allowing message … through gate …` | `MessageService.cs:282` | A specific gate predicate let the message through pre-init |
| `Cancelling execution pipeline …` | `MessageService.cs:373` | Hub is shutting down — in-flight work gets cancelled |
| `An exception occurred during the processing of MessageDelivery …` | `MessageHub.cs` | A handler threw — full delivery payload + stack are dumped here. **This is your prime suspect when a request seems to vanish.** |
| `No handler found for request <T> in <Address>` | `MessageHub.cs:369` | Hub received the message but no handler matched — a `DeliveryFailure` is sent back to the caller |
| `DeserializeDelivery: Could not deserialize message in hub <addr> — type '<T>' is not registered in this hub's TypeRegistry.` | `MessageService.cs:DeserializeDelivery` | Receiving hub fell back to `JsonElement` because the inbound `$type` discriminator isn't registered. Fix: `WithType(typeof(T), nameof(T))` on the receiving hub's config. |
| `SYNC_STREAM …` | `JsonSynchronizationStream.cs` | Cross-hub workspace-stream traffic. Look here when a `GetRemoteStream<>` subscription never emits. |

---

## Reading the Trace

For a request-response hang, the healthy timeline looks like this:

```
[T0]  MESSAGE_FLOW: Unpacking …                           (request arrives at Hub A)
[T0]  MESSAGE_FLOW: ROUTING_TO_HIERARCHICAL Target=B …
[T1]  MESSAGE_FLOW: Unpacking … Hub=B
[T1]  MESSAGE_FLOW: ROUTING_TO_LOCAL_EXECUTION Hub=B
[T2]  <handler logs at Information level>
[T3]  MESSAGE_FLOW: Unpacking …                           (response arrives back at Hub A)
[T3]  hub.RegisterCallback fires → IObservable completes  (caller's await returns)
```

Find the **last** `MESSAGE_FLOW:` line that fired and look at what should have happened next:

| Last observed tag | Likely cause | Where to look next |
|---|---|---|
| No `Unpacking` at the target hub at all | Message lost in routing | `HIERARCHICAL_ROUTING_RESULT` State field |
| `Unpacking` but no `ROUTING_TO_LOCAL_EXECUTION` | Message deferred or buffered | `Deferring on-target` / `Buffering` lines |
| `ROUTING_TO_LOCAL_EXECUTION` but no handler logs | Handler threw immediately | `An exception occurred during the processing of MessageDelivery` |
| Handler logged success but caller hangs | Response not routed back | Find the `o.ResponseFor(request)` post and its matching `Unpacking` at the sender's address |

---

## The Cross-Hub Border — `JsonSynchronizationStream`

`workspace.GetRemoteStream<TReduced, TReference>(addr, ref)` subscribes via a `SubscribeRequest` posted to the owning hub. If the owning hub has no handler for `SubscribeRequest` — or no matching reducer — the subscription receives a `DeliveryFailure` instead of a `SubscribeResponse`, and the `SynchronizationStream` errors out:

```
[Warning] [MeshWeaver.Data.Serialization.SynchronizationStream] Stream <id>
   received DeliveryFailure: No handler found for message type SubscribeRequest
```

This is the smoking gun for the "remote read returns nothing" class of hang. The fix lives on the **owning side** (register a `SubscribeRequest` handler or add a `MeshDataSource` so the hub has the reducer), not on the caller side.

---

## Type-Registry Mismatch

The handler is registered, but the message arrives at the receiving hub as `JsonElement` because the receiver's `ITypeRegistry` is missing the type. The sender surfaces a clean exception:

```
DeliveryFailureException: Could not deserialize message in hub <addr> —
    type 'MyRequest' is not registered in this hub's TypeRegistry.
```

`MessageService.DeserializeDelivery` catches the `JsonElement` fallback, calls `ReportFailure(delivery.Failed(...))`, and posts the `DeliveryFailure` back via the standard `ResponseFor(delivery)` path — the sender's `hub.Observe(...)` surfaces it as `OnError`.

**Fix:** add the type to the receiving hub's config:

```csharp
hub.WithTypes(typeof(MyRequest), typeof(MyResponse));
```

For Orleans deployments, register on **both sides** — the silo's hub config and any client/portal hub that posts the request. A ping-pong guard suppresses `DeliveryFailure` responses when the inbound `$type` is itself `DeliveryFailure`, so a misconfigured pair won't spin forever.

### FQN vs Short-Name Mismatches

The wire `$type` discriminator must match the receiver's registered `typeName`. The polymorphic serializer picks the discriminator from the **sender's** `ITypeRegistry`:

- Sender registered `WithType(typeof(T), nameof(T))` → wire `$type` is the short name (`"CreateNodeRequest"`).
- Sender's registry lacks the type → falls back to `FullName` (`"MeshWeaver.Mesh.CreateNodeRequest"`) at serialize time.

A receiver that registered `WithType(typeof(T), nameof(T))` only matches short names, so an FQN on the wire fails the lookup and produces a `DeliveryFailure` even though both sides technically "have" the type.

**Triage with the file trace** (`MESHWEAVER_MSG_TRACE=1`, `%TEMP%/meshweaver-msg-trace.log`): the `NotifyAsync ENTER` line stamps `msg=...` with the JSON `$type` discriminator from `RawJson.Content`. If it reads `msg=MeshWeaver.Mesh.CreateNodeRequest` (FQN) instead of `msg=CreateNodeRequest` (short), a hub somewhere along the hop didn't register `T` in its TypeRegistry — register on every hub the message transits, not just the originator and the final target.

For test setup specifically: `MessageHubConfiguration.TypeRegistry` is mutable per call (`WithType` returns the same instance), so `configuration.TypeRegistry.AddAITypes();` (discarded return) is sufficient — but the call must reach **the configuration of every hub that serializes the message**, including hosted sub-hubs like `{path}/_Exec` and any cross-cutting `ConfigureDefaultNodeHub` chain.

---

## Common Gotchas

- **A handler that throws uncaught** — e.g. `workspace.GetStream<T>(reference)` where the reducer isn't registered throws `InvalidOperationException("Failed to create stream")` and crashes the delivery pipeline. The exception appears in the `An exception occurred …` line. The original caller receives no response and times out. Wrap the call or verify upstream that the stream exists.

- **`Take(1).Timeout(15s)` on a never-emitting source** — results in a 15-second wait followed by `TimeoutException`. Always pair `Timeout` with `Catch` so the chain emits a sentinel value rather than dying.

- **`Subscribe` callbacks run on arbitrary threads.** State updates that need the hub scheduler must happen in the **handler body**, not inside a `Subscribe` callback. See [AsynchronousCalls.md](AsynchronousCalls).

---

## The Golden Rule

> **Run once. Grep the trace. Fix the root cause.**
>
> Don't rerun "to see if it still sticks" — it will, and you'll waste minutes per cycle. The trace tells you exactly which message went missing and why.

When you find the broken edge, leave the relevant `LogTrace` / `LogDebug` lines in place. They cost nothing at higher log levels and are the only way to debug the next analogous failure without re-instrumenting the code from scratch.
