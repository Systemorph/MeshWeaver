---
Title: In-Mesh Build and Test
Description: The shape every plugin should be able to take — source that compiles in the portal, tests that run in the portal, and no NuGet package on the path between a change and a verdict. What already works, what the boundary is, and what has to change to move it.
---

# In-Mesh Build and Test

**Direction of record (maintainer, 2026-08-30), in the maintainer's own terms:**

> *"We should use in-mesh build as well as in-mesh test run. No NuGet packages."*
> *"We should be able to form any plugin into this shape."*
> *"We want the memex to take care of the build, no external build script."*

Four things, and the fourth is the one that reframes the other three: **the mesh is the build
system.** CI triggers it; CI does not perform it.

This page records where that already holds, where it does not, and — the part worth arguing about —
which of the gaps are engineering work and which are real boundaries.

## What "in-mesh" already means here, measured

It is not aspirational. In `MeshWeaver.Plugins` today:

| | count |
|---|---|
| in-mesh node sets with their own `Test/` sources | **206** |
| NodeTypes registering a literal `.WithView("Tests", …)` area | **85** |
| compiled `.Test` projects under `src/` | **44** |
| `PackageReference Include="MeshWeaver.*"` anywhere | **0** |

So the NuGet half of the direction is already true: nothing consumes the platform as a package. Every
reference is a `ProjectReference` across `$(MeshWeaverRoot)` to a sibling checkout.

And the in-mesh half already has a working CI contract. The plugin gate compiles every NodeType from
the source stored on its nodes and then *runs its tests in the portal*, reporting per type:

```
ok  Claims/Claim: compile=Ok render=ok tests=ok
    Tests host: Claims/Claim/GateProbe — the probe instance the gate created for this check
    7/7 passed
```

`compile` is a real Roslyn pass over the node's sources; `tests` is the `Tests` area executed on a
probe instance the gate creates. Neither involves a package, and neither is a `dotnet test`.

> **This page is the CI-invocation layer.** What a plugin build *is* — take the image, install
> dependencies as artifacts, build, test — is [The Plugin Build Contract](/Doc/Architecture/PluginBuildContract),
> and the cascade it runs as is [The Build Process](/Doc/Architecture/BuildProcess). This page is
> about how CI reaches them: one tool, three lines, and no repo-local script.

## The gap is not NodeTypes — it is modules

The 206 node sets are already in the target shape. The 44 compiled test projects are testing
something different: **modules** — `MeshWeaver.Threading`, `MeshWeaver.Speech`, `MeshWeaver.Mcp`,
`MeshWeaver.SelfUpdate.Aks`, `MeshWeaver.Observability`, `MeshWeaver.Markdown.Export` and their
peers. A module is a compiled assembly the portal loads, not source the portal compiles.

So "form any plugin into this shape" reduces to a sharper question: **can a module's source live on
nodes and compile at runtime, the way a NodeType's does?**

For most of them the answer is yes and the work is mechanical. For some it is **no**, and the reason
is not effort:

- **Static assets.** `MeshWeaver.Blazor`, `MeshWeaver.Blazor.Radzen`, `MeshWeaver.Blazor.Views`,
  `MeshWeaver.Blazor.OpenStreetMap` and the three portal hosts ship a `wwwroot/`. A module cannot
  carry static assets, so their content is not expressible as compiled node source.
- **Native binaries.** The interop sets — Cosmos, the Snowflake natives, `vcruntime140` — are files,
  not C#. Nothing compiles them into existence.
- **The host itself.** `Memex.Portal.Distributed` / `Monolith` / `Gui` are what *runs* the mesh. They
  cannot be built by the thing they start.

That boundary is worth stating plainly because it decides the shape of the answer: **the target is
not "no compiled projects", it is "no compiled project on the path between a source change and a
verdict about that source".** A module that is only assets or only interop still ships as bytes; what
must not happen is a C# behaviour change that can only be tested by `dotnet test` against a
ProjectReference.

## The tension with #2193, stated rather than resolved

[#2193](https://github.com/Systemorph/MeshWeaver/issues/2193) is titled *"modules ship as baked DLLs
— no compile fallback, no source in the mesh DB"*. Read flatly, that is the opposite of this page.

They reconcile on **authoring versus shipping**, and the distinction is load-bearing:

- **Authoring and verification are in-mesh.** Source lives on nodes, compiles in the portal, and its
  tests run in the portal — that is how a change is written and how it earns a verdict.
- **Shipping is baked.** The bake compiles those sources once, ahead of time, and the artefact an
  install pulls is a DLL. No install compiles anything at first use, and no install needs the source.

#2193 is about the *runtime* contract; this page is about the *development* contract. If a future
change makes the baked artefact the only representation — deleting the source from the mesh — then
in-mesh test runs lose their subject and the two really do conflict. That has to be decided
deliberately, not discovered.

## The build itself: memex owns it, CI calls it

This is the pillar with the most existing machinery and the largest gap.

**What already exists.** `Build` is a first-class NodeType (`src/MeshWeaver.Graph/Configuration/BuildNodeType.cs`)
with a real coordination protocol — a root at `Admin/Build`, a chunk per unit at
`Admin/Build/{chunkName}`, a `BuildState` of `None → Planning → Building → Ready | Failed | Cancelled`,
and a claim arbiter where **nobody is elected**: candidates register a `BuildClaimRequest` under
their own holder id and the node's own hub grants the earliest pending one, stealing a claim whose
holder has gone. Correctness comes from node state, never from an in-memory gate, and the grant is
taken on a durable lock rather than a hub's mirror. `PreWarm__BuildProtocol` arms it.

So the mesh already knows how to *coordinate* a build. What it does not yet do is *own* one.

**What the gap actually is, counted.** The build is currently performed by **20 scripts under
`.github/scripts/` in core and 27 under `scripts/` in Plugins** — resolve the version, compose the
module set, seal the bundles, compile-check the node repos, verify the image set, decide whether to
publish. Every one of those is logic about the mesh, executing outside it, in bash and python, where
the mesh's own types are invisible and the only test is a CI run.

**The target.** A build is a node whose state machine the portal advances: CI's job is to say *"build
this commit"* and read a verdict back, the way `helm-release.yml` was reduced to calling the operator
rather than knowing about Kubernetes. That is the same move as
[the deployment program](/Doc/Architecture/DeploymentAKS) — *node types drive the process; GitHub Actions call
memex*.

🚨 **The failure mode to avoid is a script that moves house.** Porting `compose-sealed-modules.sh`
into a C# method that a workflow still invokes as a one-shot has changed nothing: the logic is still
outside the mesh's state, still unobservable while it runs, and still untested except by running CI.
The test of whether a step has really moved is whether **its progress and its verdict are readable
on a node while it happens** — which is exactly what `BuildState` already offers and nothing
currently uses.

## The CI process, concretely

The maintainer's shape for a CI job, in four lines:

```yaml
- uses: actions/checkout@v7                                   # 0. checkout git
- run: dotnet tool install -g MeshWeaver.Cli                  # 1. install the CLI
- run: memex build plugin <path> --image <image>              # 2. pull it, run in it, build + test
```

**`memex build plugin <path> --image <image>` is the whole contract.** A workflow says *which plugin
this job is about* and *which image to build it against*; the tool pulls that image, runs the build
inside it, works out what actually changed, closes over everything that depends on it, builds that,
runs its tests, and publishes. Nothing else in the job knows about modules, closures, bundles,
registries or the mesh — and a plugin repo's CI stops being a program about MeshWeaver and becomes
three lines.

The verb shape matters as much as the behaviour: `build` is the command group and `plugin` names
the subject, so the same tool has room for the other subjects CI needs without every repo growing
its own script for each.

### The image is an ARGUMENT, not an ambient

`memex build plugin <path> --image <image>` — **the image to build against is passed in.**

A plugin is built by taking the MeshWeaver image and installing its dependencies as built artifacts
into it ([the four steps](/Doc/Architecture/PluginBuildContract)). Which image that is decides the
whole result, so it belongs in the invocation where a reader can see it, not in an environment
variable resolved somewhere upstream.

🚨 **This is not a preference; it is the thing that failed on 2026-08-30.** The gate named
`Compile every NodeType (vs core)` took its 120 reference assemblies from `MW_IMAGE_DIGEST` — an
image — while advertising `MW_PLATFORM_REF: main`, a label that decided nothing. A contract merged
into core `main` twenty minutes earlier was therefore invisible to it, two NodeTypes failed
`CS0246`, and the natural conclusion from the job's name was *"the type is missing from core"* —
which was false, and would have sent someone to the wrong repository. 76 of 78 NodeTypes compiled
fine against the same set, which is exactly what "the framework in this image predates that merge"
predicts.

**And the CLI pulls the image and runs it.** That is the other half of making the image an argument:
the job does not `docker login`, does not `docker pull`, does not `docker run` with a volume mount
and a wall of flags. It names the image; the tool fetches it and executes the build inside it.

That is what collapses a plugin repo's CI to three lines. Everything the current workflows spend
their length on — logging in to the registry, resolving a digest, mounting `/repo`, wiring the bake
output, passing `--seed`, retrying a login that failed on a TCP reset — is the tool's business, in
one place, testable, rather than copied into every repo's YAML and drifting.

It also puts the registry-transient handling somewhere it can be done once and done properly.
Bounded retry around a registry operation currently exists in four workflows as four separate
shell loops, each added after a different incident.

With the image as an explicit argument:

- **the skew is visible.** `--image meshweaver.azurecr.io/mw-plugin-test:<tag>` in the job is a fact
  a reader can check against the change under test; `MW_IMAGE_DIGEST` in an env block is not.
- **the label cannot lie.** There is no second name for the framework, so no gate can be called
  *"vs core"* while consuming an image.
- **pinning during an incident is an argument change**, not an edit to a workflow under pressure —
  the same property `MW_PLUGINS_REF` was given deliberately in `main-cd.yml`.

### What `build` means by default — both directions, then publish

**By default the command closes over the dependency graph in *both* directions and ends by
publishing:**

1. **Everything the target depends on is built** — the upstream closure, so the target is built
   against the actual current state of its dependencies rather than whatever a registry last
   happened to serve.
2. **Everything that depends on this repo's targets is built** — the downstream closure, **across
   repos**. A change to core builds Plugins, Reinsurance, Education, SocialMedia, Crm and
   Manufacturing, because they are what depends on it.
3. **It ends by publishing a new package version.**

🚨 **"Package" here is a module bundle in the plugin registry, not NuGet.** That is not in tension
with *"no NuGet packages"* above: the platform is never consumed as a NuGet package, and what gets
published at the end of a build is the plugin's own bundle, which is how installs already receive
modules. The one NuGet artefact in the whole picture is the CLI itself
(`dotnet tool install -g MeshWeaver.Cli`), which is the tool, not the product.

### Why the downstream half is the important half

It is tempting to read step 2 as an optimisation. It is the opposite: it is the only step that
closes a hole this fleet has been paying for repeatedly, and it had a live example on the very day
this page was written.

Core merged an added overload — `ObserveCompletion` — which is **source-compatible everywhere the
compiler looks**. Nothing was removed, so no binary-compat gate would fire. But `<see cref>` is the
one place where *adding* a member breaks already-correct code, and three bare crefs in
**MeshWeaver.Plugins** became `CS0419` under `-warnaserror`. Core's CI could not see them; Plugins'
CI builds against the published core, not core's `main`. **Neither side could catch it**, and the
break was found only when core CD — which builds the portal hosts *from* `plugins-repo` — went red
and **delivery stopped for hours**.

A build that closes downstream by default builds the dependents *as part of the change that breaks
them*. That is [#2689](https://github.com/Systemorph/MeshWeaver/issues/2689)'s ask, delivered as a
property of the build rather than as another gate someone has to remember to add.

### Build once, reuse everywhere — three rules

**Maintainer, 2026-08-30:** *"I am speaking about minimalistic and maximally re-usable build."*

1. **We build the image.**
2. **Any further testing is done against this image, no matter where.**
3. **Every package is built and tested exactly once.**

This is not an expensive default that needs justifying — it is the **cheap** one, and the current
arrangement is the expensive one. Closing over dependents does not mean rebuilding the fleet
repeatedly; it means the closure is computed once and **each package in it is built once**, against
the one image, wherever it happens to live.

**The waste it removes, measured in `MeshWeaver.Plugins/.github/workflows/ci.yml`:**

- **14 jobs**, of which **5 check out `Systemorph/MeshWeaver`** and build the framework from source
  through `-p:MeshWeaverRoot=…` — the same core, compiled again per job, in the same run;
- `dotnet build -c Release -warnaserror "$project" -p:MeshWeaverRoot="$root"` appears repeatedly
  across those jobs, so a package's dependencies are rebuilt once per consumer rather than
  installed as artifacts.

Rule 2 is what forbids that: once the image exists, nothing rebuilds what is in it. A repo that
compiles its dependencies *"has not installed them — it has rebuilt somebody else's product, once
per mesh, on every run, and it is testing bytes nobody will ever ship"*
([The Plugin Build Contract](/Doc/Architecture/PluginBuildContract), step 2).

Rule 3 is what makes the bidirectional closure affordable: a package appearing in five consumers'
closures is still built **once**. Without it, "build everything that depends on me" really would be
the expensive thing I first assumed it was — with it, the closure is a *set*, and the cost is the
size of the set, not the number of paths into it.

🚨 **The property that keeps rule 3 honest is the affected-set self-test.** `affected-modules.py`
carries a `--self-test` that proves the computation **can say no**. A set computer that always
answers "everything" satisfies rule 3 trivially and rebuilds the world; one that answers "nothing"
is a gate that tests nothing. Both look identical on a green day. Whatever replaces it must keep
that self-test **and run it in CI**.

## The test shape: no dependencies, not even ours

**Maintainer, 2026-08-30: *"we are about to remove all nuget dependencies — for tests. All repos.
Check plugins how we do it."***

Checked, and the Plugins idiom is stricter than "no xUnit" — **a test carries no dependencies at
all**, measured across all 206 in-mesh test node sets (`using Xunit`: **0** occurrences):

- **Plain C#, throws on failure.** A test case is a `public static void` method; the assertion
  surface is a local four-line helper, per file, owned by the test:

  ```csharp
  private static void Expect(bool condition, string message)
  {
      if (!condition) throw new Exception("CourseCatalogTests failed: " + message);
  }
  ```

  No xUnit, no FluentAssertions — and deliberately not even `MeshWeaver.Reactive.Assertions`. The
  test compiles against exactly what the image's framework provides plus its own type's `Source`,
  so there is nothing to restore, nothing to version, and nothing that can drift against the
  framework under test.

- **A `TestsArea` is the runner.** It enumerates `(name, Action)` cases and renders a pass/fail
  table as the type's `Tests` layout area. Registered with a **literal** `.WithView("Tests", …)`
  in the configuration lambda — literal, because the gate reads that field as *text*, and an area
  registered only inside an extension method reports `tests=skipped` and asserts nothing.

- **The gate is the executor.** It creates a probe instance and runs the area; `tests=ok 7/7` in
  the gate log is the verdict, and the Tests-area ratchet holds the population (a type that skips
  its area is new debt, red).

This resolves the open question stated further down ("deciding what the in-mesh unit-test surface
is" for module tests): the surface exists, it is this, and it has 206 production instances. What
retires with the compiled test projects is therefore not just `MeshWeaver.Fixture` — it is the
whole test-dependency graph: xUnit v3, FluentAssertions, the runner, the `.trx` machinery, and the
restore step that feeds them.

**What is deliberately given up, stated so it is a decision rather than a discovery:** parametrised
theories, per-test isolation of the runner process, IDE test-explorer integration, and `.trx`
artifacts. The gate log and the rendered pass/fail table are the record instead. For the fleet's
test population — behaviour checks against a live mesh — that trade has already been paid 206
times without a request for any of the four back.

## What retires: the compiled test scaffolding

**Maintainer, 2026-08-30: *"we want to discontinue fixture project overall."***

That is the concrete consequence of the three rules, not a separate initiative. `MeshWeaver.Fixture`
and the two `TestBase` projects exist **only** to let compiled xUnit projects stand a mesh up
in-process. In-mesh tests do not need one: the portal is already running, and the gate creates a
`GateProbe` instance to execute a NodeType's `Tests` area on. When the tests move, the scaffolding
has no callers left.

**The footprint, measured:**

| | `.csproj` referencing it |
|---|---|
| core | **26** |
| Plugins | **39** |
| **total** | **65** |

`test/MeshWeaver.Fixture/` is `BaseFixture`, `HubTestBase`, `ServiceSetup`, `HubFactAttribute`,
`AutoTestLoggingAttribute`, `FreshThread`, `FaultRecordBudget`, and the `IMeshQuery` /
`IStorageAdapter` test extensions — all of it machinery for constructing and tearing down a mesh
around a `[Fact]`. Alongside it sit `MeshWeaver.Hosting.Monolith.TestBase` (31 of the Plugins
references) and `MeshWeaver.Hosting.Orleans.TestBase` — and behind all of it, the NuGet test
dependencies themselves (xUnit v3, FluentAssertions, the runner), which retire with it per the
no-test-dependencies shape above.

🚨 **This is the cross-repo coupling, and it dissolves rather than being ported.** Those 39 Plugins
projects reach into a core checkout through `$(MeshWeaverRoot)` *because* their tests are compiled.
An in-mesh test has no `ProjectReference` to anything — its source lives on a node and compiles in
the portal — so retiring the fixture removes the reason the two repos are wired together at build
time at all.

**The order matters, and it is the opposite of tempting.** Do not delete the fixture and then find
homes for its 65 dependents. Retire it *behind* the tests:

1. move a project's tests to an in-mesh `Tests` area and prove they fail for the same reasons —
   mutation-proved, not observed green;
2. delete the compiled project;
3. when a fixture file has no callers left, delete it;
4. the project disappears when its last file does.

🚨 **A compiled test project deleted before its in-mesh replacement exists is coverage lost in
silence** — and this repo has spent the whole of 2026-08-30 on guards, assertions and classifiers
that passed while checking nothing. The fixture's retirement is measured by *tests that still fail
when the code breaks*, never by the reference count reaching zero.

**And some of the 65 will not move**, for the same reasons modules do not: a test that exercises
static assets, native interop, or a portal host has nothing to hang a `Tests` area on. Those want an
explicit exemption recorded here, not a quiet `ProjectReference` that survives because nobody
noticed it.

## What blocks "any plugin" today

1. **44 module test projects** with no in-mesh equivalent. Each needs its module's source expressible
   as node source, or an explicit exemption on one of the grounds above.
2. **The cross-repo `ProjectReference` web** — 65 projects reaching for `MeshWeaver.Fixture` and the
   `TestBase` pair across `$(MeshWeaverRoot)`. See *What retires* above: it dissolves with the
   fixture rather than being ported.
3. **No in-mesh unit-test surface for non-NodeType code.** The `Tests` area contract is defined per
   NodeType. A module has no node to hang a `Tests` area on, so the first real piece of work is
   deciding what that surface is.
4. **47 external build scripts** (20 core, 27 Plugins) hold logic the mesh should own, and the
   `Build` node protocol that would hold it is armed by a flag (`PreWarm__BuildProtocol`) that no
   environment sets — the same shape as `PreWarm__GateReadiness`, and it will need the same care
   about paired settings before anyone turns it on.

## What NOT to do

🚨 **Do not delete a compiled test project without an in-mesh test that fails for the same reason.**
A module whose tests moved to a `Tests` area that never runs is worse than one with a `dotnet test`
project — the repo has spent enough on guards that check nothing.

🚨 **Do not "achieve" no-NuGet by pinning a package.** There are zero `MeshWeaver.*` package
references today; the direction is to keep it that way, not to replace ProjectReferences with pins.

## Building a COMPILED project without an SDK — `memex build project` (2026-08-31)

> *"The platform builds dll completely without any external dotnet kit or nuget."* — maintainer,
> 2026-08-30

The sections above are about NodeTypes, whose sources already compile in the portal. This one is
about the other half — the ~54 compiled projects under `MeshWeaver.Plugins/src`, which until now
needed a .NET SDK, a NuGet restore and a platform SOURCE checkout to build at all.

`memex build project <csproj|dir> --image <image>` builds one of them with **no SDK and no
restore**: the `.csproj` is evaluated without MSBuild, and every reference is resolved from the
image's own `/app` plus the shared frameworks installed in it. The verb is a thin trip into the
container; the work is `mw-plugin-test build-project`, which is already in the image and shares the
platform's own `EmitPipeline` for the emit.

### Why the container is the reference set

A module does not run against the platform's SOURCE and it does not run against a feed: it is loaded
into the platform IMAGE and bound by the assemblies in there. So the honest reference set is what
that image ships, and the honest package versions are the ones its `.deps.json` records — what
SHIPPED, not what a source tree would resolve today. This is the same rule
`MeshWeaver.Plugins/scripts/container-refs.py` derives for the MSBuild path, ported to C# and read
from `/app` directly instead of from an extracted image.

**A `PackageReference` the image supplies resolves to the image's assembly. One it does not supply
is an ADDITIONAL library** — additional to the platform — and it is reported by name and refused,
never skipped. `--extra-refs <dir>` is the one way to supply one, so what a build needed beyond the
platform is always visible in the command that ran it.

### Nothing is dropped in silence

The evaluator FAILS the load on any construct it cannot reproduce, naming the construct and the file
— an unknown element or item type, a `Condition` outside its grammar, an `<Import>` of a missing
file, a `<Target>` (which it cannot execute), an `<EmbeddedResource>` (which it cannot embed).
`--accept <construct>` acknowledges one deliberately. The reason is that the alternative is worse
than no build: a silently dropped `Nullable`, `NoWarn` or `DefineConstants` produces a green build
that is *not the build the SDK would have produced*, and nothing downstream can tell.

The same rule governs the reference set: an unreadable `/app`, a missing or ambiguous `.deps.json`,
or MeshWeaver assemblies that disagree on their binding identity (MeshWeaver#143's failure, caught in
the image instead of at run time) each stop the run RED.

### The diagnostic standard is the SDK's

Nullable reference analysis follows the project; `DocumentationMode.Diagnose` is on ALWAYS, so
doc-QUALITY defects surface (CS1574 unresolved cref, CS0419 ambiguous cref, CS1570 malformed XML) —
while the doc-COMPLETENESS family (CS1591/CS1573/CS1712) is suppressed exactly when the project did
not ask for a doc file, which is when csc itself would not raise it. The SDK's own default
`NoWarn` (`1701;1702`) is seeded before any `Directory.Build.props` appends to `$(NoWarn)`. Warnings
fail the build by default; `--allow-warnings` is the deliberate opt-out.

### Measured, 2026-08-31 — 12 of 54

Against `memex-portal-ai@sha256:15c49ee…`, over every non-test project in `MeshWeaver.Plugins/src`,
with `--accept targets --accept embedded-resource` and ClosedXML + CsvHelper as `--extra-refs`:

| | count | why |
|---|---|---|
| **green** | **12** | incl. `MeshWeaver.Import` — 90 source files, 0 warnings under warnings-as-errors |
| Razor/Blazor (CS0115) | 15 | *fixed 2026-08-31 — see below* |
| source generators (CS8795) | 15 | `[GeneratedRegex]` / `[LoggerMessage]` / `[JsonSerializable]` |
| gRPC `<Protobuf>` | 3 | protoc codegen is a build task, not a compile |
| additional libraries | 5 | Snowflake.Data, Microsoft.Data.Sqlite, Azure.Cosmos, … — supply with `--extra-refs` |
| portal hosts | 3 | an `<Import>` above the mount; Aspire's `<Sdk>` ELEMENT |

## The container compiles Razor (2026-08-31)

Razor was the biggest single category above, and it was one missing file rather than a missing
feature: **Razor compilation in the .NET SDK is a Roslyn source generator**
(`Microsoft.CodeAnalysis.Razor.Compiler`) that turns each `.razor` into the partial class carrying
its generated `BuildRenderTree` override. A runtime image ships no SDK, so without the generator
every component compiled to a class with nothing to override — a wall of CS0115 that reads exactly
like broken source.

The image now carries it, in `razor-generators/` beside the builder, and `build-project` finds and
runs it automatically for any project whose `Sdk` processes Razor items.

### The dependency closure, measured

Exactly **two** assemblies, established by reading the compiler's own assembly references and then
proving it by deleting one:

| assembly | why |
|---|---|
| `Microsoft.CodeAnalysis.Razor.Compiler` | carries `RazorSourceGenerator`, the `[Generator]` type |
| `Microsoft.AspNetCore.Razor.Utilities.Shared` | its one private dependency — with it absent the compiler loads, its types enumerate, and every call into it throws |

Everything else it references — `netstandard`, `Microsoft.CodeAnalysis(.CSharp)`,
`System.Collections.Immutable`, `System.Memory`, `System.Buffers` — binds to the assemblies the
image already has. 🚨 **`Microsoft.Extensions.ObjectPool` is NOT needed**, though an older Razor
compiler build referenced it; the belief that it was is exactly why the closure was measured rather
than assumed.

### Two traps, both of which fail at run time and nowhere else

🚨 **The generator is built against the SDK's Roslyn, not the image's.** SDK 10.0.400's copy
references `Microsoft.CodeAnalysis` **5.9.0.0** while the image carries this repo's pin, **5.6.0**.
The default load context binds by name *and refuses a lower version*, so a plain `Assembly.LoadFrom`
fails — and a generator loader that treats "cannot load" as "not a generator" then produces a build
indistinguishable from one where nobody asked for Razor. The generator is therefore loaded into a
context that binds every assembly the HOST already has to the host's copy, version ignored. That is
also what keeps `ISourceGenerator` ONE type: a second Roslyn in that context would give the
generator a different interface than the driver expects.

🚨 **The generator is ReadyToRun-compiled for the SDK's own RID.** The same SDK 10.0.400 file carries
PE machine `0xFD1D` on linux-x64, `0xD11D` on linux-arm64 and `0xEC20` on osx-arm64 (the target
machine XOR'd with the operating system's R2R marker), and the wrong one throws
`BadImageFormatException`. `mw-plugin-test` publishes BOTH architectures from ONE x64 build host, so
copying the build machine's SDK once would have shipped an arm64 image that cannot compile a single
Blazor project. CD stages one directory per RID (`razor-generators/<rid>/`, the arm64 copy read out
of the dotnet/sdk image of the same SDK version with `docker create` + `docker cp`, so no emulation
is involved), and the builder picks the directory for the RID it is running on.

### What it refuses, and why

- **A project with Razor files and no generator.** One named failure that says what the CS0115 wall
  would have been, not the wall.
- **A generator that ran and emitted nothing.** Not a compile error — a generator that did not
  recognise its input, which otherwise fails four steps later as CS0115.
- **CSS isolation (`*.razor.css`).** The `b-…` scope identifier comes from the SDK's
  `ComputeCssScope`/`ApplyCssScopes` tasks, and this builder runs no MSBuild task, so the components
  would compile without their scope attributes. `--accept razor-css-scope` builds them anyway (the
  assembly is valid; only the isolated stylesheet stops applying). Reproducing the scope hash from
  memory is the guess this evaluator exists to avoid.
- **`.razor` under a project whose `Sdk` does not process Razor.** The SDK's build ignores them too,
  so the outcome matches — but it is stated rather than skipped. `--accept razor-not-compiled`.

### Measured, 2026-08-31 — 10 of the 11 Razor projects

Every `Microsoft.NET.Sdk.Razor` project in `MeshWeaver.Plugins/src`, built against
`memex-portal-ai@sha256:6f38db08…` with the builder mounted, `--accept targets --accept
razor-css-scope --accept embedded-resource`:

| | count | |
|---|---|---|
| **green, no extra help** | **7** | Blazor (31 `.cs` + **42 `.razor`**), Blazor.EntityViews, Blazor.Graph, Blazor.Analysis, Blazor.OpenStreetMap, Blazor.GoogleMaps, Blazor.AppleMaps |
| green with `--generators` (the SDK's regex generator) | +2 | Blazor.Views, Blazor.Portal — they were blocked by `[GeneratedRegex]` in `MeshWeaver.Markdown.Collaboration`, not by Razor |
| green with `--extra-refs` | +1 | Blazor.Radzen — `Radzen.Blazor` is an additional library |
| still red | 1 | `Memex.Portal.Gui` — a transitive `MeshWeaver.Hosting.Grpc` carries `<Protobuf>`; protoc is a build task, not a compile |

**Razor is no longer what blocks any of them.** The two remaining categories are the pre-existing
ones — SDK source generators and `<Protobuf>`.


## The container runs the SDK and Orleans generators (2026-08-31)

With Razor closed, **source generators were the largest remaining category** — and the same
diagnosis applies twice over. A generator is not part of the runtime: `[GeneratedRegex]`'s ships in
the **.NET SDK's targeting pack** (`Microsoft.NETCore.App.Ref/<version>/analyzers/dotnet/cs`, applied
implicitly to every project), and Orleans' ships in the **`Microsoft.Orleans.CodeGenerator` NuGet
package** (applied to projects that reference `Microsoft.Orleans.Sdk`). A runtime image has neither,
so a project using one compiled to an assembly missing exactly what the generator would have written.

The image now stages both in `generators/` beside the builder, and `build-project` runs them.

### The two failures are NOT the same shape, and that decides the design

| | what a missing generator looks like |
|---|---|
| **`[GeneratedRegex]`** | a wall of **CS8795** *"partial method must have an implementation part"* — loud, but it reads like broken source |
| **Orleans** | **nothing.** The project compiles GREEN and the assembly simply has no serializers, copiers or grain proxies in it. The silo throws at grain activation, days later, with nothing pointing back at the build |

So Orleans gets a rule regex does not need: a project referencing a package in
`StagedGenerators.CodegenRequiredPackages` whose generator is **not** staged **fails the build**,
naming what the green build would have hidden. `--accept generators-missing` is the recorded escape
and really does emit the incomplete assembly.

### Which project gets which generator — the SDK's own rule

- **`generators/sdk/`** applies to **every** project, exactly as the targeting pack's analyzers do.
- **`generators/packages/<package id>/`** applies to a project whose `PackageReference` set names
  that id, exactly as a NuGet analyzer does.

🚨 The rule is deliberately **not** *"does the compilation resolve an Orleans type"*. This builder
hands every project the container's **whole** reference set — 209 assemblies in the portal image,
Orleans among them — so a reference-shaped test would run Orleans codegen over every project in the
repo. Assemblies are also de-duplicated by file name across activations: Orleans' codegen is
reachable through more than one package id, and a generator that runs twice emits every type twice
(CS0101, reported against code nobody wrote).

### The staged closure is ONE assembly each, and both are architecture-neutral

Read from the AssemblyRef tables, then checked against the portal image's 209 libraries:

| generator | references | staged |
|---|---|---|
| `System.Text.RegularExpressions.Generator` | netstandard, Roslyn (+CSharp), Immutable, Memory, Buffers, **Workspaces**, **System.Composition.AttributedModel**, CompilerServices.Unsafe | itself only |
| `Orleans.CodeGenerator` | netstandard, Roslyn (+CSharp), Immutable, Memory, Buffers | itself only |

The image ships every one of those — **including Workspaces and Composition**, which are what an
IDE-flavoured analyzer usually drags in — so neither needs a private dependency beside it, unlike
the Razor compiler. `Orleans.Analyzers` is deliberately **not** staged: it carries diagnostics and
code fixes, not a `[Generator]`, and this builder runs no analyzers.

🚨 **And neither is ReadyToRun.** Measured PE machine **`0x014C` (MSIL)** on both, inside the
`linux/amd64` *and* `linux/arm64` `dotnet/sdk:10.0.400` images — against `0xFD1D`/`0xD11D`/`0xEC20`
for the Razor compiler, which had to be staged per RID. **One copy therefore serves both image
architectures and CD stages nothing.** That is a measurement, not an assumption:
`StagedGeneratorsTest` asserts the `0x014C`, so an SDK that starts crossgenning its analyzers turns
a PR red instead of shipping an arm64 image that silently drops every generated regex. The search
path still probes `<rid>/` first, so re-splitting later is a staging change, not a code change.

### An analyzer-only package is "supplied" when its generator is staged

`Microsoft.Orleans.Sdk` is **absent from the portal image's `deps.json`** (measured: 209 libraries,
`Microsoft.Orleans.Core` and its siblings among them, no `Orleans.Sdk`) because the publish prunes a
package with no runtime assets. Reading that as *"the container does not supply it"* would refuse the
one project in the fleet that authors grains, for a file that has never existed. What supplying such
a package MEANS is that its generator is staged — so that is the test.

### Nothing is skipped in silence — including `--generators`

A staged generator that fails to LOAD **fails the build by name**; a directory of assemblies with no
`[Generator]` in it fails by name. And **`--generators` now runs through the same loud loader.** It
used to route through the node-compile discovery (`SourceGeneratorLoader`), which reads a failed load
as *"not a generator"*, logs it at **debug**, and returns the compilation UNCHANGED — so an operator
who supplied a generator built against a different Roslyn got a green build that ran none of it.
That is trap 1 from the Razor work, and it applied to the operator's own path too.

Both paths now share the host-first `AssemblyLoadContext` (`GeneratorLoader`) rather than a copy of
it.

### What it does NOT do, and what each would take

- **Source generators OTHER than the staged ones.** `--generators <dir|dll>` loads them when
  supplied, and a supplied generator that does not load now fails the build rather than running
  silently as nothing.
- **Embedded resources**, **`<Protobuf>`**, **MSBuild `<Target>`s** and **`<Sdk>` elements** — each a
  named failure with an `--accept` where one is meaningful.

**The boundary is the point.** This is not an MSBuild reimplementation and must not become one: it
evaluates the subset a library project under this organisation's `Directory.Build.props` actually
uses, and everything outside that subset is visible in the output as a refusal rather than
discovered later as a wrong answer.
