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
- `CompileAsyncCore` keeps its IO prefix and hands the synchronous half (`EmitCompiled`) to the
  **CPU lane** (below). The first cut of this fix handed it to `CompileThread.Run` — off the pool, but
  one new thread per distinct NodeType compiling at once, which a roll turns into hundreds.

### The CPU lane (`IoPoolNames.CompileCpu`)

Every purely CPU-bound Roslyn leaf goes through ONE bounded lane: an `IIoPool` whose
`InvokeBlocking` leaves run on at most `IoPoolOptions.CompileCpu` (default: processor count) threads
the lane starts itself — named `mw-cpu-lane`, never ThreadPool workers. The bound is the pool's
ordinary blocking scheduler (`LimitedConcurrencyLevelTaskScheduler`) with `dedicatedThreads: true`:
the same queue and the same cap, only the drain loops run on their own threads. A burst of hundreds of
distinct-type compiles now QUEUES behind the cap instead of becoming hundreds of threads, and the OS
time-slices the lane's threads fairly against the ThreadPool the grain turns run on.

🚨 **The compile's time bound does not count the queue.** `CompilationCacheOptions.RoslynCompileTimeout`
bounds the `roslyn-compile` leg so that a hung leaf cannot park a NodeType at `Compiling` for ever. Its
clock used to start when the leg was subscribed, so a compile still WAITING for a lane slot when the
bound elapsed failed terminally as "hung" although it had not started. Measured in the combo
verification lane (#4413) on a CI runner: one package dispatched 47 types at once, the lane
drained them over five minutes, and the ten still queued at the bound were parked at
`CompilationStatus=Error`, which read as a red package. Now (`CpuLaneBound`) the bound has three
phases, each with its own named expiry:

- The IO prefix is bounded until the emit is QUEUED.
- The queue carries no clock on the wait itself. A compile there fails only when the lane completes NO
  leaf for two bounds (`IoPool.AdmissionsCompleted` does not move). That is a lane whose every slot is
  held by work that ignores cancellation, such as a source generator's constructor. A draining queue,
  however long, is healthy, but a stalled one must still end in a terminal state rather than park the
  type at `Compiling`.
- The emit is bounded afresh from the moment the lane ADMITS it, and is cancelled when that bound
  fires, which frees its slot.

A caller that joins a compile already in flight times against the leader's queue and admission.

`CpuLaneBoundTest` pins each expiry against a real one-slot pool, and the draining case that must not
expire. `CompileBoundExcludesCpuLaneQueueTest` runs the draining case through the real compile service.

Its leaves:

- a NodeType's `EmitCompiled` (generators, bind, emit, artifact write) and the failure-path
  `CompileDiagnostics.DiagnoseInputs`;
- every language-service diagnostics bind — the node workspace (`GetDiagnostics`), a speculative
  proposal (`SpeculativeCompilation.Diagnose`) and a script cell. The IO half of each (NuGet refs,
  `Project.GetCompilationAsync`) stays on the `Compile` pool; only the bind moves. The script
  workspace now also binds with `ConcurrentBuild` off.
- a kernel code cell's build + emit + load (`ScriptSession.Compile`, #5388). The kernel used to run a
  cell as ONE async leaf of the `Compile` pool, so the whole Roslyn half ran on a ThreadPool worker
  before the leaf's first await, with Roslyn's default `ConcurrentBuild` fanning it out further. Now
  the compile is its own lane leaf with `ConcurrentBuild` off, and only the cell's EXECUTION — user
  code that awaits — stays an async leaf of the `Compile` pool.

🚨 **The lane is separate from the `Compile` pool on purpose.** `Compile` also runs kernel SCRIPTS — user
code that may activate a NodeType and wait for its compile — and cell-surface assembly loads. An emit
queued behind the same cap is the nested-gate deadlock the compile leaf already hit once (every slot
held by a script waiting for a compile that needs a slot). A lane leaf does nothing but compute: no
pool call, no mesh read, no wait on another leaf, so it cannot nest by construction. Keep it that way —
a leaf that needs IO splits it off, as the three diagnostics arms do.

### A kernel script holds no `Compile` slot (the `KernelScript` lane)

The paragraph above kept the EMIT out of the `Compile` pool, but the script's EXECUTION — and the
session warm-up wait in front of it (`KernelScriptReferences.GetReferencesAsync`, which awaits the
process-wide `SharedWarmup`) — were still async leaves of that same pool, holding one of its async
slots (default: one per processor, so two to four on a CI runner) for the cell's whole run. That is the
same nested-gate deadlock one level up: a running script can wait on another of the pool's ASYNC
leaves — another kernel session's warm-up or execution (a cell that runs a cell and waits for it), a
language-service workspace compile (`MeshNodeLanguageService`). With every slot held by a script waiting
on such a leaf, none is ever admitted and no script ever finishes. Every Activity involved then stays
`Running` for ever, and — because nothing faults — nothing is logged. Below saturation the same hold
still capped the process at one running cell per processor and queued the language service behind
user scripts.

Measured while pinning it: the pool's BLOCKING leaves (`InvokeBlocking` — prebuilt adoption's MVID read
in `PrebuiltAssemblySeeder.StandingBytesResolve`, the cell-surface loads in `CellSurfaceAssemblyProvider`)
are admitted by a separate limited-concurrency scheduler, not by the async gate, so a script holding an
async slot does NOT starve them. A script waiting on a NodeType activation is therefore not, by itself,
this deadlock; a script waiting on another cell is.

Both waits now run on their own lane, `IoPoolNames.KernelScript` (`IoPoolOptions.KernelScript`, 256 —
a drain hook and a runaway stop, not a throttle; the CPU half of a cell is bounded by `CompileCpu`).
**Rule:** a leaf that WAITS — on the mesh, on another leaf, on user code — never holds a `Compile` slot;
`Compile` leaves are short file/PE reads and Roslyn workspace IO.

`KernelScriptHoldsNoCompileSlotTest` (Graph.Test) pins it on a real mesh whose `Compile` pool has ONE
slot: a cell that runs another cell and waits for it completes, and a plain cell completes while the
pool's only async slot is held by other work. Both stay `Running` on the code before the change.

**The lane REFUSES, it does not queue.** Moving the waits off `Compile` alone only moves the cycle onto
the new lane: a parent cell holds its `KernelScript` slot while it waits, and the child needs a slot of
the same lane to warm up and execute — so with every slot held by a waiting parent a queued child is
never admitted, at any cap. The kernel therefore admits both leaves with `IoPool.InvokeOrRefuse`: a
free slot runs the leaf, a full lane fails it at once with `IoPoolSaturatedException`, and the refused
run's Activity reads `Failed` naming the refusal — which its parent sees and reports. Saturation becomes
a visible terminal instead of a silent queue. `KernelScriptLaneRefusesInsteadOfQueuingTest` pins it with
a ONE-slot lane: the child is refused and the parent completes with `inner Failed`; with a queue both
stay `Running`.

Still open (not the cause found here, recorded so it is not re-investigated blind): the cell-surface
resolution's store lookup (`IAssemblyStore.TryGetBuildPath` in `CellSurfaceAssemblyProvider.ResolveOne`)
carries no bound of its own. The file-system store answers synchronously, so it cannot park there; a
remote store that never answers would stall the submission before it runs.

### A C# run that the kernel did not settle is failed, not left `Running`

The dispatcher (`CodeNodeType`) used to post a C# cell's `SubmitCodeRequest` fire-and-forget, on the
assumption that the Roslyn kernel always settles the ActivityLog. It does — when it runs the script.
Every failure before that point (the kernel container's dispatch faulting and answering
`Success = false`, a `DeliveryFailure`, a routing `NotFound`) left the run `Running` for ever, silently.
The C# branch now observes the kernel's answer through `hub.Observe` exactly as the Python/JS branch
does, and writes any non-success onto the Activity as `Failed` with the reason (a keyed, localized
transcript entry, `activity.codeRun.failed`). The reconcile never overwrites a status the kernel
already settled. It adds no timeout: a kernel that never answers is a defect of its own, to be fixed
where it stalls — the deadlock above is one such stall, fixed at its root. Pinned by
`CSharpCellKernelFaultFailsTheActivityTest` (a kernel plug-in that faults) and its control
`CSharpCellRunKeepsTheKernelsVerdictTest` (the real kernel: success stays `Succeeded`, a script that
throws and a script that does not compile both end `Failed`, settled by the kernel with its own
reason — the exception text or the compiler diagnostic). That the reconcile never overwrites a run the
kernel settled is pinned by `ARunTheKernelCancelledAndSettled_IsNotOverwrittenWithFailed`.

🚨 **A request timeout is not a verdict on the run.** The kernel's reply is the END of the execution,
and each wait for it — the dispatcher's on the Code hub, the kernel container's on the Activity hub —
is bounded by its hub's `RequestTimeout` of silence. A healthy cell computing past that budget is still
running. So neither hop turns its own timeout into a failure: the dispatcher writes `Failed` only for a
delivery fault or a failed reply, and the container no longer answers `Success = false` when its own
wait expires. The Activity stays the authority and the kernel settles it when the script ends.
`LongCellOutlivesThe{Dispatchers,KernelContainers}RequestBudgetTest` pin each hop with a two-second
budget and a six-second cell; both read `Failed` while the script was still running before the change.

Three more holes the same review found, each of which could again leave a run wrong or `Running`:

- **A reconcile from a lagging copy.** The dispatcher's terminal guard reads ITS copy of the Activity,
  which can still say `Running` when a cancelled kernel's reply arrives — and since `Running` is the
  enum's zero value the merge patch carries no base value for `status`, so the owner accepted `Failed`
  over `Cancelled`. The reply now says whether the kernel already settled the run
  (`SubmitCodeResponse.ActivitySettled`, set on every executor reply after `Complete`); the dispatcher
  reconciles only a failed reply WITHOUT it, i.e. a run nobody will settle.
  `ARunTheKernelCancelledAndSettled_IsNotOverwrittenWithFailed` delays the kernel's terminal write
  behind its reply.
- **The idle claim ended with the reply wait.** The kernel host's idle-disconnect claim (#4422) was
  released when its bounded wait for the executor's reply ended, timeout included — so a quiet script
  that outlived both the request budget and the idle window had its hub and executor disposed mid-run,
  the submission pump stopped, and the Activity stayed `Running`. A timed-out reply wait now hands over
  to THAT SUBMISSION's own settlement signal (`KernelContainer.ReplyOrSettlement` over
  `KernelExecutor.ExpectSettlement`, keyed by the forwarded delivery's id and registered before the
  post), and the claim lasts until the executor has settled it. Never the Activity's status: one
  Activity carries many submissions (a markdown view's blocks, an `ExecutionManager` queue), and an
  earlier block's terminal status released a later, still-running block's claim at once.
  `AReplyWaitThatTimesOut_KeepsTheClaimUntilTheRunSettles` (Compiler.Pipeline.Test) crosses both
  budgets on the injected idle clock; `KernelIdleClaimIsPerSubmissionTest` (Graph.Test) does it end
  to end for sequential and queued blocks sharing one Activity.
- **An English-only refusal.** The lane's refusal is platform text, so the kernel records it as a keyed
  transcript entry (`activity.codeRun.kernelSaturated`, with the slot count) at its own failure boundary;
  the exception text stays for the host's diagnostics.

**The executor settles every run it owns (#7362).** Two gaps remained inside `KernelExecutor`, and
either could leave a run `Running` for good:

- **Disposal wrote nothing.** When the executor or its host went away (a recycle, the idle sweep after a
  crash, a process shutting down), its disposal stopped the submission pump and released the settlement
  waiters, but recorded no verdict. Every run in flight or queued stayed `Running`. A C# cell dispatched
  by `CodeNodeType` was then failed by the dispatcher with a delivery fault (*"Hub … was disposed before
  the response arrived"*), but nothing said why, and a run nobody observed stayed `Running`.
  - The executor now keeps every run it accepted and has not settled: its outcome sink, plus its own
    cancellation.
  - At the FIRST instant of teardown (`IMessageHub.ShuttingDown` — its own disposal, or the host's freeze
    cascading into it) it fails each of them through the same sink a script failure takes. That records
    `Failed` with a keyed reason, `activity.codeRun.kernelHostShutDown` (en + de), answers the caller and
    releases the settlement.
  - Only then is the run's token cancelled, so a still-running script is asked to stop. Cancelling first
    would record a user's cancel that never happened.
  - It cannot be done from the disposal registrants: those run in the ShutDown phase, after the host has
    quiesced, force-cancelled the forwarded submission and stopped taking the Activity's writes. A
    verdict written there never lands.
  - `Complete` is idempotent, so a run that settled first keeps its own verdict.
- **A cancel reached only the script pass, and only the newest run.** The cancel (the Activity's
  `RequestedStatus = Cancelled`) tripped one token: the one most recently handed in, which is a QUEUED
  run as soon as two are pending. That token reached only `RunOnePass`. A run parked in a preparation
  stage — the session warm-up, the cell-surface resolution, a NuGet restore — could not be cancelled at
  all.
  - The cancel now trips every pending run's token.
  - The preparation stages honour that token, so a cancel ends a run there `Cancelled` at once:
    - The session warm-up takes it inside its pool leaf, so the leaf is never abandoned while it could
      still write the session.
    - The cell-surface and NuGet stages are pure: they return what they resolved, run under
      `KernelExecutor.UntilCancelled`, and their result is published into the session in the next
      step, on the pump. A stage abandoned by a cancel writes nothing. A result that loses the race
      releases its leases, and the provider's collection (`CellSurfaceAssemblyProvider.CollectOwned`)
      disposes the leases it had buffered when it is unsubscribed.
    - A run whose token is already cancelled never subscribes a stage and never reaches the script.
  - A callback that a script registered on its token and that throws cannot stop the loops that cancel,
    or fail, the other pending runs.
  - A submission admitted while teardown is already running is failed at once and never queued.
  - The script pass keeps its cooperative token: abandoning a RUNNING script would let the next
    submission start beside it.

Pinned in Graph.Test on a real mesh with the real kernel:
- `KernelHostDisposedMidRunFailsTheRunTest`: a script parked on a gate when its Activity hub is disposed
  ends `Failed` with the keyed reason. Its control is a run that finished before its host went away and
  keeps `Succeeded`.
- `KernelCancelReachesAParkedStageTest`: a cell-surface provider that never answers parks the run in
  that stage, and a cancel ends it `Cancelled`.
- `KernelOrdinaryRunIsUnaffectedTest`: an ordinary run still succeeds with its value.

The first two fail on the unfixed executor: the run ends `Failed` without the reason, or stays
`Running` until the test's bound.

Tests, each with a negative control that fails on the unfixed code:

- `CompileCpuLaneIsBoundedAndOffThePoolTest` (Hosting.Test) — six parked leaves on a cap-2 lane: at
  most 2 run at once, all 6 run, none on a pool worker; the control, a pool that borrows ThreadPool
  workers with the same cap, runs all 6 on pool workers. With the registry building the lane without
  dedicated threads the first test fails. (Every other pool's blocking leaves now run on threads of
  their own too — [Blocking Leaves Off the ThreadPool](../BlockingLeavesOffTheThreadPool); the lane
  keeps its own name, `mw-cpu-lane`, so the two tests below can still tell it from the `Compile` pool.)
- `KernelCellCompileStaysOffTheThreadPoolTest` — a code cell's compile (`ScriptSession.Compile`, the
  CPU half the kernel now runs on the lane) hands no work to the pool and emits with `ConcurrentBuild`
  off; the control, the same cell's compilation with Roslyn's script default, fans out onto the pool.
  Fails when the compile is built without `WithoutConcurrentBuild`.
- `ServiceCompileStaysOffTheThreadPoolTest` — a real compile through the service reaches Roslyn on an
  `mw-cpu-lane` thread with `ConcurrentBuild` off. Fails when the emit is routed through the `Compile` pool.
- `LanguageServiceDiagnosticsRunOnTheCpuLaneTest` — all three diagnostics arms bind on `mw-cpu-lane`
  threads. Fails when the binds are routed through the `Compile` pool.
- `RoslynEmitStaysOffTheThreadPoolTest` pins the emit itself deterministically: an `AsyncLocal` change handler counts
every ThreadPool worker that starts running under the emit's `ExecutionContext` (Roslyn's fan-out tasks
flow it). With the run options the count is 0; the negative control — the same emit with
`ConcurrentBuild` on — counts more than 0. A third case pins that the content key did not move.

## What the production evidence does and does not say

The routing back-pressure flood of 2026-09-22 (core issues #5226–#5307) was read against the pods named
in each issue's evidence table. Of the **65** distinct crossings quoted in those bodies that printed
`waiting for a pool slot` ≥ 45 with `subscribing` ≤ 1 — the thread-shortage reading — **63** were
logged while two or more ReplicaSets of the same deployment were live within ±10 minutes, i.e. during
a roll (the public instance went through five ReplicaSets between 10:50Z and 13:05Z; the control
instance rolled at 17:33Z). The two exceptions are both on the control instance at 18:20Z, where no second ReplicaSet appears in the sample. The
starved pod was as often a SURVIVOR of the old ReplicaSet (`5c89c7f5f9-sddb5`, 12:34–13:02Z, while
`7cc85f47c` rolled in) as a freshly booted one (`7bc86fb6f-qnznf`, 11:32–11:43Z), so this is "during a
roll", not "on the new pod". The `[ReleasePostCondition]` compile lines on `…-sddb5` at 12:38Z fall
inside its starvation window. A roll is exactly when compiles happen on serving silos: a framework
identity change makes NodeTypes framework-stale, and release requests and first activations compile.

Read the 63/65 against its base rate: the sample is the log lines the incident filer quoted (at most
five per issue), and on the public instance a roll was in progress for most of the sampled day, so "during a
roll" is the expected state of most samples, not a rare one.

What was NOT established: the portal's own ThreadPool metrics for those windows (the control instance
was unavailable, so no `Logs`/Prometheus read was taken), and therefore how much of each window's
starvation the compiles account for. Other CPU on a rolling survivor — re-activating the grains of the
pods being replaced — competes for the same cores and is not addressed here. Still on the ThreadPool
by design: the language service's hover and completion requests (Roslyn's async `QuickInfoService` /
`CompletionService`, which schedule their own work with `ConfigureAwait(false)` and cannot be pinned
to a thread), the IO halves of the diagnostics requests, and `CompileResultFromAssembly` (assembly load
and user type initializers — bounded by its own timeout, on `CompileThread`, not a CPU compile).

## Related

- [Reading a Routing Saturation Report](../ReadingARoutingSaturationReport) — which gauge sees the wait.
- [Node Type Compilation](../NodeTypeCompilation) — the compile pipeline this changes.
- [Controlled IO Pooling](../ControlledIoPooling) — the pools and why the compile leaf is not on one.
