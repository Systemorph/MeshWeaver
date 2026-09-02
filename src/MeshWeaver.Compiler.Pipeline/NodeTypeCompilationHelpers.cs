using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MeshWeaver.Compiler;
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
/// CompilationStatus = Pending was observed. <see cref="NodeTypeCompilationHelpers.RunCompile"/>
/// compiles from this snapshot so it reads the trigger-time state (ReleaseNotes
/// etc.) without re-fetching through the mesh-hub-cached remote stream.</param>
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
/// <see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/>, which
/// offloads the Roslyn invocation to the ThreadPool under a wall-clock bound
/// (<c>BoundLeg(ct =&gt; OnThreadPool(...))</c>). <b>Not</b> <c>Observable.FromAsync</c>,
/// which is forbidden outside <c>IoPool</c> — it would run the whole synchronous
/// Roslyn Emit on the subscribing hub's action block.</para>
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

    /// <summary>
    /// How long the on-demand adoption attempt (#1782 gap 4) may take before the compile
    /// proceeds anyway. Matches the git-sync push path's budget: the sources it reads are the
    /// same image directory and CI-published root, so the cost profile is the same.
    /// </summary>
    private static readonly TimeSpan OnDemandAdoptionBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to wait for an adoption's write-back to actually land on the node before
    /// treating the reported adoption as not having happened. Short on purpose: this is a
    /// confirmation of a write that has already been made, not a wait for work.
    /// </summary>
    private static readonly TimeSpan AdoptionStampBudget = TimeSpan.FromSeconds(5);

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
        // The adoption interlock (#1763) — absent on a host that never composed AddGraph's
        // registry, in which case the kickoff behaves exactly as it did before.
        var adoptionRegistry = hub.ServiceProvider.GetService<NodeTypeAdoptionRegistry>();
        // On-demand adoption attempts (#1782 gap 4) are started from INSIDE the watcher's
        // callback, so they are not part of any pipeline the returned disposable already covers.
        // Each is bounded and self-completing, but an untracked subscription still roots the hub
        // for the length of its timeout after disposal — so they are tracked here and removed on
        // completion, which keeps the composite from growing one entry per compile miss.
        var onDemandAdoptionSubs = new CompositeDisposable();
        // The live build guards (#1664 step 11, #1707 slice 2) — the per-type dependency-record
        // resolver plus the legacy installed-module fingerprint — join HasUsableBuild /
        // HasStaleFrameworkBuild below so a module-only update re-drives the compile the same
        // way a framework redeploy does, scoped to actual dependents once a record is stamped.
        var guards = GuardsOf(hub);

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
        // informational state — UI uses it to enable the Compile button, and a
        // healthy (never-parked) type does NOT auto-fire a recompile on a source
        // edit. The one exception: a PARKED (terminally-failed) type whose source
        // then changes IS auto-retried — InstallSourcesWatcher un-parks and flips
        // Pending so a redeploy/edit that fixes the break heals without a manual
        // Compile ("retry only if the sources changed"). Doc: AccessContextPropagation.md.
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
        // #891: subscribed through SubscribeWithReEstablish — the old bare log-and-die OnError
        // left the compile control plane permanently dead (no Pending flip ever dispatched
        // again) while the hub kept running. Transient stream faults re-establish; poisoned
        // own content (undeserializable NodeTypeDefinition) parks the type — the visible,
        // bounded sink — and stops instead of looping on the replayed emission.
        var watcherSub = ActivityControlPlaneExtensions.SubscribeWithReEstablish(
            () => ownStream
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
            }),
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
                    // 🅿️ Wedge-close: a Pending flip we refuse to dispatch was already WRITTEN to
                    // the node. Without a settle write-back the type would sit at Pending forever —
                    // every settle-waiter (get_diagnostics' / the compile tool's Where(Ok|Error),
                    // WaitForLatestRelease) hangs to its timeout and the stray trigger never
                    // reaches a sink. Re-settle Pending → Error with the given reason so the
                    // trigger is answered (bounded, no Roslyn). Same fire-and-forget UpdateOwn
                    // shape as the kickoffs below; System scope because re-settling framework
                    // state is infrastructure, not a user write.
                    // 🚨 #2264: the TWO call sites of this local function need DIFFERENT
                    // FailedBuildInputs treatment, so `formedUnderLiveInputs` is not decoration —
                    // see ApplyGateSettle's doc for why.
                    void SettleAsError(string? reason, bool formedUnderLiveInputs)
                    {
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
                                    Content = ApplyGateSettle(
                                        parkedDef, reason, formedUnderLiveInputs, guards.ModulesHash)
                                };
                            }).Subscribe(
                                _ => { },
                                ex => logger?.LogWarning(ex,
                                    "Compile watcher: failed to re-settle parked {HubPath} from Pending to Error",
                                    hubPath));
                    }

                    if (parkRegistry?.IsParked(hubPath) == true)
                    {
                        // 🅿️ …unless THIS Pending flip is the one sanctioned automatic retry
                        // (#2260). The failed-verdict re-drive used to un-park so its flip would
                        // not be swallowed here — which left the type un-parked for the whole
                        // round-trip until a second failure re-parked it, and un-parked FOREVER
                        // whenever the re-drive's own re-check then declined to flip. It now asks
                        // for a one-shot ADMISSION instead, so the park never moves and the
                        // containment guarantee ("parked ⇒ no later trigger can storm") holds at
                        // every instant. Consuming the admission is what lets this single flip
                        // through; every other trigger still takes the short-circuit below.
                        if (parkRegistry.TryConsumeRetryAdmission(hubPath))
                        {
                            logger?.LogDebug(
                                "Compile watcher: {HubPath} is PARKED but this Pending flip carries the "
                                + "sanctioned one-shot retry admission — letting it through WITHOUT "
                                + "un-parking (the park holds for every other trigger).", hubPath);
                        }
                        else
                        {
                            logger?.LogDebug(
                                "Compile watcher: {HubPath} is PARKED (terminal compile failure) — " +
                                "skipping recompile, serving cached error", hubPath);
                            // The verdict being RE-SERVED is the ORIGINAL failure's, formed under
                            // ITS inputs — stamping the live ones here would mask a genuine input
                            // change since that failure and defeat #1793's whole recovery path.
                            SettleAsError(parkRegistry.GetParkedError(hubPath), formedUnderLiveInputs: false);
                            return;
                        }
                    }

                    // 🚨 #1782 gap 4 — ADOPTION MUST BE REACHABLE AT ANY TIME.
                    //
                    // Until now a prebuilt assembly could only arrive at two moments: boot
                    // default-install, and an install / git-sync push. EVERY other route into a
                    // compile — a first access, a release request, a self-heal kick, a
                    // framework-stale rebuild — went straight to Roslyn (or, under
                    // RequirePrebuilt, straight to a park) without ever asking the deployment's
                    // bundle sources whether the assembly already existed. That was survivable
                    // while every instance pre-baked at boot; with instance-level pre-bake gone
                    // in favour of lazy compile-on-access (#1746) it made the fetch path
                    // unreachable at exactly the moment it became the PRIMARY way assemblies
                    // arrive.
                    //
                    // Every route into a compile converges on this handler, so this is the one
                    // place that makes adoption universal rather than adding a fourth special
                    // case: give the bundle sources one bounded chance HERE, before the
                    // adopt-only gate turns a miss into a park and before any Roslyn pass.
                    //
                    // Bounded and never fatal, exactly like the install and push paths: a host
                    // with no consumer registered, a source that times out, and a source that
                    // faults all fall through to the behaviour that existed before — "the
                    // release pipeline compiles, as it would have anyway".
                    var prebuiltConsumer = hub.ServiceProvider.GetService<IPrebuiltAssemblyConsumer>();
                    if (prebuiltConsumer is null)
                    {
                        DispatchOrPark();
                        return;
                    }

                    // 🚨 #2818 — A FORCE MEANS "BUILD THE LIVE SOURCE", NOT "SERVE ME WHATEVER A
                    // BUNDLE STILL RESOLVES". InstallReleaseRequestWatcher honours
                    // RequestedReleaseForce (it bypasses its "already satisfied" short-circuit) and
                    // flips the type Pending — but the flip lands HERE, and until now this branch
                    // asked the bundle sources again regardless. On any mesh whose bundle still
                    // resolved, a force therefore re-adopted the very bytes the operator was trying
                    // to replace and settled "without a Roslyn pass"; it worked only where
                    // SeedForTypes MISSED, i.e. exactly where nobody needed it. That is how the
                    // stale prebuilt in #2813 could not be forced off a node whose live source was
                    // already fixed. The flag survives the Pending flip (the dispatch commit keeps
                    // it) and is consumed by the terminal stamp of the compile it dispatches —
                    // ApplyCompileSuccess / ApplyCompileFailure / ApplyGateSettle — so a stale force
                    // can never suppress adoption for a later, unforced trigger.
                    if (pendingNode!.Content is NodeTypeDefinition { RequestedReleaseForce: true })
                    {
                        logger?.LogInformation(
                            "Compile watcher: {HubPath} was flipped Pending by a FORCED release — "
                            + "skipping on-demand prebuilt adoption and compiling the live source.",
                            hubPath);
                        DispatchOrPark();
                        return;
                    }

                    var attempt = new SingleAssignmentDisposable();
                    onDemandAdoptionSubs.Add(attempt);
                    attempt.Disposable = prebuiltConsumer.SeedForTypes([hubPath])
                        .Take(1)
                        .Timeout(OnDemandAdoptionBudget)
                        .SelectMany(AdoptionLanded)
                        .Catch<bool, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "Compile watcher: on-demand prebuilt adoption for {HubPath} failed — "
                                + "compiling instead.", hubPath);
                            return Observable.Return(false);
                        })
                        .Finally(() => onDemandAdoptionSubs.Remove(attempt))
                        .Subscribe(
                            landed =>
                            {
                                if (!landed)
                                {
                                    DispatchOrPark();
                                    return;
                                }
                                logger?.LogInformation(
                                    "Compile watcher: {HubPath} adopted a prebuilt assembly on demand — "
                                    + "settling without a Roslyn pass.", hubPath);
                            },
                            ex =>
                            {
                                logger?.LogWarning(ex,
                                    "Compile watcher: on-demand prebuilt adoption for {HubPath} faulted — "
                                    + "compiling instead.", hubPath);
                                DispatchOrPark();
                            });
                    return;

                    // 🚨 Trust the STAMP, never the reported count. A consumer that reports an
                    // adoption it never wrote back would leave this type at Pending for ever, and
                    // a STRANDED type is the one outcome worse than a redundant compile: every
                    // settle-waiter (get_diagnostics, the compile tool, WaitForLatestRelease)
                    // hangs to its timeout, and the instance pages hang with them. So an adoption
                    // counts only when the node ITSELF then satisfies HasUsableBuild — the same
                    // predicate the compile decision uses. Anything else compiles.
                    IObservable<bool> AdoptionLanded(int adopted)
                    {
                        if (adopted <= 0)
                        {
                            logger?.LogDebug(
                                "Compile watcher: no prebuilt assembly available for {HubPath} on "
                                + "demand — compiling.", hubPath);
                            return Observable.Return(false);
                        }

                        // 🚨 The watcher's OWN `guards`, never a fresh GuardsOf(hub). BuildGuards
                        // exists so "which live environment" can never fork between the checks in
                        // one handler, and it is resolved once per watcher install for exactly
                        // that reason. Re-resolving here would judge the adopted build against a
                        // snapshot the kickoff that flipped this type to Pending never saw — a
                        // decision taken against one observed state while the effect commits
                        // against another. It also rebuilds the dependency-id resolver and the
                        // module MVID map on every attempt, for nothing: adoption stamps assembly
                        // coordinates on the NODE, and changes no part of the environment these
                        // guards describe.
                        // 🚨 …and the node must have LEFT Pending. A real adoption stamps
                        // CompilationStatus=Ok together with the coordinates
                        // (PrebuiltAssemblySeeder.Seed), so an Ok node with a usable build is
                        // what "landed" looks like. HasUsableBuild alone is not enough on a type
                        // that already HAD a build: its old coordinates satisfy the predicate on
                        // the very Pending node this watcher is trying to settle, so a consumer
                        // that reported an adoption it never wrote back was believed — and the
                        // type sat at Pending for ever (#2818's regression, third phase: a dirty
                        // type with the previous build's coordinates, re-triggered unforced).
                        return ownStream
                            .Where(n => n?.ContentAs<NodeTypeDefinition>(
                                            hub.JsonSerializerOptions, logger) is { } adoptedDef
                                        && adoptedDef.CompilationStatus == CompilationStatus.Ok
                                        && HasUsableBuild(n, adoptedDef, guards))
                            .Take(1)
                            .Select(_ => true)
                            .Timeout(AdoptionStampBudget)
                            .Catch<bool, Exception>(_ =>
                            {
                                logger?.LogWarning(
                                    "Compile watcher: {HubPath} reported {Adopted} adopted "
                                    + "assembly(ies) but the node still has no usable build "
                                    + "{Budget}s later — compiling instead, because a stranded type "
                                    + "is worse than a redundant compile.",
                                    hubPath, adopted, AdoptionStampBudget.TotalSeconds);
                                return Observable.Return(false);
                            });
                    }

                    void DispatchOrPark()
                    {
                        // 🚨 THE ADOPT-ONLY GATE (Modules:RequirePrebuilt, MeshWeaver#2193 §A). On a
                        // mesh that requires prebuilt assemblies, a Pending flip on a type WITHOUT a
                        // usable build — first access, a release request, a self-heal kick, a
                        // framework-stale rebuild — must never reach Roslyn. Every route into a compile
                        // passes through this handler (a deliberate retry un-parks and then lands
                        // HERE), so this is the one place that turns "compile on a miss" into a
                        // PARKED, named refusal: the type settles at Error with a message that says
                        // what is missing (the assembly for this identity/architecture), what
                        // publishes it (the package's bundle for this lane) and how to retry — and
                        // every instance page renders that reason through the compilation-error
                        // overlay instead of hanging on a compile that would not have been allowed.
                        // Parking (deterministic — the same miss reproduces until a bundle lands)
                        // keeps the refusal bounded and visible, exactly like a terminal source error;
                        // the park registry's attempt counter stays at ZERO, the observable proof no
                        // Roslyn pass ever started.
                        if (PrebuiltAssemblySeeder.RequirePrebuilt(hub.ServiceProvider))
                        {
                            var reason = PrebuiltAssemblySeeder.RequiredParkReason(hubPath);
                            logger?.LogError(
                                "Compile watcher: {HubPath} has no adopted assembly and this mesh sets {Key} — " +
                                "PARKING with a named refusal instead of compiling. {Reason}",
                                hubPath, PrebuiltAssemblySeeder.RequirePrebuiltConfigKey, reason);
                            // The registry is resolved from the hub's services HERE, exactly as the
                            // real dispatch does — the watcher's own handle is only ever probed
                            // null-safely and must not be the thing the park depends on. The park
                            // carries the type's CURRENT source snapshot so the sources watcher does
                            // not read "sources changed since the park" against a null baseline and
                            // un-park into a park/un-park ping-pong; a genuine later source edit
                            // still re-drives (and is refused again, named, by this gate).
                            var registry = hub.ServiceProvider.GetService<NodeTypeCompileParkRegistry>()
                                ?? parkRegistry;
                            var pendingDef = pendingNode!.ContentAs<NodeTypeDefinition>(
                                hub.JsonSerializerOptions, logger);
                            registry?.OnCompileFailed(
                                hub, hubPath, reason, deterministic: true,
                                recipientUserId: null, sources: pendingDef?.CurrentSourceVersions, logger);
                            // The refusal genuinely IS formed under the live compile inputs — stamp
                            // them so the failed-verdict re-drive (#1793) doesn't treat this as
                            // "never attempted" and burn a needless automatic re-drive (#2264).
                            SettleAsError(reason, formedUnderLiveInputs: true);
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
                    }
                },
            hub.Address,
            logger,
            "Compile watcher",
            onPoisonedContent: ex => parkRegistry?.OnCompileFailed(
                hub, hubPath,
                "The NodeType's own node content could not be deserialized — the compile control "
                + "plane is parked until the content is repaired: " + ex.Message,
                deterministic: true, recipientUserId: null, sources: null, logger));

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
                && !HasUsableBuild(node, def, guards)
                // Same truly-static exclusion as the watcher above: the
                // HubConfiguration delegate IS the configuration; nothing to
                // Roslyn-compile.
                && !(node.HubConfiguration is not null
                    && string.IsNullOrWhiteSpace(def.Configuration)
                    && string.IsNullOrWhiteSpace(def.HubConfiguration)
                    && (def.Sources is null || def.Sources.Count == 0)))
            .Take(1)
            // 🚨 AN IN-FLIGHT ADOPTION WINS (#1763). Adopting a prebuilt assembly writes the
            // NodeType's node, so PrebuiltAssemblySeeder.Seed ACTIVATES the very hub it is about to
            // stamp — and this kickoff is armed by that activation. The seeder therefore started
            // the Roslyn compile the adoption exists to avoid, and the compile re-stamped the type
            // over the adopted build milliseconds later: install-time consumption saved nothing,
            // and a gate consuming a bake ended up judging its own bytes instead of the ones that
            // ship. Every log line said the adoption had worked, because it had.
            //
            // The kickoff DELAYS, it does not cancel: it waits for the reservation to clear and
            // then re-evaluates (the Update below still refuses to move a non-null status), so a
            // DECLINED adoption compiles exactly as before. The wait is bounded — a leaked
            // reservation must cost a delay, never an unbuilt type.
            .SelectMany(node =>
                (adoptionRegistry?.WhenClear(hubPath) ?? Observable.Return(Unit.Default))
                    .Timeout(NodeTypeAdoptionRegistry.ReservationWaitBudget)
                    .Catch((TimeoutException _) =>
                    {
                        logger?.LogWarning(
                            "First-build kickoff: {HubPath} waited {Budget}s for an in-flight "
                            + "prebuilt adoption that never released its reservation — building "
                            + "anyway (a stranded type is worse than a redundant compile)",
                            hubPath, NodeTypeAdoptionRegistry.ReservationWaitBudget.TotalSeconds);
                        return Observable.Return(Unit.Default);
                    })
                    .Select(_ => node))
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
                            // 🚨 CLEAR the watcher's stamp (#2544). This flip does not come from
                            // the release watcher and records no inputs, so leaving a previous
                            // dispatch's token behind would let a later request be absorbed
                            // against a compile nobody can vouch for — and if that compile fails,
                            // the absorbed release request is simply lost. Null means "unstamped",
                            // and the absorber parks on it exactly as it did before this change.
                            DispatchedBuildInputs = null,
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
                        Content = def with { CompilationStatus = CompilationStatus.Pending, DispatchedBuildInputs = null }
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
                && HasStaleFrameworkBuild(def, guards)
                && !IsStaticOnlyNodeType(node, def)
                && parkRegistry?.IsParked(hubPath) != true)
            .Take(1)
            // 🚨 ASK THE STORE BEFORE REBUILDING. The record's CompiledFrameworkVersion says which
            // framework the LAST WRITE-BACK came from; it does not say whether bytes for OUR
            // framework exist. Those are different questions, and the assembly store answers the one
            // that matters — its key carries the live framework tag, so a hit IS a usable build.
            //
            // They diverge whenever something else compiled for this framework without this hub
            // seeing the write-back: another replica, or a dedicated bake service that pre-fills the
            // share ahead of a rollout. Flipping to Pending on the version mismatch alone then
            // recompiles what is already on the volume — and, while two images are live at once,
            // does it on BOTH sides: each pod rebuilds to stamp its own version, the other sees the
            // stamp and rebuilds back. A pre-bake would not merely be wasted, it would storm the pods
            // currently serving production.
            //
            // So: mismatch is the CHEAP pre-filter (a pure record read on every emission, unchanged),
            // and the store probe runs at most once per hub lifetime, here, after Take(1).
            //
            // 🚨 The probe answers the FRAMEWORK-mismatch case only. The store key carries the
            // framework tag, not the modules hash — so when the staleness is a MODULES-hash
            // mismatch on a framework-matching build (#1664 step 11), a bytes-hit for the live
            // framework IS the very stale build we are trying to replace, and skipping on it
            // would wedge the type forever. For that case, skip the probe and rebuild.
            // 🚨 THE RE-EVALUATION LANE (#1976) — see ResolveStaleBuildAction for which branch may
            // act on the comparison and, crucially, which one may not.
            .SelectMany(node => ResolveStaleBuildAction(
                hub, hubPath, node, guards, compilationService, logger))
            .Subscribe(outcome =>
            {
                if (outcome.Action is StaleBuildAction.RestampDependencyRecord)
                {
                    RestampCarriedForwardBuild(hub, workspace, hubPath, guards, outcome.Detail, logger);
                    return;
                }
                if (outcome.Action is StaleBuildAction.Skip)
                {
                    // The pre-lane behaviour, preserved exactly: the store holds bytes for the
                    // live framework, so nothing is rebuilt — and nothing is restamped either.
                    logger?.LogInformation(
                        "Framework-stale kickoff SKIPPED for {HubPath}: its record names framework "
                        + "{Compiled} but the assembly store already holds a build for the live "
                        + "framework {Live} — nothing to rebuild ({Detail})",
                        hubPath, DefinitionOf(hub, outcome.Node)?.CompiledFrameworkVersion ?? "(null)",
                        FrameworkVersion, outcome.Detail ?? "no re-evaluation was possible");
                    return;
                }
                var node = outcome.Node;
                // Name the ACTUAL staleness cause: on a modules-only update the stamped and live
                // framework are EQUAL, and a framework-shaped message would read as a no-op.
                var staleDef = node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
                if (string.Equals(staleDef?.CompiledFrameworkVersion, FrameworkVersion, StringComparison.Ordinal))
                {
                    // Record-stamped builds name the exact drifted dependency; legacy stamps
                    // fall back to the whole-set fingerprint comparison.
                    var mismatch = staleDef?.CompiledDependencies is { } rec && guards.DependencyIdOf is not null
                        ? Compiler.CompiledDependencies.FindMismatch(rec, guards.DependencyIdOf, guards.ToolchainId)
                        : $"installed-module set {staleDef?.CompiledModulesHash ?? "(null)"} vs live {guards.ModulesHash}";
                    logger?.LogInformation(
                        "Stale-build kickoff: NodeType {HubPath} assembly's dependency set drifted ({Mismatch}; framework unchanged) — flipping CompilationStatus=Pending to rebuild",
                        hubPath, mismatch);
                }
                else
                    logger?.LogInformation(
                        "Framework-stale kickoff: NodeType {HubPath} assembly was compiled against framework {Compiled} but the live framework is {Live} — flipping CompilationStatus=Pending to rebuild",
                        hubPath, staleDef?.CompiledFrameworkVersion ?? "(null)", FrameworkVersion);
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
                    if (!HasStaleFrameworkBuild(def, guards)) return curr;
                    return curr with
                    {
                        Content = def with { CompilationStatus = CompilationStatus.Pending, DispatchedBuildInputs = null }
                    };
                }).Subscribe(_ => { },
                    ex => logger?.LogWarning(ex,
                        "Framework-stale kickoff: Update failed for {HubPath}", hubPath));
            });


        // 🚨 Failed-verdict re-drive kickoff (issue #1793) — the missing COMPLEMENT of the
        // framework-stale kickoff above, and the reason a fix could never reach the nodes it was
        // written for.
        //
        // A compile that FAILS writes no assembly coordinates at all: ApplyCompileFailure stamps
        // neither LatestAssembly{Collection,Path} nor CompiledFrameworkVersion. For a NodeType that
        // never compiled successfully on this deployment those are null FOREVER — and every
        // automatic path keys off something that only exists after a first success:
        //
        //   firstBuildKickoff              needs CompilationStatus is null   → it is Error
        //   recoveryKickoff                needs Compiling                   → it is Error
        //   frameworkStaleKickoff          needs the assembly coordinates    → never written
        //     (its own filter reads `Ok or Error`, so it INTENDS to cover this — it cannot,
        //      because HasStaleFrameworkBuild delegates to fields a failure does not stamp)
        //   InstallReleaseRequestWatcher   needs RequestedReleaseAt > LastReleaseRequestHandledAt
        //                                                                    → only a human moves it
        //   the sources watcher's parked auto-retry needs the IN-MEMORY park registry to hold this
        //     path — a failure that predates this PROCESS is not in it, so a source fix after a
        //     restart re-drives nothing either
        //
        // So only a human pressing Compile (or a Recycle) got such a node out; a redeploy, a
        // framework bump, a module update and a fix to the failing code all reached none of them.
        // Measured cost: NodeCompileShaping.AnchorIncludePath was written for 15 types parked on
        // memex-cloud 2026-08-12 and, five days later, #1786 found near enough the same list still
        // parked — the fix shipped and could not reach them.
        //
        // The trigger is the only thing a failure CAN honestly record: the INPUTS the verdict was
        // formed from (framework identity + installed modules + source snapshot), stamped as
        // NodeTypeDefinition.FailedBuildInputs. Live inputs differ from the stamp ⇒ this framework,
        // these modules or these sources have never had their attempt ⇒ take exactly one.
        //
        // 🚨 Bounded three ways, because an unbounded re-drive on a genuinely broken type is a
        // recompile storm on this hub's single-threaded action block:
        //   1. STRUCTURAL, and the one that actually does the work — the flip to Pending stamps the
        //      live token in the SAME Update, so the trigger this kickoff fires on is false the
        //      instant it fires. A reconcile that feeds its own trigger is the #223 write-storm
        //      shape; the stamp is what forecloses it.
        //   2. LOUD — the process-wide ledger (NodeTypeCompileParkRegistry.RecordFailureRedrive)
        //      logs an ERROR naming the path the moment it is re-driven twice for the SAME inputs,
        //      i.e. the moment (1) provably did not hold. Non-convergence must never be quiet.
        //   3. TERMINAL — past MaxAutomaticFailureRedrives the kickoff gives up for this hub's
        //      lifetime and says so, naming the type and the remedy. An explicit Compile refunds it.
        //
        // Level-triggered, NOT Take(1): CurrentSourceVersions is written by the sources watcher
        // AFTER activation, so the source half of the token is not final on the first emission — a
        // one-shot would sample the pre-seed state and miss every source fix. Convergence comes
        // from (1), not from the Rx operator.
        var redriveGivenUp = false;
        var failedVerdictKickoffSub = ownStream
            .Where(node => node?.Content is NodeTypeDefinition def
                && HasStaleFailureVerdict(def, guards.ModulesHash)
                && !IsStaticOnlyNodeType(node, def))
            // Cheap dedupe so an unrelated field edit does not re-enter the body while the flip is
            // still in flight; correctness is the re-check inside the Update lambda, not this.
            .DistinctUntilChanged(node =>
            {
                var d = (NodeTypeDefinition)node!.Content!;
                return (d.CompilationStatus, BuildInputsToken(guards.ModulesHash, d.CurrentSourceVersions));
            })
            .Subscribe(
                node =>
                {
                    if (redriveGivenUp)
                        return;
                    var def = (NodeTypeDefinition)node!.Content!;
                    var liveInputs = BuildInputsToken(guards.ModulesHash, def.CurrentSourceVersions);
                    var (forTheseInputs, total) =
                        parkRegistry?.RecordFailureRedrive(hubPath, liveInputs) ?? (1, 1);

                    if (forTheseInputs > 1)
                        logger?.LogError(
                            "Failed-verdict re-drive DID NOT CONVERGE for NodeType {HubPath}: this process has "
                            + "already re-driven it for EXACTLY these compile inputs ({Inputs}) — attempt "
                            + "{Attempt}. The flip to Pending stamps those inputs in the same write, so the "
                            + "trigger cannot legitimately still be true; something is rewriting or dropping "
                            + "FailedBuildInputs. Treat this as a write cycle, not as a slow compile.",
                            hubPath, liveInputs, forTheseInputs);

                    if (total > NodeTypeCompileParkRegistry.MaxAutomaticFailureRedrives)
                    {
                        redriveGivenUp = true;
                        logger?.LogError(
                            "Failed-verdict re-drive GIVING UP on NodeType {HubPath} after {Total} automatic "
                            + "attempt(s) in this process (limit {Limit}). The type stays at "
                            + "{Status} serving its recorded error and NOTHING will retry it automatically "
                            + "until someone requests a build (the Compile button / a fresh release request), "
                            + "which also refunds this budget. Last error: {Error}",
                            hubPath, total, NodeTypeCompileParkRegistry.MaxAutomaticFailureRedrives,
                            def.CompilationStatus, def.CompilationError ?? "(none recorded)");
                        return;
                    }

                    logger?.LogInformation(
                        "Failed-verdict re-drive: NodeType {HubPath} is settled at {Status} with no compiled "
                        + "assembly, and its verdict was formed under different compile inputs than the live "
                        + "ones (stamped '{Stamped}', live '{Live}') — flipping CompilationStatus=Pending for "
                        + "ONE fresh attempt ({Attempt} of {Limit} in this process). Recorded error: {Error}",
                        hubPath, def.CompilationStatus, def.FailedBuildInputs ?? "(never stamped)",
                        liveInputs, total, NodeTypeCompileParkRegistry.MaxAutomaticFailureRedrives,
                        def.CompilationError ?? "(none recorded)");

                    var redriveAccess = hub.ServiceProvider.GetService<AccessService>();
                    using var systemScope = redriveAccess?.ImpersonateAsSystem();
                    // 🅿️ The park is NOT lifted here (#2260). Admission and flip are one decision
                    // and they commit together, inside the lambda — see ApplyFailedVerdictRedrive.
                    workspace.GetMeshNodeStream()
                        .Update(curr => ApplyFailedVerdictRedrive(
                            curr, hubPath, guards.ModulesHash, parkRegistry))
                        .Subscribe(_ => { },
                            ex => logger?.LogWarning(ex,
                                "Failed-verdict re-drive: Update failed for {HubPath}", hubPath));
                },
                ex => logger?.LogWarning(ex,
                    "Failed-verdict re-drive: own-stream subscription faulted for {HubPath} — a "
                    + "never-compiled failure on this type will not be re-driven until the hub recycles",
                    hubPath));

        // 🚨 …and when the re-drive is deliberately DECLINED, say so ONCE. A type settled at a
        // failure whose verdict was formed under exactly the live inputs is the give-up state of
        // the mechanism above: correct, bounded — and, before this line, completely silent. Nothing
        // anywhere named a NodeType that is broken and will not be retried, which is how thirteen
        // parked types on memex-cloud went unnoticed between the fix that was written for them and
        // the issue that rediscovered them. One line per hub lifetime, naming the type, the error
        // and the remedy.
        var stuckDiagnosticSub = ownStream
            .Where(node => node?.Content is NodeTypeDefinition def
                && def.CompilationStatus is CompilationStatus.Error or CompilationStatus.Unavailable
                && string.IsNullOrEmpty(def.LatestAssemblyPath)
                && !IsStaticOnlyNodeType(node, def)
                // Same establishment gate as the re-drive: before the sources watcher has seeded a
                // snapshot the re-drive is merely WAITING, not declining, and reporting that as
                // "stuck" would cry wolf on every cold activation of a broken type.
                && def.CurrentSourceVersions is not null
                && !HasStaleFailureVerdict(def, guards.ModulesHash))
            .Take(1)
            .Subscribe(
                node =>
                {
                    var def = (NodeTypeDefinition)node!.Content!;
                    logger?.LogWarning(
                        "NodeType {HubPath} is STUCK at {Status} with no compiled assembly: its verdict was "
                        + "formed under the compile inputs that are live now ({Inputs}), so the automatic "
                        + "re-drive correctly declines and nothing will retry it until the framework, the "
                        + "installed modules or its sources change — or someone requests a build. Error: {Error}",
                        hubPath, def.CompilationStatus,
                        def.FailedBuildInputs ?? "(never stamped)",
                        def.CompilationError ?? "(none recorded)");
                },
                ex => logger?.LogDebug(ex,
                    "Stuck-type diagnostic: own-stream subscription faulted for {HubPath}", hubPath));

        return new CompositeDisposable(
            watcherSub, firstBuildKickoffSub, recoveryKickoffSub, frameworkStaleKickoffSub,
            failedVerdictKickoffSub, stuckDiagnosticSub, onDemandAdoptionSubs);
    }

    /// <summary>
    /// THE adopted-build source stamp (#1834), as a PURE function — and, since #2813, the place
    /// the adoption is CHECKED rather than merely asserted.
    ///
    /// <para>The stamp sets <see cref="NodeTypeDefinition.CompiledSources"/> to the owner's live
    /// <paramref name="snapshot"/> and consumes the request that asked for it, so the two
    /// dictionaries are the SAME content and <see cref="NodeTypeDefinition.IsDirty"/> is false by
    /// construction — which is what <c>InstallReleaseRequestWatcher</c>'s "satisfied by the
    /// existing current build" branch requires, and what makes an adoption stick.</para>
    ///
    /// <para>🚨 <b>That post-condition is also how an adopted build lied.</b> It makes the
    /// staleness detector read clean whether or not the bytes have anything to do with the live
    /// source — the detector is not broken, it is answering a question this write already answered
    /// for it. A GitSync <c>update</c> adopted a prebuilt built from older source than the commit
    /// it had just pulled, reported <c>Succeeded</c>, and the stale code destroyed four client
    /// documents' bodies, one unrecoverable (#2813). So the stamp is now conditional on the
    /// producer's recorded source fingerprint:</para>
    ///
    /// <list type="table">
    ///   <item><term>fingerprint present, MATCHES the live one</term><description>stamp;
    ///     <see cref="BuildProvenance.AdoptedVerified"/></description></item>
    ///   <item><term>fingerprint present, DISAGREES</term><description>🚨 refuse — no stamp, flip
    ///     <see cref="CompilationStatus.Pending"/> to compile the live source;
    ///     <see cref="BuildProvenance.AdoptionRefused"/></description></item>
    ///   <item><term>absent (a legacy bundle, or the owner's own not computed yet)</term>
    ///     <description>stamp — provenance is UNKNOWN, not proven stale;
    ///     <see cref="BuildProvenance.AdoptedUnverified"/></description></item>
    /// </list>
    ///
    /// <para>Pure so all three rows are unit-testable with no hub, no mesh and no timing, and
    /// shared by all three writers that may fulfil the request (the sources watcher's publication,
    /// the release-request dispatch, and the standalone
    /// <see cref="InstallAdoptedSourceStampWatcher"/>) — one stamp shape, so two of them can never
    /// disagree about what "adopted and current" means, and turning assert into check here fixes
    /// all three at once.</para>
    /// </summary>
    /// <param name="def">The owner's own definition.</param>
    /// <param name="snapshot">The owner's live source snapshot
    /// (<see cref="NodeTypeDefinition.CurrentSourceVersions"/>, or the value about to be written
    /// into it in this same update).</param>
    internal static NodeTypeDefinition ApplyAdoptedSourceStamp(
        NodeTypeDefinition def,
        IReadOnlyDictionary<string, long> snapshot,
        bool canCompileLocally)
    {
        var stamped = def with
        {
            CompiledSources = snapshot as System.Collections.Immutable.ImmutableDictionary<string, long>
                              ?? System.Collections.Immutable.ImmutableDictionary
                                  .CreateRange(snapshot),
            RequestedSourceStampAt = null,
        };

        // ── #2813 — the three-way. ─────────────────────────────────────────────────────────────
        // The producer's fingerprint is a CONTENT hash of the sources the bytes were built from;
        // CurrentSourceFingerprint is the same shape over the live set. Both are on the node, so
        // this stays pure.
        if (def.AdoptedSourceFingerprint is not { Length: > 0 } adopted)
            // LEGACY bundle — no fingerprint, so provenance is UNKNOWN, not proven stale.
            //
            // 🚨 It KEEPS the stamp, deliberately. Withholding it would make every legacy-bundle
            // type IsDirty on arrival, which stops InstallReleaseRequestWatcher's "satisfied by the
            // existing current build" branch (it requires !IsDirty) from ever absorbing — so every
            // install would recompile everything, the 43 activations / 13.5 s of boot the prebuilt
            // lane exists to remove. On a Modules:RequirePrebuilt mesh it is worse than slow: a
            // local compile is refused by design, so not stamping would PARK every legacy-bundle
            // type. That is the outage refusing unproven bundles was rejected to avoid, arriving
            // through a different door. The requirement here is VISIBILITY, not refusal: the stamp
            // stays and the provenance says it was never earned.
            return stamped with { BuildProvenance = BuildProvenance.AdoptedUnverified };

        if (def.CurrentSourceFingerprint is not { Length: > 0 } live)
            // The owner has an adopted fingerprint but has not computed its own yet (the sources
            // watcher publishes both in one write, so this is the pre-publication window). Not a
            // mismatch — nothing has been compared. Treat exactly as unknown rather than refusing
            // on an absence, which is the INCONCLUSIVE lesson from the emit canary (#890): a probe
            // must not answer its scariest branch on its own inability to run.
            return stamped with { BuildProvenance = BuildProvenance.AdoptedUnverified };

        if (string.Equals(adopted, live, StringComparison.Ordinal))
            return stamped with { BuildProvenance = BuildProvenance.AdoptedVerified };

        // 🚨 REFUSED — the only hard fail, and the one this whole mechanism exists for. The bundle
        // states which sources it was built from and they are NOT the ones this mesh holds, so the
        // bytes are last week's code over today's data (#2813: four client documents' bodies lost,
        // one unrecoverable). Do NOT stamp CompiledSources — that write is what makes IsDirty false
        // by construction and is precisely the lie — and drive a real local compile of the live
        // source by flipping Pending. The request is still consumed so it cannot re-fire.
        //
        // The refusal is recorded on the node rather than only logged: a reader must be able to see
        // that the assembly currently serving was rejected, not merely that a compile is pending.
        var refused = def with
        {
            RequestedSourceStampAt = null,
            CompilationStatus = CompilationStatus.Pending,
            BuildProvenance = BuildProvenance.AdoptionRefused,
            // 🚨 CLEARED, not merely "not stamped". Seed does NOT clear CompiledSources, so a type
            // that had previously COMPILED here carries a snapshot matching CurrentSourceVersions
            // straight into the refusal — and IsDirty would read FALSE while BuildProvenance reads
            // AdoptionRefused. That contradictory record is the same unearned claim this function
            // exists to remove, one step along: "my compiled sources are current" about bytes that
            // were explicitly rejected. It matters most exactly where it is least visible — if the
            // compile dispatched below never completes (the process dies, or RequirePrebuilt parks
            // it), that false !IsDirty is the state the node is LEFT in. ApplyCompileFailure sets
            // the same field to null for the same reason.
            CompiledSources = null,
        };

        // 🚨 …AND WHETHER THE REJECTED BYTES KEEP SERVING IS CONDITIONAL ON THIS MESH BEING ABLE TO
        // REPLACE THEM. Seed has already stamped the adopted build's assembly coordinates, so doing
        // nothing here leaves proven-stale code executing. The two answers are wrong in opposite
        // directions, and the fork is decidable right here rather than by assuming how a flag is set
        // on some mesh we cannot see:
        //
        //   canCompileLocally  -> CLEAR the coordinates. #2813 is a data-loss issue and stale bytes
        //     EXECUTING is what destroyed the documents; the Pending flip above has already
        //     dispatched a fresh compile, so the type is unserviceable for seconds. "Marked and
        //     still serving" is exactly the state that let an armed control-plane node fire pre-fix
        //     code unattended.
        //
        //   !canCompileLocally -> KEEP them (Modules:RequirePrebuilt refuses a local compile by
        //     design, so clearing would leave the type with NO assembly at all, INDEFINITELY - an
        //     outage with no recovery path, self-inflicted by a guard). The caller logs Critical
        //     naming the node, because on such a mesh nothing this process can do will fix it and a
        //     human has to rebake.
        //
        // 🚨 Do NOT collapse this to "RequirePrebuilt is unset everywhere". It is measured absent on
        // memex and memex-cloud, and #2194 item 3 records the same - that is TWO instances, and says
        // nothing about pearl, atioz, local installs, or any external instance the registry serves.
        // Configuration lives on AKS in places this repo has never heard of (Memex#148).
        return canCompileLocally
            ? refused with
            {
                // The exact pair HasUsableBuild reads, plus the served-build identity - leaving the
                // MVID would name bytes nothing serves.
                LatestAssemblyCollection = null,
                LatestAssemblyPath = null,
                LatestAssemblyMvid = null,
            }
            : refused;
    }

    /// <summary>
    /// <see cref="ApplyAdoptedSourceStamp"/> plus the two things a pure function cannot do: resolve
    /// whether THIS mesh may compile locally, and say so when an adoption is refused on a mesh that
    /// cannot replace the rejected bytes.
    ///
    /// <para>🚨 The <c>Critical</c> level on the <c>RequirePrebuilt</c> branch is not severity
    /// inflation. On such a mesh a refused adoption is terminal by construction — the local compile
    /// that would replace the bytes is refused by design — so the type keeps serving code this
    /// process has PROVEN is not built from the live source, and no amount of waiting or retrying
    /// changes that. Only a human rebaking the package does. That is the same class of statement
    /// <c>DbVersionGate</c> makes when a portal is ahead of its schema.</para>
    /// </summary>
    internal static NodeTypeDefinition ApplyAdoptedSourceStampAndReport(
        NodeTypeDefinition def,
        IReadOnlyDictionary<string, long> snapshot,
        IMessageHub hub,
        string hubPath,
        ILogger? logger)
    {
        var canCompileLocally = !PrebuiltAssemblySeeder.RequirePrebuilt(hub.ServiceProvider);
        var result = ApplyAdoptedSourceStamp(def, snapshot, canCompileLocally);

        if (result.BuildProvenance is not BuildProvenance.AdoptionRefused)
            return result;

        if (canCompileLocally)
            logger?.LogError(
                "ADOPTION REFUSED for {HubPath} (#2813): the prebuilt bundle records source "
                + "fingerprint {Adopted} but this mesh's live sources are {Live}. The bytes were "
                + "NOT accepted — assembly coordinates cleared so the rejected build stops serving "
                + "— and a fresh compile of the live source has been dispatched.",
                hubPath, def.AdoptedSourceFingerprint, def.CurrentSourceFingerprint);
        else
            logger?.LogCritical(
                "ADOPTION REFUSED for {HubPath} (#2813) AND THIS MESH CANNOT COMPILE ({Key}=true). "
                + "The bundle records source fingerprint {Adopted} but the live sources are {Live}, "
                + "so the assembly currently serving is NOT built from the source this mesh holds. "
                + "Its coordinates are deliberately LEFT IN PLACE — clearing them would leave the "
                + "type with no assembly at all, indefinitely, which is worse than marked-stale when "
                + "there is no path to a fresh build. 🚨 NOTHING THIS PROCESS CAN DO WILL FIX IT: "
                + "rebake and republish this package, then request a release.",
                hubPath, PrebuiltAssemblySeeder.RequirePrebuiltConfigKey,
                def.AdoptedSourceFingerprint, def.CurrentSourceFingerprint);

        return result;
    }

    /// <summary>
    /// Fulfils an adoption's <see cref="NodeTypeDefinition.RequestedSourceStampAt"/> on the OWNER —
    /// the standalone half of the #1834 fix, for the orderings the two folded call sites cannot
    /// reach.
    ///
    /// <para><b>Why a request at all.</b> <c>PrebuiltAssemblySeeder.Seed</c> writes CROSS-HUB, so
    /// its lambda diffs against the MIRROR's snapshot of this node — which predates the
    /// first-activation <c>CurrentSourceVersions</c> write that the seeder's own subscribe
    /// triggers. Stamping <c>CompiledSources</c> from a field the owner has not published yet
    /// stamped <c>null</c> under a non-empty snapshot, i.e. <see cref="NodeTypeDefinition.IsDirty"/>
    /// — and the release request an install issues one step later then recompiled the type that had
    /// just been adopted. The adopter therefore ASKS; the owner, whose copy of both fields is
    /// authoritative, ANSWERS.</para>
    ///
    /// <para><b>Ordering, exhaustively.</b> Writing the request and publishing the snapshot are
    /// concurrent by construction, so both orders must converge:
    /// <list type="bullet">
    ///   <item>request first — the sources watcher's publication fulfils it in the SAME write
    ///     (see <see cref="InstallSourcesWatcher"/>), so there is no window at all;</item>
    ///   <item>publication first — nothing re-emits the sources query, so THIS watcher fires on the
    ///     adoption's own emission and stamps;</item>
    ///   <item>a release request racing the second case — the release watcher fulfils the request
    ///     itself before reading <c>IsDirty</c>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>🚨 <b>It writes the node it watches, so the trigger cannot re-arm.</b> The request is
    /// ONE-SHOT: every fulfilment clears it in the same write, and the <c>Where</c> requires it to
    /// be present — so a pass can never schedule another pass (the write-storm shape of #223). The
    /// commit advances a process-local high-water mark; an emission still carrying a trigger this
    /// hub has already committed a stamp for is NON-CONVERGENCE, and is logged as an ERROR naming
    /// the type rather than retried silently.</para>
    /// </summary>
    public static IDisposable InstallAdoptedSourceStampWatcher(
        IMessageHub hub,
        IWorkspace workspace)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.CompileWatcher");
        var hubPath = hub.Address.Path;
        var ownStream = workspace.GetMeshNodeStream();
        // Advanced ONLY on the commit path (same discipline as the release watcher's mark), so a
        // duplicate emission arriving before the write lands re-enqueues an idempotent update
        // instead of being mistaken for non-convergence.
        var stampHighWater = new MonotonicHighWaterMark();

        return ActivityControlPlaneExtensions.SubscribeWithReEstablish(
            () => ownStream
                .Where(node => node?.Content is NodeTypeDefinition def
                    && def.RequestedSourceStampAt is not null
                    // The owner has not published its snapshot yet — nothing authoritative to
                    // stamp FROM. The publication itself carries the stamp (InstallSourcesWatcher),
                    // so waiting here costs nothing and guessing here would cost correctness.
                    && def.CurrentSourceVersions is not null
                    // 🚨 #2813 — the SAME rule applied to the fingerprint. The stamp request is
                    // ONE-SHOT, so consuming it while the three-way is unanswerable spends the only
                    // chance to refuse: the judgement records AdoptedUnverified and nothing ever
                    // re-opens the question. An adopted fingerprint with no live counterpart yet is
                    // precisely that state, and it is transient — only the sources watcher holds
                    // the live source nodes, and its publication both computes the fingerprint and
                    // carries the stamp. So wait for it here, exactly as we wait for the snapshot.
                    && (def.AdoptedSourceFingerprint is not { Length: > 0 }
                        || def.CurrentSourceFingerprint is { Length: > 0 })),
            node =>
            {
                var observed = (NodeTypeDefinition)node!.Content!;
                var requestedAt = observed.RequestedSourceStampAt!.Value;
                if (!stampHighWater.IsPast(requestedAt))
                {
                    // We already committed a stamp for this very trigger and it is STILL standing.
                    // Loud, and deliberately not retried: a reconcile that cannot converge must
                    // name itself once, not spin (#223).
                    if (observed.IsDirty)
                        logger?.LogError(
                            "[AdoptedSourceStamp] {HubPath}: the adopted build's source stamp was "
                            + "committed for request {RequestedAt} and the request is still standing "
                            + "with IsDirty=true — the write did not converge; the next release "
                            + "request will recompile this type",
                            hubPath, requestedAt);
                    else
                        logger?.LogDebug(
                            "[AdoptedSourceStamp] {HubPath}: replayed emission for an already-"
                            + "committed request {RequestedAt} — nothing to do",
                            hubPath, requestedAt);
                    return;
                }

                workspace.GetMeshNodeStream().Update(curr =>
                {
                    if (curr.Content is not NodeTypeDefinition def) return curr;
                    // Consumed between the emission and this lambda (the sources watcher's
                    // publication or a release dispatch got there first) — a legitimate no-op.
                    if (def.RequestedSourceStampAt is null) return curr;
                    if (def.CurrentSourceVersions is not { } liveSources) return curr;
                    stampHighWater.Advance(def.RequestedSourceStampAt.Value);
                    return curr with { Content = ApplyAdoptedSourceStampAndReport(
                        def, liveSources, hub, hubPath, logger) };
                }).Subscribe(
                    _ => logger?.LogInformation(
                        "[AdoptedSourceStamp] {HubPath}: adopted build stamped with the owner's live "
                        + "source snapshot ({Count} source(s)) — the next release request is "
                        + "satisfied by it instead of recompiling",
                        hubPath, observed.CurrentSourceVersions!.Count),
                    ex => logger?.LogWarning(ex,
                        "[AdoptedSourceStamp] {HubPath}: failed to stamp the adopted build's source "
                        + "snapshot — the next release request will recompile it", hubPath));
            },
            hub.Address,
            logger,
            "Adopted source-stamp watcher");
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
        // 🅿️ Park registry — when a source change lands on a PARKED (terminally-failed) type,
        // this watcher un-parks and re-drives the compile (the "retry only if the sources
        // changed" path). Same mesh-scoped singleton the compile watcher's parked short-circuit
        // uses; null in the rare host that does not register it (no auto-retry, unchanged before).
        var parkRegistry = hub.ServiceProvider.GetService<NodeTypeCompileParkRegistry>();
        // #2948 — the mesh read the source fingerprint's `@@`-include walk needs. Built once per
        // watcher; it issues nothing at all for a source set with no `@@` in it.
        var includeReader = SourceFingerprintIncludeReader.For(hub, logger);

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
        // #891: SubscribeWithReEstablish, not a bare log-and-die Subscribe — a fault in the
        // per-path source streams (a cross-hub delivery blip riding through Switch) used to
        // kill source tracking permanently: IsDirty froze and the parked-type auto-retry
        // never fired again. Transient faults re-establish (the DistinctUntilChanged resets
        // and the current source set re-resolves — idempotent); poisoned own content stops
        // loudly (the compile watcher's park is the visible sink for the same emission).
        return ActivityControlPlaneExtensions.SubscribeWithReEstablish(
            () => ownStream
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
                // call.
                return Observable.Using(
                    () => accessService?.ImpersonateAsSystem()
                          ?? System.Reactive.Disposables.Disposable.Empty,
                    _ => NodeSources.GetSources(workspace, def, hubPath));
            })
            .Switch()
            .Select(sources =>
            {
                // Fold the full current set → path → NodeTypeDefinition.SourceVersionOf.
                // Same rule CompiledSources is keyed on, so IsDirty compares like-for-like
                // (empty set → empty snapshot → IsDirty=false when CompiledSources is also
                // empty). Keying on the raw timestamp is what let an un-timestamped source
                // record 1601 on both sides and never read as changed (#1836).
                var snap = System.Collections.Immutable.ImmutableDictionary<string, long>.Empty;
                foreach (var n in sources)
                    if (!string.IsNullOrEmpty(n.Path))
                        snap = snap.SetItem(n.Path!, NodeTypeDefinition.SourceVersionOf(n));
                // #2813 — carry the live source NODES alongside the tick snapshot so the
                // publication below can fingerprint their CONTENT when (and only when) there is an
                // adopted fingerprint to compare it against. The nodes are already materialised
                // here; the hash is what costs, and it is paid only where it is used.
                return (Snapshot: (IReadOnlyDictionary<string, long>)snap, Nodes: sources);
            })
            // 🚨 #2948 — the CONTENT fingerprint now covers the `@@`-include closure, and resolving
            // that needs mesh READS, so it happens HERE (an observable step) rather than inside the
            // pure Update lambda below. Cost: `CollectIncludeClosure` scans each source's text for
            // "@@" and issues NOTHING when there is none — which is nearly every type — so the
            // reads are paid only by the types that actually have includes.
            //
            // 🚨 Switch(), not SelectMany: a newer source set must SUPERSEDE an in-flight include
            // resolution, or a slow read could publish a fingerprint for a set that is already gone.
            //
            // 🚨 An include the mesh could not READ (a stall, an unavailable owner) is INCONCLUSIVE,
            // never "absent". Degrading it to absence shortens the hashed set, which is
            // indistinguishable from a stale bundle and refuses a perfectly good adoption — on a
            // Modules:RequirePrebuilt mesh that is terminal. So the fingerprint is dropped for this
            // emission (null) and the previously published value stands; the judgement then takes
            // ApplyAdoptedSourceStamp's "nothing has been compared" branch (AdoptedUnverified),
            // which is the honest answer and the same #890 rule the emit canary follows.
            .Select(published => NodeTypeSourceFingerprint
                .Compute(published.Nodes, hubPath, includeReader, logger)
                .Select(fingerprint => (published.Snapshot, Fingerprint: (string?)fingerprint))
                .Catch((SourceIncludeUnavailableException ex) =>
                {
                    logger?.LogWarning(ex,
                        "SourcesWatcher: the @@-include closure for {HubPath} could not be "
                        + "established, so CurrentSourceFingerprint is left at its previous value "
                        + "for this emission — an unreadable include must never read as an absent "
                        + "one", hubPath);
                    return Observable.Return((published.Snapshot, Fingerprint: (string?)null));
                }))
            .Switch(),
                published =>
                {
                    var snapshot = published.Snapshot;
                    // 🅿️ Auto-retry a PARKED (terminally-failed) type when its SOURCE snapshot has
                    // CHANGED since the failure — the "retry only if the sources changed" path. The
                    // compile watcher's parked short-circuit swallows a bare Pending flip, so we
                    // Unpark FIRST (in-memory + synchronous → happens-before the Pending emission the
                    // watcher observes), then flip Pending under System (re-driving framework compile
                    // state is infrastructure, not a user write — same shape as the parked
                    // re-settle in InstallCompileWatcher) so a fresh compile runs against the fixed
                    // sources. An UNCHANGED broken type never reaches here (ShouldRetryForSourceChange
                    // is false), so the failure stays contained — no recompile storm.
                    if (parkRegistry?.ShouldRetryForSourceChange(hubPath, snapshot) == true)
                    {
                        parkRegistry.Unpark(hubPath);
                        using (accessService?.ImpersonateAsSystem())
                            workspace.GetMeshNodeStream().Update(curr =>
                            {
                                if (curr.Content is not NodeTypeDefinition def) return curr;
                                // Never clobber an in-flight compile (Pending/Compiling) — only
                                // refresh the snapshot; a settled state re-drives to Pending.
                                var status =
                                    def.CompilationStatus is CompilationStatus.Pending
                                        or CompilationStatus.Compiling
                                        ? def.CompilationStatus
                                        : CompilationStatus.Pending;
                                return curr with
                                {
                                    Content = def with
                                    {
                                        CurrentSourceVersions = snapshot,
                                        CompilationStatus = status
                                    }
                                };
                            }).Subscribe(
                                _ => { },
                                ex => logger?.LogWarning(ex,
                                    "SourcesWatcher: failed to auto-retry parked {HubPath} after source change",
                                    hubPath));
                        return;
                    }

                    workspace.GetMeshNodeStream().Update(curr =>
                    {
                        if (curr.Content is not NodeTypeDefinition def) return curr;

                        // 🚨 #1834 — an adoption that could not know this snapshot asked the OWNER
                        // to stamp it (NodeTypeDefinition.RequestedSourceStampAt). Fulfil it in the
                        // SAME write that publishes the snapshot: one write instead of two on the
                        // boot path, and — decisively — NO window in which the node carries a
                        // published CurrentSourceVersions against an unstamped adopted build, which
                        // is the state a release request reads as dirty and recompiles.
                        var pendingStamp = def.RequestedSourceStampAt is not null;

                        // 🚨 #2813 — the live CONTENT fingerprint, computed HERE because this is
                        // the one place that already holds the live source nodes, and written in
                        // the SAME update as the tick snapshot so a reader can never see one
                        // without the other.
                        //
                        // 🚨 UNCONDITIONALLY, and that is the fix (#2813 second half). It used to
                        // be computed only when there was already something to compare it against
                        // ("a pending stamp, or an adopted fingerprint on the node"), and that
                        // condition is unsatisfiable in the ordering the incident actually took:
                        // the owner publishes its snapshot FIRST, then the adoption's patch lands
                        // carrying both the adopted fingerprint and the stamp request — and this
                        // watcher does NOT re-run, because its DistinctUntilChanged keys on the
                        // source QUERIES, which did not change. The judgement then read an absent
                        // live value, took the "inconclusive" branch and degraded to
                        // AdoptedUnverified. Every stale-adoption refusal was therefore reachable
                        // only in the other ordering.
                        //
                        // The cost that condition was protecting against is gone with the shape:
                        // NodeTypeSourceFingerprint hashes the compile INPUT (each source node's
                        // code text), not every node's Content serialised through the hub's
                        // polymorphic options. It is a SHA-256 over text this branch already holds
                        // in memory, on a path that runs at hub activation and per source edit —
                        // i.e. exactly where a Roslyn compile is about to cost four orders of
                        // magnitude more.
                        //
                        // 🚨 #2948 — it is now resolved UPSTREAM (the include closure needs mesh
                        // reads, which cannot happen inside this pure lambda), and it can be NULL:
                        // null means the closure could not be established, NOT that there is
                        // nothing to hash. The previous value stands in that case — inventing one
                        // from a short closure is a false refusal.
                        var fingerprint = published.Fingerprint;

                        // Idempotent: no-op when CurrentSourceVersions already
                        // matches the just-computed snapshot. IsDirty is a
                        // computed property — derives from CurrentSourceVersions
                        // vs CompiledSources — so no separate flag to write.
                        // (A pending stamp still has to be consumed, so it never no-ops.)
                        //
                        // 🚨 The fingerprint is part of the equality. Without it a node persisted
                        // before this field existed — snapshot already current, fingerprint null —
                        // would no-op forever and never acquire one, so the type could never be
                        // judged again. The condition is what makes the field SELF-HEALING on the
                        // first activation after an upgrade. An INCONCLUSIVE emission (fingerprint
                        // null) drops out of the comparison entirely: it has nothing to say about
                        // the field, so it must neither force a write nor block the snapshot's.
                        if (!pendingStamp
                            && def.CurrentSourceVersions is not null
                            && DictEquals(def.CurrentSourceVersions, snapshot)
                            && (fingerprint is null
                                || string.Equals(
                                    def.CurrentSourceFingerprint, fingerprint,
                                    StringComparison.Ordinal)))
                            return curr;

                        var updated = def with
                        {
                            CurrentSourceVersions = snapshot,
                            CurrentSourceFingerprint = fingerprint ?? def.CurrentSourceFingerprint,
                        };

                        if (pendingStamp)
                            updated = ApplyAdoptedSourceStampAndReport(updated, snapshot, hub, hubPath, logger);

                        return curr with { Content = updated };
                    }).Subscribe(
                        _ => { },
                        ex => logger?.LogWarning(ex,
                            "SourcesWatcher: failed to write CurrentSourceVersions for {HubPath}",
                            hubPath));
                },
            hub.Address,
            logger,
            "Sources watcher");
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
        // #891: SubscribeWithReEstablish, not a bare log-and-die Subscribe — a dead release
        // watcher means the user's Compile button silently does nothing forever. On a transient
        // re-establish the high-water marks live OUTSIDE the factory, so an already-committed
        // trigger replayed by the fresh subscription fails the IsPast gate — no double compile.
        // Poisoned own content stops loudly (the compile watcher parks the type on the same
        // emission — the visible sink).
        return ActivityControlPlaneExtensions.SubscribeWithReEstablish(
            () => ownStream
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
                                          and not CompilationStatus.Compiling),
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
                    // …and refund the AUTOMATIC re-drive budget (#1793): a human asking for a build
                    // is the strongest signal that a give-up should be reconsidered, so a type that
                    // exhausted its automatic attempts is eligible again after an explicit Compile.
                    parkRegistry?.ResetFailureRedrives(hubPath);
                    logger?.LogInformation(
                        "[ReleaseRequestWatcher] {HubPath}: handling RequestedReleaseAt={Req} (force={Force}, lastHandled={Handled})",
                        hubPath, triggerAt,
                        node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.RequestedReleaseForce,
                        node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.LastReleaseRequestHandledAt);
                    workspace.GetMeshNodeStream().Update(curr =>
                    {
                        if (curr.Content is not NodeTypeDefinition def) return curr;
                        // 🚨 #1834 — fulfil a pending adoption stamp BEFORE the satisfied-branch
                        // reads IsDirty, in the same write. This is the last ordering in which the
                        // adoption could still be thrown away: the sources watcher has published
                        // CurrentSourceVersions, the standalone stamp watcher's write has not
                        // committed yet, and the release request arrives in between — the node then
                        // reads dirty and recompiles the build that was just adopted. Reading the
                        // owner's own pair here is authoritative, so nothing is widened: the branch
                        // still requires a genuinely non-dirty definition.
                        //
                        // 🚨 #2813 — …but ONLY when the three-way can actually be answered. An
                        // adopted fingerprint whose live counterpart the owner has not published
                        // yet is inconclusive, and the request is one-shot: fulfilling it here
                        // would spend the refusal on an absence and record AdoptedUnverified
                        // permanently. Leaving it standing costs a recompile of a type that may
                        // have been adoptable — the safe direction, and the one #1834 was
                        // optimising away, not the one it was preventing.
                        if (def.RequestedSourceStampAt is not null
                            && def.CurrentSourceVersions is { } liveSources
                            && (def.AdoptedSourceFingerprint is not { Length: > 0 }
                                || def.CurrentSourceFingerprint is { Length: > 0 }))
                            def = ApplyAdoptedSourceStampAndReport(def, liveSources, hub, hubPath, logger);
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
                        {
                            // 🚨 CONSUME a trigger the in-flight compile already satisfies (#2544).
                            // Parking every such trigger is what turned one logical event into N
                            // sequential compiles: the high-water is deliberately not advanced, so
                            // the trigger re-fires on the FIRST COMPILE'S OWN terminal write-back —
                            // the compile does not re-trigger itself, it is the clock that releases
                            // the next queued one. Measured in production as pairs 65 ms apart and
                            // seven compiles for one merge.
                            //
                            // When the in-flight compile was dispatched for exactly these inputs it
                            // will produce byte-for-byte what this request asks for, so re-running
                            // it buys nothing and costs an instance-hub invalidation plus a
                            // "newer build available" adornment. Force still always compiles, and
                            // an UNSTAMPED in-flight compile (a direct Pending flip from one of the
                            // kickoff paths, which never sets the token) parks exactly as before —
                            // absorbing against an unknown input set would be a guess.
                            var requestedToken = BuildInputsToken(
                                GuardsOf(hub).ModulesHash, def.CurrentSourceVersions);
                            if (IsSatisfiedByInFlightCompile(def, requestedToken))
                            {
                                logger?.LogInformation(
                                    "[ReleaseRequestWatcher] {HubPath}: release request absorbed — a "
                                    + "compile is already in flight for these exact inputs, so it "
                                    + "produces what this request asks for", hubPath);
                                dispatchHighWater.Advance(triggerAt);
                                return curr with
                                {
                                    Content = def with
                                    {
                                        LastReleaseRequestHandledAt =
                                            def.LastReleaseRequestHandledAt is { } a && a > triggerAt
                                                ? def.LastReleaseRequestHandledAt
                                                : triggerAt,
                                        RequestedReleaseBy = null,
                                    }
                                };
                            }
                            return curr;
                        }
                        // #1707 slice 3 — "if yes, we take it; if no, we generate": a release
                        // request arriving while the CURRENT state already holds a VALID build of
                        // the CURRENT sources (typically just ADOPTED from a prebuilt bundle by
                        // the install/push consumption, or compiled by another replica) is
                        // SATISFIED, not recompiled — the trigger is consumed on the same commit
                        // path a compile dispatch would use, so it can never re-fire, and the
                        // request's outcome is byte-for-byte what a recompile would have produced.
                        // RequestedReleaseForce stays the user's escape hatch: an explicit force
                        // always compiles.
                        if (!def.RequestedReleaseForce
                            && def.CompilationStatus is CompilationStatus.Ok
                            && !def.IsDirty
                            && HasUsableBuild(curr, def, GuardsOf(hub)))
                        {
                            logger?.LogInformation(
                                "[ReleaseRequestWatcher] {HubPath}: release request satisfied by the "
                                + "existing current build (adopted or already compiled) — no compile "
                                + "dispatched", hubPath);
                            dispatchHighWater.Advance(triggerAt);
                            return curr with
                            {
                                Content = def with
                                {
                                    LastReleaseRequestHandledAt =
                                        def.LastReleaseRequestHandledAt is { } sat && sat > triggerAt
                                            ? sat
                                            : triggerAt,
                                    RequestedReleaseBy = null,
                                }
                            };
                        }
                        // COMMIT: this is the one path that stamps
                        // LastReleaseRequestHandledAt — advance the in-memory
                        // high-water in the same breath so the two marks never diverge.
                        dispatchHighWater.Advance(triggerAt);
                        return curr with
                        {
                            Content = def with
                            {
                                CompilationStatus = CompilationStatus.Pending,
                                // Stamp WHAT this dispatch is for, on the same commit that flips to
                                // Pending, so a later trigger for the same inputs can be recognised
                                // as already satisfied instead of queued behind it.
                                DispatchedBuildInputs = BuildInputsToken(
                                    GuardsOf(hub).ModulesHash, def.CurrentSourceVersions),
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
            hub.Address,
            logger,
            "ReleaseRequestWatcher");
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
    /// The live MeshWeaver framework identity a compiled NodeType release is pinned to —
    /// delegates to <see cref="FrameworkBuildIdentity.FrameworkVersion"/> (MeshWeaver.Compiler,
    /// the toolchain assembly the identity is anchored on since #1707). A mismatch against a
    /// NodeType's <c>CompiledFrameworkVersion</c> means "recompile". Kept as a delegating shim so
    /// the many Graph-internal consumers (and the IVT'd bake pipeline) need no re-pointing.
    /// </summary>
    internal static string FrameworkVersion => FrameworkBuildIdentity.FrameworkVersion;

    /// <summary>Degradation warning from the identity resolution (a torn/unusable surface
    /// manifest fell back to the stamp/MVID layer), or null on the happy path — see
    /// <see cref="FrameworkBuildIdentity.FrameworkVersionWarning"/>.</summary>
    internal static string? FrameworkVersionWarning => FrameworkBuildIdentity.FrameworkVersionWarning;

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
    ///
    /// <para><b>The modules fingerprint is DECISIVE (#1664 step 11).</b> When the caller passes
    /// the mesh's live <see cref="InstalledModulesFingerprint.Hash"/> as
    /// <paramref name="modulesHash"/>, a build stamped with a DIFFERENT non-null
    /// <see cref="NodeTypeDefinition.CompiledModulesHash"/> is not usable — a module-only update
    /// (framework MVID unchanged, module MVIDs changed) must invalidate baked builds that could
    /// reference the replaced module, which the framework rule cannot see once modules ship
    /// separately from the image. Two deliberate MATCH cases: a <c>null</c> STAMP is grandfathered
    /// (compiled before modules joined the compile surface — the framework rule alone governs it,
    /// per the contract on <see cref="NodeTypeDefinition.CompiledModulesHash"/>), and a
    /// <c>null</c> CALLER (no mesh in scope to resolve the fingerprint from) keeps the legacy
    /// framework-only behavior. The empty string is NOT null — it is the real stamped hash of a
    /// zero-module mesh and compares like any other value.</para>
    /// </summary>
    internal static bool HasUsableBuild(MeshNode node, NodeTypeDefinition def, string? modulesHash = null) =>
        HasUsableBuild(node, def, modulesHash is null ? null : new BuildGuards(modulesHash, null, ""));

    /// <summary>
    /// <see cref="HasUsableBuild(MeshNode, NodeTypeDefinition, string?)"/> with the full build
    /// guards (#1707 slice 2): when the definition carries a per-type
    /// <see cref="NodeTypeDefinition.CompiledDependencies"/> record AND the guards carry a
    /// resolver, the RECORD decides — every stamped (name → surface-id) pair must still resolve
    /// identically in this environment, so a module update invalidates only its dependents and
    /// the instance-wide fingerprint stops keying anything. Null record or null resolver falls
    /// back to the legacy whole-set modules-hash rule.
    /// </summary>
    internal static bool HasUsableBuild(MeshNode node, NodeTypeDefinition def, BuildGuards? guards) =>
        !string.IsNullOrEmpty(def.LatestAssemblyCollection)
        && !string.IsNullOrEmpty(def.LatestAssemblyPath)
        && string.Equals(def.CompiledFrameworkVersion, FrameworkVersion, StringComparison.Ordinal)
        && DependenciesValid(def, guards);

    /// <summary>
    /// The dependency clause shared by <see cref="HasUsableBuild(MeshNode, NodeTypeDefinition, BuildGuards?)"/>
    /// and <see cref="HasStaleFrameworkBuild(NodeTypeDefinition, BuildGuards?)"/>: record-based
    /// when a record and a resolver are both present, legacy modules-hash otherwise (null stamp
    /// or null live hash = MATCH, exactly the pre-#1707 grandfathering).
    /// </summary>
    private static bool DependenciesValid(NodeTypeDefinition def, BuildGuards? guards)
    {
        if (guards is null)
            return true;
        if (def.CompiledDependencies is { } record && guards.DependencyIdOf is not null)
            // 🚨 The RE-EVALUATION LANE's read half (#1976). With no live digest on the guards
            // this is byte-for-byte the metadata-only rule it replaced: LiveContentKeyOf returns
            // null, FindMismatchAfterReevaluation demotes nothing, and the toolchain entry decides
            // exactly as before. With one, the content key answers directly — and it is decisive
            // in BOTH directions (a moved generated input invalidates a record whose metadata
            // entries all still match, which no other check in the framework can see).
            return Compiler.CompiledDependencies.FindMismatchAfterReevaluation(
                record, guards.DependencyIdOf, guards.ToolchainId,
                Compiler.CompiledDependencies.LiveContentKeyOf(
                    record, guards.DependencyIdOf, guards.LiveGeneratedInputDigest)) is null;
        return guards.ModulesHash is null
            || def.CompiledModulesHash is null
            || string.Equals(def.CompiledModulesHash, guards.ModulesHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live environment a build's validity is judged against: the per-type dependency-record
    /// resolver + toolchain id (#1707 slice 2) and the legacy installed-module fingerprint.
    /// Resolve once per watcher install via <see cref="GuardsOf"/>.
    /// </summary>
    /// <summary>What the re-evaluation lane decided to do with a build the cheap, metadata-only
    /// predicates have already called stale — see <see cref="DecideStaleBuildAction"/>.</summary>
    internal enum StaleBuildAction
    {
        /// <summary>Compile. The pre-lane default, and every inconclusive case that is not
        /// covered by <see cref="Skip"/>.</summary>
        Rebuild,

        /// <summary>The dependency record's reserved <c>!toolchain</c> entry is restamped and no
        /// compile is dispatched — the demotion, made durable.</summary>
        RestampDependencyRecord,

        /// <summary>Neither: the store already holds bytes for the live framework, so nothing is
        /// rebuilt — and nothing is restamped either.</summary>
        Skip,
    }

    private sealed record StaleBuildOutcome(
        MeshNode Node, StaleBuildAction Action, string? Detail);

    /// <summary>
    /// 🚨 THE BRANCH RULE, pure — the safety property of the whole lane as a checkable table
    /// rather than a shape you have to read the pipeline to see. Same split, and the same reason,
    /// as <see cref="NodeTypeBakeStatus.Classify"/>: <see cref="ResolveStaleBuildAction"/> gathers
    /// the four facts (one node read, one store probe, one regeneration) and this decides.
    ///
    /// <para>🚨 <b>The invariant: a restamp is licensed ONLY when the framework held still.</b>
    /// There, the build the record describes is addressed under THIS tag, so the record and the
    /// bytes are the same thing and the content key's verdict is about exactly them. When the
    /// framework moved, a store hit resolves a DIFFERENT file — the record names a build under the
    /// previous tag, and nothing here has hashed what the live tag returned. Restamping on that
    /// would assert validity for bytes the lane never examined and would suppress the
    /// instance-activation self-heal that corrects the case today
    /// (<c>FrameworkStaleAssembly_SelfHealsOnInstanceActivation</c> went red the one time this
    /// rule was relaxed). Binding a record to bytes it does not name needs the store sidecar
    /// (#1707 residual 1).</para>
    /// </summary>
    /// <param name="frameworkHeldStill">The stamped <c>CompiledFrameworkVersion</c> equals the
    /// live one, so the staleness is a DEPENDENCY drift rather than a roll.</param>
    /// <param name="storeHasLiveFrameworkBytes">The assembly store resolved bytes for this type
    /// under the LIVE framework tag. Only consulted when the framework moved (with it held still
    /// the record's own build is the addressable one).</param>
    /// <param name="verdict">What <see cref="ContentKeyReevaluation.Reevaluate"/> concluded.</param>
    /// <param name="usableAfterReevaluation">
    /// <see cref="HasUsableBuild(MeshNode, NodeTypeDefinition, BuildGuards?)"/> re-asked with the
    /// regenerated digest on the guards — the production predicate itself, so the lane and the
    /// predicate can never disagree about what it just proved.</param>
    internal static StaleBuildAction DecideStaleBuildAction(
        bool frameworkHeldStill,
        bool storeHasLiveFrameworkBytes,
        ReevaluationVerdict verdict,
        bool usableAfterReevaluation)
    {
        if (frameworkHeldStill)
            return verdict is ReevaluationVerdict.CarryForward && usableAfterReevaluation
                ? StaleBuildAction.RestampDependencyRecord
                : StaleBuildAction.Rebuild;

        // 🚨 The framework moved: NO path here returns RestampDependencyRecord.
        if (!storeHasLiveFrameworkBytes)
            return StaleBuildAction.Rebuild;
        return verdict is ReevaluationVerdict.Rebuild
            ? StaleBuildAction.Rebuild
            : StaleBuildAction.Skip;
    }

    /// <summary>
    /// 🚨 THE RE-EVALUATION LANE (#1976) — decide what to do with a NodeType the cheap predicates
    /// have already called stale. It is a SECOND predicate behind a decision that had already been
    /// taken, after <c>Take(1)</c>, so it runs at most once per hub lifetime and only for a type
    /// the framework was already about to act on.
    ///
    /// <para>🚨 <b>The two branches carry DIFFERENT evidence, and only one of them licenses a
    /// restamp.</b> This distinction is the whole safety argument, and getting it wrong is how a
    /// re-evaluation turns into a stale serve.</para>
    ///
    /// <list type="number">
    ///   <item><b>The framework HELD STILL</b> (the staleness is a dependency drift). The build the
    ///   record describes is addressed under THIS tag, so the record and the bytes are the same
    ///   thing, and the content key's verdict is about exactly them. A <c>CarryForward</c> here is
    ///   confirmed against the production predicate itself —
    ///   <see cref="HasUsableBuild(MeshNode, NodeTypeDefinition, BuildGuards?)"/> re-asked with the
    ///   regenerated digest on the guards — and then the record is restamped. Anything else
    ///   compiles, exactly as before.</item>
    ///
    ///   <item><b>The framework MOVED.</b> The store is probed under the LIVE tag, as before. A
    ///   MISS compiles. A HIT used to skip unconditionally; it now re-evaluates, and a
    ///   <c>Rebuild</c> verdict COMPILES — a genuinely new invalidation, because the kickoff is
    ///   <c>Take(1)</c>, so a type whose generated input had moved kept serving the old bytes
    ///   forever. 🚨 But a <c>CarryForward</c> here must NOT restamp. The evidence is about the
    ///   build the RECORD names, which lives under the PREVIOUS tag; the bytes the store just
    ///   resolved are a different file, produced by whoever compiled under the live framework, and
    ///   nothing here has hashed them. Restamping <c>CompiledFrameworkVersion</c> on that would
    ///   assert validity for bytes the lane never examined AND suppress the instance-activation
    ///   self-heal that corrects exactly this case today. Binding the two needs the store sidecar
    ///   (#1707 residual 1) or a cross-generation read — both deliberately out of scope. So the
    ///   branch keeps its pre-lane behaviour: skip, and change nothing.</item>
    /// </list>
    /// </summary>
    private static IObservable<StaleBuildOutcome> ResolveStaleBuildAction(
        IMessageHub hub,
        string hubPath,
        MeshNode node,
        BuildGuards guards,
        IMeshNodeCompilationService compilationService,
        ILogger? logger)
    {
        var def = DefinitionOf(hub, node);
        if (string.Equals(def?.CompiledFrameworkVersion, FrameworkVersion, StringComparison.Ordinal))
            return Reevaluate(hub, hubPath, node, def!, guards, compilationService, logger)
                .Select(r => new StaleBuildOutcome(
                    node,
                    DecideStaleBuildAction(
                        frameworkHeldStill: true,
                        storeHasLiveFrameworkBytes: false,
                        r.Result.Verdict,
                        HasUsableBuild(
                            node, def!, guards with { LiveGeneratedInputDigest = r.Digest })),
                    r.Result.Detail));

        return ResolveAssemblyStore(hub)
            .TryGetAssemblyPath(hubPath, def?.LastCompiledVersion ?? node.Version)
            .Take(1)
            .Catch<string?, Exception>(_ => Observable.Return<string?>(null))
            .SelectMany(path => string.IsNullOrEmpty(path)
                ? Observable.Return(new StaleBuildOutcome(
                    node,
                    DecideStaleBuildAction(false, false, ReevaluationVerdict.Inconclusive, false),
                    null))
                : Reevaluate(hub, hubPath, node, def, guards, compilationService, logger)
                    .Select(r => new StaleBuildOutcome(
                        node,
                        DecideStaleBuildAction(
                            frameworkHeldStill: false,
                            storeHasLiveFrameworkBytes: true,
                            r.Result.Verdict,
                            usableAfterReevaluation: false),
                        r.Result.Detail)));
    }

    /// <summary>
    /// Regenerate this NodeType's compile input and ask <see cref="ContentKeyReevaluation"/> what
    /// it means. Never throws and never blocks: a fault, an unestablished source set or a host
    /// with no regenerating compilation service all yield <see cref="ReevaluationVerdict.Inconclusive"/>,
    /// which restamps nothing.
    /// </summary>
    private static IObservable<(Reevaluation Result, string? Digest)> Reevaluate(
        IMessageHub hub,
        string hubPath,
        MeshNode node,
        NodeTypeDefinition? def,
        BuildGuards guards,
        IMeshNodeCompilationService compilationService,
        ILogger? logger)
    {
        if (def?.CompiledDependencies is not { } record)
            return Observable.Return((new Reevaluation(
                ReevaluationVerdict.Inconclusive,
                "no dependency record is stamped on this build"), (string?)null));

        // The concrete service, not IMeshNodeCompilationService: regeneration is a Graph-internal
        // capability of the compile path, and putting it on the contract interface would make
        // every implementer owe an entry point that only this lane consumes. A host running a
        // different implementation gets INCONCLUSIVE — never a carry-forward.
        if (compilationService is not MeshNodeCompilationService compiler)
            return Observable.Return((new Reevaluation(
                ReevaluationVerdict.Inconclusive,
                "this host's compilation service cannot regenerate a compile input"),
                (string?)null));

        return compiler.RegenerateGeneratedInputDigest(node)
            .Select(digest => (
                Result: ContentKeyReevaluation.Reevaluate(
                    record, guards.DependencyIdOf, guards.ToolchainId, digest),
                Digest: digest))
            .Catch<(Reevaluation Result, string? Digest), Exception>(ex =>
            {
                logger?.LogInformation(ex,
                    "Re-evaluation for {HubPath} could not complete — the build keeps the "
                    + "metadata-only verdict", hubPath);
                return Observable.Return((
                    new Reevaluation(
                        ReevaluationVerdict.Inconclusive, "the re-evaluation faulted"),
                    (string?)null));
            });
    }

    /// <summary>
    /// 🚨 THE RESTAMP (#1976): the dependency record's reserved <c>!toolchain</c> entry is moved to
    /// the live value, because the regenerated compile input proved the proxy it stands for has not
    /// moved. Reached ONLY from the framework-held-still branch of
    /// <see cref="ResolveStaleBuildAction"/>.
    ///
    /// <para><b>What is asserted, and what is not.</b> The claim is exactly "the toolchain's MVID
    /// moved but this build's generated input did not, and every assembly it binds still resolves
    /// identically" — which is what the content-key comparison measured, about the build this
    /// record names, under the tag it is addressed by. Nothing else moves:
    /// <c>CompiledFrameworkVersion</c>, the assembly coordinates, the source snapshot and the
    /// compile status are untouched, and the <c>!input</c> entry and the assembly entries — the
    /// evidence — are carried through verbatim. This is not a compile and it does not pretend to
    /// be one.</para>
    ///
    /// <para><b>Why it is worth writing at all.</b> Without it the lane would have to regenerate
    /// on every activation to reach the same verdict; with it, every metadata-only reader
    /// (<c>HasUsableBuild</c>, the bake probe, the prebuilt seeder) answers correctly on its own.
    /// The trigger re-arms by itself: the next toolchain move mismatches the restamped entry
    /// again.</para>
    /// </summary>
    private static void RestampCarriedForwardBuild(
        IMessageHub hub,
        IWorkspace workspace,
        string hubPath,
        BuildGuards guards,
        string? detail,
        ILogger? logger)
    {
        logger?.LogInformation(
            "Re-evaluation CARRIED FORWARD the build for {HubPath}: the toolchain moved but the "
            + "regenerated compile input did not ({Detail}) — restamping '{Key}' instead of "
            + "recompiling",
            hubPath, detail ?? "content key unchanged", Compiler.CompiledDependencies.ToolchainKey);

        var access = hub.ServiceProvider.GetService<AccessService>();
        using var systemScope = access?.ImpersonateAsSystem();
        workspace.GetMeshNodeStream().Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            // Don't clobber an in-flight compile — a concurrent release request or enrichment
            // self-heal may have flipped Pending/Compiling since the verdict was formed, and its
            // write-back is authoritative over this one.
            if (def.CompilationStatus is CompilationStatus.Pending or CompilationStatus.Compiling)
                return curr;
            if (def.CompiledDependencies is not { } record) return curr;
            // 🚨 Re-check the exact state the verdict was formed on, with NO reliance on the
            // demoted rule: the framework must still be the live one (a roll between the verdict
            // and this write would put us on the other branch, where a restamp is not licensed),
            // and the entry must still be the stale one (a genuine rebuild may have landed first).
            if (!string.Equals(def.CompiledFrameworkVersion, FrameworkVersion, StringComparison.Ordinal))
                return curr;
            if (!record.TryGetValue(Compiler.CompiledDependencies.ToolchainKey, out var stamped)
                || string.Equals(stamped, guards.ToolchainId, StringComparison.Ordinal))
                return curr;
            return curr with
            {
                Content = def with
                {
                    CompiledDependencies = Compiler.CompiledDependencies.RestampToolchain(
                        record, guards.ToolchainId),
                },
            };
        }).Subscribe(_ => { },
            ex => logger?.LogWarning(ex,
                "Re-evaluation: restamp Update failed for {HubPath}", hubPath));
    }

    /// <param name="ModulesHash">The legacy instance-wide installed-module fingerprint.</param>
    /// <param name="DependencyIdOf">The live surface-id resolver for the per-type record.</param>
    /// <param name="ToolchainId">The live toolchain id for the record's reserved entry.</param>
    /// <param name="LiveGeneratedInputDigest">🚨 The stage-1 digest of THIS NodeType's compile
    /// input as REGENERATED now (#1976), or null — the overwhelmingly common case — when the
    /// caller did not regenerate. Null means the metadata-only rule applies unchanged; it never
    /// means "unchanged". Only the re-evaluation lane
    /// (<see cref="ResolveStaleBuildAction"/>) supplies it, and only for the ONE type its guards
    /// describe, which is why this rides on the per-hub guards rather than being a resolver.</param>
    internal sealed record BuildGuards(
        string? ModulesHash,
        Func<string, string?>? DependencyIdOf,
        string ToolchainId,
        string? LiveGeneratedInputDigest = null);

    /// <summary>The ONE resolver every call site goes through, so "which live environment" can
    /// never fork between the usable check and the stale check.</summary>
    internal static BuildGuards GuardsOf(IMessageHub hub) =>
        new(ModulesHashOf(hub), DependencyIdResolverOf(hub), ProcessToolchainId);

    /// <summary>
    /// The mesh's live installed-module fingerprint, resolved from the hub's service tree
    /// (<see cref="InstalledModulesFingerprint"/> is a mesh-scoped singleton registered by
    /// <c>AddGraph</c>), or null when the mesh does not register one. Legacy rule input — the
    /// per-type record supersedes it wherever a record is stamped.
    /// </summary>
    internal static string? ModulesHashOf(IMessageHub hub) =>
        hub.ServiceProvider.GetService<InstalledModulesFingerprint>()?.Hash;

    /// <summary>
    /// The surface-id resolver over THIS mesh's environment (process surface manifest + the
    /// mesh's installed modules) — see <see cref="Compiler.CompiledDependencies.CreateIdResolver"/>.
    /// </summary>
    internal static Func<string, string?> DependencyIdResolverOf(IMessageHub hub) =>
        Compiler.CompiledDependencies.CreateIdResolver(
            FrameworkBuildIdentity.ProcessSurfacePairs,
            ModuleMvidsOf(hub),
            FrameworkBuildIdentity.ProcessImplMvidOf);

    /// <summary>Installed module simple name → implementation MVID ("N"), for the resolver's
    /// exact-build module ids.</summary>
    internal static IReadOnlyDictionary<string, string> ModuleMvidsOf(IMessageHub hub)
    {
        var modules = hub.ServiceProvider.GetServices<InstalledModuleAssembly>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var name = module.Assembly.GetName().Name;
            if (!string.IsNullOrEmpty(name))
                map[name] = module.Assembly.ManifestModule.ModuleVersionId.ToString("N");
        }
        return map;
    }

    /// <summary>The process's toolchain id for the record's reserved entry — constant per
    /// process (a Lazy of an immutable string, not runtime-mutable state).</summary>
    internal static string ProcessToolchainId => _toolchainId.Value;

    private static readonly Lazy<string> _toolchainId = new(() =>
        Compiler.CompiledDependencies.ComputeToolchainId(FrameworkBuildIdentity.ProcessImplMvidOf));

    /// <summary>
    /// True when this NodeType has a cached compiled assembly (the durable
    /// <see cref="NodeTypeDefinition.LatestAssemblyCollection"/> /
    /// <see cref="NodeTypeDefinition.LatestAssemblyPath"/> pair is populated) that was built
    /// against a DIFFERENT MeshWeaver framework than the live one — i.e. the ONLY reason
    /// <see cref="HasUsableBuild(MeshNode, NodeTypeDefinition, BuildGuards?)"/> is false is the framework-version mismatch, not a missing
    /// assembly. This is the "platform self-update left a stale assembly behind" shape
    /// (issue #464, Defect 1): the bytes exist but are ABI-incompatible with the current
    /// process, so they must be rebuilt — a source-clean, never-touched NodeType included.
    ///
    /// <para>Distinct from <see cref="HasUsableBuild(MeshNode, NodeTypeDefinition, BuildGuards?)"/>: a NodeType that was never compiled
    /// (no assembly fields) is NOT "stale" — it is handled by the first-build kickoff.</para>
    /// </summary>
    /// <summary>
    /// The shared assembly store, or <see cref="NullAssemblyStore"/> when the host registered none.
    /// A null store reports every lookup as a miss, so callers fall back to their pre-store behaviour
    /// (rebuild) rather than wrongly concluding a build exists.
    /// </summary>
    private static IAssemblyStore ResolveAssemblyStore(IMessageHub hub) =>
        hub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;

    /// <summary>The node's content as a <see cref="NodeTypeDefinition"/>, or null.</summary>
    private static NodeTypeDefinition? DefinitionOf(IMessageHub hub, MeshNode? node) =>
        node?.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);

    // The twin of HasUsableBuild's modules-hash join (#1664 step 11): a build whose stamped
    // CompiledModulesHash differs from the live set is STALE the same way a framework mismatch
    // is — the bytes exist but may bind a replaced module's old ABI. Without this twin the flip
    // in HasUsableBuild would WEDGE every such type after a module-only update: HasUsableBuild
    // says "not usable" so instances fall back to the default config, but nothing would re-drive
    // the compile (first-build kickoff needs Status=null, recovery needs Compiling). Null stamp /
    // null caller keep legacy behavior, exactly as in HasUsableBuild.
    internal static bool HasStaleFrameworkBuild(NodeTypeDefinition def, string? modulesHash = null) =>
        HasStaleFrameworkBuild(def, modulesHash is null ? null : new BuildGuards(modulesHash, null, ""));

    /// <summary>The twin with full build guards — see
    /// <see cref="HasUsableBuild(MeshNode, NodeTypeDefinition, BuildGuards?)"/>: a build whose
    /// stamped dependency record no longer validates is STALE the same way a framework mismatch
    /// is, and this twin is what re-drives the compile for it.</summary>
    internal static bool HasStaleFrameworkBuild(NodeTypeDefinition def, BuildGuards? guards) =>
        !string.IsNullOrEmpty(def.LatestAssemblyCollection)
        && !string.IsNullOrEmpty(def.LatestAssemblyPath)
        && !string.IsNullOrEmpty(def.CompiledFrameworkVersion)
        && (!string.Equals(def.CompiledFrameworkVersion, FrameworkVersion, StringComparison.Ordinal)
            || (guards is not null && !DependenciesValid(def, guards)));

    /// <summary>
    /// The COMPILE INPUTS a verdict is formed from, as one comparable token — the framework
    /// identity, the installed-module fingerprint, and the source snapshot
    /// (see <see cref="NodeTypeDefinition.FailedBuildInputs"/>).
    ///
    /// <para>Pure and hub-free so the failure stamp
    /// (<see cref="ApplyCompileFailure"/>) and the re-drive kickoff compute it the SAME way —
    /// a token that two call sites derived differently would either never converge (a permanent
    /// re-drive) or never fire (the hole this closes), and both failures are silent.</para>
    ///
    /// <para>Readable rather than a bare hash: an operator reading a stuck node's record can see
    /// WHICH input the standing verdict was formed under. The source set is folded to
    /// <c>count:sha256[0..16]</c> so the token stays a line rather than a kilobyte of paths.</para>
    /// </summary>
    /// <summary>
    /// Whether a release request is already satisfied by the compile currently in flight (#2544).
    ///
    /// <para>Three ways this is deliberately FALSE, each for its own reason:</para>
    /// <list type="bullet">
    ///   <item><c>RequestedReleaseForce</c> — the user's explicit escape hatch always compiles.</item>
    ///   <item>A DIFFERENT token — the sources, framework or modules moved after the in-flight
    ///   compile was dispatched, so it will NOT produce what this request asks for. Absorbing here
    ///   would silently drop the user's latest edits.</item>
    ///   <item>An UNSTAMPED in-flight compile (<c>DispatchedBuildInputs is null</c>) — several
    ///   kickoff paths flip straight to Pending without going through the release watcher, so
    ///   nothing recorded what they were built for. Absorbing against an unknown input set is a
    ///   guess; park instead, exactly as before.</item>
    /// </list>
    /// </summary>
    internal static bool IsSatisfiedByInFlightCompile(NodeTypeDefinition def, string requestedToken)
        => !def.RequestedReleaseForce
           && def.DispatchedBuildInputs is { } dispatched
           && string.Equals(dispatched, requestedToken, StringComparison.Ordinal);

    internal static string BuildInputsToken(
        string? modulesHash, IReadOnlyDictionary<string, long>? sources) =>
        $"fw={FrameworkVersion};mod={modulesHash ?? "(none)"};src={SourceToken(sources)}";

    /// <summary>The source snapshot folded to a short, order-insensitive token. A <c>null</c>
    /// snapshot (the sources watcher has not seeded yet) is deliberately DISTINCT from an empty
    /// one (the mesh answered "this type has no sources"): those are different facts, and
    /// collapsing them would let an unseeded node read as a source-less one.</summary>
    private static string SourceToken(IReadOnlyDictionary<string, long>? sources)
    {
        if (sources is null)
            return "(unseeded)";
        if (sources.Count == 0)
            return "0";
        var joined = string.Join(
            "\n",
            sources.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}@{kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(joined));
        return $"{sources.Count}:{Convert.ToHexString(digest)[..16].ToLowerInvariant()}";
    }

    /// <summary>
    /// 🚨 True when a NodeType is sitting on a FAILURE VERDICT that the LIVE compile inputs have
    /// moved past — the predicate the failed-verdict re-drive kickoff fires on (issue #1793).
    ///
    /// <para><b>Why it cannot be <see cref="HasStaleFrameworkBuild(NodeTypeDefinition, BuildGuards?)"/>.</b>
    /// That twin needs <see cref="NodeTypeDefinition.LatestAssemblyCollection"/>,
    /// <see cref="NodeTypeDefinition.LatestAssemblyPath"/> and
    /// <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/> — all three written ONLY by a
    /// successful compile. Its own filter already reads
    /// <c>CompilationStatus is Ok or Error</c>, so it INTENDS to cover a failed type; it cannot,
    /// because a type that never compiled successfully here has none of the coordinates it
    /// delegates to. The harder case got the weaker treatment: a type that succeeded once and went
    /// stale rebuilds automatically, a type that failed the first time never did.</para>
    ///
    /// <para>So this predicate is deliberately the COMPLEMENT: it applies exactly where the
    /// framework-stale twin cannot — a settled failure with NO recorded assembly — and compares the
    /// stamped verdict inputs against the live ones instead of comparing assembly coordinates.</para>
    ///
    /// <para><b>Bounded by construction.</b> The kickoff writes the live token into
    /// <see cref="NodeTypeDefinition.FailedBuildInputs"/> in the same update that flips to
    /// <see cref="CompilationStatus.Pending"/>, so this returns false immediately afterwards:
    /// exactly one automatic attempt per distinct (framework, modules, sources) triple. A
    /// <c>null</c> stamp — every node that failed before this field existed, and every node whose
    /// Error was baked into a committed file — differs from any live token, which is precisely the
    /// one-off recovery those nodes need.</para>
    ///
    /// <para><see cref="CompilationStatus.Unavailable"/> is included, and — unlike an Error — it
    /// does NOT have to wait for the compile inputs to change. It records that a compile never
    /// reached a verdict at all, so it is even less of a reason to stop trying than an Error, and
    /// the input token cannot express the thing that actually changed (a recycling address came
    /// back). Requiring a token change made this branch unreachable for the #1701 shape: a
    /// package-root recycle moves no framework, no module and no source.
    /// (<c>NodeTypeContractHandler.EnsureCompileDispatched</c> already re-drives it on the next
    /// REQUEST; this adds the activation-time path, under the same <b>count</b> bound.)</para>
    ///
    /// <para>🚨 <b>Never re-drive from an UNESTABLISHED source set.</b> On a cold activation the
    /// sources watcher has not written <see cref="NodeTypeDefinition.CurrentSourceVersions"/> yet,
    /// so the source half of the live token is not "no sources" — it is "not known yet". Firing
    /// there would (a) drive a compile from a set nobody established, which is the #1216 lesson
    /// ("a compile driven from a set you did not establish produces verdicts about code from
    /// evidence you do not have"), and (b) burn a second attempt for free, because the watcher's
    /// first write changes the token and the type would be re-driven AGAIN one emission later. The
    /// sources watcher always writes a snapshot for every type this predicate applies to — the
    /// empty map when the queries match nothing — so this ORDERS the mechanism rather than
    /// disabling it, and a mesh that cannot answer the source query degrades to no re-drive rather
    /// than to a wrong verdict.</para>
    /// </summary>
    internal static bool HasStaleFailureVerdict(NodeTypeDefinition def, string? modulesHash) =>
        def.CompilationStatus is CompilationStatus.Error or CompilationStatus.Unavailable
        // NO usable build was ever recorded — the complement of HasStaleFrameworkBuild, which
        // owns every case where coordinates DO exist.
        && string.IsNullOrEmpty(def.LatestAssemblyCollection)
        && string.IsNullOrEmpty(def.LatestAssemblyPath)
        // The source set must be ESTABLISHED before it can be compared — see above.
        && def.CurrentSourceVersions is not null
        && (
            // 🚨 An UNAVAILABLE verdict is stale ON ITS OWN — the inputs are irrelevant, because the
            // inputs were never the reason (#1701). Unavailable records "we never found out": the
            // compile did not run, or it read an address that was recycling. Re-asking is the ONLY
            // way to find out, and the input token cannot express "the mesh has settled down" — a
            // package-root recycle changes no framework, no module and no source, so gating the
            // re-drive on the token made "Retry the read" advice that nothing could follow, exactly
            // as #1701 reports. The three existing bounds still apply and are what keeps this from
            // becoming a poll: the flip to Pending stamps the live token in the same write, the
            // DistinctUntilChanged upstream collapses repeats, and MaxAutomaticFailureRedrives caps
            // the process at five attempts before giving up LOUDLY. An address that never comes back
            // therefore costs five compiles and one error line, not a loop.
            def.CompilationStatus is CompilationStatus.Unavailable
            || !string.Equals(
                def.FailedBuildInputs,
                BuildInputsToken(modulesHash, def.CurrentSourceVersions),
                StringComparison.Ordinal));

    /// <summary>
    /// 🅿️ THE failed-verdict re-drive's COMMIT step (#2260) — the whole decision, including the
    /// retry ADMISSION, as one function of the node state it commits against. Passed as the
    /// <c>Update</c> lambda so it runs on the data source's action block, exactly once, immediately
    /// before the write it produces.
    ///
    /// <para><b>Two defects this shape closes, both in the old
    /// <c>parkRegistry.Unpark(hubPath); …Update(…)</c> pair.</b></para>
    ///
    /// <para>(1) <b>An un-park decided from a SNAPSHOT.</b> The subscriber's emission is a snapshot;
    /// by the time the body ran, a terminal failure could have parked the type again
    /// (<c>NodeTypeCompileParkRegistry.ParkAndNotify</c> is the only writer of that entry). The
    /// un-park removed a park the decision never observed — and, when the lambda's own re-checks
    /// then declined to flip, NOTHING re-parked afterwards. The type ended up neither parked nor
    /// re-driven: exactly the state the park exists to make impossible. Here every early return
    /// leaves the registry untouched, so the registry is only ever touched by a re-drive that is,
    /// in the same committed state, genuinely flipping the type back to Pending.</para>
    ///
    /// <para>(2) <b>Un-parking at all.</b> Even a perfectly-validated un-park leaves the type
    /// un-parked for the whole Pending → refusal → re-park round-trip, and any trigger arriving in
    /// that window is admitted. Under <c>Modules:RequirePrebuilt</c> that window opened on EVERY
    /// refusal. The re-drive never needed the failure cleared — only its one flip let through — so
    /// it asks for a one-shot <c>AdmitOneRetry</c> and <see cref="NodeTypeCompileParkRegistry.IsParked"/>
    /// stays true at every instant. The admission still happens-before the Pending emission the
    /// compile watcher observes (this lambda runs ahead of the commit), which is what the old
    /// hoisted-out un-park was buying.</para>
    /// </summary>
    /// <param name="curr">The node state this write is committing against (authoritative).</param>
    /// <param name="hubPath">The NodeType's path.</param>
    /// <param name="modulesHash">The installed-module fingerprint half of the compile inputs.</param>
    /// <param name="parkRegistry">The park registry, or <c>null</c> on a host that has none.</param>
    /// <returns><paramref name="curr"/> unchanged when the re-drive declines; otherwise the node
    /// flipped to <see cref="CompilationStatus.Pending"/> with the live inputs stamped.</returns>
    internal static MeshNode ApplyFailedVerdictRedrive(
        MeshNode curr, string hubPath, string? modulesHash,
        NodeTypeCompileParkRegistry? parkRegistry)
    {
        if (curr?.Content is not NodeTypeDefinition d) return curr!;
        // Never clobber an in-flight compile (a concurrent release request or
        // enrichment self-heal may already have flipped Pending/Compiling).
        if (d.CompilationStatus is CompilationStatus.Pending
                                or CompilationStatus.Compiling)
            return curr;
        // Re-check against the state being committed — a genuine compile may have settled, or a
        // fresh terminal failure may have re-parked, between the outer Where and this write.
        if (!HasStaleFailureVerdict(d, modulesHash)) return curr;
        // 🅿️ Committing the flip — and ONLY now — claim the one-shot admission, so the compile
        // watcher's parked short-circuit lets this Pending emission through. The park itself is
        // never touched.
        //
        // 🚨 …and ONLY while the type is actually PARKED. An admission for an un-parked type is
        // never consumed — the short-circuit it exists to pass is not taken — so it would sit in
        // the registry until some LATER park, where a stray Pending flip could spend it and get
        // through the very containment this restores. That case is the common one, not a corner:
        // a failure that predates this PROCESS is not in the (in-memory) registry at all. Gating
        // here makes the leak impossible rather than merely unlikely, because it establishes
        // "an admission implies a standing park" — and every path that REMOVES a park (Unpark,
        // OnCompileSucceeded) clears admissions with it, so no admission can outlive the park it
        // was granted against.
        if (parkRegistry?.IsParked(hubPath) == true)
            parkRegistry.AdmitOneRetry(hubPath);
        return curr with
        {
            Content = d with
            {
                // 🚨 The bookkeeping and the flip land TOGETHER. Stamping the live
                // inputs here — not only in ApplyCompileFailure — is what makes the
                // re-drive unable to schedule another pass even if the compile
                // never writes back at all (process death mid-compile, the parked
                // re-settle, a poisoned content read).
                FailedBuildInputs = BuildInputsToken(modulesHash, d.CurrentSourceVersions),
                CompilationStatus = CompilationStatus.Pending,
                DispatchedBuildInputs = null
            }
        };
    }

    /// <summary>
    /// 🚨 THE terminal stamp of a Pending flip the compile watcher refuses to dispatch WITHOUT
    /// running Roslyn (issue #2264) — the shared body of <c>InstallCompileWatcher</c>'s
    /// <c>SettleAsError</c> local function, extracted so the per-call-site
    /// <see cref="NodeTypeDefinition.FailedBuildInputs"/> decision is unit-testable without a hub.
    ///
    /// <para><c>SettleAsError</c> is the ONLY way a NodeType reaches <see cref="CompilationStatus.Error"/>
    /// under the adopt-only gate (<c>Modules:RequirePrebuilt</c>, #2193 §A), which never runs a
    /// compile — so it is the only writer of <see cref="NodeTypeDefinition.FailedBuildInputs"/> for
    /// such a type, and the two call sites need OPPOSITE answers:</para>
    ///
    /// <list type="bullet">
    ///   <item>the <b>adopt-only gate's refusal</b> (<paramref name="formedUnderLiveInputs"/> =
    ///     <c>true</c>) is formed under the LIVE compile inputs RIGHT NOW, so stamping them is
    ///     honest — and is what stops <see cref="HasStaleFailureVerdict"/> from reading an
    ///     unstamped verdict as "never attempted" and driving one needless automatic re-drive
    ///     (#1793) per refused type.</item>
    ///   <item>the <b>parked short-circuit's re-settle</b> (<paramref name="formedUnderLiveInputs"/>
    ///     = <c>false</c>) re-serves an ALREADY-SETTLED failure's verdict — stamping the CURRENT
    ///     live inputs there would overwrite (or fabricate, for a type parked before this fix)
    ///     the record of what THAT original failure was formed under, silently masking a genuine
    ///     input change since that failure and defeating #1793's whole recovery path. So it leaves
    ///     whatever <see cref="NodeTypeDefinition.FailedBuildInputs"/> the node already carries
    ///     untouched — which is exactly what OMITTING the field from the <c>with</c> would do too;
    ///     spelled out here so the two branches are visibly symmetric instead of one being a
    ///     silent no-op.</item>
    /// </list>
    /// </summary>
    /// <param name="parkedDef">The NodeType definition as currently persisted (authoritative — read
    /// inside the <c>Update</c> lambda, never a captured snapshot).</param>
    /// <param name="reason">The refusal/parked-error message, or <c>null</c> to keep whatever
    /// <see cref="NodeTypeDefinition.CompilationError"/> is already recorded.</param>
    /// <param name="formedUnderLiveInputs">Whether THIS settle is itself a decision formed under
    /// the live compile inputs (see above).</param>
    /// <param name="modulesHash">The installed-module fingerprint half of the compile inputs.</param>
    internal static NodeTypeDefinition ApplyGateSettle(
        NodeTypeDefinition parkedDef, string? reason, bool formedUnderLiveInputs, string? modulesHash) =>
        parkedDef with
        {
            CompilationStatus = CompilationStatus.Error,
            CompilationError = reason
                ?? parkedDef.CompilationError
                ?? "Compilation is parked after a terminal failure; request a release (Compile) to retry.",
            FailedBuildInputs = formedUnderLiveInputs
                ? BuildInputsToken(modulesHash, parkedDef.CurrentSourceVersions)
                : parkedDef.FailedBuildInputs,
            // A forced release that ends in a park is spent too (#2818): under RequirePrebuilt the
            // compile it asked for is refused by design and the park names that; a bundle that
            // arrives later must be adoptable again, not refused by the stale force.
            RequestedReleaseForce = false,
        };

    /// <summary>
    /// 🚨 The start timestamp of the compile claim ALREADY STANDING on
    /// <paramref name="def"/> — the pure rule behind <see cref="RunCompile"/>'s Compiling flip
    /// (#2895), so "which run does this stamp describe" is a checkable function rather than a
    /// shape you have to read the pipeline to see.
    ///
    /// <para><see cref="NodeTypeDefinition.LastCompileStartedAt"/> means "when the Pending →
    /// Compiling transition happened" — that is the contract
    /// <c>DynamicTypePreWarmer.SourcesMovedDuringCompile</c> compares source ticks against, and the
    /// one <c>IsLiveCompileClaim</c> ages a stranded claim by. <c>HandleDispatchCompile</c>'s
    /// compare-and-swap is where that transition occurs and where the stamp is minted, so a node
    /// already reading <see cref="CompilationStatus.Compiling"/> with a start time is carrying THIS
    /// run's own value and it is returned unchanged. Only a caller that reached the flip without
    /// that transition (there is none today — <see cref="RunCompile"/> has one caller — but the
    /// method is public) mints a fresh one, so a Compiling status can never be left without the
    /// timestamp its liveness is judged by.</para>
    /// </summary>
    internal static DateTimeOffset StartOfThisCompileClaim(NodeTypeDefinition def) =>
        def is { CompilationStatus: CompilationStatus.Compiling, LastCompileStartedAt: { } started }
            ? started
            : DateTimeOffset.UtcNow;

    /// <summary>
    /// THE terminal stamp of a SUCCESSFUL compile — the exact field set
    /// <see cref="RunCompile"/>'s write-back has always applied, extracted as a pure function so
    /// the initial-bake batch driver (issue #1207, <c>NodeTypeBatchBake</c> in
    /// <c>MeshWeaver.Hosting</c>) stamps IDENTICAL state without duplicating the compiler's
    /// write-back logic. One compiler, one stamp shape — a field added here reaches both the
    /// activation-driven and the batch-driven path by construction.
    /// </summary>
    /// <param name="def">The NodeType definition as currently persisted.</param>
    /// <param name="result">The successful compile's result (assembly + store coordinates).</param>
    /// <param name="currentNodeVersion">
    /// Fallback for <see cref="NodeTypeDefinition.LastCompiledVersion"/> when the result carries no
    /// store version — must be the version of the node the stamp lands on (see the inline note:
    /// the stamped version must MATCH the version the <c>IAssemblyStore</c> upload used).
    /// </param>
    /// <param name="activityPath">The CONFIRMED compile-activity path, or null when none was created.</param>
    /// <param name="releasePath">The CONFIRMED release-node path, or null when the create didn't land.</param>
    internal static NodeTypeDefinition ApplyCompileSuccess(
        NodeTypeDefinition def,
        NodeCompilationResult result,
        long currentNodeVersion,
        string? activityPath,
        string? releasePath,
        string? modulesHash = null)
        => def with
        {
            CompilationStatus = CompilationStatus.Ok,
            CompilationError = null,
            CompilationDiagnostics = null,
            // 🚨 #2813 — Roslyn built these bytes HERE, from the source this mesh holds, so the
            // provenance is Compiled and nothing about an earlier adoption survives. Without this
            // the field was write-once-per-adoption: a type refused as stale and then successfully
            // recompiled kept reading AdoptionRefused forever, and one refused adoption in a node's
            // history would mark it permanently. That turns the operator signal the incident asked
            // for into noise people learn to ignore, and it would make #2820's execute-time
            // interlock refuse to run a type whose live source it had just compiled itself.
            // ApplyCompileFailure deliberately does NOT do this: after a failed compile the bytes
            // in place are still whatever the refusal left, so the refusal is still the true story.
            BuildProvenance = Mesh.Services.BuildProvenance.Compiled,
            LastCompileSucceededAt = DateTimeOffset.UtcNow,
            // Stamp LastCompiledVersion to MATCH the version the IAssemblyStore upload used
            // (set by UploadToStoreIfNeeded — the captured node Version at compile kickoff).
            // A different version here would point activation at a store key that has no
            // bytes — TryGetAssemblyPath miss, activation falls back to the default config.
            LastCompiledVersion = result.Version ?? currentNodeVersion,
            LastCompilationActivityPath = activityPath,
            LatestReleasePath = releasePath ?? def.LatestReleasePath,
            ReleaseNotes = releasePath is not null ? null : def.ReleaseNotes,
            CompiledSources = result.CompiledSources
                ?? System.Collections.Immutable.ImmutableDictionary<string, long>.Empty,
            // Cross-silo durable assembly reference, from the IAssemblyStore upload
            // (UploadToStoreIfNeeded). Falls back to the previous values on a producer
            // without a store so legacy consumers keep the AssemblyLocation path.
            LatestAssemblyCollection = result.Collection ?? def.LatestAssemblyCollection,
            LatestAssemblyPath = result.ContentPath ?? def.LatestAssemblyPath,
            // 🚨 The IDENTITY of the bytes, beside their ADDRESS (#2471). The path is a store key
            // whose contents a pod resolves through its own local cache, so a path comparison
            // cannot tell "this instance is on the published build" from "this instance is on a
            // stale local copy at the same key" — which is why every recycle was inert while a
            // portal served old code under a green Ok. Read from the emitted assembly (metadata
            // only, nothing loaded); a producer with no readable file leaves the previous stamp
            // alone rather than erasing it, exactly as the other assembly fields do.
            LatestAssemblyMvid =
                ServedBuildIdentity.OfFile(result.AssemblyLocation) ?? def.LatestAssemblyMvid,
            // The framework the assembly bound against — HasUsableBuild compares this to the
            // live FrameworkVersion so a MeshWeaver redeploy forces a recompile instead of
            // loading an ABI-stale DLL.
            CompiledFrameworkVersion = FrameworkVersion,
            // The installed-module fingerprint (#1644 step 1 — recorded, not yet decisive; the
            // property doc on NodeTypeDefinition carries the full story). Preserved when the
            // caller cannot resolve a fingerprint, so a stamped hash is never erased.
            CompiledModulesHash = modulesHash ?? def.CompiledModulesHash,
            // The per-type dependency record (#1707 slice 2) — read off the emitted assembly by
            // CompileResultFromAssembly and DECISIVE over the fingerprint above wherever present.
            // Preserved when the result carries none (e.g. a legacy producer), never erased.
            CompiledDependencies = result.CompiledDependencies ?? def.CompiledDependencies,
            // Clear the consumed release-requester so a later System-only recompile doesn't
            // mis-attribute its release to a stale prior user.
            RequestedReleaseBy = null,
            // The force that dispatched this compile (if any) is spent: the compile watcher read
            // it to skip on-demand adoption (#2818), and leaving it set would make the NEXT,
            // unforced trigger skip adoption too.
            RequestedReleaseForce = false,
            // 🚨 The standing FAILURE verdict is gone, so the inputs it was formed from must go
            // with it (#1793). Leaving the token behind would make a LATER failure look like it
            // had already had its automatic attempt under these inputs, and the type would sit
            // broken with nothing due to retry it.
            FailedBuildInputs = null,
            // 🚨 This compile just answered the question an adoption's stamp request was asking
            // (#1834), and answered it from the set it actually consumed. A request left standing
            // would let a later CurrentSourceVersions publication re-stamp CompiledSources over
            // THIS snapshot — which is how a needed rebuild gets suppressed. Consume it.
            RequestedSourceStampAt = null
        };

    /// <summary>
    /// The one-line summary of a FAILED compile for the node's
    /// <see cref="NodeTypeDefinition.CompilationError"/> — names the exception TYPE for a
    /// non-Roslyn abort (a bare "Object reference not set…" told CI triage nothing — #612).
    /// Extracted with <see cref="ApplyCompileFailure"/> so both write-back paths summarize
    /// identically.
    /// </summary>
    internal static string SummarizeCompileError(NodeCompilationResult? result, Exception? error)
        => error switch
        {
            null => result?.Log?.Errors() is { Count: > 0 } errs
                ? string.Join("; ", errs.Select(m => m.Message))
                : "Compilation produced no assembly",
            CompilationException ce => ce.Message,
            // A non-Roslyn abort out of Emit carries the canary verdict
            // (EmitPipeline.ProbeSharedEmitState) — appended HERE, in the one
            // funnel, so the answer to "is this compilation's inputs or the whole process?"
            // rides the record triage already reads, without a second log line.
            { } other => $"{other.GetType().Name}: {other.Message}"
                + (other.Data[EmitPipeline.EmitCanaryDataKey] is string canary
                    ? $" [{canary}]"
                    : string.Empty),
        };

    /// <summary>
    /// <c>true</c> when <paramref name="error"/> says the compile never reached a VERDICT — the
    /// source set could not be established (<see cref="SourceDiscoveryUnavailableException"/>) or a
    /// mesh address the compile had to read was RECYCLING for the reader's whole budget
    /// (<see cref="AddressRecyclingException"/>). Both are availability facts about the mesh, never
    /// statements about the code.
    ///
    /// <para>🚨 One predicate, two consumers, and they MUST agree (#1701).
    /// <see cref="ApplyCompileFailure"/> uses it to stamp <see cref="CompilationStatus.Unavailable"/>
    /// instead of <see cref="CompilationStatus.Error"/>; the terminal outcome handler in
    /// <see cref="RunCompile"/> uses it to skip <c>NodeTypeCompileParkRegistry.OnCompileFailed</c>
    /// entirely. When only the first honoured the distinction, three recycling reads parked the
    /// type, the compile watcher's parked short-circuit re-settled it as <c>Error</c>, and — since
    /// the only automatic un-park is a SOURCE change, which a recycle never produces — it stayed
    /// there until a human pressed Compile. Duplicating the type test at both sites is how they
    /// drifted apart in the first place, so there is deliberately exactly one.</para>
    ///
    /// <para>🚨 <b>The third member is an emit the CANARY proved the PROCESS could not do
    /// (#890).</b> When Roslyn's <c>Emit</c> THROWS, <see cref="EmitPipeline"/> immediately
    /// re-emits a trivial, freshly parsed, known-good control compilation — first against the same
    /// references, then against an image-backed CoreLib that shares nothing. A
    /// <c>REFERENCES</c> or <c>BELOW-ROSLYN</c> verdict means <b>that control also failed</b>: the
    /// process can no longer emit ANY assembly, so this compile learned nothing whatsoever about
    /// the code it was handed. Recording it as <see cref="CompilationStatus.Error"/> states a
    /// verdict that was never formed — and it is durable state: <see cref="ApplyCompileFailure"/>
    /// also stamps <see cref="NodeTypeDefinition.FailedBuildInputs"/>, so
    /// <see cref="HasStaleFailureVerdict"/> then reads "this failure was formed under exactly the
    /// live inputs" and the automatic re-drive correctly declines — for a fault the inputs had
    /// nothing to do with, and which a fresh process would not reproduce. Measured on run
    /// 33322993649 shard 1: after the first such throw, <b>7 of 7</b> later compiles that reached
    /// Roslyn's metadata writer failed identically over 6 m 15 s and none succeeded, while every
    /// compile that needed only DIAGNOSTICS still returned correct <c>CS####</c> codes — the
    /// process was emit-dead and five tests reported it under five unrelated names.</para>
    ///
    /// <para>🚨 It is keyed on the verdict being READ, never on a canary being PRESENT — every
    /// emit-phase throw carries one, and <c>OK</c> / <c>INCONCLUSIVE</c> / <c>DIVERGENT</c> all
    /// mean the process was NOT shown to be at fault. Widening this to "any infrastructure fault"
    /// is the blind spot <c>SourceSnapshotEstablishmentTest.EveryOtherCompileFailure_StillStampsError</c>
    /// exists to refuse, and it stays refused: see <see cref="EmitPipeline.IsProcessEmitFailure"/>.
    /// The bake gate filing THIS case as unevaluated rather than as a code regression is the
    /// correct reading, not a hole — a bake whose process cannot emit has evaluated nothing.</para>
    /// </summary>
    internal static bool IsAvailabilityNonVerdict(Exception? error) =>
        error is SourceDiscoveryUnavailableException or AddressRecyclingException
        || (error is not null
            && EmitPipeline.IsProcessEmitFailure(error.Data[EmitPipeline.EmitCanaryDataKey]));

    /// <summary>
    /// THE terminal stamp of a FAILED compile — the exact field set <see cref="RunCompile"/>'s
    /// write-back has always applied on failure, extracted for the same one-stamp-shape reason
    /// as <see cref="ApplyCompileSuccess"/>.
    ///
    /// <para>🚨 The status is <see cref="CompilationStatus.Error"/> — Roslyn's verdict — EXCEPT
    /// when the compile never ran because its SOURCE SET could not be established
    /// (<see cref="SourceDiscoveryUnavailableException"/>) or because a mesh address it had to
    /// read was RECYCLING for the reader's whole budget
    /// (<see cref="AddressRecyclingException"/> — a package-root hub's install-recycle wedging
    /// dispose is MeshWeaver#1701's trigger). Those are availability failures, so they stamp
    /// <see cref="CompilationStatus.Unavailable"/>: "the compile state could not be
    /// determined; nothing is known to be wrong with the source". Everything downstream already
    /// reads the two apart — the instance overlay drops "please correct the code",
    /// <c>EnsureCompileDispatched</c> re-dispatches on the next request, and the bake gate files it
    /// as unevaluated rather than as an image regression (issue #1218).</para>
    /// </summary>
    internal static NodeTypeDefinition ApplyCompileFailure(
        NodeTypeDefinition def,
        NodeCompilationResult? result,
        Exception? error,
        string? activityPath,
        string? modulesHash = null)
        => def with
        {
            CompilationStatus = IsAvailabilityNonVerdict(error)
                ? CompilationStatus.Unavailable
                : CompilationStatus.Error,
            CompilationError = SummarizeCompileError(result, error),
            CompilationDiagnostics = result?.Diagnostics is { Count: > 0 } ds
                ? System.Collections.Immutable.ImmutableList.CreateRange(ds)
                : null,
            LastCompilationActivityPath = activityPath,
            CompiledSources = null,
            // 🚨 RECORD WHAT THE VERDICT WAS FORMED FROM (#1793). This is the one durable fact a
            // failure can leave behind — it writes no assembly coordinates and no framework stamp,
            // which is exactly why every automatic re-drive used to skip a never-compiled failure
            // forever. The source half is the snapshot the compile CONSUMED (the result's own set
            // when it resolved one, else the node's live snapshot), so an edit that lands while a
            // doomed compile is running still earns its own attempt.
            FailedBuildInputs = BuildInputsToken(
                modulesHash, result?.CompiledSources ?? def.CurrentSourceVersions),
            // Clear the consumed release-requester on failure too — the failed request is
            // done; a fresh request must re-stamp it.
            RequestedReleaseBy = null,
            // Spent, like the requester — see ApplyCompileSuccess (#2818).
            RequestedReleaseForce = false,
            // The adopted build this request belonged to is gone (CompiledSources is cleared
            // above), so the request goes with it — see ApplyCompileSuccess (#1834).
            RequestedSourceStampAt = null
        };

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
        // specific compile-<ts> paths (the prod storm). Reading/subscribing a node that
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
                    NodeType = GraphNodeTypeNames.Activity,
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
        //
        // 🚨 It does NOT re-mint LastCompileStartedAt (#2895). HandleDispatchCompile's
        // Pending → Compiling compare-and-swap — the ONLY caller of RunCompile, and the
        // transition this field's contract names ("stamped on the Pending → Compiling
        // transition", DynamicTypePreWarmer.SourcesMovedDuringCompile) — stamped it moments
        // ago. Re-stamping here moved the recorded start forward past the activity-node
        // create, a step bounded at TEN SECONDS below: every source written inside that
        // window then read as older than the compile, which is exactly the torn-snapshot
        // evidence SourcesMovedDuringCompile exists to surface, silently discarded. And
        // because a fresh timestamp can never equal the persisted one, it also denied this
        // write UpdateOwn's no-op gate on the runs where the activity path did not change,
        // minting a node version — and a fan-out to every reader — for no observable fact.
        // A caller that reaches RunCompile without that CAS (none today; the method is
        // public) still gets a start stamp: the fallback below mints one whenever the node
        // is not already carrying a Compiling claim's own.
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
                                    LastCompileStartedAt = StartOfThisCompileClaim(def),
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
                    //
                    // 🚨 …EXCEPT an AVAILABILITY non-verdict, which is not a compile failure at all
                    // and must never consume the park budget (#1701). `SourceDiscoveryUnavailable`
                    // and `AddressRecycling` both mean the compile never REACHED a verdict — the
                    // source set could not be established, or a mesh address the compile reads was
                    // recycling for the reader's whole budget. `ApplyCompileFailure` already stamps
                    // those as `CompilationStatus.Unavailable` rather than `Error`, precisely
                    // because they say nothing about the code. Feeding them to `OnCompileFailed`
                    // threw that distinction away one layer down: the registry only receives a
                    // string plus `deterministic:false`, so three recycling reads in a row parked
                    // the type — and a parked type is then re-settled by the watcher's park
                    // short-circuit as `CompilationStatus.Error`, the "compile=FAILED(Error)"
                    // verdict #1701 reports for all 33 types of a package whose ROOT was merely
                    // recycling. Worse, the only automatic un-park is "the SOURCES changed", and a
                    // recycle changes no source — so the park was permanent until someone pressed
                    // Compile. Not counting it is the fix: the type stays `Unavailable`, which the
                    // failed-verdict re-drive treats as stale and retries under its own bounds.
                    if (parkRegistry is not null)
                    {
                        if (ok)
                            parkRegistry.OnCompileSucceeded(hubPath);
                        else if (IsAvailabilityNonVerdict(outcome.Error))
                        {
                            // 🚨 ATTRIBUTION for the emit-dead case (#890). One poisoned process
                            // reports as up to ten unrelated test names — 23 % of all distinct
                            // failing test names in the 08-22→08-29 sweep — and each occurrence has
                            // cost a fresh, always-identical misdiagnosis. The canary already KNOWS
                            // the process is the broken thing at the first throw; nothing said so in
                            // a form the next reader could act on. This line is that statement, and
                            // it is deliberately louder than its sibling: an Information line about
                            // one node does not describe a process that can no longer compile
                            // anything, and every compile after it in this process will fail the
                            // same way for the same reason.
                            if (EmitPipeline.IsProcessEmitFailure(
                                    outcome.Error!.Data[EmitPipeline.EmitCanaryDataKey]))
                                logger?.LogError(
                                    "PROCESS CANNOT EMIT (#890) — the compile of {HubPath} aborted inside "
                                    + "Roslyn's emit, and the canary's control compilation (trivial, freshly "
                                    + "parsed, known-good) could not emit either. This says NOTHING about that "
                                    + "type's code: it is left at Unavailable, no verdict is recorded and the "
                                    + "park budget is untouched. 🚨 EVERY LATER COMPILE IN THIS PROCESS WILL "
                                    + "FAIL THE SAME WAY — attribute the failures that follow to this line, not "
                                    + "to the change under test, and do not re-diagnose them one by one. "
                                    + "Verdict: {Verdict}",
                                    hubPath, outcome.Error.Data[EmitPipeline.EmitCanaryDataKey]);
                            else
                                logger?.LogInformation(
                                    "Compile for {HubPath} reached NO VERDICT ({Type}) — an availability fact, "
                                    + "not a compile failure: it does not count towards the park budget and the "
                                    + "type is left at Unavailable for the automatic re-drive to retry. {Error}",
                                    hubPath, outcome.Error!.GetType().Name, outcome.Error.Message);
                        }
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
                            var pendingDef = outcome.PendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
                            var requestedBy = pendingDef?.RequestedReleaseBy;
                            // 🅿️ Capture the exact source snapshot this compile consumed (from the
                            // failed result; fall back to the node's live CurrentSourceVersions) and
                            // park it WITH the failure. A later edit that changes the snapshot then
                            // auto-un-parks + recompiles (ShouldRetryForSourceChange in the sources
                            // watcher) — "retry only if the sources changed".
                            var failedSources = outcome.Result?.CompiledSources ?? pendingDef?.CurrentSourceVersions;
                            parkRegistry.OnCompileFailed(
                                hub, hubPath, parkError, deterministic, requestedBy, failedSources, logger);
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
                        ? NodeTypeBuildState.TryCreateReleaseNode(
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
                        // 🚨 THE POST-CONDITION (#781), checked where the compile SETTLES — the one
                        // moment at which "does a release name the build this node is about to
                        // advertise?" is answerable from facts all in hand. A consumed request whose
                        // release did not land leaves LatestReleasePath on the PREVIOUS build and
                        // nothing ever revisits it: status Ok, sources current, an assembly built, a
                        // release path present — and every instance binding yesterday's bytes. The
                        // remedy re-cuts from the bytes this compile just produced (no recompile,
                        // under System) and is loud either way. Also totality-safe: exactly one
                        // emission, never a fault.
                        .SelectMany(newReleasePath => ok
                            ? ReleasePostCondition.Restore(
                                hub, hubPath, outcome.Result!, outcome.PendingNode,
                                resolvedActivityPath, newReleasePath, logger)
                            : Observable.Return<(string? ReleasePath, string? Diagnosis)>(
                                (newReleasePath, null)))
                        .Subscribe(settle =>
                    {
                    var newReleasePath = settle.ReleasePath;
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
                    {
                        // 🚨 Say WHICH failure this is. "Roslyn failed" in front of a source
                        // snapshot that never answered is the log-line version of the #1218 bug:
                        // Roslyn was never invoked, so a reader (or an operator reading a stalled
                        // rollout's activity log) must not be told it rejected anything.
                        var failureLead = outcome.Error switch
                        {
                            SourceDiscoveryUnavailableException =>
                                "Compile NOT ATTEMPTED — source set could not be established",
                            AddressRecyclingException =>
                                "Compile NOT SETTLED — a mesh address it reads was recycling "
                                + "for the reader's whole budget",
                            _ => "Roslyn failed"
                        };
                        activityMessages.Add(new LogMessage(
                            $"{failureLead}: {outcome.Error?.Message ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs ? string.Join("; ", errs.Select(m => m.Message)) : "Compilation produced no assembly")}",
                            LogLevel.Error));
                        // 🚨 Non-Roslyn abort (an infrastructure exception escaping the compile
                        // pipeline — NOT a CompilationException, whose message already carries the
                        // full diagnostics): record the exception TYPE + STACK on the activity log.
                        // Reducing the fault to `.Message` everywhere made the recurring CI
                        // first-compile "Object reference not set to an instance of an object"
                        // (#612, PR-884 run 31153582625) UNDIAGNOSABLE — no sink anywhere carried
                        // the throw site. The activity log is THE official diagnosis surface;
                        // faults must reach it whole (wedges-to-zero).
                        if (outcome.Error is not null and not CompilationException)
                            activityMessages.Add(new LogMessage(
                                $"Compile aborted by {outcome.Error.GetType().FullName}:\n{outcome.Error}",
                                LogLevel.Error));
                    }
                    if (newReleasePath is not null)
                        activityMessages.Add(new LogMessage(
                            $"Release created: {newReleasePath}", LogLevel.Information));
                    // The post-condition's verdict belongs on the OFFICIAL diagnosis surface, not
                    // only in a log sink — a stale release is invisible everywhere else (#781).
                    if (settle.Diagnosis is { } diagnosis)
                        activityMessages.Add(new LogMessage(diagnosis,
                            newReleasePath is null ? LogLevel.Error : LogLevel.Warning));
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

                        // The stamp field-sets live in ApplyCompileSuccess / ApplyCompileFailure —
                        // pure, shared with the initial-bake batch driver (issue #1207) so both
                        // write-back paths stamp IDENTICAL state.
                        if (outcome.Error is null && !string.IsNullOrEmpty(outcome.Result?.AssemblyLocation))
                        {
                            logger?.LogInformation("Compile success for {HubPath} → {Assembly}",
                                hubPath, outcome.Result!.AssemblyLocation);
                            return curr with
                            {
                                Content = ApplyCompileSuccess(
                                    def, outcome.Result, curr.Version, resolvedActivityPath, newReleasePath,
                                    hub.ServiceProvider.GetService<InstalledModulesFingerprint>()?.Hash)
                            };
                        }

                        // Pass the exception OBJECT so the stack reaches the log sink — message-only
                        // logging is what left the recurring CI compile-NRE undiagnosable.
                        logger?.LogWarning(outcome.Error,
                            "Compile failure for {HubPath}: {Error}", hubPath,
                            SummarizeCompileError(outcome.Result, outcome.Error));
                        return curr with
                        {
                            Content = ApplyCompileFailure(
                                def, outcome.Result, outcome.Error, resolvedActivityPath,
                                hub.ServiceProvider.GetService<InstalledModulesFingerprint>()?.Hash)
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
