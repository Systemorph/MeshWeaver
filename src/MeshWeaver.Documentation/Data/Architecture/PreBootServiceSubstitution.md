---
nodeType: Markdown
name: Pre-Boot Service Substitution
category: Architecture
description: How a decentralised suite declares the mesh it needs — the ConfigureMesh declaration the install-and-execute lane honours, why the applicator can only ever CREATE a mesh and therefore cannot exist in a portal, the measured size of the blocked population in both repos, and the honest list of what this seam does not cover.
icon: /static/NodeTypeIcons/code.svg
---

# Pre-Boot Service Substitution

**A test that proves a *composition* cannot be written against an already-composed host.** A
deliberately ABSENT `EffectivePermissionsDelegate`, a `RecordingSubscribeScheduler`, a fake
`IGitHubRepoClient`, an `IStorageAdapter` that hangs — each of those is a statement about the
container the mesh was built from, and an in-mesh `Tests` area boots INTO that container. That is
why "the test registers a double via a `ConfigureMesh` override" was the single largest blocker on
the way off xunit, and why no amount of in-area cleverness closes it: **no additive registration
can un-register anything.**

This page is the design of the seam that closes it, the argument for why it cannot exist in a
production portal, and the measured size of what it does and does not unblock.

---

## 1. The measured population — re-measured on this change, 2026-09-01

Counting `[Fact]`/`[Theory]` DECLARATIONS in classes that override a `ConfigureMesh` /
`ConfigureClient` / `ConfigureHost` hook whose body performs a DI registration:

| | classes | cases | of the repo's declarations |
|---|---:|---:|---:|
| `MeshWeaver.Plugins/src` | 135 | **507** | 507 / 3,902 = 13% |
| `MeshWeaver/test` | 141 | **729** | 729 / 6,704 = 11% |
| **both** | **276** | **1,236** | |

🚨 **Two corrections to figures that have been repeated.** The prior survey put the surface at
"~50 test-double registrations across about four interfaces". Counting the registrations
themselves rather than the classes: **core `test/` has 145 registrations across 43 distinct
types**, and the named four (`INodeValidator`, `IStaticNodeProvider`, `IStorageAdapter`,
`IMeshQueryProvider`) are **35 of those 145 — 24%, not the bulk.** `MeshWeaver.Plugins/src` has
**151 registrations across 30 types**, and its distribution is completely different: a single
interface, `IChatClientFactory`, is **65 of the 151** (the AI suites swapping the model client).
There is no small set of interfaces to special-case. What there is, is one SHAPE — "boot the mesh
composed like this" — which is what the seam expresses.

The second correction is smaller: the blocked-case figure carried into this work was 526 for
Plugins. Re-measured here it is **507**, close enough to confirm the premise and worth stating
exactly rather than repeating.

---

## 2. The seam: a DECLARATION and an APPLICATOR, deliberately far apart

A suite declares the mesh it needs by carrying one ordinary static method:

```csharp
public static class MissingEvaluatorFailsClosedTests
{
    public static MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddGraph()
            .ConfigureDefaultNodeHub(c => c.AddAccessControlPipeline());   // the gate, no evaluator

    public static IObservable<Unit> AMissingEvaluator_RefusesTheDelivery_NeverServesIt(
        IServiceProvider services, IMessageHub client) => …;
}
```

* **No attribute. No interface. No base class. No contract assembly.** The declaration is a static
  method over `MeshBuilder`, a type the suite already references. `MeshWeaver.Security.MeshTest`
  — the suite converted in this change — references **nothing** of `mw-plugin-test`, which is
  asserted (`MeshTestSuiteTest.TheSuiteAssembly_ReferencesNothingOfTheApplicator`).
* **A `MeshBuilder` nobody builds composes nothing.** The declaration is inert data until something
  applies it.
* **Cases bind BY TYPE** from a fixed two-entry table: `IServiceProvider` → the declared mesh's root
  provider (the mesh hub is `services.GetRequiredService<IMessageHub>()`), `IMessageHub` → a fresh
  client hub. A case may return `void`, or `IObservable<Unit>` — the same
  `Func<IObservable<Unit>>` idiom the in-mesh `Tests` areas already use for hosted cases, so a
  migrated body never blocks and never needs a line in `test/BlockingBridgeSites.allow`.

The applicator is `MeshTestSuite` in `tools/MeshWeaver.PluginTester`, driven by
`StaticTestRunner`: for each discovered test class it looks for the declaration, boots that mesh
**once, lazily, for that class**, runs every case whose whole signature binds, and tears the mesh
down in a `finally`.

---

## 3. 🚨 Why a production portal cannot use this — three independent reasons

The constraint is the whole design problem: a facility that lets any node replace a live portal's
`IStorageAdapter` is a security hole, not a test facility. Three things prevent it, and any one of
them is sufficient.

| # | reason | asserted by |
|---|---|---|
| 1 | **There is nothing to scan for.** No marker type exists, so a portal has no set it could enumerate. Adding such a scan would be a new feature, not a use of this one. | — (a property of having no contract) |
| 2 | **The applicator ships only in `mw-plugin-test`**, a `tools/` console binary. No project under `src/`, `memex/`, `clients/` or `samples/` references it, so it is not present in any portal image. | `NoShippingProject_ReferencesTheTester` |
| 3 | 🚨 **The applicator can only CREATE a mesh.** `MeshTestSuite.Boot` takes a `MethodInfo` and returns a brand-new private mesh. **No public member anywhere on it accepts an `IServiceProvider`, an `IServiceCollection` or an `IMessageHub`** — so there is no parameter through which a running host could be handed to it. Even loaded somewhere it does not belong, the worst it could do is stand up a throwaway mesh beside the real one. | `TheFacility_CanNeverTouchAnExistingHost` |

Reason 3 is the load-bearing one, because it survives mistakes in 1 and 2. A seam that "modifies
the mesh you give it" would have to be defended by a flag; a seam that can only build a new one has
nothing to defend.

The applicator also sets **no identity** on the mesh it boots. A suite that substitutes services is
usually ABOUT who may do what; a lane that logged an admin in first would make every fail-closed
assertion pass for the wrong reason.

---

## 4. The alternatives, and why they lost

| alternative | why not |
|---|---|
| **A boot-time manifest the tester image reads** (JSON naming types to register) | The substitution is CODE — `AddRowLevelSecurity` is not a type name, and "the pipeline with no evaluator behind it" is not expressible as a list of registrations at all. A manifest would also be a *data* surface: something a portal could be pointed at by a file, which is exactly the shape that turns into a hole. |
| **A test-only module the harness installs alongside the package** | A module is installed by the mesh, i.e. AFTER boot — the same wall the `Tests` area hits. It also gives the substitution a *shipping* form (a module bundle), which is the one artefact a portal already knows how to load. |
| **A compile-time-gated registration hook (`#if TESTLANE`) in `MeshBuilder`** | Puts the seam in the framework, where it must then be defended forever by a symbol nobody can see in a compiled image. It also cannot express removal, and it makes core's composition root carry a branch that exists for tests. |
| **`WithServices(Action<IServiceCollection>)` on the shared gate mesh** | Additive only — cannot express an ABSENT service, and cannot give two suites contradictory compositions. `MissingEvaluatorFailsClosedTests` and `DeclaredUnsecuredMeshStillServesTests` have identical evaluator state (none) and opposite verdicts; **one shared mesh cannot be both.** |

---

## 5. The converted suite — the proof, and what it costs

`test/MeshWeaver.Security.MeshTest` is `MissingEvaluatorFailsClosedTest` converted end to end: two
classes, three cases, **no xunit, no test SDK, no `MonolithMeshTestBase`**. It is the sharpest case
available because its premise is a service that must be ABSENT.

```
mesh  DeclaredUnsecuredMeshStillServesTests: declared mesh booted (229 ms)
ok    DeclaredUnsecuredMeshStillServesTests.ADeclaredUngatedMesh_IsNotRefusedByTheGate (110 ms)
mesh  MissingEvaluatorFailsClosedTests: declared mesh booted (16 ms)
ok    MissingEvaluatorFailsClosedTests.AMissingEvaluator_RefusesTheDelivery_NeverServesIt (51 ms)
ok    MissingEvaluatorFailsClosedTests.TheRefusal_NamesExactlyWhatToRegister (1 ms)
```

**Falsified before it was believed.** Removing `.ConfigureDefaultNodeHub(c => c.AddAccessControlPipeline())`
from the declaration turns both `MissingEvaluator` cases RED with *"the read was SERVED"*, while the
declared-ungated class stays green — so the declaration really is what the lane boots, and the cases
really do assert.

Two numbers worth noticing: a whole declared mesh boots in **16–229 ms**, and the suite's three
cases finish in **under half a second**. The xunit original paid a fresh `MonolithMeshTestBase`
mesh per `[Fact]`.

---

## 6. What this does NOT unblock — measured, not estimated

Of the 1,236 substituting cases:

| | cases | |
|---|---:|---|
| **A — runnable on this seam as it stands** | **580** | 267 Plugins + 313 core |
| B — plus a `[Collection]` tag and nothing else | 381 | an UPPER bound, see below |
| C — needs a facility this change does not build | 275 | |

**A = 580 is the measured unblock**, and it is the number to quote. The three largest single
contributions are `MeshWeaver.AI.Test` (172), `MeshWeaver.Hosting.Monolith.Test` (71) and
`MeshWeaver.Acme.Test` (61).

🚨 **B is an upper bound and must not be added to A.** `[Collection]` is a serialisation tag, and
the lane serialises a class's cases against its own private mesh by construction — but some
collections exist to SHARE AN EXTERNAL FIXTURE, and those need a container the lane does not boot.
`MeshWeaver.Hosting.PostgreSql.Test` alone is 105 of the 381 and is exactly that case.

**C, by what is missing** (a class can be blocked by more than one):

| missing facility | cases | note |
|---|---:|---|
| `[Theory]`/`[InlineData]`/`[MemberData]` data cases | 198 | neither in-mesh lane can express one; migrating without it collapses N per-row verdicts into one opaque row |
| a host object the table cannot bind (`LayoutAreaHost`, `IJSRuntime`, `NavigationManager`) | 77 | stays with the area lane, correctly |
| `IAsyncLifetime` per-suite setup/teardown | 50 | core only; a `SetUp`/`TearDown` convention would close it and is deliberately not built here |
| `IClassFixture` | 0 | measured zero in both repos — not a facility anyone needs |

Also out of scope, and NOT counted above because they are not blocked by *substitution*: everything
in the hard floor (a test that boots the container it would run inside, drives a browser, or loads
assemblies itself).

---

## 7. Where the case's wait happens, and why it is there

A migrated case returns `IObservable<Unit>`; the **runner** waits, never the suite. `MeshTestSuite.Run`
composes `IgnoreElements().DefaultIfEmpty(Unit.Default)` so the FIRST notification is the terminal
one — a case that emits progress and then faults reports the fault — hands it to
`ReactiveCompletion.ObserveCompletion` (the one sanctioned bridge, which cannot resume its caller on
the signalling thread) and parks with a **bounded** `Task.Wait(TimeSpan)` on the case's OWN dedicated
thread, which `StaticTestRunner` then joins with a looser budget so the inner, NAMED timeout is the
one that reports.

That placement is deliberate: the suites are `test/`-rooted, where `BlockingBridgeSites.allow`
ratchets every blocking bridge and may only shrink. **Putting the wait in the runner is what lets a
migrated body be written with no bridge at all.**

## 8. A boot failure is a FAILED case, never a skip

`StaticTestRunner` reports a declaration that throws as a failed case **per affected method**, naming
the cause. *"The suite could not boot"* and *"the suite has no cases"* must never look alike — that is
the same rule the gate applies to a `Tests` area that reports nothing, and it is why the boot is
attempted lazily but its failure is not swallowed.
