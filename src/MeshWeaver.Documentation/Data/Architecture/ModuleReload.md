---
Name: Module Reload
Category: Architecture
Description: One request — "reload module M on this instance", or every module — that anyone authorised can file. The instance resolves the newest compatible published version (declared platform floor against the running platform, never a seal), lands it through the one landing path, activates it live or with exactly one automatic restart, and reports on the request node what it found, what landed, how it activated and what every replica loaded — or why it could not.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 4v5h-5"/><rect x="9" y="9" width="6" height="6" rx="1"/></svg>
---

# Module Reload

> **The rule (policy `module-reload-request`, [register](../PolicyNotProse)).** Any authorised caller
> can ask an instance to reload one module — or all of them — through ONE durable request. The
> instance resolves the newest COMPATIBLE published version, lands it, activates it (live when the
> module can be swapped in the running process, otherwise exactly one automatic restart through the
> self-update restart path), and reports the outcome on the request node. No governed activity, no
> approval, no person's signature, no `confirmation`.

## Why it exists

A module that ships is supposed to be in use (rule R3 of [Module Adoption Policy](../ModuleAdoptionPolicy)).
When it is not, there was no single act that made it so. The shape that motivated this request: the
control instance kept `MeshWeaver.AI` 1.20.4 loaded while the Hosting module it also ran needed
1.21 (`MissingMethodException: ThreadPreparation.set_Group`). 1.21 was published and compatible; the
remedies were a manual sync, then a hand-filed `Restart` action that asked for a `confirmation` —
two people-steps for what is one fact the instance can establish itself. The unattended lane could
not take it either: the package's own update policy and its sync-owned partition both hold an
unattended landing ([One Partition, One Bookkeeping](../OnePartitionOneBookkeeping)), and a landed
generation waited for whatever restart came next.

## The request

A `ModuleReload` node at `Admin/_ModuleReload/{id}`, content `ModuleReloadRequest`
(`MeshWeaver.Graph.Configuration`):

| field | written by | meaning |
|---|---|---|
| `module` | requester | the module's entry-assembly name (`MeshWeaver.AI`) or its package id (`AI`); blank = every installed module |
| `reason` | requester | required; carried into the restart announcement and every log line |
| `requestedBy` / `requestedAt` | requester | who asked — a user id, an agent, a watcher's name |
| `status` | executor | `Requested` → `Landing` → `Activating` \| `AwaitingRestart` → `Done` \| `Failed` \| `Faulted` (open string constants, `ModuleReloadStatus`; `Faulted` is never final — see "A crash is never final" below) |
| `items[]` | executor | per module: `runningVersion` (what the executor's process ran), `foundVersion` + `foundFloor` + `registry` (what is published), `targetVersion` (what the activation record now names), `landed`, `decision`, `failure` |
| `activation` / `activationDetail` | executor | `Live`, `Restart` or `NotNeeded`, and the lane's own sentence |
| `liveSwapRequestedAt` / `restartRequestedAt` | executor | when each process was asked to swap, or when the ONE restart was requested |
| `replicas{process}` | each process | what that process has LOADED, per module — written under its own key, so reports merge rather than clobber |
| `failure` | executor | why the request is red (or faulted), by name |
| `attempt` / `faultedAt` | re-arm / executor | how many times a `Faulted` request has been re-armed (drives the backoff, never a cap), and when the current attempt faulted |
| `log[]` | executor | every step, in order |

**The only writer is `ModuleReload.Request`**, which writes the node as System after its CALLER has
authorised the requester. The executor acts only on a request whose `createdBy` is System; anything
else that can write under `Admin/_ModuleReload` is refused by name and nothing is landed — the same
trust rule as `ActivationRecycle`.

## How it runs

The executor is the request node's OWN hub (`ModuleReloadExecutor`, registered as the node type's
initialization), so exactly one process drives a request however many replicas hear it. Every step is
a `stream.Update` on that node and every step is idempotent: an activation torn down mid-step simply
runs the step again when the node is next read.

1. **Resolve and land.** The modules come from the install records (`Plugins/*` that declare a
   compiled module). Per module, `RegistryUpdateReconciler.ReloadModule` asks the configured registries
   in order — the first that serves the package answers — through
   `PluginBundleClient.AdoptModuleOutcome` with `unattended: false`: the reload is attended, like a
   Provision click, so the package's own update policy does not decline it. It runs on the
   reconciler's serialised lane and ends by proposing the module set
   (`ModuleDependencyFloor.ProposeChecked`); a refused set is the reload's failure, because neither a
   swap nor a restart would load the module.
2. **Compatibility is the floor, never a seal.** A bundle whose declared `minMeshVersion` is above the
   running platform is not downloaded, the landed generation keeps serving, and the item reads
   `declined — version X needs platform ≥ F; running R …` (policy `package-min-mesh-version`). Neither a
   seal for the running platform's identity nor a green platform build is consulted; whether the
   bytes link is measured by the landing's link probe, as for every landing.
3. **Decide activation.** A module whose `targetVersion` is not the version THIS process has loaded
   needs activating. When every such module can be swapped (`IModuleLiveActivation.CanSwap`) the
   request goes `Activating` and every process swaps; otherwise it goes `AwaitingRestart`. Nothing to
   activate is `Done` with `NotNeeded`.
4. **Exactly one restart.** The executor stamps `restartRequestedAt` FIRST, then calls
   `IModuleActivationRestart.RequestRestart` once — the stamp is what keeps a resumed executor from
   asking twice. The portal's `SelfUpdateHostedService` implements it with its one restart path: a
   self-patch restart of the running image, or `self-update-restart-pending` handed to the control
   lane, which opens an unattended `Restart` with no approval and no confirmation
   ([Self-Update on the Control Lane](../SelfUpdateControlLane)). The poller's roll floor does not
   defer a reload — it is one explicit request — and exactly one restart still holds against the
   poller's own checks: after a self-patch restart they read the fresh roll instant and defer, and on
   the control lane an open or freshly done `Restart` already delivers the announcement.
5. **Report and decide.** Each process reports what it LOADED (`ModuleReloadAgent`, armed on the mesh
   hub of every process): after a restart only a process that BOOTED after `restartRequestedAt`
   reports (it lists open requests at boot, which also re-activates the executor), and after a live
   swap every process reports once it has swapped. A report is measured, not claimed: the generation
   directory each module was loaded from (`ModuleActivationStatus.LoadedModuleGenerations`) read
   against the activation record. `ModuleReload.Evaluate` decides over the counted reports — reports
   from processes the cluster has recorded as gone are not counted — and the request is `Done` when
   every counted replica loads every target version, or `Failed` naming the replica and both versions.
   🚨 `Done` is a verdict over the WHOLE roster: where the cluster can enumerate its running members
   (`IClusterMembership.AliveMembers`), every one of them must have a counted report first, so a pod
   still booting or still swapping keeps the request open (its swap may yet fail and need the restart
   fallback), and an old pod still running after a restart keeps it open until it is gone. A roster
   change re-evaluates the request (`IClusterMembershipFeed`), since no node write accompanies it. A
   swap failure or a version mismatch on a counted report is decisive at once. Without a roster
   (monolith, no cluster) the counted reports are all there is. A running member that never reports
   leaves the request open — visible on the node, never silently `Done`.
   A failed live swap leaves the previous generation serving and falls back to the one restart.

## A crash is never final

The system repairs itself, so a step that CRASHED must never end a request. The executor tells two
kinds of red apart:

| status | what produced it | final? |
|---|---|---|
| `Failed` | a DECIDED answer: a floor above the running platform, no installed module of that name, a refused bundle, a replica that loads the wrong version after activation, a request not written by System, a host with no restart path | yes — asking again would get the same answer |
| `Faulted` | a CRASH: an exception in the landing or restart step, a registry that could not be asked (e.g. a 503), a module-set proposal that faulted, a reconcile lane that was shutting down — any item whose `transient` flag is set (`ModuleAdoptOutcome.Transient`) | **never** — retried |

A request with several modules is `Faulted` when ANY failing module crashed: that module's answer is
still unknown (`ModuleReload.OutcomeOf`).

**The retry has no timer of its own.** Every full reconcile pass of the plugin catalog
(`RegistryUpdateReconciler`: the boot pass, each safety-net tick, `ReconcileNow`) ends by calling
`ModuleReload.RetryFaulted`: a LISTING of `Admin/_ModuleReload`, a read of each open request from its
own node stream, and a re-arm (`ModuleReload.Rearm`, written through `GetMeshNodeStream(path).Update`
as System) of every `Faulted` request whose backoff is due. The backoff is the pass's own cadence,
doubled per attempt — `faultedAt + interval × 2^min(attempt, 5)`, the interval being
`PluginCatalog:ReconcileSafetyNetInterval` (30 min); the doubling stops at 32 intervals, the retries
never do. With the safety net switched off, the boot pass is the retry.

The re-arm keeps the node and its log, increments `attempt`, clears every executor-owned field of the
faulted attempt (items, activation, replica reports, the restart stamp, the failure) and sets the
status back to `Requested`; the executor then runs the new attempt from the top. 🚨 The one-restart
rule therefore holds **per attempt**: a retry of a request that faulted after its restart was stamped
asks for a restart again.

## Who can file one

| surface | authorisation | how |
|---|---|---|
| MCP `reload_module` (MeshWeaver.Plugins `McpMeshPlugin`) | `IsGlobalAdmin` — a platform admin on the Admin partition | `MeshOperations.ReloadModule(module, reason)`; returns `{status, path, message}` at once, read the node for the outcome |
| the package page — node menu **🔄 Reload module** on an installed package that declares a module | `IsGlobalAdmin`, checked for the menu entry and again on the click | the `ReloadModule` area (framework controls, en + de) files the request and opens its page |
| the platform's own watchers (the fleet-target intake, a release follow-through, a watchdog) | they run in-process as the platform | `ModuleReload.Request(hub, …)` directly |

## What this replaces, and what it does not

**`RefreshModules` self-filing is generalised into this request.** The fleet-target intake
(MeshWeaver.Plugins `Hosting/PlatformBuildInbox` → `FleetTargetIntake`) used to file a Store
`Maintenance` task `RefreshModules` for a module of its OWN instance held behind by its sync. That
task re-runs the registry installer and reports "RESTART the deployment to activate them" — it lands
and stops. The intake now files a `ModuleReload` for that module instead, which lands AND activates
AND reports what loaded. The `RefreshModules` maintenance task itself stays: it is the operator's
content-and-module re-install of a whole package, a different act.

**The live swap belongs to the live module loader** (`Doc/Architecture/LiveModuleUpdate`, MeshWeaver#6121 —
slice 1 of that work puts every module in its own collectible load context; the swap is its slice 2).
This request does not fork it: `IModuleLiveActivation` is the reload's CALL SITE, registered by the
loader once it can swap. Until a host registers it, every reload that needs activating takes the one
restart — which is exactly the fallback rule 3 of that page prescribes.

## Auto-update (policy `packages-auto-update`)

Every installed package updates by itself as soon as a newer compatible version is published. The
unattended lane is the same machinery as an explicit reload, driven by the registry instead of a
person:

- **When it runs.** `RegistryUpdateReconciler` reconciles on boot, on every `ModulePublished`
  broadcast the registry posts to this instance's inbox (minutes after a publish), and on its
  safety net (every 30 min). Each pass reads the registry's feed and bundle index — the package's
  OWN publish is the only event it needs.
- **What decides.** Two inputs and nothing else: a newer version is served, and its declared floor
  is met (`ModuleUpdateDecision` with `PackagePlatformFloorGate.HoldFor`). An incompatible floor is
  declined by name — on the install record (`heldUpdate`) and in the log — and the landed generation
  keeps serving.
- **What activates.** A pass that LANDED anything files ONE `ModuleReload` request for what it landed
  (`auto-{hash of the landed set}`, so replicas and re-runs file it once). The request activates it —
  live, else exactly one automatic restart. A second wave that lands before that restart has
  happened RIDES it (the executor finds the open request's restart stamp and does not ask for
  another).
- **Defaults.** A fresh install is `Auto` (`PackageInstaller.SeedUpdatePolicy`); the deployment-wide
  `DefaultUpdatePolicy` / `AutoUpdateByDefault` are no longer consulted. At boot,
  `PackageAutoUpdateMigration` moves every record SEEDED with the old reminder-only default to `Auto`
  through `stream.Update` (as System). It keeps — and names in one Warning per pass — a pin (`None`)
  and any policy a global administrator CHOSE on the catalog card (`updatePolicySetAt`, stamped by
  `SetUpdatePolicy` from now on).

### What no longer holds an update — and what still does

| dependency | status |
|---|---|
| a platform image build, deploy, roll, CD or "arm the fleet" step | never consulted by the package lanes |
| a publication seal / a seal for the running framework identity | never consulted — the module lane decides on the floor and the link probe; the framework MVID is recorded, never a gate |
| a green build of the platform or of the package repo's `main` | never consulted — only the package's own publish to the registry |
| the partition's SYNC SOURCE (#4355 gate 1b: "its module waits for the same seal its content does") | **retired for the module half**: a published compatible module lands even when the partition's `_GitSync` still carries older content |
| the package's own policy | `Auto` by default; a pin or an administrator's explicit choice still declines, by name |
| the CONTENT half of a sync-owned partition | **still the sync's** — one writer per partition (#4355). The installer does not write content into a partition a sync source owns; that content follows its sync, and the sync source's own seal gate (`SealedSyncGate`) is not changed here. The code no longer waits for it. |

Measured on the control instance before this change (read-only `search namespace:Plugins
nodeType:Package`): **80 install records, 0 of them not Auto** (75 declare `updatePolicy: Auto`, 5
predate the field and carry `autoUpdate: true`). The reminder-only default was not what held its
modules; the sync-owned hold on the module lane was.

## What is NOT established

- **A real cross-process run.** The scenario tests (`Memex.Portal.Shared.Test` → `ModuleReloadByRestartTest`,
  `ModuleReloadLiveTest`) run the real registry client, landing, reconciler, request node, executor,
  agent and `SelfUpdateHostedService` in one monolith process; what they SIMULATE is which generation a
  process has loaded and the process that boots after the restart. A Kubernetes restart and a
  multi-replica Orleans cluster were not exercised.
- **"Newest compatible" across versions.** The registry's bundle index advertises ONE version per
  package. When that version's floor is above the running platform the reload declines by name and
  N keeps serving; an older-but-newer-than-N version that WOULD be compatible is not discoverable from
  the index as it stands.
- **The sync-owned hold.** An attended reload lands the module even when the package's partition is
  owned by a sync source whose content has not caught up — the same as a Provision click. The item's
  `decision` says what was landed; the content half follows the sync.
- **A restart that never comes.** A restart handed to the control lane that the control plane never
  executes leaves the request `AwaitingRestart` with the hand-over sentence on it — visible, not
  retried (it did not crash; it is waiting).
- **Every crash is classified.** `Faulted` covers the exceptions caught in the executor and in the
  registry/landing calls it makes. A bundle download that answers a transient error as a `Kind`
  (`the bundle X could not be fetched (…)`) is still reported as a decided `Failed`; a restart lane
  that answers "not scheduled" is `Failed` too. The Plugins-side self-update intake
  (`FleetTargetIntake`) mirrors these status names and treats `Failed` as terminal; it does not yet
  know `Faulted`.
