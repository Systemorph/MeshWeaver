---
Name: Debugging Message Flow
Description: "Step-by-step guide to diagnosing hub-handler hangs using structured MESSAGE_FLOW and SYNC_STREAM trace tags, with log-level and grep recipes."
---

# Debugging Message Flow & Hangs

When a hub handler looks like a deadlock — a test times out, a response never arrives — resist the urge to bisect blindly or rerun the test two or three times to see if it sticks. The framework already emits a structured trace at `Trace` level. Turn it on, run **once**, grep, fix.

<svg viewBox="0 0 760 310" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#90a4ae"/>
    </marker>
    <marker id="arr-blue" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#1e88e5"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#43a047"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="310" rx="10" fill="#1a1e2e" opacity="0.7"/>
  <rect x="40" y="30" width="130" height="44" rx="10" fill="#1e88e5"/>
  <text x="105" y="56" text-anchor="middle" fill="#fff" font-weight="bold">Hub A</text>
  <rect x="590" y="30" width="130" height="44" rx="10" fill="#5c6bc0"/>
  <text x="655" y="56" text-anchor="middle" fill="#fff" font-weight="bold">Hub B</text>
  <line x1="105" y1="74" x2="105" y2="270" stroke="#1e88e5" stroke-opacity="0.35" stroke-width="1.5" stroke-dasharray="4,3"/>
  <line x1="655" y1="74" x2="655" y2="270" stroke="#5c6bc0" stroke-opacity="0.35" stroke-width="1.5" stroke-dasharray="4,3"/>
  <rect x="250" y="22" width="260" height="20" rx="4" fill="none"/>
  <text x="380" y="21" text-anchor="middle" fill="currentColor" fill-opacity="0.45" font-size="11">MESSAGE_FLOW trace tags</text>
  <rect x="80" y="88" width="170" height="22" rx="5" fill="#1e3a5f">
    <title>Unpacking message at Hub A (outbound)</title>
  </rect>
  <text x="165" y="103" text-anchor="middle" fill="#90caf9" font-size="11">Unpacking message …</text>
  <line x1="170" y1="130" x2="638" y2="130" stroke="#1e88e5" stroke-width="2" marker-end="url(#arr-blue)"/>
  <text x="380" y="123" text-anchor="middle" fill="#90caf9" font-size="11">Request</text>
  <rect x="248" y="133" width="264" height="18" rx="4" fill="none"/>
  <text x="380" y="146" text-anchor="middle" fill="#90a4ae" font-size="10">ROUTING_TO_HIERARCHICAL → HIERARCHICAL_ROUTING_RESULT</text>
  <rect x="510" y="160" width="200" height="22" rx="5" fill="#2e1f5e">
    <title>Hub B unpacks and routes locally</title>
  </rect>
  <text x="610" y="175" text-anchor="middle" fill="#b39ddb" font-size="11">Unpacking … (Hub B)</text>
  <rect x="510" y="188" width="200" height="22" rx="5" fill="#2e1f5e"/>
  <text x="610" y="203" text-anchor="middle" fill="#b39ddb" font-size="11">ROUTING_TO_LOCAL_EXECUTION</text>
  <rect x="510" y="216" width="200" height="22" rx="5" fill="#1b3a2a"/>
  <text x="610" y="231" text-anchor="middle" fill="#a5d6a7" font-size="11">handler executes ✓</text>
  <line x1="638" y1="248" x2="122" y2="248" stroke="#43a047" stroke-width="2" marker-end="url(#arr-green)"/>
  <text x="380" y="241" text-anchor="middle" fill="#a5d6a7" font-size="11">Response (ResponseFor)</text>
  <rect x="50" y="256" width="200" height="22" rx="5" fill="#1b3a2a"/>
  <text x="150" y="271" text-anchor="middle" fill="#a5d6a7" font-size="11">RegisterCallback fires → completes</text>
  <text x="380" y="298" text-anchor="middle" fill="currentColor" fill-opacity="0.38" font-size="11">Hang diagnosis: find the last MESSAGE_FLOW: tag that fired and check what follows</text>
</svg>

*Healthy request-response trace: Hub A routes the request through the hierarchy to Hub B, which executes the handler and sends the response back.*

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

---

## "Deadlock" that is really a missed observation — resurrection on init

A whole class of "hangs" are **not** locks. The signature in the
`MESHWEAVER_MSG_TRACE` file (`%TEMP%/meshweaver-msg-trace.log`) is decisive:

- **Real lock-deadlock** — one *large gap* where nothing runs, then the test
  times out. The action block is wedged on a blocking continuation.
- **Missed observation** — the hub runs a **burst of work (seconds)**, then goes
  **completely silent** for the rest of the timeout. The work *finished*; the
  thing waiting on it never saw the terminal state. No gap-during-work, no lock.

To tell them apart, compute the **max gap between handler-enters** for the stuck
node (`grep "hub=<path> " trace | grep "HandleMessageAsync ENTER"` → diff the
timestamps). A big mid-work gap ⇒ lock. Continuous work then silence ⇒ missed
observation. The volume asymmetry is another tell: a synced-query subscription
re-emitting on every change shows **far more `GetDataResponse` than `GetDataRequest`**,
and the count *scales with load* — slower round-trips ⇒ more iterations.

### The root pattern

A long-lived operation (a thread round, a parent waiting on a delegated child,
an activity) is driven by an **in-memory observer** — a `Subscribe` on a node
stream, a `TaskCompletionSource` resolved by a callback. Two ways that observer
silently dies and the operation parks forever:

1. **One-shot with a give-up.** `stream.Take(1).Timeout(15s).Subscribe(...)` —
   if the loaded-state emission is dropped during the subscribe handshake (see
   [the init-gate-drops-patches note](xref:Architecture/AsynchronousCalls)) or
   merely arrives late under load, the `Timeout` fires `onError`, the recovery
   **gives up, and never retries**. The node stays non-terminal forever.
2. **Lost on reactivation.** The observer lives only in the agent-loop / grain
   that set it up. When the grain deactivates and reactivates (Orleans) or the
   hub re-inits, the subscription is gone and is **never rebuilt**, so the child's
   eventual completion is never observed.

### The fix — self-healing resurrection on init

Lifecycle recovery (`ThreadExecution.InitializeThreadLifecycle`, and the
analogous activity init) must obey:

- **Re-establish, never give up.** Wait for the first real state emission however
  long it takes; if the observation *faults* before it drives the node to a valid
  state, **re-subscribe** (restart the watcher). No `Timeout(...)`-then-give-up.
- **Restart if any observer dies before terminal.** An observer that completes or
  errors while the node is still non-terminal (`Cancelled`/`Done`/`Failed`/settled
  `Idle`) must be restarted.
- **Re-observe children on init.** A parent frozen mid-delegation must NOT blindly
  re-run its agent loop (that re-delegates / duplicates the child). It must
  **re-observe the existing child**, and when the child reaches terminal, write
  the child's result back so the parent can settle/continue.
- **Guarantee terminal.** A last-resort watchdog forces a wedged round to a
  terminal `Idle` after a generous grace of **no progress** (Rx `Throttle` resets
  on every node emission, so live streaming never trips it; threads legitimately
  waiting on a child are skipped — that staleness is the heartbeat ticker's job).
- **Children always reach terminal.** A sub-thread's own init must drive itself to
  a terminal state so the parent's re-observation is guaranteed to fire.

Don't "fix" this by bumping the test timeout — that hides a missed emission behind
a longer wait. Find the observer that died and make it restart.
