using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// The INITIAL-BAKE batch driver (issue #1207): drives every pending dynamic NodeType's compile
/// DIRECTLY through the ONE compiler (<see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/>
/// with pre-resolved sources) instead of activating each type's own hub and waiting for its
/// compile watcher to settle.
///
/// <para><b>Why.</b> Measured on memex-cloud 2026-08-11: a healthy sweep compiled 162 types with
/// the Roslyn work summing to 51 SECONDS inside a 7-minute wall clock — and on 2026-08-10/11 the
/// same sweep took 20 minutes to 5 HOURS because every per-type activation round-trip could land
/// on a wedged peer silo and eat a 5-minute timeout. The cost was never compilation; it was the
/// 237 grain activations + stream settles that existed only to trigger compiles. This driver
/// removes them: source discovery is ONE batched pass, Roslyn runs in-process on the Compile
/// pool, the artifacts (per-type DLLs, assembly-store upload, release records, compile-state
/// stamps) are identical to the activation path's — the stamp field-set is literally shared
/// (<see cref="NodeTypeCompilationHelpers.ApplyCompileSuccess"/> /
/// <see cref="NodeTypeCompilationHelpers.ApplyCompileFailure"/>).</para>
///
/// <para><b>Initial-bake ONLY.</b> The lazy per-type path ("usual compile" — activation →
/// compile watcher → <c>RunCompile</c>) is untouched; <see cref="DynamicTypePreWarmer"/> takes
/// this path only when the pod's bake GATES readiness (the pod serves nobody while it bakes —
/// the same discriminator that removes the between-types pacing), with a config escape hatch.</para>
///
/// <para><b>State writes are storage-level, by design.</b> The compile stamps land through
/// <see cref="IStorageAdapter"/> + <see cref="IMeshChangeFeed"/>
/// (<see cref="StorageAdapterChangeFeedExtensions.WriteAndPublishUpdated"/>) — the same layer the
/// mesh hub's node-lifecycle handlers write through — NOT through
/// <c>GetMeshNodeStream(path).Update</c>, because a cross-hub stream write to a foreign node
/// ROUTES TO AND ACTIVATES the owning per-type hub: exactly the round-trip (and wedged-peer
/// 5-minute timeout) this driver exists to remove, and the activation's own first-build /
/// framework-stale kickoffs would race the stamp with a duplicate compile.
/// <see cref="IStorageAdapter.Changes"/> documents this write shape as the sanctioned
/// infrastructure bypass: any live per-node hub reconciles from the adapter's change feed
/// (forward-only, version-gated — see <c>MeshDataSource</c>'s reconcile bridge), and the
/// <see cref="IMeshChangeFeed"/> publish invalidates the cross-silo caches the same way
/// <c>RunCompile</c>'s post-compile publish does.</para>
/// </summary>
internal static class NodeTypeBatchBake
{
    /// <summary>How long a quiet gap in a chunked query result marks the accumulated set as
    /// complete — same contract as the compiler's direct source probe (a query's Initial arrives
    /// in chunks; a partial source set compiles WRONG, which is worse than slow).</summary>
    private static readonly TimeSpan QueryQuietWindow = TimeSpan.FromSeconds(1);

    /// <summary>Wall clock for the whole batched discovery pass. On elapse the caller falls back
    /// to the activation-driven sweep — discovery must never wedge the bake.</summary>
    private static readonly TimeSpan DiscoveryBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// ONE batched source-discovery pass for every pending type: instead of each compile issuing
    /// its own source queries (2 × N queries, each against a per-type synced-query cache that can
    /// stall for a 45s heartbeat), a single <c>nodeType:Code</c> fetch pulls every Code node once
    /// and each type's source set is selected in memory with the framework's own query matcher
    /// (<see cref="CodeQueryResolver.Matches"/> — the same heuristic release records use to
    /// classify compiled sources). A query the matcher cannot evaluate (free text / exotic
    /// tokens) is run individually against <see cref="IMeshService"/> so semantics never silently
    /// change — the in-memory match handles only the shapes it provably mirrors
    /// (<c>path:</c>/<c>namespace:</c>/<c>scope:</c>/<c>nodeType:Code</c>).
    /// </summary>
    /// <returns>Per-type source lists keyed by type path; missing key = no sources resolved.</returns>
    public static IObservable<ImmutableDictionary<string, IReadOnlyList<MeshNode>>> ResolveSources(
        IMeshService meshService,
        AccessService? accessService,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IReadOnlyCollection<string> pendingTypePaths,
        ILogger? logger)
    {
        // Expand every pending type's Source + Test queries — the SAME union the compiler's own
        // snapshot consumes (NodeSources / SnapshotSources), so the batch compiles the same set.
        var perType = new List<(string TypePath, IReadOnlyList<string> Matchable, IReadOnlyList<string> Exotic)>();
        foreach (var typePath in pendingTypePaths)
        {
            definitions.TryGetValue(typePath, out var def);
            var expanded = CodeQueryResolver
                .ExpandAll(def?.Sources, CodeQueryResolver.DefaultSources, typePath)
                .Concat(CodeQueryResolver.ExpandAll(def?.Tests, CodeQueryResolver.DefaultTests, typePath))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var matchable = expanded.Where(IsInMemoryMatchable).ToList();
            var exotic = expanded.Where(q => !IsInMemoryMatchable(q)).ToList();
            perType.Add((typePath, matchable, exotic));
        }

        var needsGlobalFetch = perType.Any(t => t.Matchable.Count > 0);
        var exoticQueries = perType
            .SelectMany(t => t.Exotic)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (exoticQueries.Count > 0)
            logger?.LogInformation(
                "BatchBake: {Count} source quer(ies) cannot be matched in memory and run "
                + "individually: {Queries}",
                exoticQueries.Count, string.Join(" | ", exoticQueries));

        // 🚨 System-scoped: source discovery across every partition is framework infrastructure,
        // not a user-attributable read — the same rule as the compiler's own GetSourceCollection.
        // Observable.Using holds the scope across the live subscription (the queries emit on IO
        // threads where the ambient AsyncLocal is gone).
        return Observable.Using(
                () => AccessContextScope.AsSystem(accessService),
                _ =>
                {
                    var globalFetch = needsGlobalFetch
                        ? RunQuery(meshService, $"nodeType:{CodeNodeType.NodeType}")
                        : Observable.Return(ImmutableDictionary<string, MeshNode>.Empty
                            .WithComparers(StringComparer.OrdinalIgnoreCase));

                    // Exotic queries run AFTER the global fetch, one at a time (Concat) — the
                    // rare escape hatch must not fan out against a cold mesh.
                    var exoticFetch = exoticQueries.Count == 0
                        ? Observable.Return(ImmutableDictionary<string, ImmutableDictionary<string, MeshNode>>.Empty)
                        : exoticQueries
                            .Select(q => RunQuery(meshService, q).Select(nodes => (Query: q, Nodes: nodes)))
                            .Concat()
                            .ToList()
                            .Select(results => results.ToImmutableDictionary(
                                r => r.Query, r => r.Nodes, StringComparer.Ordinal));

                    return globalFetch
                        .SelectMany(allCode => exoticFetch.Select(exotic => Assemble(perType, allCode, exotic, logger)))
                        .Timeout(DiscoveryBudget);
                });
    }

    /// <summary>
    /// A query the in-memory matcher provably mirrors: every token is <c>path:</c>,
    /// <c>namespace:</c>, <c>scope:</c> or <c>nodeType:Code</c>. Anything else (free text, other
    /// filters) goes to the mesh backend individually — never silently approximated.
    /// </summary>
    internal static bool IsInMemoryMatchable(string expandedQuery)
    {
        var hasLocation = false;
        foreach (var token in expandedQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1)
                return false;
            var key = token[..colon];
            var value = token[(colon + 1)..];
            if (key.Equals("path", StringComparison.OrdinalIgnoreCase)
                || key.Equals("namespace", StringComparison.OrdinalIgnoreCase))
                hasLocation = true;
            else if (key.Equals("scope", StringComparison.OrdinalIgnoreCase))
            {
                // The matcher understands children (default) and subtree/descendants only.
                if (!value.Equals("subtree", StringComparison.OrdinalIgnoreCase)
                    && !value.Equals("descendants", StringComparison.OrdinalIgnoreCase)
                    && !value.Equals("children", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else if (key.Equals("nodeType", StringComparison.OrdinalIgnoreCase))
            {
                // The global fetch is nodeType:Code — any other type filter must run for real.
                if (!value.Equals(CodeNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else
                return false;
        }
        // Without a location token the query would match everything under the matcher while the
        // backend applies its own semantics — run it for real.
        return hasLocation;
    }

    /// <summary>
    /// One mesh query, chunk-accumulated to a settled path→node map: fold every
    /// <see cref="QueryResultChange{T}"/> (the compiler's own pure fold,
    /// <see cref="MeshNodeCompilationService.ApplyQueryChange"/>) and treat a
    /// <see cref="QueryQuietWindow"/> of silence as completion — a bare <c>Take(1)</c> on a
    /// chunked Initial would hand the compiler a PARTIAL source set, which compiles wrong.
    /// </summary>
    private static IObservable<ImmutableDictionary<string, MeshNode>> RunQuery(
        IMeshService meshService, string query)
        => Observable
            .Defer(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query)))
            .Scan(
                ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
                MeshNodeCompilationService.ApplyQueryChange)
            .Throttle(QueryQuietWindow)
            .Take(1);

    /// <summary>Select each type's source set from the batched fetches.</summary>
    private static ImmutableDictionary<string, IReadOnlyList<MeshNode>> Assemble(
        IReadOnlyList<(string TypePath, IReadOnlyList<string> Matchable, IReadOnlyList<string> Exotic)> perType,
        ImmutableDictionary<string, MeshNode> allCode,
        ImmutableDictionary<string, ImmutableDictionary<string, MeshNode>> exoticResults,
        ILogger? logger)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, IReadOnlyList<MeshNode>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (typePath, matchable, exotic) in perType)
        {
            var matched = new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase);
            if (matchable.Count > 0)
                foreach (var (path, node) in allCode)
                    if (CodeQueryResolver.Matches(path, matchable))
                        matched[path] = node;
            foreach (var q in exotic)
                if (exoticResults.TryGetValue(q, out var nodes))
                    foreach (var (path, node) in nodes)
                        matched[path] = node;

            // Deterministic order: the compiler concatenates the files into one syntax tree, so
            // any order compiles identically — but a stable order keeps artifacts reproducible.
            builder[typePath] = matched.Values
                .OrderBy(n => n.Path, StringComparer.Ordinal)
                .ToList();
        }

        logger?.LogInformation(
            "BatchBake: source discovery resolved {CodeNodes} Code node(s) into source sets for "
            + "{Types} pending type(s) in one pass",
            allCode.Count, builder.Count);
        return builder.ToImmutable();
    }

    /// <summary>
    /// Compile ONE pending type directly — no hub activation, no compile-watcher settle — then
    /// write the same artifacts the activation path writes: the release record
    /// (<see cref="MeshDataSourceExtensions.TryCreateReleaseNode"/> — bounded, best-effort, same
    /// helper) and the compile-state stamps
    /// (<see cref="NodeTypeCompilationHelpers.ApplyCompileSuccess"/> /
    /// <see cref="NodeTypeCompilationHelpers.ApplyCompileFailure"/> — the shared field-set),
    /// storage-level under System identity. Emits exactly one <see cref="PreWarmOutcome"/> with
    /// the activation path's status vocabulary; never throws.
    /// </summary>
    public static IObservable<PreWarmOutcome> BakeOne(
        IMessageHub mesh,
        MeshNode typeNode,
        IReadOnlyList<MeshNode> sources,
        TimeSpan budget,
        ILogger? logger)
    {
        var typePath = typeNode.Path;
        var compiler = mesh.ServiceProvider.GetService<IMeshNodeCompilationService>();
        if (compiler is null)
            return Observable.Return(new PreWarmOutcome(
                typePath, PreWarmStatus.Faulted, "no IMeshNodeCompilationService registered"));
        var accessService = mesh.ServiceProvider.GetService<AccessService>();

        // 🚨 System-scoped end-to-end: the compile reads sources across the mesh and the stamp
        // writes framework state — infrastructure, exactly like RunCompile's deferred pipelines.
        return Observable.Using(
                () => AccessContextScope.AsSystem(accessService),
                _ => compiler
                    .CompileAndGetConfigurations(typeNode, sources)
                    .Take(1)
                    .Select(result => (Result: result, Error: (Exception?)null))
                    .Catch<(NodeCompilationResult? Result, Exception? Error), Exception>(ex =>
                        Observable.Return(((NodeCompilationResult?)null, (Exception?)ex)))
                    // Totality guard — same contract as RunCompile: every driven compile reaches
                    // exactly one terminal outcome; an empty completion must not be silent.
                    .DefaultIfEmpty((null, new InvalidOperationException(
                        $"Batch compile pipeline for '{typePath}' completed without producing an outcome — "
                        + "an upstream stage (include resolution / Roslyn bridge) terminated empty.")))
                    .SelectMany(o => StampAndReport(mesh, typeNode, sources, o.Result, o.Error, logger)))
            // The per-type budget bounds compile + stamp together. A timeout is NOT a verdict —
            // it reports TimedOut, which the gate files under "not evaluated", never a regression.
            .Timeout(budget)
            .Catch<PreWarmOutcome, Exception>(ex => Observable.Return(
                ex is TimeoutException
                    ? new PreWarmOutcome(typePath, PreWarmStatus.TimedOut,
                        "batch compile did not settle within the per-type budget")
                    : new PreWarmOutcome(typePath, PreWarmStatus.Faulted, ex.Message)))
            .Do(o => logger?.LogInformation(
                "BatchBake: {TypePath} → {Status}{Detail}",
                o.TypePath, o.Status,
                string.IsNullOrEmpty(o.Detail) ? "" : $" ({o.Detail})"));
    }

    private static IObservable<PreWarmOutcome> StampAndReport(
        IMessageHub mesh,
        MeshNode typeNode,
        IReadOnlyList<MeshNode> sources,
        NodeCompilationResult? result,
        Exception? error,
        ILogger? logger)
    {
        var typePath = typeNode.Path;
        var ok = error is null && !string.IsNullOrEmpty(result?.AssemblyLocation);

        // Release record exactly as the activation path writes it — the SAME bounded, observed,
        // best-effort helper (a failed create yields null and the stamp keeps the previous
        // release path; compile correctness never depends on it). No compile activity node is
        // created on the batch path: the per-type activity bookkeeping was pure overhead on a
        // gated pod nobody is watching, and a null activity path is exactly what the shared stamp
        // writes when the activation path's activity create doesn't land.
        var releaseObservable = ok
            ? MeshDataSourceExtensions.TryCreateReleaseNode(
                mesh, typePath, result!, typeNode, activityPath: null, logger)
            : Observable.Return<string?>(null);

        return releaseObservable
            .Take(1)
            .DefaultIfEmpty()
            .SelectMany(releasePath => WriteStamp(mesh, typeNode, ok, result, error, releasePath, logger))
            .Select(_ =>
            {
                if (ok)
                    return new PreWarmOutcome(typePath, PreWarmStatus.Compiled);

                // Same classification the activation path applies (ClassifyCompileFailure): a
                // type that DECLARES source queries whose resolution is EXPLICITLY empty is
                // broken by CONTENT (its sources were deleted from the mesh) — NoSources, which
                // must never gate a rollout. The batch's own resolved snapshot is the authority
                // here — it IS the set the compile just consumed.
                var declaredSources = typeNode
                    .ContentAs<NodeTypeDefinition>(mesh.JsonSerializerOptions)?.Sources;
                var status = declaredSources is { Count: > 0 } && sources.Count == 0
                    ? PreWarmStatus.NoSources
                    : PreWarmStatus.CompileError;
                return new PreWarmOutcome(
                    typePath, status, NodeTypeCompilationHelpers.SummarizeCompileError(result, error));
            });
    }

    /// <summary>
    /// The bulk state write: read the freshest persisted node, apply the SHARED stamp field-set,
    /// mint the next version, and commit through the storage adapter with the post-commit
    /// change-feed publish (<see cref="StorageAdapterChangeFeedExtensions.WriteAndPublishUpdated"/>).
    /// Best-effort by design: a failed stamp is logged loudly but never fails the bake — the
    /// bytes are already on the share, and the level-triggered probe / lazy kickoffs self-heal
    /// the record on the next pass (the same restartability rule as an interrupted bake).
    /// </summary>
    private static IObservable<Unit> WriteStamp(
        IMessageHub mesh,
        MeshNode typeNode,
        bool ok,
        NodeCompilationResult? result,
        Exception? error,
        string? releasePath,
        ILogger? logger)
    {
        var storage = mesh.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
        {
            logger?.LogWarning(
                "BatchBake: no IStorageAdapter on the mesh hub — compile-state stamps for "
                + "{TypePath} were NOT written (lazy activation will recompile)", typeNode.Path);
            return Observable.Return(Unit.Default);
        }
        var changeFeed = mesh.ServiceProvider.GetService<IMeshChangeFeed>();
        var options = mesh.JsonSerializerOptions;

        return storage
            .Read(typeNode.Path, options)
            .Take(1)
            .Select(fresh => fresh ?? typeNode)
            .Select(fresh =>
            {
                var def = fresh.ContentAs<NodeTypeDefinition>(options, logger);
                if (def is null)
                {
                    logger?.LogWarning(
                        "BatchBake: {TypePath} content could not be read as NodeTypeDefinition — "
                        + "stamp skipped", typeNode.Path);
                    return null;
                }
                var stamped = ok
                    // currentNodeVersion = typeNode.Version — the version the compiler's
                    // IAssemblyStore upload used (it uploaded the node it was handed), so
                    // LastCompiledVersion always names a store key that has bytes.
                    ? NodeTypeCompilationHelpers.ApplyCompileSuccess(
                        def, result!, typeNode.Version, activityPath: null, releasePath)
                    : NodeTypeCompilationHelpers.ApplyCompileFailure(
                        def, result, error, activityPath: null);
                return fresh with
                {
                    Content = stamped,
                    Version = MeshNode.NextVersion(fresh.Version)
                };
            })
            .SelectMany(stampedNode => stampedNode is null
                ? Observable.Return(Unit.Default)
                : storage
                    .WriteAndPublishUpdated(stampedNode, options, changeFeed)
                    .Select(_ => Unit.Default))
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "BatchBake: failed to write compile-state stamps for {TypePath} — the "
                    + "assembly is on the share; the next probe or lazy activation self-heals "
                    + "the record", typeNode.Path);
                return Observable.Return(Unit.Default);
            });
    }
}
