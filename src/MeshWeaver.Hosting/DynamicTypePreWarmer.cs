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
    /// <summary>The warm subscription faulted (best-effort — the lazy path still works).</summary>
    Faulted
}

/// <summary>One dynamic NodeType's pre-warm result.</summary>
public record PreWarmOutcome(string TypePath, PreWarmStatus Status, string? Detail = null);

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
        TimeSpan? perTypeBudget = null)
    {
        var budget = perTypeBudget ?? DefaultPerTypeBudget;
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
                    var definitions = change.Items
                        .Where(n => !string.IsNullOrEmpty(n.Path)
                            && n.State == MeshNodeState.Active
                            && n.Content is NodeTypeDefinition d
                            // Only DYNAMIC types have source to compile. Static/framework
                            // NodeTypes ship their assembly with the process — nothing to warm.
                            && HasCompilableSource(d))
                        .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            g => g.Key,
                            g => (NodeTypeDefinition?)g.First().Content,
                            StringComparer.OrdinalIgnoreCase);

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
                    var dependencies = NodeTypeDependencyGraph.Build(definitions);
                    var order = NodeTypeDependencyGraph.TopologicalOrder(dependencies, out var cyclic);

                    logger?.LogInformation(
                        "DynamicTypePreWarmer: warming {Count} dynamic NodeType hub(s) in dependency order "
                        + "(sequential, perTypeBudget={Budget}): {Order}",
                        order.Count, budget, string.Join(" → ", order));
                    if (!cyclic.IsEmpty)
                        logger?.LogWarning(
                            "DynamicTypePreWarmer: {Count} NodeType(s) form a source dependency CYCLE and "
                            + "cannot be ordered — warmed last, in path order: {Cyclic}",
                            cyclic.Count, string.Join(", ", cyclic));

                    // FAIL GRACEFULLY DOWNSTREAM. A type whose upstream did not reach a usable build
                    // cannot build either, so it is not attempted: it is reported as UpstreamFailed
                    // naming the blocker, and joins the failed set so ITS dependents are skipped too
                    // — the propagation is transitive purely because we walk in topological order.
                    // Without this, one broken type costs every dependent a full per-type budget of
                    // waiting for something that cannot succeed.
                    //
                    // The set is mutated inside a Concat, which subscribes strictly one at a time,
                    // so there is no concurrent access — and each step is wrapped in Defer so it
                    // reads the set as it stands WHEN ITS TURN COMES, not when the chain was built.
                    var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Concat, never Merge: the next type is SUBSCRIBED only after the previous one
                    // has completed, so "dependencies first" is structural rather than a convention.
                    // The gap between types keeps the sweep a background trickle rather than a queue
                    // of back-to-back cold activations; it costs nothing, since the warm-up has no
                    // deadline and Part 2 compiles lazily regardless.
                    return order.Count == 0
                        ? Observable.Empty<PreWarmOutcome>()
                        : order
                            .Select((p, i) => Observable.Defer(() =>
                            {
                                var blocker = NodeTypeDependencyGraph.FirstBlockedBy(p, dependencies, failed);
                                if (blocker is not null)
                                {
                                    failed.Add(p);
                                    logger?.LogWarning(
                                        "DynamicTypePreWarmer: skipping {TypePath} — its dependency {Blocker} "
                                        + "did not reach a usable build, so it cannot compile either "
                                        + "(lazy compile still applies if the upstream recovers)",
                                        p, blocker);
                                    // No pacing for a skip: it activates nothing, so there is no
                                    // burst to spread out and no reason to slow the sweep down.
                                    return Observable.Return(new PreWarmOutcome(
                                        p, PreWarmStatus.UpstreamFailed, $"blocked by {blocker}"));
                                }

                                return WarmOne(workspace, accessService, p, budget, logger)
                                    .DelaySubscription(i == 0 ? TimeSpan.Zero : BetweenTypes)
                                    .Do(o =>
                                    {
                                        if (o.Status != PreWarmStatus.Compiled)
                                            failed.Add(p);
                                    });
                            }))
                            .Concat();
                })
                .Catch<PreWarmOutcome, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "DynamicTypePreWarmer: enumeration of dynamic NodeTypes failed — pre-warm skipped (Part 2 still compiles lazily on first access)");
                    return Observable.Empty<PreWarmOutcome>();
                }));
    }

    /// <summary>A NodeType has something for Roslyn to compile (so it is a dynamic type worth warming).</summary>
    private static bool HasCompilableSource(NodeTypeDefinition d) =>
        !string.IsNullOrWhiteSpace(d.Configuration)
        || !string.IsNullOrWhiteSpace(d.HubConfiguration)
        || (d.Sources is { Count: > 0 });

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
                    .Where(n => n?.Content is NodeTypeDefinition d
                        && (NodeTypeCompilationHelpers.HasUsableBuild(n, d)
                            || d.CompilationStatus == CompilationStatus.Error))
                    .Take(1)
                    .Timeout(budget)
                    .Select(n =>
                    {
                        var d = (NodeTypeDefinition)n!.Content!;
                        return d.CompilationStatus == CompilationStatus.Error
                            ? new PreWarmOutcome(typePath, PreWarmStatus.CompileError, d.CompilationError)
                            : new PreWarmOutcome(typePath, PreWarmStatus.Compiled);
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
