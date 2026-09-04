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
> For compiling the same source **outside** the portal — in CI, so the bytes can be shipped rather
> than recomputed — see [Plugin Packaging](/Doc/Architecture/PluginPackaging).

**Where the code lives (since #1707):** everything that shapes a compile's *generated input* —
the skeleton generator (`DynamicMeshNodeAttributeGenerator`), source-query resolution
(`CodeQueryResolver`), `@@`-include shaping, source aggregation/filter/join order, the reference
set, parse/compilation options, source-generator execution, and the emit itself — lives in the
dedicated **`MeshWeaver.Compiler`** assembly, whose full MVID pins the framework build identity.
The mesh-actor half — source discovery against the live mesh, access impersonation, scheduling,
compile-status write-backs — stays in `MeshWeaver.Graph` (`MeshNodeCompilationService`,
`NodeTypeCompilationHelpers`) and orchestrates that toolchain. One pipeline serves every path:
the portal's on-demand compile, the batch bake, and the CI bake host all call the same
`MeshWeaver.Compiler` code.

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
| Source snapshot — the one-shot read of the source set | `SourceSnapshotTimeout` (30 s) | `Unavailable`, naming the source query that never emitted — **not** `Error`; see below |
| `roslyn-compile` — NuGet restore, source generators, `Emit`, disk write | `RoslynCompileTimeout` (5 min) | `Error` naming the leg; the stage is **cancelled**, so an unreachable package feed stops instead of pinning a compile thread |
| `assembly-load` — assembly load, `GetTypes`, provider reflection, config instantiation | `AssemblyLoadTimeout` (2 min) | `Error` naming the leg (a running type initializer cannot be interrupted, so the stage is abandoned, not cancelled) |
| `assembly-store-upload` — publishing the bytes to the `IAssemblyStore` | `AssemblyStoreUploadTimeout` (2 min) | **Not** an error: an upload failure has never failed a compile, so the compile settles `Ok` with a warning naming the leg on its ActivityLog — the assembly is usable locally but cross-silo activation will not find it |

A tripped bound is never something to raise: it means a stage genuinely stopped answering, and
the message names which one. Fix that stage, then retry with **Create Release** / the Compile
button — the terminal status is settled, so the next trigger dispatches normally.

### 🚨 A compile NEVER runs against an unestablished source set

The source snapshot is the one stage whose failure is **not** a compile verdict, and it is the
only one that settles `Unavailable` instead of `Error`.

Discovery races two legs — a direct `IMeshService` read and the cached synced query — and each
leg issues one query per declared `Sources`/`Tests` entry. Those queries can fail
*independently*: a `shared=@Other/Partition/Source` entry crossing into a busy peer silo
answers with a `SubscribeRequest` timeout while the type's own `Source` query answers fine. The
snapshot therefore carries an **establishment verdict** (`SourceSnapshot.IsEstablished`), not
just a list:

| Snapshot | Meaning | What the compile does |
|---|---|---|
| Established, non-empty | every query answered, here are the sources | compile |
| Established, **empty** | every query answered and matched nothing (sources deleted, or a configuration-only type) | compile — a failure then classifies `NoSources`, which does not gate a rollout |
| **Unestablished** | at least one query errored or never answered | **refuse**: throw `SourceDiscoveryUnavailableException`, stamp `CompilationStatus.Unavailable` |

A failed leg used to be swallowed (`.Catch(_ => empty)`), so the surviving legs' **partial** set
won the race and reached Roslyn — which then emitted a completely genuine-looking
`CS0246: The type or namespace name 'ScopeLibrary' could not be found` about code that was
fine. The bake readiness gate cannot tell that from a real image regression, so a rollout
stalled on healthy content (issue #1218; memex-cloud 2026-08-11, 14 of 56 sampled types).

An unestablished report never *wins* the race either — it is held until both legs have
answered, so one dead query cannot veto a healthy cached set. It only settles the snapshot when
nothing else could.

Downstream, the `Unavailable` stamp already means the right thing everywhere: the instance
overlay drops "please correct the code" for the retry copy (#641),
`EnsureCompileDispatched` treats it as "never determined" and re-dispatches on the next
**request** (never a timer), and `DynamicTypePreWarmer.WarmOne` reports `PreWarmStatus.TimedOut`
— which `NodeTypeBakeGateState` files under *not evaluated* rather than as a regression. A real
Roslyn error on an established set is untouched: `Error` → `CompileError` → it still gates.

---

## Activations behind an in-flight compile show LIVE progress — never a silent park

An instance hub activating while its NodeType is `Pending`/`Compiling` waits for the compile
to settle — but only **briefly** in silence. After a short grace
(`NodeTypeEnrichmentHelpers.InFlightOverlayGrace`, 5 s), the activation stops holding every
delivery and settles onto the **compilation-in-progress overlay**
(`WithCompilationInProgressOverlay`):

- **Every area of the instance renders the type's live progress page**
  (`NodeTypeLayoutAreas.CompileProgressView`): current status, the streaming compile
  activity log, and — when more types are queued (the framework-bump warm-up recompiles
  every dynamic type) — the whole sweep as an "N of M types compiled" progress bar with the
  type currently compiling and the queued count. On `Ok` it redirects back to the area the
  caller asked for, so a deep link survives the wait.

  The overlay registers `Overview` by name plus a **catch-all** guarded by
  `LayoutDefinition.HasNamedRenderer` — the type's own areas (`KeyMetrics`, …) do not exist on
  the overlay hub, and covering only `Overview` did not remove the silent park, it relocated it:
  every other area answered `"**Area not found** — No renderer is registered for area
  `KeyMetrics`"`, a terminal-looking verdict for a state that resolves itself in seconds
  (issue #1411). The guard is what keeps the catch-all off areas the default node configuration
  already owns — two renderers for one area are last-wins-*destructive*.
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

## A fault card must not outlive its cause — the overlay RE-EVALUATES

The overlay is a **degraded binding with a lifetime**, never a verdict. Enrichment binds a
per-instance hub's configuration exactly once — the re-enrichment short-circuit
(`node.HubConfiguration != null`) is what makes activation cheap — so a card applied during a
bad ten seconds is served for the grain's whole lifetime unless something *revokes* it. That
revocation is `ArmOverlaySelfHeal`, and its correctness rests on one rule:

> 🚨 **The heal signal must not share a failure mode with the fault.** Both original heal routes
> — the version-advance and the grace re-read — subscribe
> `meshHub.GetWorkspace().GetMeshNodeStream(nodeType)`: *the same stream whose silence made the
> enrichment slow path time out*. When that stream stops emitting, the fault and its cure vanish
> together and the card is permanent.

That is not hypothetical. On **2026-08-17** (issue #1814) a deploy left the first pod compiling
269 types; page requests inside that window latched the card onto ~12 plugin roots
(`Training`, `Video`, `RolePlay`, `Edu`, `Chess`, `Collaboration`, …). `Store/Plugin` then
compiled **successfully on both pods** — and **1 h 24 m later** an anonymous browser still got
the card, while neither pod had logged a single overlay or "did not settle" event in the
preceding 30–40 minutes. Nothing was retrying. Recycling the twelve roots by hand was the only
remedy, and the card's own copy ("the page recovers automatically … this instance recycles
itself") was straightforwardly false.

So the watcher has a third route that owes the stream nothing:

- **`AuthoritativeTypeRead`** — a one-shot `path:{nodeType}` query through `IMeshQueryCore`, as
  System. It reads the mesh's **query providers (storage)**, not a cached stream, so a mirror
  that can never learn it is stale cannot suppress it.
- **A widening ladder, not a poll** — `45 s → 90 s → 3 min → 6 min`, then **10 min for ever**
  (`ReEvaluationLadder` / `ReEvaluationCeiling`). One read per rung, each capped by
  `ReEvaluationReadTimeout` and serialised with `Concat`, so a slow read can never overlap the
  next rung. At the 2026-08-17 blast radius that steady state is ~72 single-node reads an hour
  across the whole mesh. **The ladder never stops** — a re-evaluation budget that ran out would
  re-create precisely the defect it exists to remove.
- **A faulted or empty read is not a verdict** — it is logged and the ladder asks again. Giving
  up quietly is how the card latched in the first place.
- **Loud on non-convergence** — a re-read that still finds no usable build logs the type's
  status/assembly/framework; past the last rung it does so at `Warning`, next to the existing
  admin notification at `StuckReportDelay`.

### …and the recycle it orders is SPACED, because it destroys its own watcher

The heal disposes the instance hub — taking the watcher with it. The replacement hub arms a
**fresh** watcher whose ladder starts at the first rung, so a pair whose *re-enrichment* keeps
faulting (a type that reports a usable build the instance still cannot bind — #1814's
deterministic cross-hub `Conflict`) would recycle every 45 s for ever. No state inside the
watcher can bound that, because the bound has to outlive the thing being bounded.

`OverlayHealBudget` (mesh-scoped singleton, registered in `AddGraph`) is that memory, keyed by
*(instance, NodeType)*. The **first** heal is never delayed; each further heal inside a 30-minute
window waits out a widening spacing (45 s → 90 s → 3 min → 6 min → 10 min). It **defers** a
recycle, never cancels one, and a pair that heals once and stays healthy is forgotten.

Pinned by `OverlaySelfHealWatcherTest` (silent stream → heals unaided; still-broken type → keeps
its card on single-digit reads per hour; faulted/empty read → ladder continues; non-converging
loop → bounded recycles, with the un-budgeted control in the same test) and
`OverlayHealBudgetTest`.

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
the assembly that produced it — and, conversely, a later failed compile can leave
`Status=Error` behind a perfectly usable earlier build.
`NodeTypeCompilationHelpers.HasUsableBuild` is the gate: a compile is **skipped
only when all of these hold** —

1. `LatestAssemblyCollection` is populated (only a *successful* compile write-back sets it)
2. `LatestAssemblyPath` is populated
3. `CompiledFrameworkVersion` equals the **current** framework identity
   (`FrameworkBuildIdentity.FrameworkVersion` in `MeshWeaver.Compiler`)
4. the dependency clause holds — **record-first** (#1707 slice 2): when the
   definition carries a per-type `CompiledDependencies` record (referenced
   assembly name → surface-id, read off the EMITTED assembly's AssemblyRef
   table + the reserved `!toolchain` entry), every stamped pair must still
   resolve identically in this environment — so a module update invalidates
   only its dependents, and a type that binds no module is valid on any
   deployment regardless of composition. A null record falls back to the
   legacy whole-set `CompiledModulesHash` vs `InstalledModulesFingerprint`
   comparison (null stamp or null caller keep the framework-only behavior)

`CompilationStatus` is deliberately **not** a condition — the assembly fields
self-heal across a stray `Status=Error`. The check is also **metadata-only**: no
store probe, no `File.Exists` — the kickoff prefers a redundant compile over a
blocking store round-trip, and a store that has lost the bytes is caught at
activation (`TryGetAssemblyPath` misses → `TriggerRecompileAndRetry`); the bake
probe's `NodeTypeBakeStatus.Classify` has the `BytesMissing` state for exactly
that gap.

🚨 **The `!toolchain` half of clause 4 is a PROXY, and it now demotes to a trigger
(#1976).** It hashes the toolchain closure's implementation MVIDs — 16 assemblies,
383 commits/30d — so it moves on changes that touch none of a given type's compile
input. When a stale-build verdict has already been formed *and* the store already
holds bytes under the live framework tag, the **re-evaluation lane** regenerates
what a compile would be handed and compares it with the build's stamped `!input`
content key: equal ⇒ the record is restamped and no compile is dispatched;
different ⇒ compile (a NEW invalidation — that branch used to skip
unconditionally); inconclusive ⇒ exactly the behaviour above, and never a
restamp. Full reasoning, including the one half that is deliberately still gated:
[The Toolchain Re-evaluation Lane](../ToolchainReevaluationLane).

**Adopt-before-compile (#1707 slice 3).** "If a pre-built lib exists, take it; only if not,
generate" holds at every entry point, not just at boot: a package INSTALL and a git-sync PUSH
first run their affected types through the deployment's bundle sources
(`IPrebuiltAssemblyConsumer.SeedForTypes` — the image's `prebuilt/` plus the CI-published
identity root, each assembly validated by the framework-identity and dependency-record gates),
and the **release-request watcher satisfies** a request that arrives while the node already holds
a valid build of the current sources — the trigger is consumed without dispatching Roslyn.
`RequestNodeTypeRelease(force: true)` remains the documented escape hatch and always compiles.
Anything not adopted compiles exactly as before — the "generate" branch is untouched.

🚨 **"Always compiles" has to hold in the compile watcher, not only in the release watcher
(#2818).** The release watcher bypasses its satisfied-branch on a force and flips the type
`Pending` — but the on-demand adoption step lives in the *compile* watcher, where every `Pending`
converges, and until #2818 that step asked the bundle sources again regardless of the force. On any
mesh whose bundle still resolved, a force re-adopted the very bytes the operator was trying to
replace and settled "without a Roslyn pass" with a fresh `LastCompileSucceededAt` over the same
`LatestAssemblyMvid`; it only worked where the bundle had gone missing. This is what left the stale
prebuilt of #2813 unrecoverable on a node whose source was already fixed. Now a `Pending` that
carries `RequestedReleaseForce` skips adoption and dispatches (or parks, under `RequirePrebuilt`,
with the park's named reason), and every terminal stamp — `ApplyCompileSuccess`,
`ApplyCompileFailure`, `ApplyGateSettle` — sets the flag back to `false`, so a spent force can never
suppress adoption for a later, unforced trigger. The regression
(`NodeTypeOnDemandAdoptionTest.AForcedRelease_NeverConsultsTheBundleSources`) asserts the
discriminating fact — the forced `Pending` never *consults* the consumer — rather than the MVID
moving, because a force on a type whose bundle does not resolve compiled correctly even before the
fix. Its third phase found the adjacent gap: the on-demand step judged an adoption "landed" by
`HasUsableBuild` alone, which a type's PREVIOUS build already satisfies — so a consumer that
reported an adoption it never wrote back stranded such a type at `Pending`. Landed now also
requires `CompilationStatus == Ok`, which is what a real `PrebuiltAssemblySeeder.Seed` stamps.

Anything else triggers a recompile. This makes a cold hub start **self-healing**:

| Situation | Why the bare record lies | Caught by |
|---|---|---|
| Cleaned-up cache / lost store bytes | The record still points at them | Activation store probe (`BytesMissing`) |
| **MeshWeaver redeployed with a breaking change** | The cached DLL bound against the *old* framework surface (ABI-stale) | Rule 3 |
| Module updated | The cached DLL may bind the replaced module's old ABI | Rule 4 |

### A compile that FAILED is re-driven too — one attempt per set of inputs

`HasUsableBuild` and its framework-stale twin both key on **assembly coordinates**, and a failed
compile writes none: `ApplyCompileFailure` stamps neither `LatestAssembly{Collection,Path}` nor
`CompiledFrameworkVersion`. For a NodeType that never compiled successfully *on this deployment*
those stay null forever, so every automatic path used to skip it — the first-build kickoff needs a
`null` status, the recovery kickoff needs `Compiling`, the framework-stale kickoff needs the
coordinates, the release watcher needs a human, and the park registry's source-change auto-retry is
in-memory (a failure that predates the process is not in it). Only a human pressing **Compile** got
such a node out; a redeploy, a framework bump, a module update and a fix to the failing code reached
none of them ([#1793](https://github.com/Systemorph/MeshWeaver/issues/1793); the fix written for
fifteen types parked on memex-cloud could not reach the nodes it was written for).

So a failure records the one thing it honestly can: **the inputs the verdict was formed from** —
framework identity, installed-module fingerprint, and the source snapshot the compile consumed —
folded into `NodeTypeDefinition.FailedBuildInputs`. The owner-side re-drive fires exactly when the
LIVE inputs differ from that stamp:

| What moved | Effect |
|---|---|
| A new framework (a redeploy, possibly carrying the fix) | one fresh attempt |
| A module update | one fresh attempt |
| An edited / added / removed source | one fresh attempt |
| Nothing — same framework, modules and sources | **no attempt**: the identical failure would reproduce |
| The stamp is `null` (a failure from before this field, or an `Error` baked into a node file) | one fresh attempt — the migration |
| The source set has not been established yet (`CurrentSourceVersions` unwritten) | **no attempt — it WAITS**: "not known yet" is not "no sources", and a compile driven from a set nobody established forms a verdict from evidence the mesh does not have |

It is bounded three ways, and the first is the one that does the work:

1. **Structural.** The flip to `Pending` writes the live token **in the same update**, so the trigger
   the re-drive fires on is false the instant it fires. A reconcile that can re-arm its own trigger
   is the 257,000-version write-storm shape; the stamp forecloses it.
2. **Loud.** A process-wide ledger (`NodeTypeCompileParkRegistry.RecordFailureRedrive`) logs an
   **error naming the path** the moment a type is re-driven twice for the *same* inputs — i.e. the
   moment (1) provably did not hold. Non-convergence is never quiet.
3. **Terminal.** Past `MaxAutomaticFailureRedrives` the kickoff gives up for the hub's lifetime and
   says so, naming the type, its error and the remedy. An explicit Compile refunds the budget.

The re-drive is **owner-driven, never caller-driven**. It fires from the type's OWN hub on facts the
node already holds; no request, and no requester's identity, is an input. The compile runs as System
and its activity row lands in the owning partition attributed to System — exactly as the first-build,
recovery and framework-stale kickoffs have always done — and no user's `RequestedReleaseAt` /
`RequestedReleaseBy` is touched, so nothing is misattributed. An unauthorized caller who merely
activates the hub therefore gains no lever: the trigger is a property of the persisted record, and
the three inputs that can move it (framework identity, installed modules, the type's own source
nodes) are all writable only by principals who already hold that access.

And when the re-drive **declines** — a type settled at `Error`/`Unavailable` whose verdict was formed
under exactly the live inputs — the hub logs one warning per activation naming the type, its error
and why nothing will retry it. That state is correct and bounded, and before that line it was also
completely silent: nothing anywhere named a NodeType that is broken and will not be retried.

> 🚨 `FailedBuildInputs` is **mesh-owned operational state**: exports strip it, imports preserve the
> live node's value, and `ShippedNodeTypeStateTest` bans it from committed node files. An authored
> token that happened to match the importing deployment's live inputs would suppress precisely the
> retry it exists to grant.

### 🚨 An ADOPTED build must say whether it was ever checked against the source

Adoption — taking a prebuilt assembly from a bundle instead of compiling — is what makes installs
and restarts cheap. It is also, until #2813, the one path that could make a NodeType assert something
nobody established.

`PrebuiltAssemblySeeder.Seed` writes CROSS-HUB, so it cannot read the owner's live source snapshot
(#1834); it asks instead, via `RequestedSourceStampAt`, and the owner answers by stamping
`CompiledSources = CurrentSourceVersions`. **That stamp is what makes `IsDirty` false**, which is
what `InstallReleaseRequestWatcher`'s "satisfied by the existing current build" branch requires and
what makes an adoption stick.

And it is also how an adopted build lied. The two signals an operator is taught to trust —
`CompilationStatus.Ok` and `CompiledSources == CurrentSourceVersions` — both read clean **whether or
not the bytes have anything to do with the live source**, because the adoption writes the second one
itself. The staleness detector was never broken; it was answering a question the adoption had already
answered for it. On 2026-08-30 a GitSync `update` pulled new source, adopted a prebuilt built from
older source, reported `Succeeded`, and the stale code destroyed four client documents' bodies — one
unrecoverable. Only forcing a compile moved the MVID.

#### The check, and where it can be made

The producer records a **content** fingerprint of the sources the bytes were built from. It has to
be content, not versions: `CurrentSourceVersions` is `{path → LastModified.UtcTicks}`, mesh-LOCAL
modification times the producer cannot know and does not have (the bake writes zeros), so a
fingerprint over ticks would never match and every adoption would be refused.

**What exactly is hashed — `NodeTypeSourceFingerprint` (in `MeshWeaver.Compiler`).** The fingerprint
is taken over the *compile input*: the `NodeCompileShaping.CollectCompileSources` fold — deduplicated
ordinal-ignore-case, executable cells and blank files dropped — reduced to
`(node path, SHA-256 of the code text)`, **plus the `@@`-include closure** as
`(@@{resolved path}, SHA-256 of the code text)`, folded with `PartitionSourceFingerprint`, which
sorts by path so enumeration order cannot reach the result. Both the runtime and the bake call
*that* fold already, so they cannot fork on which files count.

The obvious alternative — hashing the source MeshNodes' serialised content — is unusable across
processes, and each of its three failure modes produces a **false refusal**, which is an outage
strictly worse than the staleness bug:

- **Run bookkeeping churns it.** `CodeConfiguration` carries `LastExecutedAt` / `LastExecutedBy` /
  `LastExecutedCodeHash` / `LastActivityPath`, written when a reader presses **Run** on a code cell.
  The live hash would move with no source change at all, and no producer can know those values.
- **The two sides serialise differently by design.** The consumer has a hub and therefore a
  TypeRegistry (polymorphic `$type` discriminators); the compiler-driven bake deliberately has
  neither — `TreeNodeLoader` materialises exactly the two content types a compile reads and leaves
  everything else null, precisely so a half-populated registry cannot degrade content to
  `JsonElement`. Two honest readers, two different JSON strings.
- **Node metadata is not compile input.** A description, an icon, an order: none of them change a
  byte of the emitted assembly.

It lives in `MeshWeaver.Compiler` because `FrameworkBuildIdentity.FullMvidAssemblies` is that
assembly's transitive closure, and adoption is *already* gated on the framework identity matching —
so a producer and a consumer that can adopt across each other necessarily run the same
implementation of this hash. Anywhere else, two meshes could disagree about the shape while agreeing
about the gate.

The comparison happens on the **owner**, in `ApplyAdoptedSourceStamp` — one pure function shared by
all three writers that can fulfil the request, so turning assert into check fixes all three at once:

| bundle stamp | matches the live one | outcome |
|---|---|---|
| yes | yes | adopt; `BuildProvenance = AdoptedVerified` |
| yes | **no** | 🚨 **refuse** — no stamp, flip `Pending` to compile the live source; `AdoptionRefused` |
| no (legacy), or the owner's own not published yet | — | adopt, **keep the stamp**; `AdoptedUnverified` |

> 🚨 **The legacy row keeps the stamp deliberately.** Withholding it makes every legacy-bundle type
> `IsDirty` on arrival, the `!IsDirty` absorb branch stops firing, and every install recompiles
> everything — the 43 activations / 13.5 s of boot the prebuilt lane exists to remove. On a
> `Modules:RequirePrebuilt` mesh a local compile is refused by design, so not stamping would **park
> every legacy-bundle type**: the outage that refusing unproven bundles was rejected to avoid,
> arriving through a different door. A bundle with no fingerprint is of **unknown** provenance, not
> **proven stale**, and those deserve different answers. The requirement is VISIBILITY, not refusal.

`BuildProvenance` is operational (stripped on export, preserved from the live node on import) and is
mirrored onto the compile-state satellite, so a control plane can read it through
`GetMeshNodeStream(path)`.

#### Whether the refused bytes keep serving is CONDITIONAL

`Seed` has already stamped the adopted build's assembly coordinates by the time the owner judges it,
so a refusal that changes nothing else leaves proven-stale code executing. Both answers are wrong in
one direction, and the fork is decided at the point of refusal rather than by assuming how a flag is
set on a mesh nobody can see:

- **this mesh can compile** → **clear** the coordinates. The `Pending` flip has already dispatched a
  fresh compile, so the type is unserviceable for seconds — and "marked and still serving" is exactly
  the state that lets an armed control-plane node fire pre-fix code unattended.
- **`Modules:RequirePrebuilt`** → **keep** them, and log `Critical` naming the type. The local compile
  that would replace the bytes is refused by design, so clearing leaves the type with no assembly at
  all, indefinitely — an outage with no recovery path, self-inflicted by a guard. Only a rebake fixes
  it, and the log says so.

> 🚨 Do not collapse this to "`RequirePrebuilt` is unset everywhere". It is measured absent on
> **memex** and **memex-cloud** (#2194 item 3 records the same) — two instances, saying nothing about
> `pearl`, `atioz`, local installs, or any external instance the registry serves.

#### 🚨 The path the fingerprint actually travels — and why it was INERT for months

The comparison above shipped complete, and for months it could not fire. Nothing was broken in it;
the value it compares simply never arrived, and every part of the system reported success:

1. `PrebuiltAssemblySeeder.Seed` had a **seven-parameter convenience overload** that hard-coded
   `sourceFingerprint: null`. Both production callers — `PluginBundleClient` and
   `ShippedPrebuiltBundles` — bound to it, so `AdoptedSourceFingerprint` was never written, the
   first guard in `ApplyAdoptedSourceStamp` short-circuited, and **every adoption on every mesh
   returned `AdoptedUnverified`**. That overload is now `[Obsolete(error: true)]`: passing null is
   still allowed (a legacy bundle genuinely records none) but must be *written at the call site*,
   where a reviewer sees the claim being waived. It is obsoleted rather than **deleted** because
   deleting public framework surface is a breaking change to in-mesh code no compiler can see, and
   the live-mesh sweep AGENTS.md requires could not be completed — `search_chunks` answers
   `"searched": false` on both reachable deployments, which is a FAILED sweep, not a clean one. Both
   repos' node trees were swept by hand and hold no caller, so the symbol stays (nothing already
   compiled breaks) while every source call site now fails loudly, at the call, with the reason.
2. **No producer wrote one.** `BundleWriter.AssemblyEntry.SourceFingerprint` /
   `BundleReader.AssemblyRef` / `BundleReader.Payload` now carry it end to end, and all three bake
   producers record it — the compiler-driven `TreeBake` and `CascadeBuild` from the tree, the
   mesh-driven `BakeOutput` through the same `NodeSources.GetSources` query the owning hub reads.
3. **The live half was computed only when it was already too late to matter.** The owner computed
   `CurrentSourceFingerprint` only "when there is something to compare it against" — a condition
   unsatisfiable in the ordering the incident took. The owner publishes its snapshot first; the
   adoption's patch then lands carrying both the adopted fingerprint and the stamp request, and the
   sources watcher does **not** re-run, because its `DistinctUntilChanged` keys on the source
   *queries*, which did not change. The judgement then read an absent live value and took the
   "inconclusive" branch. It is now computed on **every** publication (a SHA over text already in
   hand, on a path where a Roslyn compile is about to cost four orders of magnitude more), the
   idempotency check includes it so a node persisted before the field existed self-heals on its next
   activation, and the two writers that cannot compute it — the standalone stamp watcher and the
   release-request watcher — now **wait** rather than consume the one-shot request on an absence.

> 🚨 **The lesson is the general one:** *"the fix merged" is not "the fix runs."* A guard whose
> input is never supplied is indistinguishable from a guard that passes. The regression that pins
> this one is `AdoptedBuildSourceStampTest`, which drives a real bundle through
> `BundleWriter → BundleReader → ShippedPrebuiltBundles → Seed → the owner's stamp` on a real mesh and
> asserts all three rows of the table; a unit test over the pure function cannot see any of the four
> links above.

**Producer and consumer must hash identically, or every good bundle is refused.** That equality is
pinned twice. `BakeEquivalenceTest` bakes one content set both ways — through a real mesh (whose
value *is* the consumer-side value) and through the mesh-free tree bake — and asserts the
fingerprints are equal, non-empty, and different for different types. And the PR lane asserts it on
the REAL trees: `bake-then-gate.sh` stages the bake and the gate from the same tree, so an
`ADOPTION REFUSED` line in that run can only mean the two producers hash the same content
differently, and `assert-bake-consumption.sh` fails on it by name.

> 🚨 That second check exists because a false refusal is otherwise INVISIBLE in CI, in the same way
> the original defect was. `Seed` returns `true`, the adopted/declared counts balance (the owner
> refuses *afterwards*, when it stamps), the type flips to `Pending`, recompiles locally, and every
> per-type verdict is green. Only the log says anything — and only because the refusal logs at
> `Error`, above the gate's default level.

#### 🚨 The fingerprint covers the `@@`-include closure — both sides resolve before hashing

An `@@` include pulls a Code node that **no source query matches**. It reaches the emitted assembly
(`NodeSetCompiler.ResolveInputs` substitutes it after the fold) and it used to be absent from the
hash — so editing an included-only snippet moved neither `AdoptedSourceFingerprint` nor
`CurrentSourceFingerprint`, and a prebuilt assembly baked *before* that edit still adopted as
`AdoptedVerified`.

That is the worst of the three rows to be wrong on. `AdoptedUnverified` says "nobody established
where these bytes came from", and an operator reads it as the warning it is. `AdoptedVerified` is an
**assertion** that the shipped bytes match the source this mesh holds — standing over source that
was never hashed, it is a *false verification*, which is the #2813 incident one layer in. So both
halves now resolve includes first (#2948):

- **The producer** already substituted them, so it simply keeps what it pulled in.
  `NodeCompileShaping.ResolveCodeIncludes` takes an optional collector and records
  `resolved path → code text` at the point it consumes each include; `ResolveInputs` hands that back
  on `CompileInputs.ResolvedIncludes`, and `TreeBake` / `CascadeBuild` fold it straight into the
  fingerprint. **No second walk, no second read, and no new blocking bridge** at a synchronous build
  step.
- **The consumer** resolves it through the mesh, in the one place that already holds the live source
  nodes: the sources watcher (`NodeTypeCompilationHelpers.InstallSourcesWatcher`). Resolving a closure
  means mesh READS, which a pure `Update` lambda cannot make — so the fingerprint moved *out* of that
  lambda into an observable step ahead of it — composed with `Switch()`, so a newer source set supersedes an in-flight
  resolution rather than racing it.

The cost is bounded by the shape of the walk, not by the size of the mesh:
`CollectIncludeClosure` scans each source's text for `@@` and **issues no read at all** when there
is none, which is almost every type. Only a type that actually has includes pays, and it pays the
same reads its own compile would.

**Why the walk is order-stable and cycle-safe.** A fingerprint that moves when nothing changed is
*phantom staleness* — endless recompiles, or a false refusal — so three properties are load-bearing:

1. **Order cannot reach the result.** The closure is keyed by *resolved path* and returned sorted
   (ordinal); `PartitionSourceFingerprint` then sorts again. The roots are walked in
   `CollectCompileSources` order — itself ordinal by node path — and **serially**, one `SelectMany`
   chain and never a `Merge`, so there is no read interleaving to observe either.
2. **Suppression is result-preserving.** The per-root visited set skips a path that root already
   walked. Skipping a re-read of the *same* anchored path can only re-derive an entry that is
   already present, so which parent reached a shared snippet first cannot change the closure.
3. **A cycle terminates.** `A → B → A` adds each path to the visited set once; the second visit
   takes the already-added branch, reads nothing and recurses no further. A self-include is the same
   case with one hop.

**And an unreadable include is INCONCLUSIVE, never absent.** This is the direction that would hurt.
The producer's lookup is in-memory and never stalls; a consumer that quietly treated a stalled read
as "the include is gone" would hash a *shorter* closure, which is indistinguishable from a stale
bundle — so a perfectly good adoption is **refused**, and on a `Modules:RequirePrebuilt` mesh that
is terminal and needs a human to rebake. `SourceFingerprintIncludeReader` therefore uses
`GetMeshNodeOutcome` and keeps the three states apart: `Present` contributes, `Absent` contributes
nothing (it contributes nothing to the bytes either — the directive stays verbatim, so both sides
agree), and `Unavailable` / `DeleteInProgress` / a timeout raise
`SourceIncludeUnavailableException`. The watcher catches exactly that, logs it, and **leaves the
previously published `CurrentSourceFingerprint` standing** — the judgement then takes the "nothing
has been compared" branch (`AdoptedUnverified`). Same rule as the emit canary (#890): a probe must
not answer its scariest branch on its own inability to run.

#### Two things this deliberately does NOT do

- **The include closure is NOT added to `CompiledSources` / `CurrentSourceVersions`.** So
  `IsDirty` still does not notice an included-only edit, and the sources watcher — which re-runs on
  its source *query* — does not re-publish until the type's own source set moves or its hub
  reactivates. That is the recompile-*trigger* half, and it is a different mechanism: a change feed,
  not a hash. It is also a producer/consumer contract — the bundle manifest's `sourceVersions`
  mirrors the RAW query match on both bakes, which `BakeEquivalenceTest` pins deliberately (an
  include target is asserted *absent* from `sourceVersions` and *present* in the emitted surface and
  in the fingerprint's input). What #2948 closes is the **verified claim**, which is decided by the
  two fingerprints and nothing else.
- **Refusing to LOAD is the second line of defence.** The damage needs two ingredients — stale bytes
  *and something armed to run them*. A type that renders read-only pages from unverified bytes is a
  degraded system; a type that WRITES from them is the incident. The execute-time half is
  [The Execute-Time Build-Provenance Interlock](/Doc/Architecture/ExecuteTimeInterlock) (#2820):
  a type whose provenance is `AdoptionRefused` is never given a durable, write-capable per-instance
  hub and is never joined into a kernel session's cell surface, while its own pages keep rendering
  so an operator can diagnose it. `AdoptedUnverified` is deliberately NOT refused there either, for
  exactly the reason the legacy row above keeps its stamp.

> `BuildProvenance` is reset to `Compiled` by `ApplyCompileSuccess`: Roslyn built those bytes here,
> from the source this mesh holds, so nothing about an earlier adoption survives a successful local
> compile. Without that reset the field was write-once-per-adoption — a type refused as stale and
> then recompiled kept reading `AdoptionRefused` forever, which turns the operator signal into noise
> and would make the execute-time interlock refuse a type whose source it had just compiled itself.
> `ApplyCompileFailure` deliberately does *not* reset it: after a failed compile the bytes in place
> are still whatever the refusal left, so the refusal is still the true story.

> 🚨 **Do not use `LatestReleasePath` vs the NodeType as a staleness signal.** It lags routinely —
> a healthy type can serve perfectly with its release pointer several versions behind, and a check of
> that shape fires constantly on healthy meshes. The fingerprint comparison above is the signal
> precisely because it does not have this problem.
### 🚨 When the PROCESS cannot emit — a failure that is not a verdict at all

Everything above assumes the compile *found something out*. Sometimes it does not: Roslyn's `Emit`
**throws** instead of returning diagnostics, and from that moment the process cannot emit any
assembly at all. This is [#890](https://github.com/Systemorph/MeshWeaver/issues/890) — a
`NullReferenceException` inside `Cci.MetadataWriter.GetConsolidatedTypeParameters`, reading a
`ContainingType` that the guard immediately above it had just read as non-null.

**The condition is total and permanent, not intermittent.** Measured on run `33322993649` shard 1
(2026-08-30): the first throw landed at 16:42:42.375, and across the remaining 6 m 15 s of that
process **7 of 7** compiles that reached the metadata writer failed identically and **none**
succeeded. Compiles that only needed *diagnostics* kept working perfectly — the deliberately-broken
NodeTypes in `NodeTypeCompileParkTest` still reported their real `CS1040`/`CS0246` codes — so parse
and bind were healthy and only the **emit** was dead. That split is the fastest way to recognise it
in a log: correct `CS####` for broken source, `NullReferenceException` for source that should
succeed.

**Replicated independently in MeshWeaver.Plugins** — run
[`33760859754`](https://github.com/Systemorph/MeshWeaver.Plugins/actions/runs/33760859754) shard 3,
2026-09-03, a `push` to `main`, `MeshWeaver.Hosting.Monolith.Test`. Onset **13:34:23.568**, 1.3 s
into `CodeEditRecompileTest.CodeEdit_ExplicitRelease_IsUpToDate_RecompilesOnSourceChange` and 33 s
into the process; it is the **first** `NullReferenceException` in the whole trace, so it is not
downstream of an earlier fault. Then **39 of 39** compile faults over the next 13 m 21 s were that
same NRE with `canary=BELOW-ROSLYN` — **zero** `CompilationException` — across 12 unrelated node
paths and partitions, and `BrokenNodeTypeAccessTest` still **passed** at +13 min with correct
`CS####` diagnostics. Memory was flat (managed 147 MiB / RSS 961 MiB) through the final wedge, and
the ten test hosts that ran after it **on the same runner were clean**, so the condition is
process-scoped, not machine-scoped. Two repos, two harnesses, two shard layouts, one shape.

🚨 **Onset is not a warm-up threshold.** Core's occurrence fired 130 s and 199 `TEST START`s into the
host; this one fired at 33 s and 12, with `alc=1`, `asm=138`, `gc2=4` — no ALC churn, no memory
pressure, and the two ALC-heavy suites (`NodeTypeRecompileAlcLeakTest`,
`NodeAlcUnloadTeardownOrderingTest`) ran **after** the onset, not before it. Any hypothesis that
needs a precursor workload has to explain that.

**It is not a MeshWeaver defect and no compile-side change fixes it.** `EmitPipeline`'s canary
answers that at the first throw, in two legs: re-emit a trivial, freshly parsed, known-good
compilation against the same references, then — if that fails too — against an image-backed CoreLib
that shares neither the `MetadataReference` instances nor their file mappings. `REFERENCES` and
`BELOW-ROSLYN` both mean **the control could not emit either**.

#### What that changes about the verdict

A compile that aborts this way has learned **nothing** about the code it was handed, so recording
`CompilationStatus.Error` states a verdict that was never formed — and it is durable: the same write
stamps `FailedBuildInputs`, the re-drive above reads it back as *"formed under exactly the live
inputs"*, and declines. The type is left saying *"your code is broken"* about code nothing evaluated,
retried by nothing until a human presses **Compile** or an input genuinely moves.

So `IsAvailabilityNonVerdict` treats it as the third availability fact, beside an unestablished
source set and a recycling address: status `Unavailable`, no verdict recorded, the park budget
untouched, and the type re-drivable — which is what lets a later, healthy process compile it. The
bake gate filing this as *unevaluated* rather than as a code regression is the correct reading: a
bake whose process cannot emit has evaluated nothing.

🚨 **The predicate reads the VERDICT, never the presence of a canary.** Every emit-phase throw
carries one, and three of the five verdicts deliberately withhold the claim — `OK` (the control
emitted fine against the same references, so the fault *is* this compilation's inputs — a genuine
`Error`), `INCONCLUSIVE` (the control could not be built, so leg 2 never ran) and `DIVERGENT` (both
legs failed, in different frames). Widening this to "any infrastructure fault" is the blind spot
`SourceSnapshotEstablishmentTest.EveryOtherCompileFailure_StillStampsError` exists to refuse.

#### Reading it in CI — one event, many names

One poisoned process reports as up to ten unrelated test failures; across 2026-08-22→08-29 that was
**9 events wearing 37 distinct test names**, 23 % of all failing test names in the week. Every
occurrence has cost a fresh and always-identical misdiagnosis. In run `33322993649` all five
failures — three `NodeTypeCompileParkTest`, one `CodeEditRecompileTest`, one
`ReleaseRequestWatcherHighWaterTest` — were this one event, and the shard's `exit=124` was its
consequence, not a second problem.

Two readings that look right and are not:

- **"The retry compiled stale source."** It did not. The retry runs a real compile; the broken
  source's `CS####` lines in the same test's output belong to the *first* compile, before the fix was
  written. The retry's own error is the `NullReferenceException`. Compare timestamps, not adjacency.
- **"That one is just a slow shard."** `ReleaseRequestWatcherHighWaterTest`'s compile threw **174 ms**
  after `TEST START`; the 50 s wait that followed was waiting for an `Ok` that could never arrive.

> 🚨 **The core dump the canary asks for cannot be captured this way.** `DOTNET_DbgEnableMiniDump`
> fires on a *signal*, and this process never crashes — it throws a managed exception and keeps
> running until CI's per-suite wall-clock cap kills it (`exit=124`, no dump): 8 minutes on core's
> shards, **15 minutes** on MeshWeaver.Plugins' `Portal hosts` shards, so the cap is a property of
> the harness and not a fingerprint to grep for. That is why weeks of
> "capture a core dump" produced none. Its SIGSEGV twin,
> [#613](https://github.com/Systemorph/MeshWeaver/issues/613), *does* dump — see
> [Debugging Native Crashes](/Doc/Architecture/DebuggingNativeCrashes) for the invariant the two share (a single
> 8-byte word reading exactly zero while its block stays coherent) and for how to read one.

### Framework-version freezing

A compiled NodeType DLL references the MeshWeaver framework assemblies present
at compile time. When MeshWeaver is **redeployed at a new version**, those
assemblies change and the cached DLL may be ABI-incompatible — so a release is
only usable while the framework version matches.

`RunCompile` stamps `NodeTypeDefinition.CompiledFrameworkVersion` with
`NodeTypeCompilationHelpers.FrameworkVersion` on every success. That value is
resolved once per process (`FrameworkBuildIdentity`,
[#1660](https://github.com/Systemorph/MeshWeaver/issues/1660) WS3):

- **Hosts with a surface manifest** (the portals and the CI bake host, which
  build with `MeshWeaverSurfaceManifest=true`) — the **API-surface identity**
  `s<hash>`: per compile reference, the SHA-256 of its *reference assembly*
  (the compiler's own definition of the API surface — byte-stable under
  body-only and private-member edits, changed by any surface change), hashed
  over the canonical content-surface set, with the generated-input-shaping
  exceptions contributing their full implementation MVID: the toolchain roots
  `MeshWeaver.Compiler` (THE compile toolchain since #1707 — skeleton
  generation, source-query resolution, include shaping, aggregation, options,
  generator execution, emit) and `MeshWeaver.NuGet` (the `#r "nuget:"`
  parser/resolver), plus their computed MeshWeaver dependency closure — the
  toolchain calls into what it links, so a body-only change in a closure member
  (Mesh.Contract, ContentCollections, …) must recompile content too.
  "Rebuild only when we need to": an internal-only
  framework release keeps the identity, so every cached and CI-published build
  stays valid; a breaking surface change mints a new one, and the CI bake for
  the new surface seeds at boot instead of recompiling per pod.
- **Manifest-less CI processes** (test hosts) — the **commit identity** `g<sha>`
  stamped as `AssemblyMetadata("MeshWeaverFrameworkIdentity")` by
  `Directory.Build.props` (CI compile inputs are commit-deterministic — no run
  number or timestamp reaches any compiled attribute). Kept everywhere as
  logged PROVENANCE.
- **Manifest-less local builds** — the identity anchor's **MVID**
  (`MeshWeaver.Compiler.dll`; a content hash of the compiled module, no stamp
  present). Content-exact for a dirty working tree — stable across rebuilds
  that don't change the toolchain's bytes, changed whenever they do. Note that
  every host compiling against a *persistent* assembly store (the portals, the
  CI bake host, mw-plugin-test) ships a surface manifest and resolves the
  surface identity even locally — the MVID fallback governs test hosts and
  ad-hoc tools.

On a framework-version mismatch the NodeType recompiles and **mints a new release**
for the new framework. The old release is left intact as history so instances still
loaded on it keep running until they cycle.

### 🚨 ONE POD BAKES — and cluster membership, not a clock, decides when another may take over

Rule 3 makes every pod on a new image discover the same framework-stale cache at once. The cache is
shared but the *decision* to rebuild is per-process, so with `maxSurge` during a rollout — or any
`replicas > 1` — every replica independently starts the same sweep over the same NodeTypes into the
same volume. That is not merely duplicated work: it is concurrent cold Roslyn compiles of the SAME
type, which is precisely the storm the sequential, dependency-ordered sweep exists to prevent (four
of them on memex, 2026-07-28 04:05, dropped six plugin roots to the "did not settle" overlay and
needed a scale-to-zero).

Coordination is the **build protocol**: candidates register a claim on the `Admin/Build` node and its
own hub grants exactly one, while everyone else waits on the per-fingerprint GO. There is no lease
file any more — [BuildCoordination](/Doc/Architecture/BuildCoordination) is the mechanism, and its
"[Who becomes the build master](/Doc/Architecture/BuildCoordination#who-becomes-the-build-master)" section carries the
takeover rule: **cluster membership decides, not a clock** — gone → take over immediately, alive →
never take over however old the heartbeat looks, unknown → the `ClaimStaleAfter` fallback for hosts
with no cluster.

**What the claim does NOT fix.** `NodeTypeBatchBake.WriteStamp` is a read-modify-write at
`NextVersion`, and the monotonic write guard bounces the loser (`compile-state stamp … was REFUSED`),
so the bytes land on the share while the record does not name them. The claim removes one of the
two writers that can race there — a second baker — but the other is the type's OWN per-node hub,
which stamps compile state from the activation path, the sources watcher and the release watcher, and
is legitimately live while a batch bake runs. The loser's outcome is already correct: the bytes are
durable and content-addressed, the record stays pending, and the level-triggered probe (which asks
the STORE, not the record) re-bakes and re-stamps on the next pass.

### 🚨 That means one whole GENERATION of assemblies per deploy — and the store never removed one

`FileSystemAssemblyStore` keys every file `v{version}-{frameworkTag}-{contentHash}.dll`, where the
tag is the first 8 chars of the framework version. That tag is what stops a new image loading the
previous image's bytes (`BadImageFormatException` → failed grain activations → a portal-wide wedge,
prod 2026-06-20) — so a **fresh set of files for the whole fleet on every published build is
correct**, and making the tag stable is not the fix.

What was missing is anything that ever removes an old set. Measured on memex 2026-08-12:

| | |
|---|---|
| DLLs under `/data/assembly-cache` | 7817 |
| distinct framework generations | 93 |
| size | 3.2 GB of a 16 GiB share |
| **loadable by the running image** | **83 files — 1%** |

The share is not private to the compiler. `/data` also holds the **DataProtection key ring**
(`/data/dataprotection-keys`), the NuGet package cache and the Graph storage base path, so filling it
is not a contained failure: key persistence and the compile cache fail together, and a full SMB share
reports write failures far from their cause.

#### What proves a generation is unreferenced

Not its age, and not a count. A pod's generation is fixed for its whole life (it is its image's
MVID), and a fully warm pod can go days without touching the share — so **file age says nothing at
all about whether a generation is in use**, and a count is blind to a pod that has not rolled inside
the window.

The proof is a **live claim**. Every process that owns a filesystem assembly cache re-asserts
`{root}/.generations/{tag}/{holder}` on an interval, and a generation any live claim names is never
collected. The claim's instant is written into the file's **content**, not its last-write metadata:
SMB metadata caching can report a timestamp that is stale while the holder is alive, and a
falsely-stale claim is the one misreading that deletes a live generation.

Every rule is a KEEP rule and they are ORed — a generation survives if **any** holds:

| Keep rule | Covers |
|---|---|
| it is the sweeping process's own framework | a failed claim write must never let a pod delete what it is loading |
| a claim younger than `ClaimTtl` (24 h) names it | another pod is still running that image |
| it is among the `KeepGenerations` (3) most recently written | rollback headroom — and the rollout that first introduces claims, where the outgoing image is not asserting one yet |
| its newest file is younger than `MinimumAge` (7 d) | a backstop bounding what a wrong answer from either of the above can do |

Anything the sweep cannot attribute to a generation — the claim files, an untagged pre-2026-06 DLL,
any foreign file — is counted and **never deleted**, and any error reading the tree or the claims
**aborts the sweep with nothing collected**. Note the polarity: the coordination path around the bake
(the build-protocol claim) fails **open**, because being wrong there costs duplicated work; this one
fails **closed**, because being wrong here deletes an assembly a live pod is about to load.

Deletion is **off by default** (`AssemblyCache:Retention:Delete`). Until it is armed the sweep
measures the cache and logs exactly what it would remove — which is the evidence a deployment should
arm it against.

Code: `AssemblyCacheGenerations` (`src/MeshWeaver.Graph/Configuration/AssemblyCacheRetention.cs`),
wired by `AddAssemblyCacheRetention` (`src/MeshWeaver.Hosting/AssemblyCacheRetentionHostedService.cs`).
`BlobAssemblyStore` is unaffected: it keys `v{version}` with no framework tag, so a new image
overwrites rather than accrues.

#### 🚨 The cache grows on a SECOND axis, and generation retention is blind to it

Generations are one axis. The other is **per-type version accumulation *inside* one generation** —
one dll/pdb pair per recompile, forever, every file carrying the same tag. Measured on memex-cloud
2026-08-22, when the 16 GiB `/data` PVC hit 100% and every NodeType recompile failed with
`No space left on device` (surfacing four steps away as `compilationStatus: Error`, while the
migration pod crash-looped 66 times):

| | |
|---|---|
| files in `Store_Plugin`'s directory alone | **4,184** (`v100` … `v8800+`, since June) |
| framework generations they span | **one** |

Keeping three *generations* of that shape still keeps ~12.5k files, so no setting of
`KeepGenerations` could ever have been the answer. The collector for this axis is therefore in the
**writer**: after `FileSystemAssemblyStore.PutWithLocation` publishes a new version it prunes that
type's directory to the newest `KeepVersionsPerType` versions (default **3**, override
`AssemblyCache:Retention:KeepVersionsPerType`). The pass that made the directory grow is the one
that trims it, so nothing has to walk the tree to discover the growth.

**Why this one may delete on defaults while the generation sweep may not.** Different worst cases:

| | generation sweep | per-type eviction |
|---|---|---|
| removes | a whole framework generation | older versions **within the writer's own generation** |
| whose bytes belong to | possibly **another image**, on another live pod | this image |
| worst case of a wrong answer | `BadImageFormatException` → failed grain activations → portal wedge | a cache **miss** → `TryGetAssemblyPath` returns null → activation recompiles |
| therefore needs | a live **claim** before deleting; armed by an operator | nothing beyond staying inside its own tag |

Eviction never crosses the tag boundary, only removes names
`AssemblyCacheFileName.Parse` attributes to this store (so `.tmp-*` leftovers, bake leases, claim
files and pre-tag legacy DLLs are untouchable), never removes the file it just wrote, and treats a
delete that throws as "leave it alone" — a file that will not unlink is one something is holding.
Both collectors share that one parser deliberately: two collectors disagreeing about which names this
store wrote is how one of them would remove a file the other treats as foreign.

### 🚨 In-MEMORY generations accumulate the same way — a superseded build stays ROOTED while any instance serves it

The store section above is about disk; the same generation arithmetic plays out inside every
process that hosts instance hubs, and there the unit is an **AssemblyLoadContext**. Every publish
of a usable build mints a new collectible ALC; *collectible* only means "may unload once nothing
roots it" — and a serving instance hub roots the build it bound. The stale-build banner ("a newer
build of this type is available — Recycle") deliberately leaves every instance on its old build
until a human clicks, so in a sync-heavy window each publication wave stacks one more full
assembly generation — types × Roslyn artifacts — onto the silo hosting the type hubs. The
eviction fix on the compile path (#605) cannot free any of it: eviction drops the store's
reference, not the instance's.

Measured, 2026-08-25 (issue #2194): two memex-cloud silos flat at 2.7–4.5 GB for five hours, then
a hard inflection the moment the first scheduled bake tick after a framework-pin bump published a
full-catalog rebake, followed by five content merges — six publication waves in four hours. The
two type-hosting silos climbed ~2.5 GB/h to 17–20 GB and four cores of GC; every other replica
stayed ≤3.6 GB. Nothing intervened: no OOMKill (the pod limit was never reached), no probe
failure (after startup, readiness and liveness both watch `/alive`, a process-up check a
thrashing pod still answers), no alert — the pods served hung requests as Ready for 3½ hours
until a human read `kubectl top`. The same stranded instances also produced the visible half:
old and new assemblies serving side by side (`$type` registration mismatches), pages wedged until
the type and instance hubs were recycled by hand.

The missing piece is **convergence**, and it is policy, not plumbing:
`Modules:AutoRecycleOnStaleBuild` (#2192, default **off**) turns the banner's offer into an
automatic self-recycle — when a NodeType publishes a usable build whose assembly differs from the
one an instance bound, the instance posts its own `DisposeRequest`, re-activates on the new
build, and the superseded ALC unroots and collects. Anywhere the catalog updates itself — every
self-updating portal — leaving the key off means choosing the accumulation above; #2194 tracks
the prod enablement. The structural end state is stronger still: DLL-only module adoption
("never ship uncompiled state") removes in-portal recompilation altogether, so a publication
wave costs an assembly load instead of a Roslyn generation.

**Triage fingerprint** — intermittent hangs while most requests succeed, on a portal that
recently synced or baked: suspect a degraded-but-Ready replica, not a global wedge. One or two
pods far above their siblings in BOTH memory and CPU in `kubectl top pods` is this incident;
`kubectl delete pod` them (grace-drain — the Deployment replaces them) and read #2194.

### 🚨 A LEAVING pod never touches shared NodeType state — the adoption sweep observes host shutdown

The NodeType node is **one record for the whole deployment**. Every generation of pods reads its
assembly coordinates, and every generation's adoption sweep writes them — which is fine while every
writer is a pod that will go on serving the type, and a clobber the moment one of them is not.

**What was measured (issue #3129, memex.systemorph.com, 2026-09-02).** A rollout left the old pod
terminating for 27 minutes (`terminationGracePeriodSeconds=1800`, circuits still held, 11.8 GB,
3–5 s GC stalls every ~4 s). While draining it kept running the prebuilt-bundle adoption sweep
against the shared NodeType nodes, and Loki shows the loop verbatim:

```
ADOPTION REFUSED for Underwriting/Workbench (#2813): bundle fingerprint aa0e63c8… vs live 572495ee…
→ coordinates cleared, Roslyn compile dispatched
→ LATE_NACK_TERMINAL code=Unknown TimeoutException (+10 s)   [UpdateQueue] FAILED path=Underwriting/Workbench elapsedMs=12344
→ ShippedPrebuiltBundles: seeding … did not complete — the sweep compiles it instead
→ next access repeats (Workbench/Guideline every 30–90 s; Crm/Client 388× in 30 min)
```

All 1424 `ADOPTION REFUSED` and 350 `[UpdateQueue] FAILED` lines of the window were on the dying
pod; the two new pods logged 0 of either. **The cross-generation effect IS the reported
navigation hang:** because the old pod kept clearing `Underwriting/Workbench`'s coordinates, the
NEW pods logged `Overlay self-heal: instance 'Underwriting/Desk' still stuck on NodeType
'Underwriting/Workbench' after 120s` → self-heal compile → Release, ~2 minutes later. A ≥120 s
hang per Underwriting type, per roll — three rolls that day.

**The clobber, step by step.** `PrebuiltAssemblySeeder.Seed` stamps the bundle's assembly
coordinates onto the node *first* and asks the owner to judge the adoption *second*
(`RequestedSourceStampAt`, #1834 — the seeder's snapshot may predate the owner's source
publication, so only the owner can compare fingerprints). When the bundle is stale the owner
**refuses** (#2813) and, because the coordinates on the node at that moment are the *rejected*
bundle's, it **clears** them so proven-stale bytes stop executing — that clear is required by the
#2813 design and stays. The build that was actually serving on the healthy pods is already gone at
that point: the stamp replaced it one write earlier. Each access on the draining pod re-ran the
sequence (the Pending flip's on-demand adoption), so the shared record was wiped every 30–90 s for
as long as the pod lived.

**Why the grace period is the amplifier, not the cause.** Thirty minutes is how long the wrong
writer stayed alive; the defect is that it wrote at all. Cutting the grace period would shorten the
window and leave the same clobber in it — a band-aid.

**The rule.** *A hub that is leaving does not START a sweep pass, does not STAMP, does not CLEAR
coordinates, and does not DISPATCH a compile through the refusal path.* The predicate is
`hub.IsLeaving()` (`HubLeavingExtensions`, `MeshWeaver.Mesh.Contract`), and it reads **two**
signals:

- `IMessageHub.IsShuttingDown` — this hub's own teardown or an ancestor's cascade; the shape #3109
  gave BuildupActions and `SubscribeHubWatcher` gives emissions.
- `IHostApplicationLifetime.ApplicationStopping` — the **process** is leaving. This is the signal
  that was live for the whole window above and the reason the hub signal alone would have changed
  nothing: the mesh is disposed by `MeshTeardownHostedService.StoppedAsync`, i.e. at the very END
  of host shutdown, after Kestrel has finished with the circuits the pod still holds. Every one of
  the 1424 refusals was issued by a watcher `SubscribeHubWatcher` would have dropped had
  `IsShuttingDown` been true — so on a draining pod it was false the entire time. It is the same
  token `OrleansRoutingService` already consults for its shutdown routing decisions.

It is asked at four places, and every route into adoption converges on them: the start of a
`ShippedPrebuiltBundles` pass (no pass starts — boot, on-demand from the compile watcher, a push's
recompile, an install); `PrebuiltAssemblySeeder.Seed` at **subscribe** time (the seeder runs one
`Seed` per assembly under `Concat`, so a pass in flight stops at its next node boundary) and again
inside the write lambda (an unchanged node is a no-op `Update`); and
`ApplyAdoptedSourceStampAndReport` on the **owner**, reached from the stamp watcher, the sources
watcher's fold and the release watcher's fold — a leaving owner judges nothing and leaves the
request standing for the owner activation on a pod that stays. The answer everywhere is the
caller's ordinary "not adopted / nothing to do" signal, so nothing is ever parked on it.

**Decline before you write.** The seeder also now refuses to *start* the stamp-then-refuse
sequence when the refusal is already decidable from the owner's snapshot in hand: a bundle whose
`SourceFingerprint` disagrees with a **published** live `CurrentSourceFingerprint` is declined
before any write, exactly like a framework or dependency mismatch, and the live build's
coordinates are never replaced. The owner's three-way check stays for the pre-publication window
the snapshot cannot decide (no live fingerprint yet) and as the last line of defence. A decline is
always safe — a compile follows; a write a refusal must undo is not.

**What a leaving pod still does, deliberately.** A user still held on the draining pod who opens a
type with no usable build gets a compile dispatched by the ordinary first-access kickoff — that
result is a *new* version, clobbers nothing, and #3115 made its result write land under the pod's
own flush bound. Only the refusal-driven clear-and-compile, whose sole effect on a shared record
is destructive, is withheld.

**Triage fingerprint** — `Overlay self-heal: … still stuck on NodeType … after 120s` on the new
pods right after a roll, while a pod of the *previous* ReplicaSet is still `Terminating`: read
that pod's log for `ADOPTION REFUSED`. Pinned by `LeavingHubAdoptionSweepTest`
(`MeshWeaver.Compiler.Pipeline.Test`): the same seed on a shutting-down hub leaves the shared
record byte-for-byte untouched, and on a live hub adopts.

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

### A sweep that ERRORED is not a sweep that passed

The leniency above is about *individual types* the sweep could not evaluate. It does **not** extend
to the sweep itself failing. If the ENUMERATION errors or times out, the pod has verified nothing at
all, and that is `BakePhase.Faulted` — **refuses readiness**, exactly like a regression:

| Sweep terminal | Phase | `/health` | Why |
|---|---|---|---|
| Enumerated, every type settled | `Complete` | Healthy | the bake ran |
| Enumerated, found **zero** types | `Complete` | Healthy | emptiness is an *answer*; a fresh mesh must serve |
| Enumeration **threw / timed out** | `Faulted` | **Unhealthy** | nothing was measured — there is no bake to certify |
| Pre-warm switched off | `NotStarted` | Healthy | nothing is being measured *and nothing claims to be* |

The last two rows are the whole point, and they are easy to conflate. The health check's documented
policy — *"fail CLOSED on a regression, fail OPEN on 'not running'"* — is scoped to `NotStarted`,
i.e. the gate is **disabled** ("a configuration mistake must never black-hole a pod"). A sweep that
**errored** was armed and measuring and simply failed to, which is a different thing; the same
switch already reports `Running` — a pod that knows strictly *more* than a faulted one — as
Unhealthy.

`WarmDynamicTypes` used to swallow the enumeration fault and return `Observable.Empty`, so both
"found nothing" and "could not look" reached the subscriber as the same empty completion and the
gate marked itself `Complete` → Healthy. The retired pre-run bake Job carried the counterpart guard
from outside the portal — *"FINDING NOTHING IS NOT PASSING … a gate that certifies 'I verified
nothing' is worse than no gate"*, exit 3, with a `Bake:AllowEmpty` escape — and named that `Catch`
as the reason it had to. Retiring the Job (#1357) removed the guard; the distinction now lives at
the source, where it can be made honestly.

**Cold start.** On a roll this is free: `maxSurge:1 / maxUnavailable:0` keeps the old pod serving
while the new one refuses. On a *first* deployment there is no predecessor, so the pod fails its
`startupProbe` and restarts — re-running the sweep each time, which is what clears a transient cause
without anybody adding a retry. That is the same trade `Regressed` already makes, and the likeliest
environmental cause of a blind enumeration (an unmigrated database) is refused earlier and more
precisely by `DbVersionGate`. The escape hatch is `PreWarm:AllowUnprovenBake` — it relaxes the
verdict only, never the record: the phase stays `Faulted` and the payload keeps saying the bake was
never proven. It cannot waive a real regression.

### The obligation on framework changes

Removing or renaming any public framework surface — extension methods on
`MessageHubConfiguration` / `IMessageHub`, `Controls.*`, `host.*` helpers, content base types — is
a **breaking change to code the compiler cannot see**. Before deleting one:

1. `grep -rn "<Symbol>" content samples/*/Data` — catches in-repo node source. **Grep the node
   JSON too, without a `--include='*.cs'`**: a `Code` node stores its C# in a `content.code` string
   and a NodeType stores its `configuration` lambda the same way, so a `.cs`-only sweep reports
   *"no callers"* over a page full of them.
2. **Grep every OTHER node repository** — `MeshWeaver.Education`, `.Plugins`, `.Reinsurance`,
   `.SocialMedia`, `.Manufacturing`, `Memex`. A symbol on the cell surface is called by bare name
   from content this repository has never seen.
3. Search the **live mesh** (`search_chunks`) — catches nodes that drifted from the repo.
   🚨 An answer carrying `"searched": false` is a **failed** sweep, not a clean one: that
   deployment has no embedding provider, nothing was searched, and the envelope deliberately
   carries no `count` so an absent field cannot be read as "no callers". Sweep on another
   deployment or stop.
4. Port or delete the callers in the same change, then sweep.

#### 🚨 A cell-surface symbol has no retirement that ends in a delete

Steps 1–4 can reach zero and still be wrong, because they only cover content **somebody can edit**.

Installing a course or an app **copies** its nodes into the installer's own space. The copy is a
snapshot: the cell inside it is that person's content, its code is whatever it said on the day they
installed, and no repository change and no plugin update rewrites it. So for a type published with
`cellSurface: true` — reachable by bare name from every kernel session in the mesh — there is no
state a caller sweep can establish that means "nothing calls this any more".

That is not a hypothetical either. #975 retired `TrainingSimResponder` on a "no callers left"
reading of core and `MeshWeaver.Plugins`. Thirteen callers were live in `MeshWeaver.Education` (five
of them C# inside a JSON string), and behind those an unknown number of installed copies. Every
live prompt cell in the AgenticEngineering course failed with `CS0103: The name
'TrainingSimResponder' does not exist in the current context`, showing learners a page that promised
a prompt box and a ✨ button and rendered neither. Fixing the course repaired the central pages and
every future install and **nothing already installed** (Plugins#1258).

**And the copies cannot be enumerated, by design.** A learner's copy lives in *their* partition, so
no sweep any agent can run may read it — row-level security is doing exactly its job. Measured on
memex: `rbuergi/AgenticEngineering/Introduction/Exercise/AskForATable` is a **2026-08-13** snapshot
that still carries the cell INLINE (a ```` ```csharp --render Chat ```` block in the markdown body
plus a `codeSubmissions` entry) — a shape the central course stopped using on 2026-08-21, when the
cell became a separate `Source/Chat` node. That copy calls `TrainingSimResponder.Live`, and it is
merely the one copy the sweeper happened to own. So "how many callers are left" is not a hard
question here; it is an **unanswerable** one.

**So the last step is a forwarder, not a delete.** Keep the old name on the same cell surface as a
shim that delegates to the successor, `[Obsolete]` as a *warning* — never `error: true`, which would
break the very copies it exists to keep compiling, and warnings never reach a cell anyway, since the
kernel only surfaces `CompilationErrorException`. Pin its surface with a test in the NodeType's
`Test/` area so the shim cannot be narrowed by accident; a parameter quietly dropped from a
forwarder is the same invisible break one layer down. The shim may go when the installed copies are
gone — which is a fact about a running mesh, not about a repository.

**Store Repair is not a substitute for the shim.** A learner *can* pull a page back from the central
course — the per-item repair dialog (`Store/Installer`, with `ContentFingerprint` telling *missing*
from *unmodified* from *edited*) exists precisely so one broken exercise does not mean reinstalling
a course. But it is the wrong remedy for a break the platform caused: it asks every affected learner
to notice a dead page, go looking for the dialog, and confirm an overwrite that **discards their own
work** on any page they have edited. Repair is for a copy the learner damaged. A forwarder is for a
copy *we* damaged.

**ADDING a symbol has the same hazard, in reverse.** In-mesh source that references a brand-new
framework helper compiles only once the image carrying it has actually shipped — and node content
reaches a portal by a completely different route from the image (a GitSync/plugin sync, or an MCP
edit, either of which can land first). Merging both halves in one PR does **not** make them arrive
together.

That is not hypothetical: #1386 moved a copy-pasted Article extractor into compiled framework code
as `MarkdownBody.Of`, and the in-mesh callers referencing it went out before the image did.
`ACME/Article`, `Cornerstone/Article` and `Northwind/Article` sat at `CS0103: The name
'MarkdownBody' does not exist in the current context` on memex-cloud until the portal self-updated
to the image that contained it, at which point all three returned to `Ok` on their own.

So when a framework change and its in-mesh callers ship together, **the framework half must land
first**, and the content half is only safe once the target portal reports the image that carries
it. The failure is invisible to CI in exactly the same way a deletion is, and it reads identically
to a content defect.

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
| Delete or rename a public framework API | Grep `content` + `samples/*/Data` + **every other node repo, JSON included** and search the live mesh (`searched:false` = failed sweep) — CI never compiles in-mesh source |
| Delete a symbol published with `cellSurface: true` | You cannot. Installed copies call it and nobody can edit them — leave an `[Obsolete]` forwarder and pin its surface with a test (#1258) |
| Add a framework API that in-mesh source will call | Ship the framework half FIRST; the content half is safe only once the portal reports the image carrying it (#1386) |
| Check the mesh is shippable | `Search('nodeType:NodeType')` → `LspDiagnosticsForNode` per type → every one reads `Ok` |
| Understand why one bad NodeType took the portal down | `CompileError` → dependents `UpstreamFailed` → readiness refused → 60 s hub-activation faults |
