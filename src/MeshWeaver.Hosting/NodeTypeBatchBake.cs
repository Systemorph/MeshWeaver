using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Diagnostics;
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
    /// The EXPLICIT ceiling every discovery query carries — and the reason issue #1216 can never
    /// recur silently.
    ///
    /// <para><b>What went wrong.</b> Discovery used to issue a bare
    /// <c>MeshQueryRequest.FromQuery("nodeType:Code")</c>, which leaves both
    /// <see cref="MeshQueryRequest.Limit"/> and <see cref="ParsedQuery.Limit"/> null. "No limit"
    /// is NOT read the same way by every backend: the in-memory
    /// <c>StorageAdapterMeshQueryProvider</c> (<c>LoadCap</c>) and the per-schema
    /// <c>PostgreSqlMeshQuery</c> treat it as UNBOUNDED, but the cross-schema fan-out that serves
    /// an UNPINNED query on Postgres substitutes a hard default of 50
    /// (<c>PostgreSqlCrossSchemaQueryProvider.QueryAcrossSchemasAsync</c>). <c>nodeType:Code</c>
    /// carries no <c>namespace:</c>/<c>path:</c>, so it is exactly that unpinned shape — and on
    /// memex-cloud 2026-08-11 the batch resolved 50 Code nodes out of thousands and compiled 169
    /// of 237 types against NOTHING. A test mesh has fewer than 50 Code nodes, so the cap never
    /// bound and every test stayed green.</para>
    ///
    /// <para><b>Why a number and not "unbounded".</b> Unbounded is not honoured uniformly (above),
    /// and it cannot be reached by paging either: <c>public.search_across_schemas</c> takes a LIMIT
    /// but no OFFSET, and its <c>ORDER BY n.last_modified DESC</c> is not a total order, so a
    /// Skip/Limit loop over it would silently drop and duplicate rows across pages — worse than one
    /// big page. So discovery states its ceiling EXPLICITLY (every backend honours an explicit
    /// <c>limit:</c>) and then ASSERTS it was not reached. A mesh that genuinely exceeds this many
    /// Code nodes gets a loud discovery failure and the activation-driven sweep — slower, correct,
    /// and impossible to mistake for a content verdict.</para>
    /// </summary>
    internal const int SourceDiscoveryLimit = 25_000;

    /// <summary>
    /// The global fetches whose UNION is every Code node on the mesh. Three queries, not one —
    /// and the reason is the second half of issue #1216, invisible until this ran against a real
    /// partitioned Postgres mesh.
    ///
    /// <para>🚨 <c>nodeType:Code</c> ALONE CANNOT SEE MOST CODE NODES. On the partitioned backend a
    /// node's table is chosen by PATH SEGMENT: anything under a <c>Source/</c> or <c>Test/</c>
    /// segment is written to the per-schema <c>code</c> satellite table, and everything else to
    /// <c>mesh_nodes</c>. Resolving a table from a <c>nodeType:</c> filter is a DIFFERENT map, and
    /// <c>Code</c> is deliberately absent from it — <see cref="SatelliteTableMapping.Defaults"/>
    /// states it outright: "<c>Source</c>/<c>Test</c> are primary code content sharing the
    /// <c>code</c> table (no leading underscore — matched as a path segment, and not
    /// nodeType-resolvable)". So an unpinned <c>nodeType:Code</c> query fans out over every
    /// schema's <c>mesh_nodes</c> and returns ZERO of the Code nodes that live where Code nodes
    /// actually live. Measured on a two-partition PG mesh holding 60 Code nodes: <c>nodeType:Code</c>
    /// returned 2 (both from the static catalog, none from Postgres), while
    /// <c>namespace:*/Source scope:subtree nodeType:Code</c> returned all 60.</para>
    ///
    /// <para>That is what turned "the limit truncated the set" into "169 of 237 types resolved
    /// NOTHING" in production: the cap explains a PARTIAL set, never an empty one. The wildcard
    /// namespace shapes route through <c>PostgreSqlPartitionedMeshQuery.ResolveTable</c>'s
    /// wildcard-namespace branch, which reads the satellite segment out of the <c>namespace LIKE</c>
    /// value and lands on <c>code</c>. The union stays O(1) in the number of pending types — three
    /// queries regardless of whether the mesh has 3 types or 300 — which is the whole point of the
    /// batched pass, and each one is bounded and truncation-checked by <see cref="RunQuery"/>.</para>
    ///
    /// <para>Overlap is free: the results fold into one path-keyed map, and the in-memory matcher
    /// then selects each type's set from it exactly as before.</para>
    ///
    /// <para>🚨 <b>The union's COMPLETENESS is what makes the in-memory match sound</b>, and it rests
    /// on <c>scope:subtree</c> meaning the same thing for a WILDCARD namespace as for a concrete
    /// one. It did not, and that was the residual half of #1216: the wildcard form cannot become a
    /// <c>ParsedQuery.Path</c>, so it is emitted as a <c>namespace LIKE '%/Source'</c> filter and the
    /// scope — which only ever drove a PATH walk — was silently dropped. The fetch then reached only
    /// the Code nodes sitting DIRECTLY in a <c>…/Source</c> folder, and a type whose sources include
    /// a cross-partition <c>shared=@Other/SampleData/Source/Fixtures</c> resolved a PARTIAL set: its
    /// own sources present, that one absent, a convincing <c>CS0103</c> out of Roslyn, and the bake
    /// gate refusing readiness on healthy content. A partial set is strictly worse than an empty
    /// one — the emptiness invariant below cannot see it. <c>QueryParser</c> now widens the wildcard
    /// namespace to self-or-below (<c>WidenWildcardNamespacesToSubtree</c>), which is what lets the
    /// matcher keep serving these queries from the global map instead of re-running them.</para>
    /// </summary>
    private static IReadOnlyList<string> GlobalCodeQueries =>
    [
        // mesh_nodes across every schema, plus every non-Postgres provider (the static catalog).
        $"nodeType:{CodeNodeType.NodeType}",
        // The `code` satellite table across every schema — where a Source/ or Test/ segment puts a
        // Code node, and therefore where nearly all of them are. These match on NAMESPACE, so they
        // cover every Code node BELOW such a folder but not one whose own last segment IS the word
        // (namespace `X/Y` for `X/Y/Source`); `CodeNodeSegmentNameValidator` forbids that name, which
        // is what makes "the union is every Code node" true by construction rather than by luck
        // (issue #1235, part 2).
        $"namespace:*/{CodeNodeType.SourceSubNamespace} scope:subtree nodeType:{CodeNodeType.NodeType}",
        $"namespace:*/{CodeNodeType.TestSubNamespace} scope:subtree nodeType:{CodeNodeType.NodeType}",
    ];

    /// <summary>
    /// ONE batched source-discovery pass for every pending type: instead of each compile issuing
    /// its own source queries (2 × N queries, each against a per-type synced-query cache that can
    /// stall for a 45s heartbeat), a fixed set of global fetches (<see cref="GlobalCodeQueries"/> —
    /// three, independent of the number of types) pulls every Code node once
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
        var perType = new List<PendingType>();
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
            perType.Add(new PendingType(
                typePath, matchable, exotic,
                // The invariant's subject: only a type that DECLARES source queries is required to
                // resolve a non-empty set. A type that relies on the DEFAULT queries may legitimately
                // own no Code at all (a Configuration-only type is still compilable).
                DeclaresSources: def?.Sources is { Count: > 0 },
                // The one independent witness for "genuinely zero matches": the type's OWN live
                // sources snapshot, maintained by its per-NodeType sources watcher and persisted on
                // the record. EXPLICITLY empty (Count 0, not null) is the #1204 deleted-content
                // shape; the same discriminator DynamicTypePreWarmer.ClassifyCompileFailure uses.
                SourcesKnownDeleted: def?.CurrentSourceVersions is { Count: 0 }));
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
                        ? GlobalCodeQueries
                            .Select(q => RunQuery(meshService, q, logger))
                            .Concat()
                            .Aggregate(
                                ImmutableDictionary<string, MeshNode>.Empty
                                    .WithComparers(StringComparer.OrdinalIgnoreCase),
                                (all, page) => all.SetItems(page))
                        : Observable.Return(ImmutableDictionary<string, MeshNode>.Empty
                            .WithComparers(StringComparer.OrdinalIgnoreCase));

                    // Exotic queries run AFTER the global fetch, one at a time (Concat) — the
                    // rare escape hatch must not fan out against a cold mesh.
                    var exoticFetch = exoticQueries.Count == 0
                        ? Observable.Return(ImmutableDictionary<string, ImmutableDictionary<string, MeshNode>>.Empty)
                        : exoticQueries
                            .Select(q => RunQuery(meshService, q, logger).Select(nodes => (Query: q, Nodes: nodes)))
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
            {
                // 🚨 A WILDCARD location is not mirrorable (issue #1235). The matcher is
                // CodeQueryResolver.Matches, which compares locations with a literal
                // path.StartsWith(ns + "/") — it has no glob at all, so `namespace:*/Source`
                // would match NOTHING and the type would resolve a silently partial source
                // set. That is the #1216 failure shape, and the shape this gate exists to
                // prevent: run it against the mesh instead, where the wildcard is understood.
                if (QueryWildcard.ContainsWildcard(value))
                    return false;
                hasLocation = true;
            }
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
    /// One pending type's discovery inputs, plus the two facts the source-set invariant needs.
    /// </summary>
    /// <param name="TypePath">The NodeType's mesh path.</param>
    /// <param name="Matchable">Expanded queries the in-memory matcher provably mirrors.</param>
    /// <param name="Exotic">Expanded queries that must run against the mesh individually.</param>
    /// <param name="DeclaresSources">The type declares its OWN source queries (not the defaults).</param>
    /// <param name="SourcesKnownDeleted">
    /// The type's own persisted source snapshot is EXPLICITLY empty — independent corroboration
    /// that "no matches" is the mesh's real answer rather than a broken discovery pass.
    /// </param>
    private sealed record PendingType(
        string TypePath,
        IReadOnlyList<string> Matchable,
        IReadOnlyList<string> Exotic,
        bool DeclaresSources,
        bool SourcesKnownDeleted);

    /// <summary>
    /// Raised when the batched discovery pass cannot be TRUSTED to have established the source
    /// sets. It fails the WHOLE batch on purpose: <see cref="DynamicTypePreWarmer"/> catches it and
    /// falls back to the activation-driven sweep for the entire run.
    ///
    /// <para>🚨 The alternative — letting a starved pass produce per-type verdicts — is issue #1216.
    /// An empty source set classifies as <see cref="PreWarmStatus.NoSources"/>, which is
    /// DELIBERATELY non-gating (it means "the content was deleted", #1204), so a discovery bug wore
    /// the costume of content breakage: 169 of 237 types "content-broken", nothing refusing
    /// readiness, and on an ungated pod a fleet of empty assemblies would have been baked and
    /// stamped as good. A verdict about code requires a source set you actually established;
    /// anything less is "I don't know", and "I don't know" must never look like an answer.</para>
    /// </summary>
    internal sealed class SourceDiscoveryFailedException(string message) : Exception(message);

    /// <summary>
    /// One mesh query, chunk-accumulated to a settled path→node map: fold every
    /// <see cref="QueryResultChange{T}"/> (the compiler's own pure fold,
    /// <see cref="MeshNodeCompilationService.ApplyQueryChange"/>) and treat a
    /// <see cref="QueryQuietWindow"/> of silence as completion — a bare <c>Take(1)</c> on a
    /// chunked Initial would hand the compiler a PARTIAL source set, which compiles wrong.
    ///
    /// <para>🚨 The query carries an EXPLICIT <see cref="SourceDiscoveryLimit"/> — stated in the
    /// query TEXT as well as on the request, because the Postgres cross-schema fan-out reads only
    /// <see cref="ParsedQuery.Limit"/> and never <see cref="MeshQueryRequest.Limit"/> — and the
    /// result is then CHECKED against it. Reaching the ceiling means the set may be truncated, and
    /// a truncated source set compiles wrong in the most convincing possible way (see #1218: a
    /// starved source set produces a genuine-looking CS0246 that the bake gate reads as an image
    /// regression). So a full page is a discovery FAILURE, never a result.</para>
    /// </summary>
    private static IObservable<ImmutableDictionary<string, MeshNode>> RunQuery(
        IMeshService meshService, string query, ILogger? logger)
    {
        // Last `limit:` wins in QueryParser, so appending overrides an author-supplied one. That is
        // intended: a `limit:` inside a NodeType's source query truncates the set the compiler is
        // handed, which is never what the author meant, and the ceiling below is far larger anyway.
        if (query.Contains("limit:", StringComparison.OrdinalIgnoreCase))
            logger?.LogWarning(
                "BatchBake: source query '{Query}' carries its own limit: — discovery overrides it "
                + "with {Limit}, because a truncated source set compiles WRONG", query, SourceDiscoveryLimit);
        var bounded = $"{query} limit:{SourceDiscoveryLimit}";
        return Observable
            // 🚨 .AsSystem() — batch-bake source discovery is framework infrastructure, not a
            // user-scoped read, and this Defer's subscription does not carry the caller's ambient
            // identity. Unstamped it would resolve as Anonymous and hand the compiler a silently
            // TRUNCATED source set, which "compiles WRONG" exactly as the limit: note above warns.
            // Declared on the request so no scheduler hop can lose it. See
            // Doc/Architecture/QueryIdentity.
            .Defer(() => meshService.Query<MeshNode>(new MeshQueryRequest
            {
                Query = bounded,
                Limit = SourceDiscoveryLimit,
            }.AsSystem()))
            .Scan(
                ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
                MeshNodeCompilationService.ApplyQueryChange)
            .Throttle(QueryQuietWindow)
            .Take(1)
            .SelectMany(nodes => nodes.Count < SourceDiscoveryLimit
                ? Observable.Return(nodes)
                : Observable.Throw<ImmutableDictionary<string, MeshNode>>(
                    new SourceDiscoveryFailedException(
                        $"source discovery query '{bounded}' returned {nodes.Count} node(s) — its own "
                        + $"ceiling of {SourceDiscoveryLimit}, so the source set may be TRUNCATED and "
                        + "no compile driven from it would be a verdict about the code")));
    }

    /// <summary>
    /// Select each type's source set from the batched fetches, then ASSERT the pass actually
    /// established them (see <see cref="SourceDiscoveryFailedException"/>).
    /// </summary>
    private static ImmutableDictionary<string, IReadOnlyList<MeshNode>> Assemble(
        IReadOnlyList<PendingType> perType,
        ImmutableDictionary<string, MeshNode> allCode,
        ImmutableDictionary<string, ImmutableDictionary<string, MeshNode>> exoticResults,
        ILogger? logger)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, IReadOnlyList<MeshNode>>(
            StringComparer.OrdinalIgnoreCase);
        // Types whose DECLARED source queries resolved to NOTHING and whose own persisted snapshot
        // does not corroborate that. Discovery cannot tell such a type apart from a broken pass, so
        // it refuses to answer for the whole batch rather than guess per type.
        var unestablished = new List<string>();
        foreach (var pending in perType)
        {
            var matched = new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase);
            if (pending.Matchable.Count > 0)
                foreach (var (path, node) in allCode)
                    if (CodeQueryResolver.Matches(path, pending.Matchable))
                        matched[path] = node;
            foreach (var q in pending.Exotic)
                if (exoticResults.TryGetValue(q, out var nodes))
                    foreach (var (path, node) in nodes)
                        matched[path] = node;

            if (matched.Count == 0 && pending.DeclaresSources && !pending.SourcesKnownDeleted)
                unestablished.Add(pending.TypePath);

            // Deterministic order: the compiler concatenates the files into one syntax tree, so
            // any order compiles identically — but a stable order keeps artifacts reproducible.
            builder[pending.TypePath] = matched.Values
                .OrderBy(n => n.Path, StringComparer.Ordinal)
                .ToList();
        }

        logger?.LogInformation(
            "BatchBake: source discovery resolved {CodeNodes} Code node(s) into source sets for "
            + "{Types} pending type(s) in one pass",
            allCode.Count, builder.Count);

        if (unestablished.Count > 0)
        {
            // 🚨 LOUD, and naming every type — this is the line that would have made #1216 obvious
            // on the first production run instead of reading as "169 types are content-broken".
            logger?.LogWarning(
                "BatchBake: source discovery resolved an EMPTY source set for {Count} type(s) that "
                + "DECLARE source queries, and none of them records an explicitly-empty source "
                + "snapshot to corroborate it: {Types}. That is a DISCOVERY failure, not a content "
                + "verdict — abandoning the batch for the activation-driven sweep rather than "
                + "compiling anything against nothing.",
                unestablished.Count, string.Join(", ", unestablished));
            throw new SourceDiscoveryFailedException(
                $"{unestablished.Count} pending type(s) declare source queries that resolved to an "
                + $"EMPTY source set with no corroborating empty snapshot: {string.Join(", ", unestablished)}");
        }

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
        // PER-COMPILE cost, appended to the per-type line that already exists — no new log volume.
        // This is the measurement that answers "is compilation what eats the memory?" directly rather
        // than by elimination: one linked bake of 279 types left the pod at 2.5 GB, so the fleet is
        // cheap in aggregate, but nothing reported what an INDIVIDUAL compile cost or whether it was
        // returned. A type that repeatedly costs tens of MB and never gives them back is visible here
        // and nowhere else. See MemoryDelta.
        var compileMemory = MemoryDelta.Start();
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
                "BatchBake: {TypePath} → {Status}{Detail} — {Memory}",
                o.TypePath, o.Status,
                string.IsNullOrEmpty(o.Detail) ? "" : $" ({o.Detail})",
                compileMemory));
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

                // 🚨 The compile never RAN because its source set could not be established — an
                // availability failure, not a verdict about the code (issue #1218). It reports
                // TimedOut, which the gate files under "not evaluated"; the stamp above already
                // recorded CompilationStatus.Unavailable for the same reason. The batch driver
                // hands the compiler a pre-resolved snapshot, so this normally cannot arise here
                // — but the status vocabulary must not depend on WHICH driver ran the compile.
                if (error is SourceDiscoveryUnavailableException)
                    return new PreWarmOutcome(typePath, PreWarmStatus.TimedOut, error.Message);

                // Same classification the activation path applies
                // (DynamicTypePreWarmer.ClassifyCompileFailure): a compile whose source set
                // resolved EMPTY is broken by CONTENT (its sources were deleted from the mesh) —
                // NoSources, which must never gate a rollout.
                //
                // 🚨 BOTH witnesses are required (#1216). The batch's own resolved snapshot alone is
                // NOT sufficient authority: when discovery is starved or truncated it also reports
                // "no sources", and NoSources does not gate — so a discovery bug would quietly
                // reclassify itself as deleted content on an ungated pod and bake empty assemblies.
                // The type's persisted CurrentSourceVersions is the independent corroboration, and
                // ResolveSources already refuses to produce a set at all when it is missing; this
                // check keeps the classification honest even if a future caller bypasses that.
                //
                // 🚨 What is NOT required — and its removal is the fix for #1391 — is that the type
                // DECLARE its source queries. An empty NodeTypeDefinition.Sources means "uses the
                // DEFAULT {path}/Source query", which is how nearly every NodeType is authored, so
                // demanding declared queries made NoSources unreachable for almost the whole
                // population and turned every deleted-source type into a gating image verdict.
                //
                // 🚨 What REPLACES it is LastCompileSucceededAt: the sources must have been LOST,
                // not merely absent. A type that never built cannot have lost anything — its empty
                // snapshot is its normal state and a failure is a defect in its own Configuration,
                // which must keep gating (and must keep cascading UpstreamFailed to its dependents).
                // See ClassifyCompileFailure for the full reasoning; the two paths must agree,
                // because the status vocabulary may not depend on WHICH driver ran the compile.
                var def = typeNode.ContentAs<NodeTypeDefinition>(mesh.JsonSerializerOptions);
                var status = def is not null
                        && sources.Count == 0
                        && def.CurrentSourceVersions is { Count: 0 }
                        && def.LastCompileSucceededAt is not null
                    ? PreWarmStatus.NoSources
                    : PreWarmStatus.CompileError;
                return new PreWarmOutcome(
                    typePath, status, NodeTypeCompilationHelpers.SummarizeCompileError(result, error))
                {
                    // Same torn-snapshot evidence the activation path carries (#1214): whether the
                    // type's sources moved while this compile ran. Read off the type record, which
                    // holds both the compile-start stamp and the live source snapshot.
                    SourcesMovedDuringCompile =
                        def is not null && DynamicTypePreWarmer.SourcesMovedDuringCompile(def)
                };
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
            // The durable version the stamp is computed FROM — 0 when there is no row at all, which
            // the compare-and-set below reads as "insert only if absent". Carried explicitly rather
            // than derived from stampedNode.Version - 1, which would silently rot if the mint
            // changed.
            .Select(stored => (Expected: stored?.Version ?? 0, Fresh: stored ?? typeNode))
            .Select(read =>
            {
                var fresh = read.Fresh;
                var def = fresh.ContentAs<NodeTypeDefinition>(options, logger);
                if (def is null)
                {
                    logger?.LogWarning(
                        "BatchBake: {TypePath} content could not be read as NodeTypeDefinition — "
                        + "stamp skipped", typeNode.Path);
                    return (Expected: read.Expected, Stamped: (MeshNode?)null);
                }
                var stamped = ok
                    // currentNodeVersion = typeNode.Version — the version the compiler's
                    // IAssemblyStore upload used (it uploaded the node it was handed), so
                    // LastCompiledVersion always names a store key that has bytes.
                    ? NodeTypeCompilationHelpers.ApplyCompileSuccess(
                        def, result!, typeNode.Version, activityPath: null, releasePath)
                    : NodeTypeCompilationHelpers.ApplyCompileFailure(
                        def, result, error, activityPath: null);
                return (Expected: read.Expected, Stamped: (MeshNode?)(fresh with
                {
                    Content = stamped,
                    Version = MeshNode.NextVersion(fresh.Version)
                }));
            })
            .SelectMany(stamp => stamp.Stamped is not { } stampedNode
                ? Observable.Return(Unit.Default)
                : storage
                    // 🚨 COMPARE-AND-SET, not a plain write (#1424). The other writer here is the
                    // type's OWN per-node hub — not a baker at all: it stamps compile state from the
                    // activation-driven path, the sources watcher and the release watcher, and it is
                    // legitimately live while a batch bake runs. So the read-modify-write above stays
                    // racy by construction, and the single-baker claim does NOT close it (the claim
                    // excludes second bakers; this writer is not one).
                    //
                    // What the plain write got WRONG is what happened when it lost. Both writers read
                    // the row at v and both mint v+1, and the store's monotonic condition applies at
                    // EQUAL versions — re-persisting an unchanged node is a legitimate shape — so both
                    // writes landed, last-write-wins, and the refusal branch below could never fire
                    // (saved.Version was equal, never strictly greater). One stamp was silently
                    // destroyed and the log that was supposed to say so stayed quiet. Conditioning on
                    // the version we READ makes the loser deterministic and LOUD.
                    //
                    // The loser's outcome is unchanged and still correct: the bytes are durable and
                    // content-addressed, the record is left pending, and the level-triggered probe
                    // re-bakes the type on the next pass (NodeTypeBakeStatus.Probe asks the STORE, not
                    // the record). Deliberately NO compare-and-re-apply loop: that would be a second
                    // write path into a record this driver does not own, which is the shape that
                    // produced the racing writers in the first place.
                    .WriteIfVersion(stampedNode, stamp.Expected, options)
                    .Do(applied =>
                    {
                        if (applied is false)
                        {
                            logger?.LogWarning(
                                "BatchBake: compile-state stamp for {TypePath} at Version={Written} "
                                + "was REFUSED — the durable row moved past Version={Expected} while "
                                + "the compile ran (a live owner hub is writing this record). The "
                                + "assembly is on the share; the next probe re-bakes and re-stamps it.",
                                typeNode.Path, stampedNode.Version, stamp.Expected);
                            return;
                        }
                        // Post-commit announce, exactly as WriteAndPublishUpdated did — the
                        // mesh-change feed is what invalidates the resolution / stream caches.
                        if (applied is true)
                            changeFeed?.Publish(MeshChangeEvent.Updated(stampedNode));
                    })
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
