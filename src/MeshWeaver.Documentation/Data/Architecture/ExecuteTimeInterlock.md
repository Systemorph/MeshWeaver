---
nodeType: Markdown
name: The Execute-Time Build-Provenance Interlock
category: Architecture
description: Why a control plane refuses to ARM a NodeType whose build is proven stale, which trigger surfaces that covers, why the refusal is not read-vs-write, and how an operator sees and clears it.
icon: /static/NodeTypeIcons/shield.svg
---

# The Execute-Time Build-Provenance Interlock

> Refusing to **load** stale bytes is the second line of defence. Refusing to **run** them is the
> first.

[Adopting a prebuilt assembly](/Doc/Architecture/NodeTypeCompilation) is what makes installs and
restarts cheap. It is also the one path that could make a NodeType assert something nobody
established: on 2026-08-30 a GitSync `update` pulled new source, adopted a prebuilt built from older
source, reported success, and the stale code destroyed four client documents' bodies — one
unrecoverable ([#2813](https://github.com/Systemorph/MeshWeaver/issues/2813)).

`BuildProvenance` (that issue's fix) makes the state *visible* and refuses a provably-stale adoption
at load time. This page is the other half
([#2820](https://github.com/Systemorph/MeshWeaver/issues/2820)):

> The damage needed TWO ingredients — stale bytes, **and something armed to run them.** Stale bytes
> sitting unloaded harm nobody.

## The verdict is three-valued, and the middle one is the important one

`NodeTypeExecutionGate.Evaluate(NodeTypeDefinition?)` answers with exactly one of three, never a
boolean:

| provenance | verdict | why |
|---|---|---|
| `AdoptionRefused` | **Refused** | The bundle NAMED the sources it was built from and they are not this mesh's. Proven stale — the one hard refusal. |
| `AdoptedUnverified` | **Permitted** | 🚨 A legacy bundle carries no fingerprint, so nothing was compared. **Unknown is not proven-stale.** |
| `AdoptedVerified` | Permitted | Fingerprints compared and equal. |
| `Compiled` | Permitted | Roslyn built these bytes here, from this mesh's source. Also the zero value, so a record written before the field existed reads honestly. |
| *(definition unreadable)* | **Inconclusive** | No verdict was reached. Neither a clean bill of health nor a refusal. |

> 🚨 **`AdoptedUnverified` must never be folded into `AdoptionRefused`.** Every bundle published
> before producers recorded a source fingerprint adopts as unverified. Refusing those would park
> every legacy type on every mesh — and on a `Modules:RequirePrebuilt` mesh a local compile is
> refused *by design*, so there would be no recovery path at all. That is precisely the outage
> "refuse every unproven bundle" was rejected to avoid, arriving through a different door. The same
> reasoning governs the legacy row in `ApplyAdoptedSourceStamp`, and it is pinned by
> `NodeTypeExecutionGateTest.AdoptedUnverified_IsPermitted_TheAntiOutageProperty` plus the
> `AnUnverifiedAdoption_StillArms_TheAntiOutageProperty` rows of `ExecuteTimeInterlockTest`.

> **Where the predicate lives.** `NodeTypeExecutionGate` is in **`MeshWeaver.Compiler.Pipeline`**,
> not `MeshWeaver.Graph.Contract` — even though the latter is where `NodeTypeDefinition` now lives
> and would be the tidier home. `Graph.Contract` is inside `MeshWeaver.Compiler`'s reference closure,
> which is a full-MVID toolchain root, so a body-only change there re-bakes every NodeType on every
> mesh. The pipeline is surface-hashed and is referenced by both enforcement sites, so it carries the
> predicate at no rebake cost. See [Graph / Compiler Layering](/Doc/Architecture/GraphCompilerLayering).

`BuildExecutionVerdict.Inconclusive` is a separate member for the reason `ErrorType.Unavailable` is:
a probe must not answer its scariest branch — or its friendliest one — on its own inability to run.
A boolean gate would force the caller to pick, and both picks are wrong.

## Where a NodeType's compiled code can actually run

A census, because *a check in only some trigger surfaces is worse than none — it makes the gap look
closed*. Every surface below either loads a NodeType's assembly or invokes something out of it.

| # | surface | what it does | gated? |
|---|---|---|---|
| 1 | `NodeTypeEnrichmentHelpers` — hot activation path | Binds the type's `HubConfiguration` onto a **per-instance node**; `MonolithRoutingService` / `MessageHubGrain` then build the real hub from it | ✅ |
| 2 | …its pinned-release branch | Same, from `RequestedReleasePath` | ✅ (same check, above both) |
| 3 | `NodeTypeContractHandler` — legacy `GetCompilationPathRequest` | Hands a configuration to an activating instance | ✅ (via the same node state) |
| 4 | `CellSurfaceAssemblyProvider` — the kernel cell-surface join | Loads the assembly straight through `NodeAssemblyLoadContext` so **every script submission in the session can call its functions by bare name**, with full write access | ✅ (its own check) |
| 5 | `NodeTypeDataModelAreas.ProbeInstanceModel` | Renders the type's `$Model` / data-model page through a **transient probe** hub | ❌ deliberate |
| 6 | `MeshDataSource.HandleNodeTypeSchemaRequest` | Answers one `SchemaReference`, transient probe | ❌ deliberate |
| 7 | `MeshOperations.ReadFromContentType` | Schema validation for agent-facing tools, transient probe | ❌ deliberate |
| 8 | `NodeTypeBatchBake` / `DynamicTypePreWarmer` | Boot-time compile/pre-warm sweep — produces `Compiled` provenance by construction | n/a |

Surfaces 1–3 all pass through `ApplyStreamResult`, so **one check above every assembly-resolving
branch covers them**; that is where the gate sits. Surface 4 is architecturally separate — it never
enriches and never builds a `HubConfiguration` — so it carries its own check rather than an
inherited one.

## Read vs write: the split the issue asked for is not available at the type level

The natural design would be "reads keep serving, writes refuse". It is not implementable here, and
the reasons are worth stating rather than rediscovering:

- **Every surface executes assembly code, including the read-only ones.**
  `MeshNodeCompilationService.CompileResultFromAssembly` loads the assembly and then calls
  `Activator.CreateInstance` on every `MeshNodeProviderAttribute`-derived type it finds — *before*
  any caller has decided whether to use the result. A "read-only render" already runs the
  assembly's constructors and static initialisers.
- **A rendered layout area is not write-free.** Areas carry `WithClickAction`, the `Edit` macro
  binds a writable editor, and a `WithInitialization` watcher installed by the same configuration
  fires unattended. "Renders a page" is not a property that implies "cannot write".
- **Tagging the writer is not available either.** Attributing a write back to the assembly it came
  from would need an ambient execution identity across `IObservable` hops — the shape this codebase
  forbids, and which `AsyncLocal` does not survive.

So the cut is not read-vs-write but **durable-arming vs transient-probe**, which the framework
already draws for its own reasons: `AsTransientNodeProbe` hubs get the data context but *not* the
per-node control plane, no persistence sampler and no node identity — their own documentation says
"a probe hub must never be used to WRITE" — and they are disposed in the same breath. Refused bytes
may still answer a schema question inside one of those; they may not be given a long-lived,
message-processing, persistence-capable home, and they may not be joined into a live kernel session.

The practical effect: **the refused type's own pages keep rendering** — Overview, data model, schema,
compile diagnostics — which is exactly what an operator needs in order to diagnose it. Its
*instances* serve the refusal card instead of their real areas.

## Where this is actually reachable

On a mesh that can compile locally the gate is mostly unreachable by construction: a refusal already
**clears** the assembly coordinates and flips `Pending`, so `HasUsableBuild` is false and the
ordinary bytes-missing branch takes over. Two states remain, and they are the ones that matter:

1. **`Modules:RequirePrebuilt`** — where the refusal deliberately **keeps** the coordinates, because
   clearing them would leave the type with no assembly at all, indefinitely, and only a human rebake
   can replace them. That is the state in which proven-stale code was left executing.
2. **The window** between `PrebuiltAssemblySeeder.Seed` stamping the coordinates and the owner
   judging them.

## The refusal is a verdict, not a timeout

A refusal nobody can see becomes "the portal is broken", and a refusal that reports as *slow* costs
an hour — [#2818](https://github.com/Systemorph/MeshWeaver/issues/2818) documents exactly that. So
the refusal announces itself four ways, all carrying the same sentence:

- **On the page** — the instance activates and serves an overlay whose lead-in says a build exists
  and the platform refused to run it, *"this is not a compilation error in the source"*. The copy is
  resolved through `host.Localize` at render time off the viewer's `AccessContext`, so it is German
  for a German reader (`ui.executionRefusedIntro` / `ui.executionRefusedGuidance`).
- **To every caller** — the overlay installs an `UnhandledMessageNack` with
  `ErrorType.ExecutionRefused` and the NodeType path, so a typed request gets a terminal
  `DeliveryFailure` naming the type. Deliberately **not** `CompilationFailed` (which would send an
  author to edit source Roslyn never rejected — #641) and **not** `Unavailable` (which reads as
  "retry" when a verdict was in fact reached).
- **In the log** — `LogCritical` at the arming site, `LogError` at the cell-surface join, both
  naming the type and *both* fingerprints, so the verdict can be checked against the bundle by hand.
- **On the record** — `BuildProvenance` is on the NodeType's node and mirrored onto the
  compile-state satellite at `{type}/_Activity/compile-state`, readable through
  `GetMeshNodeStream(path)`.

### The recovery verb

**Recompile the type** — the Recompile button, or the `compile` verb over MCP. Since
[#2824](https://github.com/Systemorph/MeshWeaver/pull/2824) a *forced* release skips on-demand
re-adoption and compiles the live source, which is what makes this a real remedy rather than a
re-adoption of the same bytes. A successful compile stamps `BuildProvenance = Compiled`, the
overlay's self-heal watcher sees the type's version advance with a usable build, and every stuck
instance recycles itself onto its real page.

On a `Modules:RequirePrebuilt` mesh there is no local compile: **rebake and republish the package,
then request a release.** The log line says so, in those words.

> 🚨 The self-heal is what makes the refusal safe to ship. Without `ApplyCompileSuccess` resetting
> the provenance, one refused adoption in a node's history would mark it permanently and this gate
> would refuse a type whose live source it had just compiled itself.

## What this deliberately does NOT do

- **It does not refuse `AdoptedUnverified`.** See above; this is the anti-outage property and it is
  the assertion most worth protecting.
- **It does not gate the transient probe surfaces** (rows 5–7). They execute assembly constructors,
  which is a residual and is named here rather than assumed away: a probe cannot persist, has no
  node identity, and is disposed immediately, so the exposure is a bounded constructor run, not an
  armed control plane. Closing it means gating inside `CompileResultFromAssembly`, which would
  require plumbing the definition into `IMeshNodeCompilationService` — a contract change worth
  making on its own evidence, not as a side effect.
- **It does not attribute individual writes.** A refused type that reaches outside the mesh — HTTP,
  email, a foreign-language kernel worker — is stopped only because it was never armed, not by any
  per-write check.
- **It does not change `HasUsableBuild`.** That predicate also drives the release watcher's
  "satisfied by the existing current build" branch; folding provenance into it would change which
  installs recompile, which is a different decision with a different blast radius.

## Related

- [NodeType Compilation & Releases](/Doc/Architecture/NodeTypeCompilation) — the adoption check that
  produces `BuildProvenance`, and the conditional decision about whether refused bytes keep serving.
- [Plugin Packaging](/Doc/Architecture/PluginPackaging) — where the producer's source fingerprint is
  written into a bundle.
