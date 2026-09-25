---
Name: Blocking Leaves Off the ThreadPool
Category: Architecture
Description: Every IIoPool ran its blocking leaves on borrowed ThreadPool workers under caps up to 256, so a burst of network-volume reads, bundle reads or git waits could hold every worker a silo's grain turns need; and a kernel code cell's compile ran on one. What Orleans' "Thread Pool execution stalled" readings on memex-cloud say was GC and what was blocking, the fix, and what it does not explain.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M4 12h10"/><path d="M4 18h16"/><circle cx="19" cy="12" r="2"/></svg>
---

# Blocking Leaves Off the ThreadPool

A portal silo has ONE shared .NET ThreadPool: Orleans grain turns, routing legs and every reactive
continuation run on it, and its minimum worker count is the core count (6 on a portal pod). Past the
minimum it grows slowly. Orleans' `LocalSiloHealthMonitor` measures how late a work item queued to that
pool starts, and logs `.NET Thread Pool execution stalled for N s` when it is more than a second late.
Three things produce that line: a GC pause (every managed thread stops), a machine pause (CPU
throttling, a stopped container), and genuine starvation, where every worker is busy or blocked and the
queue waits for the pool to inject a thread.

## What the readings on memex-cloud say (#5388)

Measured with `Logs` InstanceActions on memex.meshweaver.cloud (`Ops/logs-memexcloud-20260925-5388-*`).
Each stall produces two lines (the stall and a `Self-monitoring … degraded` line).

| window | images (core) | stall readings | longest |
|---|---|---|---|
| 09-23 14:38–17:49Z (3.2 h) | ci.9258–ci.9262 (`9a61fed964`) | the 48 h read was CUT at 1000 lines inside this window: about 500 readings; 204 landed and parsed, 21 of them ≥ 5 s | 10.2 s |
| 09-24 06:58–08:13Z (a roll) | ci.9263 survivors (`76cf776847`), the next ReplicaSets | 26 readings | 3.8 s |
| 09-24 08:41Z – 09-25 04:55Z (20.2 h) | ci.9291 (`140c5fb7ae`) | **4 readings** | 1.5 s |

So the 1–9 s family the ticket describes belongs to the `9a61fed964` images. Between them and the
current image are, among others, #5635 (a same-silo grain call no longer round-trips every delivery
through JSON — CPU and allocation on the pool), #5571 (the compile-state satellite write per NodeType
activation), #5615 (live re-queries coalesced) and Plugins#2329 (the post-boot Store sweep batched).
This page does not establish which of them removed how much.

**GC or blocking, on the current image.** The `[LIVENESS]` heartbeat runs on a dedicated thread every
10 s and logs the GC pause accumulated in each tick; a tick that arrives late prints `OVERRAN`. Over the
20 h from 09-24 08:59Z no tick OVERRAN on any pod (the same query over 48 h finds 21, so the pattern does match).
For the three stalls on pod `…589ff8f895-ghgjt`:

| stall | tick it falls in | GC pause in that tick | gen2 | pool threads / pending | reading |
|---|---|---|---|---|---|
| 13:23:18Z, 1.05 s | 1680 | **1.25 s** | +1 | 16 / 0 | GC pause |
| 14:16:05Z, 1.40 s | 1996 (1995 before) | **0.82 s** (0.48 s) | +0 (+1) | 10 / 1 | mostly GC; replicas were joining |
| 19:29:49Z, 1.42 s | 3876 | 0.03 s | +0 | 10 / 0 | **not GC**: the heartbeat was on time, so no machine pause either — the pool itself was starved |

Two of three are GC pauses (heap steps are #5555's subject); one is genuine starvation at a quiet
moment. The heartbeat samples the pool once per tick, so it cannot say what held the workers.

## What could hold the workers: blocking leaves on borrowed threads

`IIoPool.InvokeBlocking` is the sanctioned place for blocking work — file walks on the module volume,
bundle reads and zip inflates, `git` process waits, prebuilt bundle reads at boot. Its contract says the
work cannot starve Orleans. Its implementation did not keep that promise: the limited-concurrency
scheduler (adapted from the Docs sample) BORROWED ThreadPool workers and only capped how many. The caps
are resource caps, not thread budgets — `FileSystem` 256, `Blob` 128, `Http` 16, `Compile` and the
default the core count, `Process` 4 — so a burst could hold every worker the silo had, and the only way
out was the pool's thread injection. Only the CPU lane (`CompileCpu`, [Compiling Off the
ThreadPool](../CompileOffTheThreadPool)) started threads of its own.

A second holder was CPU, not blocking: a kernel code cell ran as ONE async leaf of the `Compile` pool,
so its whole Roslyn build + emit ran on a pool worker before the leaf's first await, with Roslyn's
default `ConcurrentBuild` fanning it out further — the same shape the NodeType compile was fixed for.

## The fix

- **Every pool's blocking leaves run on threads the pool starts itself** (`mw-io-lane`; the CPU lane
  keeps `mw-cpu-lane`). Same scheduler, same cap, same queue — one thread per concurrent drain loop,
  exiting when the queue is empty, so an idle pool holds no thread. The async entry points (`Invoke`,
  `InvokeStream`) are unchanged: an async leaf yields its thread at every await.
- **A kernel cell's compile is its own CPU-lane leaf** (`ScriptSession.Compile`, `ConcurrentBuild`
  off); only the cell's execution, user code that awaits, stays an async leaf of the `Compile` pool.

A continuation that runs synchronously after a blocking leaf (a `.Select` behind `InvokeBlocking`) now
runs on the leaf's own thread rather than a pool worker. That is where the bundle endpoint's zip
assembly (`PluginBundleEndpoints.BuildResult`) and module walk run today.

Tests, each with a negative control:

- `BlockingLeavesStayOffTheThreadPoolTest` (Hosting.Test) — six parked leaves on each of the
  `FileSystem`, `Http`, `Process`, `Compile`, `Blob` and default pools at cap 2: at most 2 run, all 6
  run, none on a pool worker, all on `mw-io-lane`; the control, the borrowing scheduler, runs all 6 on
  pool workers.
- `KernelCellCompileStaysOffTheThreadPoolTest` (Compiler.Pipeline.Test) — see
  [Compiling Off the ThreadPool](../CompileOffTheThreadPool).

## Not established

- **What held the pool at 19:29:49Z** on `…-ghgjt`. The fix removes one mechanism that can; it was not
  observed doing so there.
- **Still on the pool by design:** the language service's completion and hover requests (Roslyn's async
  services schedule their own work), CPU-heavy synchronous code inside handlers and grain turns
  (serialization of large payloads, view construction), and `IoPool.Unbounded`'s `InvokeBlocking`, the
  fallback for a host with no registry, which still uses `Task.Run`.
- **Which change between `9a61fed964` and `140c5fb7ae`** accounts for the drop from ~500 readings in
  3.2 h to 4 in 20.2 h. The windows also differ in load: the first is a day of rolls and heap steps.

## Related

- [Controlled IO Pooling](../ControlledIoPooling) — the pools and their entry points.
- [Compiling Off the ThreadPool](../CompileOffTheThreadPool) — the CPU lane.
- [Reading a Routing Saturation Report](../ReadingARoutingSaturationReport) — the routing gauge that
  sees the same thread shortage from the other side.
