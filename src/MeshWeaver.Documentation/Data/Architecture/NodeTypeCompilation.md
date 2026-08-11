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

> For *authoring* a NodeType (namespace layout, content record, layout areas, CSV
> data) see [Creating Node Types](/Doc/DataMesh/CreatingNodeTypes). For the design
> rationale behind the release model see the
> [NodeType Release Redesign](/Doc/Architecture/Postmortems/NodeTypeReleaseRedesign) postmortem.

<svg viewBox="0 0 760 340" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arrow" markerWidth="10" markerHeight="7" refX="9" refY="3.5" orient="auto">
      <polygon points="0 0, 10 3.5, 0 7" fill="#90a4ae"/>
    </marker>
    <marker id="arrow-blue" markerWidth="10" markerHeight="7" refX="9" refY="3.5" orient="auto">
      <polygon points="0 0, 10 3.5, 0 7" fill="#1e88e5"/>
    </marker>
    <marker id="arrow-green" markerWidth="10" markerHeight="7" refX="9" refY="3.5" orient="auto">
      <polygon points="0 0, 10 3.5, 0 7" fill="#43a047"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="340" rx="12" fill="#1a1f2e" opacity="0.7"/>
  <rect x="20" y="20" width="160" height="100" rx="10" fill="#1565c0" stroke="#1e88e5" stroke-width="1.5"/>
  <text x="100" y="47" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">NodeType</text>
  <text x="100" y="66" text-anchor="middle" fill="#90caf9" font-size="11">{ns}/{Type}</text>
  <text x="100" y="84" text-anchor="middle" fill="#90caf9" font-size="10">Source/*.cs</text>
  <text x="100" y="100" text-anchor="middle" fill="#90caf9" font-size="10">CompilationStatus</text>
  <text x="100" y="114" text-anchor="middle" fill="#90caf9" font-size="10">LatestReleasePath</text>
  <rect x="20" y="200" width="160" height="78" rx="10" fill="#1b3a1b" stroke="#43a047" stroke-width="1.5"/>
  <text x="100" y="225" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">Instance Hub</text>
  <text x="100" y="245" text-anchor="middle" fill="#a5d6a7" font-size="11">{ns}/{Type}/{id}</text>
  <text x="100" y="262" text-anchor="middle" fill="#a5d6a7" font-size="10">Loads the active</text>
  <text x="100" y="276" text-anchor="middle" fill="#a5d6a7" font-size="10">Release DLL</text>
  <rect x="300" y="110" width="170" height="100" rx="10" fill="#4a1a00" stroke="#f57c00" stroke-width="1.5"/>
  <text x="385" y="137" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">Compile Activity</text>
  <text x="385" y="157" text-anchor="middle" fill="#ffcc80" font-size="11">_Activity/compile-{id}</text>
  <text x="385" y="175" text-anchor="middle" fill="#ffcc80" font-size="10">Status: Running →</text>
  <text x="385" y="191" text-anchor="middle" fill="#ffcc80" font-size="10">Succeeded / Failed</text>
  <text x="385" y="207" text-anchor="middle" fill="#ffcc80" font-size="10">Roslyn diagnostics</text>
  <rect x="560" y="20" width="170" height="110" rx="10" fill="#1a2744" stroke="#5c6bc0" stroke-width="1.5"/>
  <text x="645" y="47" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">Release</text>
  <text x="645" y="67" text-anchor="middle" fill="#9fa8da" font-size="11">Release/{yyyyMMdd…}</text>
  <text x="645" y="85" text-anchor="middle" fill="#9fa8da" font-size="10">Compiled DLL path</text>
  <text x="645" y="101" text-anchor="middle" fill="#9fa8da" font-size="10">FrameworkVersion</text>
  <text x="645" y="117" text-anchor="middle" fill="#9fa8da" font-size="10">Immutable snapshot</text>
  <rect x="560" y="200" width="170" height="78" rx="10" fill="#1a2744" stroke="#8e24aa" stroke-width="1.5"/>
  <text x="645" y="225" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">DLL on disk</text>
  <text x="645" y="245" text-anchor="middle" fill="#ce93d8" font-size="11">AssemblyPath</text>
  <text x="645" y="262" text-anchor="middle" fill="#ce93d8" font-size="10">Never deleted while</text>
  <text x="645" y="276" text-anchor="middle" fill="#ce93d8" font-size="10">any ALC holds it</text>
  <line x1="180" y1="65" x2="298" y2="155" stroke="#f57c00" stroke-width="1.5" marker-end="url(#arrow)" stroke-dasharray="5,3"/>
  <text x="220" y="110" fill="#ffcc80" font-size="10" text-anchor="middle">Pending →</text>
  <text x="220" y="123" fill="#ffcc80" font-size="10" text-anchor="middle">RunCompile</text>
  <line x1="470" y1="145" x2="558" y2="80" stroke="#1e88e5" stroke-width="1.5" marker-end="url(#arrow-blue)"/>
  <text x="526" y="106" fill="#90caf9" font-size="10" text-anchor="middle">mints</text>
  <line x1="645" y1="130" x2="645" y2="198" stroke="#43a047" stroke-width="1.5" marker-end="url(#arrow-green)"/>
  <line x1="560" y1="75" x2="182" y2="75" stroke="#1e88e5" stroke-width="1.5" marker-end="url(#arrow-blue)"/>
  <text x="370" y="65" fill="#90caf9" font-size="10" text-anchor="middle">LatestReleasePath</text>
  <line x1="100" y1="198" x2="100" y2="120" stroke="#43a047" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arrow-green)"/>
  <text x="120" y="168" fill="#a5d6a7" font-size="10">activates</text>
  <line x1="180" y1="229" x2="558" y2="229" stroke="#43a047" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arrow-green)"/>
  <text x="370" y="222" fill="#a5d6a7" font-size="10" text-anchor="middle">loads DLL from release</text>
</svg>

*NodeType compilation lifecycle: source changes trigger the Compile Activity, which mints an immutable Release; instance hubs load the DLL from the active release.*

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

**The one entry point is `hub.RequestNodeTypeRelease(nodeTypePath, …)`** (`MeshWeaver.Graph/NodeTypeReleaseExtensions.cs`) — GUI, agents, and tests all call it. It writes the trigger onto the NodeType node via `stream.Update`: `RequestedReleaseAt` (a timestamp, so repeated requests are distinct), plus `RequestedReleaseForce` to bypass the "sources match the last compile" short-circuit and `RequestedReleaseBy` to attribute the release to the caller. The per-NodeType release watcher dispatches only while `RequestedReleaseAt > LastReleaseRequestHandledAt` — an idempotent CAS — and lands on the same `RunCompile`.

> 🚨 **Do not post `CreateReleaseRequest` from new code.** The legacy request/handler pair (`MeshDataSource.HandleCreateRelease`) still exists for already-migrated callers, but the canonical surface is the `stream.Update` trigger above. See [RequestViaStreamUpdate](/Doc/Architecture/RequestViaStreamUpdate).

Use this to capture a named release with author-written `ReleaseNotes`.

### Re-triggering after a source edit

`RunCompile` records a `CompiledSources` snapshot — `{sourceNodePath → version}`
for every `Code`/`Test` node that fed the compile. When you edit a `Source/*.cs`
node, its version bumps. The mismatch against the snapshot marks the NodeType
dirty and triggers a recompile automatically — you never invalidate a cache by
hand.

### Source and test queries — and naming them

Which Code nodes feed a compile is declared on `NodeTypeDefinition.Sources` /
`Tests` as mesh queries (defaults: `namespace:Source scope:subtree` /
`namespace:Test scope:subtree` — the conventional sibling namespaces). Each entry
may carry an optional `name=` prefix, e.g.
`"shared=@SocialMedia/Post/Source/Platform"`. The name is display-only: the
NodeType side menu groups the resolved files under it (unnamed entries land in
the default `src` / `test` group), while the compiler strips the prefix and
behaves identically. `CodeQueryResolver` is the single expansion/grouping
implementation, so the files shown in the GUI are exactly the files that
compile.

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

## Every stage is bounded — a compile can never park at `Compiling`

`CompilationStatus` is also the **single-flight lock**: the compile watcher fires only on a
transition *into* `Pending`, and the `Pending → Compiling` flip inside the owning hub's
serialized `Update` elects exactly one dispatcher. Two concurrent triggers therefore produce
one run — but the corollary is that a trigger arriving while the type is `Compiling` is
**absorbed** by design. So a compile stage that never answers does not merely run late: it
strands the NodeType at `Compiling` for the life of the activation, with nothing able to
restart it.

Every stage consequently has a wall clock, and the compile subscription is guaranteed to
produce exactly one terminal status — an *empty* completion is caught by a totality guard, and
**no** completion by these bounds. All four are tunable on `CompilationCacheOptions`:

| Stage | Option (default) | On expiry |
|---|---|---|
| Source snapshot — the one-shot read of the source set | `SourceSnapshotTimeout` (30 s) | `Error`, naming the source query that never emitted |
| `roslyn-compile` — NuGet restore, source generators, `Emit`, disk write | `RoslynCompileTimeout` (5 min) | `Error` naming the leg; the stage is **cancelled**, so an unreachable package feed stops instead of pinning a compile thread |
| `assembly-load` — assembly load, `GetTypes`, provider reflection, config instantiation | `AssemblyLoadTimeout` (2 min) | `Error` naming the leg (a running type initializer cannot be interrupted, so the stage is abandoned, not cancelled) |
| `assembly-store-upload` — publishing the bytes to the `IAssemblyStore` | `AssemblyStoreUploadTimeout` (2 min) | **Not** an error: an upload failure has never failed a compile, so the compile settles `Ok` with a warning naming the leg on its ActivityLog — the assembly is usable locally but cross-silo activation will not find it |

A tripped bound is never something to raise: it means a stage genuinely stopped answering, and
the message names which one. Fix that stage, then retry with **Create Release** / the Compile
button — the terminal status is settled, so the next trigger dispatches normally.

---

## Activations behind an in-flight compile show LIVE progress — never a silent park

An instance hub activating while its NodeType is `Pending`/`Compiling` waits for the compile
to settle — but only **briefly** in silence. After a short grace
(`NodeTypeEnrichmentHelpers.InFlightOverlayGrace`, 5 s), the activation stops holding every
delivery and settles onto the **compilation-in-progress overlay**
(`WithCompilationInProgressOverlay`):

- The instance's **Overview renders the type's live progress page**
  (`NodeTypeLayoutAreas.CompileProgressView`): current status, the streaming compile
  activity log, and — when more types are queued (the framework-bump warm-up recompiles
  every dynamic type) — the whole sweep as an "N of M types compiled" progress bar with the
  type currently compiling and the queued count. On `Ok` it redirects back to the instance.
- **Typed requests fail fast** with `ErrorType.CompilationInProgress` naming the NodeType
  (`UnhandledMessageNack`), instead of parking until the caller's own 60 s request timeout.
  Area clients handle that NACK by swapping to the type's `Progress` area
  (`AreaErrorClassifier.TryGetCompilationInProgressNodeType`).
- The standard **overlay self-heal** watches the type: the compile's terminal write advances
  the node version, the watcher recycles the instance, and the next access enriches against
  the settled build.

A compile that settles inside the grace never surfaces any of this — short compiles activate
the real hub directly. The grace is a *visibility* bound, not a compile bound: the compile
keeps running however long it needs (see the stage bounds above).

---

## Cancelling a compile

Compilation is an Activity, so it cancels through the **Activity Control Plane**
(see [ActivityControlPlane](/Doc/Architecture/ActivityControlPlane)) — patch the activity's
`RequestedStatus`, never post a bespoke cancel message:

```csharp
hub.CancelActivity(activityPath);
```

The activity hub's control-plane watcher sees the patch and tears the compile
down; the activity and the NodeType settle to `Cancelled` / the previous status.

---

## Where releases live

Releases are MeshNodes at `{nodeTypePath}/Release/{version}`, with content type
`NodeTypeRelease`:

| Field | Description |
|---|---|
| `Version` | `{yyyyMMddHHmmss}-{hash}` by default (chronologically sortable), or an explicit label supplied at Create-Release time. |
| `Code`, `HubConfiguration`, `ContentCollections` | The exact inputs that were compiled. |
| `FrameworkVersion` | The MeshWeaver version this release was built against. |
| `AssemblyPath` / `PdbPath` | The compiled DLL on disk — stable per `(NodeType, Version)`, never deleted while any ALC may still hold it. |
| `Status` | `Succeeded` (loadable, candidate for "active release") or `Failed` (kept as history; the previous succeeded release stays active). |
| `SourceVersions` / `TestVersions` | `{codeNodePath → LastModified.UtcTicks}` snapshots of the source and test files that went into the release — the release page renders them as navigable lists, so every release knows exactly which file versions it was built from. |

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

## 🚨 That recompile can FAIL — and nothing upstream can warn you

Rule 3 guarantees a framework upgrade recompiles **every** dynamic NodeType. That recompile
builds source **stored in the mesh** against the **new** framework — and nothing in the build
pipeline has ever type-checked it.

### Where NodeType source actually lives

|  | Repo file | Mesh node |
|---|---|---|
| Example | `samples/Graph/Data/SocialMedia/Post/Source/*.cs` | `SocialMedia/Post/Source/*` |
| Role | **import seed** — declared `<None Include="**/*" …>` in `MeshWeaver.Samples.Graph.csproj` | **the runtime source of truth** |
| Compiled by `dotnet build` / CI | **Never.** It is content, not code — no `<Compile>` item, no `-warnaserror`, no test | Compiled at runtime by `RunCompile` |
| Edited by | git | in-mesh edit, `Patch`, GitSync import |

🚨 **The `configuration` lambda is C# inside a JSON string field**, so it is not reached by any
`.cs`-shaped search or build. A `grep --include='*.cs'` for a deleted symbol comes back clean while
three NodeTypes still call it from their JSON. Search the node JSON too.

### The failure cascade

A failed type produces no assembly, so everything downstream of it is *skipped* rather than built
(`NodeTypeDependencyGraph.FirstBlockedBy`), and `DynamicTypePreWarmer` refuses readiness for any
type that regressed against its pre-bake baseline:

```
framework API deleted in src/  →  in-mesh caller no longer compiles
  → NodeType = CompileError
  → every dependent = UpstreamFailed          (transitive, in topological order)
  → DynamicTypePreWarmer: REFUSING READINESS
  → instance hubs never activate → each SubscribeRequest waits the full 60 s → faults
  → hung pages, failed liveness probes, dropped silo
```

The shape in the log is a `CS1061` / `CS0103` against framework surface:

```
CS1061: 'MessageHubConfiguration' does not contain a definition for 'AddTracking'
```

**A timeout is not a verdict, and it does not cascade like one.** The chain above starts with a
`CompileError` — Roslyn's answer that the type is broken. When the sweep instead gets *no* answer
(`TimedOut`: the per-type budget elapsed, typically a cross-silo `SubscribeRequest` during a roll),
that is not evidence of breakage and must never stall a rollout. So the two cascade differently:

| Upstream outcome | Dependent reported as | Refuses readiness? |
|---|---|---|
| `CompileError` / `Faulted` | `UpstreamFailed` | **yes** |
| `TimedOut` | `UpstreamUnevaluated` | no |
| `UpstreamUnevaluated` | `UpstreamUnevaluated` | no |

Both no-answer statuses are filed by `NodeTypeBakeGateState` under *unevaluated* — they can never
set `Regressed`, but they are **named in the `/health` payload**, because non-blocking must not mean
invisible. Only a failure on a type that was **healthy before this image** gates at all: a type
already sitting at `Error` before the deploy is pre-existing damage, and gating on it would let one
abandoned NodeType freeze every future rollout.

That distinction is what makes the readiness gate safe to arm. Counting timeouts as regressions
stalled memex-cloud on 2026-08-02 with "7 NodeType(s) regressed" and not one `CS####` diagnostic;
counting only *direct* timeouts leniently still let one timed-out shared source gate through its
dependents, which reproduced the same stall one hop downstream.

### The obligation on framework changes

Removing or renaming any public framework surface — extension methods on
`MessageHubConfiguration` / `IMessageHub`, `Controls.*`, `host.*` helpers, content base types — is
a **breaking change to code the compiler cannot see**. Before deleting one:

1. `grep -rn "<Symbol>" content samples/*/Data` — catches in-repo node source.
2. Search the **live mesh** (`search_chunks`) — catches nodes that drifted from the repo.
3. Port or delete the callers in the same change, then sweep.

### The pre-prod sweep

`Search('nodeType:NodeType')` → `LspDiagnosticsForNode('@{path}')` per type (reads the *cached*
compilation, no re-emit) → fix roots first, since one red upstream reports as a failure in every
dependent → re-check until every type reads `Ok`. Warnings are in scope: an unregistered `$type`
leaves content as an untyped `JsonElement`, which renders **empty** rather than erroring. The full
protocol lives in the `/code` skill.

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
| Understand a "Compile leg '…' did not complete within Ns" error | That stage stopped answering — see [Every stage is bounded](#every-stage-is-bounded--a-compile-can-never-park-at-compiling) |
| Delete or rename a public framework API | Grep `content` + `samples/*/Data` **and** search the live mesh for callers first — CI never compiles in-mesh source |
| Check the mesh is shippable | `Search('nodeType:NodeType')` → `LspDiagnosticsForNode` per type → every one reads `Ok` |
| Understand why one bad NodeType took the portal down | `CompileError` → dependents `UpstreamFailed` → readiness refused → 60 s hub-activation faults |
