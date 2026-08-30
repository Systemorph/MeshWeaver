---
Name: Removing Observable-to-Task Bridges
Category: Architecture
Description: Why .ToTask() is forbidden everywhere including tests, what to write instead at each kind of boundary, the fleet-wide inventory it was measured against, and the ratchet that holds src/ at zero.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 4v6h6"/><path d="M20 20v-6h-6"/><path d="M20 9A9 9 0 0 0 5.6 5.6L4 7"/><path d="M4 15a9 9 0 0 0 14.4 3.4L20 17"/></svg>
---

# Removing Observable-to-Task Bridges

**`.ToTask(` is forbidden in this codebase — everywhere, tests included.** The exemption the
guidance used to state (*"tests are the ONLY place `await …FirstAsync().ToTask()` is acceptable"*)
was retracted by the maintainer on 2026-08-30: *"totask is forbidden" · "strictly" · "no totask
ever" · "the only place they may work is inside activities but usually even there avoid"*.

This page is the operational companion to [Asynchronous Calls](../AsynchronousCalls), which carries
the rule itself. Here: **why** the test edge was never safe, **what to write instead** at each kind
of boundary, **what the fleet actually contained** when the ruling landed, and **the ratchet** that
keeps the number at zero once a tree reaches it.

## Why the test edge was never safe either

Rx's bridge completes its `TaskCompletionSource` from **inside the Rx pipeline**, without
`TaskCreationOptions.RunContinuationsAsynchronously`. So `TrySetResult` resumes the awaiter
**inline, on the signalling thread, still inside Rx's trampoline** (`Producer.SubscribeRaw`) — and
everything the continuation then does inherits that. The reproduction captured in
`InlineObservableExtensions`' remarks is a 558-frame stack showing it escape the pipeline entirely:

```text
MessageService.DrainOne()                       // the hub's own pump, on a ThreadPool thread
 → Producer.SubscribeRaw
   → CurrentThreadScheduler.Schedule            // trampoline OPENED here; the flag is now set
     → … ~500 Rx frames …
       → ToTaskObserver.OnCompleted → TaskCompletionSource.TrySetResult
         → AwaitTaskContinuation.RunOrScheduleAction(allowInlining: true)   // awaiter resumes INLINE
           → … the awaiting code, and everything it goes on to call …
```

It is also **sticky**: `await` captures `TaskScheduler.Current` when there is no
`SynchronizationContext`, so once one continuation lands on that scheduler, every later `await` in
the same method schedules onto it too. That is the mechanism behind two separate incidents —
a grain teardown that parked the very scheduler its own deactivation needed, and a live children
listing that silently stayed empty forever because the walk it enqueued could only run after a
block that only returned when the walk ran.

**A bridge written "only in a test" therefore changes how the code under test runs, and a green
test proves the wrong thing.** Under xUnit the reach is wider still: the resumed continuation was a
mesh teardown await, so the runner carried on *on that stack* and started subsequent tests inside
the trampoline.

## What to write instead

Pick by **who owns the signature**, in this order.

### 🚨 0. The trap first: awaiting the observable directly is NOT a fix

The obvious way to remove a bridge is to await the observable itself — `await source.FirstAsync()`.
It reads cleaner, it drops a namespace, and it looks like exactly what "stay reactive" means. **It
fixes nothing.** Rx's own awaiter is built on `AsyncSubject<T>`, which completes its continuation
from inside `OnCompleted` — on the signalling thread — so it has the *same* inline-resume property
as the bridge it replaced.

Measured on this repo's Rx version rather than reasoned about (a `Subject<T>` signalled from a known
thread, recording where the awaiter resumes), and pinned by `InlineResumptionMechanismTest`:

```text
TOTASK        signalling=6 resumed=6 INLINE=True
DIRECT-AWAIT  signalling=7 resumed=7 INLINE=True
OBSERVECOMPL  signalling=4 resumed=6 INLINE=False
```

Only `TaskCreationOptions.RunContinuationsAsynchronously` breaks the chain. This is the one
substitution that would pass review, satisfy any textual scan, and leave the defect exactly where it
was — so it is listed before the real options rather than after them.

### 1. The signature is yours → stay reactive

Return `IObservable<T>`, compose with `.Select` / `.SelectMany` / `.Where` / `.Timeout`, and end in
`.Subscribe(onNext, onError)`. There is no Task to produce, so there is nothing to bridge. This is
the preferred outcome and it is what most sites become.

### 2. A `Task`-returning override whose RESULT you do not need → subscribe, return `Task.CompletedTask`

An `IHostedService.StartAsync`, an Orleans grain lifecycle hook, an ASP.NET middleware hop that
only fires work. Subscribe and hand back a completed Task — do not bridge at all. This is Rule 1a
of the [/async](../AsynchronousCalls) contract.

### 3. An external signature forces a `Task<T>` AND you need the value → `ReactiveCompletion.ObserveCompletion`

An ASP.NET minimal-API endpoint, an MVC controller action, an `ILifecycleObserver.OnStop`, an SDK
interface you implement. `MeshWeaver.Messaging.ReactiveCompletion.ObserveCompletion` is the one
sanctioned wait:

```csharp
var node = await workspace.GetMeshNodeStream(path).FirstAsync()
    .ObserveCompletion(
        ex => logger.LogWarning(ex, "read faulted AFTER the wait settled for {Path}", path),
        cancellationToken);
```

It differs from Rx's bridge in exactly the two ways that matter:

- it completes with **`RunContinuationsAsynchronously`**, so the caller's continuation is *queued*
  rather than run on the thread that signalled — the line that removes the defect above;
- it **keeps its error arm attached** after the task settles, so a fault arriving late is reported
  to `reportLateFault` instead of becoming an unobserved exception on the finalizer (which xUnit v3
  escalates to a Catastrophic failure that poisons the *next* test class).

`reportLateFault` is never `null` and never an empty lambda — an ignored late fault is half of what
the method exists to remove.

### Conversion traps, all of them met in practice

| Trap | What happens | What to do |
|---|---|---|
| Empty source | `.ToTask()` **throws** `InvalidOperationException`; `ObserveCompletion` settles with `default` | Where the throw was load-bearing (a negative "nothing was emitted" assertion), insert `.FirstAsync()` |
| Nullability | `ObserveCompletion` returns `Task<T?>`; `-warnaserror` rejects `Task<T?>` → `Task<T>` | `expr!`, and `(await expr)!` when the `!` would otherwise bind to the Task |
| Generic inference | Inference off the lambda picks up the nullable `T?` and yields `IObservable<T?>` | State the type argument: `Invoke<T>(…)` |
| Dropping the `using` | `System.Reactive.Threading.Tasks` also hosts `Task<T>.ToObservable()` — the SAFE direction | Grep the file for `ToObservable(` first; keep the using if any hit is on a Task. The failure is `CS0411` on the `IEnumerable` overload and does not mention Task |
| Cancellation | `.ToTask(ct)` cancelled the wait | Pass the same token as `ObserveCompletion`'s last argument; never silently drop a `RequestAborted` |

## The inventory the ruling was measured against

Counted on core `main`, 2026-08-30, **code sites only** — comment and string-literal occurrences
masked, because this repo quotes the banned shape at length on purpose to explain why it is banned.
The raw `grep` total is much higher than the real one, and the difference is documentation:

| Tree | `grep` hits | Real code sites |
|---|---:|---:|
| core `src/` | 83 | **32** |
| core `memex/` | 41 | 33 |
| core `tools/` | 3 | 3 |
| core `test/` | 1 537 | 1 514 |
| `MeshWeaver.Plugins` production | 80 | 64 |
| `MeshWeaver.Plugins` `*.Test` | 715 | — |

**Never scan for this shape without masking.** A textual scan counts the war stories that make the
rule teachable and pressures you into deleting them; that is a net loss, and it is why the guard
below masks first.

The distribution matters more than the total: in core `src/` the 32 sites sat in **10 files**, and
three of them — `IStorageAdapterTestExtensions` (13), `ObservableAssertions` (3) and
`MonolithMeshTestBase` (2) — are shared helpers behind several hundred test call sites. Fixing a
helper removes the bridge from every caller without touching one of them, so **helpers first** is
not a preference, it is where nearly all the leverage is.

## The ratchet

`ObservableToTaskBridgeGuard` (in `test/MeshWeaver.Documentation.Test`) enforces this with **two
deliberately different rules**:

- **`src/`, `tools/`, `samples/`, `clients/` are held at ZERO with no allow file at all.** The
  maintainer's words are *"in src especially we should have zero"* — zero, not "zero except". There
  is no line to add and no budget to raise; the only way past is to fix the site.
- **`test/` and `memex/` carry a seeded inventory that may only SHRINK**, because their sweeps land
  in later waves. When a wave empties one, its root moves into the zero list in the same change.

Two properties are load-bearing and are pinned by the guard's own tests:

- **It masks comments and string literals** before counting, via `SourceScan.MaskCommentsAndStrings`
  — so the prose above, and every remark that quotes the shape, is not a site.
- **It is not vacuous.** The zero half's evidence is an *empty* result, and "found nothing" is
  indistinguishable from "the scanner is broken" — the skip-trapdoor shape AGENTS.md forbids in a
  gate. So the guard drives its real scanner over a synthetic file and asserts both directions: it
  counts a call, and it does not count the same text in a comment, a doc comment, a block comment
  or a string.

A stale entry (its site was fixed) is **reported, not failed** — two PRs closing sites concurrently
would otherwise red `main` on whichever merged second, and a gate that punishes the direction it is
asking for teaches people to stop shrinking.

## Related

- [Asynchronous Calls](../AsynchronousCalls) — the rule, and the wider no-`async` contract.
- [Controlled I/O Pooling](../ControlledIoPooling) — where a genuinely-async leaf belongs.
- [Debugging Message Flow](../DebuggingMessageFlow) — when a wait never completes.
- [Writing Tests](../WritingTests) — how a test waits on a condition.
