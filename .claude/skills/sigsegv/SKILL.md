---
name: sigsegv
description: 'Triage a host crash — exit=139 SIGSEGV, exit=134 SIGABRT, a createdump core, a trx that says "N passed" over a dead process, a pod crash-looping with lastExit=139. Use whenever a test host or portal dies on a signal instead of failing an assertion. In this codebase a crash is almost never a runtime bug: it is async on a turn-based scheduler, or a disposal whose wait was not a subscription. Read the exit code before naming a cause. Grounded in DebuggingNativeCrashes.md, AlcLeaseRegistry, the #613 teardown inversion.'
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /sigsegv — Read a host crash as async-or-dispose, not a runtime bug

A host that dies on a **signal** does not fail a test — it fails the shard. No assertion, no stack
in the trx, often no trx at all. The instinct is to suspect the runtime. Resist it: in this
codebase the crashes that were ours came from exactly two places, and both are the same mistake
wearing different clothes —

> **either `async`/`await` is involved, or a disposal-path observable was not properly
> subscribed.**

🚨 **And of those two, it is MOSTLY THE SECOND: a dispose nobody waited for.** Teardown here is
reactive — `Dispose()` *starts* the work and returns; the completion arrives later on `Disposed`,
`DisposalCompleted`, or a `StreamEndedEvent`. So "disposed" and "finished disposing" are different
moments, and every crash in Family B is something acting during the gap: unloading an ALC, freeing a
pool, tearing down a hub, while the previous phase is still running. **Start by asking which wait
was skipped**, not which line faulted.

The gap is dangerous in both directions, which is why it is worth naming rather than filing under
"ordering":

- **Nobody subscribed to the completion**, so teardown ran ahead of it → a use-after-free, and the
  crash lands in whatever happened to still be executing (Family B).
- **Somebody raced the completion with a timer** — `Task.WhenAny(done, Task.Delay(…))` — and then
  acted on "the wait expired" as if it were "the work finished" (Family A). *Prefer retaining a
  resource over acting on a guess.*
- **The completion had nowhere to land.** The waiter is not wrong and not slow: it is holding a
  subscription to something already torn down, so it receives no value, no completion and no error,
  and only its own timeout ever fires. Seen 2026-08-28: a compile-overlay self-heal recycled an
  instance hub with `DisposeRequest` while a live subscriber was mid-render; the terminal
  `StreamEndedEvent` was dropped (`no synchronization hub found`) and the render sat 37.7 s of total
  silence until its 50 s budget expired. **A burst of work then dead silence is this shape** — see
  [/debug](../debug/SKILL.md); it is a missed observation, not a slow path.

A timer race deciding "the wait expired, unload anyway"; a `Dispose()` that blocks; an unload
ordered before the drain; a subscription whose `IDisposable` was discarded; a `Subscribe(onNext)`
with no error arm. **Look there first** — before the dump, before the GC, before "flaky CI".

> Canonical references — the doc is the forensics, this skill is the triage:
> - [DebuggingNativeCrashes.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DebuggingNativeCrashes.md) — fetching and reading a core dump (do not re-derive it here).
> - [WritingTests.md](../../../src/MeshWeaver.Documentation/Data/Architecture/WritingTests.md) → "Reading a CI Failure" — what the run actually carries.
> - [ControlledIoPooling.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ControlledIoPooling.md) · [HubDisposalModel.md](../../../src/MeshWeaver.Documentation/Data/Architecture/HubDisposalModel.md) · [/async](../async/SKILL.md) Rule 1a.

## Step 0 — Is it native at all? `139` is not proof

`139 = 128 + 11`, and `.github/workflows/dotnet-test.yml` classifies it for you
(`[CI] <name> exit=139 SIGNAL SIGSEGV`). But **`createdump` also fires on unhandled MANAGED
exceptions**, and the spew is identical. 2026-08-26: `memex.systemorph.com` crash-looping with
`lastExit=139` + a core dump was read as a native fault and a plausible-looking disposal defect was
shipped as "the prod fix". One line of the actual dump said otherwise:

```
[createdump] Unwind: exception type System.TypeInitializationException
```

It was a `MissingMethodException` from a binary-breaking record change. **Get the exception type
before naming a cause:**

```bash
kubectl -n <ns> logs <pod> --tail=2000 | grep -iE "Unwind: exception type|MissingMethod|TypeInitialization"
```

| marker | meaning |
|---|---|
| `exit=139` | SIGSEGV — *or* an unhandled managed exception routed through `createdump`. Check. |
| `exit=134` | SIGABRT — runtime abort / `FailFast`. An `AccessViolationException` on a non-null-but-unmapped pointer lands here: that **is** use-after-unload (#613). |
| `exit=124` / `137` | **not a crash** — CI's 8 m wall-clock cap killed a hang. Never appears locally (macOS ships no `timeout`). |
| `exit=2` | xUnit v3's `AppDomain.UnhandledException` handler calling `Environment.Exit(2)`. The summary prints all green and the trx is clean. Grep the shard log for `FATAL ERROR`, not "catastrophic". |

**The crashing PROJECT is meaningful; the crashing TEST usually is not** — the signal lands wherever
the process happened to be. Where the evidence lives:

- **The trx.** For any exit it cannot explain, the shard runs `.github/scripts/record-host-crash.py`
  and writes a `<project>.HOST_CRASHED` failure into it, so no reporter can announce "N passed" over
  a dead process (`MeshWeaver.Content.Test` did exactly that, #2495). Captured output is in
  `<Output><TextMessages>`, **not** `<StdOut>` — finding `StdOut` empty says nothing.
- **`collected-logs/_meshweaver-test-trace.log`.** The only log surviving a killed host.
  `TEST_START` / `TEST_END` window markers and `[FAULT]` records with stacks, joinable by `pid=`.
  A `TEST_START` with no `TEST_END` names the test a killed host was stuck in.
  🚨 Records are rate-bounded — `grep FAULT-BUDGET` before reasoning from an *absence*.
- **`test-logs/teardown-stragglers.log`** (`test/MeshWeaver.Fixture/TeardownStragglerCapturer.cs`) —
  `FirstChanceException` dumps every straggler with a stack, **including the caught ones invisible
  everywhere else**. This is the diagnostic for "all tests pass, host exits non-zero". Run the
  native host the way CI does (`dotnet <Suite>.dll -trx …` from the bin dir); `dotnet test` is not
  equivalent.

## Step 1 — Is it OURS? Two fingerprints, and only one of them is

Both are SIGSEGV. They are different bugs and the dump distinguishes them in one line —
`si_code` and `si_addr` from `NT_SIGINFO`:

| | fault address | reading | verdict |
|---|---|---|---|
| **Use-after-unload** | `si_code = SI_KERNEL`, `si_addr = 0`, faulting register holds a **non-canonical** value (e.g. `rax = 0x0074007300200022` — UTF-16 text where a pointer belonged) | a **#GP on a non-canonical pointer**, not a null deref — freed-and-reused memory | **OURS.** Family A below. |
| **Zeroed MethodTable header** | `si_code = 1` (`SEGV_MAPERR`), `si_addr = 0x0`, `TRAPNO=14`/`ERR=0x4`, `RIP` inside file-backed `libcoreclr` `gc_heap::*`, instruction reading `MT->m_dwFlags` off a register that is `0` (`mov ecx,[rax]` with `RAX = 0`; `mov r9d,[rcx]` with `RCX = 0` — the register allocation varies, the dereference does not) | one 8-byte object header reads as exactly zero while its block stays coherent | **CoreCLR GC.** Not ours. |

The second one is the FutuRe family — **nine sightings, and the collectible-ALC hypothesis has been
falsified three separate ways** (RIP is in file-backed runtime code; a freed `LoaderAllocator` yields
a non-null *unmapped* pointer, never `0x0`; a free-list item has no ALC at all). Do not keep paying
that hypothesis forward, and **do not "fix" it by disabling concurrent GC** — that was tried
(#1274), changed nothing measurable, and was removed. Read the instruction + registers, not the
function name: the frame moved across `background_sweep` → `plan_phase` → `find_first_object` →
`background_mark_simple1` (2026-09-03) while the fault did not. Measured base rate on Plugins CI over
1,197 runs: **0.74 %**, `MeshWeaver.FutuRe.Test` only, `main` included. Full table:
DebuggingNativeCrashes.md.

🚨 **One more non-ours case, specific to test hosts:** `exit=139` in a process where **ClrMD** runs
anywhere means suspect the DAC first. `DataTarget.Dispose` dlcloses `libmscordaccore.so` without
deleting a process-global `pthread_key` whose destructor points into DAC code — any thread exiting
later segfaults *in whatever test is running*. Pinned by
`test/MeshWeaver.Hosting.Monolith.Test/ClrMdDacPin.cs`. The crashing test name is meaningless here.

## Family A — an ALC unloaded out from under live code

Every dynamic NodeType recompile mints a collectible `AssemblyLoadContext`. Unload one while a
thread can still enter its code and you get a genuine native fault. **The canonical crash of this
codebase**, CI run `32713409169`: a dedicated thread faulted on its **first managed call**, taking
the prestub into `UnsafeJitFunction` and dying in `LCGMethodResolver::GetCodeInfo` — JIT-compiling a
dynamic method whose allocator was already gone.

**The cause was a timer race in a `Task`-returning override** — async *and* an unsubscribed disposal
signal, in one defect:

```csharp
// ❌ parks the grain turn, then races a timer to decide whether the work happened
var done = hub.DisposalCompleted.FirstOrDefaultAsync().ToTask(ct);
if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(5))) == done)
    loadContext.Unload();          // …and if the timer won? unload anyway? THAT is the bug

// ✅ subscribe; the turn returns immediately and the work belongs to the signal
hub.DisposalCompleted
    .Take(1)                                   // unsubscribes on first emission — no rooted subscription
    .Catch<Unit, Exception>(ex => { logger.LogError(ex, "disposal faulted — KEEPING its context"); return Observable.Empty<Unit>(); })
    .Subscribe(_ => UnloadContextIfSafe(reason, grainId));
return Task.CompletedTask;
```

The ✅ is live at `src/MeshWeaver.Hosting.Orleans/MessageHubGrain.cs:784` (the subscription at
`:844`). With a timer you must answer "what if the wait expired?", and the tempting answer is the
crash. With a subscription there is no branch: **the callback runs when the signal says so, or it
never runs — and "never runs" means the context is simply retained.**

> **Prefer retaining memory over acting on a guess.** A retained context costs memory until process
> exit; an unload with a live user costs the process.

Three things enforce this, all current:

- **`AlcLeaseRegistry`** (`src/MeshWeaver.Mesh.Contract/Threading/AlcLeaseRegistry.cs`) — per-ALC
  in-flight accounting plus a reactive quiescence signal. `UnloadWhenQuiesced` reports `false` and
  **leaves the context loaded** rather than unloading on timeout. Used by
  `ScriptCompilationService` when a recompile evicts its predecessor on a *live* silo.
- **`MeshTeardownSignal`** — the mesh-wide equivalent at silo stop. It is mesh-wide and says nothing
  about one context being retired, which is the gap `AlcLeaseRegistry` closes.
- **`Take(1)`** — a subscription that never completes roots whatever the callback closes over. See
  Family B.

## Family B — a dispose nobody waited for *(this is most of them)*

Also called teardown phase inversion, but that names the symptom. The cause is always the same: a
phase treated `Dispose()` as if it had finished the work, when it had only started it. Find the
completion signal that exists — `Disposed`, `DisposalCompleted`, `StoppedAsync`, the drain — and ask
who was supposed to be waiting on it.

**#613, root-caused 2026-08-08.** Node ALCs unloaded in `DisposeImpl()` — *before*
`SignalDisposalCompleted()` — while in-flight pooled render work was only joined *after*
`DisposalCompleted`. In that window a Layout-pool leaf ran a cast over a NodeType-compiled `T` whose
`LoaderAllocator` had just been freed → `AccessViolationException` → SIGABRT. **The evidence was a
symbolized managed stack in the job log, not the dump** — when a crash prints `Fatal error.` with a
managed stack, read the job log first; the 185 MB dump downloads at ~30 KB/s and is usually
unnecessary.

The ordering traps that produce this shape:

- **"Registered first ⇒ stops last" is FALSE on an ASP.NET host.** `GenericWebHostService` is
  registered by `WebApplication.CreateBuilder`, strictly before anything the mesh builder can
  register — so **Kestrel stops LAST**, and the mesh was being drained while the portal still
  served. Nine separately-filed issues, one root. The fix is
  `IHostedLifecycleService.StoppedAsync`, invoked only after every `StopAsync` has returned:
  `src/MeshWeaver.Hosting/MeshTeardownHostedService.cs` (its `StopAsync` is deliberately
  `Task.CompletedTask`). Pinned by `MeshTeardownRunsAfterEveryOtherHostedServiceTest`.
- **The Orleans silo is itself a hosted service**, so it stops before the mesh drains:
  `silo stops → grains deactivate → every Unload() → only THEN the mesh drains`.
  `IoPoolSiloTeardown` subscribes at `ServiceLifecycleStage.First` ⇒ **stops LAST**, and composes
  `registry.Disposed` into **one** `ReactiveCompletion.ObserveCompletion(reportLateFault, ct)` at
  the `ILifecycleObserver` boundary — the only sanctioned place a Task appears, and never Rx's
  `.ToTask()` (forbidden repo-wide, 2026-08-30): that bridge resumes its caller INLINE on the
  signalling thread — here, the very lifecycle thread the unload is waiting on.
- **A blocking `Dispose()` is not the fix.** Making `IoPool.Dispose()` join first *hangs* — `using
  var pool = …` in an async method runs Dispose on a ThreadPool thread and blocks it for the whole
  `DrainTimeout`. An 18-core dev box hides it completely. The shape that works:
  **`Dispose()` cancels and returns; the WAIT lives on `Disposed`** (an `AsyncSubject`, so a late
  subscriber still gets the report) — `src/MeshWeaver.Mesh.Contract/Threading/IoPool.cs:76`.
- **`CancellationTokenSource.Cancel()` is SYNCHRONOUS** — it runs every registered callback inline
  on the caller's thread, and here those callbacks are entire downstream teardowns. Called from the
  teardown thread it runs arbitrary application cleanup unbounded; one leg that never returns parks
  mesh teardown silently forever (`exit=124` with `DISPOSE_INVOKED` and no `DISPOSE_DONE`, #2394).
- **A leaf that ignores the pool token holds its permit** — `Drain()` then burns its full 30 s
  `DrainTimeout` and reports a leak. Signature: a test failing **in teardown** with
  `teardown DIRTY — N pooled I/O leaf(s) still running` and a dispose of **~32 000 ms**. The named
  test is the victim, not the defect.

### The subscription mistakes that turn into crashes

- **A discarded `IDisposable` is a leak, not a style slip.** `Observable.Timer(…).Subscribe(…)` whose
  return value is dropped leaves a pending entry on the process-wide `TimerQueue` — a strong GC root
  holding the hub alive past disposal. **Seven sites of this one defect class**, found months apart;
  every one was discovered by a sampling probe firing at random on an unrelated PR.
- **`Subscribe(onNext)` with no error arm is a host kill**, not a missing log. Rx's default `onError`
  is `Stubs.Throw`, which rethrows on whatever thread carried the fault — a timer thread, unhandled,
  process dead (#2468).
- **An Rx subscribe-time throw goes to the SUBSCRIBE CALLER, not `OnError`** — so every `.Catch` in
  the chain is blind to it, and if the caller is a scheduler it is unhandled. Wrap the leg in
  `Observable.Defer`: Defer's *factory* exceptions are forwarded to `OnError`.
- **Hosted services must be stopped before the mesh is disposed**, in reverse start order — a service
  with an in-flight `Observe` at test end leaves a pending hub callback (PR #432).

## Procedure

1. **Classify the exit.** Read the `[CI] <name> exit=<n>` marker and the `HOST_CRASHED` trx entry.
   `124`/`137` ⇒ a hang, go to [/debug](../debug/SKILL.md). `2` ⇒ grep `FATAL ERROR`.
2. **Rule out a managed exception.** `grep "Unwind: exception type"` (pod logs) — if it names a type,
   you are done with the dump and you have a different bug.
3. **Read the trace log, and check it is complete.** `grep FAULT-BUDGET` first, then the `[FAULT]`
   records and the `TEST_START`/`TEST_END` window around the death.
4. **Ask which PHASE** — read the bottom of the stack, not the top. A `Build`/`CreateHub` bottom is
   construction; a dispose cascade is teardown. The FutuRe crash was framed as a teardown race for
   weeks while the dump showed construction. 🚨 A method's *name* is not a phase:
   `SubscribeToOwnDeletion` is a `.WithInitialization(...)` hook.
5. **Only now, the dump** — and only for the `si_code`/`si_addr` discriminator of Step 1.
   🚨 **macOS traps** (from a run that cost real time):
   - **`dotnet-dump analyze` REFUSES Linux dumps on macOS.** There is no flag. But the file is a
     plain ELF64 core, so parse it yourself — the faulting frame is ~10 minutes of `struct.unpack`.
     A container is needed only for the **managed** (DAC) commands.
   - **`createdump` prints the crashing thread id in HEX** — `Crashing thread 1684` is `0x1684` =
     5764. Looking up 1684 finds nothing and invites the wrong conclusion.
   - **`NT_PRSTATUS` is the signal handler, not the crash** (createdump runs ~20 s later, from
     inside the handler). Recover the faulting registers from the `ucontext` on the alternate signal
     stack. For the same reason **post-hoc memory reads are stale**; registers at fault time are
     authoritative.
   - **`libcoreclr.so` as shipped is stripped** — resolve against the build-id `.debug` from the
     symbol server, using the runtime named in `collected-logs/symbols-<Project>/_runtimes.txt`. A
     mismatched patch yields no symbols at all and looks like the technique failing.
6. **Prefer a deterministic repro over more forensics.** A stack says *where* the process died, not
   *why*. The cheapest proofs on record are ordering tests, not dumps: record
   `UnloadNodeContexts` vs `DrainAll()` during teardown; park a stub leaf that exits only on its own
   token and assert `DrainAll()` returns 0. A negative control — revert only the fix lines, watch the
   test fail, restore, watch it pass — is what turns "plausible" into "verified".

🚨 **Do not re-run to see if it reproduces.** A crash is a real race; a re-run passing on the
identical commit means you won the timer race, not that the code is clean.

## Checklist — before you call a crash fixed

- [ ] The exit code was classified, and a managed exception was ruled **out** (not assumed away).
- [ ] The fingerprint was read: `si_code`/`si_addr` say ours (non-canonical #GP) vs CoreCLR GC
      (`si_addr = 0x0` in `gc_heap`) vs the ClrMD DAC. No unload fix is being aimed at a GC fault.
- [ ] No `await` / `Task.WhenAny` / `Task.Delay` / `.ToTask()` decides whether a disposal happened.
      The wait for disposal is a **`.Subscribe(...)`** on a completion observable — never a poll, a
      timeout race, a `TaskCompletionSource`, or a fixed delay.
- [ ] Every unload is gated on a **positive** quiescence signal (`AlcLeaseRegistry.UnloadWhenQuiesced`
      / `DisposalCompleted`), with **no "expired, unload anyway" branch**. Retention is the
      acceptable outcome.
- [ ] `.Take(1)` on the disposal subscription; its `IDisposable` is not discarded; the fault arm is
      **fluent** (`.Catch`), not a `try`/`catch` around `Subscribe`.
- [ ] Every `Subscribe` on the teardown path has an **error arm** (no `Stubs.Throw` host kill).
- [ ] Nothing blocks in a `Dispose()`; no `Cancel()` is issued from a thread teardown needs to keep
      moving.
- [ ] Ordering holds: hosted services stopped → `StoppedAsync` → drain → unload. Not registration
      order.
- [ ] A deterministic repro or ordering test pins the cause — and no bound was raised to make it
      pass ([AGENTS.md](../../../AGENTS.md) → "No band-aids").
