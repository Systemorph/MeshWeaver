---
nodeType: Markdown
name: Decentralised Tests
category: Architecture
description: The plan to retire MonolithMeshTestBase and OrleansTestBase by moving tests to the module they are about — the measured survey of core's 44 test projects and 6,841 xunit case declarations, the four setup shapes 463 subclasses reduce to, the blocker histogram, the xunit features the two in-mesh runner lanes still lack, and the honest floor of what cannot decentralise.
icon: /static/NodeTypeIcons/code.svg
---

# Decentralised Tests

**Goal, from the maintainer: eliminate `MonolithMeshTestBase` and `OrleansTestBase`.** Not by
rewriting 463 classes onto a new base — by **decentralising tests to the module they are about**, so
that nothing is left inheriting them and the two files can be deleted.

> *"refactoring goal: eliminate monolith.testbase and orleans.testbase"*
> *"anything which is a plugin surely does not stay xunit. test process is 1) take image,
> 2) install plugin, 3) execute test code"*
> *"the substrate ones are testable in-mesh ==> test container must boot with this plugin enabled"*
> *"each repo should only use itself"* · *"decentralize" … "also tests"*

Tests are the third artefact to decentralise, after skills and docs:

| artefact | today | destination |
|---|---|---|
| skills | `{Module}/Skill/*.md` | ship in the module bundle (`ModuleSkillProvider`, Plugins#1060) |
| docs | one tree under `MeshWeaver.Documentation/Data/` | with the functionality they document |
| **tests** | **111 xunit projects across core + Plugins** | **`{Module}/Test/`, shipped and executed with the module** |

This is not a new mechanism. **78 NodeTypes in `MeshWeaver.Plugins` already carry a `<Type>/Test/`
folder, 76 of them with a `Tests` layout area**, executed by `mw-plugin-test` against a pinned image.
Those were never xunit. The xunit estate is the *centralised exception*, not the norm.

Measured against `Systemorph/MeshWeaver@f8e208567` (core `origin/main`) and
`Systemorph/MeshWeaver.Plugins@origin/main`, 2026-09-01. **Re-measure before acting; the numbers
move weekly.**

---

## 1. What is actually there — the measured estate

🚨 **Correct a number that has been repeated wrongly.** "68 test projects under `test/`" is
**Plugins'** count (69 test csproj), not core's. **Core has 44 project directories under `test/`** —
40 with cases, 4 support libraries (`MeshWeaver.Fixture`, `MeshWeaver.Hub.Fixture`,
`MeshWeaver.TestDomain`, `MeshWeaver.Data.TestDomain`) plus the two test bases this page is about.
The **6,817 cases** figure IS core's; comment-stripped the count is **6,841**.

| | core | Plugins | total |
|---|---:|---:|---:|
| test projects | 44 | 67 | **111** |
| xunit cases *(declarations)* | 6,841 | **7,030** | **13,871** |

🚨 **A second number to correct: the estate is ~13,900 declarations, not ~10,760.** Plugins carries
slightly MORE than core (7,030 vs 6,841) across 67 projects — and seven of its suites share a name
with a core suite (`Hosting.Monolith.Test`, `PluginCatalog.Test`, `Query.Test`, `Security.Test`,
`GitSync.Test`, `Auth.Test`, `Content.Test`), which is the carve-out's footprint. Its largest single
suite is `MeshWeaver.AI.Test` at 1,354.

🚨 **Every case count on this page is a count of `[Fact]`/`[Theory]` DECLARATIONS, not of executed
cases.** `[InlineData]` multiplies them: `MeshWeaver.Persistence.Test` declares 99 and **executes
1,137** (measured on this branch, 46 s). The executed estate is several times 6,841. That gap is not
bookkeeping — it is the whole argument of §5b, and it is why an in-mesh runner without data cases is
not a substitute.

### Base-class fan-in (core, direct derivations)

| base | classes | cases reached |
|---|---:|---:|
| `MonolithMeshTestBase` | **439** | **2,092** |
| `HubTestBase` | 155 | 611 |
| `GitHubSyncTestBase` | 29 | *(within Monolith)* |
| `TestBase` (direct) | 14 | 31 |
| `OrleansTestBase<T>` | **12** ⁽¹⁾ | **23** |
| `OrleansSharedTestBase` | **12** | **27** ⁽¹⁾ |
| `DataValidationTestBase` | 5 | 12 |
| `TodoDataTestBase` | 2 | *(within Hub)* |
| *no base at all* | — | **4,048** |

⁽¹⁾ Re-measured by RUNNING the assembly during step 3: `OrleansSharedTestBase` reaches 27 cases,
not 24, and the `OrleansTestBase<T>` row's twelfth "class" is the non-generic `OrleansTestBase`
declaration — a base, not a suite. Eleven suites, 23 cases. Both bases together: **23 suites,
50 cases**. Counting `[Fact]` declarations undercounts; only a run is evidence.

🚨 **The first honest correction: the two bases in scope carry 2,139 of 6,841 cases — 31%.**
**4,048 cases (59%) inherit no test base at all** — they are plain xunit unit tests that never boot a
mesh. Deleting `MonolithMeshTestBase` and `OrleansTestBase` does not touch them, and no plan should
claim their number as progress. `HubTestBase` (611 cases) is a **third** base, not in the maintainer's
scope, and it is the natural landing place for anything that needs hubs but not a mesh.

---

## 2. Per-project classification

Buckets are the maintainer's: **plugin** (its subject ships as an installable module ⇒ install-and-
execute), **substrate** (the mesh runs *on* it ⇒ in-mesh with the container booted **with that backend
enabled**), **framework primitive** (below any mesh boot), **not-a-mesh-thing**, and — added by
measurement — **harness** (the code that *boots* the mesh, which cannot run inside one).

| project | cases | Mono / Orl / Hub / pure | bucket | destination module | exec |
|---|---:|---|---|---|---|
| `MeshWeaver.Graph.Test` | 1332 | 110 / – / 60 / 1162 | framework primitive + **harness** | `MeshWeaver.Graph`, `.Compiler`, `.Plugin.Build` | mixed — see §6 |
| `MeshWeaver.Hosting.Monolith.Test` | 703 | 557 / – / – / 146 | **substrate** (in-process host) | `MeshWeaver.Hosting.Monolith` | in-mesh, monolith bootstrap |
| `MeshWeaver.PluginCatalog.Test` | 581 | 175 / – / – / 406 | substrate (install machinery) | `MeshWeaver.PluginCatalog` | in-mesh |
| `MeshWeaver.Layout.Test` | 398 | – / – / 209 / 189 | framework primitive | `MeshWeaver.Layout` | in-mesh (no mesh needed) |
| `MeshWeaver.Data.Test` | 351 | – / – / 172 / 167 | framework primitive | `MeshWeaver.Data` | in-mesh (no mesh needed) |
| `Memex.Portal.Shared.Test` | 333 | 52 / – / – / 281 | **host composition** | *(no module — it IS the host)* | **stays xunit** |
| `MeshWeaver.Query.Test` | 328 | 147 / – / 10 / 171 | framework primitive (mesh query) | `MeshWeaver.Graph` | in-mesh |
| `MeshWeaver.Security.Test` | 321 | 283 / – / – / 38 | framework primitive (access) | `MeshWeaver.Graph` / `.Hosting` | in-mesh |
| `MeshWeaver.Messaging.Hub.Test` | 272 | – / – / 102 / 168 | framework primitive | `MeshWeaver.Messaging.Hub` | in-mesh (no mesh needed) |
| `MeshWeaver.Hosting.Test` | 243 | – / – / 20 / 223 | **substrate** (host builder) | `MeshWeaver.Hosting` | mixed |
| `MeshWeaver.PluginTester.Test` | 239 | – / – / – / 239 | **harness** — tests `mw-plugin-test` | *(the runner itself)* | **stays xunit** |
| `MeshWeaver.Hosting.Orleans.Test` | 192 | – / 47 / – / 118 | **substrate** (Orleans) | `MeshWeaver.Hosting.Orleans` | in-mesh, **orleans bootstrap** |
| `MeshWeaver.GitSync.Test` | 164 | 102 / – / – / 62 | substrate (sync engine) | `MeshWeaver.GitSync` | in-mesh |
| `MeshWeaver.Auth.Test` | 155 | 150 / – / – / 5 | host composition (sign-in) | `Memex.Portal.Shared` | mixed |
| `MeshWeaver.Content.Test` | 154 | 95 / – / – / 59 | framework primitive | `MeshWeaver.Graph` | in-mesh |
| `MeshWeaver.Documentation.Test` | 137 | 5 / – / – / 132 | **plugin** (docs content) | `MeshWeaver.Documentation` | install-and-execute |
| `MeshWeaver.Markdown.Test` | 121 | 7 / – / – / 114 | **plugin** | `MeshWeaver.Markdown` | install-and-execute |
| `MeshWeaver.Persistence.Test` | 99 | 81 / – / – / 18 | **substrate** (storage adapters) | `MeshWeaver.Hosting.*` backends | in-mesh, per-backend bootstrap |
| `MeshWeaver.Autocomplete.Test` | 98 | 89 / – / – / 9 | framework primitive | `MeshWeaver.Graph` | in-mesh |
| `MeshWeaver.PathResolution.Test` | 93 | 37 / – / – / 56 | framework primitive | `MeshWeaver.Graph` | in-mesh |
| `MeshWeaver.Json.Test` | 78 | – / – / – / 78 | **not-a-mesh-thing** (pure) | `MeshWeaver.Utils` | stays xunit — no gain |
| `MeshWeaver.NodeOperations.Test` | 76 | 66 / – / 5 / 5 | framework primitive | `MeshWeaver.Mesh.Operations` | in-mesh |
| `MeshWeaver.Acme.Test` | 65 | 65 / – / – / – | **plugin** (demo) | Acme sample module | install-and-execute |
| `MeshWeaver.ContentCollections.Indexing.Test` | 62 | – / – / – / 62 | **plugin** | `MeshWeaver.ContentCollections.Indexing` | install-and-execute |
| `MeshWeaver.FutuRe.Test` | 59 | 46 / – / – / 13 | **plugin** (demo) | FutuRe sample module | install-and-execute |
| `MeshWeaver.Portal.E2E.Test` | 54 | – / – / – / 54 | **harness** (Playwright) | *(outside-in)* | **stays xunit** |
| `MeshWeaver.ContentCollections.Indexing.Graph.Test` | 28 | 8 / – / – / 20 | **plugin** | `…Indexing.Graph` | install-and-execute |
| `MeshWeaver.Reactive.Assertions.Test` | 23 | – / – / – / 23 | not-a-mesh-thing (a test library) | `MeshWeaver.Reactive.Assertions` | stays xunit |
| `MeshWeaver.ContentCollections.Test` | 18 | 2 / – / 15 / 1 | **plugin** | `MeshWeaver.ContentCollections` | install-and-execute |
| `MeshWeaver.AccessControl.Test` | 16 | 6 / – / – / 10 | framework primitive | `MeshWeaver.Layout` | in-mesh |
| `MeshWeaver.Serialization.Test` | 14 | – / – / 13 / 1 | not-a-mesh-thing (pure) | `MeshWeaver.Data` | stays xunit — no gain |
| `MeshWeaver.Kernel.Test` | 11 | – / – / – / 11 | **plugin** (script kernel) | `MeshWeaver.Kernel` | install-and-execute |
| `MeshWeaver.Todo.Test` | 6 | 6 / – / – / – | **plugin** (sample) | `MeshWeaver.Todo` | install-and-execute |
| `MeshWeaver.Search.Test` | 5 | – / – / 5 / – | **plugin** | `MeshWeaver.ContentCollections` | install-and-execute |
| `MeshWeaver.Testcontainers.Test` | 4 | – / – / – / 4 | **harness** | *(boots the container)* | **stays xunit** |
| `MeshWeaver.PluginImage.Test` | 4 | – / – / – / 4 | **harness** | *(boots the image)* | **stays xunit** |
| `MeshWeaver.PythonDemo.Test` | 2 | 2 / – / – / – | **plugin** (demo) | PythonDemo sample | install-and-execute |
| `MeshWeaver.MathDemo.Test` | 1 | 1 / – / – / – | **plugin** (demo) | MathDemo sample | install-and-execute |

**Bucket totals (cases):**

| bucket | projects | cases | share |
|---|---:|---:|---:|
| framework primitive | 10 | 2,107 | 30.8% |
| **substrate** | 6 | **1,982** | 29.0% |
| compiler/build core (`Graph.Test`) | 1 | 1,332 | 19.5% |
| **plugin (installable module)** | 12 | **515** | 7.5% |
| host composition | 2 | 488 | 7.1% |
| harness (boots the thing under test) | 4 | 301 | 4.4% |
| not-a-mesh-thing (pure) | 3 | 115 | 1.7% |
| *(support libraries)* | 5 | 1 | – |
| **total** | **43** | **6,841** | |

🚨 **The second honest correction: "anything which is a plugin does not stay xunit" removes 515
cases, 7.5% of core's estate.** Core is a framework repo — almost nothing in it ships as an installable
module. The volume is in *framework primitive* and *substrate*, and the maintainer is right that
**substrate is migratable**: booting a mesh **on** PostgreSQL and asserting from inside is a more
faithful test than a mock, not a less faithful one. That is what the bootstrap seam exists to say.

---

## 3. The 463 subclasses reduce to FOUR knobs

The elimination is only tractable if the 439 + 24 classes are not 463 different setups. **They are
not.** Measured by parsing every class body that transitively reaches each base and recording which
`virtual` it overrides:

### `MonolithMeshTestBase` — 471 class bodies (439 direct + generic/nested)

| shape | classes |
|---|---:|
| `ConfigureMesh` only | 174 |
| **nothing at all — the bare base** | **111** |
| `ConfigureMesh` + `ShareMeshAcrossTests` | 35 |
| `ConfigureMesh` + `SetupAccessRightsAsync` | 29 |
| `ConfigureMesh` + `DisposeAsync` | 23 |
| `ShareMeshAcrossTests` only | 23 |
| `ConfigureClient` + `ConfigureMesh` | 19 |
| `ConfigureClient` only | 10 |
| everything else (deadlines, `PreWarmNodeTypeHubs`, combinations) | 47 |

**Four knobs cover 96%:** `ConfigureMesh` (~285), `ShareMeshAcrossTests` (~74), `ConfigureClient`
(~53), `SetupAccessRightsAsync` (~34). The long tail is two deadline properties and `DisposeAsync`.

### `OrleansTestBase` / `OrleansSharedTestBase` — 23 class bodies

| shape | classes |
|---|---:|
| **nothing at all — the bare base** | **20** |
| `ConfigureMesh` | 2 |
| `ConfigureClient` | 1 |

🚨 **`OrleansTestBase` is not a design, it is a hosting choice.** 20 of 23 subclasses override
nothing. That is exactly the shape the bootstrap seam replaces: *which mesh do I boot* is a class-level
parameter, not a second base class.

> ✅ **DONE (step 3).** The two bases are now one, `OrleansMeshTestBase`. **Re-measured while doing
> it, because two of the numbers above are wrong:** the two bases carry **23 classes / 50 cases**,
> not 24 / 47 — `OrleansTestBase`'s family is 11 classes / 23 cases (the "12th class" the parser
> counted is the non-generic `OrleansTestBase` declaration itself, which is a base, not a suite) and
> `OrleansSharedTestBase`'s is 12 classes / **27** cases, not 24. The whole assembly executes **207**
> cases, not the 192 the `[Fact]`-counting survey reported. Verdict before and after the conversion:
> **207 passed / 0 failed**, 30 s → 26 s.
>
> 🚨 **And the step as written does not work.** "Convert the subclasses to `Bootstrap =>
> MeshBootstrap.Orleans(…)`" reads as *make them `MonolithMeshTestBase` subclasses*. They cannot be:
> a cluster is deployed ASYNCHRONOUSLY, in `InitializeAsync`, whereas `ConfigureMeshBase` — where
> `Bootstrap.Bootstrap(builder)` is called — runs in the CONSTRUCTOR, before any cluster exists; and
> 18 of these suites assert on `Cluster` or `SiloServices`, surfaces a base that must not reference
> Orleans cannot expose. What the seam unifies is the **declaration**, not the boot. So the win is
> the one the measurement actually supports: the two Orleans bases were the same machinery written
> twice (deploy, client hub + `AccessContext` + routing registration, tracked teardown), differing
> in three VALUES — silo configurator, silo count, leased-or-dedicated — and values do not need a
> second base class.

### And what is inside `ConfigureMesh`?

322 `ConfigureMesh` bodies, tagged by what they do (a body can do several):

| tag | bodies | expressible as a bootstrap/manifest declaration? |
|---|---:|---|
| **module-enable** (`AddGraph`, `AddPluginCatalog`, `AddDocumentation`, `AddAcme`, `AddContentCollections`, …) | 151 | **yes** — this IS "install these modules" |
| **DI-registration** (`ConfigureServices`, `AddSingleton`) | 119 | **partly** — see §4 |
| **node-seed** (`AddMeshNodes`, `AddSpaceType`, `SaveNodeSynchronously`) | 117 | **yes** — seed data ships with the module |
| **access-setup** (`UserRole`, `AddRowLevelSecurity`, `PublicAdminAccess`) | 77 | **yes** |
| **persistence-choice** (`AddPartitionedFileSystemPersistence`, `AddInMemoryPersistence`) | 58 | **yes** — `StorageProvider` on the bootstrap |
| **hosting-choice** (`UseMonolithMesh`) | 54 | **yes** — and today 54 classes *re-declare it inline*, which is the duplication the seam removes |
| config (`AddInMemoryCollection`) | 26 | yes |
| other | 41 | case by case |

---

## 4. The blockers, with counts

In Plugins the blocker was `ServiceProvider.GetRequiredService<T>()` — 169 files, because in-mesh
*area* code is compiled node source running in a NODE hub and cannot reach the host container.

**Core's equivalent: 384 files, 1,145 sites.** The histogram is far narrower than the raw count:

| resolved type | sites | cumulative |
|---|---:|---:|
| `AccessService` | 260 | 23% |
| `IMeshService` | 256 | 45% |
| `IStorageAdapter` | 74 | 52% |
| `IMessageHub` | 53 | 56% |
| `IWorkspace` | 31 | 59% |
| `IMeshChangeFeed` | 29 | 62% |
| `IContentService` | 29 | 64% |
| `IMeshNodeStreamCache` | 19 | 66% |
| `IPathResolver` | 18 | 68% |
| `ILoggerFactory` | 18 | 69% |
| `ITypeRegistry` | 16 | 71% |
| *(~120 further types)* | 342 | 100% |

🚨 **Eleven types account for 803 of 1,145 sites (70%).** The facility question is therefore *"can a
decentralised test be handed a context exposing about a dozen mesh services?"* — not *"can it reach an
arbitrary container?"*.

And the answer differs by **how** the test ships, which is the single most important design choice on
this page:

| test ships as… | reaches the host container? | reaches `internal` types? |
|---|---|---|
| **compiled node `Source`** (today's in-mesh `Tests` area) | **no** — it runs in a node hub | no |
| **an assembly loaded into the host** (`{Module}/Test/` compiled into or beside the module DLL) | **yes, by construction** | **yes**, via `InternalsVisibleTo` |

**Decentralising tests must take the second route.** Then `GetRequiredService<T>` is not a blocker at
all: it needs the runner to hand each test the mesh's `IServiceProvider`. That is **one facility**, and
it retires all 1,145 sites — and §5 shows the mechanism already half-exists as `StaticTestRunner`'s
`Outcome.NeedsMesh`, which routes a case *that takes a parameter* to the mesh lane.

**The shipping route is settled by that choice too.** Plugins#1060's `ModuleSkillProvider` is the
precedent for per-module content: `<EmbeddedResource Include="MeshSkills/**/*.md" />` — one csproj
line, no marker attribute, no reference to the consuming module — read by an `IStaticNodeProvider`
over `IEnumerable<InstalledModuleAssembly>` (what `MeshBuilder.InstallAssemblies` records). A
`MeshTests/{Package}/…` analogue would be a direct structural copy, **and it is the wrong route for
core.** That mechanism serves *source the mesh compiles*; a framework assembly's tests are already
compiled, already have `InternalsVisibleTo`, and `StaticTestRunner` already loads the built assembly
and reflects over it. 🚨 It also carries a cost that matters here: a statically served path **cannot
also hold a durable row**, so an embedded-resource route would forbid a test node ever being written
to. Ship core's decentralised tests as **types in (or beside) the module assembly**, discovered by
reflection — not as embedded source.

### The second blocker: registering test doubles into the mesh

119 `ConfigureMesh` bodies register into the host container. What they register:

| registered | bodies |
|---|---:|
| `IConfiguration` (test settings) | 26 |
| `ILoggerProvider` | 9 |
| `INodeValidator` implementations (Rejecting, RequireTitle, ScriptedOutcome, …) | 12 |
| `IStaticNodeProvider` implementations | 8 |
| `IStorageAdapter` | 4 |
| `IMeshQueryProvider` | 4 |
| `ILogonAction`, `IPackageSource`, `IGitHubRepoClient`, `IHttpClientFactory`, … | ~15 |

🚨 **This is the real facility gap, and it is ~50 sites, not 1,145.** A decentralised suite must be
able to say *"boot the mesh with THIS extra registration"*. Because the test ships as an assembly, the
registration is just a delegate — the bootstrap needs a `WithServices(Action<IServiceCollection>)`
escape hatch, applied once per suite. `IConfiguration` and `ILoggerProvider` (35 of the 119) are
infrastructural and belong on the runner, not in test code.

### Other blockers, measured (files / cases)

| blocker | files | cases in those files |
|---|---:|---:|
| reaches the host container | 384 | — |
| touches the real filesystem (`Directory.CreateDirectory`, `Path.GetTempPath`) | 146 | ~1,400 |
| `System.Reflection` | 49 | — |
| `AssemblyLoadContext` / `Assembly.LoadFrom` | 24 | ~200 |
| `BindingFlags` into internals | 18 | ~110 |
| Playwright | 45 | 54 |
| `HttpClient` | 16 | 31 |
| `Environment.GetEnvironmentVariable` | 24 | 38 |
| Testcontainers | 3 | 9 |
| `Process.Start` | 6 | ~60 |
| NSubstitute | 3 | 4 |
| Moq / FakeItEasy | **0** | 0 |

**The filesystem is not a blocker.** A test running inside the portal container has a filesystem; the
paths just live in the container. The `AssemblyLoadContext` cases *are* a blocker, because they are
the compile/emit pipeline the in-mesh runner itself depends on (§6).

**There is no mocking framework to port.** Zero Moq sites; four NSubstitute sites. Test doubles here
are hand-written classes, which ship with the module like any other type.

---

## 5. What the in-mesh runner is today — TWO lanes, not one

Before asking what a runner must replicate, be precise about what exists. `mw-plugin-test`
(`tools/MeshWeaver.PluginTester/`) drives **two different lanes**, and they discover tests
differently. Conflating them is the main way this design goes wrong.

### Lane A — the mesh gate (renders the `Tests` area)

`PluginGateRunner` + `AreaProbe`: stands up a mesh, imports content, renders each type's `Tests`
layout area over the ordinary client sync stream, and classifies the frames
(`AreaProbe.ClassifyTestsFrame`): compile-progress and *"Area not found"* are transient; `❌` is red;
`N/M passed` is green iff `N == M`; **no verdict inside the timeout is RED** — *"a Tests area that
reports nothing is a broken gate, never a silent pass."*

The suites it runs are hand-rolled. `Store/Catalog/Test/CatalogTestsArea.cs` is the canonical shape:

```csharp
var cases = new (string Name, Action Body)[]
{
    ("Packs: the backfill trigger is exact", StoreCatalogTests.StandardPacksBackfill_TriggerIsExact),
    …
};
```

— a **manual `(name, delegate)` array**, bodies that are `public static void` and signal failure by
`throw`, and a `StringBuilder` markdown table emitted as ONE frame at the end. A separate
`(string, Func<IObservable<Unit>>)` array carries hosted cases through `RunLive` (45 s budget,
`DefaultIfEmpty` so *"completed without an outcome"* is a named row rather than silence); only 3 of
74 `*TestsArea.cs` files use it.

🚨 **A method that is not listed in the array is never run.** There is no discovery in Lane A.

### Lane B — the build lane (`mw-compiler build`, no mesh)

`CascadeBuild` → `StaticTestRunner`. **This lane is already xunit-shaped**, and it is the one to
build on:

- loads the just-emitted assembly into a **collectible `AssemblyLoadContext`**;
- **discovers by reflection and convention** — `static class` whose name ends `Test`/`Tests`/`TestsArea`,
  public static methods, not compiler-generated;
- runs each case on **its own thread with `Thread.Join(timeout)`**, so a hang is reported by name and
  the build continues;
- `Outcome` is `Passed | Failed | NeedsMesh` — **a method that takes parameters is classified
  `NeedsMesh`, counted and named, never dropped**;
- `Run.IsGreen => LoadError is null && Failed == 0`, with the explicit rule *"No cases is green, not a
  pass."*

🚨 **`NeedsMesh` is the existing answer to "how does a test get a mesh handle": it takes one as a
parameter.** Today that parameter is a `LayoutAreaHost`. Generalising it to a test context that also
exposes the mesh's `IServiceProvider` is an extension of a mechanism that already ships — not a new
one.

### The xunit shim is already in the tree, and is not wired up

`tools/MeshWeaver.PluginTester/Testing/TestContext.cs` (namespace `MeshWeaver.Testing`) already
defines, verbatim for this purpose:

- `TestContext.Current` → `TestContextData(CaseName, CancellationToken)`, `[ThreadStatic]`, installed
  by `TestContext.Enter(…)` — *"the shape xUnit's `TestContext.Current` gave the suites this
  replaces, so a migrated body keeps reading `TestContext.Current.CancellationToken`"*;
- `SkipException(string reason)` — *"The runner reports it as `skipped` — never as passed, never as
  failed"*;
- `TestLog.WriteLine` — *"the role `ITestOutputHelper` played in the suites this replaces."*

🚨 **`StaticTestRunner` does not reference any of it.** There is no `TestContext.Enter` call, no
`SkipException` catch, no `Outcome.Skipped`. The shim is landed and unwired — the cheapest first
facility on the list, and a gate that silently never skips is worse than one that cannot.

## 5b. The xunit features that must be replicated, with counts

| feature | sites (core `test/`) | Lane A | Lane B | verdict |
|---|---:|---|---|---|
| `[InlineData]` | **1,767** | ✗ | ✗ | 🚨 **must build** |
| `[Theory]` | **431** | ✗ | ✗ | 🚨 **must build** |
| `[MemberData]` | 24 | ✗ | ✗ | must build |
| discovery (no manual registry) | every case | ✗ | **✓** | use Lane B |
| `[Collection]` / `[CollectionDefinition]` | 130 / 22 | ✗ | ✗ | must build (serialisation groups) |
| `IAsyncLifetime` | 10 | ✗ | ✗ | replaced by per-suite bootstrap |
| `IClassFixture` / `ICollectionFixture` | 8 / 2 | ✗ | ✗ | replaced by per-suite bootstrap |
| `Skip =` | 6 | ✗ | **designed, unwired** | wire `SkipException` |
| per-case timeout | — | live cases only | **✓** | done |
| `ITestOutputHelper` | — | ✗ | **`TestLog`, unwired** | wire it |
| `[ClassData]` | 0 | — | — | not needed |
| `TestCaseOrderer` | **0** | — | — | **not needed — nothing depends on order** |
| `[Trait]` | 0 | — | — | not needed |

🚨 **`[Theory]` + `[InlineData]` is the requirement that decides the plan, and the raw count
understates it.** Measured on a real suite: `MeshWeaver.Persistence.Test` declares **99**
`[Fact]`/`[Theory]` and executes **1,137 cases** — an 11× expansion. **Neither lane can express a
data case.** Today the in-mesh idiom is an in-body `foreach` over a local array, which collapses N
logical cases into ONE row and aborts the rest at the first failure. Migrating 431 theories onto that
would turn ~2,000+ per-case verdicts into 431 opaque ones and lose the "which row failed" information
that makes a red run actionable.

**Build the data-case facility before migrating any suite.**

## 6. The honest floor — what cannot decentralise

**Hard floor: 535 of 6,841 cases (7.8%).** These cannot execute inside a booted mesh *by
construction* — not for want of a facility, but because they are about the boot itself.

| class | projects | cases | why |
|---|---|---:|---|
| **A. is the harness** | `Portal.E2E.Test` (54), `PluginTester.Test` (239), `Testcontainers.Test` (4), `PluginImage.Test` (4) | **301** | drives a browser, or boots the container/image the other tests would run inside. A test cannot be its own harness. |
| **B. constructs the host it asserts on** | `Memex.Portal.Shared.Test` (73), `Hosting.Orleans.Test` (54), `Hosting.Monolith.Test` (24), `Hosting.Test` (2) | **153** | asserts on `HostApplicationBuilder` / `TestCluster` / `IHostApplicationLifetime` composition. You cannot assert a boot from inside a booted host. |
| **C. spawns an external process** | `Graph.Test` (21), `Documentation.Test` (8), `Serialization.Test` (3) | **32** | invokes `dotnet`/`git`/`python` and asserts on the result. |
| **D. loads/unloads assemblies itself** | `Graph.Test` (70), `Hosting.Monolith.Test` (14), `Hosting.Test` (11), `Messaging.Hub.Test` (8), `PluginCatalog.Test` (7), `Layout.Test` (7), others (5) | **~122** | the ALC/emit pipeline. 🚨 **Circular**: this pipeline is *how* in-mesh code runs. A broken compiler cannot report its own test failure. |

**Plus a soft floor of ~115 cases with nothing to gain**: `Json.Test` (78), `Reactive.Assertions.Test`
(23), `Serialization.Test` (14) are pure functions over pure data. Nothing blocks moving them; nothing
is learned by doing so. Leave them.

**So the honest arithmetic for core:**

| | cases |
|---|---:|
| total | **6,841** |
| hard floor (cannot run inside a mesh) | −535 |
| soft floor (pure, no fidelity gain) | −115 |
| **can move, given the facilities in §4–§5** | **≈6,190 (90%)** |
| …of which need a facility that **does not exist yet** | **all of them** |

🚨 **The last row is the one to read. Not one of the 6,190 can move today.** Five facilities are
missing or unwired, and until each exists the count of migratable cases is **zero**:

| # | facility | state | cost |
|---|---|---|---|
| 1 | **`[Theory]` / `[InlineData]` / `[MemberData]` data cases** | **absent from both lanes, not designed** | 🚨 large — and it gates everything, because 431 theories expand to thousands of executed cases (§5b) |
| 2 | **A test context exposing the mesh's `IServiceProvider`** | **half-exists** — `StaticTestRunner.Outcome.NeedsMesh` already routes a case that takes a parameter to the mesh lane; the parameter type must be generalised | medium |
| 3 | **A per-suite service-registration hook** on the bootstrap (`WithServices(Action<IServiceCollection>)`) | absent | **small** — ~50 sites |
| 4 | **`Skip` and test output** | **designed and landed, NOT WIRED**: `TestContext.Current`, `SkipException`, `TestLog` exist in `tools/MeshWeaver.PluginTester/Testing/TestContext.cs`; `StaticTestRunner` references none of them and has no `Outcome.Skipped` | **smallest — do it first** |
| 5 | **`[Collection]` serialisation groups** | absent | medium — 130 sites |

**Only #3 and #4 are genuinely small. An estimate of "90% can move" that does not carry all five
preconditions is an optimistic number** — and #1 alone is a larger piece of work than any single
suite migration on this page.

---

## 7. The bootstrap seam — where it lives

`IMeshBootstrap` is the mechanism by which a base class disappears: **a base class can be deleted when
its setup is expressible as a bootstrap configuration and its subclasses select one.**

```csharp
protected override IMeshBootstrap Bootstrap => MeshBootstrap.Orleans(o => o
    .WithClustering(ClusterProvider.AdoNet, "Server=…")
    .WithGrainStorage(StorageProvider.Redis, "localhost:6379")
    .WithSilos(2));
```

**It lives in core.** Maintainer, 2026-09-01: *"we can migrate it to core for now."* That is settled,
and it is the right way round — core is not allowed to depend on Plugins, and **439 of the 463
inheriting classes are in core**.

🚨 **"For now" is doing real work in that sentence.** Its final home is with the **substrate** module
it describes, once tests are decentralised: `MonolithBootstrap` with `MeshWeaver.Hosting.Monolith`,
`OrleansBootstrap` with `MeshWeaver.Hosting.Orleans`, and the `IMeshBootstrap` contract in the
messaging/mesh contract layer everything already references. So the seam is deliberately kept
relocatable: it depends on `MeshBuilder` and nothing else, `OrleansBootstrapOptions` is a **pure
record** (validated without a cluster), and the Orleans applicator is a **static delegate the
Orleans-referencing assembly registers** — which is why `MeshWeaver.Hosting.Monolith.TestBase` never
takes an Orleans dependency.

### Retiring Plugins' copy — and a defect the survey found

`MeshWeaver.Plugins/src/MeshWeaver.Hosting.Monolith.TestBase` is a **full fork** of core's
`test/MeshWeaver.Hosting.Monolith.TestBase`: byte-identical except `MeshBootstrap.cs`, the 12-line
`Bootstrap` route, and `$(MeshWeaverRoot)`-relative ProjectReferences.

🚨 **The fork is already inert, and nobody noticed.** Every Plugins test project references the base
with a conditional pair:

```xml
<ProjectReference Include="$(MeshWeaverRoot)/test/MeshWeaver.Hosting.Monolith.TestBase/…"
                  Condition="Exists('$(MeshWeaverRoot)/test/…')" />
<ProjectReference Include="$(MeshWeaverRoot)/src/MeshWeaver.Hosting.Monolith.TestBase/…"
                  Condition="!Exists('$(MeshWeaverRoot)/test/…')" />
```

Core has carried `test/MeshWeaver.Hosting.Monolith.TestBase` since the *"fixture to test"* move
(2026-08-30), so **the first arm always wins and the fork is never referenced by any real suite**. Its
only consumer is `MeshWeaver.Mesh.TestBase.Test`, which references it by relative path — so the
bootstrap seam was pinned by its own unit test and reachable from nothing else. This is the two-homes
failure mode in its purest form: the copy that got the new feature is the copy nothing builds against.

**Retirement, in order:**

1. **(done in this change)** core gains `MeshBootstrap.cs` and the 12-line `Bootstrap` route;
   `MeshBootstrapTest` moves to `test/MeshWeaver.Hosting.Monolith.Test`. Behaviour is unchanged —
   `MonolithBootstrap.Instance` applies exactly `UseMonolithMesh().AddInMemoryPersistence()`, which is
   what `ConfigureMeshBase` inlined before.
2. Plugins **deletes** `src/MeshWeaver.Hosting.Monolith.TestBase/` and
   `src/MeshWeaver.Mesh.TestBase.Test/`, and drops the `!Exists(...)` fallback arm from all ~50
   consumers. There is no delta left to preserve.
3. `MeshWeaver.Hosting.Orleans.TestBase` registers `OrleansBootstrap.Applicator` at module init, so
   `MeshBootstrap.Orleans(…)` can actually stand a cluster up.

Until step 2 lands, **the fork must not receive changes** — it is the drift hazard, not the seam.

---

## 8. The order to do it in

Each step is chosen so the next one is cheaper, and so that **nothing is migrated before the facility
it needs exists**.

| # | step | unblocks |
|---|---|---|
| **1** | **Seam to core** (this change): `IMeshBootstrap` + `Bootstrap` virtual, default unchanged. | Every later step can say *which mesh*. Plugins' fork becomes deletable. |
| **2** | **Delete Plugins' fork**, drop the fallback arms. | Removes the live two-homes hazard before anyone edits either copy. |
| **3** ✅ | **Register the Orleans applicator** in `Hosting.Orleans.TestBase` (a `[ModuleInitializer]`, so the monolith base keeps its zero Orleans references); collapse `OrleansTestBase<T>` + `OrleansSharedTestBase` into **one** `OrleansMeshTestBase` whose cluster is a DECLARATION (`Bootstrap => MeshBootstrap.Orleans(…)` + `SiloConfiguratorType`); convert all 23 core subclasses. | **Done, 50 cases of blast radius, 207/207 green.** The two names remain ONLY as a bridge in `OrleansTestBaseCompat.cs`: MeshWeaver.Plugins derives from them in 11 places and its CI checks this repo out at *main*, unpinned, so deleting them here reds that trunk before its own conversion can land. **Step 3b** is that Plugins PR; **step 3c** deletes the bridge. |
| **4** | **Substrate bootstraps**: `ClusterProvider`/`StorageProvider` gain a real PostgreSQL/Sqlite/Cosmos path so *"boot the container with this backend enabled"* is expressible. | `Persistence.Test` (99) and the six Plugins `Hosting.*` backend suites stop mocking the substrate. |
| **5a** | **Wire the shim that is already landed**: `StaticTestRunner` calls `TestContext.Enter`, catches `SkipException`, gains `Outcome.Skipped`, and routes `TestLog` output. One file, no design work. | A gate that can say *skipped* instead of silently never skipping — and the migration idiom (`TestContext.Current.CancellationToken`) starts working. |
| **5b** | **Generalise `Outcome.NeedsMesh`**: the parameter a hosted case takes becomes a test context carrying the mesh's `IServiceProvider` and hub, alongside `LayoutAreaHost`. Add `WithServices(…)` to `IMeshBootstrap`. | Facilities #2 and #3. Retires the 1,145-site container blocker and the ~50 test-double registrations. |
| **5c** | 🚨 **Build data cases** — `[Theory]`/`[InlineData]`/`[MemberData]` equivalents with **per-row verdicts**, in Lane B's reflection discovery and in the `N/M passed` frame contract Lane A classifies. | Facility #1. **Do not start step 6 before this is green**; migrating a theory-heavy suite without it destroys per-case verdicts irreversibly. |
| **5d** | Prove 5a–5c on **ONE** decentralised suite end to end, measured against its xunit executed-case count (not its `[Fact]` count). | Everything after this. |
| **6** | **Migrate the `plugin` bucket** (515 cases, 12 projects) — smallest, most clearly in scope, and the maintainer's rule says they must not stay xunit. | Validates install-and-execute on real modules. |
| **7** | **Migrate the `substrate` bucket** (1,982 cases) — `Hosting.Monolith`, `PluginCatalog`, `GitSync`, `Persistence`, `Hosting`. | Removes the bulk of `MonolithMeshTestBase`'s 2,092 cases. |
| **8** | **Migrate the `framework primitive` bucket** (2,107 cases) to `{Assembly}/Test/`, co-located, still executed in-mesh where a mesh helps and as plain xunit where it does not. | The last `MonolithMeshTestBase` subclass. |
| **9** | **Delete `MonolithMeshTestBase`.** | — |
| **10** | Fold `HubTestBase` (611 cases) into the same shape, or leave it — it is out of the maintainer's scope and boots no mesh. | — |

**Steps 1–3 eliminate one of the two named base classes for 50 cases of blast radius (measured;
the survey's 47 counted `[Fact]` declarations, not executed cases).** That is the
whole argument for this ordering: the Orleans half is 2% of the estate and 100% of the second goal,
and it needs **none** of the five facilities in §6 — the bootstrap seam alone is enough.
`MonolithMeshTestBase` cannot follow until step 5c is green.

---

## 9. What this change actually contains

- `test/MeshWeaver.Hosting.Monolith.TestBase/MeshBootstrap.cs` — the seam, moved from Plugins.
- `MonolithMeshTestBase.ConfigureMeshBase` routes through `Bootstrap.Bootstrap(builder)`; a new
  `protected virtual IMeshBootstrap Bootstrap => MonolithBootstrap.Instance`.
- `test/MeshWeaver.Hosting.Monolith.Test/MeshBootstrapTest.cs` — 9 cases pinning the fluent API
  without a cluster, moved from Plugins' `MeshWeaver.Mesh.TestBase.Test`.
- this page.

No behaviour change: the default bootstrap emits the same two builder calls that were inlined. All 22
core consumers of `MonolithMeshTestBase` are untouched.
