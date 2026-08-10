---
Name: AsyncLocal Across Scheduler Hops
Category: Architecture
Description: An AsyncLocal write made inside an async method lives in that method's copied ExecutionContext and is discarded when it returns — the caller never sees it. Three instances of that one defect landed in a single day, one of which silently dropped every ILogger record of every MonolithMeshTestBase test. The rule, the correct pattern, and why silo-side ILogger is unreliable in Orleans tests.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h6a4 4 0 0 1 4 4v4"/><path d="M20 18h-6"/><path d="M17 15l3 3-3 3"/><circle cx="4" cy="6" r="1.6"/><path d="M14 6h6" stroke-dasharray="2 2"/></svg>
---

`AsyncLocal<T>` is the mechanism behind ambient context in this codebase — the user's
`AccessContext`, the active test's output helper, the resolved locale. It is *not* a variable that
"just works everywhere". It is a slot in the **`ExecutionContext`**, and the .NET runtime's rules for
when that context is copied and when a copy is discarded decide whether your write is visible to the
code you expect.

Two rules cover every mistake made with it here:

> **1. A write survives only forward, along the flow that made it.** A continuation scheduled on a
> different scheduler — a hub action block, an Orleans grain turn, an Rx scheduler, a thread-pool
> callback — starts from whatever `ExecutionContext` *it* captured, which may be one where your slot
> was never set. The value is not "lost"; it was never in that flow.
>
> **2. A write made inside an `async` method is discarded when the method returns.** Entering an
> `async` method copies the `ExecutionContext`; assignments to an `AsyncLocal` land in the **copy**,
> and the copy dies with the method. The caller never observes it.

Rule 2 is the one that surprises people, because the code reads as if it obviously works.

## The shape that costs you your instruments

```csharp
// ❌ The write lands in InitializeAsync's COPIED ExecutionContext and is gone on return.
public virtual async ValueTask InitializeAsync()
{
    await StartHostedServices();
    SomeRegistry.Register(this, FileOutput);      // AsyncLocal assignment
}
```

`TestBase` registered its xUnit output helper exactly like that, and `MonolithMeshTestBase`
**overrides `InitializeAsync` as `async`** (it awaits hosted-service starts and access-rights setup).
`XUnitFileOutputRegistry` is backed by an `AsyncLocal<XUnitFileOutputHelper?>`, so by the time xUnit
ran `AutoTestLoggingAttribute.Before` the registry was empty, `SetCurrentTestMethod` was never
called, `XUnitFileOutputHelper.IsInTestMethod()` stayed `false` for the whole test — and
**`XUnitFileLogger.Log` dropped every record of every `MonolithMeshTestBase` test, at every level, on
every platform.**

For two weeks that read as evidence of absence: the diagnostic channels people had added produced
nothing, so the thing being diagnosed looked like it was not happening.

**The tell, and it is a good one.** The A/B between an otherwise identical sync and `async`
`InitializeAsync`:

| | sync `InitializeAsync` | `async` `InitializeAsync` |
|---|---|---|
| `GetAnyActiveOutputHelper()` | non-null | **null** |
| `IsInTestMethod()` | `true` | **`false`** |
| `logger.IsEnabled(Debug)` | `true` | `true` |
| the `=== TEST START: … ===` marker | present | **absent** |
| a `Debug` line in the output | present | **absent** |

`IsEnabled(Debug)` is `true` in both. **A live level check plus no output is not a configuration
problem — it is a closed sink.** If you are turning log levels up and still getting nothing, stop
adjusting configuration and check whether the marker your harness writes is there at all.

### The fix: register from the constructor

```csharp
// ✅ The constructor runs synchronously on the runner's own flow, so the write survives
//    into the test method and into every continuation that descends from it.
protected TestBase(ITestOutputHelper output)
{
    …
    XUnitFileOutputRegistry.Register(this, FileOutput);
}
```

That is what `XUnitFileOutputRegistry`'s own contract always claimed — *"the value set in a test's
ctor flows to its method and any awaited continuations"* — and it can never go back into a lifecycle
hook. A comment at the call site says so, because the move looks arbitrary otherwise.

## Instance 2 — silo-side `ILogger` is unreliable in Orleans tests

`AddXUnitLogger()` called with no accessor registers a **fresh** `TestOutputHelperAccessor` singleton
in whatever container it is called on:

```csharp
if (outputHelperAccessor == null)
    builder.Services.AddSingleton<TestOutputHelperAccessor>();   // fresh, and nothing populates it
```

A silo built by `ConfigureLogging(logging => logging.AddXUnitLogger())` therefore gets its own
accessor, while `ServiceSetup.SetOutputHelper` only ever populates the **test's** provider. So
`XUnitLogger.Log` falls through to its second source:

```csharp
var outputHelper = testOutputHelperAccessor.OutputHelper
    ?? XUnitFileOutputRegistry.GetAnyActiveOutputHelper();   // ← the AsyncLocal
if (outputHelper == null)
    return;                                                   // silently
```

…and that `AsyncLocal` is empty on any thread whose `ExecutionContext` does not descend from the test
class's constructor: **the Orleans grain scheduler, timers, and pool threads.** This is rule 1 rather
than rule 2, but it lands in exactly the same place — a `LogInformation` that runs and produces no
output.

The consequence is a working rule, not just a curiosity:

> **Diagnose silo-side code with a file-based trace, never `LogInformation`.** A gap in an Orleans
> test's silo log carries *no information*. In one investigation a file trace recorded the enrichment
> running squarely inside a "45 seconds of complete silence" window whose silo log contained two
> lines, neither of them the `LogInformation` calls that had just executed.

Reading that silence as "the request never reached the grain" is what sent a previous session down
the wrong path entirely; a message trace showed routing, activation, subscribe, ack and the render
round-trip all completing in ~260 ms.

## The framework gets this right where it matters — copy that pattern

`LayoutAreaHost` needs the subscriber's identity during a render that happens on a different
scheduler entirely. It does the only thing that works: **capture at construction, restore on the
scope that needs it.**

```csharp
// ctor — runs on the subscriber's flow, where the AsyncLocal is live.
var capturedAccessContext = accessService?.Context;

…
    .WithInitialization(_ => BuildInitialization(
        context, isDefaultArea, resolvedArea, accessService, capturedAccessContext, ctorLogger))

// BuildInitialization — runs later, on the render scheduler, where the AsyncLocal is empty.
if (capturedAccessContext != null)
    accessService?.SetContext(capturedAccessContext);
…
// and it is CLEARED on teardown, so the scope does not leak into the next thing on that thread.
```

Three properties make it correct, and all three are required:

1. **The capture happens where the value is live** — synchronously, in the constructor, on the
   subscriber's own flow.
2. **The restore happens on the scope that consumes it**, not somewhere convenient upstream.
3. **The scope is cleared on teardown.** A restored ambient value on a pooled thread outlives the
   work that needed it otherwise.

This is the same mechanism `CarryAccessContext` applies to every framework write primitive so
`.Subscribe(...)` callbacks run under the calling user — see
[AccessContext Propagation](/Doc/Architecture/AccessContextPropagation).

## The rule, applied

**Writing to an `AsyncLocal`:**

- Write from a **synchronous** frame on the flow whose continuations must observe it — a
  constructor, a synchronous setter, a `using` scope. Never from inside an `async` method that
  returns before the observers run.
- If the value must survive a scheduler hop, that is not an `AsyncLocal` problem to solve — it is a
  **capture-and-restore** problem. Capture into a local or a field at the point of truth and restore
  explicitly on the far side.

**Reading from an `AsyncLocal`:**

- Never read one on a hub action block, a grain turn, an Rx scheduler callback, a timer, or a pool
  thread and treat the result as authoritative. Read it from the **message** or the **captured**
  value instead: `delivery.AccessContext`, `AccessContext.Locale`, a captured field.
- 🚨 The localisation rule is a direct instance of this: never resolve from
  `CultureInfo.CurrentUICulture`, because a layout-area render hops the hub scheduler and an ambient
  culture does not survive it — one user's UI would pick up another user's language. Resolution is
  always explicit off `AccessContext.Locale`. See [Localization](/Doc/Architecture/Localization).

**Reviewing a diagnostic that produces nothing:**

- Check for the harness's own marker before adjusting any level. Present marker + no lines is a level
  problem; absent marker is a **closed sink**, and no amount of configuration will open it.
- On the silo side, do not treat missing log lines as evidence at all. Write a file trace.

## Pinned by

`TestOutputLoggingLifecycleTest` guards both halves of "a diagnostic reaches CI":

| Test | What it holds |
|---|---|
| `AsyncInitializeAsync_StillLeavesTheTestLogSinkOpen` | the run-time half — both assertions fail against the pre-fix registration point (that is the A/B table above) |
| `ActivityTrackingDebugChannel_IsActuallyEnabled` | the configuration half, complementing the build-time `$(TargetDir)` guards |

## See also

- [AccessContext Propagation](/Doc/Architecture/AccessContextPropagation) — the identity baton: how the framework carries `AccessContext` across exactly these hops, and what happens when it does not.
- [Localization](/Doc/Architecture/Localization) — why locale resolution is explicit off `AccessContext.Locale` and never ambient.
- [Orleans Task Scheduler](/Doc/Architecture/OrleansTaskScheduler) — which schedulers your continuation can land on.
- [Writing Tests](/Doc/Architecture/WritingTests) — the harness this instance broke, and how test logging is wired.
- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — the trace surface to use when `ILogger` is not trustworthy.
