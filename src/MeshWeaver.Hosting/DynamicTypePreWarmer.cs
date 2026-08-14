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
    /// The compile settled at Error AND the type's source queries currently match ZERO Code nodes
    /// (<see cref="NodeTypeDefinition.CurrentSourceVersions"/> is EXPLICITLY empty) — the sources
    /// were deleted or moved out from under the type. This is a CONTENT verdict, not an image
    /// verdict: which nodes a mesh query matches is a property of the mesh, not of the framework
    /// being rolled out, so no image caused it and no rollout can fix it.
    ///
    /// <para>🚨 "Source queries" here means the type's EFFECTIVE queries — the ones it DECLARES in
    /// <see cref="NodeTypeDefinition.Sources"/> or, far more commonly, the DEFAULT
    /// <c>namespace:{path}/Source scope:subtree</c> pair it gets when it declares none. The
    /// classifier used to additionally require declared queries, which made this member
    /// unreachable for nearly every NodeType in a real mesh and is what let a DELETED type gate
    /// readiness on every boot (#1391). Do not reintroduce that condition: an empty
    /// <see cref="NodeTypeDefinition.Sources"/> means "uses the defaults", not
    /// "configuration-only".</para>
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

    /// <summary>
    /// 🚨 WALL-CLOCK COST of this one type's bake — the number that was missing.
    ///
    /// <para>Before this existed the only signal a compile unit produced was a binary pass/fail
    /// against a bootstrap deadline, so "which types are expensive?" and "did that change help?"
    /// were both unanswerable, and the only available response to a type brushing the budget was to
    /// widen it (Systemorph/MeshWeaver#1439). A per-type duration turns a budget into a measurement.</para>
    ///
    /// <para><see cref="TimeSpan.Zero"/> for an outcome that did no work — a type skipped because
    /// its upstream failed, or one already on the shared store — so a zero here means "not compiled",
    /// never "compiled instantly".</para>
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// How many source documents this type's compile unit contained, and how many bytes of C# they
    /// amounted to — the correlate a duration is only interesting NEXT to. A type whose duration
    /// grows while its unit does not is a different problem from one where both grow together, and
    /// the whole point of #1439 is that nobody could tell those apart. Zero when the driver did not
    /// resolve a source set (the activation path, which lets the compiler do its own discovery).
    /// </summary>
    public int SourceCount { get; init; }

    /// <inheritdoc cref="SourceCount"/>
    public long SourceBytes { get; init; }

    /// <summary>
    /// The cost, rendered for the per-type log line that already exists — e.g.
    /// <c>"3.4 s over 72 file(s), 1399 KB"</c>, or an empty string for an outcome that compiled
    /// nothing (so a skip does not gain a misleading "0 s").
    /// </summary>
    public string DescribeCost() =>
        Duration <= TimeSpan.Zero
            ? string.Empty
            : SourceCount > 0
                ? string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F1} s over {1} file(s), {2} KB",
                    Duration.TotalSeconds, SourceCount, SourceBytes / 1024)
                : string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F1} s", Duration.TotalSeconds);

    /// <summary>
    /// Evidence that this compile sampled a TORN source snapshot: at least one of the type's
    /// sources was last modified AT OR AFTER the compile started
    /// (<see cref="DynamicTypePreWarmer.SourcesMovedDuringCompile(NodeTypeDefinition)"/>), so the
    /// diagnostics describe a source set the mesh was in the middle of replacing.
    ///
    /// <para>Diagnostic only — it deliberately does NOT reclassify the outcome. A source moving
    /// mid-compile makes a failure SUSPECT, not innocent, and a torn compile of genuinely broken
    /// code must still gate. What clears the gate is the type actually rebuilding on this image
    /// (<c>NodeTypeBakeGateState.RetractRegression</c>); this flag is what lets the log say WHY the
    /// pod is refusing readiness on content that may repair itself, instead of telling an operator
    /// at 3am that the image is broken (issue #1214, proposal 3).</para>
    /// </summary>
    public bool SourcesMovedDuringCompile { get; init; }

    /// <summary>
    /// For a DERIVED outcome — <see cref="PreWarmStatus.UpstreamFailed"/>,
    /// <see cref="PreWarmStatus.UpstreamContentBroken"/>,
    /// <see cref="PreWarmStatus.UpstreamUnevaluated"/> — the upstream type that blocked this one.
    /// <c>null</c> for an outcome the sweep measured directly.
    ///
    /// <para>It is what makes a derived regression retractable WITHOUT touching this type: such a
    /// verdict's entire evidentiary basis is the blocker's verdict (the sweep never attempted this
    /// type — deliberately, since attempting it would burn a whole per-type budget on a build that
    /// cannot succeed). So when the blocker's regression is withdrawn, this one has nothing left
    /// holding it up and is withdrawn too. The alternative — watching each dependent for its own
    /// recovery — would ACTIVATE every skipped dependent's hub and hold it for the pod's lifetime,
    /// undoing exactly the saving the skip exists for.</para>
    /// </summary>
    public string? BlockedBy { get; init; }
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
    /// <see cref="PreWarmOutcome"/> per type and completes when all have settled or timed out.
    ///
    /// <para>🚨 <b>A FAILED ENUMERATION FAULTS THE STREAM — it is never laundered into an empty
    /// completion.</b> Per-TYPE trouble still folds into an outcome
    /// (<see cref="PreWarmStatus.CompileError"/>, <see cref="PreWarmStatus.TimedOut"/>,
    /// <see cref="PreWarmStatus.Faulted"/>), because that is a measurement. But if the ENUMERATION
    /// itself errors or times out, the sweep measured NOTHING, and the two states a subscriber has
    /// to tell apart are:</para>
    /// <list type="bullet">
    ///   <item><b>Zero types because none exist</b> — a fresh or genuinely empty mesh. Legitimate:
    ///     the stream completes normally with no emissions, and a readiness gate may serve.</item>
    ///   <item><b>Zero types because enumeration threw</b> — the pod has learned nothing. The
    ///     stream FAULTS, so a readiness gate can refuse rather than certify an unmeasured
    ///     bake.</item>
    /// </list>
    /// <para>These used to be indistinguishable here: a <c>Catch</c> swallowed the enumeration
    /// fault and returned <c>Observable.Empty</c>, so both arrived at the subscriber as "completed,
    /// zero outcomes" and <c>DynamicTypePreWarmerHostedService</c> marked the gate Complete →
    /// Healthy. The pre-run bake Job used to catch that from the outside — <i>"FINDING NOTHING IS
    /// NOT PASSING … a gate that certifies 'I verified nothing' is worse than no gate"</i>, exit 3,
    /// with a <c>Bake:AllowEmpty</c> escape — and it named THIS <c>Catch</c> as the reason it had
    /// to. Retiring that Job (#1357) removed the counterpart guard and left the hole live on the
    /// only remaining path. The distinction now lives at the source instead, which is the one place
    /// it can be made honestly.</para>
    ///
    /// <para>Note what did NOT change: an empty result is NOT an error. Emptiness is a legitimate
    /// answer, and refusing readiness for it would black-hole a genuinely empty mesh. Only the
    /// inability to obtain an answer gates.</para>
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
        bool batchBake = false,
        bool buildProtocol = false)
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
                            budget, pacing, batchBake, buildProtocol, logger));
                })
                // 🚨 NO Catch HERE, AND NO LOG-AND-SWALLOW — DELIBERATELY.
                //
                // The fault must reach the subscriber, because it is the ONLY thing that
                // distinguishes "this mesh has no dynamic types" from "this pod could not find
                // out". What used to stand here logged a warning and returned Observable.Empty,
                // which handed both cases to the caller as the identical terminal — and the
                // readiness gate then certified a bake that never happened.
                //
                // Nor is a log added here to compensate: the subscriber
                // (DynamicTypePreWarmerHostedService) already logs THIS exception, with the
                // severity that depends on whether the gate is armed — something only it knows.
                // A second Error line at the source would say the same thing worse and pay twice
                // for it in Loki.
                );
    }

    /// <summary>
    /// The shared assembly store this mesh compiles into, or <see cref="NullAssemblyStore"/> when the
    /// host registered none. A null store reports every lookup as a miss, so every type is treated as
    /// needing a bake — which matches what a storeless host does anyway (it recompiles on every
    /// activation), so the sweep degrades to its previous behaviour rather than misreporting.
    /// </summary>
    private static IAssemblyStore ResolveAssemblyStore(IMessageHub mesh) =>
        mesh.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;

    /// <summary>
    /// 🚨 ONE PROCESS BAKES; the rest SUBSCRIBE TO THE GO.
    ///
    /// <para>The cache is shared but the decision to rebuild is per-process, so without
    /// coordination every replica on a new image independently finds the same framework-stale cache
    /// and starts the same sweep into the same volume — concurrent cold compiles of the SAME
    /// NodeType, precisely the storm the sequential ordered sweep exists to prevent (four
    /// concurrent compiles on memex, 2026-07-28 04:05, dropped six plugin roots to the "did not
    /// settle" overlay). A rollout with <c>maxSurge</c>, or any <c>replicas &gt; 1</c>, hits this
    /// by default.</para>
    ///
    /// <para>Coordination is the build protocol (<c>Doc/Architecture/BuildCoordination</c>): the
    /// <c>Admin/Build</c> claim decides who bakes, and every other process completes on the
    /// per-fingerprint GO subscription. This replaced a file lease beside the assembly cache plus
    /// a 60 s share-poll follower; the lease's one-builder and steal-on-stale properties live on
    /// in the claim arbiter, asserted by the protocol's own tests. The protocol path runs even
    /// when the report is complete — a complete share still (re)publishes its fingerprint's GO,
    /// so a baker that crashed between finishing the share and announcing it heals on the next
    /// boot instead of stranding future GO waiters.</para>
    ///
    /// <para><paramref name="buildProtocol"/> <c>false</c> is the escape hatch: bake solo, no
    /// coordination — the right shape for a monolith, a test, a dev box, and the wrong one for any
    /// fleet.</para>
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
        bool buildProtocol,
        ILogger? logger)
    {
        if (buildProtocol)
            return BuildProtocolDriver.Run(
                mesh, report, definitions, store,
                () => WarmPending(
                    mesh, workspace, accessService, definitions, nodes, report,
                    budget, pacing, batchBake, logger),
                logger);

        return WarmPending(mesh, workspace, accessService, definitions, nodes, report, budget, pacing, batchBake, logger);
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
                + "cannot be ordered RELATIVE TO EACH OTHER — warmed together, in path order, as "
                + "soon as everything outside the cycle that they wait on is built (#1347: they "
                + "used to be demoted to last, which put the whole store/paywall chain at the end "
                + "of the sweep): {Cyclic}",
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
                        //
                        // BlockedBy carries the blocker as DATA, not just inside the detail text:
                        // a derived regression is retracted when its blocker's is, and parsing that
                        // relationship back out of a message would be the kind of coupling that
                        // breaks the next time the wording changes.
                        return Observable.Return(
                            new PreWarmOutcome(p, outcome, $"blocked by {named}") { BlockedBy = named });
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
                                        CompilationStatus.Error => FromFailedCompile(typePath, d),
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
                                    // Don't clobber an in-flight compile someone else already started…
                                    // …but ONLY while it can still be in flight (#1462).
                                    //
                                    // 🚨 `Compiling` is a non-terminal state and nothing else reconciles
                                    // it. The flip to `Compiling` is DURABLE; the terminal write is not
                                    // guaranteed — a process death mid-compile leaves the row there for
                                    // good. This guard then declines to touch it on every subsequent
                                    // sweep, so the type never reaches a terminal state: it is neither
                                    // `Ok` (usable) nor `Error` (classifiable), it holds portal
                                    // readiness, and it parks every instance hub for the full activation
                                    // budget. One row on `public.mesh_nodes` sat at `Compiling` for TEN
                                    // WEEKS this way; it was harmless only because it was an orphan the
                                    // prewarmer never enumerates.
                                    //
                                    // A claim older than the per-type budget cannot still be in flight —
                                    // whoever made it would have written a terminal state or been given
                                    // up on long ago — so it is re-driven rather than deferred to.
                                    // Note what this is NOT: no timer, no poller, no background sweep for
                                    // stale rows. The recovery rides the enumeration that already runs,
                                    // and only ever reinterprets a claim that has provably expired.
                                    if (IsLiveCompileClaim(def, budget))
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
    /// Classify a compile that settled at Error. The discriminator is one sentence:
    /// <b>"no sources matched" is not the same as "sources matched and did not compile."</b> The
    /// first is a CONTENT fact and must not gate readiness; the second is a verdict about this
    /// image and must.
    ///
    /// <para>The evidence is the live snapshot
    /// (<see cref="NodeTypeDefinition.CurrentSourceVersions"/>, maintained by the per-NodeType
    /// sources watcher over the type's resolved source set). EXPLICITLY empty ⇒
    /// <see cref="PreWarmStatus.NoSources"/>; see that member for why it must not gate. Anything
    /// else is <see cref="PreWarmStatus.CompileError"/> — in particular a NULL snapshot (watcher
    /// never seeded) stays gating, so a real regression cannot hide behind "not seeded".</para>
    ///
    /// <para>🚨 It deliberately does NOT also require <c>d.Sources is { Count: &gt; 0 }</c>, and
    /// that removal is the fix for issue #1391. An empty <see cref="NodeTypeDefinition.Sources"/>
    /// does not mean "configuration-only" — it means <b>"uses the DEFAULT queries"</b>
    /// (<c>namespace:{path}/Source scope:subtree</c>, see
    /// <see cref="Graph.Configuration.CodeQueryResolver.DefaultSources"/>), which is how very
    /// nearly every NodeType in a real mesh is authored. Requiring declared queries therefore made
    /// <see cref="PreWarmStatus.NoSources"/> unreachable for almost the entire population: a type
    /// whose <c>Source/</c> subtree had been DELETED was compiled against nothing, its
    /// configuration lambda's resulting <c>CS0246</c>/<c>CS1061</c> were recorded as an image
    /// verdict, and a node that no longer exists held portal readiness hostage on every pod boot.
    /// That is exactly what <c>Edu/Course</c> was doing to <c>memex</c>.</para>
    ///
    /// <para>This restores the three-outcome contract <see cref="Graph.Configuration.SourceSnapshot"/>
    /// already documents — an established-but-EMPTY snapshot is a content fact "(the sources were
    /// deleted, or the type is configuration-only)" and classifies as <c>NoSources</c> — from which
    /// the extra conjunct had silently drifted. A genuinely configuration-only type is folded in on
    /// purpose: with no Code nodes at all there is nothing for an image to regress, so a failure in
    /// its configuration lambda is content drift for its owner to fix, not a reason to stall
    /// everyone else's rollout.</para>
    ///
    /// <para>🚨 An empty snapshot is NOT sufficient on its own, and the second witness —
    /// <see cref="NodeTypeDefinition.LastCompileSucceededAt"/> — is what keeps the gate intact.
    /// Two very different types both present an empty snapshot:</para>
    /// <list type="bullet">
    ///   <item><b>Sources were DELETED</b> (<c>Edu/Course</c>): the type built successfully at some
    ///   point, so its sources demonstrably existed then, and their absence now is a content
    ///   change. Content verdict — must not gate.</item>
    ///   <item><b>Never had sources, and its own <c>Configuration</c> is broken</b>: nothing was
    ///   deleted; the type is defective as authored. That is a real defect and MUST gate — it is
    ///   what <c>DynamicTypePreWarmerTest</c>'s broken fixtures
    ///   (<c>Configuration = "config =&gt; this is not valid C# at all (("</c>, no sources) pin, and
    ///   reclassifying it also downgraded its dependents' cascade from the gating
    ///   <see cref="PreWarmStatus.UpstreamFailed"/> to the non-gating
    ///   <see cref="PreWarmStatus.UpstreamContentBroken"/>.</item>
    /// </list>
    /// <para>"It once produced a working build" is the durable evidence that separates them, and it
    /// survives a failure — <c>ApplyCompileFailure</c> clears <c>CompiledSources</c> but never
    /// <c>LastCompileSucceededAt</c>. A type that has NEVER built cannot have lost anything.</para>
    ///
    /// <para>🚨 THREE shapes of <see cref="NodeTypeDefinition.CurrentSourceVersions"/> exist in
    /// production, not two — populated, explicitly <c>{}</c>, and ABSENT (SQL NULL; observed on
    /// <c>public.mesh_nodes</c> rows). Only the middle one may reclassify. The pattern
    /// <c>is { Count: 0 }</c> gets this right BY CONSTRUCTION — a C# property pattern never matches
    /// null — so absent falls through to <see cref="PreWarmStatus.CompileError"/> and gates. Do not
    /// "simplify" it to <c>d.CurrentSourceVersions?.Count == 0</c> or a bare <c>.Count == 0</c>:
    /// the first is equivalent but easy to misread, the second throws. Pinned by
    /// <c>ClassifyCompileFailure_DefaultQueriesWithNullSnapshot_StaysCompileError</c>.</para>
    ///
    /// <para><b>Why not <see cref="NodeTypeDefinition.CompiledSources"/></b>, which would be the
    /// more precise evidence ("the last successful build CONSUMED sources"): it is written only on
    /// SUCCESS, and <c>ApplyCompileFailure</c> nulls it. Every currently-failing type in production
    /// therefore lacks it — including the three that demonstrably compiled in June — so it carries
    /// no history at all. A new field stamped on success would be no better: it could only populate
    /// after a future successful compile, which is precisely what a source-less type can no longer
    /// do.</para>
    ///
    /// <para>The one case this deliberately concedes: a genuinely configuration-only type that once
    /// built and is later broken by an IMAGE change reads as content-broken and does not gate.
    /// Accepted knowingly — it is far rarer than the population the old rule broke (every
    /// default-query type with deleted sources, gating forever), and no such type appears in the
    /// observed failures on either portal.</para>
    ///
    /// <para>🚨 What this must NEVER become is "compile errors stop gating". The gate is right; only
    /// the classification was wrong. A type whose sources are still there and do not compile keeps
    /// a non-empty snapshot and keeps gating — pinned by
    /// <c>ClassifyCompileFailure_MatchedSources_StaysCompileError</c>.</para>
    /// </summary>
    public static PreWarmStatus ClassifyCompileFailure(NodeTypeDefinition d) =>
        d.CurrentSourceVersions is { Count: 0 } && d.LastCompileSucceededAt is not null
            ? PreWarmStatus.NoSources
            : PreWarmStatus.CompileError;

    /// <summary>
    /// Whether the type's SOURCES MOVED while this compile was running — at least one entry of
    /// <see cref="NodeTypeDefinition.CurrentSourceVersions"/> (each value is that Code node's
    /// <c>LastModified.UtcTicks</c>, written by the per-NodeType sources watcher) is at or after
    /// <see cref="NodeTypeDefinition.LastCompileStartedAt"/> (stamped on the Pending → Compiling
    /// transition). When that holds, Roslyn's verdict was produced against a source set the mesh
    /// was concurrently replacing — a half-applied plugin auto-update, a git sync, a bulk edit.
    ///
    /// <para>Both fields survive a failed compile: <c>ApplyCompileFailure</c> nulls
    /// <c>CompiledSources</c> but leaves the start stamp and the live snapshot alone, so the
    /// evidence is readable exactly where the verdict is formed.</para>
    ///
    /// <para>🚨 SUSPICION, NOT ABSOLUTION. This never downgrades an outcome: a torn compile of
    /// code that is genuinely broken must still gate, and the check is one-sided anyway — a source
    /// written a second AFTER the compile failed is just as much a torn snapshot but is invisible
    /// here until the write lands (in the 2026-08-11 incident the callers landed at 11:06:57, after
    /// the failures). That asymmetry is precisely why the CURE is
    /// <c>NodeTypeBakeGateState.RetractRegression</c> — level-triggered on the type rebuilding —
    /// and this predicate is only what names the suspicion in the log.</para>
    /// </summary>
    public static bool SourcesMovedDuringCompile(NodeTypeDefinition d) =>
        d.LastCompileStartedAt is { } started
        && d.CurrentSourceVersions is { Count: > 0 } current
        && current.Values.Any(ticks => ticks >= started.UtcTicks);

    /// <summary>
    /// The outcome for a compile that settled at <see cref="CompilationStatus.Error"/>: Roslyn's
    /// verdict classified by <see cref="ClassifyCompileFailure"/>, carrying the torn-snapshot
    /// evidence (<see cref="SourcesMovedDuringCompile(NodeTypeDefinition)"/>) so the reporting
    /// layer can say whether the source set was stable when the verdict was formed.
    /// </summary>
    internal static PreWarmOutcome FromFailedCompile(string typePath, NodeTypeDefinition d) =>
        new(typePath, ClassifyCompileFailure(d), d.CompilationError)
        {
            SourcesMovedDuringCompile = SourcesMovedDuringCompile(d)
        };

    /// <summary>
    /// Watch a REGRESSED NodeType until it reaches a usable build on this image, then retract its
    /// regression from <paramref name="gate"/> — the cure for issue #1214, where a bake compiled a
    /// half-applied plugin update, recorded four false regressions, and stalled the rollout even
    /// though the content converged (and the types recompiled green) seconds later.
    ///
    /// <para><b>Observation, not a watchdog.</b> Nothing here triggers, retries or polls: the
    /// platform's own park registry already un-parks and recompiles a failed type the moment its
    /// source snapshot changes (<c>NodeTypeCompileParkRegistry.ShouldRetryForSourceChange</c>), and
    /// a lazy activation compiles it too. This subscription only READS the type's own node stream
    /// — the same level-triggered surface the sweep and the GUI use — and stops at the first
    /// emission that shows a usable build. There is deliberately no timeout: a regression that is
    /// never repaired must gate forever, which is what "no emission" already means.</para>
    ///
    /// <para>Holding the subscription also keeps the condemned type's per-node hub ACTIVATED, which
    /// is what keeps its sources watcher — the thing that notices the repair — installed and
    /// running. So the watch is not merely a listener for a recovery; on an idle pod it is part of
    /// why the recovery can happen at all.</para>
    ///
    /// <para>🚨 <b>The recovery must be a compile that is demonstrably FRESH</b> — a settled
    /// <see cref="CompilationStatus.Ok"/> whose
    /// <see cref="NodeTypeDefinition.LastCompileSucceededAt"/> is strictly newer than the one this
    /// watch observed when it started. <see cref="NodeTypeCompilationHelpers.HasUsableBuild"/>
    /// alone is NOT sufficient and matching on it would silently disable the whole gate: a failed
    /// compile keeps the PREVIOUS build's assembly coordinates and framework stamp
    /// (<c>ApplyCompileFailure</c> clears only the status, the error and
    /// <c>CompiledSources</c>), so any type that had ever compiled successfully on this image would
    /// satisfy that check the instant its regression was recorded, and every regression would
    /// retract itself immediately. The <see cref="RebuildMissingBytes"/> path guards the identical
    /// trap the identical way; this is the same <see cref="IsFreshSuccess"/> rule.</para>
    ///
    /// <para>The subscription is returned so its owner disposes it at shutdown; a discarded
    /// subscription would root the hub.</para>
    /// </summary>
    public static IDisposable WatchForRecovery(
        IMessageHub mesh, NodeTypeBakeGateState gate, string typePath, ILogger? logger)
    {
        var workspace = mesh.GetWorkspace();
        var accessService = mesh.ServiceProvider.GetService<AccessService>();
        // System-scoped for the same reason the sweep's reads are: watching a NodeType record
        // across partitions is infrastructure, not a user-attributable read.
        return Observable
            .Using(
                () => AccessContextScope.AsSystem(accessService),
                _ =>
                {
                    // One shared handle (IMeshNodeStreamCache): the baseline read and the wait are
                    // the SAME stream, and it replays its latest node to the second subscriber — so
                    // a success landing between the two cannot fall through the gap.
                    var stream = workspace.GetMeshNodeStream(typePath);
                    return stream
                        .Take(1)
                        .Select(current =>
                            (current?.Content as NodeTypeDefinition)?.LastCompileSucceededAt)
                        .SelectMany(baseline => stream
                            .Where(n => n?.Content is NodeTypeDefinition d
                                && d.CompilationStatus == CompilationStatus.Ok
                                && NodeTypeCompilationHelpers.HasUsableBuild(n, d)
                                && IsFreshSuccess(d.LastCompileSucceededAt, baseline))
                            .Take(1));
                })
            .Subscribe(
                _ =>
                {
                    if (gate.RetractRegression(
                            typePath, "rebuilt to a usable build on this image after the bake"))
                        logger?.LogWarning(
                            "DynamicTypePreWarmer: RETRACTING the regression recorded for "
                            + "{TypePath} — it has since reached a usable build on THIS image, so "
                            + "the earlier failure was not evidence against the image (typically a "
                            + "compile that sampled a half-applied content update — issue #1214). "
                            + "Gate now: {Detail}",
                            typePath, gate.Detail);
                },
                ex => logger?.LogWarning(ex,
                    "DynamicTypePreWarmer: recovery watch for the regressed type {TypePath} "
                    + "faulted — the regression STANDS (a watch that cannot observe a recovery "
                    + "must never be read as one)",
                    typePath));
    }

    /// <summary>
    /// Whether <paramref name="def"/>'s compile claim can still be in flight, i.e. whether deferring
    /// to it is honouring a live compile rather than a stranded one (#1462).
    ///
    /// <para><c>Pending</c> is always honoured: it is the state this prewarmer itself writes to ASK
    /// for a compile, and a driver picks it up promptly.</para>
    ///
    /// <para><c>Compiling</c> is honoured only while <see cref="NodeTypeDefinition.LastCompileStartedAt"/>
    /// is within <paramref name="budget"/> — the same bound the sweep gives a type to settle. Past it,
    /// no driver is still working on it: either it finished (and would have written a terminal state)
    /// or it died. A row with NO start timestamp at all is treated as stranded too, since a live
    /// compile always stamps one (<c>NodeTypeCompilationHelpers</c>); an unstamped <c>Compiling</c> is
    /// exactly the shape a row left over from an older write carries, and honouring it forever is how
    /// this became permanent.</para>
    /// </summary>
    internal static bool IsLiveCompileClaim(NodeTypeDefinition def, TimeSpan budget) =>
        def.CompilationStatus switch
        {
            CompilationStatus.Pending => true,
            CompilationStatus.Compiling =>
                def.LastCompileStartedAt is { } startedAt
                && DateTimeOffset.UtcNow - startedAt <= budget,
            _ => false,
        };

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
    {
        // 🚨 What this type's activation-driven bake COST, on the line that already exists —
        // the activation-path twin of the batch driver's measurement (issue #1439). Note what it
        // measures HERE: not Roslyn, but the whole round trip — activating the type's own hub,
        // its compile watcher settling, the state write coming back. That is the number worth
        // having, because it is the one the bootstrap's liveness deadline is actually spent on.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return Observable.Using(
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
                            CompilationStatus.Error => FromFailedCompile(typePath, d),
                            // TimedOut, never CompileError: the type is not broken, its
                            // state simply never came back.
                            //
                            // 🚨 This is also where a compile that REFUSED TO RUN lands. A
                            // compile whose source set could not be established never reaches
                            // Roslyn and stamps CompilationStatus.Unavailable rather than Error
                            // (SourceDiscoveryUnavailableException → ApplyCompileFailure), so the
                            // "starved cross-silo discovery ⇒ phantom CS0246 ⇒ false regression"
                            // class arrives here as a non-verdict — issue #1218. Do NOT add a
                            // classification for it below: the distinction is made where the
                            // knowledge is (the compiler saw WHICH query died), and this branch
                            // is what carries it into the non-gating bucket.
                            CompilationStatus.Unavailable => new PreWarmOutcome(
                                typePath, PreWarmStatus.TimedOut, d.CompilationError),
                            _ => new PreWarmOutcome(typePath, PreWarmStatus.Compiled)
                        };
                    }))
            .Catch<PreWarmOutcome, Exception>(ex => Observable.Return(
                ex is TimeoutException
                    ? new PreWarmOutcome(typePath, PreWarmStatus.TimedOut)
                    : new PreWarmOutcome(typePath, PreWarmStatus.Faulted, ex.Message)))
            .Select(o => o with { Duration = clock.Elapsed })
            .Do(o => logger?.LogInformation(
                "DynamicTypePreWarmer: {TypePath} → {Status}{Detail} — {Cost}",
                o.TypePath, o.Status,
                string.IsNullOrEmpty(o.Detail) ? "" : $" ({o.Detail})",
                o.DescribeCost()));
    }
}
