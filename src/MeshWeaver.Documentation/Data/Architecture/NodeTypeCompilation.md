---
nodeType: Markdown
name: NodeType Compilation & Releases
category: Architecture
description: The full lifecycle of a dynamic NodeType â€” how compilation is triggered, how to watch progress, how to cancel, where releases live, how to pin an instance to a fixed release, and why a build is recompiled (self-healing verify-before-skip + framework-version freezing).
icon: /static/NodeTypeIcons/code.svg
---

# NodeType Compilation & Releases

A **dynamic NodeType** carries its behaviour as C# source (`Source/*.cs`) plus a
`configuration` lambda. That source is compiled **at runtime, on demand** â€” you
never redeploy the portal to add or change a NodeType. This document is the
canonical reference for the *runtime* side of that story: triggering a compile,
watching it, cancelling it, finding releases, pinning to one, and the rules that
decide when a NodeType is recompiled.

For *authoring* a NodeType (folder layout, content record, layout areas, CSV
data) see [Creating Node Types](../DataMesh/CreatingNodeTypes). For the original
design rationale see the [NodeType Release Redesign](Postmortems/NodeTypeReleaseRedesign)
postmortem.

## The model in one picture

```
NodeType MeshNode  â”€â”€(compile)â”€â”€â–º  Release MeshNode            â”€â”€â–º  compiled DLL
{ns}/{Type}                        {ns}/{Type}/Release/{ver}        (on disk / blob)
  Content: NodeTypeDefinition        Content: NodeTypeRelease
    Configuration   (lambda src)       Code, HubConfiguration
    CompilationStatus                  FrameworkVersion
    LatestReleasePath  â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â–º   AssemblyPath
    RequestedReleasePath (pin)         Status (Succeeded/Failed)
    CompiledFrameworkVersion           CompilationActivityPath â”€â”€â–º Activity MeshNode
    CompiledSources {pathâ†’version}                                  {ns}/{Type}/_Activity/compile-{id}
```

* The **NodeType MeshNode** is the editable definition.
* A **Release MeshNode** (`{nodeTypePath}/Release/{version}`, content
  [`NodeTypeRelease`](xref:MeshWeaver.Graph.Configuration.NodeTypeRelease)) is an
  **immutable** snapshot of one successful (or failed) compile â€” its source, the
  framework version it was built against, and the path to the compiled DLL. Old
  releases are never deleted: instances already loaded on a release keep running
  on it.
* A **Compilation Activity** (`{nodeTypePath}/_Activity/compile-{id}`, content
  `ActivityLog`) is the live, observable progress + diagnostics channel for one
  compile run.

## Triggering a compile

There are two entry points; both end up in `NodeTypeCompilationHelpers.RunCompile`.

### 1. Automatic â€” the per-NodeType hub kickoff

When a per-NodeType hub activates, `NodeTypeCompilationHelpers.InstallCompileWatcher`
runs two subscriptions on the hub's own MeshNode stream:

* **kickoff** â€” on first sight of the `NodeTypeDefinition`, if the NodeType does
  **not** already have a usable build (see *When is a NodeType recompiled?*
  below) it flips `CompilationStatus = Pending` on its own MeshNode.
* **watcher** â€” whenever `CompilationStatus` becomes `Pending`, it creates a
  compile-activity MeshNode and posts a `RunCompileRequest` to that activity's
  hub.

This is what makes a NodeType "just work" the first time an instance of it is
created, after a `Source/*.cs` edit, or after a framework redeploy â€” no operator
action required.

### 2. Explicit â€” Create Release

The GUI's **Create Release** button posts a `CreateReleaseRequest` to the
NodeType hub; `MeshDataSource.HandleCreateRelease` calls the same `RunCompile`.
Use this to capture a named release with author-written `ReleaseNotes`.

### Re-triggering after a source edit

`RunCompile` records a `CompiledSources` snapshot â€” `{sourceNodePath â†’ version}`
for every `Code`/`Test` node that fed the compile. When you edit a `Source/*.cs`
node its version bumps; the mismatch against the snapshot marks the NodeType
dirty and a recompile is triggered. (Editing flips the NodeType back through
`Pending`; you do not invalidate a cache by hand.)

## Watching compile progress

Every compile runs on its **Activity hub** at
`{nodeTypePath}/_Activity/compile-{id}` â€” created by
`NodeTypeCompilationActivity.Start`. Subscribe to it as a normal MeshNode
stream:

```csharp
workspace.GetMeshNodeStream(activityPath)
    .Select(n => n.Content as ActivityLog)
    .Where(log => log is not null)
    .Subscribe(log =>
    {
        // log.Status   : Running â†’ Succeeded / Failed / Cancelled
        // log.Messages : streamed Roslyn diagnostics + progress lines
    });
```

The activity path is also surfaced on the NodeType itself
(`NodeTypeDefinition.LastCompilationActivityPath`) and on each
`NodeTypeRelease.CompilationActivityPath`, so you can always drill from a
release â€” succeeded *or* failed â€” back into its Roslyn output.

The NodeType's own `NodeTypeDefinition.CompilationStatus` reflects the terminal
outcome: `Compiling` while in flight, then `Ok` or `Error` (with
`CompilationError` carrying the formatted diagnostics).

## Cancelling a compile

Compilation is an Activity, so it cancels through the **Activity Control Plane**
(see [ActivityControlPlane](ActivityControlPlane)) â€” you patch the activity's
`RequestedStatus`, you never post a bespoke cancel message:

```csharp
hub.CancelActivity(activityPath);
```

The activity hub's control-plane watcher sees the patch and tears the compile
down; the activity (and the NodeType) settle to `Cancelled` / the previous
status.

## Where releases live

Releases are MeshNodes at `{nodeTypePath}/Release/{version}`, content
[`NodeTypeRelease`](xref:MeshWeaver.Graph.Configuration.NodeTypeRelease):

* `Version` â€” `{yyyyMMddHHmmss}-{hash}` by default (chronologically sortable),
  or an explicit label supplied at Create-Release time.
* `Code`, `HubConfiguration`, `ContentCollections` â€” the exact inputs that were
  compiled.
* `FrameworkVersion` â€” the MeshWeaver version this release was built against.
* `AssemblyPath` / `PdbPath` â€” the compiled DLL on disk (per-`(NodeType, Version)`,
  stable; never deleted while any ALC may still hold it).
* `Status` â€” `Succeeded` (loadable, candidate for "active release") or `Failed`
  (kept only as history; the previous succeeded release stays active).

`NodeTypeDefinition.LatestReleasePath` always points at the most recent release;
the release history is the set of `Release/*` children.

## Pinning an instance to a fixed release

By default every instance hub of a NodeType binds to `LatestReleasePath` â€” a new
release moves them forward. To freeze a NodeType (and all its instances) on a
specific historical build, set `NodeTypeDefinition.RequestedReleasePath` to that
`Release/{version}` path.

* While `RequestedReleasePath` is set, instance hubs resolve to **that** release,
  not `LatestReleasePath`.
* Creating a new release updates `LatestReleasePath` but **does not** touch
  `RequestedReleasePath` â€” pinned NodeTypes stay put until you clear or move the
  pin deliberately.

This is the supported way to "program against a fixed release": pin the
NodeType, develop/compile freely, and unpin (or re-point) when you're ready to
adopt the new build.

## When is a NodeType recompiled? â€” verify-before-skip

The kickoff does **not** trust a bare `CompilationStatus == Ok`. `CompilationStatus`,
`MeshNode.AssemblyLocation` and `CompiledFrameworkVersion` are all persisted into
the NodeType MeshNode's JSON â€” so a stale `Ok` can outlive the assembly that
produced it. `NodeTypeCompilationHelpers.HasUsableBuild` is the gate: a compile
is skipped **only** when *all* of the following hold â€”

1. `CompilationStatus == Ok`,
2. `MeshNode.AssemblyLocation` points at a DLL that **still exists on disk**, and
3. `CompiledFrameworkVersion` equals the **current** framework version
   (`NodeTypeCompilationHelpers.FrameworkVersion`).

Anything else â†’ recompile. This makes a cold hub start **self-healing** against:

| Situation | Why the bare `Ok` lies | Caught by |
|---|---|---|
| Seed-data pollution â€” a prior run stamped `Ok` into sample/seed JSON | the DLL was a per-process temp artefact | rule 2 |
| Cleaned-up `.mesh-cache` / temp DLL | file deleted since | rule 2 |
| Cross-machine checkout / fresh CI agent | the DLL never existed here | rule 2 |
| **MeshWeaver redeployed at a new version** | the cached DLL bound against the *old* framework assemblies (ABI-stale) | rule 3 |

### Framework-version freezing

A compiled NodeType DLL references the MeshWeaver framework assemblies present
at compile time. When MeshWeaver is **redeployed at a new version**, those
assemblies change and the cached DLL may be ABI-incompatible â€” so a release is
only usable while the framework version matches.

`RunCompile` stamps `NodeTypeDefinition.CompiledFrameworkVersion` with
`NodeTypeCompilationHelpers.FrameworkVersion` on every success. That value is:

* **Deployed builds** â€” the semver baked into `AssemblyInformationalVersion` by
  the NuGet pack process (e.g. `3.0.0-preview2`). It is identical on every
  server running the same deployed build â€” a file write-time would differ per
  machine and is therefore *not* used.
* **Un-packed dev builds** â€” the version stays the frozen default (`1.0.0`)
  across every local `dotnet build`, so the `MeshWeaver.Graph` assembly's
  last-write time is folded in (`1.0.0+{timestamp}`). On the single dev machine
  that is "frozen per build" â€” stable within a run, changes on rebuild â€” exactly
  the dev-iteration signal we want.

On a framework-version mismatch the NodeType recompiles, which **mints a new
release** for the new framework. The old release is left intact as history so
instances still loaded on it keep running until they cycle.

## Quick reference

| I want toâ€¦ | Do this |
|---|---|
| Compile a NodeType for the first time | nothing â€” activating any instance hub kicks it automatically |
| Force a recompile | edit a `Source/*.cs` node, or click **Create Release** |
| Capture a named, annotated release | **Create Release** (sets `ReleaseNotes`) |
| Watch a compile | subscribe to `{nodeTypePath}/_Activity/compile-{id}` (`ActivityLog`) |
| Read diagnostics of a failed compile | `NodeTypeRelease.CompilationActivityPath` â†’ that Activity's `Messages` |
| Cancel a running compile | patch the activity's `RequestedStatus = Cancelled` |
| List releases | the `Release/*` children of the NodeType |
| Find the current release | `NodeTypeDefinition.LatestReleasePath` |
| Pin instances to a fixed release | set `NodeTypeDefinition.RequestedReleasePath` |
| Understand why it recompiled | `HasUsableBuild` failed rule 2 (assembly gone) or rule 3 (framework changed) |
