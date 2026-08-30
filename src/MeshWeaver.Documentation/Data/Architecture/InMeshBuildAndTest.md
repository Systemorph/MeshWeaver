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
