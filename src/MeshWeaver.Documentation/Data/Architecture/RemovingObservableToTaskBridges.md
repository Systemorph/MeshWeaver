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

## 🚨 The spelling is not the thing

**A guard that matches text can only ever find the shapes someone thought to spell out.** This is not
a hypothetical: `ObservableToTaskBridgeGuard` matched the literal string `.ToTask(` and nothing
else, and its own remarks claimed `src/` was at zero *"with no escape hatch"*. On 2026-08-30 that
claim was measured and found **false as enforced**, in two independent ways:

| What was there | Why the marker missed it |
|---|---|
| `src/MeshWeaver.Reactive.Assertions/ReactiveWait.cs` hand-rolls the bridge from a `TaskCompletionSource` and a `Subscribe` | Not one character of `.ToTask(` in it. The mechanism was spelled differently. |
| `src/MeshWeaver.Mesh.Contract/Services/MeshServiceExtensions.cs` declares a method **literally named `ToTask`** and calls it as `ToTask<bool>(service.DeleteNode(path), ct)` | The marker's **leading dot**. The call has no receiver, so a static invocation of a hand-rolled bridge of exactly the banned name walked straight past a rule written to ban it. |

The second one is the sharper lesson, because the leading `.` was *documented as deliberate* —
"the honest reading: any `.ToTask(` in this repo is Rx's". The reasoning was sound and the
conclusion was wrong, and nothing in the guard could tell you so: **its evidence for "src is clean"
was an empty result, which is indistinguishable from a scanner that cannot see.**

So the production rule is now **structural, not textual**. It looks for the mechanism and the shape,
and it does not care what anything is named — rename `ToTask` to `First` and it still fires:

1. **A `TaskCompletionSource` settled from inside a subscription** — `.Subscribe(` whose argument
   region contains a `TrySetResult`/`TrySetException`.
2. **A `Task`-returning method over an `IObservable<>` that builds its own completion source.**
   *Both* halves are required — see the boundary below.
3. **An `IObserver<>` implementation that settles a completion source** — the same bridge with its
   callbacks extracted into a named type. Catching it twice is deliberate: a detector you can defeat
   by extracting a class is a spelling again.

**The lesson recurs one level down.** The first version of those detectors matched
`new TaskCompletionSource` — and missed `new System.Threading.Tasks.TaskCompletionSource<T>(…)` and
the target-typed `TaskCompletionSource<T> x = new(…)`. Missing a *construction* is worse than missing
a call site: the safe-form classifier asks "do **all** constructions carry
`RunContinuationsAsynchronously`?", so a construction the regex cannot see makes the answer
vacuously *yes* and the zero rule passes having checked nothing. Caught in review; every spelling is
now covered and pinned by fixtures. **When you write a structural detector, enumerate the spellings
of each part you match — including the ones the compiler lets you omit.**

### 🚨🚨 Where the line is: this bans BRIDGING, never the `Task` type

Getting this boundary wrong would make the rule unshippable, so it is stated in the guard and
enforced by its tests. **`Task` is not the defect. Hand-rolling the wait is.**

- **`IIoPool` is the sanctioned async/IO boundary** — `pool.Invoke` and friends take and return
  tasks by design. (Measured: `IoPool` constructs no `TaskCompletionSource` at all.)
- **Orleans grain signatures are `Task` by contract.** A grain method returning `Task<T>` is not a
  defect.
- **A `TaskCompletionSource` used as a plain lifecycle signal is not a bridge.**
  `MessageHub.hasStarted` is settled from `Start()`/`FailStartup()`, never from a subscription
  callback, and is correctly invisible to every detector.
- **A `Task`-returning method that CONSUMES the sanctioned bridge is not hand-rolling one.**
  `HubDisposalJoin.JoinDisposalAsync` takes an `IObservable<Unit>` and returns `Task<bool>`, but
  waits through `ObserveCompletion` and builds no completion source — so detector 2 requires the
  signature **and** an own-built completion source, and that site passes.

A rule that reds on legitimate code gets suppressed, and **a suppressed rule is worse than no rule.**
For the same reason `.Result` is deliberately **not** a marker: it is overwhelmingly a domain
property here (`ToolCall.Result`, `PatchResult.Result`), so matching it would flag a hundred innocent
reads to catch one bridge. `.GetAwaiter().GetResult()` is unambiguous and *is* matched;
`.Wait()` is matched with **empty parentheses only**, because `.Wait(timeout)` is the bounded,
legitimate form (`IoPool`'s sanctioned `SemaphoreSlim` gate, `HubDisposalJoin`'s deliberate
`Task.Wait(TimeSpan)`).

## The ratchet

`ObservableToTaskBridgeGuard` (in `test/MeshWeaver.Documentation.Test`) enforces this with rules of
**deliberately different strength**:

| Rule | Trees | Strength |
|---|---|---|
| A hand-rolled bridge in the **unsafe form** (no `RunContinuationsAsynchronously`) | production | **ZERO** — no register, no allow file, no exemption. This *is* the #2377 defect. |
| A hand-rolled bridge in the **safe form** | production | Only the entries in `SanctionedBridges`, each **verified**. |
| `.ToTask(` | production | **ZERO**, no allow file. Rx's own bridge is never the safe form, so it is never registrable. |
| `.ToTask(` | `test/` | Seeded inventory, may only **shrink**. `memex/` left this row when its sweep reached zero (#2764) and is now a production root — checked there by all three detectors, not just the marker that emptied it. |
| `.Wait()` / `.GetAwaiter().GetResult()` | production | Seeded inventory, may only **shrink** (see below). |

**`SanctionedBridges` is a register, not an allow file.** An allow file lists sites you tolerate and
grows by appending a line. Every entry here is machine-checked to still **exist**, to still **contain
a bridge**, and to still be the **safe form** — so an entry whose subject was fixed, moved or renamed
FAILS and tells the next author to delete it. It currently holds four: `ReactiveCompletion` (the one
real implementation), `ReactiveWait` (its standalone duplicate — see below), `MeshServiceExtensions`
(retained debt, already governed by `MeshServiceHasNoTaskShimGuard`, with 58 in-mesh Reinsurance
callers no compiler here can see) and `MessageHubGrain` (a grain whose `Task` return is contractual
and which must map an activation fault to a *successful* classified `DeliveryFailure`, which
`ObserveCompletion` cannot express because it faults instead).

**Why the blocking markers are a ratchet and not a zero.** They did not start from zero: 9 sites
across 5 files in the compiler, the Orleans test-base disposal path, the plugin tester and a sample.
Holding them at zero on day one would have made the guard red on merge — and a rule that reds on
arrival gets suppressed rather than obeyed. Note also what this closed:
`BlockingBridgeInTestRatchetGuard` scans `test/` **only**, on the stated grounds that `src/` is
*"governed by … the reviews that enforce it"*. **Reviews are not a gate.** The production trees had
no mechanical check at all for a shape whose consequence — an unbounded park on a turn-based
scheduler — is strictly *worse* in product code than in a test.

Three properties are load-bearing and are pinned by the guard's own tests:

- **It masks comments and string literals** before scanning, via `SourceScan.MaskCommentsAndStrings`
  — so the prose above, and every remark that quotes the shape, is not a site.
- **It is not vacuous, and this is proved rather than asserted.** The zero rules' evidence is an
  *empty* result — the skip-trapdoor shape AGENTS.md forbids in a gate. So the guard builds a real
  temporary directory tree with a real production root, drops a hand-rolled bridge into it that
  contains **no `.ToTask(` at all**, and drives the same scanner over it, asserting the discrimination
  in every direction: the unsafe fixture is found *and classified unsafe*, the safe fixture is found
  *and classified safe*, and a mere consumer of the sanctioned bridge is **not** found.
- **It was mutation-checked.** With the detection reverted to the `.ToTask(` marker alone, the
  production rules go **green on the very tree that contains the bridges** — the exact false pass
  that stood before this change.

A stale entry (its site was fixed) is **reported, not failed** — two PRs closing sites concurrently
would otherwise red `main` on whichever merged second, and a gate that punishes the direction it is
asking for teaches people to stop shrinking.

## 🧭 Removing a bridge SURFACES latent bugs — it does not create them

When the assertion helpers moved off Rx's bridge onto `ReactiveWait`, a test flake appeared. The
reflex reading — *"the new wait introduced a defect, revert it"* — was wrong, and the root cause is
the strongest argument this page has for the rule.

`NodeTypeCompileParkTest` carried a **latent ordering assumption**. Rx's bridge resumed the test
**inline on the signalling thread**, so the test's next step ran *while the mesh was still on that
thread* — which happened to sequence a recycle after the fix became visible. `ReactiveWait`
correctly queues that continuation instead (`RunContinuationsAsynchronously`), so the recycle can
now genuinely race ahead of the fix's visibility and the recompile finds the old source.

**The banned bridge was masking a real test bug.** That is the general shape, not a one-off: an
inline resumption imposes an accidental ordering that code silently comes to depend on, and the
dependency is invisible for exactly as long as the bridge stays. So:

- **`ReactiveWait` is exonerated as a design.** It introduced no defect; it removed the accidental
  serialisation that was hiding one. Its planned move to `test/` is **layering** — test
  infrastructure should not live in a production root — and must never be described as containing a
  defect.
- **Expect this when you remove a bridge.** A test that starts failing after a bridge is removed is
  evidence about *that test*, and the first move is to find the ordering it was relying on — not to
  put the bridge back.

## 🕳️ Known gaps — where the next person will trip

Three things this rule does **not** cover, recorded here rather than left to be rediscovered. None is
a reason to distrust the guard; each is a reason not to read its green as more than it means.

### 1. A `using` alias still evades the construction detectors

The detectors match `new [qualifiers.]TaskCompletionSource…` and the target-typed
`TaskCompletionSource<T> x = new(…)`. They do **not** see an aliased type:

```csharp
using Tcs = System.Threading.Tasks.TaskCompletionSource<int>;
…
var completion = new Tcs();   // invisible to the scanner
```

The consequence is the bad one, the same as the qualified-spelling hole found in review: the
safe-form classifier asks *"do **all** constructions carry `RunContinuationsAsynchronously`?"*, so a
construction it cannot see makes the answer vacuously **yes**, and the unsafe-form zero rule passes
having checked nothing. Closing it properly means resolving `using` aliases per file — real work, not
another alternation in a regex. **If you are reviewing a file that aliases a completion source, the
guard is not helping you; read it yourself.**

### 2. `MeshServiceExtensions.ToTask` never settles on an empty source

`SingleObserver<T>.OnCompleted` is an empty body. A source that **completes without emitting** leaves
the returned task pending **forever** — not a fault, not a `default`, just a wait that never ends.
This is deliberately unchanged: fixing it is a behaviour change (the wait would start yielding
`default`, or throwing as `ObservableAwait` does) and belongs with the port that retires the shim,
not with the inline-resumption fix that shares its line. Note the contrast — `ObservableAwait` and
`ReactiveCompletion` both settle on completion, so **the shim is the odd one out**, and a caller that
migrates to either will see an empty source behave differently.

### 3. The shim's real callers are invisible to every compiler here

`CreateNodeAsync`/`UpdateNodeAsync`/`DeleteNodeAsync` have **zero** callers in this repo outside
tests. The ones that matter are **58 call sites across 22 in-mesh `Source/*.cs` files in
MeshWeaver.Reinsurance**, which compile at RUNTIME in the portal. So the
`RunContinuationsAsynchronously` fix — a strict improvement, no signature change — was **never
exercised against a single real caller by any build or test in this repository**. Green CI proves
nothing about them, and neither does this page. The exit is to port those sites to
`CreateNode(...).Subscribe(...)` and then move the shim to `MeshWeaver.Fixture`
(MeshWeaver.Reinsurance #102).

## Related

- [Asynchronous Calls](../AsynchronousCalls) — the rule, and the wider no-`async` contract.
- [Controlled I/O Pooling](../ControlledIoPooling) — where a genuinely-async leaf belongs.
- [Debugging Message Flow](../DebuggingMessageFlow) — when a wait never completes.
- [Writing Tests](../WritingTests) — how a test waits on a condition.
