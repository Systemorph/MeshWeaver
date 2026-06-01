---
nodeType: Markdown
name: NodeType Compilation & Releases
category: Architecture
description: The full lifecycle of a dynamic NodeType — compilation triggers, progress observation, cancellation, release storage, instance pinning, and the verify-before-skip rules that make cold starts self-healing.
icon: /static/NodeTypeIcons/code.svg
---

# NodeType Compilation & Releases

A **dynamic NodeType** carries its behaviour as C# source (`Source/*.cs`) plus a
`configuration` lambda — and that source is compiled **at runtime, on demand**.
You never redeploy the portal to add or change a NodeType. This page is the
canonical reference for the *runtime* side of that story: what triggers a
compile, how to watch or cancel it, where releases are stored, how to pin an
instance to a specific build, and the rules that decide when a NodeType must be
recompiled.

> For *authoring* a NodeType (folder layout, content record, layout areas, CSV
> data) see [Creating Node Types](../DataMesh/CreatingNodeTypes). For the design
> rationale behind the release model see the
> [NodeType Release Redesign](Postmortems/NodeTypeReleaseRedesign) postmortem.

---

## The model in one picture

```
NodeType MeshNode  ──(compile)──►  Release MeshNode            ──►  compiled DLL
{ns}/{Type}                        {ns}/{Type}/Release/{ver}        (on disk / blob)
  Content: NodeTypeDefinition        Content: NodeTypeRelease
    Configuration   (lambda src)       Code, HubConfiguration
    CompilationStatus                  FrameworkVersion
    LatestReleasePath  ────────────►   AssemblyPath
    RequestedReleasePath (pin)         Status (Succeeded/Failed)
    CompiledFrameworkVersion           CompilationActivityPath ──► Activity MeshNode
    CompiledSources {path→version}                                  {ns}/{Type}/_Activity/compile-{id}
```

Three kinds of MeshNode collaborate here:

| MeshNode | Role |
|---|---|
| **NodeType** (`{ns}/{Type}`) | The editable definition — source code, configuration, and compilation state. |
| **Release** (`{nodeTypePath}/Release/{ver}`) | An immutable snapshot of one compile run. Old releases are never deleted; instances already loaded on a release keep running on it. |
| **Compilation Activity** (`{nodeTypePath}/_Activity/compile-{id}`) | The live, observable progress and diagnostics channel for a single compile. |

---

## Triggering a compile

All compile paths converge on `NodeTypeCompilationHelpers.RunCompile`.

### Automatic — the per-NodeType hub kickoff

When a per-NodeType hub activates, `NodeTypeCompilationHelpers.InstallCompileWatcher`
registers two subscriptions on the hub's own MeshNode stream:

- **Kickoff** — on first sight of the `NodeTypeDefinition`, if the NodeType does
  *not* already have a usable build (see [When is a NodeType recompiled?](#when-is-a-nodetype-recompiled--verify-before-skip)
  below) it flips `CompilationStatus = Pending` on its own MeshNode.
- **Watcher** — whenever `CompilationStatus` becomes `Pending`, it creates a
  compile-activity MeshNode and posts a `RunCompileRequest` to that activity's hub.

This is what makes a NodeType "just work" the first time an instance is created,
after a `Source/*.cs` edit, or after a framework redeploy — no operator action
required.

### Explicit — Create Release

The GUI's **Create Release** button posts a `CreateReleaseRequest` to the NodeType
hub; `MeshDataSource.HandleCreateRelease` calls the same `RunCompile`. Use this to
capture a named release with author-written `ReleaseNotes`.

### Re-triggering after a source edit

`RunCompile` records a `CompiledSources` snapshot — `{sourceNodePath → version}`
for every `Code`/`Test` node that fed the compile. When you edit a `Source/*.cs`
node, its version bumps. The mismatch against the snapshot marks the NodeType
dirty and triggers a recompile automatically — you never invalidate a cache by
hand.

---

## Watching compile progress

Every compile runs on its **Activity hub** at `{nodeTypePath}/_Activity/compile-{id}`,
created by `NodeTypeCompilationActivity.Start`. Subscribe to it as a normal MeshNode
stream:

```csharp
workspace.GetMeshNodeStream(activityPath)
    .Select(n => n.Content as ActivityLog)
    .Where(log => log is not null)
    .Subscribe(log =>
    {
        // log.Status   : Running → Succeeded / Failed / Cancelled
        // log.Messages : streamed Roslyn diagnostics + progress lines
    });
```

The activity path is surfaced in two places:

- `NodeTypeDefinition.LastCompilationActivityPath` — on the NodeType itself.
- `NodeTypeRelease.CompilationActivityPath` — on each release, succeeded *or* failed.

This means you can always drill from a release back into its full Roslyn output,
regardless of whether it compiled successfully.

The NodeType's own `NodeTypeDefinition.CompilationStatus` reflects the terminal
outcome: `Compiling` while in flight, then `Ok` or `Error` (with
`CompilationError` carrying the formatted diagnostics).

---

## Cancelling a compile

Compilation is an Activity, so it cancels through the **Activity Control Plane**
(see [ActivityControlPlane](ActivityControlPlane)) — patch the activity's
`RequestedStatus`, never post a bespoke cancel message:

```csharp
hub.CancelActivity(activityPath);
```

The activity hub's control-plane watcher sees the patch and tears the compile
down; the activity and the NodeType settle to `Cancelled` / the previous status.

---

## Where releases live

Releases are MeshNodes at `{nodeTypePath}/Release/{version}`, with content type
[`NodeTypeRelease`](xref:MeshWeaver.Graph.Configuration.NodeTypeRelease):

| Field | Description |
|---|---|
| `Version` | `{yyyyMMddHHmmss}-{hash}` by default (chronologically sortable), or an explicit label supplied at Create-Release time. |
| `Code`, `HubConfiguration`, `ContentCollections` | The exact inputs that were compiled. |
| `FrameworkVersion` | The MeshWeaver version this release was built against. |
| `AssemblyPath` / `PdbPath` | The compiled DLL on disk — stable per `(NodeType, Version)`, never deleted while any ALC may still hold it. |
| `Status` | `Succeeded` (loadable, candidate for "active release") or `Failed` (kept as history; the previous succeeded release stays active). |

`NodeTypeDefinition.LatestReleasePath` always points at the most recent release;
the full release history is the set of `Release/*` children.

---

## Pinning an instance to a fixed release

By default, every instance hub of a NodeType binds to `LatestReleasePath` — a new
release automatically moves them forward. To freeze a NodeType (and all its
instances) on a specific historical build, set
`NodeTypeDefinition.RequestedReleasePath` to that `Release/{version}` path.

- While `RequestedReleasePath` is set, instance hubs resolve to **that** release,
  not `LatestReleasePath`.
- Creating a new release updates `LatestReleasePath` but **does not** touch
  `RequestedReleasePath` — pinned NodeTypes stay put until you clear or move the
  pin deliberately.

This is the supported way to develop against a fixed release: pin the NodeType,
compile freely, then unpin (or re-point) when you're ready to adopt the new build.

---

## When is a NodeType recompiled? — verify-before-skip

The kickoff does **not** trust a bare `CompilationStatus == Ok`. That value is
persisted into the NodeType MeshNode's JSON, so a stale `Ok` can easily outlive
the assembly that produced it. `NodeTypeCompilationHelpers.HasUsableBuild` is the
gate: a compile is **skipped only when all three conditions hold** —

1. `CompilationStatus == Ok`
2. `MeshNode.AssemblyLocation` points at a DLL that **still exists on disk**
3. `CompiledFrameworkVersion` equals the **current** framework version (`NodeTypeCompilationHelpers.FrameworkVersion`)

Anything else triggers a recompile. This makes a cold hub start **self-healing** against a range of real-world conditions:

| Situation | Why the bare `Ok` lies | Caught by |
|---|---|---|
| Seed-data pollution — a prior run stamped `Ok` into sample/seed JSON | The DLL was a per-process temp artefact | Rule 2 |
| Cleaned-up `.mesh-cache` / temp DLL | File deleted since | Rule 2 |
| Cross-machine checkout / fresh CI agent | The DLL never existed here | Rule 2 |
| **MeshWeaver redeployed at a new version** | The cached DLL bound against the *old* framework assemblies (ABI-stale) | Rule 3 |

### Framework-version freezing

A compiled NodeType DLL references the MeshWeaver framework assemblies present
at compile time. When MeshWeaver is **redeployed at a new version**, those
assemblies change and the cached DLL may be ABI-incompatible — so a release is
only usable while the framework version matches.

`RunCompile` stamps `NodeTypeDefinition.CompiledFrameworkVersion` with
`NodeTypeCompilationHelpers.FrameworkVersion` on every success. That value is:

- **Deployed builds** — the semver baked into `AssemblyInformationalVersion` by
  the NuGet pack process (e.g. `3.0.0-preview2`). It is identical on every server
  running the same deployed build — a file write-time would differ per machine and
  is therefore *not* used.
- **Un-packed dev builds** — the version stays the frozen default (`1.0.0`) across
  every local `dotnet build`, so the `MeshWeaver.Graph` assembly's last-write time
  is folded in (`1.0.0+{timestamp}`). On the single dev machine that is
  "frozen per build" — stable within a run, changes on rebuild — exactly the
  dev-iteration signal we want.

On a framework-version mismatch the NodeType recompiles and **mints a new release**
for the new framework. The old release is left intact as history so instances still
loaded on it keep running until they cycle.

---

## Quick reference

| I want to… | Do this |
|---|---|
| Compile a NodeType for the first time | Nothing — activating any instance hub kicks it automatically |
| Force a recompile | Edit a `Source/*.cs` node, or click **Create Release** |
| Capture a named, annotated release | **Create Release** (sets `ReleaseNotes`) |
| Watch a compile | Subscribe to `{nodeTypePath}/_Activity/compile-{id}` (`ActivityLog`) |
| Read diagnostics of a failed compile | `NodeTypeRelease.CompilationActivityPath` → that Activity's `Messages` |
| Cancel a running compile | `hub.CancelActivity(activityPath)` |
| List releases | The `Release/*` children of the NodeType |
| Find the current release | `NodeTypeDefinition.LatestReleasePath` |
| Pin instances to a fixed release | Set `NodeTypeDefinition.RequestedReleasePath` |
| Understand why it recompiled | `HasUsableBuild` failed rule 2 (assembly gone) or rule 3 (framework changed) |
