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
    /// <summary>How many dynamic NodeType hubs to activate concurrently. The compiles
    /// themselves serialize on the Compile IoPool; this just caps in-flight activations.</summary>
    public const int DefaultMaxConcurrency = 4;

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
    /// </summary>
    public static IObservable<PreWarmOutcome> WarmDynamicTypes(
        IMessageHub mesh,
        ILogger? logger = null,
        int maxConcurrency = DefaultMaxConcurrency,
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
                    var typePaths = change.Items
                        .Where(n => !string.IsNullOrEmpty(n.Path)
                            && n.State == MeshNodeState.Active
                            && n.Content is NodeTypeDefinition d
                            // Only DYNAMIC types have source to compile. Static/framework
                            // NodeTypes ship their assembly with the process — nothing to warm.
                            && HasCompilableSource(d))
                        .Select(n => n.Path!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    logger?.LogInformation(
                        "DynamicTypePreWarmer: warming {Count} dynamic NodeType hub(s) (maxConcurrency={Max}, perTypeBudget={Budget})",
                        typePaths.Length, maxConcurrency, budget);

                    return typePaths.Length == 0
                        ? Observable.Empty<PreWarmOutcome>()
                        : typePaths
                            .Select(p => WarmOne(workspace, accessService, p, budget, logger))
                            .Merge(Math.Max(1, maxConcurrency));
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
