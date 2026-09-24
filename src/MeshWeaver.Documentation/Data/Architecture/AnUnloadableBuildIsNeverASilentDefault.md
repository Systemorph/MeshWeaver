---
Name: An Unloadable Build Is Never A Silent Default
Category: Architecture
Description: A per-instance hub whose type's recorded build does not LOAD in this process used to bind the mesh default configuration for its whole life, silently. On the control instance that took down the always-activated Hosting/PlatformBuilds hub — webhook inbox, fleet watch, build queue, triage intake and self-update routing — for twenty hours while every record read Ok (#4471). What was measured, the seam, the fix, and two hypotheses the measurements refuted.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 9v4"/><path d="M12 17h.01"/><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/></svg>
---

# An Unloadable Build Is Never A Silent Default

**The rule.** When an instance activates, its hub binds the configuration its NodeType's recorded
build yields. If that build does not **load** in this process, the instance serves the
assembly-unavailable **diagnosis** — after one bounded recompile — and never the mesh default
configuration. A hub resolves its configuration exactly once, so a default binding is not a slower
first paint: it is the whole life of the grain without any of the type's handlers, layout areas or
`WithInitialization` watchers.

This is the third branch of the same rule. [#3006](https://github.com/Systemorph/MeshWeaver/issues/3006)
cured it for a type whose first build had not been kicked off, and
[The Dependency Record Floor](../DependencyRecordFloor) (clause 4, #3934) cured it for a recorded
build whose bytes the store could not resolve. The branch where the bytes **are** resolved and then
fail to load kept the old behaviour until
[#4471](https://github.com/Systemorph/MeshWeaver/issues/4471).

## What was measured — memex.systemorph.com, 2026-09-16

| when (UTC) | what |
|---|---|
| 00:19:40 | last `Ops/Status` written by the fleet watch, on the pods about to be replaced |
| 00:43 / 00:46 | the two new pods start (`3.0.0-ci.8710`) |
| 00:49:15.385–.391 | pod `…-ztqz8`, level Error, `MeshNodeCompilationService`: **`Failed to load assembly for Hosting/PlatformBuildInbox — the per-node hub for this NodeType (and every instance of it) cannot activate`** ×2 — and the same line for `Hosting/ModuleReportInbox` ×2, `Hosting/LogEntry`, `Hosting/InstanceAction`, `Hosting/ModuleInventory` and `Store/Catalog`, all inside four seconds |
| 00:49:37 | `Hosting/PlatformBuildInbox/_Activity/compile-state` re-verified `AdoptedVerified`, `Succeeded` — the type itself was fine |
| 01:02 → 21:09 | 125+ `WebhookEvent`s land in `Hosting/PlatformBuilds/_Inbox`, every one at `version: 1`; zero `[FleetWatch]` lines of any kind (#4471's comments) |
| 21:17 | the control instance is restarted (a Restart action, same image) |
| 21:2x | the `Inbox` area on `Hosting/PlatformBuilds` reads *"Watcher armed … seen 303, processed 303, deferred 0"*; `_Inbox` is empty |

The log lines were read from `Ops/Logs` entries an earlier `Logs` action had already landed (selector
`{namespace="memex"} |~ "(?i)(…|fail|exception|…)"`); nothing was written to take them.

Two facts carry the diagnosis. The hub **was** activated — it served a 172,350-character read at
08:30 — yet not one of its type's initialisers ran; and the types whose instances were re-activated
later (`Hosting/InstanceAction`, `Hosting/TriageItem`, `Hosting/LogEntry`, `Hosting/ModuleInventory`)
all healed within the hour, while the one instance the Hosting module's `InboxHubAnchor` holds
activated for the life of the process never did. An anchored hub is never re-activated, so whatever
it bound at 00:49 was what it served until the restart.

🚨 **What is inferred.** The default bind itself was not observed — that branch logged nothing, which
is half of the defect. What was observed is the load failure, a hub that answered reads while running
none of its initialisers, and the recovery on re-activation; the seam below is the only path in the
activation code that produces that combination silently, and the regression test reproduces it
(including the production log line, word for word) against a real loader.

## The seam

`NodeTypeEnrichmentHelpers.ApplyStreamResult`, usable-build branch (and its pinned-release twin):

1. `HasUsableBuild` is true — the record names a build for the live framework.
2. `ResolveAssembly` returns a local path — the bytes are there.
3. `GetConfigurationsFromExistingAssembly` → `CompileResultFromAssembly` → `LoadNodeAssembly`
   answers `null` (the file vanished, was deleted as older than the framework, or is a bad image) and
   the result comes back with **no `AssemblyLocation`**, no configurations, and the loader's reason
   appended to its log as an Error.
4. The caller took `matching?.HubConfiguration` — `null` — and bound `ApplyEntry(hubConfig: null)`,
   wrapped only in the stale-assembly watcher, which fires when the **published** build changes. It
   had not changed and did not change.

No line was logged at the bind. The only trace is step 3's Error, which reads as a compile problem
of the type and says nothing about which instance just lost its configuration.

## The fix

The extraction's verdict is read before anything is bound. `UnloadableBuildDetail` answers *"did the
recorded build load here?"* from the one field every failure branch of `CompileResultFromAssembly`
leaves empty — `AssemblyLocation` — and carries the loader's own reason. A build that loaded but
declares no configuration is **not** refused: a NodeType may legitimately have none, and the default
chain is then correct.

An unloadable build goes through `RefuseUnloadableBind`, which is exactly the machinery its two
siblings already use, because the state is the same one — *a build is recorded and this process
cannot use it*:

* within the retry budget, `TriggerRecompileAndRetry` flips the type `Ok → Pending`, waits for the
  fresh build, and re-enriches against it;
* once the budget is spent, the `AssemblyUnavailable` overlay — it names the reason, sets an
  `UnhandledMessageNack` so a typed request gets a terminal `DeliveryFailure` naming the type instead
  of being ignored, and self-heals on the next NodeType write.

`AnUnloadableBuildIsNeverASilentDefaultTest` stores bytes the loader rejects under a record that
reads usable, and asserts the verdict on `ApplyStreamResult` with the budget already spent: red
before (no configuration at all), green after (the diagnosis overlay). Its control stores loadable
bytes and asserts they are still bound.

## The log line names the cause, not a list of possible causes

The loader's reason (`CompilationCacheService.LastLoadFailure`: the file was absent, older than the
framework and deleted, or a bad image with its length and the volume's free space) used to reach
only the **record** — the verdict appended to the compile's log. The `Error` line
`MeshNodeCompilationService` writes said *"Common causes: corrupt cached .dll …, source compilation
error …, or missing dependency"* and never which one applied.

That mattered because the record is the wrong place to look for this failure. Measured on
memex.meshweaver.cloud, 2026-09-23 05:31Z → 2026-09-24 01:49Z (incident `e114ad743fe17da3`,
routed to [#1126](https://github.com/Systemorph/MeshWeaver/issues/1126)): **166** sightings over a
dozen NodeTypes (`Edu/Page`, `Edu/Module`, `Publish/Slide`, `DoublePendulum/Pendulum`,
`AgenticOffice/*`, `SocialMedia/*` …), and the types read `compilationStatus: Ok` again seconds
later — `DoublePendulum/Pendulum` failed to load at 00:32:31.010Z and recorded a successful compile
at 00:32:32.445Z, so the record's copy of the reason had been overwritten before anyone could read
it. The log is the surface the incident watcher folds, so it now carries the reason itself:
`… cannot activate. … Cause: {LoadFailure} (assembly {AssemblyLocation}).`

🚨 **What this does not establish:** *why* those loads failed. That is the next sighting's to say,
which is the point of the change. The regression test asserts the line on a real
`BadImageFormatException` and was red before the change.

## Two hypotheses the measurements refuted

Both were written into #4471 before the log lines above were found, and both would have produced a
wrong fix.

**"`MeshWeaver.AI` is not loaded, so the watcher's `using MeshWeaver.AI;` cannot compile."** The
Hosting inbox chain does bind `MeshWeaver.AI` (`TriageIntake` and the results thread call
`hub.StartThread`), and `/health` did list it under `pending_module_activation`. But
`Hosting/TriageItem` — whose record carries the same `MeshWeaver.AI: min:3.0.0.0` entry — was
re-verified and **compiled in this process** on the new pods at 01:10:15; the inbox type's own
compile state was re-verified at 00:49:37; and after the restart **both fresh pods still listed
`MeshWeaver.AI` as pending while the watcher drained 303 deliveries**. Removing the dependency would
have changed nothing, and would have broken triage threads.

**"A restart activates the 8 pending modules."** It did not: the pods started by that restart (bake sweeps at 21:21 and 21:24) printed, still at 21:33,
the same eight names. The loader says why, in its own words
(`[ModuleLoad] STALE PACK: MeshWeaver.Mcp is loading /app/modules/MeshWeaver.Mcp/…`): a *baseline*
image copy is loading because no usable store-installed entry claims the name, and the remedy it
names is re-installing the module, not restarting. So `pending_module_activation`'s *"a restart
activates them"* is a promise the boot loader does not keep for these entries — a detector
disagreeing with the loader it describes. That is a separate defect and is not changed here.

## Design points recorded, not changed

**Five capabilities on one hub.** The inbox watcher, the build queue, operational-space provisioning,
the fleet watch and the layout are one `configuration` chain on `Hosting/PlatformBuildInbox`. That
concentrates the blast radius, but the failure was the **activation**, not the chain: every
`With…`-armed capability of any hub dies the same way under a default binding. Splitting the chain
into several anchored NodeTypes would multiply the hubs exposed to it without removing it — the fix
belongs where the binding is decided, which is where it now is.

**The watcher's own alarm had no reader.** `PlatformBuildInboxWatcher.Observe(hub)` returns null on a
hub serving the node without an armed watcher, and its comment calls that null *the alarm* — but it
is read only by the `Inbox` area, i.e. only while someone looks. The frozen `Ops/Status` that #4471
was filed on is the same shape one level up: a detector nothing consumes.

See also: [The Dependency Record Floor](../DependencyRecordFloor) ·
[Node Type Compilation](../NodeTypeCompilation) · [Webhook Inbox](../WebhookInbox) ·
[Operating From The Portal](../OperatingFromThePortal)
