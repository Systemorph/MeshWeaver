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

Until 2026-09-13 its message also named the cause: *"This is the cumulative Autofac Reflection.Emit
factory leak from non-shared MonolithMeshTestBase classes."* This page records the occurrence on
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

## The rule

> A guard's message states what it MEASURED and where the cause can be read. It does not name the
> cause. The watchdog measures RSS against a threshold; the trace measures which class was active
> and what each released; neither measures Autofac. A message that names a mechanism its
> instrument cannot see sends the reader to the wrong repository of fixes — here, to the mesh
> classes that were flat, away from the plain ones that were not.

The same rule as [Controls That Cannot Fail](../ControlsThatCannotFail) and
[Reading a Disposal Stall Verdict](../DisposalStallVerdicts), one instrument over.

## Where the code is

| Piece | File |
|---|---|
| the watchdog | `test/MeshWeaver.Hosting.Monolith.TestBase/MonolithMeshTestBase.cs` (`_memWatchdog`, `MemCriticalBytes`) |
| the shared reference set | `src/MeshWeaver.Compiler/CompileReferences.cs` (`Default`) |
| the test-side filter | `test/Memex.Portal.Shared.Test/PlatformReferences.cs`, `test/MeshWeaver.Compiler.Pipeline.Test/PlatformReferences.cs` |
| where the trace lands on CI | `.github/workflows/dotnet-test.yml` → `collected-logs/_meshweaver-test-trace.log`, `_meshweaver-memory-delta.log` |
