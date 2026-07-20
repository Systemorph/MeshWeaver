using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Internal fire-and-forget message posted by <see cref="NodeTypeCompilationHelpers.InstallCompileWatcher"/>
/// when it observes a Pending → Compiling transition request on the NodeType's
/// own MeshNode. The handler (<see cref="NodeTypeCompilationHelpers.HandleDispatchCompile"/>)
/// runs on the per-NodeType hub's ActionBlock — that is the single-threaded
/// dispatcher that owns "drive a compile for this NodeType." Routing the
/// dispatch through a message instead of executing in the watcher's Subscribe
/// callback eliminates the cross-scheduler deadlock where the callback fired on
/// the workspace emission thread and synchronously waited on a GetQuery
/// cold-cache (Acme layout-area-render hang, 2026-05-24).
/// </summary>
/// <param name="PendingNode">Snapshot of the NodeType MeshNode at the moment
/// CompilationStatus = Pending was observed. The handler ships it inside
/// <see cref="RunCompileRequest"/> so the activity reads the trigger-time
/// state (ReleaseNotes etc.) without re-fetching through the mesh-hub-cached
/// remote stream.</param>
public record DispatchCompileTrigger(MeshNode PendingNode);

/// <summary>
/// Static helpers for NodeType compilation, owned by the per-NodeType hub
/// (the actor that "is" the NodeType). The hub is at <c>Address(nodeTypePath)</c>;
/// its own <see cref="MeshNode"/> carries every property the compile needs
/// (<c>NodeTypeDefinition.CompilationStatus</c>, <c>CompilationError</c>,
/// <c>AssemblyLocation</c>, …) and the result of every compile is written
/// back to that same MeshNode. The NodeType is its own boss
/// (see <c>Doc/Architecture/SyncedMeshNodeQueries.md</c> +
/// <c>feedback_dirty_flag_on_owner</c>).
///
/// <para>This file exists so the auto-watcher and the on-demand
/// <c>CreateReleaseRequest</c> handler share one body (<see cref="RunCompile"/>)
/// and so the soon-to-be-deleted <c>NodeTypeService</c> stops being the home
/// of compilation logic.</para>
///
/// <para>Reactive end-to-end — no <c>await</c>, no <c>.ToTask()</c> at this
/// layer; the only Task is buried inside
/// <see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/>
/// which wraps the Roslyn invocation as <c>Observable.FromAsync</c>.</para>
/// </summary>
internal static class NodeTypeCompilationHelpers
{
    /// <summary>
    /// Subscribes to the per-NodeType hub's own MeshNode stream and auto-fires
    /// <see cref="RunCompile"/> whenever <see cref="NodeTypeDefinition.CompilationStatus"/>
    /// flips to <see cref="CompilationStatus.Pending"/>. Wired from the per-NodeType
    /// hub's <c>WithInitialization</c> hook (<c>SubscribeToOwnDeletion</c>) so the
    /// watcher's lifetime matches the hub's.
    ///
    /// <para>Trigger model: callers that previously called
    /// <c>NodeTypeService.InvalidateCache(path)</c> or <c>GetAssemblyPath(path)</c>
    /// (which lazily compiled) now write <c>CompilationStatus = Pending</c> to the
    /// NodeType MeshNode via <c>workspace.GetMeshNodeStream(path).Update(...)</c>.
    /// The watcher sees the flip and runs the compile; the result lands on the
    /// MeshNode and every subscriber sees it through synced-query fan-out.</para>
    /// </summary>
    private static int _watcherInstallCount;

    public static IDisposable InstallCompileWatcher(
        IMessageHub hub,
        IWorkspace workspace,
        IMeshNodeCompilationService compilationService)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.CompileWatcher");
        var accessService = hub.ServiceProvider.GetService<MeshWeaver.Messaging.AccessService>();
        // 🅿️ Park registry — the parked short-circuit below refuses to dispatch Roslyn for a
        // NodeType whose compile already terminally failed, so a broken type can never drive
        // the recompile storm that saturates this hub's single-threaded action block.
        var parkRegistry = hub.ServiceProvider.GetService<NodeTypeCompileParkRegistry>();

        var installSeq = System.Threading.Interlocked.Increment(ref _watcherInstallCount);
        logger?.LogDebug(
            "Compile watcher: install#{Seq} on {HubPath}",
            installSeq, hub.Address.Path);

        // No in-memory single-flight flag. CompilationStatus on the
        // NodeTypeDefinition IS the lock: the watcher atomically transitions
        // Pending → Compiling inside the Update lambda and dispatches the
        // activity only when WE were the one that made the transition. Every
        // Pending-flipper (the kickoff below, the CreateReleaseRequest handler
        // in MeshDataSource.DispatchPendingFlip) is status-guarded so two
        // independent requests can't both result in Pending while a compile is
        // already requested or running.

        // Eager kickoff on hub activation: when the per-NodeType hub starts and
        // its own NodeTypeDefinition is NOT backed by a usable compiled
        // assembly, flip CompilationStatus = Pending on its OWN stream so the
        // watcher below fires Roslyn immediately. This is a LOCAL UpdateOwn —
        // it lands on the hub's own MeshNode, which the watcher (same hub)
        // observes.
        //
        // 🚨 Verify-before-skip — the kickoff must NOT trust a bare
        // CompilationStatus == Ok. CompilationStatus + AssemblyLocation are
        // runtime state, but they are persisted into the NodeType's own
        // MeshNode JSON. A stale Ok therefore survives across process
        // boundaries: it can be baked into seed/sample data by a previous run
        // (the test-seed-pollution class of bug), or it can point at a temp /
        // .mesh-cache assembly that has since been cleaned up. Trusting it
        // strands the NodeType — the kickoff skips, no recompile runs, and
        // every instance hub falls back to the default config (no
        // MeshNodeReference reducer → "No reducer defined for
        // MeshNodeReference" on every subscribe). The ONLY safe skip condition
        // is "Ok AND the compiled assembly still exists on disk"
        // (<see cref="HasUsableBuild"/>); everything else — null / Unknown /
        // Compiling (interrupted) / Error / Ok-but-assembly-gone — recompiles.
        //
        // 🚨 Kickoff deleted 2026-05-21. Previously this block subscribed to the
        // NodeType's own MeshNode stream and, on its FIRST emission, flipped
        // CompilationStatus = Pending whenever HasUsableBuild was false — i.e.
        // every grain activation against a never-compiled / freshly-deployed
        // dynamic NodeType auto-triggered a recompile. Prod 2026-05-21 trace:
        //   "AccessControlPipeline: Access denied: user 'sync/...' lacks
        //    Create permission on 'Systemorph/EventCalendar'"
        //   "Compile watcher: activity start emitted EMPTY for
        //    Systemorph/EventCalendar — running compile inline (deadlock-
        //    fallback)"
        // The activation fan-out (synced queries, NodeType enrichment,
        // background grain hydration) carried whichever AccessContext happened
        // to be on the inbound delivery — typically NOT a user with Create on
        // the partition. The kickoff therefore drove an endless "try to create
        // activity → denied → inline fallback → next activation reruns" loop
        // visible in Loki as a steady stream of "lacks Create" denials.
        //
        // Compile is now an EXPLICITLY user-triggered operation:
        //   - User clicks the Compile button in NodeType's Overview panel
        //     (NodeTypeLayoutAreas.BuildCompileStatusPanel) → the click flips
        //     RequestedReleaseAt on the NodeType's own stream.
        //   - InstallReleaseRequestWatcher observes the flip → promotes it to
        //     CompilationStatus = Pending under the USER's AccessContext.
        //   - The watcher block below picks up Pending and dispatches the
        //     activity through NodeTypeCompilationActivity.Start — the
        //     activity CreateNode runs with the user's identity, so
        //     AccessControl rejects without Edit (intended) or accepts and
        //     produces a Release attributed to the user.
        // The IsDirty computed property on NodeTypeDefinition stays as
        // informational state — UI uses it to enable the Compile button, but
        // it NEVER auto-fires a recompile. Doc: AccessContextPropagation.md.
        var ownStream = workspace.GetMeshNodeStream();

        var hubPath = hub.Address.Path;
        // Single-flight is STATUS-BASED, not an in-memory flag. The watcher posts a
        // DispatchCompileTrigger on every transition INTO Pending; HandleDispatchCompile
        // then atomically transitions Pending → Compiling inside the per-NodeType hub's
        // serialized ActionBlock Update (`if Status != Pending return curr`), so only the
        // FIRST trigger of a burst starts an activity — every later one no-ops.
        //
        // `DistinctUntilChanged(status)` coalesces duplicate Pending emissions at the
        // Subscribe layer so we don't flood the inbox; it fires once per Pending
        // transition. The previous in-memory `dispatchInFlight` flag + a `resetSub` that
        // cleared it on a settled emission was FRAGILE: when a terminal state (Error/Ok)
        // was written cross-hub by the activity handler and didn't re-emit on this OWN
        // stream as a status the reset filter recognised, the flag stuck at 1 and the
        // NEXT Pending was coalesced away → the NodeType wedged at Compiling on the
        // SECOND compile (CodeEditRecompileTest.FailedCompile / recompile). Status-based
        // single-flight has no such latch.
        //
        // 🚨 The watcher does NOTHING but post to the OWN hub — no Update / activity
        // start / GetQuery wait inside the Subscribe callback (those can deadlock when
        // the callback fires on the workspace emission thread that coincides with the
        // ActionBlock). HandleDispatchCompile owns all the work on the hub's ActionBlock.
        var watcherSub = ownStream
            .Where(node => node?.Content is NodeTypeDefinition)
            .DistinctUntilChanged(node => ((NodeTypeDefinition)node!.Content!).CompilationStatus)
            .Where(node =>
            {
                var def = (NodeTypeDefinition)node!.Content!;
                // Truly-static NodeTypes (HubConfiguration delegate set AND no source
                // code) ship their assembly with the framework — nothing to compile even
                // if something flips them Pending. A dynamic NodeType whose source string
                // compiled into a delegate still needs a real assembly emit, so allow
                // Pending through when source exists.
                return def.CompilationStatus == CompilationStatus.Pending
                    && !(node.HubConfiguration is not null
                        && string.IsNullOrWhiteSpace(def.Configuration)
                        && string.IsNullOrWhiteSpace(def.HubConfiguration)
                        && (def.Sources is null || def.Sources.Count == 0));
            })
            .Subscribe(
                pendingNode =>
                {
                    // 🅿️ PARKED short-circuit. A prior compile of this NodeType reached a
                    // terminal failed state, so we do NOT re-run Roslyn — that is the
                    // containment that turns a broken type from a portal-wide recompile storm
                    // (every Pending flip = a fresh Roslyn pass on the action block) into a
                    // single, bounded, visible failure. Every DELIBERATE retry (a release
                    // request — the UI Compile button, the MCP compile/recycle tools) goes
                    // through InstallReleaseRequestWatcher, which un-parks BEFORE promoting to
                    // Pending — so this guard never blocks a legitimate retry, only a stray
                    // re-trigger (a persisted-Compiling recovery kickoff, a legacy direct flip).
                    if (parkRegistry?.IsParked(hubPath) == true)
                    {
                        logger?.LogDebug(
                            "Compile watcher: {HubPath} is PARKED (terminal compile failure) — " +
                            "skipping recompile, serving cached error", hubPath);
                        // 🅿️ Wedge-close: the Pending flip we refuse to dispatch was already
                        // WRITTEN to the node. Without a settle write-back the type would sit
                        // at Pending forever — every settle-waiter (get_diagnostics' / the
                        // compile tool's Where(Ok|Error), WaitForLatestRelease) hangs to its
                        // timeout and the stray trigger never reaches a sink. Re-settle
                        // Pending → Error with the CACHED parked error so the trigger is
                        // answered (bounded, no Roslyn) while the park stays in place. Same
                        // fire-and-forget UpdateOwn shape as the kickoffs below; System scope
                        // because re-settling framework state is infrastructure, not a user
                        // write.
                        var parkedError = parkRegistry.GetParkedError(hubPath);
                        using (accessService?.ImpersonateAsSystem())
                            workspace.GetMeshNodeStream().Update(curr =>
                            {
                                var parkedDef = curr.ContentAs<NodeTypeDefinition>(
                                    hub.JsonSerializerOptions, logger);
                                if (parkedDef is null) return curr;
                                // Only re-settle the stray Pending — never clobber a state a
                                // genuine (un-parked) compile has already moved past it.
                                if (parkedDef.CompilationStatus != CompilationStatus.Pending)
                                    return curr;
                                return curr with
                                {
                                    Content = parkedDef with
                                    {
                                        CompilationStatus = CompilationStatus.Error,
                                        CompilationError = parkedError
                                            ?? parkedDef.CompilationError
                                            ?? "Compilation is parked after a terminal failure; request a release (Compile) to retry."
                                    }
                                };
                            }).Subscribe(
                                _ => { },
                                ex => logger?.LogWarning(ex,
                                    "Compile watcher: failed to re-settle parked {HubPath} from Pending to Error",
                                    hubPath));
                        return;
                    }

                    logger?.LogDebug(
                        "Compile watcher: saw Pending for {HubPath} — posting DispatchCompileTrigger to OWN hub (system identity)",
                        hubPath);
                    // 🚨 Compilation runs under SYSTEM identity — circumventing
                    // RLS by design. The access check that gates compilation is
                    // upstream: the user has to be permitted to flip
                    // RequestedReleaseAt on the NodeType's MeshNode (checked by
                    // the owning hub's AccessControl pipeline at submit time).
                    // Once requested, the compile activity runs as
                    // system-security so it can read every source file across
                    // the mesh, write the activity log without per-flag RLS
                    // probing, and emit the compiled assembly. NOT FromNode —
                    // compile-as-the-last-editor-of-the-NodeType would deny
                    // access to source files owned by other users.
                    using (MeshWeaver.Mesh.Security.AccessContextScope.AsSystem(accessService))
                    {
                        // Fire-and-forget. ActionBlock picks it up and runs
                        // HandleDispatchCompile on the hub's thread; the
                        // delivery.AccessContext is stamped with system identity
                        // so every downstream write inside the activity bypasses
                        // RLS.
                        hub.Post(new DispatchCompileTrigger(pendingNode!),
                            o => o.WithTarget(hub.Address));
                    }
                },
                ex => logger?.LogWarning(ex,
                    "Compile watcher faulted for {HubPath}", hub.Address.Path));

        // 🚨 2026-05-21 (PM) — First-build-only kickoff (safer variant).
        //
        // The original kickoff was deleted because it fired on every grain
        // activation when HasUsableBuild=false (prod EventCalendar loop). This
        // variant is GUARDED:
        //   1. CompilationStatus is null → truly never-compiled (any prior
        //      compile attempt — success or failure — sets a non-null status).
        //      After the kickoff transitions status to Pending → Compiling →
        //      Ok/Error, subsequent grain activations don't re-fire. No loop.
        //   2. Take(1) — explicit one-shot at the Rx layer in addition to the
        //      status guard. Belt-and-suspenders against any churn that briefly
        //      flips status back to null.
        //   3. ImpersonateAsSystem — the kickoff is framework-internal first-
        //      build, not a user action. Avoids the per-user "lacks Create"
        //      denials that drove the prod loop (background grain activations
        //      carried whatever AccessContext was on the inbound delivery —
        //      typically NOT a user with Create on the partition).
        //
        // This restores the test-time behaviour where samples (FutuRe,
        // Cornerstone, graph/type, …) auto-compile on first activation so
        // ~22 dynamic-NodeType-dependent tests don't need to inline an
        // explicit RequestedReleaseAt + wait sequence in every fixture.
        // Per-user recompile (Compile button, dirty re-build) stays explicit
        // via InstallReleaseRequestWatcher — that's the "compile is user-
        // triggered" directive that the prod fix established.
        var firstBuildKickoffSub = ownStream
            .Where(node => node?.Content is NodeTypeDefinition def
                && def.CompilationStatus is null
                && !HasUsableBuild(node, def)
                // Same truly-static exclusion as the watcher above: the
                // HubConfiguration delegate IS the configuration; nothing to
                // Roslyn-compile.
                && !(node.HubConfiguration is not null
                    && string.IsNullOrWhiteSpace(def.Configuration)
                    && string.IsNullOrWhiteSpace(def.HubConfiguration)
                    && (def.Sources is null || def.Sources.Count == 0)))
            .Take(1)
            .Subscribe(node =>
            {
                // Flip CompilationStatus directly to Pending. The watcher (above)
                // observes the Pending transition and drives the actual compile.
                // Crucially: do NOT touch RequestedReleaseAt. RequestedReleaseAt
                // is the USER-DRIVEN release trigger (Compile button); a kickoff
                // setting it would (1) misattribute the build to "user action"
                // in audit logs, and (2) trip tests that assert
                // `RequestedReleaseAt is null` after first-build (see
                // CodeEditRecompileTest.PressingCompileButton_…). Kickoff is
                // infrastructure — bypass the release trigger entirely.
                logger?.LogDebug(
                    "First-build kickoff: NodeType {HubPath} has CompilationStatus=null and no usable build — flipping CompilationStatus=Pending",
                    hubPath);
                var accessService = hub.ServiceProvider.GetService<AccessService>();
                using var systemScope = accessService?.ImpersonateAsSystem();
                workspace.GetMeshNodeStream().Update(curr =>
                {
                    if (curr?.Content is not NodeTypeDefinition def) return curr!;
                    // Race guard: only fire if status is still null. If another
                    // path (explicit user button, second kickoff for a Take(1)
                    // ordering race) already set status, leave as-is.
                    if (def.CompilationStatus is not null) return curr;
                    return curr with
                    {
                        Content = def with
                        {
                            CompilationStatus = CompilationStatus.Pending,
                        }
                    };
                }).Subscribe(_ => { },
                    ex => logger?.LogWarning(ex,
                        "First-build kickoff: Update failed for {HubPath}", hubPath));
            });

        // 🚨 Recovery kickoff — un-strand a NodeType that comes up persisted as
        // CompilationStatus = Compiling. This is the activity-side wake-up state
        // machine: a freshly-activated NodeType hub has NO in-process compile (the
        // compile runs on a separate Activity hub and is not a resumable job), so
        // Compiling on the FIRST init emission ALWAYS means the previous compile
        // was interrupted before its terminal Ok/Error write-back.
        //
        // When the process dies (or the per-NodeType grain deactivates) AFTER the
        // Pending→Compiling flip but BEFORE the write-back, the on-disk JSON
        // freezes at Compiling. On the next activation NOTHING re-drives the
        // compile (firstBuildKickoffSub needs null, watcherSub needs Pending,
        // InstallReleaseRequestWatcher needs a SETTLED status). So the NodeType
        // sits in Compiling forever, every instance hub falls back to the default
        // config (no MeshNodeReference reducer), and the instance page renders
        // nothing — the rbuergi/CatBond/AtlanticBond "I get nothing" symptom.
        //
        // Fix: re-request a fresh compile from the OWNER's OWN state — flip
        // Compiling→Pending so watcherSub dispatches. We deliberately do NOT probe
        // the Activity hub cross-hub: that read lags the owner's writes, and a
        // false "still running" leaves the NodeType stranded (the very bug). A
        // rare duplicate compile is harmless — it settles to the same Ok release.
        // Take(1) BEFORE the Where so a normal in-flight compile that legitimately
        // flips Compiling LATER never trips this; the idempotent re-check inside
        // the Update lambda drops the write if the genuine compile settled first.
        var recoveryKickoffSub = ownStream
            .Take(1)
            .Where(node => node?.Content is NodeTypeDefinition def
                && def.CompilationStatus == CompilationStatus.Compiling
                && !IsStaticOnlyNodeType(node, def))
            .Subscribe(node =>
            {
                logger?.LogWarning(
                    "Compile recovery: {HubPath} came up persisted as Compiling — re-triggering compile (flip Compiling→Pending)",
                    hubPath);
                var recoveryAccess = hub.ServiceProvider.GetService<AccessService>();
                using var systemScope = recoveryAccess?.ImpersonateAsSystem();
                workspace.GetMeshNodeStream().Update(curr =>
                {
                    if (curr?.Content is not NodeTypeDefinition def) return curr!;
                    // Only recover if STILL Compiling — the genuine compile may
                    // have settled between the init emission and this lambda.
                    if (def.CompilationStatus != CompilationStatus.Compiling) return curr;
                    return curr with
                    {
                        Content = def with { CompilationStatus = CompilationStatus.Pending }
                    };
                }).Subscribe(_ => { },
                    ex => logger?.LogWarning(ex,
                        "Compile recovery: re-trigger Update failed for {HubPath}", hubPath));
            });

        // 🚨 Framework-stale kickoff (issue #464, Defect 1) — PROACTIVE, level-triggered.
        //
        // The platform self-update case: a MeshWeaver redeploy changes FrameworkVersion (the
        // Graph MVID), so every NodeType whose cached assembly was built against the PREVIOUS
        // build is now ABI-stale — its bytes reference framework members that may have changed
        // signature, and will throw MissingMethodException the moment they run on the new
        // binaries. Such a type is still persisted as CompilationStatus=Ok with the OLD
        // CompiledFrameworkVersion, so NOTHING re-drives it: firstBuildKickoff needs null,
        // recoveryKickoff needs Compiling, InstallReleaseRequestWatcher needs a fresh
        // RequestedReleaseAt. The framework-stale self-heal in NodeTypeEnrichmentHelpers only
        // fires when an INSTANCE of the type is activated — a NodeType with no live instances
        // stays stale (and `compile`/CreateRelease up-to-date checks could report it clean)
        // until an operator manually rebuilds it.
        //
        // This subscription is the OWNER-side, proactive analogue: when the NodeType's own
        // hub observes a SETTLED (Ok/Error) state whose cached assembly is framework-stale, it
        // flips CompilationStatus=Pending so the compile watcher rebuilds it against the current
        // framework — no instance activation, no user click, no MissingMethodException timebomb.
        //
        // Bounded & storm-safe, mirroring firstBuildKickoff/recoveryKickoff:
        //   • Take(1) — one-shot per hub lifetime; a successful rebuild stamps the CURRENT
        //     FrameworkVersion so the type is no longer stale and never re-matches.
        //   • Skips PARKED types — a broken type that terminally failed stays parked (serving
        //     its cached error); only a deliberate retry (Compile/Recycle/UI button, which
        //     un-parks) rebuilds it. So a framework-stale type whose sources ALSO don't compile
        //     against the new framework parks after one attempt instead of storming.
        //   • Skips static-only types (nothing to Roslyn-compile).
        //   • ImpersonateAsSystem — a framework-internal self-heal, exactly like the other
        //     kickoffs (no inbound AccessContext → no "lacks Create" loop).
        var frameworkStaleKickoffSub = ownStream
            .Where(node => node?.Content is NodeTypeDefinition def
                && def.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error
                && HasStaleFrameworkBuild(def)
                && !IsStaticOnlyNodeType(node, def)
                && parkRegistry?.IsParked(hubPath) != true)
            .Take(1)
            .Subscribe(node =>
            {
                logger?.LogInformation(
                    "Framework-stale kickoff: NodeType {HubPath} assembly was compiled against framework {Compiled} but the live framework is {Live} — flipping CompilationStatus=Pending to rebuild",
                    hubPath,
                    node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.CompiledFrameworkVersion ?? "(null)",
                    FrameworkVersion);
                var staleAccess = hub.ServiceProvider.GetService<AccessService>();
                using var systemScope = staleAccess?.ImpersonateAsSystem();
                workspace.GetMeshNodeStream().Update(curr =>
                {
                    if (curr?.Content is not NodeTypeDefinition def) return curr!;
                    // Don't clobber an in-flight compile (a concurrent enrichment self-heal or
                    // release request may already have flipped Pending/Compiling).
                    if (def.CompilationStatus is CompilationStatus.Pending
                                              or CompilationStatus.Compiling)
                        return curr;
                    // Re-check staleness inside the lambda — a genuine rebuild may have refreshed
                    // CompiledFrameworkVersion between the outer Where and this write.
                    if (!HasStaleFrameworkBuild(def)) return curr;
                    return curr with
                    {
                        Content = def with { CompilationStatus = CompilationStatus.Pending }
                    };
                }).Subscribe(_ => { },
                    ex => logger?.LogWarning(ex,
                        "Framework-stale kickoff: Update failed for {HubPath}", hubPath));
            });

        return new CompositeDisposable(
            watcherSub, firstBuildKickoffSub, recoveryKickoffSub, frameworkStaleKickoffSub);
    }

    /// <summary>
    /// Subscribes (no <c>Take(1)</c>) to the shared <see cref="NodeSources.GetSources"/>
    /// synced query for this NodeType. Every emission recomputes
    /// <c>{path → MeshNode.LastModified.UtcTicks}</c> from the live source set
    /// and writes <see cref="NodeTypeDefinition.CurrentSourceVersions"/> +
    /// <see cref="NodeTypeDefinition.IsDirty"/> on the own MeshNode.
    ///
    /// <para><b>Source of truth</b>: the synced query is cached per NodeType
    /// path inside the workspace, so the watcher, the compile pipeline, and
    /// any layout-area that lists sources all observe the SAME upstream
    /// subscription with the SAME content. No duplicate <c>SubscribeRequest</c>s,
    /// no risk of a watcher-side view diverging from a compile-side view.</para>
    ///
    /// <para><b>IsDirty contract</b>: dirty iff
    /// <see cref="NodeTypeDefinition.CurrentSourceVersions"/> differs from
    /// <see cref="NodeTypeDefinition.CompiledSources"/>. The first synced-query
    /// emission at hub initialization seeds <c>CurrentSourceVersions</c> —
    /// restart-safe: a NodeType that boots up with a stale persisted
    /// <c>CompiledSources</c> snapshot immediately flips <c>IsDirty=true</c>
    /// on the first emission, the compile watcher's kickoff (or the user's
    /// "Compile" button) takes it from there.</para>
    ///
    /// <para><b>Update lambda is idempotent</b>: when the recomputed dictionary
    /// matches the persisted one, the lambda returns <c>curr</c> unchanged —
    /// no Version bump, no echo, no infinite re-emission loop (the watcher
    /// itself observes the synced query, not its own write-back; the
    /// idempotent return is belt-and-braces).</para>
    /// </summary>
    public static IDisposable InstallSourcesWatcher(
        IMessageHub hub,
        IWorkspace workspace)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.CompileWatcher");
        var hubPath = hub.Address.Path;
        var ownStream = workspace.GetMeshNodeStream();
        // Source-set discovery is read as System (see the GetSources call in the
        // Select below — break-the-cycle fix for the activation self-deadlock).
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        // Outer subscription: discover the source path set via the shared
        // synced query (NodeSources.GetSources). When the path set changes
        // (sources added / removed), we re-subscribe to per-path streams.
        // Each per-path stream emits on EVERY update to that source MeshNode
        // — propagated by the synchronization protocol from the owning hub's
        // OWN stream — so the watcher sees stream.Update writes without
        // needing the IDataChangeNotifier round-trip the synced-query
        // change-detection layer relies on. This is the "bind by path" shape
        // the thread-streaming view uses.
        //
        // Switch() disposes the previous combined per-path subscription set
        // when the source path list changes; final outer dispose tears down
        // everything (the returned IDisposable is registered for hub
        // disposal in MeshDataSource).
        //
        // 🚨 Static-only NodeTypes (HubConfiguration delegate set in-process
        // AND no source code on the definition) ship their assembly with the
        // framework — there is no source set to watch and nothing to
        // recompile. Without this gate every per-node hub activation
        // (including non-NodeType nodes like Threads / Code / Markdown that
        // get filtered by the Where below) opens an upstream subscription
        // that walks every partition's query provider; the network of
        // SubscribeRequests that DefaultSources expands into
        // (`nodeType:Code namespace:{hubPath}/Source scope:subtree`) was
        // the dominant background traffic in prod (2026-05-21). Mirrors the
        // skip branch in InstallCompileWatcher's kickoff at line ~122.
        return ownStream
            .Where(node => node?.Content is NodeTypeDefinition def
                && !IsStaticOnlyNodeType(node, def))
            .DistinctUntilChanged(node =>
            {
                var d = (NodeTypeDefinition)node!.Content!;
                // Re-resolve only when the source-query inputs themselves
                // change. Any other field edit (CompilationStatus,
                // LatestReleasePath, RequestedReleaseAt, …) keeps the same
                // path set, so don't churn the per-path subscriptions.
                return (
                    Sources: d.Sources is null ? "" : string.Join("|", d.Sources),
                    Tests: d.Tests is null ? "" : string.Join("|", d.Tests));
            })
            .Select(node =>
            {
                var def = (NodeTypeDefinition)node!.Content!;
                // Live source set via the SHARED synced query
                // (NodeSources.GetSources → workspace.GetQuery). It RECEIVES
                // source changes and re-emits the FULL current set on every
                // edit / add / remove — so CurrentSourceVersions (and therefore
                // IsDirty) gets dirty on its own simply by observing the query.
                // NO .Take(1) (which freezes on the Replay(1) cached snapshot and
                // misses the edit) and no ad-hoc per-path stream plumbing.
                //
                // 🚨 Read the source set as System. Source-set discovery is
                // framework infrastructure, NOT a user-scoped read. Without this
                // scope, workspace.GetQuery routes through WrapWithPerUserRls,
                // which — under a user-triggered activation — issues a
                // CheckPermission round-trip per source node. For a source path
                // UNDER this NodeType, resolving the ancestor's Read routes a
                // GetPermissionRequest BACK to this very grain, forming a
                // call-chain cycle that deadlocks the single-threaded,
                // non-reentrant activation. Reading as System makes
                // WrapWithPerUserRls short-circuit — no CheckPermission, no
                // self-call, no cycle. Observable.Using keeps the System scope
                // alive for the LIVE subscription, not just the GetSources build
                // call. Repro: OrleansSourcesWatcherDeadlockTest.
                return Observable.Using(
                    () => accessService?.ImpersonateAsSystem()
                          ?? System.Reactive.Disposables.Disposable.Empty,
                    _ => NodeSources.GetSources(workspace, def, hubPath));
            })
            .Switch()
            .Select(sources =>
            {
                // Fold the full current set → path → LastModified.UtcTicks. Same
                // version field CompiledSources is keyed on, so IsDirty compares
                // like-for-like (empty set → empty snapshot → IsDirty=false when
                // CompiledSources is also empty).
                var snap = System.Collections.Immutable.ImmutableDictionary<string, long>.Empty;
                foreach (var n in sources)
                    if (!string.IsNullOrEmpty(n.Path))
                        snap = snap.SetItem(n.Path!, n.LastModified.UtcTicks);
                return (IReadOnlyDictionary<string, long>)snap;
            })
            .Subscribe(
                snapshot =>
                {
                    workspace.GetMeshNodeStream().Update(curr =>
                    {
                        if (curr.Content is not NodeTypeDefinition def) return curr;

                        // Idempotent: no-op when CurrentSourceVersions already
                        // matches the just-computed snapshot. IsDirty is a
                        // computed property — derives from CurrentSourceVersions
                        // vs CompiledSources — so no separate flag to write.
                        if (def.CurrentSourceVersions is not null
                            && DictEquals(def.CurrentSourceVersions, snapshot))
                            return curr;

                        return curr with
                        {
                            Content = def with
                            {
                                CurrentSourceVersions = snapshot
                            }
                        };
                    }).Subscribe(
                        _ => { },
                        ex => logger?.LogWarning(ex,
                            "SourcesWatcher: failed to write CurrentSourceVersions for {HubPath}",
                            hubPath));
                },
                ex => logger?.LogWarning(ex,
                    "SourcesWatcher: per-path stream for {HubPath} faulted", hubPath));
    }

    /// <summary>
    /// True when a NodeType definition is "static-only" — the in-process
    /// <see cref="MeshNode.HubConfiguration"/> delegate is set AND the
    /// persisted definition carries no source code at all
    /// (<see cref="NodeTypeDefinition.Configuration"/>,
    /// <see cref="NodeTypeDefinition.HubConfiguration"/>,
    /// <see cref="NodeTypeDefinition.Sources"/> all empty). Such NodeTypes
    /// ship their assembly with the framework and have nothing to compile or
    /// watch.
    ///
    /// <para>Lifted from the kickoff branch in <see cref="InstallCompileWatcher"/>
    /// so <see cref="InstallSourcesWatcher"/> can share the same condition —
    /// keeps the "what counts as static?" question in one place.</para>
    /// </summary>
    internal static bool IsStaticOnlyNodeType(MeshNode node, NodeTypeDefinition def) =>
        node.HubConfiguration is not null
        && string.IsNullOrWhiteSpace(def.Configuration)
        && string.IsNullOrWhiteSpace(def.HubConfiguration)
        && (def.Sources is null || def.Sources.Count == 0);

    /// <summary>
    /// Order-insensitive equality for two source-version dictionaries.
    /// <see cref="System.Collections.Immutable.ImmutableDictionary{TKey,TValue}"/>
    /// doesn't override <c>Equals</c>; two dictionaries with identical
    /// (path, ticks) pairs return false for value-equality. We need a
    /// content-equal check so the watcher's no-op short-circuit fires.
    /// </summary>
    private static bool DictEquals(
        IReadOnlyDictionary<string, long> a,
        IReadOnlyDictionary<string, long> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var v) || v != kvp.Value)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Stream-update release watcher: clients flip
    /// <see cref="NodeTypeDefinition.RequestedReleaseAt"/> (optionally with
    /// <see cref="NodeTypeDefinition.RequestedReleaseForce"/>) on the NodeType's
    /// own MeshNode via <c>workspace.GetMeshNodeStream(nodeTypePath).Update(...)</c>.
    /// This watcher observes the OWN node, treats every transition where
    /// <c>RequestedReleaseAt &gt; LastReleaseRequestHandledAt</c> as a release
    /// trigger, and flips <see cref="NodeTypeDefinition.CompilationStatus"/>
    /// to <see cref="CompilationStatus.Pending"/> — the existing
    /// <see cref="InstallCompileWatcher"/> takes it from there. No bespoke
    /// <c>CreateReleaseRequest</c> needed for new code; see
    /// <c>RequestViaStreamUpdate.md</c>.
    ///
    /// <para>The lambda also stamps <c>LastReleaseRequestHandledAt</c> in the
    /// same Update so the trigger isn't re-fired on every subsequent emission.
    /// The Status guard inside the Update keeps a re-fire during an in-flight
    /// Compiling/Pending window from racing the active activity.</para>
    /// </summary>
    public static IDisposable InstallReleaseRequestWatcher(
        IMessageHub hub,
        IWorkspace workspace)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.CompileWatcher");
        var hubPath = hub.Address.Path;
        var ownStream = workspace.GetMeshNodeStream();
        // 🅿️ A fresh release request is the DELIBERATE retry that un-parks a previously
        // parked (broken) type — the single un-park trigger, analogous to the old
        // InvalidateCache. We un-park before promoting the request to Pending so the
        // compile watcher's parked short-circuit doesn't refuse the user's explicit rebuild.
        var parkRegistry = hub.ServiceProvider.GetService<NodeTypeCompileParkRegistry>();

        // 🚨 MONOTONIC high-water mark of the highest RequestedReleaseAt this
        // process has COMMITTED — advanced ONLY where LastReleaseRequestHandledAt
        // is actually stamped (the on-node stamp is the cross-silo / restart
        // equivalent). It is the FLAP-BACK guard.
        //
        // Under load, two concurrent cross-hub RequestedReleaseAt patches can
        // apply OUT OF ORDER at the owner, so this OWN stream can momentarily
        // re-emit a node whose RequestedReleaseAt has FLAPPED BACK to an OLDER
        // value AFTER we already saw the newer one (captured trace:
        // RRW-FIRED req=665 → the node re-emits reqAt=399). If the Update lambda
        // RE-READ def.RequestedReleaseAt it would see 399, stamp
        // LastReleaseRequestHandledAt = 399 and SKIP the Pending flip
        // (399 <= handled) → the genuine recompile NEVER runs. Fix: the action
        // CARRIES the value the Where OBSERVED into the Update — never a live
        // re-read — and the on-node stamp is written monotonically.
        //
        // 🚨 COMMIT-time advance, NEVER dispatch-time (issue #185). The Update
        // below has bail paths (trigger already handled; status flipped to
        // Pending/Compiling by an earlier trigger's commit or a concurrent
        // kickoff) that return `curr` WITHOUT stamping LastReleaseRequestHandledAt.
        // The old code advanced the high-water EAGERLY in the Subscribe callback,
        // so a trigger whose Update bailed was left with high-water >= trigger
        // while the on-node stamp stayed below it — the post-settle re-emission
        // then failed `req > high-water` and the trigger was LOST for the life of
        // the process (deterministic repro: ReleaseRequestWatcherHighWaterTest).
        // Advancing only on the commit path keeps the in-memory mark exactly in
        // step with the on-node stamp, so a bailed trigger stays live and
        // re-fires on the next settled emission — the recovery the settled-gate
        // comment below promises. Cost: between dispatch and commit, a duplicate
        // or flapped-back emission can re-enter the Subscribe and queue a
        // redundant Update — which then bails on the `triggerAt <= handled` /
        // status guards (no double compile, no stale stamp: the carried-trigger
        // + monotonic-stamp write is what handles flap-back correctness; the
        // data-layer guard DataExtensions.DropStaleMonotonicTriggers additionally
        // stops flap-back at its source).
        //
        // Tear-free by construction: the mark is advanced inside the Update
        // lambda (the owner's serialized write path) while the Where reads it on
        // the reduced-stream emission path — two different serialized contexts,
        // so the mark uses Interlocked over UTC ticks instead of a bare
        // Nullable<DateTimeOffset> local.
        var dispatchHighWater = new MonotonicHighWaterMark();

        // 🚨 Gate on Status being SETTLED (Ok / Error / null) — never fire
        // while a compile is in-flight (Pending or Compiling). If we fired
        // mid-flight and just kept Status at Compiling (the old behaviour),
        // we'd stamp `LastReleaseRequestHandledAt` and effectively absorb
        // the trigger — the user's intent ("compile now, with my latest
        // edits") gets folded into a compile that may have started before
        // those edits even landed. By gating on settled here, the trigger
        // sits unprocessed until the in-flight compile transitions out,
        // and the NEXT emission (with `Status = Ok` / `Error`) drives a
        // fresh Pending flip → fresh compile. No spin loop: this
        // post-settle emission stamps `LastReleaseRequestHandledAt`, so
        // subsequent emissions with the same trigger fail the `req > handled`
        // gate.
        return ownStream
            .Where(node => node?.Content is NodeTypeDefinition def
                && def.RequestedReleaseAt is { } req
                // Strictly past our process-local COMMIT high-water — a flapped-back
                // older value (or a duplicate emission of an already-committed
                // trigger) never matches.
                && dispatchHighWater.IsPast(req)
                // …and strictly past the on-node last-handled stamp (cross-silo /
                // restart consistency; owner-written via UpdateOwn, so it never flaps).
                && (def.LastReleaseRequestHandledAt is null
                    || req > def.LastReleaseRequestHandledAt.Value)
                && def.CompilationStatus is not CompilationStatus.Pending
                                          and not CompilationStatus.Compiling)
            .Subscribe(
                node =>
                {
                    // 🚨 The trigger value OBSERVED by the Where for THIS emission.
                    // Everything below uses this CAPTURED value — never a fresh
                    // def.RequestedReleaseAt re-read, which can have flapped back to
                    // an older value by the time the Update lambda runs on the
                    // action block (see the high-water comment above).
                    if (node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.RequestedReleaseAt
                        is not { } triggerAt) return;
                    // 🚨 NO high-water advance here. The mark advances ONLY on the
                    // Update's COMMIT path below — where LastReleaseRequestHandledAt
                    // is actually stamped. Advancing eagerly at dispatch time lost
                    // any trigger whose Update bailed (issue #185; see the
                    // high-water comment above).
                    // 🅿️ Deliberate retry → un-park so the explicit rebuild is allowed through
                    // the compile watcher's parked short-circuit (and the attempt budget resets).
                    parkRegistry?.Unpark(hubPath);
                    logger?.LogInformation(
                        "[ReleaseRequestWatcher] {HubPath}: handling RequestedReleaseAt={Req} (force={Force}, lastHandled={Handled})",
                        hubPath, triggerAt,
                        node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.RequestedReleaseForce,
                        node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.LastReleaseRequestHandledAt);
                    workspace.GetMeshNodeStream().Update(curr =>
                    {
                        if (curr.Content is not NodeTypeDefinition def) return curr;
                        // 🚨 Gate on the CARRIED triggerAt, NOT def.RequestedReleaseAt
                        // (which may have flapped back to an older value). Already
                        // handled at or beyond this trigger? bail.
                        if (def.LastReleaseRequestHandledAt is { } handled
                            && triggerAt <= handled)
                            return curr;
                        // Double-check status inside the lambda — OWN's state may have
                        // transitioned to Pending/Compiling between the outer Where
                        // matching and this lambda running (an earlier trigger's commit
                        // in the same burst, or a concurrent kickoff). Returning `curr`
                        // unchanged is safe BECAUSE the high-water was not advanced for
                        // this trigger: it stays strictly above both gates, so the next
                        // settled emission (Status = Ok / Error after the in-flight
                        // compile) re-fires it and drives a fresh Pending flip → fresh
                        // compile with the user's latest edits.
                        if (def.CompilationStatus is CompilationStatus.Pending
                                                  or CompilationStatus.Compiling)
                            return curr;
                        // COMMIT: this is the one path that stamps
                        // LastReleaseRequestHandledAt — advance the in-memory
                        // high-water in the same breath so the two marks never diverge.
                        dispatchHighWater.Advance(triggerAt);
                        return curr with
                        {
                            Content = def with
                            {
                                CompilationStatus = CompilationStatus.Pending,
                                // Stamp the CARRIED trigger value, never the
                                // (possibly flapped-back) live value, and never
                                // below the existing stamp — the on-node stamp is
                                // monotonic too.
                                LastReleaseRequestHandledAt =
                                    def.LastReleaseRequestHandledAt is { } h && h > triggerAt
                                        ? def.LastReleaseRequestHandledAt
                                        : triggerAt
                            }
                        };
                    }).Subscribe(
                        _ => { },
                        ex => logger?.LogWarning(ex,
                            "[ReleaseRequestWatcher] {HubPath}: failed to dispatch release", hubPath));
                },
                ex => logger?.LogWarning(ex,
                    "[ReleaseRequestWatcher] {HubPath}: stream faulted", hubPath));
    }

    /// <summary>
    /// Process-local monotonic high-water mark over <see cref="DateTimeOffset"/>
    /// instants for <see cref="InstallReleaseRequestWatcher"/>. Stored as UTC ticks
    /// behind <see cref="System.Threading.Interlocked"/> because the mark is
    /// advanced on the owner's serialized write path (inside the Update lambda's
    /// commit branch) while the watcher's Where reads it on the reduced-stream
    /// emission path — two different serialized contexts, and a bare
    /// <c>Nullable&lt;DateTimeOffset&gt;</c> (16 bytes) is not tear-free across
    /// them. Instance per watcher install (captured in the subscription closure);
    /// never static (NoStaticState.md).
    /// </summary>
    private sealed class MonotonicHighWaterMark
    {
        // long.MinValue = "nothing committed yet": every real trigger is past it.
        private long utcTicks = long.MinValue;

        /// <summary>True when <paramref name="candidate"/> is STRICTLY past the mark.</summary>
        public bool IsPast(DateTimeOffset candidate) =>
            candidate.UtcTicks > System.Threading.Interlocked.Read(ref utcTicks);

        /// <summary>Advances the mark to <paramref name="candidate"/> if (and only if)
        /// it moves forward — monotonic under concurrent advancers.</summary>
        public void Advance(DateTimeOffset candidate)
        {
            var c = candidate.UtcTicks;
            long seen;
            while (c > (seen = System.Threading.Interlocked.Read(ref utcTicks))
                   && System.Threading.Interlocked.CompareExchange(ref utcTicks, c, seen) != seen)
            {
            }
        }
    }

    /// <summary>
    /// The live MeshWeaver framework identity a compiled NodeType release is pinned to — the
    /// <c>MeshWeaver.Graph</c> assembly's MVID (a content hash of the compiled module). It is
    /// identical on every server running the same build, stable across local rebuilds that don't
    /// change Graph's bytes, and changes whenever the framework is rebuilt with different content
    /// — so a mismatch against a NodeType's <c>CompiledFrameworkVersion</c> means "recompile".
    /// (MVID rationale below.) Computed once per process.
    /// </summary>
    internal static string FrameworkVersion => _frameworkVersion.Value;

    // Content-based framework identity: the Graph assembly's MVID (Module Version Id). It is
    // part of the compiled module, so it is STABLE whenever the DLL bytes are identical (an
    // incremental build that skips recompiling Graph, or a deterministic rebuild of unchanged
    // source) and CHANGES whenever Graph — or any dependency, which forces Graph's recompile —
    // is rebuilt with different content. That is exactly the "did the framework ABI change?"
    // signal a compiled NodeType release pins to.
    //
    // Why MVID and not AssemblyInformationalVersion: deriving this from the version STRING forced
    // Directory.Build.props to stamp a fresh version into EVERY build (so the string changed on a
    // local rebuild), which regenerated AssemblyInfo every build and DESTROYED incremental builds
    // (full rebuild every time → hot machine). The MVID decouples the two — the version string can
    // now stay stable across local rebuilds (incremental restored) while ABI invalidation stays
    // correct. An earlier scheme appended the DLL's last-write time, but that drifted on
    // copies/touches even when the bytes were identical; the MVID does not. For a deployed build
    // the MVID is fixed in the shipped DLL (identical on every server) and changes only when a
    // redeploy actually changes framework content — so it also recompiles LESS than the old
    // per-build-version scheme, which invalidated on every deploy whether or not anything changed.
    private static readonly Lazy<string> _frameworkVersion = new(() =>
        typeof(NodeTypeCompilationHelpers).Assembly.ManifestModule.ModuleVersionId.ToString("N"));

    /// <summary>
    /// True when a NodeType's persisted compile state is backed by a compiled
    /// assembly that was compiled against the CURRENT MeshWeaver framework
    /// version — the condition under which the compile kickoff may safely skip
    /// a (re)compile. Self-healing across <c>Status=Error</c>:
    /// <c>LatestAssembly{Collection,Path}</c> and <c>CompiledFrameworkVersion</c>
    /// are only ever populated by a <i>successful</i> compile write-back, so
    /// if all three match the current framework, a prior compile produced a
    /// usable assembly even if a subsequent compile failed (e.g. ALC file lock
    /// during cross-test re-write) and left <c>Status=Error</c> behind in the
    /// persisted JSON. Activation re-uses the existing assembly via
    /// <see cref="IAssemblyStore.TryGetAssemblyPath"/>; if the store has lost
    /// the bytes, activation's <c>TriggerRecompileAndRetry</c> kicks a fresh
    /// compile. Trusting the assembly fields here gates the kickoff against
    /// pointless recompiles that pollute <c>Status</c> further on failure.
    ///
    /// <para><b>Framework match is the freshness check.</b> A MeshWeaver
    /// redeploy changes <see cref="FrameworkVersion"/> (semver or, in dev
    /// builds, Graph.dll's last-write time), invalidating every cached compile.
    /// Mismatch forces a recompile (which mints a new release and leaves the
    /// old one as history for instances still loaded on it).</para>
    ///
    /// <para>This is a metadata-only check — no <see cref="IAssemblyStore"/>
    /// probe, no <c>File.Exists</c>. The kickoff path prefers a redundant
    /// compile over a blocking store round-trip on every stream emission;
    /// the runtime miss is caught later when activation tries to hydrate the
    /// assembly and the store reports a miss.</para>
    /// </summary>
    internal static bool HasUsableBuild(MeshNode node, NodeTypeDefinition def) =>
        !string.IsNullOrEmpty(def.LatestAssemblyCollection)
        && !string.IsNullOrEmpty(def.LatestAssemblyPath)
        && string.Equals(def.CompiledFrameworkVersion, FrameworkVersion, StringComparison.Ordinal);

    /// <summary>
    /// True when this NodeType has a cached compiled assembly (the durable
    /// <see cref="NodeTypeDefinition.LatestAssemblyCollection"/> /
    /// <see cref="NodeTypeDefinition.LatestAssemblyPath"/> pair is populated) that was built
    /// against a DIFFERENT MeshWeaver framework than the live one — i.e. the ONLY reason
    /// <see cref="HasUsableBuild"/> is false is the framework-version mismatch, not a missing
    /// assembly. This is the "platform self-update left a stale assembly behind" shape
    /// (issue #464, Defect 1): the bytes exist but are ABI-incompatible with the current
    /// process, so they must be rebuilt — a source-clean, never-touched NodeType included.
    ///
    /// <para>Distinct from <see cref="HasUsableBuild"/>: a NodeType that was never compiled
    /// (no assembly fields) is NOT "stale" — it is handled by the first-build kickoff.</para>
    /// </summary>
    internal static bool HasStaleFrameworkBuild(NodeTypeDefinition def) =>
        !string.IsNullOrEmpty(def.LatestAssemblyCollection)
        && !string.IsNullOrEmpty(def.LatestAssemblyPath)
        && !string.IsNullOrEmpty(def.CompiledFrameworkVersion)
        && !string.Equals(def.CompiledFrameworkVersion, FrameworkVersion, StringComparison.Ordinal);

    /// <summary>
    /// Compile-and-write-back loop for one NodeType. Runs Roslyn via
    /// <see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/>,
    /// writes the outcome back to the NodeType's own MeshNode
    /// (<see cref="NodeTypeDefinition.CompilationStatus"/>,
    /// <see cref="NodeTypeDefinition.CompilationError"/>,
    /// <c>AssemblyLocation</c>,
    /// <see cref="NodeTypeDefinition.LastCompileSucceededAt"/>,
    /// <see cref="NodeTypeDefinition.LatestReleasePath"/>,
    /// <see cref="NodeTypeDefinition.CompiledSources"/>), and (best-effort)
    /// publishes the post-compile MeshNode onto the mesh change feed so other
    /// silos invalidate their caches.
    ///
    /// <para>Shared by two callers:
    /// <list type="number">
    ///   <item><see cref="InstallCompileWatcher"/> auto-triggers on
    ///     <see cref="CompilationStatus.Pending"/> — passes <paramref name="request"/> = null.</item>
    ///   <item>The <c>CreateReleaseRequest</c> handler in <c>MeshDataSource</c>
    ///     responds to a UI "Create Release" click — passes the delivery so
    ///     <c>CreateReleaseResponse</c> can be returned to the requester.</item>
    /// </list></para>
    /// </summary>

    /// <summary>
    /// Per-NodeType-hub handler for <see cref="DispatchCompileTrigger"/>. Runs on
    /// the hub's ActionBlock — the single-threaded actor for "this NodeType."
    /// Owns the Pending → Compiling transition + activity dispatch (or inline
    /// fallback). Status-based single-flight: if the OWN MeshNode already shows
    /// Compiling (a sibling trigger raced ahead), the handler no-ops.
    /// </summary>
    public static IMessageDelivery HandleDispatchCompile(
        IMessageHub hub, IMessageDelivery<DispatchCompileTrigger> request)
    {
        var hubPath = hub.Address.Path;
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MeshWeaver.Graph.CompileWatcher");
        var workspace = hub.GetWorkspace();
        var compilationService = hub.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();
        var pendingNode = request.Message.PendingNode;

        logger.LogInformation(
            "[COMPILE-TRACE] HandleDispatchCompile: ENTERED on {HubPath}",
            hubPath);

        // Atomic Pending → Compiling transition. ActionBlock serialises
        // messages so two DispatchCompileTriggers cannot run in parallel —
        // the second sees Status=Compiling and the Update lambda short-circuits.
        var weTransitioned = false;
        workspace.GetMeshNodeStream().Update(curr =>
            {
                if (curr.Content is not NodeTypeDefinition def) return curr;
                if (def.CompilationStatus != CompilationStatus.Pending) return curr;
                weTransitioned = true;
                return curr with
                {
                    Content = def with
                    {
                        CompilationStatus = CompilationStatus.Compiling,
                        LastCompileStartedAt = DateTimeOffset.UtcNow
                    }
                };
            })
            .Take(1)
            .Subscribe(
                compilingSnapshot =>
                {
                    if (!weTransitioned)
                    {
                        logger.LogInformation(
                            "[COMPILE-TRACE] HandleDispatchCompile: status already past Pending on {HubPath} — skipping dispatch",
                            hubPath);
                        return;
                    }

                    var snapshot = compilingSnapshot ?? pendingNode;
                    logger.LogInformation(
                        "[COMPILE-TRACE] HandleDispatchCompile: running compile INLINE (reactive) for {HubPath}",
                        hubPath);
                    // Run the compile INLINE on this NodeType hub — fully reactive, no
                    // waiting. The previous shape created an _Activity node and posted
                    // a cross-hub RunCompileRequest to its address; RouteMessage resolves
                    // a path ONCE with no retry/fallback, so a just-created _Activity hub
                    // is not yet routable → the request is dropped → the compile never
                    // runs → status stuck Compiling → HandleCreateRelease's
                    // AwaitCompilationSettled never settles → CreateReleaseRequest hangs.
                    // RunCompile writes the terminal parent status on OWN (no routing)
                    // and creates the activity MeshNode complete in one shot (no patch
                    // to a not-yet-routable node), so there is no cross-hub dispatch to
                    // race. Roslyn itself runs on the Compile IoPool inside
                    // CompileAndGetConfigurations, so the hub action block stays
                    // responsive.
                    RunCompile(workspace, hub, compilationService, snapshot, request: null);
                },
                ex => logger.LogWarning(ex,
                    "[COMPILE-TRACE] HandleDispatchCompile: Pending→Compiling Update faulted for {HubPath}",
                    hubPath));

        return request.Processed();
    }

    public static void RunCompile(
        IWorkspace workspace,
        IMessageHub hub,
        IMeshNodeCompilationService compilationService,
        MeshNode pendingNode,
        IMessageDelivery<CreateReleaseRequest>? request,
        IReadOnlyList<MeshNode>? sourcesOverride = null)
    {
        var hubPath = hub.Address.Path;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.CompileWatcher");
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        // 🚨 Compile ALWAYS runs under System. It reads source files across the mesh
        // and writes the _Activity progress node + the NodeType's own status. The
        // deferred pipelines below SUBSCRIBE after HandleDispatchCompile's delivery
        // scope has cleared (the flip-Compiling Update callback fires post-Finally),
        // so the ambient AccessContext is already gone — capturing it would carry
        // null and the activity writes would be RLS-denied (the activity never lands →
        // progress readers NotFound-storm). Each deferred pipeline RE-ESTABLISHES
        // System at its own subscribe via Observable.Using(ImpersonateAsSystem):
        // ImpersonateAsSystem sets System UNCONDITIONALLY (it doesn't read the ambient),
        // so it survives the cleared scope, and System (Permission.All) can never be
        // denied. This is the StaticRepoImporter pattern. See AccessContextPropagation.md.
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        // 🅿️ Record this real Roslyn kick-off in the park registry. A parked (broken) type
        // holds at its small attempt count instead of climbing on every access — the
        // observable proof the failure is bounded. The terminal write-back below parks the
        // type (and notifies) on failure, or clears the park on success.
        var parkRegistry = hub.ServiceProvider.GetService<NodeTypeCompileParkRegistry>();
        parkRegistry?.RecordAttempt(hubPath);

        // Activity Control Plane — THE official progress mechanism. Create the
        // compile activity UP FRONT (canonical uppercase _Activity, satellite
        // routing + Releases query) so compile progress is observable via the
        // activity node's stream (workspace.GetMeshNodeStream(activityPath)) — the
        // GUI Releases pane and any diagnosis read it there, NOT logs.
        //
        // 🚨 ROOT-CAUSE GUARD (the `_Activity/compile-*` resubscribe storm). The
        // activity create is PROVISION-ORDERED and OBSERVED, and we stamp
        // LastCompilationActivityPath on the NodeType ONLY when the create actually
        // landed — never a phantom path. The old code created the activity
        // fire-and-forget (swallowing the failure at Debug) with no
        // EnsurePartitionProvisioned ordering, then stamped LastCompilationActivityPath
        // UNCONDITIONALLY. On a not-yet-provisioned partition schema the create faulted
        // (42P01) and was swallowed, yet the NodeType still advertised the never-created
        // `compile-<ts>` path. Every reader of that NodeType — the per-NodeType hub's own
        // activity-control-plane read (streamCache.GetStream IsOwn → routes a
        // SubscribeRequest, BYPASSING the MeshNodeStreamCache negative-cache breaker), the
        // GUI CompileProgressIndicator's in-flight SubscribeToActivity, the
        // NodeTypeLayoutAreas.Progress embed — then subscribed to that phantom path, each
        // routing a SubscribeRequest → RoutingGrain → endless `[ROUTE] NotFound` for a FEW
        // specific compile-<ts> paths (the atioz storm). Reading/subscribing a node that
        // does not exist is the defect — so we only ever advertise a path we created.
        // Provision is reactive + pooled + promise-cached (no-op when already provisioned —
        // EnsurePartitionProvisioned, the sanctioned pattern StaticRepoImporter uses); the
        // create is bounded so a hung owner can never block the compile (we fall back to a
        // null path — the compile still runs, just without an activity surface).
        var activityId = $"compile-{DateTime.UtcNow:yyyyMMddHHmmssfff}{Guid.NewGuid():N}";
        var activityNamespace = $"{hubPath}/_Activity";
        var activityPath = $"{activityNamespace}/{activityId}";
        var partition = hubPath.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segs
            ? segs[0]
            : hubPath;
        var partitionProviders = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToArray();
        var provisioned = partitionProviders.Length == 0 || string.IsNullOrEmpty(partition)
            ? Observable.Return(System.Reactive.Unit.Default)
            : Observable.Merge(partitionProviders.Select(p => p.EnsurePartitionProvisioned(partition)))
                .ToList().Select(_ => System.Reactive.Unit.Default);

        // Cold, SHARED observable resolving to the activity path on a confirmed create,
        // or null when no IMeshService is present / the create fails / it doesn't land
        // within the bound. Replay(1).AutoConnect(1): the create's side effect runs ONCE
        // on the first subscribe and its result is buffered, so BOTH the Compiling-flip
        // and the compile pipeline (which subscribes on the pool via SubscribeOn, possibly
        // after the flip's Take(1) already completed) observe the SAME resolved value
        // without re-running the create and without depending on an exact subscriber count.
        var activityPathObservable = (meshService is null
            ? Observable.Return<string?>(null)
            : provisioned
                .SelectMany(_ => meshService.CreateNode(new MeshNode(activityId, activityNamespace)
                {
                    Name = $"Compile {hubPath}",
                    NodeType = ActivityNodeType.NodeType,
                    MainNode = hubPath,
                    State = MeshNodeState.Active,
                    Content = new ActivityLog(ActivityCategory.Compilation)
                    {
                        Id = activityId,
                        HubPath = hubPath,
                        Status = ActivityStatus.Running,
                        Messages = System.Collections.Immutable.ImmutableList.Create(
                            new LogMessage($"Compile started for {hubPath}", LogLevel.Information),
                            new LogMessage("Invoking compiler…", LogLevel.Information))
                    }
                }))
                .Take(1)
                .Select(_ => (string?)activityPath)
                // Bound: a hung owner must NEVER block the compile. On timeout/fault emit
                // null — the compile proceeds with no activity surface (best-effort
                // observability), and crucially the NodeType never advertises an
                // un-created path that would storm the router.
                .Timeout(TimeSpan.FromSeconds(10), Observable.Return<string?>(null))
                .Catch<string?, Exception>(ex =>
                {
                    logger?.LogDebug(ex,
                        "Compile: activity create failed for {HubPath} (best-effort) — " +
                        "LastCompilationActivityPath stays null so no reader subscribes to a phantom node",
                        hubPath);
                    return Observable.Return<string?>(null);
                }))
            .Replay(1)
            .AutoConnect(1);

        // Flip the parent NodeType to Compiling, stamping the ACTUAL activity path (or
        // null when the create didn't land). The stamp follows the create — it is never
        // a path that does not exist.
        Observable.Using(
                () => accessService?.ImpersonateAsSystem() ?? (IDisposable)System.Reactive.Disposables.Disposable.Empty,
                _ => activityPathObservable
                    .Take(1)
                    .SelectMany(resolvedActivityPath => workspace.GetMeshNodeStream().Update(curr =>
                        curr.Content is NodeTypeDefinition def
                            ? curr with
                            {
                                Content = def with
                                {
                                    CompilationStatus = CompilationStatus.Compiling,
                                    LastCompileStartedAt = DateTimeOffset.UtcNow,
                                    LastCompilationActivityPath = resolvedActivityPath
                                }
                            }
                            : curr)))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "Compile: failed to flip status to Compiling for {HubPath}", hubPath));

        if (request is not null)
            hub.Post(new CreateReleaseResponse(true), o => o.ResponseFor(request));

        // 🚨 Subscribe the compile OFF the NodeType hub's action block. RunCompile is
        // invoked inline (HandleDispatchCompile → flip-Compiling Update → Subscribe), so
        // without this the compile pipeline's source GetSources / GetMeshNodeStream
        // SYNCED subscriptions open on the action-block thread that is mid-handler — the
        // subscribe-on-the-blocked-hub race (see synced-query-thread-hub note). The
        // compile leaf is provably sound when subscribed off-hub (CompileLeafStabilityTest:
        // 8/8 emit); the kickoff stall ("Invoking compiler", no outcome) only appears on
        // the inline path. SubscribeOn moves the whole subscribe to the pool so the action
        // block stays free to service those synced handshakes. Order isn't a concern here
        // — this is a single self-contained compile, not cross-message FIFO.
        //
        // 🚨 Resolve the activity path BEFORE Roslyn (SelectMany off the bounded
        // activityPathObservable) so the terminal write stamps the SAME confirmed path
        // (or null) the Compiling-flip used — never an un-created path.
        var sub = Observable.Using(
                () => accessService?.ImpersonateAsSystem() ?? (IDisposable)System.Reactive.Disposables.Disposable.Empty,
                _ => activityPathObservable.Take(1)
                    .SelectMany(resolvedActivityPath => compilationService
                        .CompileAndGetConfigurations(pendingNode, sourcesOverride)
                        .Take(1)
                        .Select(result => new CompileOutcome(result, null, pendingNode, resolvedActivityPath))
                        .Catch<CompileOutcome, Exception>(ex =>
                            Observable.Return(new CompileOutcome(null, ex, pendingNode, resolvedActivityPath)))
                        // 🚨 TOTALITY guard — this subscription is the ONLY component that can
                        // settle the Compiling status it just flipped, so its termination MUST
                        // produce exactly one outcome. An EMPTY completion (an upstream stage
                        // completed without emitting — the silent twin of the hung-snapshot
                        // wedge) previously fell through Subscribe(onNext, onError) with NO
                        // terminal write: the NodeType stayed Compiling forever and every later
                        // trigger no-oped (memex-cloud 2026-07-20, Store/Plugin). Synthesize a
                        // terminal error outcome so the state machine ALWAYS settles — the same
                        // completion-guard contract MeshQuery.MergeProviderObservables enforces
                        // for provider Initials ("every Query observable must emit exactly one
                        // Initial" ⇒ "every dispatched compile reaches exactly one terminal
                        // status").
                        .DefaultIfEmpty(new CompileOutcome(null, new InvalidOperationException(
                            $"Compile pipeline for '{hubPath}' completed without producing an outcome — "
                            + "an upstream stage (source snapshot / include resolution / Roslyn bridge) "
                            + "terminated empty. This is a framework defect surfaced as a terminal "
                            + "compile failure so the NodeType cannot wedge at Compiling; retry via the "
                            + "Compile button / a fresh RequestedReleaseAt."), pendingNode, resolvedActivityPath)))
                    .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default))
            .Subscribe(
                outcome =>
                {
                    var ok = outcome.Error is null
                        && !string.IsNullOrEmpty(outcome.Result?.AssemblyLocation);

                    // 🅿️ Park / un-park on the terminal compile outcome. On success, clear any
                    // parked failure so a fixed type recompiles cleanly. On failure, park the
                    // type (bounded + terminal) and emit one user notification — so the broken
                    // type serves its cached error WITHOUT re-running Roslyn on every later
                    // access (the recompile-storm wedge). A deterministic source error (Roslyn
                    // diagnostics / CompilationException, or a clean "no assembly" outcome with
                    // no infra exception) parks immediately; a non-deterministic infra fault is
                    // retried up to the registry's bound, then parked. The release requester
                    // (RequestedReleaseBy, set by the Compile-gated Create Release action) is the
                    // bell to notify; a System first-build / seed compile has none → the
                    // notification becomes a satellite of the failing type instead.
                    if (parkRegistry is not null)
                    {
                        if (ok)
                            parkRegistry.OnCompileSucceeded(hubPath);
                        else
                        {
                            var hasRoslynErrors =
                                (outcome.Result?.Log?.Errors() is { Count: > 0 })
                                || (outcome.Result?.Diagnostics is { Count: > 0 });
                            var deterministic = outcome.Error is null
                                || outcome.Error is CompilationException
                                || hasRoslynErrors;
                            var parkError = outcome.Error?.Message
                                ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } perr
                                    ? string.Join("; ", perr.Select(m => m.Message))
                                    : "Compilation produced no assembly");
                            var requestedBy = outcome.PendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.RequestedReleaseBy;
                            parkRegistry.OnCompileFailed(
                                hub, hubPath, parkError, deterministic, requestedBy, logger);
                        }
                    }

                    // The CONFIRMED activity path (or null when the create didn't land).
                    // Everything below stamps / writes against this — never an un-created
                    // node, so no reader can subscribe to a phantom `compile-*` path.
                    var resolvedActivityPath = outcome.ActivityPath;

                    // 🚨 Release create is OBSERVED + BOUNDED (same guard as the activity
                    // create): TryCreateReleaseNode emits the path only after the create
                    // LANDED, or null on timeout/fault. The terminal write below runs in
                    // its OnNext, so LatestReleasePath is never stamped with a path that
                    // does not exist yet — a reader following the field right after the
                    // Ok write used to hit a hard path-resolution NotFound (the
                    // NodeTypeReleaseGateTest 2-core flake).
                    var releasePathObservable = ok
                        ? MeshDataSourceExtensions.TryCreateReleaseNode(
                            hub, hubPath, outcome.Result!, outcome.PendingNode, resolvedActivityPath, logger)
                        : Observable.Return<string?>(null);

                    releasePathObservable
                        .Take(1)
                        // 🚨 Same totality guard as the compile pipeline above: the terminal
                        // Status write below runs in THIS OnNext — a release-create observable
                        // that completed empty would silently skip it and wedge the NodeType at
                        // Compiling. TryCreateReleaseNode is bounded (null on timeout/fault) by
                        // contract; DefaultIfEmpty makes the write unconditional even if that
                        // contract is ever violated.
                        .DefaultIfEmpty()
                        .Subscribe(newReleasePath =>
                    {
                    // Terminal writes run under System — same rule as every other
                    // deferred pipeline in RunCompile (the ambient scope from the
                    // compile subscription does not flow into this create-response
                    // callback thread).
                    using var terminalScope = accessService?.ImpersonateAsSystem();

                    // Write the FULL compile log + terminal status to the activity
                    // node (the official progress surface) in ONE atomic update:
                    // CompileCore's diagnostics, the Roslyn produced/failed line, and
                    // the release outcome. Cross-hub, best-effort from this hub — the
                    // GUI Releases pane / diagnosis read it via the activity stream.
                    var activityMessages =
                        System.Collections.Immutable.ImmutableList.CreateBuilder<LogMessage>();
                    if (outcome.Result?.Log is { } compileLog && compileLog.Messages.Count > 0)
                        activityMessages.AddRange(compileLog.Messages);
                    if (ok)
                        activityMessages.Add(new LogMessage(
                            $"Roslyn produced assembly at: {outcome.Result!.AssemblyLocation}",
                            LogLevel.Information));
                    else
                        activityMessages.Add(new LogMessage(
                            $"Roslyn failed: {outcome.Error?.Message ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs ? string.Join("; ", errs.Select(m => m.Message)) : "Compilation produced no assembly")}",
                            LogLevel.Error));
                    if (newReleasePath is not null)
                        activityMessages.Add(new LogMessage(
                            $"Release created: {newReleasePath}", LogLevel.Information));
                    NodeTypeCompilationActivity.Complete(hub, resolvedActivityPath,
                        ok ? ActivityStatus.Succeeded : ActivityStatus.Failed,
                        activityMessages.ToImmutable(), logger!);

                    workspace.GetMeshNodeStream().Update(curr =>
                    {
                        // 🚨 Tolerant read — NOT a bare `curr.Content is not NodeTypeDefinition`.
                        // Under load a concurrent cross-hub patch can leave this node's Content as
                        // an untyped JsonElement (the TypeRegistry-miss degrade path). The bare
                        // type test would then return `curr` unchanged and the TERMINAL
                        // Status=Ok/Error write would SILENTLY NEVER LAND — the NodeType wedges at
                        // Compiling, WaitForLatestRelease / Status==Ok times out, and every
                        // instance hub falls back to the default config. ContentAs recovers the
                        // JsonElement into NodeTypeDefinition (logging loud on a genuine degrade)
                        // so the terminal status — and the typed write-back below — always lands.
                        var def = curr.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions, logger);
                        if (def is null)
                            return curr;

                        if (outcome.Error is null && !string.IsNullOrEmpty(outcome.Result?.AssemblyLocation))
                        {
                            logger?.LogInformation("Compile success for {HubPath} → {Assembly}",
                                hubPath, outcome.Result!.AssemblyLocation);
                            return curr with
                            {
                                Content = def with
                                {
                                    CompilationStatus = CompilationStatus.Ok,
                                    CompilationError = null,
                                    CompilationDiagnostics = null,
                                    LastCompileSucceededAt = DateTimeOffset.UtcNow,
                                    // Stamp LastCompiledVersion to MATCH the version the
                                    // IAssemblyStore upload used (set by
                                    // UploadToStoreIfNeeded — the captured pendingNode.Version
                                    // at compile kickoff). Using curr.Version here would
                                    // point activation at a different version than the one
                                    // the store actually has — TryGetAssemblyPath miss,
                                    // activation falls back to default config without
                                    // AddMeshDataSource, IWorkspace fails to activate.
                                    LastCompiledVersion = outcome.Result.Version ?? curr.Version,
                                    LastCompilationActivityPath = resolvedActivityPath,
                                    LatestReleasePath = newReleasePath ?? def.LatestReleasePath,
                                    ReleaseNotes = newReleasePath is not null ? null : def.ReleaseNotes,
                                    CompiledSources = outcome.Result.CompiledSources
                                        ?? System.Collections.Immutable.ImmutableDictionary<string, long>.Empty,
                                    // Cross-silo durable assembly reference. Populated from
                                    // the IAssemblyStore upload during compile (see
                                    // MeshNodeCompilationService.UploadToStoreIfNeeded).
                                    // Falls back to the previous values on a producer that
                                    // hasn't wired a store yet (Null store keeps the new
                                    // fields null and consumers still fall through to the
                                    // legacy AssemblyLocation path during Stage 0/1).
                                    LatestAssemblyCollection = outcome.Result.Collection ?? def.LatestAssemblyCollection,
                                    LatestAssemblyPath = outcome.Result.ContentPath ?? def.LatestAssemblyPath,
                                    // Stamp the framework version the assembly bound
                                    // against — HasUsableBuild compares this to the live
                                    // FrameworkVersion so a MeshWeaver redeploy forces a
                                    // recompile instead of loading an ABI-stale DLL.
                                    CompiledFrameworkVersion = FrameworkVersion,
                                    // Clear the consumed release-requester. TryCreateReleaseNode
                                    // already read it (off pendingNode) to stamp this release's
                                    // owner; clearing it here ensures a SUBSEQUENT System-only
                                    // recompile (first-build kickoff / framework-stale self-heal)
                                    // doesn't mis-attribute its release to a stale prior user.
                                    RequestedReleaseBy = null
                                }
                            };
                        }

                        var errorSummary = outcome.Error?.Message
                            ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs
                                ? string.Join("; ", errs.Select(m => m.Message))
                                : "Compilation produced no assembly");
                        logger?.LogWarning("Compile failure for {HubPath}: {Error}", hubPath, errorSummary);
                        return curr with
                        {
                            Content = def with
                            {
                                CompilationStatus = CompilationStatus.Error,
                                CompilationError = errorSummary,
                                CompilationDiagnostics = outcome.Result?.Diagnostics is { Count: > 0 } ds
                                    ? System.Collections.Immutable.ImmutableList.CreateRange(ds)
                                    : null,
                                LastCompilationActivityPath = resolvedActivityPath,
                                CompiledSources = null,
                                // Clear the consumed release-requester on failure too — the
                                // failed request is done; a fresh request must re-stamp it.
                                RequestedReleaseBy = null
                            }
                        };
                    })
                    .Subscribe(
                        saved =>
                        {
                            // Publish the post-compile MeshNode update onto the
                            // mesh change feed for cross-silo cache invalidation.
                            try
                            {
                                hub.ServiceProvider.GetService<IMeshChangeFeed>()
                                    ?.Publish(MeshChangeEvent.Updated(saved));
                            }
                            catch (Exception publishEx)
                            {
                                logger?.LogWarning(publishEx,
                                    "Compile: failed to publish post-compile change-feed event for {HubPath}",
                                    hubPath);
                            }
                        },
                        ex => logger?.LogWarning(ex,
                            "Compile: failed to write post-compile status for {HubPath}", hubPath));
                    },
                    ex => logger?.LogWarning(ex,
                        "Compile: release-create observation faulted for {HubPath}", hubPath));
                },
                ex => logger?.LogWarning(ex, "Compile faulted for {HubPath}", hubPath));

        hub.RegisterForDisposal(sub);
    }

    /// <summary>Per-NodeType compile outcome — either the compiler's result or the exception that aborted it.
    /// <paramref name="ActivityPath"/> is the CONFIRMED compile-activity node path (the create landed) or
    /// <c>null</c> when no activity node was created — the terminal write stamps this so the NodeType never
    /// advertises a never-created <c>_Activity/compile-*</c> path that readers would storm the router on.</summary>
    private record CompileOutcome(NodeCompilationResult? Result, Exception? Error, MeshNode PendingNode, string? ActivityPath);
}
