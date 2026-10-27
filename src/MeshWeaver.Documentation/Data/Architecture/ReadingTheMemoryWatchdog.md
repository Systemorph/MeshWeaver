---
Name: Reading the Memory Watchdog
Category: Architecture
Description: >-
  The test-host memory watchdog FailFasts a shard at 6 GiB RSS and used to name its cause in the
  message. On #4127 its own trace refuted that name — every mesh class sat flat, and the growth was
  two steps that coincided with plain xUnit classes compiling with Roslyn against a 520-assembly
  reference set they rebuilt per call. How to read the trajectory, what each field measures, and the
  shared reference set that removed the steps.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 17l4-8 4 4 4-10 4 6"/><path d="M3 21h18"/></svg>
---

# Reading the Memory Watchdog

`MonolithMeshTestBase` carries a memory watchdog: a timer that samples the test host's working set
every few seconds, writes a `MEM_WATCHDOG` heartbeat into the test trace, and on CI `FailFast`s the
host when RSS crosses 6 GiB — before the runner's OOM killer turns the shard into a silent
wall-clock timeout. The signal it produces is `exit=134 SIGNAL SIGABRT` plus a synthetic
`<Project>.HOST_CRASHED` trx entry.

Until 2026-09-13 its message also named the cause: *"This is the cumulative [container] Reflection.Emit
factory leak from non-shared MonolithMeshTestBase classes."* (the container then in use was Autofac,
since removed in MeshWeaver#7145) This page records the occurrence on
which the watchdog's own trace refuted that sentence, what the trace actually shows, and the rule
for reading one.

## The occurrence (#4127)

Core run 34704176793, `Run tests (shard 5)`, `Memex.Portal.Shared.Test`, 2026-09-12:

```
MEM_CRITICAL_FAILFAST rss=6384MiB threshold=6144MiB managed=294MiB
                      vmsz=44384MiB rssAnon=6102MiB rssFile=125MiB
```

`managed=294MiB` against `rssAnon=6102MiB` reads, at first sight, as the message says: growth
outside the managed heap, i.e. native memory pinned by something GC cannot reclaim. The heartbeat
trajectory in `collected-logs/_meshweaver-test-trace.log` says otherwise:

| time | rss | what the trace shows |
|---|---:|---|
| 16:39:29 → 16:39:59 | 276 → 607 MiB | ordinary ramp through the first mesh classes |
| 16:40:01.654 | 609 MiB | `LocalSourceContentVersionTest` `DISPOSE_MEM` — the last mesh class before the gap |
| 16:40:04 → 16:40:09 | **1601 → 2407 MiB** | **no `MonolithMeshTestBase` class active**; gen2 collections 11 → 26 |
| 16:40:12.548 | 2417 MiB | `FetchedSourceStillSeedsOnceTest` `CTOR` — the next mesh class |
| 16:40:12 → 16:42:01 | 2300 → 2495 MiB | ~40 mesh classes, **every `DISPOSE_MEM` Δ within ±3 MiB**, `alc=1` throughout |
| 16:42:01.143 | 2495 MiB | `SitemapDescentTest` `DISPOSE_MEM` — the last line before the crash |
| 16:42:02.9 | — | stderr: `[MeshWeaver.Mesh.FallbackModule] 'MeshWeaver.Test.ParkedViewPack' …` |
| 16:42:04.922 | **6384 MiB** | `FailFast` — `vmsz` +4 GB in 3.8 s, no mesh class active |

Two **steps**, not a ramp. Both fell in windows with no `MonolithMeshTestBase` class between its
`INIT_MEM` and `DISPOSE_MEM` lines, and every mesh class on the shard released what it took. A
cumulative per-class leak cannot produce that shape.

`MeshWeaver.Test.ParkedViewPack` is a module name `ModulePlatformLinkTest` writes — a plain xUnit
class whose `Emit` helper compiled with Roslyn against **every entry of
`TRUSTED_PLATFORM_ASSEMBLIES`**, ~520 assemblies and ~177 MB of PE images (the test's own bin: 204
DLLs / 82 MB; the runtime: 172 / 66 MB; ASP.NET Core: 141 / 29 MB), through
`MetadataReference.CreateFromFile` **per call**. Roslyn reads every reference's metadata when it
binds a compilation, so each call re-read the whole platform into memory that only a finalizer
releases. The arithmetic closes: 19 emits × 177 MB ≈ 3.4 GB against the +3.9 GB step;
`ModuleLinkVersionTest`'s 11 emits ≈ 1.9 GB against the +1.8 GB step in the first gap. The second
occurrence, shard 2 of the same PR (`MeshWeaver.Compiler.Pipeline.Test`, `managed=41MiB`), carries
five classes of the same shape — one of them rebuilding the set as an **instance field xUnit
re-creates for every test**, under a comment explaining that it is "never static".

What decided pass or fail between the two attempts was finalizer timing against the step: attempt 2
passed with no push. That is why it read as a rate rather than a defect.

## What the fields measure

| field | source | what it is — and is not |
|---|---|---|
| `rss` | `Process.WorkingSet64` | resident pages of every kind: managed heap (live AND committed-but-empty), native heap, mapped files, JIT code, stacks |
| `managed` | `GC.GetTotalMemory(false)` | the GC's estimate of LIVE managed bytes. **Not** what the GC has committed — a heap that grew to absorb a burst and has not decommitted counts in `rssAnon` exactly like a native block |
| `gcCommitted` / `gcHeap` | `GC.GetGCMemoryInfo()` | added on #4127: the committed and total heap sizes, so `managed` small + `rssAnon` large can be split into "the GC is holding it" and "something native is" |
| `rssAnon` | `/proc/self/status` (Linux) | anonymous resident pages — native heap, GC heap commit, JIT code, stacks. Not file-backed |
| `rssFile` | `/proc/self/status` (Linux) | file-backed resident pages — memory-mapped assemblies, including an ALC's loaded images |
| `alc=` / `asm=` | the trace's `INIT_MEM`/`DISPOSE_MEM` | collectible load contexts alive / assemblies loaded — the fingerprint of an ALC that did not unload |

The thresholds are unchanged: soft `MEM_PRESSURE` at 4 GiB, `FailFast` at 6 GiB, CI only. Raising
either would be a bound-widening.

## How to read a trajectory

1. **Find the shape.** Take the `MEM_WATCHDOG` lines (every 5 s) and the `INIT_MEM`/`DISPOSE_MEM`
   pairs. A **ramp** whose `DISPOSE_MEM` deltas are positive across many mesh classes is per-mesh
   retention — an ALC that did not unload (`alc=` climbing; see
   [Debugging Native Crashes](../DebuggingNativeCrashes) for the `LoaderAllocator` root walk), a
   container root, a hosted service that outlived its mesh. A **step** inside a window with no
   mesh class active is a plain test class: a Roslyn compile, a large allocation, a file read into
   memory.
2. **Name the class in the gap.** The test reporter prints a test's output when it finishes and
   prints nothing for a test that wrote none, so a plain class is invisible in the console log. The
   stderr lines the runtime writes (`[MeshWeaver.Mesh.FallbackModule]`, `[ModuleSet]`) carry names
   the test chose; grep the test tree for them. The xUnit `-list classes` order is not the run
   order, so do not infer the neighbour from the source tree.
3. **Check the arithmetic before believing the mechanism.** Count the calls in the window and the
   bytes each would take; a mechanism that does not reproduce the step's size is not the mechanism.
4. **Do not read `managed` against `rssAnon` as "native".** Read `gcCommitted` first.

## The fix

The tests now share the process-wide reference set production compiles against —
`CompileReferences.Default`, built once at type load and allowlisted as the one Roslyn reference
holder — filtered per call (`PlatformReferences.Platform(excludeReference)`) instead of rebuilding
it. Roslyn caches each reference's metadata and symbol table against the instance, so the platform
is read once per process. Measured on one machine (`/usr/bin/time -l`, peak RSS of the class run
alone, before → after):

| class | tests | peak RSS | wall time |
|---|---:|---:|---:|
| `ModulePlatformLinkTest` | 23 | 1054 → 350 MiB | 3.7 → 0.9 s |
| `ModuleLinkVersionTest` | 11 | 615 → 367 MiB | 5.1 → 2.0 s |
| the three `Memex.Portal.Shared.Test` classes together | 47 | 1250 → 457 MiB | 7.8 → 3.0 s |
| `ConfiguredModuleActivationTest` | 34 | 1109 → 322 MiB | 6.0 → 1.2 s |
| `CompileFailureReportedOnceTest` | 8 | 727 → 403 MiB | 0.8 → 0.5 s |
| `CompileFailureReportOrderTest` | 5 | 729 → 287 MiB | 0.6 → 0.3 s |
| `EmitToDiskWithRetryTest` | 10 | 680 → 300 MiB | 1.1 → 0.5 s |

A mesh class on the same machine peaks at ~180 MiB, which is the floor the emitting classes now sit
just above.

Two sites of the same shape remain at low volume and are named rather than changed here:
`MeshTestSuiteTest.ProbeReferences` (`MeshWeaver.PluginTester.Test`, 7 tests) and
`ModulesAssemblyLoadContextTest.Emit` (`MeshWeaver.Hosting.Orleans.Test`, 3 tests).

## The second occurrence: disposed meshes that stayed reachable

Plugins #4230, run 40214543622, `portal-hosts (Memex.Portal.Shared.Test)`: the host aborted after
10 min 42 s at `rss=6281MiB managed=4932MiB gcHeap=4991MiB`. The suite has no baseline, because
the shard had not run in the previous 25 runs. This time the trajectory WAS a ramp across mesh
classes.

**Read `DISPOSE_MEM` with a forced collection.** Without `MESHWEAVER_TEST_FORCE_GC=1`, `managed=`
counts garbage that has not been collected yet. A workstation-GC host can go minutes between gen-2
collections, so the ramp then mixes retention with uncollected garbage. With the variable set,
consecutive `DISPOSE_MEM` lines are live bytes after a full compacting collection. Measured
locally, that live figure still climbed from 221 MiB to about 1060 MiB in the first two minutes.

**Then ask the heap who holds a disposed mesh.** Take a heap dump of the running host:
`dotnet-dump collect --type Heap`, with `TMPDIR` set to the host's, because the diagnostic socket
lives there. Read it with `dumpheap -stat -live -ignoreGCState`, then `gcroot` one per-mesh
singleton (here `TypeDisplayMetadataCache`, one per mesh-root `TypeRegistry`) for every instance,
and classify the first non-infrastructure object on each path. The reading three minutes in:

| what | count |
|---|---:|
| live heap | 1.14 GB, 835 MB of it strings |
| meshes actually running (per-mesh `NodeAssemblyLoadContext` maps) | 23 |
| live mesh-root registries (`TypeDisplayMetadataCache`) | 84 |
| … rooted through `IHttpClientFactory` (`ActiveHandlerTrackingEntry`, `DefaultHttpClientFactory`) | 68 |
| … rooted through a running mesh's stream-cache sweep | 14 |
| … rooted through `AccessService+AccessContextScope` in a timer's captured `ExecutionContext` | 1 |
| live `XmlMemberDoc` (the per-mesh XML documentation reader) | 1,032,764 |

Three retention edges, each fixed where it lives:

1. **`IHttpClientFactory`'s handler-expiry timer.** Each handler gets a two-minute lifetime,
   enforced by a `Timer` in the process-wide timer queue. The timer's state is the tracking
   entry, the entry holds the service scope the handler was built in, and the scope reaches the
   whole container. Disposed meshes therefore stayed reachable for two minutes, which is about 80
   at this suite's rate. Of 81 live entries, 61 were the `deployment-report` client, which every
   mesh creates in `DeploymentReportService`'s constructor. Test meshes now set an infinite
   handler lifetime (`MeshScopedHttpHandlers`, applied in `ConfigureMeshBase`), so the factory
   starts no timer. A production mesh lives as long as its process, so this retention never
   arises there.
2. **The test logger closed over the test.** `TestBase` registered
   `new XUnitFileLoggerProvider(() => FileOutput, …)`, which captured `this`. Every logger the mesh
   handed out could then reach the test instance, and from there its `Services`, the
   `MeshBuilder` and the mesh. Anything that keeps a logger alive past the mesh kept the whole
   mesh. The closure now captures the output helper alone.
3. **The access scope marker was the scope.** `AccessService` marks the flow with an
   `AsyncLocal` value. That value was the `AccessContextScope` itself, which holds the service,
   and so the service's logger. Every `ExecutionContext` captured inside a scope carried it: each
   timer, thread-pool item and Rx scheduled action started under an impersonation. The marker is
   now an opaque token.

What made each retained mesh expensive is a separate cost, and this fix does not address it.
XML documentation is now read once per mesh rather than once per process, about 10 MB per mesh
here. Retention multiplies that cost by the number of meshes held.

Measured on one machine, the full `Memex.Portal.Shared.Test` run (3158 tests, same 15
environment-bound local failures before and after), peak RSS sampled every 10 s:

| | peak RSS | wall time |
|---|---:|---:|
| before | 7941 MiB | 10 min 40 s |
| after | 4858 MiB | 10 min 6 s |

At the same point in a forced-GC run, the live heap was 1.14 GB before and 456 MB after. Live
mesh-root registries fell from 84, with 23 meshes running, to 30, with 29 running: no disposed
mesh remained reachable.

Regression tests: `TestMeshHttpHandlersDieWithTheMeshTest`, which carries its own negative
control, plus `TestLoggerDoesNotRootTheTestTest` (`MeshWeaver.Hosting.Test`) and
`AccessScopeDoesNotRootTheServiceTest` (`MeshWeaver.Messaging.Hub.Test`). Each of the last two
fails with its fix reverted.

### After both fixes (retention, plus the shared XML-doc text of MeshWeaver#7213)

The same suite, now run 4-way parallel (its `xunit.runner.json`), against a core that carries both
the retention fix and #7213's shared, interned doc text. It ran 3163 tests with none failing. Peak
RSS was sampled every 5 s in a run with no dump attached.

| | peak RSS | wall time | live heap ~3 min | strings | `XmlMemberDoc` | readers / mesh-root registries / running meshes |
|---|---:|---:|---:|---:|---:|---:|
| before | 7941 MiB | 10 min 40 s | 1.14 GB | 835 MB | 1,032,764 | — / 84 / 23 |
| retention fix only | 4858 MiB | 10 min 6 s | 456 MB | 298 MB | ~357k | 30 / 30 / 29 |
| both fixes | 1199 MiB | 5 min 56 s | 159 MB | 32.8 MB | ~348k | 29 / 29 / 28 |

The rows are not strictly like-for-like: the earlier two ran serially and the last one 4-way
parallel. Readers now track running meshes one to one, so no disposed mesh holds its docs. What
remains is the doc index of each running mesh: its dictionary nodes and entries, about 1.3 MB per
mesh. A production portal runs one mesh, so a lazier per-member snapshot would save about 1.3 MB
there at the cost of a cache and a second parse path. It was therefore not built.

## The rule

> A guard's message states what it MEASURED and where the cause can be read. It does not name the
> cause. The watchdog measures RSS against a threshold; the trace measures which class was active
> and what each released; neither measures the DI container. A message that names a mechanism its
> instrument cannot see sends the reader to the wrong repository of fixes — here, to the mesh
> classes that were flat, away from the plain ones that were not.

The same rule as [Controls That Cannot Fail](../ControlsThatCannotFail) and
[Reading a Disposal Stall Verdict](../DisposalStallVerdicts), one instrument over.

## Where the code is

| Piece | File |
|---|---|
| the watchdog | `test/MeshWeaver.Hosting.Monolith.TestBase/MonolithMeshTestBase.cs` (`_memWatchdog`, `MemCriticalBytes`) |
| test meshes' HTTP handler lifetime | `test/MeshWeaver.Hosting.Monolith.TestBase/MeshScopedHttpHandlers.cs` |
| the test logger provider | `test/MeshWeaver.Fixture/TestBase.cs` |
| the access scope marker | `src/MeshWeaver.Messaging.Hub/AccessService.cs` (`AccessContextScope`) |
| the shared reference set | `src/MeshWeaver.Compiler/CompileReferences.cs` (`Default`) |
| the test-side filter | `test/Memex.Portal.Shared.Test/PlatformReferences.cs`, `test/MeshWeaver.Compiler.Pipeline.Test/PlatformReferences.cs` |
| where the trace lands on CI | `.github/workflows/dotnet-test.yml` → `collected-logs/_meshweaver-test-trace.log`, `_meshweaver-memory-delta.log` |
