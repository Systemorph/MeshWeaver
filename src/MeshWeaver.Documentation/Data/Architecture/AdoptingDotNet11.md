---
Name: Adopting .NET 11
Category: Architecture
Description: What .NET 11 and C# 15 offer this platform, checked against the official pages and grounded in our own files — where each feature applies, what it buys, what our policies forbid, how to verify it, the order to adopt it in, and every .NET 11 breaking change that touches our code.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 17l6-6-6-6"/><path d="M12 19h8"/></svg>
---

# Adopting .NET 11

This page assesses .NET 11 and C# 15 for this platform. The retarget itself, `net10.0` → `net11.0`
and `PlatformVersion` 4.0.0, is a separate declared break. It is carried by the v4 pull request
(Systemorph/MeshWeaver#8135), whose own page records what the new runtime changed under unchanged
code. Everything proposed here lands on the **v4 line only**. The 3.x line stays on .NET 10, which is
the LTS release. .NET 11 is an STS release: STS releases are supported for 24 months
([STS support policy](https://devblogs.microsoft.com/dotnet/dotnet-sts-releases-supported-for-24-months/)).

Every feature claim below was checked against an official Microsoft page, cited where it is used.
Where a page did not settle a question, the page says **not verified** and names the measurement
that would settle it. The code references come from core (this repository) and from
MeshWeaver.Plugins, which holds the portal hosts, the AI engine and the Blazor GUI. Plugins paths are
written `Plugins:<path>`.

## Verdicts on the claims we started from

| Claim | Verdict | Official source |
|---|---|---|
| C# 15 union types and `closed` hierarchies | **Confirmed.** Both GA in C# 15, and neither needs `LangVersion preview`. | [What's new in C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15) |
| Runtime Async | **Confirmed, but it is opt-in for our assemblies.** A `net11.0` project enables it with `<Features>runtime-async=on</Features>`. The BCL is already compiled with it, and so are the ASP.NET Core shared-framework-only libraries. | [Runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime), [ASP.NET Core 11](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11) |
| Zstandard in System.IO.Compression | **Confirmed.** `ZstandardStream`, `ZstandardEncoder` and `ZstandardDictionary` now live in `System.IO.Compression`. | [Libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries) |
| zstd response compression on by default in ASP.NET Core 11 | **Confirmed, but only inside the response-compression and request-decompression middleware.** It does not reach a host that never registers that middleware, and no portal host of ours does. | [ASP.NET Core 11](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11) |
| System.Text.Json JSON Lines | **Half right.** .NET 11 adds JSON Lines **output** (`SerializeAsyncEnumerable(…, topLevelValues: true)`). Reading a sequence of top-level values already existed through `DeserializeAsyncEnumerable(…, topLevelValues: true)` on the Stream overloads since .NET 9. | [Libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries), [API](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializer.deserializeasyncenumerable) |
| LINQ `FullJoin` | **Confirmed.** Available on `Enumerable`, `Queryable` and `AsyncEnumerable`, with tuple-returning `Join`/`GroupJoin` overloads. | [Libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries) |
| Blazor Server circuit pause/resume, auto-pause on a hidden tab | **Confirmed, two separate features.** A server-initiated `Circuit.RequestCircuitPauseAsync` is built in. Auto-pause is **opt-in** through the `Microsoft.AspNetCore.Components.Server.AutoPause` package, with a default delay of 2 minutes. | [ASP.NET Core 11](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11) |
| Native OpenTelemetry tags in ASP.NET Core | **Confirmed, and it is a breaking change.** `SuppressActivityOpenTelemetryData` now defaults to `false`. | [Breaking change](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/11/http-activity-otel-semconv) |
| Virtualize `AnchorMode` | **Confirmed** (`None`, `Start` as the default, `End`). The QuickGrid forwarding parameter is experimental (`ASP0030`). | [ASP.NET Core 11](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11) |
| x86-64-v2 / Arm64 LSE hardware requirement | **Partly refuted.** x86/x64 needs x86-64-v2 on every OS. The Arm64 **minimum** rises to `armv8.0-a + LSE` **on Windows only**. Linux keeps `armv8.0-a`, and only its ReadyToRun target adds LSE. | [Runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime), [Breaking change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/jit/11/minimum-hardware-requirements) |

## 1. Union types and `closed` hierarchies

**What the language gives.** A `union Pet(Cat, Dog, Bird)` converts implicitly from each case type,
and the compiler proves a `switch` over it exhaustive. A `closed` class can be derived from only
inside its declaring assembly. A switch that handles every direct descendant is therefore
exhaustive and needs no default arm. Derivation is not transitive: to carry exhaustiveness further
down, each intermediate level has to be marked `closed` as well
([C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15)).

**Where it applies.** Our "outcome" types spell a closed set of states by hand, and their own doc
comments say what that costs. `src/MeshWeaver.Mesh.Contract/NodeReadOutcome.cs` (`NodeReadStatus` +
`NodeReadOutcome`) states:

> C# cannot prove an enum switch exhaustive (an undeclared value is always possible, hence CS8509),
> so the compiler will not sweep call sites for you when a state is added here

That is exactly what a `closed` record hierarchy fixes. The other hand-rolled outcome types:

| Type | File | Shape today |
|---|---|---|
| `NodeReadOutcome` / `NodeReadStatus` | `src/MeshWeaver.Mesh.Contract/NodeReadOutcome.cs` | enum + record carrying `Node`, `Failure`, `HiddenBy` |
| `NodeReadOutcome` (Operations) | `src/MeshWeaver.Mesh.Operations/NodeReadOutcome.cs` | record; the state is implied by which field is null |
| `PermissionCheckOutcome` | `src/MeshWeaver.Mesh.Contract/Security/PermissionCheckOutcome.cs` | `IsGranted` + `UndeterminedReason`, three states over two fields |
| `DetachedReplyOutcome<T>` | `src/MeshWeaver.Mesh.Contract/DetachedReplyOutcome.cs` | `internal readonly record struct (bool HasValue, T? Value, Exception? Error)` |
| `NodeCopyOutcome` / `NodeCopyStatus` | `src/MeshWeaver.Graph/NodeCopyOutcome.cs` | enum + record |
| `DependencyRecordOutcome` / `DependencyRecordStatus` | `src/MeshWeaver.Compiler/DependencyRecordOutcome.cs` | enum + record |

**Benefit.** When a state is added, every consumer that switches on the type gets a compile error
under `-warnaserror` instead of silently inheriting a default arm. On `NodeReadOutcome` that default
arm is the "absent, therefore create" bug that resurrected deleted nodes (#1471). The benefit can be
counted: how many fall-through arms disappear.

**Risk under our policies.** A union, like an enum, is **closed by design**. So it is safe for a type
only when ALL of the following hold:

1. **Not persisted and not serialized.** System.Text.Json writes a union's active case "directly,
   without a wrapper or `$type` discriminator". When there are several object cases, it needs a
   structural classifier to deserialize, and it throws on an ambiguous payload
   ([Libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries)). Our
   polymorphic `$type` convention and the typed-content pipeline would see an untyped shape, which is
   the silent-null trap (see `.As<T>()` in the mesh-data rules).
2. **Not extended by modules.** A set that a module, satellite or deployment extends is an open
   vocabulary, and it must stay string constants
   ([Open Vocabularies](../OpenVocabulariesAsStringConstants)). So these are **never** candidates:
   `UiControl` (`src/MeshWeaver.Layout/UiControl.cs`), `WorkspaceReference`
   (`src/MeshWeaver.Data.Contract/WorkspaceReference.cs`), `StreamMessage`/`JsonChange`
   (`src/MeshWeaver.Data.Contract/Messages.cs`), and every data-source base in `src/MeshWeaver.Data/`.
   All of them are serialized polymorphically or extended outside their assembly, and `closed` would
   break both.
3. **Not consumed by in-mesh C#.** NodeType sources compile at runtime and no `dotnet build` sees
   them ([NodeType Compilation](../NodeTypeCompilation)). **Measured: two of the public candidates
   above are consumed in-mesh today.**
   - `Plugins:Hosting/Queue/Source/JobSeals.cs:114-120` switches on `MeshWeaver.Mesh.NodeReadStatus.Present`/`.Absent`.
   - `Plugins:Feedback/EvalNight/Source/NightlyEvals.cs:318` and `NightlyEvalSchedule.cs:362-367` take
     `PermissionCheckOutcome` and call `PermissionCheckOutcome.Undetermined(…)`.

   Reshaping either type in place therefore breaks code that CI cannot compile. The sweep covered the
   two checkouts only. A live-mesh `search_chunks` sweep was **not run**, so other in-mesh callers
   are not excluded.

**So the rule is:**

- **Internal-only types: adopt directly.** `DetachedReplyOutcome<T>` is internal, never serialized and
  invisible to in-mesh code. It is the first candidate.
- **Public types read by in-mesh code: additive only.** Turn `NodeReadOutcome` into a
  `public closed record` with one sealed descendant per state, but KEEP `Status`, `Node`, `Failure`
  and `HiddenBy` as members of the base, and keep the static factories (`Present`, `Absent`, `Hidden`,
  `DeleteInProgress`, `Unavailable`). An in-mesh consumer that switches on `read.Status` still
  compiles. A consumer that switches on the type gains exhaustiveness. The enum stays, and is not
  marked `[Obsolete]`, for as long as any in-mesh caller uses it. This is the same forwarder
  discipline as for any public surface a `cellSurface` type may have copied.
- **Do not let in-mesh authors use `union`/`closed` until the runtime compiler is proven on C# 15.**
  `src/MeshWeaver.Compiler/EmitPipeline.cs` `CreateParseOptions()` sets no `LanguageVersion`, so it
  takes whatever `Microsoft.CodeAnalysis.CSharp` 5.9.0 calls its default. That version is pinned on
  `main` and unchanged on the v4 branch. **Not verified:** whether that Roslyn build's default is
  C# 15. The measurement is a probe NodeType whose source declares a `union`, compiled by the portal
  and read through its `compilationStatus`. Until then, a public union in `src/` could reach in-mesh
  consumers compiled by an older language version. What such a consumer sees is not documented, so
  keep unions off every surface in-mesh code reads.
- **New keyword collisions.** `union` and `closed` are contextual keywords in type-declaration
  contexts (CS9370, CS9380, CS1519;
  [compiler breaking changes](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/breaking-changes/compiler%20breaking%20changes%20-%20dotnet%2011)).
  A grep of core `src/`, `samples/` and the Plugins checkout found no type or field declared with
  either name. The same page also rebinds `[with(…)]` in collection expressions, and that grep found
  no such element either.

**Verification.** "Adding a state breaks every incomplete consumer" cannot be shown by a runtime
test. Use a Roslyn-driven test instead, in the style of the guard
tests in `test/MeshWeaver.Documentation.Test`: it compiles a consumer switch with one arm missing and
expects CS8509. After that, the in-mesh compile sweep from AGENTS.md (`content.compilationStatus:Error
partitions:all`, with its denominator), run on the control instance after the v4 roll.

## 2. Runtime Async

**What the runtime gives.** The runtime, not the compiler, generates the async state machine. Live
stack traces drop from 13 frames to 5 in the official sample. Continuations skip `ExecutionContext`
capture and restore when there is nothing to restore. It is enabled per project with
`<Features>runtime-async=on</Features>` and disabled with `<UseRuntimeAsync>false</UseRuntimeAsync>`.
The `DOTNET_RuntimeAsync` environment variable is gone. Exception stack traces "already look the
same with or without Runtime Async"
([Runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)).

**Where it applies, and where it already applies without us.** `src/` holds about a hundred
`async Task`/`async ValueTask` lines, mostly at the I/O leaves behind `IIoPool`. `test/` holds about
4,600. The BCL and ASP.NET Core's shared-framework libraries are runtime-async on `net11.0`
regardless of what we set, so three of our files meet it on v4 whether we opt in or not:

- `src/MeshWeaver.Hosting/ThreadPoolQueueCensus.cs` `StateMachineOf` attributes a queued `Task` by
  walking to a base type whose name starts with `AsyncStateMachineBox`. A runtime-async continuation
  has no such box. Census rows for BCL and ASP.NET work items will therefore fall back to
  `Display(type)`. **Not verified** how they look. Pin it with a census test on 11 that queues a
  runtime-async continuation.
- `src/MeshWeaver.Messaging.Hub/AccessService.cs` keeps `AccessContext` in three `AsyncLocal` fields.
  The ExecutionContext shortcut is documented to apply only when "no ambient state is in use", so the
  flow should be unchanged. Even so, the ASP.NET Core notes explicitly ask for reports of
  "`ExecutionContext`/`AsyncLocal` flow" regressions, and this is the field every write's identity
  rides on ([AccessContext Propagation](../AccessContextPropagation)).
- `src/MeshWeaver.PluginCatalog/Telemetry/ErrorSignatureLoggerProvider.cs` `TopFrame` parses
  `exception.StackTrace` text into an error signature. Exception traces are documented as unchanged,
  so signatures should stay stable. One assertion on a thrown-through-await exception pins it.

**Benefit.** For `src/` the gain is small: hub code is `IObservable`, and the async that remains is a
leaf. For `test/` the gain is diagnostic. The per-test bound (`BoundedTestFramework`) and
`src/MeshWeaver.Connection.Orleans/BlockedThreadStacks.cs` dump live stacks of stuck threads, and
those are exactly the traces runtime-async shortens to the real call chain.

**Risk under our policies.** None of the no-async rules relax. Runtime Async changes how `await` is
generated, not where it is allowed. The three instruments hold differently:

- `test/MeshWeaver.Documentation.Test/ObservableToTaskBridgeGuard.cs` scans **source text**. It is
  unaffected by codegen and keeps holding `src/` at zero.
- `test/MeshWeaver.Messaging.Hub.Test/InlineResumptionMechanismTest.cs` **measures** where an awaiter
  resumes. It is the evidence that `.ToTask()` and `await observable` resume inline on the signalling
  thread, while `.Await(ct)` and `.ObserveCompletion(…)` (`src/MeshWeaver.Reactive.Assertions/ReactiveWait.cs`,
  `TaskCreationOptions.RunContinuationsAsynchronously`) do not. The official pages do not say whether
  awaiting a custom awaiter such as Rx's `AsyncSubject` resumes the same way under runtime-async.
  **Not verified.** The test has to be run once with `runtime-async=on` on its own project before any
  project opts in, and the sanctioned bridge has to stay on the "does not resume inline" side.
- `.ToTask()` stays forbidden everywhere. A shorter stack trace does not change which thread the
  continuation inherits.

**Verification.** Run `InlineResumptionMechanismTest` and the AccessContext propagation tests on
`net11.0`, once with runtime-async off and once with it on, before any opt-in. Then opt in one test
project, compare its wall clock and peak allocations against the same commit with the feature off,
and roll out per project.

## 3. Zstandard

**What the platform gives.** Zstandard streams, encoders and trained dictionaries in
`System.IO.Compression`
([Libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries)). In
ASP.NET Core, zstd joins the response-compression and request-decompression middleware and is enabled
by default *there* (`ZstandardCompressionProviderOptions`, quality 1–22)
([ASP.NET Core 11](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11)).

**Where the default reaches us: nowhere today.** No portal host registers `AddResponseCompression` or
`AddRequestDecompression` (grep over `Plugins:src`, test projects excluded). The only
`Accept-Encoding` logic in core, `src/MeshWeaver.Hosting.AspNetCore/MeshModuleStaticAssetExtensions.cs`,
writes identity-encoded bodies and documents that the encoding negotiation runs later. So zstd-on-by-default
changes nothing on the wire for us. A future pull request that adds the middleware turns zstd on in
the same change. It must then answer for the ingress controller and every client in between, and the
new behaviour of always emitting `Vary: Accept-Encoding`
([breaking change](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/11/response-compression-always-vary))
contradicts the deliberate no-`Vary` choice that file documents.

**Where zstd could apply, and why not yet.**

- **Module bundles** are ZIP archives: `src/MeshWeaver.Plugin.Packaging/BundleWriter.cs`,
  `BundleReader.cs` and `src/MeshWeaver.PluginCatalog/ServedModuleBytes.cs`. Their bytes are content
  addressed, and every reader on every replica must open them. The official page does not say whether
  .NET 11's ZIP reader accepts zstd-compressed entries. **Not verified.** Changing the container
  format is a bundle-format break, so it would belong to a major line of its own.
- **Node export** (`src/MeshWeaver.Mesh.Operations/MeshOperations.cs`, `CompressionLevel.Optimal`
  ZIP) is a user-facing file. ZIP stays the interchange format.
- **CI artifacts** are packed by Python (`.github/scripts`), not by .NET, so nothing here applies.

**Recommendation.** Do not adopt zstd on v4 for any format another process reads. Revisit when a
measured hot path (a bundle download over the registry, the content-addressed store) shows that
compression ratio or CPU time is the cost that matters.

## 4. System.Text.Json and LINQ

- **JSON Lines.** Our line-delimited JSON is **read** from child-process stdout:
  `Plugins:src/MeshWeaver.AI.Codex/CodexAppServer.cs` `ReadLoopAsync` and the Claude Code harness's
  `Plugins:src/MeshWeaver.AI.ClaudeCode/StreamJsonRound.cs`. Those parse one line at a time on
  purpose: a malformed line is skipped rather than ending the stream, so the new writer does not
  apply. **No adoption.**
- **Union serialization.** This is why unions stay out of node content (section 1).
- **Allocation behaviour changed under unchanged code.** The v4 pull request measured that a missed
  `JsonValue.TryGetValue<T>` on a constructed number boxes on STJ 11. The probe order in
  `src/MeshWeaver.Data.Contract/ContentShapes.cs` is now the allocation budget. Any new probe loop
  needs the same allocation test.
- **`FullJoin`.** No `LeftJoin`/`RightJoin`/`GroupJoin`/hand-rolled outer join exists in `src/`
  (grep). **No adoption until a caller needs it.**

## 5. Blazor Server circuits

All of this lives in MeshWeaver.Plugins.

**Server-initiated pause.** "`Circuit.RequestCircuitPauseAsync(CancellationToken)` is used to request
that the connected client begin the graceful circuit-pause flow". Its documented scenarios are
planned shutdowns and instance draining
([ASP.NET Core 11](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11)). That is
exactly what we built by hand:

- `Plugins:src/MeshWeaver.Hosting.Blazor/CircuitDrainHandler.cs` `SendDrainHint` invokes the JS
  function `meshweaverDrainCircuit` (`CircuitShutdownDrain.DrainHintFunction`) through `IJSRuntime`
  on the HTTP I/O pool.
- `Plugins:src/MeshWeaver.Blazor/wwwroot/MeshWeaver.Blazor.lib.module.js` then calls
  `blazor.pauseCircuit()` and reloads.
- `Plugins:src/MeshWeaver.Blazor/CircuitCutOff.cs` states that "ASP.NET Core offers no public
  server-side call that ends a circuit".

`RequestCircuitPauseAsync` can replace the JS hint and its interop call. It does **not** replace
`CircuitCutOff`. It is still a *request* that the client must act on, and the cut-off exists for the
browser that never acts (a disconnected-and-retained circuit, a sleeping tab). It returns a `Task`, so
it stays behind `IIoPool.Invoke` exactly as the JS call does now. **Not verified:** whether a resumed
circuit lands on another pod. Our hint reloads in order to leave the draining pod. The built-in flow
resumes, and where it resumes depends on ingress affinity. Measure it on a two-replica roll before
deleting the reload.

**Auto-pause on a hidden tab (opt-in).** After a configurable delay (default 2 minutes) the circuit
pauses, "freeing server memory and SignalR connections held by inactive users". **Benefit:** every
per-circuit mesh subscription is released for the whole time a tab is hidden: each `BlazorView`
binding, the `CircuitCutOff` registrations, and the per-circuit `CircuitAccessHandler` identity. That
can be measured as live circuits and hub subscriptions per replica, through the platform meter and
`ActiveCircuitTracker`. **Risks:**

- **Live data-binding** survives by construction: views re-subscribe to `GetMeshNodeStream` on resume
  and render the current value ([Data Binding](/Doc/GUI/DataBinding)). Only state held in a component
  and nowhere else is lost: an unsent composer draft, an unsaved Monaco buffer. The side panel and the
  nav rail already persist through `[PersistentState]` (`SidePanelStateService`, `NavRailStateService`).
- **Presence.** `Plugins:src/MeshWeaver.Hosting.Blazor/MeshPresenceWriter.cs` withdraws presence when
  the circuit closes. A tab hidden for two minutes would read as offline. Decide whether that is
  wanted before enabling.
- **A resume burst.** Every resume re-subscribes all of a page's views at once. That load is normal
  per page, but there is no evidence yet at fleet scale.

**Verification.** A Playwright E2E (the `playwright` skill's throwaway portal) that opens a bound view
and pauses the circuit with `Blazor.pauseCircuit()`. A second session then updates the node, the
first resumes, and the test asserts the new value renders with no reload. Plus a two-replica drain
test for `RequestCircuitPauseAsync`, asserting that the circuit leaves the draining pod.

**Virtualize `AnchorMode`.** We render no `<Virtualize>` directly. `Plugins:src/MeshWeaver.Blazor.Chat/ChatMessageList.razor.js`
hand-rolls bottom-pinning with a `MutationObserver`, which is what `AnchorMode.End` does for a
virtualized list. That only becomes relevant if long threads get virtualized. Separately,
`Plugins:src/MeshWeaver.Blazor.Views/Components/DataGridView.razor` passes `Virtualize` to the Fluent
data grid. Virtualize's `OverscanCount` default moves from 3 to 15, and it stops assuming uniform
heights. Whether the Fluent grid inherits either change is **not verified**. Measure scroll
behaviour on a large grid.

## 6. Observability

- **HTTP semantic-convention tags are on by default** (breaking change above). Our hosts trace through
  `OpenTelemetry.Instrumentation.AspNetCore` (`Plugins:src/Memex.Portal.ServiceDefaults/ServiceDefaults.cs`
  `ConfigureOpenTelemetry`, pinned at 1.20.1 in `Directory.Packages.props`, moved to 1.21.0 on the v4
  branch). The documented remedy for duplicate tags is to upgrade the instrumentation package, which
  then "detects the built-in tags and skips adding them". **Keep the package.** Its `Filter` drops the
  probe endpoints (`ProbeEndpoints.Health`/`Live`/`Ready`), and a bare
  `AddSource("Microsoft.AspNetCore")` has no such filter. Whether the new activity-tracing rules
  (`AddTracing`/`DisableTracing`) can filter by request path is **not verified**. Also:
  `http.server.request.duration` gains an `error.type` dimension on 5xx responses, and
  `Activity.StatusDescription` is no longer set. Check the dashboards that aggregate either.
- **`MemoryCache` metrics.** `src/MeshWeaver.Hosting/Persistence/PartitionStorage/PartitionStorageServiceExtensions.cs:31`
  creates the partition-hub cache as `new MemoryCache(new MemoryCacheOptions())`. With
  `TrackStatistics = true`, `dotnet.cache.requests`, `.evictions`, `.entries` and `.estimated_size`
  give partition-hub hit and eviction rates for free. Use the constructor that takes an
  `IMeterFactory`. Without one, "the instruments are aggregated process-wide on a shared meter", which
  is process-wide state that would mix the many meshes of a test run
  ([No Static State](../NoStaticState)).
- **HttpClient high-cardinality metrics become observable counters**
  ([breaking change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/networking/11/http-metrics-observable-counters)).
  Check the Grafana panels built on them.

## 7. Hardware baseline

x86-64-v2 everywhere (CX16, POPCNT, SSE3, SSSE3, SSE4.1, SSE4.2). On older CPUs .NET 11 "fails to run"
with *"The current CPU is missing one or more of the baseline instruction sets."* The docs place the
last chips without it around 2013, so current cloud nodes and runners should clear it. That was **not
measured** per node pool: run `dotnet --info` on each AKS node pool's image after the v4 roll. Core's CD builds each architecture's images on that architecture (`core-tests-image-arch` in
`main-cd.yml`), so no emulated build is in the path. **Not verified:** the CPU model an emulated amd64
container gets on an Apple-silicon host (Colima). Run `dotnet --info` inside one before relying on it.

## Breaking changes that touch our code

Lists: [.NET 11](https://learn.microsoft.com/en-us/dotnet/core/compatibility/11),
[ASP.NET Core 11](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/11/overview),
[C# 15 compiler](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/breaking-changes/compiler%20breaking%20changes%20-%20dotnet%2011).
Each entry below was grepped for. "Hit" means our code meets it.

| Change | What we found | Action |
|---|---|---|
| Some `Microsoft.Extensions.*` packages join the shared framework, and their assemblies are no longer copied to output ([page](https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/11/extensions-in-shared-framework)) | **Hit.** 16 `PackageReference`s in `src/` and `test/` to the nine packages (Caching, Configuration, DependencyInjection, Hosting and Logging `.Abstractions`). The resulting NU1510 is already in `NoWarn` (`Directory.Build.props`). What lands in a bundle's output and in the published platform surface changes. The v4 PR's `ModulePlatformSurface.OfRunningPlatform` fix covers the surface. | Drop the redundant references on v4. Then check that the in-mesh reference set (`build-host-reference-set.py`) still resolves those assemblies from the shared framework. |
| DI 11 refuses a re-entrant resolve of a singleton under construction (found by the v4 PR's CI run, and **not** on the official list) | **Hit**, fixed in the v4 PR (`ModuleContexts.PublishOnce`, `ModuleServices.BuildBeforePublish`) | Any other singleton factory that resolves itself through the root is the same defect |
| `IHost.RunAsync`/`StopAsync` throw when a `BackgroundService` fails ([page](https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/11/ihost-runasync-stopasync-throw-backgroundservice-failure)) | **Hit.** `Plugins:src/MeshWeaver.Observability/LogIncidentControlPlane.cs:34` and `Plugins:src/MeshWeaver.LogWatcher/LogWatchWorker.cs:28` are `BackgroundService`s | Desired: a failed worker now exits non-zero instead of 0. Confirm neither throws on ordinary cancellation. |
| MSBuild server is used by default (SDK notes, [overview](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview)) | **Hit, measured.** A long-lived server kept a deleted `Exists()`-guarded import in 8–9 of 10 rounds. `.github/scripts/msbuild-evaluate.py` and `.github/scripts/abi-key.py:332` now force `DOTNET_CLI_USE_MSBUILD_SERVER=0` | Every script that evaluates two trees in turn needs the same setting |
| HTTP server activity emits semconv tags; `StatusDescription` unset ([page](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/11/http-activity-otel-semconv)) | **Hit.** `Plugins:src/Memex.Portal.ServiceDefaults/ServiceDefaults.cs` | Upgrade the instrumentation package (section 6) |
| HttpClient metrics become observable counters | **Hit** through `AddHttpClientInstrumentation` in the same file | Check the dashboards |
| `ChangeToken.OnChange` async overloads rebind Task-returning callbacks ([page](https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/11/changetoken-onchange-async-overloads-rebind-callbacks)) | Three call sites: `src/MeshWeaver.Graph/Configuration/ActionHandlerRuleNodeType.cs:124`, `src/MeshWeaver.Mesh.Contract/Features/FeatureFlags.cs:158`, `Plugins:src/MeshWeaver.AI/Supervision/ThreadSupervisor.cs:287`. The two core callbacks are `void` lambdas (`() => observer.OnNext(Read())`), so they do not rebind. The added overload also made a `cref` ambiguous (CS0419), which the v4 PR fixed. | None beyond the `cref` fix |
| `TimeOnly`/`DateOnly.TryParse` throw for an invalid `DateTimeStyles` or format ([page](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/dateonly-timeonly-tryparse-argumentexception)) | `src/MeshWeaver.Hosting/Persistence/Query/QuerySortKey.cs:111` passes `DateTimeStyles.None`. `Plugins:src/MeshWeaver.AI/ModelCredit/ModelCreditRateFeed.cs:589` and `Plugins:src/Memex.Portal.Shared/Settings/WhatsNewSettingsTab.cs:176` use `TryParseExact` with formats written in the code | Not affected: all arguments are constants |
| CRC32 validation when reading ZIP entries ([page](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/ziparchive-entry-crc32-validation)) | **Hit.** `BundleReader.cs` (`ReadAll`), `ServedModuleBytes.cs` (`Inflate`), `MeshOperations.cs:4483` (import) | Desired. A corrupt bundle now throws on read. Confirm each reader surfaces that as a refusal on its observable error arm, not as an unhandled fault. |
| `DeflateStream`/`GZipStream` write header and footer for an empty payload ([page](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/deflatestream-gzipstream-empty-payload)) | No direct use in `src/`. Whether `ZipArchive` routes an empty entry through it, which would change content-addressed bundle bytes between 10 and 11, is **not verified** | Expected anyway across a major (bundles are foreign across the framework identity). Pin determinism within v4 with the existing bake determinism tests. |
| `Math.Round` returns correctly rounded results | Display-only call sites (`src/MeshWeaver.Mesh.Contract/BootProgress.cs`, `src/MeshWeaver.Compiler.Pipeline/CpuLaneBound.cs`) | None |
| `Nullable.GetUnderlyingType` throws for custom `Type` subclasses | `src/MeshWeaver.Reflection/TypeExtensions.cs:241-275` passes runtime types | **Not verified** that no custom `Type` subclass (for example a metadata-load-context type) reaches it |
| `closed`/`union` contextual keywords; `[with(…)]` rebinding ([page](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/breaking-changes/compiler%20breaking%20changes%20-%20dotnet%2011)) | No declaration named `union`/`closed` and no `[with(` element in core `src/`, `samples/` or the Plugins checkout | Re-check in the live-mesh sweep, since in-mesh sources are not all in these trees |
| `dotnet test` needs a writable VSTest artifacts base in containers ([page](https://learn.microsoft.com/en-us/dotnet/core/compatibility/containers/11/vstest-artifacts-base-directory)) | `deploy/core-tests/Dockerfile` builds on `mcr.microsoft.com/dotnet/sdk`, and "the official .NET SDK container images set this variable". `global.json` selects Microsoft.Testing.Platform | None for that image. A runner image on another base must set `VSTEST_ARTIFACTS_BASE_DIRECTORY`. |
| Restore no longer looks for a higher version in the global packages folder ([page](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/11/restore-global-packages-folder-version-search)) | Affects the warm package caches (`.github/nuget-warm/`) only when a requested version is missing from the sources | Pins name versions that exist on a source, and a missing one is now NU1102 instead of a silent substitution |
| Minimum hardware | Section 7 | None known |

Changes on the list that do **not** touch us (grep found no use): named mutexes, `Environment.GetCommandLineArgs`,
`ZipArchive.CreateAsync`, `PackagePart`, `NamedPipeServerStream`, `UnmanagedCallersOnly`, Tar,
Cosmos/SQL Server/SQLite in EF Core 11 (no EF Core in the platform), the removed `ConcurrencyLimiter`
middleware, and MVC compatibility options.

## Recommended order

1. **Land the retarget first** (the v4 PR). Nothing here starts before a sealed 4.0 set exists.
2. **Measure before adopting.** Run `InlineResumptionMechanismTest` with runtime-async on, run the
   `ThreadPoolQueueCensus` attribution test on 11, run the probe compile of `union` through the
   portal compiler, and run the live-mesh sweep for in-mesh callers and keyword collisions. Each one
   decides whether a later step is safe.
3. **Observability hygiene:** upgrade the instrumentation package, check the dashboards, and enable
   `TrackStatistics` on the partition-hub cache with a per-mesh `IMeterFactory`.
4. **`closed` hierarchies on internal outcome types** (`DetachedReplyOutcome<T>` first), then the
   additive reshape of `NodeReadOutcome` and `PermissionCheckOutcome` that keeps every in-mesh call
   site compiling.
5. **`RequestCircuitPauseAsync` in the circuit drain** (MeshWeaver.Plugins), after the two-replica
   test settles the reload question.
6. **Runtime Async in `test/`**, one project at a time, for the stack traces. `src/` only if the
   measurements of step 2 show a gain worth the codegen change.
7. **Auto-pause**, only once the presence decision is made and the E2E is green.
8. **Not adopting:** zstd for any format another process reads, JSON Lines output, `FullJoin`.

See also: [Dependency Major Upgrades](../DependencyMajorUpgrades) (the differential method for a major
boundary), [Deploying Across Platform Versions](../DeployingAcrossPlatformVersions),
[Asynchronous Calls](../AsynchronousCalls), [Removing Observable-to-Task Bridges](../RemovingObservableToTaskBridges),
[Controlled IO Pooling](../ControlledIoPooling), [In-Mesh Warning Standard](../InMeshWarningStandard).
