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

### The cost, stated

A default that builds every dependent across six repos is expensive, and pretending otherwise would
be dishonest. Two things make it tractable, and both already exist:

- **the affected-set computation** — the closure is over what *changed*, not over everything; and
- **the publication-derived baseline** — the diff is against what is actually published, so a
  re-run or a raced run does not re-do the world.

Which is why the `--self-test` that *proves the computation can say no* matters more here than
anywhere else: with a bidirectional default, an affected-set computer that always answers
"everything" turns every commit into a fleet-wide rebuild, and nothing about a green run would tell
you.

**Most of this is already written — it is just split three ways and one of the three is python.**

| piece | where it lives today | state |
|---|---|---|
| the tool CI would install | `src/MeshWeaver.Cli` — `PackAsTool`, `ToolCommandName=memex`, `PackageId=MeshWeaver.Cli`, `AssemblyName=memex` | **exists**, already documented as `dotnet tool install -g MeshWeaver.Cli` |
| module pack/fetch + dependency closure | `src/MeshWeaver.Plugin.Build` — `ToolCommandName=meshweaver-plugin-build`, with `DepsClosure.cs`, `ModulePackCommand.cs`, `ModuleFetchCommand.cs` | **exists**, a second tool |
| "what changed and everything that depends on it" | `MeshWeaver.Plugins/scripts/affected-modules.py` + `project-closure.py` | **python**, outside the mesh |

`affected-modules.py` already computes exactly the thing step 2 needs, and its own docstring states
the contract better than a summary would: *map every changed file to its owning module, close over
all transitive DEPENDENTS (a change to X invalidates everything that compiles against X), then add
the transitive DEPENDENCIES of that closure* — because the gate boots a fresh mesh and a gated
module whose dependencies are absent fails with `NodeType 'X' is not registered` and `shared=@…`
sources that resolve to nothing.

It also already has **two consumers with one answer** (the gate narrows its mount; the bake narrows
what it republishes), and it takes its baseline from the **publication** — the `source-commit.txt`
beside the sealed bundles — rather than from `github.event.before`, so a run that lost a race, was
re-run, or followed a skipped run still diffs against what is actually published.

**So the work is not "invent the affected-set logic". It is:**

1. **One tool, not two.** `memex` is the thing CI installs; `meshweaver-plugin-build`'s pack/fetch
   and `DepsClosure` belong behind it as verbs rather than as a second `dotnet tool install`.
2. **Move the closure out of python and into that tool**, keeping the two-consumers-one-answer
   property and the publication-derived baseline — both are load-bearing and neither is obvious.
3. **Give it the verb**: `memex build plugin <path>`, taking the plugin's path as its argument and
   deciding the rest. A repo's job is then literally the three lines above.

🚨 **`affected-modules.py` has a `--self-test` that proves it can say NO.** Whatever replaces it must
keep that, and it must be run in CI rather than existing: an affected-set computer that always
answers "everything" is indistinguishable from a correct one on a green day, and an affected-set
computer that answers "nothing" is a gate that tests nothing. That property is the single most
important thing to carry across, and it is the easiest to lose in a port.

## What blocks "any plugin" today

1. **44 module test projects** with no in-mesh equivalent. Each needs its module's source expressible
   as node source, or an explicit exemption on one of the grounds above.
2. **The cross-repo `ProjectReference` web.** 31 `.csproj` files in Plugins reference core's
   `MeshWeaver.Hosting.Monolith.TestBase` through `$(MeshWeaverRoot)`. That coupling exists *because*
   the tests are compiled; in-mesh tests need no such reference, so it dissolves rather than being
   ported.
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
