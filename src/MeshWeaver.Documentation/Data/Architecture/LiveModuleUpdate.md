---
Name: Live Module Update
Category: Architecture
Description: A module update goes live in the running process by default — each module in its own collectible load context, the old generation swapped out and unloaded — and only a declared or failed live swap falls back to an automatic restart. What is shipped, what is owed, and how each module is classified.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 4v5h-5"/><path d="M12 8v4l3 2"/></svg>
---

# Live Module Update

> **The rule (policy `module-live-update-default`, [register](../PolicyNotProse)).** A module update
> goes live **in the running process**: generation N+1 loads into a fresh collectible load context,
> the hubs bound to N are disposed and re-instantiate on N+1, and N's context is unloaded. That is
> THE update path for every module — no flag, no opt-in, no governed activity, no approval. A
> restart is the exception, and it is taken only when (a) a module **declares** `restartRequired`
> with a reason, or (b) the live swap **fails at runtime** — and then it is automatic, with N
> serving until it happens. A module updates **independently of the platform**: it never needs a
> new image, a platform roll or a seal for the running platform's identity.

## Why this replaces restart-as-activation

Until this change every landed module was `Assembly.LoadFrom`-ed into the **default** load context
([Modules](../Modules), "Activation is restart-based"). The default context cannot unload an assembly
and holds one copy per simple name ([Module Generation Substitution](../ModuleGenerationSubstitution)),
so no module could change inside a running process: every update raised `pending_module_activation`,
announced `self-update-restart-pending`, and waited for a restart. On 2026-10-05 that wait was the
incident — AI 1.21.0 landed at 05:30Z while the process ran 1.20.4, and every NodeType compiled
against the new member failed with `CS0117 'ThreadPreparation' does not contain a definition for
'Group'` until somebody filed a restart by hand.

## The four governing rules

1. **Live by default.** Every module the image does not bind runs in its own collectible
   `ModuleLoadContext`; an update swaps the generation in place. Nothing has to be enabled.
2. **A restart is DECLARED, never assumed.** A module that cannot be swapped in-process carries
   `restartRequired: true` on its package manifest with the reason, and a guard fails any module
   that blocks a live swap without the declaration. *(Owed — slice 3.)*
3. **Try live first; fall back only on a measured failure.** When the swap fails at runtime — the new
   context will not load, a contribution cannot be re-applied, a hub cannot re-instantiate, the old
   context does not unload inside its bound — generation N keeps serving (never a half-swapped
   state), the reason is recorded by name, and an automatic restart loads N+1. The restart is the
   self-update lane's routed Restart, which needs no confirmation and no approval (MeshWeaver#4607:
   the routed action carries `origin: self-update` and is accepted only on a node created by
   `system-security`). *(Owed — slice 2.)*
4. **Modules are independent of the platform.** A module bundle resolves every platform contract
   from the running platform and never bundles one; it is admitted by its declared floor against the
   running platform (`PlatformFloor`, policy `package-min-mesh-version`), never by framework-identity
   equality; an image's baked module copy is only a boot baseline that a newer published generation
   supersedes live. A platform roll is needed only for a platform change.

## What is shipped (slice 1 — the load contexts)

| Piece | What it does |
|---|---|
| `ModuleLoadContext` (`MeshWeaver.Mesh.Contract`) | Collectible context for ONE generation of ONE module, named `module:<Name>#<n>`. Resolves **platform first** (anything the default context can bind — one `MeshNodeProviderAttribute`, one `IMessageHub` in the process; a bundled platform copy is never used), then **another module** (the current generation of a module whose entry assembly is asked for, or an assembly a module it depends on already holds — each edge recorded as `DependsOn`), then the generation's **own directory**. Natives resolve through the same candidates as `ModuleNativeAssets`. Marked `IPlatformLoadContext`, so the impersonation guard classifies module code with the platform. Purges Autofac's and System.Text.Json's process-static caches on `Unloading`, as a NodeType context does. |
| `ModuleContexts` | The mesh's registry (a mesh singleton, disposed with the mesh): `Load` a generation into a fresh context, `Commit` it as current (returning the one it replaces), `Retire` the old one — unloaded on a POSITIVE quiescence signal from its `AlcLeaseRegistry`, never on a timer, and recorded on `CollectibleContextUnloads` so "really collected" is observable. `Resolve(name)` is the explicit lookup NodeType builds and script sessions use; `DependentsOf(name)` is the set a swap has to take with it. |
| `MeshBuilder.InstallModules` | Loads every module the image does not bind into its own context and commits it only after its contributions materialised. A generation whose contributions throw is unloaded and the serving one stays current. Because a failed generation never takes its name in the default context, the previous generation and the image copy stay reachable for the fallback (#3649, #3735). |
| `NodeAssemblyLoadContext`, the kernel `ScriptSession` | Bind a module through `ModuleContexts.Resolve` — its CURRENT generation. |

🚨 **Never a `Default.Resolving` handler that hands out a module assembly.** The default context
caches a binding for the life of the process, so the first generation it saw would be pinned forever
and no swap could take effect — and the runtime refuses a non-collectible assembly binding a
collectible one in any case. Every context that needs a module asks the registry explicitly.

**What stays in the default context.** A module whose entry assembly is in the application's own
closure (`TRUSTED_PLATFORM_ASSEMBLIES` — `ModuleContexts.IsImageBound`) is bound by name for every
platform assembly that references it; a second copy in its own context would split its identity. It
loads into the default context exactly as before, and it is what a live update cannot reach until the
image stops shipping it in its closure.

**Tests** (`MeshWeaver.Compiler.Pipeline.Test` → `ModulesRunInTheirOwnContextTest`, real Roslyn emits
and real collections): an installed module runs in a collectible `ModuleLoadContext` and still binds
the one platform; two generations coexist and the retired one is really collected — and its
**negative control** holds one instance of the retired generation and must see it reported RETAINED
by context name (that control caught a first version of the retirement sentinel that reported
"collected" the moment the unload started, because `Unload()` swaps the `Unloading` delegate out); a
dependent binds its dependency's current generation and is recorded as its dependent; a generation
whose contributions throw leaves the serving one current; and a NodeType compiled against a member
only N+1 has fails to bind while N serves (the incident's shape, the negative half) and binds once
N+1 is current — in the running process.

## What is owed

- **Slice 2 — the swap.** The update path calls the swap instead of raising the restart marker:
  load N+1 → recycle every hub the module's contributions configure (its NodeTypes' per-node hubs
  and module-owned hubs, through the `DisposeRequest` cascade of
  [Stale State Until Recycle](../StaleStateUntilRecycle)) and every dependent module with it → the
  hubs re-instantiate binding N+1's contributions → retire N. Module hub configuration has to be
  re-read on re-instantiation rather than folded once into the mesh configuration at boot. The
  runtime-failure fallback (rule 3) and the scenario tests: running with M@N, an update arriving
  through the real install path, M@N+1 serving without restart, the old context unloaded, a second
  update mid-swap, an update while hubs are mid-request, an injected failure producing exactly one
  automatic restart with N serving until it.
- **Slice 3 — the modules (MeshWeaver.Plugins).** Classify every module by what it contributes and
  move what can move to per-hub registration; declare `restartRequired` with the reason on the rest;
  the guard test (with a negative control) that fails a module blocking live update without the
  declaration; refusal at landing of a bundle that carries a platform assembly, by name; the
  platform-fixed tests (P fixed while M goes N → N+1 → N+2 live, N+1 built against an older
  compatible platform build, N+3 above the floor declined by name while siblings keep updating).

## How the module surfaces classify today (measured)

From each module's `MeshNodeProviderAttribute` in MeshWeaver.Plugins `origin/main` (`f78c46725`), the
contribution kinds that cannot be re-applied in-process as the platform stands:

| Contribution | Modules | Re-appliable? |
|---|---|---|
| Root DI through a node's `WithGlobalServiceRegistry` | the AI providers (Acp, Anthropic, OpenAI, AzureFoundry, ClaudeCode, Codex, Copilot, WebSearch, AppleIntelligence), Mcp, Teams, Speech, Courses, Mail, WhatsApp, AppleMessages, Azure.Blob, Cosmos, Snowflake, AppleMaps, GoogleMaps, Radzen | not today — the root container is built once; owed to slice 3 (per-hub registration) |
| The mesh hub's own configuration (`HubConfigurations`) | the Blazor view packs (Chat, Views, Analysis, EntityViews, Graph, OpenStreetMap, AppleMaps, GoogleMaps, Radzen), Markdown.Collaboration | slice 2: re-read on re-instantiation |
| Every per-node hub (`DefaultNodeHubConfigurations`) | OgCard, Indexing.PostgreSql, SelfUpdate.Aks | slice 2: a recycled node hub re-binds |
| The full builder hook (`BuilderConfigurations`) | MeshWeaver.AI, Graph.Views, Observability, Publish, Stripe, Notifications, Hosting.Instance, Fleet.Control, SelfUpdate.Aks, Markdown.Export, Markdown.Collaboration, Indexing.PostgreSql, Testing, Mail, WhatsApp, AppleMessages | per module: whatever the hook registers decides it |
| ASP.NET endpoints (`MeshEndpointProviderAttribute`) | Mcp, Teams, Courses, Mail, WhatsApp, Hosting.Grpc | not in-process — the endpoint map is built once |
| Nodes only | Import, Northwind | yes |

`MeshWeaver.AI` additionally has dependents bound to it (every AI provider module, `Blazor.Chat`, and
the in-mesh NodeTypes compiled against it), so its swap is the swap of that whole set — the shape the
`DependentsOf` edge exists for.

## What is NOT established

- Whether every module the portal image references sits in its `TRUSTED_PLATFORM_ASSEMBLIES` — those
  stay image-bound by construction; the portal's deps.json was not read for this slice.
- Behaviour of Blazor component types and Orleans-serialised payloads from a collectible module
  context in the running portal: no portal-level test of a module in its own context ran here.
- Boot ORDER between a module and a sibling it references during attribute materialisation: a
  dependency must be installed before a dependent whose contribution GETTERS touch its types (the
  same constraint the default context had); a dependency touched only at run time resolves lazily.
