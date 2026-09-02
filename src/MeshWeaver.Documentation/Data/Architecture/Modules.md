---
Name: Modules
Category: Architecture
Description: The module lane end to end — MeshNodeProviderAttribute, the Modules:Assemblies activation list, every module's configuration section, the modules/ publish layout, and the compile-surface + fingerprint story.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l8 4.5v9L12 21l-8-4.5v-9L12 3z"/><path d="M12 12l8-4.5M12 12v9M12 12L4 7.5"/></svg>
---

A **module** is a compiled MeshWeaver assembly a deployment turns on by LISTING it — no code
change, no recompile of the platform. This page is the operator- and author-facing reference for
the whole lane: how a module declares itself, how a deployment activates and configures it, how
its bits reach the image, and how the in-mesh compiler and bake fingerprint treat it.

## Declaring a module — `MeshNodeProviderAttribute`

A module carries one assembly-level attribute deriving from `MeshNodeProviderAttribute`
(`MeshWeaver.Mesh.Contract`). Its five hooks are the complete boot-time surface:

| Hook | What it contributes |
|---|---|
| `Nodes` | Mesh nodes (node types, seeds) — with `.WithGlobalServiceRegistry` for root DI services |
| `AddressTypes` | Address types for the type registry |
| `HubConfigurations` | The MESH hub's configuration |
| `DefaultNodeHubConfigurations` | Configuration applied to EVERY per-node hub (layout areas, type registrations) |
| `BuilderConfigurations` | The full-surface hook — a `MeshBuilder → MeshBuilder` fold, applied last |

HTTP endpoints ride a SEPARATE assembly attribute — `MeshEndpointProviderAttribute`
(`MeshWeaver.Hosting.AspNetCore`), applied by the host's `app.MapMeshModuleEndpoints()` at
endpoint-mapping time. The split is layering (the mesh contract never references ASP.NET) and
timing (endpoints map after the auth middleware). Every contribution maps inside an
authenticated-by-default group — a route is anonymous only where the module explicitly opts out —
and duplicate (verb, pattern) registrations refuse the app loudly at startup. Delisting the
module removes its routes wholesale: a 404, not a compiled optional-service 503.
`MeshWeaver.Social` is the first consumer — its LinkedIn connect/publish/page-sync routes ride
this hook, with the two OAuth callback routes opting out via `AllowAnonymous` (LinkedIn's
redirect must not bounce through a login challenge; the CSRF state cookie is the guard).
`MeshWeaver.Hosting.Grpc` is the second: the whole `meshweaver.v1.Mesh` service maps through the
hook, `AllowAnonymous` on every route because the transport authenticates each connection itself
(Bearer API token in gRPC call metadata, or the trusted loopback port). One piece cannot ride the
hook: the gRPC-web MIDDLEWARE must run between `UseRouting` and the endpoint maps, so the host
keeps a single compiled `UseMeshWeaverGrpcWebWhenInstalled()` line that self-gates on the module
being listed — the module listing stays the only switch.

### Which routes ride the module, and which stay in the host

Not every route belonging to a module's feature belongs *in* the module. The dividing question is
**whose API is it**:

- **The module's OWN protocol surface rides the module.** LinkedIn's OAuth callbacks and the
  `meshweaver.v1.Mesh` gRPC service exist only because that module exists; nobody calls them when
  it is delisted, and a 404 is the honest answer. They also carry their own auth story
  (`AllowAnonymous` plus a CSRF cookie; per-connection Bearer metadata), so nothing is left behind
  in the host.
- **The PORTAL's client API stays in the host, behind a 503 seam** — even when the engine it calls
  ships as a module. `POST /api/log-incidents` (Observability) and `POST /api/speech/transcribe`
  (Speech) are both this shape: the route is part of the portal's REST surface that clients are
  configured against, its access rule is the HOST's to state, and it resolves the module's service
  **optionally**, answering an actionable 503 that names the missing module rather than a 500 or a
  bare 404. Note the two state that rule differently — speech requires the portal's Bearer-only
  `McpAuth` policy, while log-incidents is `AllowAnonymous` at the ASP.NET layer and gates on the
  `LogWatch:IngestToken` shared secret (its caller is a cluster service, not a signed-in user), and
  is not mapped at all when that token is unset. What makes them the same case is not a shared
  policy but a shared owner: the host decides who may call, and the module only supplies the engine.

Two things go wrong when a portal-API route is pushed onto the hook. The caller loses the
diagnosis — "the module is not listed" becomes an indistinguishable 404 — and, more sharply, the
route loses the host's authorization policy. The module hook's group applies the **default**
policy; a route that needs a specific one (the portal's Bearer-only `McpAuth`, whose challenge
forwarding is what makes an unauthenticated API call answer `401 + WWW-Authenticate` instead of
`302` to an HTML login) would have to name that policy by string across the assembly boundary,
which throws at request time in any host that never registered it. Both failures pass CI and
surface as "the mobile app logs me out".

Module DI options bind through the options pipeline —
`services.AddOptions<T>().BindConfiguration("Section")` — never `services.Configure(section)`:
there is no `IConfiguration` instance at install time. A module whose activation depends on
runtime facts guards itself with a resolve-time `enabledWhen` gate (the PostgreSQL indexing
module registers its provider `enabledWhen` the mesh database connection resolves) instead of
failing at boot.

Modules that also need explicit composition (test fixtures, bespoke hosts) expose ONE
`Add<Name>()` extension sharing the same internal configure path as the attribute — the two lanes
must never drift (`OgCardExtensions` is the reference shape).

## Activating — the appsettings baseline ∪ persisted store installs

A deployment's active module set is the union of two lanes, computed at boot (before the DI
container builds) and fed to `MeshBuilder.InstallAssemblies` as one list:

1. **The `Modules:Assemblies` appsettings baseline** — the DLLs the image ships with; the list is
   the operator's on/off switch for first-party packs, exactly as before. A baseline entry that
   fails to load fails loudly at startup, never silently.
2. **The persisted activation record** — one file per module under `modules/activation.d/`, written
   by the runtime landing service (`ModuleLandingService`) when a compiled module is installed from
   the Store. Each entry records the module name, its source, the install record's mesh path, its
   generation directory, its declared platform floor, and the framework MVID the landed assemblies
   were built against. The legacy aggregate `modules/activation.json` is still READ (deployments
   already carry one) and a per-module file wins over it by name; nothing writes it any more.

   > 🚨 **Why one file per module and not one index.** Every portal replica mounts the same RWX
   > `/data`, and a republish after a release pushes 30+ modules concurrently. A single mutable
   > index that each landing read, appended to and renamed over has two failure modes no retry
   > fixes: concurrent landings of *different* modules **lose each other's entries** (last writer
   > wins the whole list), and the rename **contends for the file's SMB lease** with every other
   > reader and writer of that one path — `Access to the path '/data/modules/activation.json' is
   > denied` on the write side (HTTP 409), and a `FileNotFoundException` on the read side from
   > opening into the replace window, which the reader then reported as a corrupt sidecar and
   > **booted the pod with no store modules at all**. Sharding by module removes the shared cell:
   > two writers of different modules share no path, so neither outcome is possible. The
   > restart-required flag is a marker FILE (`activation.d/.pending-restart`) for the same reason —
   > setting it is a create and clearing it is a delete, never a read-modify-write. And a record
   > that cannot be read now costs exactly that one module, reported by name, instead of collapsing
   > the whole answer to the empty list.

The union dedupes by module name (a store install of an already-baseline module contributes
nothing). **Activation is restart-based**: landing a module writes its assemblies into
`modules/<name>/` and its activation entry, flags `PendingRestart` in the sidecar, and the module
loads on the NEXT restart — nothing is loaded into the running process (a genuinely dynamic
loader collides with the kernel snapshot). Boot consumes the `PendingRestart` flag: applying the
list IS the restart. Uninstall is the mirror: the entry is disabled (kept, for history), the
folder is deleted, and the change likewise takes effect at restart.

**The skip rules** (persisted entries only — the deployment must always boot):

- **Unsatisfied platform floor** — the running platform no longer satisfies the module's declared
  `minMeshVersion` (a rollback below its requirement). The entry is SKIPPED with a loud log
  naming both versions and stays in the sidecar, waiting for the platform to move forward again.
  The gate is `ModulePlatformFloor.DeclineReason` — the ONE notion of the module platform
  requirement, shared with landing and serving. Deliberately a **semver floor, never MVID
  equality**: a module is a plain assembly binding by simple name, so a landed module keeps
  loading across ordinary platform updates; the MVID it was built with is recorded on the entry
  as diagnostics only (MVID equality is bake semantics and belongs to the NodeType assembly
  lane).
- **Missing DLL** — the entry's `modules/<name>/<name>.dll` does not exist (lost volume, manual
  deletion). Skipped loudly; re-install to heal. The check is that path SPECIFICALLY — a
  same-named DLL in the app closure never satisfies a store-installed entry (the
  `ResolveModulePath` base-directory fallback applies to baseline entries only, so a tampered
  sidecar can never silently bind the platform's own binaries).

The landing service itself gates twice more, at placement: the same floor check (declined bytes
never reach disk), and a refusal of any module whose entry DLL name collides with an app-closure
assembly — `ResolveModulePath` probes `modules/<name>/` first, so such a module would silently
shadow the platform's own binary at the next boot.

### 🚨 "Keeps loading across ordinary platform updates" is a promise the PLATFORM owes (#2370)

The semver floor above is not a weaker gate than MVID equality — it is a **different contract**, and
the platform side of it is: *a public type a module can bind must keep its full name and keep being
reachable from the assembly it was bound in*. A module's IL holds neither a `using` nor a source
reference; it holds

```
TypeRef  MeshWeaver.AI.MeshOperations     scope: AssemblyRef MeshWeaver.AI
```

so **moving a public type to another assembly, or renaming its namespace, breaks every module
compiled earlier** — at the next roll, with no warning anywhere:

```
System.TypeLoadException: Could not load type 'MeshWeaver.AI.MeshOperations'
    from assembly 'MeshWeaver.AI, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null'
```

That is #2370. `MeshOperations` moved to `MeshWeaver.Mesh.Operations` and the store-installed
`MeshWeaver.Mcp` could no longer construct `McpMeshPlugin`; because the MCP SDK builds its tool
target per invocation, EVERY tool call — `get`, `search`, `create`, `render_area`, the LSP and chunk
tools — failed identically. A full outage of the deployment's `/mcp` surface, for every external
client, from a change that was source-compatible and reviewed as a refactor.

**The move is fine; losing the name is not.** Leave a forwarder in the old assembly and keep the
type's ORIGINAL full name in its new home — a forwarder cannot rename:

```csharp
// src/MeshWeaver.AI/TypeForwards.cs
[assembly: TypeForwardedTo(typeof(MeshWeaver.AI.MeshOperations))]
```

The CLR then resolves the module's TypeRef through the old assembly to ONE type identity — not a
shim, which would mint a second identity and reintroduce the `as`/`is` trap-door.

🚨 **No repo-local build can see this break, and two green gates specifically cannot.**
`landed-modules-gate` compiled the plugins repo's module SOURCE against the PR, which is a different
question from whether the module ALREADY PUBLISHED still binds — and on #2370 it passed, because the
module's source carried `using` directives for both namespaces. (That job is **gone** besides: core
builds the image and runs its own tests, and plugins are built by the repo that owns them, so nothing
in core's CI compiles a line of module source today.) The semver floor cannot see a type at all.
`scripts/check-type-forwards.py` (wired into the *Public surface (binary compatibility)* job
beside #2298's `check-record-signatures.py`) is what refuses the next one; its allow file is a
statement that no shipped module can hold the TypeRef, not a way to make it quiet.

🚨 **A move OUT OF THIS REPO reads exactly like a deletion, and the gate used to be silent on it.**
Since #2276 the module assemblies are built in MeshWeaver.Plugins, so a public type moving from a
core assembly into one of them deletes files here and adds none. The gate's original scoping decision
— a type that vanishes from `src/` entirely is out of scope, because "a deletion reads AS a deletion
in review" — therefore stopped holding, and it reported `OK` across `v3.0.0-rc7 → main` while that
window contained the seven types below. A **departure** (the type is gone from `src/` while the
assembly it left is still built here) is now its own counted, named category and it fails; pass
`--sibling <checkout>` to have the gate say which departures are cross-repo moves and which are
deletions.

**It had already happened again before the gate existed.** Replaying that gate across
`v3.0.0-rc7 → main` found **17** unguarded moves; #2370 fixed four, and #2398 fixed six more that
#2276 made when it moved the credential-protection and MCP-back-connection contracts into
`MeshWeaver.Mesh.Contract` — `IProviderKeyProtector`, `ProviderKeyProtector`, `IMasterKeyProvider`,
`ConfigMasterKeyProvider`, `IMcpBackConnection`, `McpConnectionInfo`. Three of those have a proven
module consumer in the plugins repo today. So when reading a file under `src/MeshWeaver.Mesh.Contract`
that declares `namespace MeshWeaver.AI` (or `MeshWeaver.AI.Connect`) — and the one under
`src/MeshWeaver.Mesh.Operations` that does the same — that mismatch is **the contract, not a
leftover**. A forwarder cannot rename, so tidying the namespace to match its assembly re-breaks
every module built before the move. `MovedTypeBinaryContractTest` pins each name at runtime.

🚨 **A forwarder is not always available, and that is a decision rather than a workaround.** The
forwarder must live in the assembly being LEFT, so that assembly has to reference the type's new
home — impossible when the move runs *against* the existing reference direction. Two of #2276's
moves are exactly that (`MeshWeaver.GitSync → MeshWeaver.AI` and `MeshWeaver.Hosting →
MeshWeaver.AI`; `MeshWeaver.AI` references both, so neither can reference it back). When a move has
that shape there are only two honest options — **move the type back**, or accept the break and do
the **atomic** republish: rebuild and republish every affected bundle, then roll the image, so no
deployment is ever running an old bundle against a new platform. Inventing a shim to dodge the cycle
is the one thing that must not happen: it mints a SECOND type identity and reintroduces the `as`/`is`
trap-door that reads as a silent null.

### 🚨 An ACTIVATED entry with no bytes — the GC race (#2303)

The "Missing DLL" skip above is the SYMPTOM; #2303 traced one concrete way an entry ends up
pointing at nothing: a race between `ModuleLandingService.CollectGarbage` (run once per pod start,
after `ApplicationStarted` — see the readiness section below) and a landing happening on a
DIFFERENT replica at the same moment.

A landing is two writes on the shared `/data` volume, deliberately ordered bytes-then-entry: it
`Directory.Move`s the new generation into place, THEN writes the sidecar entry that names it
(`LandCore`). Those two writes are adjacent in one synchronous call on the landing replica, but
nothing serializes them against a GC pass on ANOTHER replica — the per-module sidecar file and the
landing service's IO pool both bound a single process, not a cross-process sequence. If a GC pass
reads the sidecar in the gap between the other replica's two writes, the new generation directory
is on disk but no entry references it YET — indistinguishable from a genuinely orphaned directory —
and GC deletes it a moment before the landing's `WriteEntry` lands, pointing a real, enabled
activation entry at bytes that no longer exist. Nothing throws anywhere: the landing that raced GC
reports success (both of ITS writes succeeded), and the entry only reveals itself as unresolvable
the next time something reads it — `ModuleActivationStatus.Unresolvable`'s loud startup report and
`Degraded` health check (#2093), or a boot that silently skips the module via the "Missing DLL" rule
above. That is the exact shape #2303 reported for `MeshWeaver.Blazor.EntityViews`: an ACTIVATED
entry whose landed assembly was gone, with no exception or stack frame naming why — likeliest to
fire during a rolling restart landing (or auto-updating) a module while sibling pods are cycling
through boot at the same time.

The fix cannot be a lock — replica coordination here is deliberately structural, not a gate. Instead
`CollectGarbage` carries a grace period (`ModuleLandingService.DefaultGarbageMinAge`, 5 minutes):
an unreferenced generation (or `.staging-`/`.pending-` leftover) younger than the window is left for
a LATER pass rather than reclaimed immediately. A directory that survives the window and is STILL
unreferenced is a genuine orphan and is collected exactly as before — the grace period defers
reclamation, it does not disable it. The two writes of a real landing are back-to-back with no I/O
between them, so the actual exposure the window has to cover is low-single-digit seconds even over
a slow network volume; five minutes is generous headroom on top of that.

### 🚨 GC vs a RUNNING process — three more holes, closed after the 2026-08-27 outage (#2509)

The grace window protects a landing IN FLIGHT; #2509 measured three ways GC still broke modules
that had landed long ago, on both prods at once:

1. **Unreadable is never unreferenced.** The reference set GC deletes against comes from
   `ModuleActivationSidecar.Read`, which — correctly, for boot (#2189) — skips a per-module entry
   file it cannot read and keeps the rest. For GC that per-module resilience inverts into a hazard:
   one transient SMB read fault makes that module's ACTIVE generation indistinguishable from an
   orphan, and the pass deletes the very bytes its entry references — a dangling activation entry
   with nothing naming why. `CollectGarbage` is now fail-closed: any entry-file read fault skips
   EVERY generation delete that pass (transient `.staging-`/`.pending-`/`.trash-` folders still
   collect — nothing references those by design), and a later boot re-reads and sweeps.

2. **Removal is atomic per directory.** Deleting a generation in place could fail PARTWAY — one
   locked file aborts the recursion — and the skip-on-locked catch then preserved a HALF-GUTTED
   generation: entry DLL present, lazily-loaded dependency DLLs gone. A generation is now first
   renamed to a `.trash-*` sibling (one atomic rename, after which resolution can no longer see
   it) and only then recursively deleted; a refused rename leaves the directory fully intact, an
   interrupted delete leaves only a `.trash-*` folder a later pass finishes. There is no
   half-deleted state either way.

3. **A running process loads from PROCESS-LOCAL storage** (`ModuleGenerationPin`). The shared
   `modules/` tree has reference-set lifetime, but a process needs its loaded generation for its
   own lifetime: dependency DLLs load LAZILY, and Roslyn content compiles
   (`CompileReferences.ComposeWithModules`) re-read module files by path hours after boot. An
   auto-update that lands a newer generation makes the one THIS pod loaded unreferenced, and a
   sibling pod's boot GC then reclaims it — correctly, by the sidecar's lights — so the pod's
   first lazy load afterwards was `FileNotFoundException: Could not load file or assembly
   'OpenAI'`. Boot now copies each store-landed generation into a per-process folder under the OS
   temp path and loads from there; the shared tree stays a transport that GC may reclaim freely.
   The pin is protection, not a gate: a boot that cannot copy warns loudly and falls back to the
   shared path.

### 🚨 GC is OFF the readiness path (#2684)

Where the pass runs is as load-bearing as what it deletes. It used to run synchronously in the
portal's boot path — before the host listened — and on an Azure Files (CIFS) `/data` the
rename-then-recursive-delete of orphaned generations is one SMB round-trip per file: minutes of
uninterruptible IO for a handful of directories. Rollout time thereby became a function of how much
garbage the previous generation left on a network volume, which is unbounded and invisible until
the probe kills the pod: memex-cloud's roll to ci.6559 sat as PID 1 in `Dsl` at
`wchan=wait_for_response`, never bound :8080, blew the 300 s startup probe — whose kill cannot land
on a process parked in uninterruptible IO — and looped, wedging the whole `helm upgrade`. Raising
the probe budget would only move the cliff.

Reclaiming orphans is housekeeping: valid at any time, needed by nothing the portal serves. So the
pass now runs from `ModuleGenerationsGcHostedService`, registered by the same boot path that used
to call it: `StartAsync` only registers an `ApplicationStarted` callback (it can never delay the
listener), the callback schedules `CollectGarbage` on the file-system `IIoPool`, and the pass
observes the pool's cancellation between directories so a mesh teardown never waits out a slow
unlink. Nothing about the pass gates `/health` or `/alive`, and nothing about its SEMANTICS
changed: same rules, same grace window, same atomic `.trash-*` rename — and the reference set is
re-read from the per-module sidecar files at run time, so a post-start pass sees a set at least as
fresh as the boot-time pass did. The running process is immune to its own reclaim because it loads
store-landed generations from the process-local pin (above), never the shared tree — the same
property that already protected it from a SIBLING pod's pass.

Why a sidecar file and not a mesh node: the list is consumed before any storage provider, hub, or
connection string exists, and it must move with the DLLs it describes — the landing service writes
both in one operation onto the same volume, so they cannot drift apart.

The current first-party inventory and each module's configuration section:

| Module DLL | Concern | Configuration |
|---|---|---|
| `MeshWeaver.AI.OpenAI.dll` | OpenAI-compatible model providers | `OpenAI`, `OpenAICompatible:Models` |
| `MeshWeaver.AI.AzureFoundry.dll` | Azure Foundry + Anthropic-on-Azure providers | `AzureFoundry`, `Anthropic` |
| `MeshWeaver.AI.ClaudeCode.dll` | Claude Code harness | `ClaudeCode` |
| `MeshWeaver.AI.Copilot.dll` | Copilot harness | `Copilot` |
| `MeshWeaver.AI.WebSearch.dll` | Agent web-search tools (`SearchWeb`, `FetchWebPage`, feed readers) | `WebSearch` (self-gates on credentials) |
| `MeshWeaver.Blazor.Radzen.dll` | Radzen view pack (charts etc.) | — |
| `MeshWeaver.Blazor.Analysis.dll` | Analysis view pack | — |
| `MeshWeaver.Blazor.GoogleMaps.dll` | Google Maps map provider | `GoogleMaps` |
| `MeshWeaver.ContentCollections.Indexing.PostgreSql.dll` | Content indexing (PG) | gated `enabledWhen` the mesh DB resolves |
| `MeshWeaver.Speech.dll` | Speech transcription | `Speech` |
| `MeshWeaver.Markdown.Export.dll` | Document export (PDF/DOCX/HTML/email) | — |
| `MeshWeaver.Observability.dll` | Red-log ticketing / log watch | `LogWatch` |
| `MeshWeaver.OgCard.dll` | Link-preview (og-card) layout area | — |
| `MeshWeaver.Notifications.Channels.dll` | Notification delivery channels (rule/channel node types + AI triage escalation) | `Email` (triage self-skips unless `Email:Enabled`) |
| `MeshWeaver.Social.dll` | LinkedIn publishing: connect/publish/page-sync endpoints + node-menu actions | `Social:LinkedIn` |
| `MeshWeaver.Teams.dll` | Microsoft Teams bot channel: messaging endpoint, inbound routing into threads, proactive replies | `Teams` (inert until bot credentials set) |
| `MeshWeaver.SelfUpdate.Aks.dll` | AKS/ACR mechanics: ACR tag reads, Kubernetes deployment patching, cluster instance provisioning (the self-update POLLER stays in the platform) | `SelfUpdate`, `Instances` |
| `MeshWeaver.Courses.dll` | Course delivery: the entitlement-gated `/assets/{Space}/…` route over a Space's synced repo | `GitHub:App:*` (shared with GitSync) |
| `MeshWeaver.Mail.MicrosoftGraph.dll` | Mail over Microsoft Graph: system email, inbound intake + its webhook, the Executive Assistant's mailbox tools | `Email` (`Enabled`, `InboundEnabled`) |
| `MeshWeaver.Import.dll` | Tabular import: Excel/CSV readers (its private `MeshWeaver.DataSetReader.*` closure), mapping configuration, the `ImportRequest` handler | — (🚨 list it FIRST — see below) |
| `MeshWeaver.Mcp.dll` | The Model Context Protocol server: the mesh tool surface + the `/mcp` HTTP transport | `Mcp` (`BaseUrl`; the `McpAuth` policy stays platform-side) |
| `MeshWeaver.Hosting.Grpc.dll` | The mesh gRPC transport: `meshweaver.v1.Mesh` + gRPC-web, `py`/`node` foreign participants AND the React GUI's browser data plane | `Grpc` (`TrustedPort`) |
| `MeshWeaver.Hosting.Cosmos.dll` | Cosmos DB storage backend (keyed adapter factory + native query) | selected by `Graph:Storage:Type` = `Cosmos` |
| `MeshWeaver.Hosting.Snowflake.dll` | Snowflake storage backend (persistence, change feed, cross-schema query, access projection) | selected by `Graph:Storage:Type` = `Snowflake` |
| `MeshWeaver.AI.dll` | The AI ENGINE — the agent runtime (threads, rounds, delegation, tool calling, harnesses, token accounting) **and** the catalogs that administer it (Agents, Skills, Providers, Models, Tiers) | `Features:StaticRepoSync:Partitions`, `Features:Ai:Clis:*`, `Skills:Directory`, `ClaudeConnect` |

🚨 **On a deployment with a plugin catalog the AI engine is registry-served, so it is listed under
`Modules:Required` and NOT under `Modules:Assemblies`** — see *Deciding* below for why those two
lists are mutually exclusive for one name. (`Modules:Assemblies` remains the correct lane for the
engine on a host that has no catalog and therefore ships it in its own closure — the LocalMesh case
immediately below. The rule is about not listing it in BOTH, not about one lane being wrong.) Its Store entry is `preInstalled`, so a first-party deployment lands it unattended, and
`Required` is what turns an absence into a degraded readiness report rather than a silently
model-less portal (no chat, no models, and `Provider/*` empty — the catalog is engine-projected).

🚨 **`Memex.LocalMesh` is the exception that shows the rule.** The headless sidecar has no plugin
catalog — no registry client, no auto-install — so a `Modules:Required` entry there would name a
module nothing can ever land, and every chat send would be refused *"NodeType 'Thread' is not
registered"*. It keeps the engine in its own app closure instead; with no install path, there is
nothing for a registry module to collide with. **A host without a catalog cannot consume the
registry lane at all** — check that before flipping any module on a new host.

🚨 **`MeshWeaver.Hosting.Grpc` is DEFAULT-ON in every deployment.** Its endpoint is not just the
foreign-participant (`py/*`, `node/*`) transport — the React GUI connects over the very same
grpc-web `Connect`+`Deliver` split at the origin root (`clients/portal-next`, `clients/portal`).
Delist it only in a deployment with NO React GUI and NO foreign participants; anywhere else a
delist silently breaks the React frontend's live connection. (The former `Features:Grpc` flag is
gone — the module listing is the switch.)

🚨 **`MeshWeaver.Import` is listed FIRST, and a module that registers nothing is still doing work.**
No host ever called `AddImport()` — `AddImport(...)` is an application-level call a data source
makes for itself, and the portals referenced the assembly for exactly one reason: so that **in-mesh
source could `using MeshWeaver.Import`**. NodeType sources compile against
`TRUSTED_PLATFORM_ASSEMBLIES` **composed with the deployment's installed modules**
(`CompileReferences.ComposeWithModules`), and `MeshBuilder.InstallAssemblies` records an
`InstalledModuleAssembly` for **every** listed DLL — attribute or not — so listing it is what keeps
that compile surface. Because the reference set is composed in list order, a module whose own
content compiles against `MeshWeaver.Import` must be listed **after** it.

Note what a module contributes to that surface: **its entry assembly, not its private closure.** A
module's own dependencies (here the six `MeshWeaver.DataSetReader.*` assemblies, plus
`MeshWeaver.DataStructures` and `CsvHelper`) resolve at
RUNTIME from the module folder, but they are not metadata references — so in-mesh code may use the
module's public types freely, and would need the platform to carry any *other* assembly whose types
appear in those signatures. Keep a module's in-mesh-facing surface self-contained.

Boot packs select by OTHER configuration too: `Graph:Storage:Type` `Cosmos`/`Snowflake` requires
the matching `MeshWeaver.Hosting.Cosmos`/`.Snowflake` DLL in this list — installation runs before
storage selection, so ordering is safe. Delisting a UI module removes its areas mesh-wide;
embeds of a removed area render the standard area-not-found placeholder (documented per module).

Both storage backends **ship in the image but are listed by nobody** — every memex portal runs
PostgreSQL — so selecting one is purely an appsettings edit in the deployment that wants it.
They ride the closure lane rather than the Store bundle lane on purpose: persistence selection
reads `Graph:Storage` during boot, so a storage backend cannot be something the mesh installs
for itself once it is already running. The bits cost ~25 MB of publish output (Cosmos ~15 MB with
the Direct/ServiceInterop client, Snowflake ~10 MB — its driver carries Arrow plus the AWS and
GCS SDKs for stage transfer); `-p:PublishMeshModules=false` skips the whole layout for a host
that wants none of it.

Being **bootstrap tier** — the mesh cannot read itself without a storage backend, so the Store's
catalog lives behind the very storage an install would be delivering — is also what leaves these
two with no compiled reference anywhere in the tree, and therefore nothing that would notice their
folder going wrong. `StorageModuleLayoutTest` (`test/Memex.Portal.Shared.Test`) is that gate: it
walks the seam a portal walks and asserts nothing more — `ResolveModulePath` lands inside
`modules/<Name>/` rather than on its app-folder fallback, the private driver survived the prune and
loads, `InstallAssemblies` folds the assembly's `MeshNodeProviderAttribute`, and the keyed
`IStorageAdapterFactory` that `Graph:Storage:Type` resolves comes from THAT DLL. No emulator, no
endpoint, ~40 ms. It closes two blind spots at once: the compiler proves the SOURCE binds but says
nothing about the publish layout, and the emulator suites green-SKIP when their backend is
unreachable, so they can pass by not running. The same test is what a released binary would have to
satisfy if these backends ever moved out of the platform repo (#1752) — point it at the pinned bytes
instead of the in-tree build and it answers the question a moved backend raises.

Entries resolve through `MeshBuilder.ResolveModulePath`: a rooted path passes through; a bare
DLL name probes **`modules/<name>/<name>.dll`** beside the app first (the publish layout below),
then falls back to the app folder.

## The `modules/` publish layout (#1644)

Both hosts import `memex/MeshModulesPublish.targets`: publishing lays every listed module out
under `modules/<Name>/` beside the app, pruning same-identity files the app output already
carries. While a module still ALSO rides a `ProjectReference` (the transition state), its folder
prunes to empty and the loader falls back to the app folder — byte-for-byte the classic image.
Flipping a module's reference off (one module at a time, its entry upgraded to a closure layout
correct for that module) is what makes the folder carry real content; which modules EXIST then
becomes a publish (or Store-install) decision while which ACTIVATE stays the boot union above.
Skip the whole target with `-p:PublishMeshModules=false`.

`-p:MeshModulesClosureSubset=<Name>;<Name>` narrows the closure lane to the named modules, so a
project that is not a host can lay out a couple of them into its own `bin/` — today only
`Memex.Portal.Shared.Test`, so `StorageModuleLayoutTest` loads the real layout rather than a copy
of it. 🚨 A host must never pass it: `-p:` is global to every project in the build. A subset naming
nothing fails the lane RED instead of laying out nothing and reporting success.

The first flipped module is `MeshWeaver.Markdown.Export`: no host references it any more — its
targets entry runs a full closure publish pruned against the app root AND the shared-framework
targeting packs, so its folder carries the engine assembly (measured private deps beyond it:
none; the engine's package closure still rides the app via other references). Because a flipped
DLL exists nowhere else, the closure lane also lays it into a plain build's output
(`bin/…/modules/`), keeping `dotnet run` on a host working without a publish step.

### 🚨 Which COPY loaded — the boot report (#2223)

Two `modules/` trees are legitimate at once: the image publishes baseline packs beside the app, and
a store install LANDS its bytes as a fresh generation under the deployment's writable, pod-shared
root (`modules/<Name>@<id>/`). So "the pack" is not a place — and until this report existed nothing
said which of them a running portal had actually loaded.

Measured on memex-cloud 2026-08-25: the portal ran an image built from the fix's own merge commit,
the store held **two** newer copies of `MeshWeaver.Blazor.Views` that both contained the fix, and
`/proc/1/maps` showed the process had mapped the **image** copy — which did not. Every lane was
green. The mechanism is not a bug in any single step:

1. a **baseline** `Modules:Assemblies` entry resolves through `MeshBuilder.ResolveModulePath`, whose
   probes are landed root → image → app closure;
2. the landed probe looks in the fixed `modules/<Name>/`, which generation landing never writes, so
   it misses and the image copy wins;
3. the sidecar entry that *would* have named the generation is deduped away by name, silently,
   because the baseline already claimed it (`ComputeEffectiveModuleEntries`).

`ModuleLoadReport` (`src/MeshWeaver.PluginCatalog/ModuleLoadReport.cs`) makes that visible. At boot,
immediately before `InstallAssemblies`, it emits one `[ModuleLoad]` line per pack — name, source
(`appsettings` / `store`), the **exact path being loaded**, its MVID and its last-write time — and a
`STALE PACK` warning when the store holds a copy of the same module that is both **newer** and
carries a **different MVID**. Two copies with the same MVID are the same bytes in two places and
warn nothing, or the line would be noise.

It reports the array it is HANDED, so the line and the load cannot disagree; the acceptance is
literally that the path in `/proc/1/maps` equals the path the line named:

```bash
kubectl exec -n <ns> <pod> -c memex-portal -- sh -c \
  'cat /proc/1/maps | grep -o "[^ ]*Blazor.Views.dll" | sort -u'
kubectl logs -n <ns> <pod> -c memex-portal | grep '\[ModuleLoad\]'
```

🚨 **It warns; it never refuses to start.** Which copy *ought* to win is an open policy question, and
a pod that dies on the answer cannot be given the module that fixes it — the same deadlock as a
registry that cannot start delivering the module breaking it. The remedy the warning names is a
deployment decision: delist the pack from `Modules:Assemblies` so the landed generation stops being
shadowed.

### Native assets — `runtimes/<rid>/native/` (#1728)

A module is loaded with `Assembly.LoadFrom`, which never consults the module's own `deps.json`, so
the runtime's fallback probe is the module's FLAT folder and nothing else. That is why the closure
lane's first prune used to delete `runtimes/` outright — and why a module could not ship a native
library at all.

It can now. The publish keeps `runtimes/<rid>/native/**` (dropping the managed `runtimes/<rid>/lib`
trees, which genuinely need the deps.json, and `.a`/`.lib` link-time artifacts, which nothing can
open), and the host resolves them at load time: `ModuleNativeAssets` subscribes
`AssemblyLoadContext.Default.ResolvingUnmanagedDll`, derives the module folder from the REQUESTING
assembly's own location — so a dependency such as `SkiaSharp.dll`, which declares the P/Invokes
rather than the module assembly, resolves too — and probes
`modules/<Name>/runtimes/<current-rid>/native/`, then the flat folder.

Resolution rather than placement, because every module MSBuild invocation strips RID globals by
design (#1675/#1676): a module publish is always portable, so the RID is unknown when the bits are
laid out and only the host knows its own. The RID probe is the running RID plus its portable form
(`osx.14-arm64` → `osx-arm64`); it deliberately does NOT walk a wider graph, because
`linux-musl-x64` and `linux-x64` are different C libraries and loading one for the other crashes
instead of failing cleanly.

Two modules already needed this: Snowflake P/Invokes `libsf_mini_core.*` (and Mono.Unix), and
Cosmos' query-plan `ServiceInterop` is native. Both were shipping with those files pruned away.

## The bundle lane — modules as Store packages (#1664)

A compiled module reaches a deployment one of two ways: shipped in the image (the baseline above),
or **installed from the Store as part of an ordinary package**. The second rides the plugin bundle
transport end to end — there is deliberately no second distribution channel:

1. **Declare** — the package's root `index.json` carries `content.module` naming the module's
   entry-assembly (`"module": "MeshWeaver.Social"`), plus the platform floor it requires in the
   `content.minMeshVersion` field authors already write. The listing reads both onto the catalog
   entry (`PackageManifest.Module` / `.MinMeshVersion`) and the ordinary install-record stamp
   carries them onto the record. A package with content nodes AND a module is one Store product —
   card, price, install funnel, pre-install eligibility all unchanged.
2. **Build** — `MeshWeaver.Plugin.Build`'s `module-pack` mode packs a built module's closure into
   a bundle recording the `minMeshVersion` floor (`--min-mesh-version`) and, as diagnostics, the
   MVID of the identity anchor (`MeshWeaver.Compiler.dll`, #1707) in the build output. It is a
   plain dotnet invocation over
   an output folder, so ANY node repo's CI can drive it — SocialMedia builds its own module
   bundle the same way the platform repo does — and because the gate is the floor, ONE bundle
   serves every compatible platform build: nothing is rebundled per CI build. The closure is an
   explicit statement (`--with`), never a folder scrape: a publish output contains the whole app
   closure, and bundling framework assemblies would shadow the platform at the consumer.
3. **Serve** — the registry portal's `/api/plugins/bundles` serves the module section inside the
   SAME bundle that carries the package's NodeType assemblies (`meshweaver/modules/` beside
   `meshweaver/assemblies/`, one manifest naming both). The registry serves a module's bytes from
   its own `modules/<name>/` tree — the very bytes it loads and runs — and refuses to serve a
   landing its own boot would skip (uninstalled, or a floor the registry's own platform no longer
   satisfies). The index stamps each bundle's `module` (and its floor) only when the bytes are
   actually servable, so a consumer never downloads for a section that will not be there. Same
   instance-key auth, fail-closed.
4. **Land** — on install (and on update), a consumer whose package declares a module fetches the
   bundle, verifies the **platform floor** (`ModulePlatformFloor.DeclineReason` — the one notion
   of the module platform requirement, checked at the index, at the manifest, and again at
   placement), and lands it through `ModuleLandingService` into `modules/<name>/` with its
   activation entry (version + floor recorded, plus the **framework identity the registry
   advertised for those bytes** — the producer's value, which the update decision reads back).
   The landing GATE is deliberately **not** MVID equality — that is bake semantics, the NodeType
   lane's gate: a module binds by simple name, so a bundle built against an older platform
   installs ex post on any deployment satisfying its floor. Restart-as-activation as above:
   `PendingRestart` is the signal, the next restart loads it.

### Auto-update

Store-installed modules **update themselves by default**. The boot reconcile
(`RegistryUpdateReconciler`) runs a module pass after the content pass: for every installed
module-declaring package it consults the registry's bundle index and applies the one pure decision
(`ModuleUpdateDecision`) — a newer version whose **floor this platform satisfies** lands via
`ModuleLandingService` and flags `PendingRestart`; the same served version **built against the same
framework** is skipped without a download; a bundle whose floor **exceeds** the running platform is
skipped silently-with-log (it becomes installable once the platform has updated, and the same
reconcile lands it then). Nothing is ever rolled back unattended.

#### "Already landed" means this content against this FRAMEWORK

🚨 **A module's version encodes its CONTENT only.** Rebuild the same source against a new platform
and it republishes under the *same* version — so a reconcile that compared the version alone
answered "already landed" for an artifact the deployment does not hold, and nothing ever looked
again (Plugins#931). Measured in Plugins#723: after a platform identity flip the updater landed the
~12 modules whose versions had moved and then went quiet with no new `MeshWeaver.AI.OpenAI` build,
because OpenAI's had not; rolling the image anyway crash-looped deterministically (the pre-flip
build cannot resolve `ProviderModelLister` on the new platform, whose registration had moved) and
the fleet was held on an old image.

So the skip is keyed on **(version, framework identity)**. The registry records the identity of a
module's bytes when their owning repo's CI publishes them (`ModulePublish` → `ShelveModule`) and
advertises it **per bundle** on the index (`BundleRef.FrameworkMvid` — never the index's top-level
identity, which is the registry's own bake and says nothing about a module it did not build); the
consumer records what it landed on the activation entry and compares the two before downloading a
byte.

**The two sides are deliberately not symmetric, and that asymmetry is what stops the fix becoming a
download loop:**

| landed | served | verdict |
|---|---|---|
| known | known, **different** | **Land** — same content, different platform build. The reason names both identities. |
| **unknown** (entry predates the field) | known | **Land**, once. The landing writes the identity back, so the next reconcile has two known values. |
| any | **unknown** | **Skip**, and the reason SAYS the identity could not be checked. Landing could never turn "the registry states nothing" into evidence — it would state nothing next time too — so answering Land there re-downloads every module on every reconcile, forever, against any registry that predates the field. |
| known | known, equal | **Skip** — the genuine no-op, with the framework named. |

The remaining blind spot (a registry that states no identity) is closed **where it is created**, not
by churning consumers: a bundle that cannot say what it was built against must not be publishable.
See [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) → "Content-addressed outputs" for the
producer half.

The policy gate is the deployment's **existing update policy — `Admin/UpdatePolicy`**, the same
single surface that governs the platform image roll; there is no module-specific knob.
**Continuous — the platform default, and what an absent policy reads as — lands unattended;
Stable and None decline the UPGRADE** (the catalog's manual Update still works there): a
deployment that pins its image takes updates deliberately, and its modules do not run ahead of
that choice. A **first landing** is deliberately policy-exempt: it completes an install the
operator's own surfaces already sanctioned, and gating it would ship a package whose binary half
never arrives. The wiring is `IModuleUpdatePolicy` (`MeshWeaver.PluginCatalog`), implemented by
the memex portals over the policy node; a host that registers no implementation gets the default
(allowed).

## Deciding: what can be a module, and where its source may live

Two properties decide a module's shape, and they are **independent** — they move in separate
changes, and confusing them is what makes a carve-out look blocked when it is not, or land when it
should not have.

| | question | answer decided by |
|---|---|---|
| **Delivery** | do the bits arrive in the IMAGE or from the REGISTRY? | whether the deployment can boot without it |
| **Source** | does the code live in the PLATFORM repo or a NODE repo? | whether the platform's bake host must compile against it |

### Delivery — image closure vs registry bundle

Registry delivery is the default for anything a deployment can start without. The exceptions are
structural, not preferences:

- **Storage backends** must be image-shipped: a store-installed module needs storage to already
  work, so the thing that provides storage cannot itself arrive through it.
- **Auth schemes and anything with middleware-ORDER significance** must be image-shipped: the
  pipeline is composed at boot, before any install has run.
- **The loader itself** and the persistence contracts it reads.

Everything else can be registry-served, and the switch between the two lanes is one line per host:
a module in the image closure is listed under `Modules:Assemblies`; a module from the registry is
listed under `Modules:Required` and installed by its Store entry (`preInstalled` for the ones a
first-party deployment must not be without).

🚨 **The two lists are mutually exclusive for one name, and the exclusion is enforced, not advisory.**
`ComputeEffectiveModuleEntries` takes the baseline first and dedupes the persisted entry away by
name, so a leftover `Modules:Assemblies` line SHADOWS a landed store module — the deployment binds
an app-closure copy that a later image may not even ship. On the install side the landing service
answers **409** while any host still carries the same-named DLL in its closure. So flipping a module
from image to registry means dropping the `ProjectReference` and the baseline entry in the same
change set that publishes the Store entry.

### Source — platform repo vs node repo

A module's source may live in a node repo only when **nothing the platform's own bake host must
compile depends on it**.

The bake host (`tools/MeshWeaver.PluginTester`) compiles the platform's gated content — the sample
trees `.github/scripts/stage-samples-gate.sh` stages — and it builds what it needs **from the
platform checkout**. It can therefore land a module the way a portal does, from a tester-local
`MeshModuleClosure` row, only for as long as that module's source is still in the platform tree.

This gives the ordering rule for any carve-out:

> A module's **delivery flip** — out of the image, out of the canonical content surface — can happen
> while its source is still in the platform repo. Its **source move** cannot, until the bake host
> consumes a node-repo-BUILT bundle instead of building from source.

Two live examples of each side of that line: the AI engine has flipped delivery (registry-served,
`Modules:Required`) while `src/MeshWeaver.AI` remains in the platform repo, because the gate still
builds it there. `MeshWeaver.Maps` cannot move its source at all yet, because gated sample content
(`Cornerstone/Pricing`) uses `MapControl`/`MapMarker` and the gate has no other way to obtain the
assembly.

### The canonical content surface follows delivery, not source

`FrameworkBuildIdentity.ContentSurfaceAssemblies` is the set in-mesh content may compile against,
and it is *defined* as the bake host's transitive `MeshWeaver.*` closure. When a module leaves the
image it leaves that set too — and three things must move together, or hosts fork their identity:

1. the name comes out of `ContentSurfaceAssemblies` **and** out of the bake host's reference closure
   (the equality between the two is asserted by `FrameworkBuildIdentityTest.CanonicalList_MatchesTheTesterClosure`,
   which recomputes the closure from the csproj graph — never satisfy it by editing the list alone);
2. the bake host gains the tester-local `MeshModuleClosure` row so content that references the
   module still compiles (`CompileReferences.ComposeWithModules` puts installed modules into the
   reference set);
3. anything that arrived **transitively** through the removed reference and is still content surface
   gets re-anchored directly — dropping one reference drops everything it pulled in.

## Modules and the in-mesh compiler

In-mesh source compiles against the platform's `TRUSTED_PLATFORM_ASSEMBLIES` **plus this mesh's
installed modules**: `InstallAssemblies` records every loaded module as an
`InstalledModuleAssembly` DI singleton, and `MeshNodeCompilationService` composes its reference
set from both — so a module published outside the app closure stays visible to scope classes and
NodeType source that reference it (e.g. a map control). Two boundaries stand:

- **Kernel cells — the pack-scripting seam (#1649).** Executable `--render` cells compose their
  reference set per SESSION, not from the frozen process snapshot alone: every installed module
  joins automatically (`MeshScriptEnvironment.SessionAssemblies` enumerates the
  `InstalledModuleAssembly` registrations — modules are Default-ALC file-backed, so the runtime
  bind is free), and a dynamic NodeType joins by DECLARING it — `cellSurface: true` in its
  definition (the pack's `index.json`). At session init the kernel resolves each cell-surface
  type's CURRENT baked assembly through the assembly store + compilation cache, references its
  PE, and binds its collectible load context by name — scoped to the session's declared set,
  never a blanket hook. Assemblies in collectible load contexts never enter the frozen snapshot,
  so the cell surface is a declaration, not a load-order lottery. Two rules follow:
  a `cellSurface` NodeType's `Source/` is **single-home** — any other NodeType that
  `shared=`-consumes it fails its compile with a message naming the owner (the CS0433
  duplicate-type class, prevented by construction); and a live session **pins** the generation it
  bound — sessions are short-lived, and a recompile mid-session keeps old sessions on the old
  generation while new sessions bind the new one (the same semantics live layout areas have).
- **The bake fingerprint is DECISIVE.** Every successful NodeType compile stamps
  `CompiledModulesHash` — a hash of the sorted installed-module MVIDs
  (`InstalledModulesFingerprint`) — beside `CompiledFrameworkVersion`, and the usable-build check
  (`HasUsableBuild`) invalidates a build stamped with a DIFFERENT non-null hash than the live set,
  while its rebuild-kickoff twin (`HasStaleFrameworkBuild`) re-drives the compile for it. That is
  what makes a module-only update safe: a store install lands new module MVIDs without changing
  the framework MVID, and baked builds that could reference the replaced module rebuild on the
  next boot instead of throwing `MissingMethodException` at activation. Definitions stamped before
  the feature carry `null`, which compares as MATCH — such builds predate modules in the compile
  surface and stay governed by the framework rule; call sites without a mesh in scope pass no hash
  and likewise keep the framework-only behavior.

## Related

UI contributed as data (menus, settings tabs, whole top-bar menus — `UiContribution` nodes) is
[UI Extensibility](/Doc/Architecture/UiExtensibility). Content plugins and their registry are
[Plugins](/Doc/Architecture/Plugins) and [Plugin Packaging](/Doc/Architecture/PluginPackaging).
Deployment surfaces: [Feature Flags](/Doc/Architecture/FeatureFlags) ·
[Environment Composition](/Doc/Architecture/EnvironmentComposition) ·
[Deployment](/Doc/Architecture/Deployment).

**Modules and composition are different axes, deliberately.** Which compiled ASSEMBLIES a deployment
loads is `Modules:Assemblies` (plus the persisted store installs above) — decided before the DI
container exists, so it cannot be a mesh-level decision. Which CONTENT PACKAGES an environment
carries is [Environment Composition](/Doc/Architecture/EnvironmentComposition)'s `Features:Flags:*`,
reconciled by the boot install pass. A Store package that carries a module rides both: its content
lands through the composition lane, its assemblies through the bundle lane above.
