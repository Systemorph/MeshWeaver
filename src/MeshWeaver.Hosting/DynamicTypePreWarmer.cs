using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>Terminal outcome of pre-warming one dynamic NodeType's hub.</summary>
public enum PreWarmStatus
{
    /// <summary>The NodeType reached a usable compiled build.</summary>
    Compiled,
    /// <summary>
    /// NOT ATTEMPTED because it did not need to be: the shared assembly store already holds this
    /// NodeType's bytes for the LIVE framework (<see cref="BakeState.Baked"/>), so there is nothing
    /// to compile. This is what makes the sweep restartable — an interrupted or partial bake resumes
    /// from the share instead of starting over, and a second pod finds the first pod's work.
    /// </summary>
    AlreadyBaked,
    /// <summary>The NodeType's compile settled at Error (its diagnostics are already logged by the compile watcher).</summary>
    CompileError,
    /// <summary>The per-type warm budget elapsed before the compile settled — Part 2 handles the late arrival.</summary>
    TimedOut,
    /// <summary>
    /// NOT ATTEMPTED: a NodeType this one draws sources from did not reach a usable build, so this
    /// type cannot build either — its assembly would be missing exactly the sources the upstream
    /// owns. <see cref="PreWarmOutcome.Detail"/> names the blocking type. Skipping is the graceful
    /// outcome: warming it anyway burns the whole per-type budget on a guaranteed failure, and
    /// doing that for a fan-out of dependents is how one broken type used to stall a whole sweep.
    /// </summary>
    UpstreamFailed,
    /// <summary>
    /// NOT ATTEMPTED, and NOT a verdict: a NodeType this one draws sources from was itself never
    /// evaluated (it timed out, or ITS upstream did). The sweep therefore knows nothing about this
    /// type either — "I don't know" propagates as "I don't know".
    ///
    /// <para>🚨 This member exists because the alternative silently freezes the fleet. A cross-silo
    /// <c>SubscribeRequest</c> timeout on a shared upstream (core #694) is not evidence that anything
    /// is broken, and <see cref="NodeTypeBakeGateState"/> correctly refuses to gate on a direct
    /// <see cref="TimedOut"/>. But before this status existed, that same unevaluated upstream turned
    /// every previously-healthy DEPENDENT into <see cref="UpstreamFailed"/> — which DOES gate. So the
    /// leniency stopped at depth 1 and the false regression simply reappeared one hop downstream,
    /// re-creating the 2026-08-02 memex-cloud stall through the back door.</para>
    ///
    /// <para>Deliberately distinct from <see cref="UpstreamFailed"/>: a dependent of a genuinely
    /// broken (<see cref="CompileError"/>) upstream still gates, because that upstream IS a verdict.
    /// Only "no answer" propagates as "no answer".</para>
    /// </summary>
    UpstreamUnevaluated,
    /// <summary>
    /// The compile settled at Error AND the type's declared source queries currently match ZERO
    /// Code nodes (<see cref="NodeTypeDefinition.CurrentSourceVersions"/> is EXPLICITLY empty) —
    /// the sources were deleted or moved out from under the type. This is a CONTENT verdict, not
    /// an image verdict: which nodes a mesh query matches is a property of the mesh, not of the
    /// framework being rolled out, so no image caused it and no rollout can fix it.
    ///
    /// <para>🚨 This member exists because on 2026-08-10 four such types (KmuBasics/* — their
    /// Source subtrees removed when the course was re-installed under a new id, the type nodes
    /// left behind) were counted as image regressions and stalled memex-cloud's self-update for a
    /// day, across two successive images. A failure the image cannot influence must never hold
    /// the image out of rotation — the same deploy-freeze rule as
    /// <see cref="PreWarmOutcome.WasHealthyBeforeBake"/>, arriving through content deletion
    /// instead of an abandoned Error record.</para>
    ///
    /// <para>Deliberately narrow: only an EXPLICITLY empty snapshot reclassifies. A null snapshot
    /// means the sources watcher never seeded, so the sweep does not actually know the sources are
    /// gone — that stays <see cref="CompileError"/>, because a real regression must not hide
    /// behind an unseeded snapshot.</para>
    /// </summary>
    NoSources,
    /// <summary>
    /// NOT ATTEMPTED, and content-broken one hop up: a NodeType this one draws sources from is
    /// <see cref="NoSources"/>-broken, so this type cannot build either — for the same
    /// content-not-image reason, which must propagate AS ITSELF rather than as a gating
    /// <see cref="UpstreamFailed"/>. This is the same depth-1 hole
    /// <see cref="UpstreamUnevaluated"/> closes for timeouts: leniency on the direct outcome is
    /// worth nothing if the identical condition gates through the dependents.
    /// </summary>
    UpstreamContentBroken,
    /// <summary>The warm subscription faulted (best-effort — the lazy path still works).</summary>
    Faulted
}

/// <summary>One dynamic NodeType's pre-warm result.</summary>
public record PreWarmOutcome(string TypePath, PreWarmStatus Status, string? Detail = null)
{
    /// <summary>
    /// The type is usable on this framework now — whether this sweep compiled it
    /// (<see cref="PreWarmStatus.Compiled"/>) or found it already on the shared store
    /// (<see cref="PreWarmStatus.AlreadyBaked"/>).
    ///
    /// <para>Callers deciding "is this mesh ready to serve?" want THIS, not an equality check
    /// against <see cref="PreWarmStatus.Compiled"/> — a warm share is a success, not a miss, and a
    /// gate that insisted on a fresh compile would fail every pod that inherited a good cache.</para>
    /// </summary>
    public bool ReachedUsableBuild =>
        Status is PreWarmStatus.Compiled or PreWarmStatus.AlreadyBaked;

    /// <summary>
    /// Whether this NodeType was WORKING before the sweep started — the regression baseline, taken
    /// from <see cref="NodeTypeBakeEntry.WasHealthy"/>.
    ///
    /// <para>Only a failure on a type that was previously healthy is a REGRESSION, and only a
    /// regression may hold a pod out of rotation. A type that was already sitting at
    /// <c>CompilationStatus.Error</c> before this image failing again is not new damage — gating on it
    /// would let one abandoned NodeType block every future deploy.</para>
    /// </summary>
    public bool WasHealthyBeforeBake { get; init; } = true;
}

/// <summary>
/// Best-effort, background PRE-WARM of dynamic NodeType hubs at startup (Part 1 of the
/// fresh-pod compile-race hardening).
///
/// <para><b>The window it shrinks.</b> On a fresh pod — every image roll / self-update
/// spins one up — the platform's <see cref="NodeTypeCompilationHelpers.FrameworkVersion"/>
/// (Graph's MVID) changes, so every dynamic NodeType's cached assembly is ABI-stale and
/// must recompile. Nothing drives that until the FIRST user request activates a per-node
/// hub — so the unlucky first visitor of each type waits out the cold Roslyn compile.
/// This warmer front-loads those compiles: it activates each dynamic NodeType's own hub
/// (which fires the framework-stale / first-build kickoff → Roslyn), so the compiles run
/// proactively rather than on a user's critical path.</para>
///
/// <para><b>Best-effort — never blocks, never wedges.</b> It runs on a background
/// subscription after the silo is up (<c>ApplicationStarted</c>), bounds concurrency with
/// a reactive <c>Merge(maxConcurrency)</c> (Roslyn itself is already serialized on the
/// Compile IoPool, so this only caps how many activations are in flight), and gives each
/// type a generous per-type budget. A type that fails to compile, times out, or faults is
/// LOGGED and skipped — it does not block the others and it is NOT gated on. If any type
/// is still compiling when a user arrives, Part 2
/// (<see cref="NodeTypeEnrichmentHelpers.WaitForCompileSettled"/>) makes that activation
/// WAIT for the compile instead of faulting — so the warmer is a latency optimisation, not
/// a correctness dependency. It deliberately does NOT gate the readiness probe: a slow or
/// broken compile must never keep a pod out of rotation (Part 2 already covers late
/// arrivals), and a readiness gate would risk the exact "slow pod startup" it is meant to
/// avoid.</para>
/// </summary>
public static class DynamicTypePreWarmer
{
    /// <summary>
    /// Pause between types, so a long warm sweep stays a background trickle rather than a queue of
    /// back-to-back cold activations. Cheap insurance: the sweep is a latency optimisation with no
    /// deadline, so there is no reason for it to ever look like load.
    ///
    /// <para>🚨 Why there is no concurrency knob beside it (there was one, defaulting to 4, and 4 is
    /// measurably harmful): on 2026-07-28 04:05 four compiles were triggered on memex in quick
    /// succession — nothing to do with this warmer, but the identical load shape — and within
    /// minutes SIX plugin roots (Claims, Edu, Underwriting, Chess, Publish, Training) fell to the
    /// "did not settle" overlay and needed a scale-to-zero to recover. The compiles already
    /// serialize on the Compile IoPool, so concurrency bought nothing except simultaneous cold
    /// ACTIVATIONS — the expensive part (109ms/0ms discovery for the same NodeType shape on a fresh
    /// mesh, versus 45.20s on memex — issue #686). A dependency ORDER cannot be honoured while its
    /// members run in parallel either, so the sweep is now strictly sequential and the knob is
    /// gone rather than left lying about what it does.</para>
    /// </summary>
    /// <remarks>
    /// 🚨 This is the SERVING-pod default, and on a big mesh it — not Roslyn — is the bake's
    /// wall-clock. Measured on memex-cloud 2026-08-11 (healthy single silo): 162 types compiled in
    /// a 7-minute sweep of which the compiles themselves summed to 51 SECONDS (p50 0.2s, p90 0.5s,
    /// max 3.4s) — the 2s trickle was ~5.4 of the 7 minutes. The trickle exists to protect a pod
    /// that is SERVING while it warms; an INITIAL BAKE on a readiness-gated pod serves nobody, so
    /// the hosted service passes <see cref="TimeSpan.Zero"/> there (see
    /// <c>DynamicTypePreWarmerHostedService</c> — the gated fast path, overridable via
    /// <c>PreWarm:BetweenTypes</c>). That distinction is what turns "the bake takes 20 minutes"
    /// into "the bake takes ~2".
    /// </remarks>
    public static readonly TimeSpan BetweenTypes = TimeSpan.FromSeconds(2);

    /// <summary>Per-type warm budget — generous, because a cold Roslyn compile queued behind
    /// others on the Compile IoPool can legitimately take a while. On elapse we log
    /// <see cref="PreWarmStatus.TimedOut"/> and move on (Part 2 handles the eventual arrival).</summary>
    public static readonly TimeSpan DefaultPerTypeBudget = TimeSpan.FromMinutes(5);

    /// <summary>Budget for the one-shot enumeration query of dynamic NodeTypes.</summary>
    private static readonly TimeSpan EnumerationBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enumerate the dynamic NodeTypes on the mesh and activate each one's hub, waiting
    /// (best-effort, bounded) for its compile to settle. Emits one
    /// <see cref="PreWarmOutcome"/> per type and completes when all have settled or timed
    /// out. Never throws — enumeration/activation failures fold into an outcome or an empty
    /// completion so a subscriber can simply log the summary.
    ///
    /// <para>Types are warmed STRICTLY SEQUENTIALLY in
    /// <see cref="NodeTypeDependencyGraph.TopologicalOrder(IReadOnlyDictionary{string, ImmutableHashSet{string}}, out ImmutableList{string})"/>
    /// order — dependencies before dependents. There is deliberately no concurrency knob: a
    /// dependency order cannot be honoured while its members run in parallel, and concurrent cold
    /// activations are what produced the 60s <c>SubscribeRequest</c> timeouts this warmer exists to
    /// prevent.</para>
    /// </summary>
    public static IObservable<PreWarmOutcome> WarmDynamicTypes(
        IMessageHub mesh,
        ILogger? logger = null,
        TimeSpan? perTypeBudget = null,
        TimeSpan? betweenTypes = null,
        bool batchBake = false)
    {
        var budget = perTypeBudget ?? DefaultPerTypeBudget;
        var pacing = betweenTypes ?? BetweenTypes;
        var meshService = mesh.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
        {
            logger?.LogDebug("DynamicTypePreWarmer: no IMeshService registered — nothing to warm");
            return Observable.Empty<PreWarmOutcome>();
        }
        var accessService = mesh.ServiceProvider.GetService<AccessService>();
        var workspace = mesh.GetWorkspace();

        // 🚨 System-scoped: enumerating + activating dynamic NodeType defs across EVERY
        // partition is infrastructure, not a user-attributable read (mirrors the enrichment
        // probe + activation reads). Observable.Using holds the scope across the live
        // subscription, not just the synchronous build.
        return Observable.Using(
            () => AccessContextScope.AsSystem(accessService),
            _ => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{MeshNode.NodeTypePath}"))
                .Take(1)
                .Timeout(EnumerationBudget)
                .SelectMany(change =>
                {
                    // The full nodes (not just their definitions): the batch driver feeds the
                    // compiler the enumerated MeshNode directly — no re-fetch, no activation.
                    var nodes = change.Items
                        .Where(n => !string.IsNullOrEmpty(n.Path)
                            && n.State == MeshNodeState.Active
                            && n.Content is NodeTypeDefinition d
                            // Only DYNAMIC types have source to compile. Static/framework
                            // NodeTypes ship their assembly with the process — nothing to warm.
                            && HasCompilableSource(d))
                        .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            g => g.Key,
                            g => g.First(),
                            StringComparer.OrdinalIgnoreCase);
                    var definitions = nodes.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (NodeTypeDefinition?)kvp.Value.Content,
                        StringComparer.OrdinalIgnoreCase);

                    // 🚨 ASK THE SHARE WHAT IS ACTUALLY THERE, before deciding what to build.
                    //
                    // The obvious shortcut — trust each NodeType's own record (HasUsableBuild) — is
                    // wrong in the one case that matters operationally: that check is deliberately a
                    // pure record read ("no store probe, no File.Exists"), so when the assembly-cache
                    // volume is cleared, remounted, or restored from a stale snapshot, every type
                    // still claims a usable build while its bytes are gone. Nothing re-drives a
                    // compile and the miss surfaces later, one instance at a time, at activation.
                    //
                    // Probing the STORE makes this level-triggered on reality rather than on history,
                    // which buys three properties at once: a cleared cache re-bakes by itself, an
                    // interrupted bake RESUMES (what already landed comes back Baked), and a second
                    // pod inherits the first pod's work instead of repeating it.
                    var store = ResolveAssemblyStore(mesh);
                    return NodeTypeBakeStatus
                        .Probe(definitions, store, logger: logger)
                        .SelectMany(report => BakeOrFollow(
                            mesh, workspace, accessService, definitions, nodes, store, report,
                            budget, pacing, batchBake, logger));
                })
                .Catch<PreWarmOutcome, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "DynamicTypePreWarmer: enumeration of dynamic NodeTypes failed — pre-warm skipped (Part 2 still compiles lazily on first access)");
                    return Observable.Empty<PreWarmOutcome>();
                }));
    }

    /// <summary>
    /// The shared assembly store this mesh compiles into, or <see cref="NullAssemblyStore"/> when the
    /// host registered none. A null store reports every lookup as a miss, so every type is treated as
    /// needing a bake — which matches what a storeless host does anyway (it recompiles on every
    /// activation), so the sweep degrades to its previous behaviour rather than misreporting.
    /// </summary>
    private static IAssemblyStore ResolveAssemblyStore(IMessageHub mesh) =>
        mesh.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;

    /// <summary>How often a FOLLOWING pod re-checks the share (and re-attempts the lease).</summary>
    public static readonly TimeSpan FollowPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 🚨 ONE POD BAKES; the rest WATCH THE SHARE FILL.
    ///
    /// <para>The cache is shared but the decision to rebuild is per-process, so without this every
    /// replica on a new image independently finds the same framework-stale cache and starts the same
    /// sweep into the same volume. That is not just duplicated work — it is concurrent cold compiles
    /// of the SAME NodeType, which is precisely the storm the sequential ordered sweep exists to
    /// prevent (four concurrent compiles on memex, 2026-07-28 04:05, dropped six plugin roots to the
    /// "did not settle" overlay). A rollout with <c>maxSurge</c>, or any <c>replicas &gt; 1</c>, hits
    /// this by default.</para>
    ///
    /// <para><b>The follower is not passive.</b> Each poll it re-probes the share AND re-attempts the
    /// lease. So when the share completes it finishes immediately, and when the baker DIES its lease
    /// goes stale and the next poll takes the bake over. There is no state that requires a human to
    /// notice — the same level-triggered rule as the rest of this design.</para>
    ///
    /// <para>With no <see cref="BakeCoordination"/> registered there is no fleet to coordinate with
    /// (monolith, tests, dev), and the caller simply bakes.</para>
    /// </summary>
    private static IObservable<PreWarmOutcome> BakeOrFollow(
        IMessageHub mesh,
        IWorkspace workspace,
        AccessService? accessService,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IReadOnlyDictionary<string, MeshNode> nodes,
        IAssemblyStore store,
        NodeTypeBakeReport report,
        TimeSpan budget,
        TimeSpan pacing,
        bool batchBake,
        ILogger? logger)
    {
        // Nothing outstanding — no lease needed, and nothing for a follower to wait for.
        if (report.IsComplete)
            return WarmPending(mesh, workspace, accessService, definitions, nodes, report, budget, pacing, batchBake, logger);

        var coordination = mesh.ServiceProvider.GetService<BakeCoordination>();
        if (coordination is null)
            return WarmPending(mesh, workspace, accessService, definitions, nodes, report, budget, pacing, batchBake, logger);

        var lease = NodeTypeBakeLease.TryAcquire(
            coordination.LeaseDirectory, report.FrameworkVersion, Environment.MachineName, logger);

        if (lease is not null)
            // Observable.Using holds the lease for the LIFETIME of the bake and releases it on
            // completion, error or unsubscribe — a lease that outlived its sweep would lock the
            // fleet out until it went stale.
            return Observable.Using(
                () => lease,
                _ => WarmPending(mesh, workspace, accessService, definitions, nodes, report, budget, pacing, batchBake, logger));

        logger?.LogInformation(
            "DynamicTypePreWarmer: another pod holds the bake lease — following the shared assembly "
            + "store instead of compiling ({Pending} type(s) outstanding). {Report}",
            report.Pending.Count, report.Summary);

        // Re-enter after a pause: re-probe (has the baker finished?) and re-attempt the lease (has
        // the baker died?). Recursion, not a loop, because each round's decision depends on the fresh
        // probe — bounded in practice by the poll interval against a bake measured in hours.
        return Observable
            .Timer(FollowPollInterval)
            .SelectMany(_ => NodeTypeBakeStatus.Probe(definitions, store, logger: logger))
            .SelectMany(fresh => BakeOrFollow(
                mesh, workspace, accessService, definitions, nodes, store, fresh, budget, pacing, batchBake, logger));
    }

    /// <summary>
    /// Warm every NodeType the <paramref name="report"/> says still needs building, dependencies
    /// first, one at a time. Types the share already holds are reported
    /// <see cref="PreWarmStatus.AlreadyBaked"/> and never activated.
    /// </summary>
    private static IObservable<PreWarmOutcome> WarmPending(
        IMessageHub mesh,
        IWorkspace workspace,
        AccessService? accessService,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IReadOnlyDictionary<string, MeshNode> nodes,
        NodeTypeBakeReport report,
        TimeSpan budget,
        TimeSpan pacing,
        bool batchBake,
        ILogger? logger)
    {
        var baked = report.Entries
            .Where(e => !e.NeedsBake)
            .Select(e => e.TypePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var bytesMissing = report.BytesMissing
            .Select(e => e.TypePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The regression baseline, captured BEFORE anything is rebuilt: which types were working on
        // the way in. A type missing from this set was already broken, so its failure is pre-existing
        // damage rather than something this image caused — see PreWarmOutcome.WasHealthyBeforeBake.
        var healthyBefore = report.Entries
            .Where(e => e.WasHealthy)
            .Select(e => e.TypePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Say it out loud when the SHARE changed rather than the code: a record that claims a
        // live-framework build with no bytes behind it means the cache was cleared, remounted or
        // restored — a very different diagnosis from "these types are stale", and one an operator
        // reading the log at 3am should not have to infer from a recompile count.
        if (!report.BytesMissing.IsEmpty)
            logger?.LogWarning(
                "DynamicTypePreWarmer: {Count} NodeType(s) claim a usable build for the live "
                + "framework but the assembly store has NO bytes for them — the shared cache was "
                + "cleared or replaced. Rebuilding: {Types}",
                report.BytesMissing.Count,
                string.Join(", ", report.BytesMissing.Select(e => e.TypePath)));

        // 🚨 DEPENDENCIES FIRST, ONE AT A TIME. A NodeType can compile ANOTHER type's
        // Code into its own assembly (Store/Plugin declares shared=@Store/Coupon/Source,
        // @Store/Order/Source, @Store/BillingProfile/Source), and every plugin ROOT is
        // itself an instance of such a type — so warming them in arbitrary order makes
        // dependents wait on dependencies that have not been built yet, and they blow
        // the 60s activation budget:
        //     [STALE-CALLBACK] … SubscribeRequest@Store/Plugin(45028ms)
        //     TimeoutException: No response received … within 00:01:00 → Store/Plugin
        // The order is computed from the DECLARED sources, so it stays correct as
        // plugins add cross-type sources without anyone maintaining a list.
        //
        // The order is computed over EVERY type, not just the pending ones: an
        // already-baked dependency still has to be positioned before its dependents so
        // the blocked-by walk below sees it as satisfied rather than as absent.
        var dependencies = NodeTypeDependencyGraph.Build(definitions);
        var order = NodeTypeDependencyGraph.TopologicalOrder(dependencies, out var cyclic);
        var pending = order.Where(p => !baked.Contains(p)).ToList();

        // Initial-bake fast path (issue #1207): drive the compiler DIRECTLY for every pending
        // type — batched source discovery, no per-type hub activation, no compile-watcher
        // settle, no cross-silo hop (the 2026-08-10/11 20-min/5-h bakes were per-type
        // activation round-trips eating 5-minute timeouts on a wedged peer, not compilation).
        // Only taken when requested (the pod's bake gates readiness, or PreWarm:BatchBake) AND
        // the compiler + mesh service are actually on this host.
        var batchCompiler = batchBake
            ? mesh.ServiceProvider.GetService<IMeshNodeCompilationService>()
            : null;
        var batchMeshService = batchBake ? mesh.ServiceProvider.GetService<IMeshService>() : null;
        var useBatch = batchBake && pending.Count > 0
            && batchCompiler is not null && batchMeshService is not null;
        if (batchBake && !useBatch && pending.Count > 0)
            logger?.LogWarning(
                "DynamicTypePreWarmer: batch bake requested but "
                + "{Missing} is not registered — falling back to the activation-driven sweep",
                batchCompiler is null ? "IMeshNodeCompilationService" : "IMeshService");

        logger?.LogInformation(
            "DynamicTypePreWarmer: {Pending} of {Total} dynamic NodeType(s) need building "
            + "(sequential, dependency order, {Mode}, perTypeBudget={Budget}) — {Baked} already on the share. "
            + "{Report}. Building: {Order}",
            pending.Count, order.Count, useBatch ? "batch direct-compile" : "activation-driven",
            budget, baked.Count, report.Summary,
            pending.Count == 0 ? "(nothing)" : string.Join(" → ", pending));
        if (!cyclic.IsEmpty)
            logger?.LogWarning(
                "DynamicTypePreWarmer: {Count} NodeType(s) form a source dependency CYCLE and "
                + "cannot be ordered — warmed last, in path order: {Cyclic}",
                cyclic.Count, string.Join(", ", cyclic));

        // FAIL GRACEFULLY DOWNSTREAM. A type whose upstream did not reach a usable build
        // cannot build either, so it is not attempted: it is reported naming the blocker,
        // and joins a skip set so ITS dependents are skipped too — the propagation is
        // transitive purely because we walk in topological order. Without this, one broken
        // type costs every dependent a full per-type budget of waiting for something that
        // cannot succeed.
        //
        // 🚨 TWO skip sets, not one, because "it broke" and "I never found out" must NOT
        // propagate as the same thing. A readiness gate may stall a rollout on the first
        // and must never stall on the second:
        //
        //   verdictFailed — the sweep got an answer and the answer was bad (CompileError,
        //     Faulted, or a dependent of one of those). Dependents get UpstreamFailed,
        //     which GATES.
        //   unevaluated   — the sweep got no answer at all (TimedOut, or a dependent of
        //     something unevaluated). Dependents get UpstreamUnevaluated, which does NOT
        //     gate. A cross-silo SubscribeRequest timeout on a shared upstream (core #694)
        //     is not evidence that anything is broken, so it must not become one downstream.
        //
        // Collapsing these back into one set is exactly how the 2026-08-02 memex-cloud
        // stall would return: the direct-timeout leniency in NodeTypeBakeGateState would
        // still hold, and the false regression would simply reappear one hop downstream.
        //
        // The sets are mutated inside a Concat, which subscribes strictly one at a time,
        // so there is no concurrent access — and each step is wrapped in Defer so it
        // reads them as they stand WHEN ITS TURN COMES, not when the chain was built.
        var verdictFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unevaluated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Third cascade, same reasoning one axis over: a type whose sources were DELETED
        // (PreWarmStatus.NoSources) is a verdict — but a CONTENT verdict, and its dependents must
        // inherit "content-broken", not the gating UpstreamFailed. See PreWarmStatus.NoSources.
        var contentBroken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Concat, never Merge: the next type is SUBSCRIBED only after the previous one
        // has completed, so "dependencies first" is structural rather than a convention.
        // The gap between types keeps the sweep a background trickle rather than a queue
        // of back-to-back cold activations; it costs nothing, since the warm-up has no
        // deadline and Part 2 compiles lazily regardless. (The batch path skips the gap:
        // it activates nothing, so there is no burst to spread out — and a gated pod
        // serves nobody, so there is nothing to be gentle to.)
        IObservable<PreWarmOutcome> Sweep(
            ImmutableDictionary<string, IReadOnlyList<MeshNode>>? batchSources)
            => order
                .Select((p, i) => Observable.Defer(() =>
                {
                    // Already on the share: report it and move on WITHOUT activating the hub.
                    // Skipping the activation is the entire saving — it is what turns a
                    // re-run, a second replica, or a resumed bake from hours of Roslyn into
                    // a directory listing.
                    if (baked.Contains(p))
                        return Observable.Return(new PreWarmOutcome(
                            p, PreWarmStatus.AlreadyBaked, "assembly store already holds this build"));

                    // A real verdict upstream wins over a merely-unevaluated one: if ANY dependency
                    // actually failed to compile, this type is genuinely blocked and must gate,
                    // regardless of some other dependency having also timed out. A content-broken
                    // upstream sits between the two: it IS a verdict (so it beats "I don't know"),
                    // but a content verdict — its dependents inherit content-broken, never gating.
                    var blocker = NodeTypeDependencyGraph.FirstBlockedBy(p, dependencies, verdictFailed);
                    var contentBlocker = blocker is null
                        ? NodeTypeDependencyGraph.FirstBlockedBy(p, dependencies, contentBroken)
                        : null;
                    var unevaluatedBlocker = blocker is null && contentBlocker is null
                        ? NodeTypeDependencyGraph.FirstBlockedBy(p, dependencies, unevaluated)
                        : null;
                    var missingBytes = bytesMissing.Contains(p);
                    if (blocker is not null || contentBlocker is not null || unevaluatedBlocker is not null)
                    {
                        var named = blocker ?? contentBlocker ?? unevaluatedBlocker!;
                        var outcome = blocker is not null ? PreWarmStatus.UpstreamFailed
                            : contentBlocker is not null ? PreWarmStatus.UpstreamContentBroken
                            : PreWarmStatus.UpstreamUnevaluated;
                        (blocker is not null ? verdictFailed
                            : contentBlocker is not null ? contentBroken
                            : unevaluated).Add(p);
                        // {Outcome} carries WHICH cascade this is as a queryable token — the two
                        // differ in whether they may stall a rollout, so a log that blurred them
                        // would hide the thing an operator most needs to know.
                        logger?.LogWarning(
                            "DynamicTypePreWarmer: skipping {TypePath} — its dependency {Blocker} "
                            + "did not yield a usable build ({Outcome}), so it cannot compile either "
                            + "(lazy compile still applies if the upstream recovers)",
                            p, named, outcome);
                        // No pacing for a skip: it activates nothing, so there is no
                        // burst to spread out and no reason to slow the sweep down.
                        return Observable.Return(new PreWarmOutcome(p, outcome, $"blocked by {named}"));
                    }

                    // Batch mode drives the ONE compiler directly with the pre-resolved source
                    // set — no activation, no watcher settle. It also subsumes the missing-bytes
                    // case: a direct compile re-emits, re-uploads and re-stamps regardless of
                    // what the record claims, so no record surgery is needed to force it.
                    //
                    // 🚨 A type whose BYTES are gone cannot be warmed by activation alone.
                    // WarmOne waits for HasUsableBuild, which is a RECORD check that deliberately
                    // ignores status — and in this state the record is pristine. Activating the hub
                    // would therefore return "Compiled" instantly without rebuilding anything, and
                    // the sweep would report a green bake over a share that is still empty. The
                    // rebuild has to be DRIVEN.
                    var warm = batchSources is not null && nodes.TryGetValue(p, out var typeNode)
                        ? NodeTypeBatchBake.BakeOne(
                            mesh, typeNode,
                            batchSources.TryGetValue(p, out var srcs) ? srcs : Array.Empty<MeshNode>(),
                            budget, logger)
                        : missingBytes
                            ? RebuildMissingBytes(workspace, accessService, p, budget, logger)
                            : WarmOne(workspace, accessService, p, budget, logger);

                    return warm
                        .DelaySubscription(
                            i == 0 || batchSources is not null ? TimeSpan.Zero : pacing)
                        .Do(o =>
                        {
                            // A timeout is not a verdict — route it to `unevaluated` so its
                            // dependents inherit "no answer", not "it broke". Deleted sources are
                            // a CONTENT verdict — dependents inherit content-broken, never gating.
                            // Everything else that missed a usable build (CompileError, Faulted)
                            // is an image verdict.
                            if (o.Status is PreWarmStatus.TimedOut)
                                unevaluated.Add(p);
                            else if (o.Status is PreWarmStatus.NoSources)
                                contentBroken.Add(p);
                            else if (!o.ReachedUsableBuild)
                                verdictFailed.Add(p);
                        });
                }))
                .Concat()
                // Stamp the regression baseline on every outcome so a downstream readiness gate can
                // tell a NEW failure from one that was already broken on the way in, without having
                // to re-read the report.
                .Select(o => o with { WasHealthyBeforeBake = healthyBefore.Contains(o.TypePath) });

        if (order.Count == 0)
            return Observable.Empty<PreWarmOutcome>();
        if (!useBatch)
            return Sweep(null);

        // Batch discovery first — ONE pass resolving every pending type's source queries.
        // Only a DISCOVERY failure falls back to the activation-driven sweep (before any
        // outcome was produced); a failure inside the sweep itself propagates as usual.
        //
        // 🚨 "Discovery failure" is now ASSERTED, not assumed (issue #1216). ResolveSources errors
        // when its query hit its own ceiling (possible truncation) or when a type that declares
        // source queries resolved to an empty set with nothing corroborating it. Both mean the batch
        // does not know what the sources ARE, and a compile driven from a set you did not establish
        // produces verdicts about code from evidence you do not have — on memex-cloud 2026-08-11
        // that read as "169 of 237 types are content-broken" and, on an ungated pod, would have
        // baked a fleet of empty assemblies with nothing refusing readiness. Whole-batch fallback is
        // the only safe answer: the activation-driven sweep resolves each type's sources itself.
        return NodeTypeBatchBake
            .ResolveSources(batchMeshService!, accessService, definitions, pending, logger)
            .Select(index =>
                (ImmutableDictionary<string, IReadOnlyList<MeshNode>>?)index)
            .Catch<ImmutableDictionary<string, IReadOnlyList<MeshNode>>?, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "DynamicTypePreWarmer: batched source discovery did not establish the source "
                    + "sets — abandoning the batch and falling back to the activation-driven sweep "
                    + "for ALL {Pending} pending type(s). No type is reported from this pass, so "
                    + "nothing here can be mistaken for a content or compile verdict.",
                    pending.Count);
                return Observable.Return(
                    (ImmutableDictionary<string, IReadOnlyList<MeshNode>>?)null);
            })
            .SelectMany(Sweep);
    }

    /// <summary>A NodeType has something for Roslyn to compile (so it is a dynamic type worth warming).</summary>
    private static bool HasCompilableSource(NodeTypeDefinition d) =>
        !string.IsNullOrWhiteSpace(d.Configuration)
        || !string.IsNullOrWhiteSpace(d.HubConfiguration)
        || (d.Sources is { Count: > 0 });

    /// <summary>
    /// Rebuild a NodeType whose record claims a usable build for the LIVE framework but whose bytes
    /// are no longer in the shared assembly store — a cleared, remounted or partially-restored
    /// assembly-cache volume.
    ///
    /// <para><b>Why activation is not enough.</b> Every existing kickoff is keyed on the RECORD:
    /// first-build needs null assembly fields, recovery needs <c>Compiling</c>, framework-stale needs
    /// a version mismatch, and the release watcher needs a fresh release request. In this state the
    /// record satisfies none of them — it is a clean <c>Ok</c> pointing at bytes that are gone — so
    /// activating the hub fires nothing, and <see cref="WarmOne"/>'s <c>HasUsableBuild</c> wait
    /// (which deliberately ignores status) would return <see cref="PreWarmStatus.Compiled"/> at once.
    /// The sweep would then report a green bake over an empty share, which is the one outcome a bake
    /// gate must never produce.</para>
    ///
    /// <para><b>How.</b> Flip <see cref="CompilationStatus.Pending"/> — the same lever the
    /// framework-stale kickoff and the enrichment self-heal pull — so the per-NodeType compile
    /// watcher rebuilds, then wait for a compile that is demonstrably FRESH: a settled
    /// <see cref="CompilationStatus.Ok"/> whose <see cref="NodeTypeDefinition.LastCompileSucceededAt"/>
    /// is newer than the one we observed before flipping. Matching on status alone would accept the
    /// replayed pre-existing Ok and green-light a share that never got its bytes back.</para>
    ///
    /// <para>The settle subscription is established BEFORE the flip is issued (left-to-right
    /// <c>Merge</c>), so a compile that completes quickly cannot land in the gap between triggering
    /// and listening.</para>
    /// </summary>
    private static IObservable<PreWarmOutcome> RebuildMissingBytes(
        IWorkspace workspace,
        AccessService? accessService,
        string typePath,
        TimeSpan budget,
        ILogger? logger)
        => Observable.Using(
                () => AccessContextScope.AsSystem(accessService),
                _ =>
                {
                    var stream = workspace.GetMeshNodeStream(typePath);
                    return stream
                        .Take(1)
                        .Timeout(budget)
                        .SelectMany(current =>
                        {
                            var baseline = (current?.Content as NodeTypeDefinition)?.LastCompileSucceededAt;
                            logger?.LogInformation(
                                "DynamicTypePreWarmer: {TypePath} has no bytes in the assembly store despite a "
                                + "clean record — flipping CompilationStatus=Pending to force a rebuild "
                                + "(previous success {Baseline})",
                                typePath, baseline);

                            var settled = stream
                                .Where(n => n?.Content is NodeTypeDefinition d
                                    && (d.CompilationStatus is CompilationStatus.Error
                                                            or CompilationStatus.Unavailable
                                        || (d.CompilationStatus == CompilationStatus.Ok
                                            && IsFreshSuccess(d.LastCompileSucceededAt, baseline))))
                                .Take(1)
                                .Timeout(budget)
                                .Select(n =>
                                {
                                    var d = (NodeTypeDefinition)n!.Content!;
                                    return d.CompilationStatus switch
                                    {
                                        CompilationStatus.Error => new PreWarmOutcome(
                                            typePath, ClassifyCompileFailure(d), d.CompilationError),
                                        // The rebuild never reported an answer — not a
                                        // compile failure, so never labelled one.
                                        CompilationStatus.Unavailable => new PreWarmOutcome(
                                            typePath, PreWarmStatus.TimedOut, d.CompilationError),
                                        _ => new PreWarmOutcome(
                                            typePath, PreWarmStatus.Compiled, "rebuilt after store miss")
                                    };
                                });

                            // Never emits — it exists only for its write side effect, and it is
                            // merged SECOND so `settled` is already subscribed when it fires.
                            var trigger = stream
                                .Update(node =>
                                {
                                    if (node?.Content is not NodeTypeDefinition def)
                                        return node!;
                                    // Don't clobber an in-flight compile someone else already started.
                                    if (def.CompilationStatus is CompilationStatus.Pending
                                                              or CompilationStatus.Compiling)
                                        return node;
                                    return node with
                                    {
                                        Content = def with { CompilationStatus = CompilationStatus.Pending }
                                    };
                                })
                                .IgnoreElements()
                                .Select(_ => default(PreWarmOutcome)!)
                                .Catch<PreWarmOutcome, Exception>(ex =>
                                {
                                    logger?.LogWarning(ex,
                                        "DynamicTypePreWarmer: could not flip {TypePath} to Pending — the settle "
                                        + "wait below will time out and report it", typePath);
                                    return Observable.Empty<PreWarmOutcome>();
                                });

                            return Observable.Merge(settled, trigger).Take(1);
                        });
                })
            .Catch<PreWarmOutcome, Exception>(ex => Observable.Return(
                ex is TimeoutException
                    ? new PreWarmOutcome(typePath, PreWarmStatus.TimedOut, "rebuild after store miss did not settle")
                    : new PreWarmOutcome(typePath, PreWarmStatus.Faulted, ex.Message)));

    /// <summary>
    /// A compile success that is demonstrably NEWER than the one observed before the rebuild was
    /// triggered. With no baseline, any recorded success counts.
    /// </summary>
    private static bool IsFreshSuccess(DateTimeOffset? succeeded, DateTimeOffset? baseline) =>
        succeeded is { } s && (baseline is not { } b || s > b);

    /// <summary>
    /// Classify a compile that settled at Error. A type that DECLARES source queries whose live
    /// snapshot (<see cref="NodeTypeDefinition.CurrentSourceVersions"/>, maintained by the
    /// per-NodeType sources watcher) is EXPLICITLY empty is broken by CONTENT — its sources were
    /// deleted from the mesh — and reports <see cref="PreWarmStatus.NoSources"/>; see that member
    /// for why it must not gate a rollout. Anything else is
    /// <see cref="PreWarmStatus.CompileError"/>: in particular a NULL snapshot (watcher never
    /// seeded) stays a gating compile error, so a real regression cannot hide behind "not seeded".
    /// </summary>
    public static PreWarmStatus ClassifyCompileFailure(NodeTypeDefinition d) =>
        d.Sources is { Count: > 0 } && d.CurrentSourceVersions is { Count: 0 }
            ? PreWarmStatus.NoSources
            : PreWarmStatus.CompileError;

    /// <summary>
    /// Activate one dynamic NodeType's hub by subscribing to its own MeshNode stream —
    /// which routes a SubscribeRequest to the owning hub, activating it and firing the
    /// compile watcher's framework-stale / first-build kickoff. Holds the subscription
    /// (keeping the grain alive) until the compile reaches a usable build or Error, bounded
    /// by <paramref name="budget"/>. Best-effort: every non-success folds into an outcome,
    /// never an exception.
    /// </summary>
    private static IObservable<PreWarmOutcome> WarmOne(
        IWorkspace workspace,
        AccessService? accessService,
        string typePath,
        TimeSpan budget,
        ILogger? logger)
        => Observable.Using(
                () => AccessContextScope.AsSystem(accessService),
                _ => workspace.GetMeshNodeStream(typePath)
                    // Unavailable is terminal too — a driver already gave up determining
                    // the state, so waiting out the rest of the budget for a write that
                    // is not coming only slows the sweep down.
                    .Where(n => n?.Content is NodeTypeDefinition d
                        && (NodeTypeCompilationHelpers.HasUsableBuild(n, d)
                            || d.CompilationStatus is CompilationStatus.Error
                                                   or CompilationStatus.Unavailable))
                    .Take(1)
                    .Timeout(budget)
                    .Select(n =>
                    {
                        var d = (NodeTypeDefinition)n!.Content!;
                        return d.CompilationStatus switch
                        {
                            CompilationStatus.Error => new PreWarmOutcome(
                                typePath, ClassifyCompileFailure(d), d.CompilationError),
                            // TimedOut, never CompileError: the type is not broken, its
                            // state simply never came back.
                            CompilationStatus.Unavailable => new PreWarmOutcome(
                                typePath, PreWarmStatus.TimedOut, d.CompilationError),
                            _ => new PreWarmOutcome(typePath, PreWarmStatus.Compiled)
                        };
                    }))
            .Catch<PreWarmOutcome, Exception>(ex => Observable.Return(
                ex is TimeoutException
                    ? new PreWarmOutcome(typePath, PreWarmStatus.TimedOut)
                    : new PreWarmOutcome(typePath, PreWarmStatus.Faulted, ex.Message)))
            .Do(o => logger?.LogInformation(
                "DynamicTypePreWarmer: {TypePath} → {Status}{Detail}",
                o.TypePath, o.Status,
                string.IsNullOrEmpty(o.Detail) ? "" : $" ({o.Detail})"));
}
