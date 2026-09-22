---
Name: Compiling Off the ThreadPool
Category: Architecture
Description: A NodeType compile in a serving silo used to occupy the shared .NET ThreadPool twice over — its async leaf ran Roslyn on a pool worker, and Roslyn's ConcurrentBuild fanned every emit out onto the pool from whatever thread started it. That pool is the one grain turns and routing legs run on, which is what the "waiting for a pool slot ~60, subscribing ≤1" routing back-pressure signature measures. The mechanism, the probe that measured it, the deterministic test that pins it, and what this does not yet explain.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="16" rx="2"/><path d="M9 9h6v6H9z"/><path d="M9 1v3"/><path d="M15 1v3"/><path d="M9 20v3"/><path d="M15 20v3"/><path d="M20 9h3"/><path d="M20 14h3"/><path d="M1 9h3"/><path d="M1 14h3"/></svg>
---

# Compiling Off the ThreadPool

A portal silo has ONE shared .NET ThreadPool. Orleans grain turns, the routing pool's subscribe legs
(`IIoPool.SubscribeThroughPool`, see [Reading a Routing Saturation Report](../ReadingARoutingSaturationReport))
and every reactive continuation that delivers a cross-hub response all run on it. Its minimum worker
count is `Environment.ProcessorCount` — **6** on an AKS portal pod (CPU limit 6) — and past that it
grows by hill-climbing, roughly one or two threads a second. Anything that holds pool workers for
hundreds of milliseconds therefore stops the silo delivering messages while nothing is stuck.

## The two ways a compile held the pool

**1. The CPU-bound half of the compile ran on a pool worker.** `MeshNodeCompilationService` starts a
compile through `OnThreadPool(() => CompileAsync(...))`. `CompileAsync` returns a `Task`, so overload
resolution binds the `Func<Task<T>>` overload — `Task.Run` — and not the `Func<T>` overload that uses
`CompileThread`'s dedicated thread. `CompileAsyncCore` then awaits the debug source write
(`EnableSourceDebugging` is on by default) and, for `#r "nuget:"` sources, the NuGet restore; after
the first await the rest — parse, source generators, bind, emit — resumed on whatever pool worker the
await completed on. So every production emit ran on the pool, although `CompileThread`'s own comment
said the compile leaf did not.

**2. Roslyn fanned each emit back out onto the pool.** `CSharpCompilationOptions.ConcurrentBuild`
defaults to `true`. With it, Roslyn compiles declarations and method bodies through `Parallel.For` and
`Task.Run` on `TaskScheduler.Default`, with no degree-of-parallelism cap — from whatever thread
started the emit. A dedicated thread therefore kept only the CALLER off the pool, never the work.

## The measurement

A standalone probe (`Environment.ProcessorCount = 6`): every 5 ms queue one ThreadPool work item and
record how long it waits to START, while N concurrent emits of a 20-class × 20-method source run.

| N | launch | ConcurrentBuild | probe p50 | probe p99 | pool queue max |
|---|---|---|---|---|---|
| 0 | — | — | 0.01 ms | 0.04 ms | 1 |
| 1 | `Task.Run` | on (was prod) | 7.7 ms | 41 ms | 19 |
| 1 | dedicated | off (now) | 0.01 ms | 0.40 ms | 1 |
| 6 | `Task.Run` | on (was prod) | 46 ms | 417 ms | 115 |
| 6 | `Task.Run` | off | 326 ms | 687 ms | 114 |
| 6 | dedicated | on | 33 ms | 354 ms | 114 |
| 6 | dedicated | off (now) | 0.01 ms | 0.18 ms | 1 |
| 12 | `Task.Run` | on (was prod) | 124 ms | 812 ms | 147 |
| 12 | dedicated | off (now) | 0.02 ms | 0.68 ms | 1 |

Both halves are needed: switching `ConcurrentBuild` off alone pins one pool worker per compile (row
"6 / Task.Run / off" is the WORST row), and a dedicated thread alone still fans out. Wall time for the
batch is unchanged at N ≥ 6 (729 ms vs 799 ms at N = 6; 1404 vs 1216 ms at N = 12); a lone compile
takes longer on one core (about 150 → 500 ms for this source), which is the price of not borrowing the
silo's grain-turn threads to finish it.

## The fix

- `EmitPipeline.CreateRunCompilationOptions()` = the canonical `CreateCompilationOptions()` with
  `ConcurrentBuild` off. `CreateEmitCompilation` and the compilation inputs the diagnostics and
  language-service compilations are built from use it.
- The canonical factory is **unchanged**, and the content key (`EmitPipeline.OptionsFingerprint`) still
  renders it — `ConcurrentBuild` is a scheduling choice that does not change the emitted bytes, and
  changing the fingerprinted factory would re-key every NodeType in the fleet.
- `CompileAsyncCore` keeps its IO prefix and hands the synchronous half (`EmitCompiled`) to
  `CompileThread.Run`.

`RoslynEmitStaysOffTheThreadPoolTest` pins it deterministically: an `AsyncLocal` change handler counts
every ThreadPool worker that starts running under the emit's `ExecutionContext` (Roslyn's fan-out tasks
flow it). With the run options the count is 0; the negative control — the same emit with
`ConcurrentBuild` on — counts more than 0. A third case pins that the content key did not move.

## What the production evidence does and does not say

The routing back-pressure flood of 2026-09-22 (core issues #5226–#5307) was read against the pods named
in each issue's evidence table. Of the **65** distinct crossings quoted in those bodies that printed
`waiting for a pool slot` ≥ 45 with `subscribing` ≤ 1 — the thread-shortage reading — **63** were
logged while two or more ReplicaSets of the same deployment were live within ±10 minutes, i.e. during
a roll (memex-cloud went through five ReplicaSets between 10:50Z and 13:05Z; memex rolled at 17:33Z).
The two exceptions are both memex at 18:20Z, where no second ReplicaSet appears in the sample. The
starved pod was as often a SURVIVOR of the old ReplicaSet (`5c89c7f5f9-sddb5`, 12:34–13:02Z, while
`7cc85f47c` rolled in) as a freshly booted one (`7bc86fb6f-qnznf`, 11:32–11:43Z), so this is "during a
roll", not "on the new pod". The `[ReleasePostCondition]` compile lines on `…-sddb5` at 12:38Z fall
inside its starvation window. A roll is exactly when compiles happen on serving silos: a framework
identity change makes NodeTypes framework-stale, and release requests and first activations compile.

Read the 63/65 against its base rate: the sample is the log lines the incident filer quoted (at most
five per issue), and on memex-cloud a roll was in progress for most of the sampled day, so "during a
roll" is the expected state of most samples, not a rare one.

What was NOT established: the portal's own ThreadPool metrics for those windows (the control instance
was unavailable, so no `Logs`/Prometheus read was taken), and therefore how much of each window's
starvation the compiles account for. Other CPU on a rolling survivor — re-activating the grains of the
pods being replaced — competes for the same cores and is not addressed here. The Compile `IIoPool`'s
blocking leaves (the language service) still run on pool workers by that pool's design. And nothing
bounds how many DISTINCT NodeTypes compile at once — the service single-flights per type only — so a
burst of hundreds now costs hundreds of dedicated threads (it cost as many pool-injected threads
before); a bound needs an `IIoPool` lane over dedicated threads, since the Compile pool's gate
deadlocked the compile against itself and its leaves run on the pool.

## Related

- [Reading a Routing Saturation Report](../ReadingARoutingSaturationReport) — which gauge sees the wait.
- [Node Type Compilation](../NodeTypeCompilation) — the compile pipeline this changes.
- [Controlled IO Pooling](../ControlledIoPooling) — the pools and why the compile leaf is not on one.
