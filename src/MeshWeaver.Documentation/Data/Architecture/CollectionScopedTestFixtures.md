---
Name: Collection-Scoped Test Fixtures
Category: Architecture
Description: Why the estate's 1,778 [InlineData] rows each paid for their own mesh boot, and the xunit v3 execution host that gives a shared fixture a real lifetime — one boot per test collection, disposed when the collection ends.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3v18h18"/><path d="M7 16v-6"/><path d="M12 16v-10"/><path d="M17 16v-3"/></svg>
---

# Collection-Scoped Test Fixtures

**We were about to reimplement xunit. We hosted it instead.**

## The problem, as it was originally framed

The estate has **1,778 `[InlineData]`** declarations across **404 `[Theory]`** methods (plus **24
`[MemberData]`** and **22 `TheoryData<>`** sites), measured over core `test/` on 2026-09-01. The
plan to move the test estate onto an install-and-execute lane hit the observation that neither of
the two node-plugin test lanes can express a data case:

- the in-mesh `Tests` layout area is a hand-written `(string Name, Action Body)[]` array,
  iterated with `try`/`catch` into a markdown table;
- the static/build lane (`StaticTestRunner` in `mw-plugin-test`) reflects over **parameterless**
  public statics, and classifies anything taking a parameter as `Outcome.NeedsMesh`.

The obvious next step was to design a data-case vocabulary those lanes could express, and rewrite
1,778 sites into it.

## Why that was the wrong shape

`[Theory]`, `[InlineData]`, `[MemberData]`, `Skip=`, `[Collection]` grouping, `ITestOutputHelper`
(**877 uses**) and per-row pass/fail reporting are not missing facilities. **xunit already
implements every one of them, correctly.** Reimplementing them would have bought a worse copy of
something the estate already depends on, and charged 1,778 edits for it.

Exactly one thing is genuinely missing: **an execution host** — somewhere for a case to run that has
a live mesh in it, without every case paying to boot one.

## What actually costs the time

`MonolithMeshTestBase` implements `IAsyncLifetime`. xunit constructs a **new test class instance per
test method**, so the mesh boots **per case** — and a `[Theory]` row is a case. A class with 25
`[InlineData]` rows boots 25 meshes.

**72 test classes already declare `ShareMeshAcrossTests => true`**, asking for exactly the fix. It
never took effect. A master kill-switch, `ShareMeshClusterEnabled`, was hard-coded `false`, because
the only sharing mechanism available was a `static ConcurrentDictionary<Type, IServiceProvider>`
that was never cleared:

> Keeping a per-class `IServiceProvider` alive in the static `_sharedProviders` pinned the mesh (and
> every hosted hub + subscription + MemoryCache timer it owns) for the whole testhost. A pinned
> class's mesh then interfered with later classes' per-test meshes — concretely the Acme bulk
> `UpdateNodeRequest@…/DefinePersona` never received its reply once the shared `AcmeSearchTest` mesh
> stayed live alongside the Todo meshes (passes in isolation, hangs in bulk).

**A static cache is not a lifetime.** That is the whole defect: there was no correct place to hang
the end of a shared fixture, so sharing was switched off for the entire estate.

## The seam

xunit v3 has the bracket a static dictionary was standing in for.
`TestAssemblyRunner.RunTestCollection(context, collection, cases)` is `protected virtual`, and it
spans exactly one test collection — every case in, every case out.

```
[assembly: TestFramework(typeof(MeshTestFramework))]
        │
MeshTestFramework          : XunitTestFramework          → CreateExecutor
MeshTestFrameworkExecutor  : XunitTestFrameworkExecutor  → RunTestCases
MeshTestAssemblyRunner     : XunitTestAssemblyRunner     → RunTestCollection
                                                             │
                            ┌────────────────────────────────┴──────┐
                            │  TestCollectionScope.Begin(name)      │
                            │      … every case of the collection … │
                            │  await scope.DisposeAsync()           │
                            └───────────────────────────────────────┘
```

`MeshWeaver.Testing.Xunit` is ~250 lines and depends only on `xunit.v3.extensibility.core` — a
package the estate **already** pinned and already referenced from `MeshWeaver.Fixture` and
`MeshWeaver.Hosting.Orleans.TestBase`.

**Discovery, data enumeration, skipping, ordering, parallelism and reporting are untouched.** This
substitutes *where a case's fixture comes from*, nothing else. That is why zero of the 1,778
`[InlineData]` sites needed an edit.

## The scope

`TestCollectionScope` is a keyed, lazily-populated resource holder:

- **Lazy.** A collection whose cases never ask for a mesh pays nothing for the host being installed.
- **Created once per key per collection**, however many cases ask.
- **Disposed in reverse creation order** — a mesh goes down before the service provider it resolved
  out of, without the scope knowing what either is.
- **Every resource is disposed even when one throws.** A disposal skipped because its neighbour
  failed is a leak that outlives the collection, which is the failure this type exists to end.
- **A failed creation is cached and rethrown** to every later caller. Retrying a broken fixture per
  case turns one boot failure into one timeout per row, which reads as a slow suite rather than the
  single broken fixture it is.
- **Ambient**, via an `AsyncLocal` written inside `RunTestCollection`. A write inside an `async`
  method flows *down* into everything it awaits — every case of that collection — and is discarded
  when it returns, so two collections running in parallel cannot see each other's fixture. (The
  inverse of the trap on `TestBase`'s constructor, where a write in an async `InitializeAsync`
  failed to flow *up* to its caller.)

`GetOrCreate` has a synchronous overload because `ServiceSetup.Initialize()` is synchronous.
It exists so a synchronous caller never blocks on a `ValueTask` — the deadlock this codebase
forbids outright, and "just this once, in a fixture" is how it gets in.

That overload therefore keeps the value in a second map and reads it from THERE, never off the
`Task`. The first cut wrote `task.GetAwaiter().GetResult()` guarded by `task.IsCompleted`, and
`BlockingBridgeInTestRatchetGuard` failed the build for it — correctly. "The task is already
completed" is not a licence to write a blocking bridge: the shape is what the ratchet keeps out,
because the next edit is what makes it not-completed. Forcing a `Lazy` runs its factory inline and
is not a wait; a key created by the async overload has no sync value and is refused by name rather
than waited on.

## What changed in `MonolithMeshTestBase`

The kill-switch is gone. Sharing is now enabled precisely when there is a lifetime to hang it on:

```csharp
private bool SharesMeshAcrossTests =>
    ShareMeshAcrossTests && TestCollectionScope.Current is not null;
```

`TestCollectionScope.Current` is `null` in every assembly that has not opted in, so
`SharesMeshAcrossTests` stays `false` there and the behaviour is bit-for-bit what it was — a fresh,
per-test-disposed mesh. **Adoption is one assembly at a time**, by adding a single line:

```csharp
[assembly: Xunit.TestFramework(typeof(MeshWeaver.Testing.Xunit.MeshTestFramework))]
```

The shared provider is wrapped in a `SharedMeshProvider` that disposes it the same way the per-test
path always has — `(sp as IDisposable)?.Dispose()`, failure traced rather than thrown, so a teardown
fault cannot red a suite that passed.

## Measured

**In core, now — `MeshWeaver.Graph.Test`** (2026-09-01, local, `-c Release`, in-process runner):

| | cases | baseline | on the host | |
|---|---|---|---|---|
| `AppIconAdoptionTest` (6 `[InlineData]`) | 14 | **2.879 s** | **1.330 s** | **2.2×** |

The shape is the amortisation: the first case pays **0.387 s** (the boot), every later one
**0.047–0.062 s**. Validated **20/20 green** on the class, and the whole assembly — **1063 cases,
every one routed through `MeshTestAssemblyRunner`** — is **3/3 green** at 64–72 s.

Per-row verdicts, with the row arguments xunit prints:

```
A_target_with_no_better_icon_changes_nothing(targetIcon: null)          [FINISHED] 0.050s
A_target_with_no_better_icon_changes_nothing(targetIcon: "")            [FINISHED] 0.047s
A_target_with_no_better_icon_changes_nothing(targetIcon: "…puzzle.svg") [FINISHED] 0.050s
A_record_without_a_real_icon_adopts_the_apps_own(current: null)         [FINISHED] 0.048s
A_record_without_a_real_icon_adopts_the_apps_own(current: "")           [FINISHED] 0.052s
A_record_without_a_real_icon_adopts_the_apps_own(current: "…puzzle.svg")[FINISHED] 0.049s
```

**The original measurement, on a suite that has since left this repo.** The facility was built and
proved against `MeshWeaver.Autocomplete.Test`, which moved to MeshWeaver.Plugins on 2026-09-01 with
16 other mesh suites (`b253cec0a`, "core stops building them"). Recorded because it is the larger
number and the honest provenance of the design:

| | cases | baseline | on the host | |
|---|---|---|---|---|
| `AutocompleteMultiSourceTest` (25 `[InlineData]`) | 48 | **26.9 s** | **4.0 s** | **6.7×** |
| whole assembly (5 of 6 classes shared) | 146 | **33.5 s** | **12.2 s** | **2.7×** |

18/18 green against a 33.5–36.9 s baseline over 21 runs; the first case paid the 2.62 s boot and
every later one 0.04–0.11 s. With a failure injected into one row, seven rows produced seven
verdicts and one failure and the run continued past it — per-row isolation, which is xunit's and
not ours, and the non-negotiable the whole facility exists for.

## 🚨 Every opt-in is an untested assumption — re-validate one class at a time

The flag was **inert for its whole life**. All 72 `ShareMeshAcrossTests => true` overrides were
written against a kill-switch hard-coded `false`, so **not one was ever executed shared**. Turning
the lifetime on does not "restore an optimisation" — it runs classes in a mode none of them has been
observed in. That is why adoption is per assembly, then measured per class.

It is not a theoretical risk. `MeshWeaver.Autocomplete.Test` had six such classes; five survived
sharing and one did not:

| | sharing ON | sharing OFF |
|---|---|---|
| `AutocompleteIntegrationTest.ChatAutocomplete_GlobalFanOut_ReachesOtherPartitions`, full assembly | **2 failures / 22 runs** | **0 / 21** |
| the same class run **alone**, sharing ON | **0 / 20** | — |

Run alone it was perfectly green, so the fixture was not simply broken: what it did not tolerate was
a mesh that had already served the class's other 22 cases, in a process where the rest of the
assembly ran first. Its opt-in was declined in the class with the measurement recorded beside it,
and the test itself was never touched. (That suite now lives in MeshWeaver.Plugins; the lesson is
why `AppIconAdoptionTest`'s opt-in above was measured 20/20 before it was turned on, not after.)

**A class that flakes under sharing has its own opt-in turned off with the numbers written down** —
never by relaxing the test, and never by widening a timeout to hide it.

## 🚨 Adding a test project: two places, or the suite runs nowhere

`shard-assign.sh` globs `find test -name '*.csproj'`. The build builds **`MeshWeaver.slnx`**. Those
are different lists, and a project in the first but not the second is **assigned to a shard and
never built** — it contributes no `bin`, its tests execute nowhere, and nothing says so.

That is what happened when this facility first landed (PR #2966). Worse, it did not surface as
"project not built". The packaging step read:

```bash
[ -d "$d" ] && echo "$d"
```

A `while` loop's exit status is that of its **last iteration**, so under `set -euo pipefail` this
killed the step whenever the last project *assigned to a shard* had no `bin` — a bare
`Process completed with exit code 1`, no message, and the step's own empty-list guard never reached.
Four steps later it read as *"No test results found in any shard — the suite did not run"*.

It is a pure lottery on sort order: the same missing project one position earlier in the shard's
list is silently tolerated, which is why the latent bug had never fired before. Both halves are now
fixed — the loop uses `if`, so the real guard gets to speak, and a dedicated step fails RED naming
any test project that is not a solution member.

**When you add a test project, add it to `MeshWeaver.slnx` in the same commit.**

## Parallelism

**Rows are not parallelised, and nothing here changes the parallelism model.** `TestCaseOrderer`
count in the estate is **zero**, so nothing depends on execution order — but xunit already runs the
cases of one collection sequentially, and the shared-mesh classes were never validated under
intra-collection parallelism. The per-project `xunit.runner.json` opt-in remains the only way a
project gets parallel collections.

## What this does NOT cover

- **The in-mesh node lane still cannot express a data case.** In-mesh `Test/*.cs` is compiled by
  Roslyn *inside a portal*, and a portal image ships no xunit assemblies. Test source that writes
  `[Theory]` would compile in the tester container (whose `ContainerReferenceSet.Read` folds the
  builder process's own TPA into the reference set) and fail to compile on a real portal. Bringing
  xunit to that lane means putting it in the portal's compilation reference set — a separate,
  larger decision.
- **The static lane (`StaticTestRunner`) is unchanged.** It has no xunit and reflects over
  parameterless statics.
- **`MonolithMeshTestBase` is not retired.** This removes the reason it could not be — a shared mesh
  now has an end — but the migration is its own work.
- **One core assembly is converted, and one class in it shares.** `MeshWeaver.Graph.Test` runs on
  the host (1063 cases); `AppIconAdoptionTest` is the class that shares a mesh. Every other class in
  it is unaffected, because a class that does not declare `ShareMeshAcrossTests` never takes the
  shared path.
- 🚨 **Core has no other opt-ins left.** All 72 `ShareMeshAcrossTests => true` classes moved to
  MeshWeaver.Plugins with the 17 mesh suites (`b253cec0a`), so the population this facility was
  built for now lives THERE. `MonolithMeshTestBase` stays here with 64 consumer files, and the host
  is a core assembly, so Plugins adopts it the same way: one assembly at a time, measured per class.

## See also

- [Asynchronous Calls](../AsynchronousCalls) — why the synchronous `GetOrCreate` overload exists.
- [AsyncLocal Across Hops](../AsyncLocalAcrossHops) — the flow rule the ambient scope relies on.
