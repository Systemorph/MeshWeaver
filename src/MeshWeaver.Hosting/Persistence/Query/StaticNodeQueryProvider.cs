using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// An IMeshQueryProvider that serves static nodes (e.g., built-in roles, type definitions)
/// from IStaticNodeProvider instances and MeshConfiguration.Nodes.
/// Provider nodes (roles) bypass path/scope checks (they are global).
/// Configuration nodes (type definitions) respect path/scope and context filtering.
/// Every served row that is not a definition (<see cref="IsDeclaredVocabulary"/>) is row-filtered
/// for the query's viewer exactly as a stored row is.
///
/// <para><b>Lifetime.</b> The provider subscribes to the module registry's
/// <see cref="MeshWeaver.Mesh.ModuleContexts.VersionChanged"/> and OWNS that subscription: it is disposed
/// with the provider, which the mesh's container disposes when the mesh ends (it builds the provider, so
/// it owns it). A subscription left on the registry kept the provider — and everything it references —
/// reachable for as long as the registry was (MeshWeaver#7373).</para>
/// </summary>
public class StaticNodeQueryProvider : IMeshQueryProvider, IDisposable
{
    /// <summary>
    /// Default <see cref="Matches"/> predicate: union of the providers' static
    /// node first-segments + the seed nodes' first-segments. Computed once at
    /// registration time so the resulting lambda is closed over the
    /// partition set (data-driven, no hard-coded namespace list).
    ///
    /// <para>Accepts the query when any of the pre-extracted
    /// <c>queryNamespaces</c> is in our segment set. Unscoped
    /// queries are pre-filtered by the aggregator (every provider
    /// participates) so this predicate is only consulted for scoped ones.</para>
    /// </summary>
    public static Func<IReadOnlyList<string>, bool> BuildDefaultMatches(
        IEnumerable<IStaticNodeProvider> providers,
        MeshConfiguration? meshConfiguration)
    {
        var firstSegments = providers
            .SelectMany(p => p.GetStaticNodes())
            .Concat((meshConfiguration?.AddMeshNodesList ?? Enumerable.Empty<MeshNode>()))
            .Select(n => n.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!.Split('/', 2)[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return queryNamespaces =>
        {
            for (var i = 0; i < queryNamespaces.Count; i++)
                if (firstSegments.Contains(queryNamespaces[i]))
                    return true;
            return false;
        };
    }

    // 🚨 The catalog is a SNAPSHOT taken from the providers, and it is re-taken whenever a module
    // generation is swapped live (policy module-live-update-default): a module's static nodes are
    // served from its CURRENT generation, and a snapshot taken once at construction would answer
    // queries with the generation that booted — stale data, and a reference that pins it.
    private sealed record Catalog(long Version, MeshNode[] ProviderNodes, MeshNode[] ConfigNodes, MeshNode[] AllNodes, HashSet<string> NodeTypes);
    private Catalog? catalog;
    private readonly IList<IStaticNodeProvider> _providerList;
    private readonly MeshWeaver.Mesh.ModuleContexts? _modules;
    // The registry subscription this provider owns — ended in Dispose (MeshWeaver#7373).
    private IDisposable? _versionChanged;

    private Catalog Current
    {
        get
        {
            var version = _modules?.Version ?? 0;
            var snapshot = Volatile.Read(ref catalog);
            if (snapshot is not null && snapshot.Version == version)
                return snapshot;
            var built = Build(version);
            Volatile.Write(ref catalog, built);
            return built;
        }
    }

    // Provider nodes (from IStaticNodeProvider) — global, no path/scope check
    private MeshNode[] _providerNodes => Current.ProviderNodes;
    // Config nodes (from MeshConfiguration.Nodes) — respect path/scope/context
    private MeshNode[] _configNodes => Current.ConfigNodes;
    // All nodes combined for SelectAsync/nodeType index
    private MeshNode[] _allNodes => Current.AllNodes;
    private HashSet<string> _nodeTypes => Current.NodeTypes;
    private readonly MeshConfiguration? _meshConfiguration;
    private readonly IEnumerable<Lazy<INodeValidator>>? _nodeValidators;
    private readonly AccessService? _accessService;
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    // Caller-supplied predicate — keeps partition routing data-driven (the
    // registration site declares which namespaces this provider owns, via a
    // lambda or the partition registry) rather than hard-coded here.
    private readonly Func<IReadOnlyList<string>, bool> _matches;

    /// <inheritdoc/>
    public bool Matches(IReadOnlyList<string> queryNamespaces) => _matches(queryNamespaces);

    /// <summary>
    /// Creates the static-node query provider over the supplied static node
    /// sources and seed configuration, pre-computing the provider/config node
    /// buckets and the node-type index used to short-circuit non-matching
    /// queries.
    /// </summary>
    /// <param name="providers">The static node providers (built-in roles, type definitions, etc.).</param>
    /// <param name="matches">Predicate deciding whether a scoped query's namespaces are owned by this provider.</param>
    /// <param name="meshConfiguration">Optional mesh configuration supplying the autocomplete /
    /// context-exclusion rules; may be <see langword="null"/>. The seed (<c>AddMeshNodes</c>) nodes
    /// come from <paramref name="providers"/> — <c>MeshBuilder</c> registers them as an
    /// <see cref="IStaticNodeProvider"/> — so ONE resolution decides who serves a contested path
    /// (<c>StaticNodeProviderExtensions.ResolveStaticNodes</c>, MeshWeaver#2908).</param>
    /// <param name="loggerFactory">Optional logger factory; diagnostics are suppressed when <see langword="null"/>.</param>
    public StaticNodeQueryProvider(
        IEnumerable<IStaticNodeProvider> providers,
        Func<IReadOnlyList<string>, bool> matches,
        MeshConfiguration? meshConfiguration = null,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : this(providers, matches, meshConfiguration, loggerFactory, null)
    {
    }

    /// <summary>As the four-argument constructor, re-taking the catalog whenever a module generation
    /// in <paramref name="modules"/> is swapped live.</summary>
    public StaticNodeQueryProvider(
        IEnumerable<IStaticNodeProvider> providers,
        Func<IReadOnlyList<string>, bool> matches,
        MeshConfiguration? meshConfiguration,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory,
        MeshWeaver.Mesh.ModuleContexts? modules)
        : this(providers, matches, meshConfiguration, loggerFactory, modules, null, null)
    {
    }

    /// <summary>
    /// As the five-argument constructor, row-filtering every declared node that is not a definition
    /// (<see cref="IsDeclaredVocabulary"/>) through <paramref name="nodeValidators"/> for the viewer
    /// of each query, exactly as the storage providers filter a stored row. See <see cref="Query{T}"/>.
    /// </summary>
    /// <param name="providers">The static node providers.</param>
    /// <param name="matches">Predicate deciding whether a scoped query's namespaces are owned by this provider.</param>
    /// <param name="meshConfiguration">Optional mesh configuration.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="modules">Module generations; the catalog is re-taken when one is swapped.</param>
    /// <param name="nodeValidators">The mesh's node validators (row-level security among them),
    /// resolved LAZILY for the same reason the storage provider does — the RLS validator depends on
    /// the security service, whose warm-up queries through this provider.</param>
    /// <param name="accessService">The ambient identity, consulted only when a query names no viewer.</param>
    public StaticNodeQueryProvider(
        IEnumerable<IStaticNodeProvider> providers,
        Func<IReadOnlyList<string>, bool> matches,
        MeshConfiguration? meshConfiguration,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory,
        MeshWeaver.Mesh.ModuleContexts? modules,
        IEnumerable<Lazy<INodeValidator>>? nodeValidators,
        AccessService? accessService)
    {
        _nodeValidators = nodeValidators;
        _accessService = accessService;
        _meshConfiguration = meshConfiguration;
        _logger = loggerFactory?.CreateLogger<StaticNodeQueryProvider>();
        _matches = matches;
        _providerList = providers as IList<IStaticNodeProvider> ?? providers.ToList();
        _modules = modules;
        catalog = Build(_modules?.Version ?? 0);
        // 🚨 LOUD, once per mesh: two providers claiming one path with DIFFERENT content means one
        // of them is being dropped. Registration order is not something a host controls, so
        // "append a node at the platform's path to override it" is not a supported pattern — and
        // before this warning existed it looked like one, because the append was accepted and only
        // half-honoured (MeshWeaver#2908).
        // Emitted HERE, at construction, and never from Build: Build re-runs after every live module
        // swap, so a gate inside it cannot tell the first build from a rebuild.
        if (_logger is not null)
            foreach (var collision in StaticNodeProviderExtensions.DescribeStaticProviderCollisions(_providerList))
                _logger.LogWarning("[StaticNodeQueryProvider] {Collision}", collision);

        // Dropped the moment a module generation changes — never held until the next query, which
        // would keep the swapped-out generation's nodes, and so the generation, alive.
        // 🚨 OWNED, and ended in Dispose (MeshWeaver#7373): a subject keeps every observer it was handed
        // until the observer unsubscribes, so a discarded subscription rooted this provider — and its
        // validators, and through them a hub — in the registry for as long as the registry lived.
        _versionChanged = _modules?.VersionChanged.Subscribe(_ => Volatile.Write(ref catalog, null));
    }

    /// <summary>Whether <see cref="Dispose"/> has ended the registry subscription.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private int _disposed;

    /// <summary>
    /// Ends the subscription to module swaps and drops the catalog — called by the mesh's container when
    /// the mesh ends. Idempotent: the provider is reachable under two registrations (itself and
    /// <see cref="IMeshQueryProvider"/>), and each may hand it back for disposal.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Interlocked.Exchange(ref _versionChanged, null)?.Dispose();
        Volatile.Write(ref catalog, null);
        GC.SuppressFinalize(this);
    }

    private Catalog Build(long version)
    {
        var providerList = _providerList;

        // 🚨 The AddMeshNodes-seed-wins rule is NOT implemented here. It lives in
        // StaticNodeProviderExtensions.ResolveStaticNodeBuckets, which is also what
        // FindServedStaticNode / FindStaticNode resolve through — that shared call is the whole
        // point (MeshWeaver#2908): a second static node at a path another contributor already
        // claimed used to be served by this reader and NOT by hub activation's, silently, because
        // each seam applied its own rule. The buckets stay separate because the two are treated
        // differently BELOW (seed nodes are registration declarations, suppressed under
        // context:search / is:content); who WINS a contested path is decided in one place.
        //
        // Seed priority also keeps a bridged copy of a seed node out of the provider bucket, which
        // has no isSearch guard (caught by
        // StaticNodeQueryContextTests.SearchContext_ExcludesStaticNodes).
        var (seedNodes, providedNodes) =
            StaticNodeProviderExtensions.ResolveStaticNodeBuckets(providerList);

        // Definition-only nodes (a DB-synced NodeType catalog's in-memory type-def) are NOT
        // queryable — Postgres owns the runtime node at their path. Excluding them keeps the
        // bare partition path (e.g. path:Harness) resolving to exactly the PG nodeType:NodeType
        // root, with no second claimant. They still CLAIMED their path in the resolution above,
        // so no lower-precedence provider slips a served node underneath them.
        // See Doc/Architecture/NodeTypeCatalogs.md.
        var providerNodes = providedNodes.Where(n => !n.IsDefinitionOnly).ToArray();
        _logger?.LogDebug(
            "[StaticNodeQueryProvider] ctor: {Providers} provider(s) -> {Count} nodes; byType=[{ByType}]; byNamespace(top)=[{ByNs}]",
            providerList.Count,
            providerNodes.Length,
            string.Join(", ", providerNodes.GroupBy(n => n.NodeType ?? "(null)").Select(g => $"{g.Key}={g.Count()}")),
            string.Join(", ", providerNodes.GroupBy(n => n.Namespace ?? "(null)").OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}={g.Count()}")));

        // See _providerNodes: a definition-only catalog type-def is never a query result.
        var configNodes = seedNodes.Where(n => !n.IsDefinitionOnly).ToArray();

        var allNodes = providerNodes.Concat(configNodes).ToArray();

        var nodeTypes = new HashSet<string>(
            allNodes
                .Where(n => !string.IsNullOrEmpty(n.NodeType))
                .Select(n => n.NodeType!),
            StringComparer.OrdinalIgnoreCase);
        return new Catalog(version, providerNodes, configNodes, allNodes, nodeTypes);
    }

    /// <summary>
    /// Reactive autocomplete — <b>fully synchronous</b>. The static catalog is purely in-memory and
    /// never performs I/O, so it builds the snapshot and <c>Observable.Return</c>s it (no
    /// <c>Task.Run</c>, no <c>await</c>, no async-enumerable). This is the "in-memory never async"
    /// rule in action — see <c>Doc/Architecture/AggregatingProviders.md</c> → "The async boundary
    /// lives at the I/O edge".
    /// </summary>
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
        string? contextPath = null, string? context = null)
    {
        var providerName = ((IMeshQueryProvider)this).Name;
        // 🚨 The viewer is resolved NOW, on the caller's thread — the same instant and the same
        // AccessService StorageAdapterMeshQueryProvider.Autocomplete resolves it at — and every
        // declared row that is not a definition is row-filtered BEFORE the limit, so a
        // suggestion and a `search` of the same path agree: a caller who cannot read
        // Admin/_Access cannot autocomplete the platform admins out of it either (#6578).
        var viewer = QueryIdentityResolver.Resolve(
            new MeshQueryRequest { Query = string.IsNullOrEmpty(basePath) ? "" : $"path:{basePath}" },
            _accessService?.Context?.ObjectId ?? _accessService?.CircuitContext?.ObjectId).UserId;
        var candidates = ComputeSuggestions(basePath, prefix, contextPath, context);
        return FilterReadable(candidates, c => c.Node, viewer)
            .Select(visible => (IReadOnlyCollection<QueryResult>)Order(visible.Select(c => c.Suggestion), mode)
                .Take(limit)
                .Select(s => new QueryResult
                {
                    Path = s.Path,
                    Name = s.Name,
                    NodeType = s.NodeType,
                    Icon = s.Icon,
                    Score = s.Score,
                    ProviderName = providerName,
                })
                .ToList());
    }

    /// <summary>
    /// Synchronous suggestion computation backing the reactive <see cref="Autocomplete"/> override.
    /// Pure in-memory scan + score + order — no I/O, so no async.
    /// </summary>
    private List<(MeshNode Node, QuerySuggestion Suggestion)> ComputeSuggestions(
        string basePath, string prefix, string? contextPath, string? context)
    {
        var normalizedPrefix = (prefix ?? "").ToLowerInvariant();
        var suggestions = new List<(MeshNode Node, QuerySuggestion Suggestion)>();

        foreach (var node in _allNodes)
        {
            if (_meshConfiguration?.AutocompleteExcludedNodeTypes.Contains(node.NodeType ?? "") == true)
                continue;
            if (IsExcludedByContext(node, context))
                continue;

            // Check path scope: node must be a descendant of basePath (or basePath is empty)
            if (!string.IsNullOrEmpty(basePath))
            {
                var nodePath = node.Path ?? "";
                if (!nodePath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(nodePath, basePath, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var name = node.Name ?? node.Id ?? node.Path ?? "";
            double score = 0;

            if (string.IsNullOrEmpty(normalizedPrefix))
            {
                score = 1;
            }
            else if (name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 100 - (name.Length - normalizedPrefix.Length);
            }
            else if (name.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 50;
            }
            else if ((node.Path ?? "").Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 30;
            }
            else
            {
                continue;
            }

            score += PathProximity.ComputeBoost(contextPath, node.Path);
            suggestions.Add((node, new QuerySuggestion(node.Path ?? "", name, node.NodeType, score, node.Icon)));
        }

        return suggestions;
    }

    private static IEnumerable<QuerySuggestion> Order(IEnumerable<QuerySuggestion> suggestions, AutocompleteMode mode) =>
        mode switch
        {
            AutocompleteMode.RelevanceFirst => suggestions
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Path.Length)
                .ThenBy(s => s.Name),
            _ => suggestions
                .OrderBy(s => s.Path.Length)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.Name)
        };

    /// <inheritdoc />
    /// <remarks>
    /// 🚨 Every effective query, not just the first. A multi-query request
    /// (<see cref="MeshQueryRequest.FromQueries"/>) is a UNION — "matches A OR B OR …" — and
    /// <see cref="StorageAdapterMeshQueryProvider"/> has always iterated
    /// <see cref="MeshQueryRequest.EffectiveQueries"/> to build it. This provider read
    /// <c>request.Query</c>, which <c>FromQueries</c> fills with <c>list[0]</c>, so a static node
    /// matched only by the SECOND or later query was silently absent from the union — no error, no
    /// log, an answer that simply omits it. The asymmetry was invisible for as long as every union's
    /// static half happened to sit in query #1: the skill query set
    /// (<c>AgentPickerProjection.BuildSkillQueries</c>) puts the platform <c>Skill</c> catalog first
    /// and a package's own <c>{partition}/Skill</c> subtree second, so built-in skills resolved and a
    /// module-shipped one did not (MeshWeaver.Plugins — skills that ship with their module).
    /// </remarks>
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        // Static nodes are purely in-memory (no I/O) and never change.
        // Collect synchronously and return a completed Observable.Return — no async, no scheduler.
        var effective = request.EffectiveQueries;
        // The FIRST query owns sort / paging / scoring for the whole union — the documented
        // contract on MeshQueryRequest.Queries ("the others contribute membership only"), and the
        // one the storage provider already implements.
        var parsedQuery = _parser.Parse(effective.FirstOrDefault() ?? string.Empty);
        var items = CollectStaticResults<T>(parsedQuery, request.Context);
        // Union the remaining queries by PATH, which is the identity the request's own Items list
        // de-dupes on. A node matched by two queries must appear once.
        if (effective.Count > 1)
        {
            var seen = new HashSet<string>(
                items.OfType<MeshNode>().Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
            for (var q = 1; q < effective.Count; q++)
            {
                foreach (var item in CollectStaticResults<T>(_parser.Parse(effective[q]), request.Context))
                {
                    // A non-MeshNode projection has no path to key on; keep it (the single-query
                    // path never de-duped those either) rather than silently dropping it.
                    if (item is MeshNode node && !seen.Add(node.Path))
                        continue;
                    items.Add(item);
                }
            }
        }
        _logger?.LogDebug(
            "[StaticNodeQueryProvider] Query query='{Query}' parsedPath='{Path}' parsedNodeType='{NodeType}' -> {Count} item(s)",
            string.Join(" | ", effective), parsedQuery.Path ?? "(null)",
            (parsedQuery.Filter as MeshWeaver.Mesh.QueryComparison)?.Condition.Values is { } vs
                ? string.Join("|", vs) : "(complex)",
            items.Count);
        QueryResultChange<T> Initial(List<T> visible) => new()
        {
            ChangeType = QueryChangeType.Initial,
            Items = visible,
            // Score the matched items when the query carries a text-search term.
            // For pure filter / namespace / nodeType queries we leave Scores=null so
            // the aggregator falls back to insertion order — there's no relevance
            // signal to surface ("give me all Threads in this namespace" is
            // unordered with respect to score).
            Scores = ComputeScores(visible, parsedQuery),
            Query = parsedQuery,
            Version = 0,
            Timestamp = DateTimeOffset.UtcNow
        };

        // Rows decided per viewer are FOLLOWED for the life of the query (MeshWeaver#7365); everything
        // else is decided once and the query completes, exactly as before.
        var decided = items.OfType<MeshNode>().Where(RequiresRowFilter).ToList();
        if (_nodeValidators is null || AccessFollowServices is null || decided.Count == 0)
            return RowFilter(items, request).Select(Initial);
        var viewer = ResolveViewer(request);
        if (string.Equals(viewer, WellKnownUsers.System, StringComparison.Ordinal))
            return FilterReadable(items, item => item as MeshNode, viewer).Select(Initial);
        return FollowAccess(items, decided, viewer, parsedQuery, Initial);
    }

    /// <summary>
    /// 🚨 <b>An open query follows grant changes on its declared rows</b> (MeshWeaver#7365). The rows
    /// this provider serves live in memory and never change, but their READ verdict does: a grant or a
    /// revocation on an ancestor's <c>_Access</c>, a group membership, a role. This provider used to
    /// decide each declared row once, emit its <c>Initial</c> and complete, so a revoked reader kept the
    /// row for as long as the query stayed open and a newly granted reader never received it. Storage
    /// supplies no delta for a row it does not hold, so nothing downstream could correct it.
    ///
    /// <para>The re-check signals are the storage executor's (<see cref="SecuredQueryAccessFollow.RecheckTriggers"/>),
    /// anchored on the decided rows' own paths and MainNodes. Each signal re-runs the row filter (coalesced,
    /// one run at a time) and the result is diffed against what the reader holds: a revoked row leaves as
    /// <see cref="QueryChangeType.Removed"/>, a newly readable one arrives as <see cref="QueryChangeType.Added"/>,
    /// and a signal that changes nothing for this reader emits nothing. A fault in the signals ends the query —
    /// a listing that can no longer follow access must not go on serving rows.</para>
    /// </summary>
    private IObservable<QueryResultChange<T>> FollowAccess<T>(
        List<T> items, List<MeshNode> decided, string viewer, ParsedQuery parsedQuery,
        Func<List<T>, QueryResultChange<T>> initial)
    {
        var anchors = decided
            .SelectMany(n => new[] { n.Path, n.MainNode })
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Observable.Defer(() =>
        {
            // Per subscription; mutated only inside the coalesced pipeline, which runs one at a time.
            Dictionary<string, T>? held = null;
            long version = 0;
            return SecuredQueryAccessFollow.RecheckTriggers(AccessFollowServices, viewer, anchors)
                .Select(_ => System.Reactive.Unit.Default)
                // Subscribed AFTER the triggers, so a grant that lands while the first run reads is
                // never missed: it queues one follow-up run.
                .Merge(Observable.Return(System.Reactive.Unit.Default))
                .CoalesceWhileRunning(() => FilterReadable(items, item => item as MeshNode, viewer))
                .SelectMany<List<T>, QueryResultChange<T>>(visible =>
                {
                    var now = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in visible)
                        if (item is MeshNode { Path: { Length: > 0 } path })
                            now[path] = item;
                    if (held is null)
                    {
                        held = now;
                        return [initial(visible)];
                    }
                    var removed = held.Where(h => !now.ContainsKey(h.Key)).Select(h => h.Value).ToList();
                    var added = now.Where(n => !held.ContainsKey(n.Key)).Select(n => n.Value).ToList();
                    held = now;
                    var frames = new List<QueryResultChange<T>>(2);
                    if (removed.Count > 0)
                        frames.Add(new QueryResultChange<T>
                        {
                            ChangeType = QueryChangeType.Removed,
                            Items = removed,
                            Query = parsedQuery,
                            Version = ++version,
                            Timestamp = DateTimeOffset.UtcNow,
                        });
                    if (added.Count > 0)
                        frames.Add(new QueryResultChange<T>
                        {
                            ChangeType = QueryChangeType.Added,
                            Items = added,
                            Scores = ComputeScores(added, parsedQuery),
                            Query = parsedQuery,
                            Version = ++version,
                            Timestamp = DateTimeOffset.UtcNow,
                        });
                    return frames;
                })
                .Do(_ => { }, ex => _logger?.LogWarning(ex,
                    "[StaticNodeQueryProvider] the access follow of an open secured query faulted — ending the query"));
        });
    }

    /// <summary>
    /// The services an open secured query follows grant changes through
    /// (<see cref="SecuredQueryAccessFollow.RecheckTriggers"/>) and the permission-establishment budget its
    /// per-row Read check is bounded by. Set by the mesh's registration; when null (a provider built
    /// without a mesh) declared rows are decided once and the query completes after its Initial.
    /// An <c>init</c> property rather than a constructor parameter, so no published constructor signature moves.
    /// </summary>
    public IServiceProvider? AccessFollowServices { get; init; }

    private string ResolveViewer(MeshQueryRequest request)
        => QueryIdentityResolver.ResolveAndReport(
            request, _accessService?.Context?.ObjectId ?? _accessService?.CircuitContext?.ObjectId, _logger).UserId;

    /// <summary>
    /// 🚨 <b>A declared node is row-filtered like a stored one — unless it is platform VOCABULARY.</b>
    /// Declared nodes are served from memory, and until this filter covered them they reached every
    /// caller: a viewer holding no grant on a partition listed a node declared there through
    /// <c>IMeshService.Query</c> — viewer stamped on the request — while a point read of the very same
    /// node (<c>GetMeshNodeStream</c>, whose gate is the permission fold) refused them. Two read seams
    /// answering one question differently is the defect; the point read is the one that is right. So
    /// every declared row is decided by the same validators the storage providers apply to a stored
    /// row, for the same viewer (<see cref="QueryIdentityResolver"/>) — the one exception being
    /// <see cref="IsDeclaredVocabulary"/>, the definitions every caller needs in order to use the mesh
    /// at all and that no grant is ever written for.
    ///
    /// <para>Pure in-memory and synchronous when there is nothing to decide (no row needs a verdict,
    /// no validators, or a System read).</para>
    /// </summary>
    private IObservable<List<T>> RowFilter<T>(List<T> items, MeshQueryRequest request)
    {
        if (_nodeValidators is null || !items.Any(i => i is MeshNode n && RequiresRowFilter(n)))
            return Observable.Return(items);
        return FilterReadable(items, item => item as MeshNode, ResolveViewer(request));
    }

    /// <summary>
    /// The declared nodes served to every caller without a per-row verdict: DEFINITIONS — a type's
    /// hub registration (no node type of its own, and a <see cref="MeshNode.HubConfiguration"/>: it
    /// configures the hub of every instance of the type it names), a
    /// <see cref="MeshNode.NodeTypePath">NodeType</see> definition, a <c>Role</c> definition and a
    /// <c>Partition</c> definition. They are the mesh's vocabulary: every caller resolves types,
    /// roles and partitions by them, and no access grant is written on them — on a mesh with no
    /// public grants a viewer holds Read on almost none of them, so filtering them would empty every
    /// type picker and role list for everyone but an admin. A declared node of ANY other type —
    /// content, configuration, a grant — is decided by the row filter, so declaring it is no longer a
    /// way to publish it.
    ///
    /// <para>🚨 A missing node type ALONE identifies nothing: content may be declared untyped (a body,
    /// a name, no hub configuration), and treating that as vocabulary served it — name, path and
    /// content — to a viewer with no grant on its partition. Only a positively identified
    /// registration is exempt; an untyped node without a hub configuration is decided by the row
    /// filter like any other declared row.</para>
    ///
    /// <para>🚨 METADATA is never vocabulary, whatever its shape (<see cref="IsMetadataPath"/>):
    /// a satellite-table path (<c>_Access</c>, <c>_Thread</c>, …), a SIBLING metadata node
    /// (<c>{ns}/_Policy</c> — a regular row, so <see cref="SatelliteTableMapping.IsSatellitePath"/>
    /// does not see it), and anything filed beneath one.</para>
    /// </summary>
    /// <param name="node">The declared node.</param>
    internal static bool IsDeclaredVocabulary(MeshNode node)
        => !IsMetadataPath(node)
           && (IsTypeRegistration(node) || (node.NodeType is { } type && DefinitionNodeTypes.Contains(type)));

    /// <summary>
    /// A declared node that is metadata of another node rather than a definition: on a satellite-table
    /// path, or with ANY underscore metadata segment (<see cref="SatelliteTableMapping.IsSatelliteId"/>)
    /// below its partition root — itself (<c>{ns}/_Policy</c>, a sibling satellite) or an ancestor.
    ///
    /// <para>🚨 Read from the PATH, never from how it is split between <see cref="MeshNode.Id"/> and
    /// <see cref="MeshNode.Namespace"/>: a full-path id (<c>new MeshNode("p/_Policy")</c>, Namespace
    /// null) is a documented registration shape that <c>AddMeshNodes</c> preserves, and a check on the
    /// namespace or on the id alone exempted it. The root segment is skipped — a partition may be
    /// NAMED with a leading underscore (<c>_Setting</c>) — and so is an entry of the partition CATALOG
    /// (<c>Admin/Partition/{name}</c>), whose id is the name it declares, exactly as
    /// <see cref="SatelliteTableMapping.IsSiblingSatellite"/> rules.</para>
    /// </summary>
    private static bool IsMetadataPath(MeshNode node)
    {
        if (SatelliteTableMapping.IsSatellitePath(node.Path))
            return true;
        var segments = (node.Path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < segments.Length; i++)
        {
            if (!SatelliteTableMapping.IsSatelliteId(segments[i]))
                continue;
            var isCatalogEntry = i == segments.Length - 1 && i == 2
                && string.Equals(segments[0], "Admin", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], "Partition", StringComparison.OrdinalIgnoreCase);
            if (!isCatalogEntry)
                return true;
        }
        return false;
    }

    /// <summary>A type's hub registration: untyped itself, and carrying the hub configuration of the
    /// type it names. See <see cref="IsDeclaredVocabulary"/>.</summary>
    private static bool IsTypeRegistration(MeshNode node)
        => string.IsNullOrEmpty(node.NodeType) && node.HubConfiguration is not null;

    private static bool RequiresRowFilter(MeshNode node) => !IsDeclaredVocabulary(node);

    // Immutable, read-only lookup — the node types whose declared instances ARE definitions.
    private static readonly System.Collections.Immutable.ImmutableHashSet<string> DefinitionNodeTypes =
        System.Collections.Immutable.ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            MeshNode.NodeTypePath,
            MeshWeaver.Graph.Configuration.RoleNodeType.NodeType,
            MeshWeaver.Graph.Configuration.PartitionNodeType.NodeType);

    /// <summary>
    /// <paramref name="items"/> without the declared rows <paramref name="viewer"/> may not read — the
    /// one filter <see cref="Query{T}"/> and <see cref="Autocomplete"/> share. Synchronous when there
    /// is nothing to decide (only vocabulary rows, no validators, or a System read).
    /// </summary>
    private IObservable<List<TItem>> FilterReadable<TItem>(List<TItem> items, Func<TItem, MeshNode?> nodeOf, string viewer)
    {
        if (_nodeValidators is null
            || string.Equals(viewer, WellKnownUsers.System, StringComparison.Ordinal)
            || !items.Any(i => nodeOf(i) is { } n && RequiresRowFilter(n)))
            return Observable.Return(items);

        return items
            .Select(item => nodeOf(item) is { } node && RequiresRowFilter(node)
                ? ValidateRead(node, viewer).Select(visible => (item, visible))
                : Observable.Return((item, visible: true)))
            .ToInlineObservable()
            .Concat()
            .Where(r => r.visible)
            .Select(r => r.item)
            .ToList()
            .Select(list => list.ToList());
    }

    /// <summary>The Read verdict of every Read-capable validator for <paramref name="viewer"/> —
    /// the same composition <see cref="StorageAdapterMeshQueryProvider"/> applies to a stored row.</summary>
    private IObservable<bool> ValidateRead(MeshNode node, string viewer)
    {
        var relevant = _nodeValidators!
            .Select(lv => lv.Value)
            .Where(v => v.SupportedOperations.Count == 0 || v.SupportedOperations.Contains(NodeOperation.Read))
            .ToList();
        if (relevant.Count == 0)
            return Observable.Return(true);
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Read,
            Node = node,
            AccessContext = new AccessContext { ObjectId = viewer },
        };
        // 🚨 Bounded and FAIL-CLOSED (MeshWeaver#7365), the rule StorageAdapterMeshQueryProvider.ValidateRead
        // holds: an open query re-runs this check on every grant change, so a row is shown only on an
        // ESTABLISHED grant. A validator that completes without a verdict refuses the row (`.All` over an
        // empty answer was true, so silence admitted it), a validator that faults refuses the row instead
        // of ending the query, and past the permission-establishment budget the row is withheld.
        var budget = (AccessFollowServices?.GetService(typeof(MeshOperationOptions)) as MeshOperationOptions
                      ?? new MeshOperationOptions()).PermissionEstablishmentBudget;
        return relevant
            .Select(v => v.Validate(context).Take(1).Select(r => r.IsValid).DefaultIfEmpty(false))
            .ToInlineObservable()
            .Concat()
            .All(valid => valid)
            .Timeout(budget)
            .Catch((Exception ex) =>
            {
                _logger?.LogWarning(ex,
                    "[StaticNodeQueryProvider] Read check on {Path} for {User} reached no verdict within {Budget} — the row is withheld",
                    node.Path, viewer, budget);
                return Observable.Return(false);
            });
    }

    /// <summary>
    /// Score the result set with <see cref="MeshWeaver.Data.Completion.FuzzyScorer"/> when the query has
    /// a <c>TextSearch</c> term — fzf-style ranking against the item's Name
    /// (with Path as a fallback for items without a Name). The aggregator
    /// (<c>MeshQuery.ClipMergedInitial</c>) sorts by these scores descending
    /// after applying any explicit <c>OrderBy</c>, so a typed-name match
    /// climbs above filter-only matches without the caller having to add
    /// <c>sort:</c>. Returns <see langword="null"/> for non-MeshNode T or
    /// non-text queries — aggregator preserves insertion order in those cases.
    /// </summary>
    private static IReadOnlyList<double>? ComputeScores<T>(IReadOnlyList<T> items, ParsedQuery parsed)
    {
        if (items.Count == 0) return null;
        if (string.IsNullOrEmpty(parsed.TextSearch)) return null;
        var scorer = new MeshWeaver.Data.Completion.FuzzyScorer();
        // FuzzyScorer.Score returns ScoredItem<T> filtered to MATCHES only —
        // it drops non-matches. For our purpose every item already matched the
        // server-side filter; we just want the relevance score on each. Re-run
        // scoring per-item via a one-element collection so we never lose rows.
        var scores = new double[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is not MeshNode node)
            {
                scores[i] = 0;
                continue;
            }
            var text = !string.IsNullOrEmpty(node.Name) ? node.Name : (node.Path ?? string.Empty);
            var scored = scorer.Score(new[] { node }, parsed.TextSearch, _ => text).FirstOrDefault();
            // Exact name and Space matches lead (SearchRanking, #7381); fzf orders within a tier.
            scores[i] = SearchRanking.Boost(node, parsed.TextSearch) + (scored?.Score ?? 0);
        }
        return scores;
    }

    private List<T> CollectStaticResults<T>(ParsedQuery parsed, string? requestContext)
    {
        var context = requestContext ?? parsed.Context;
        var items = new List<T>();

        // source:activity / source:accessed are activity-satellite / UserActivity
        // joins — static catalog entries have neither, so always empty here.
        if (parsed.Source is QuerySource.Activity or QuerySource.Accessed)
            return items;

        var nodeTypeFilter = GetNodeTypeFilterValue(parsed.Filter);
        if (nodeTypeFilter != null && !_nodeTypes.Contains(nodeTypeFilter))
            return items;

        // Emit static nodes when:
        //  - a field filter is set (nodeType:, name:, etc.),
        //  - a path is set,
        //  - or an explicit scope walks the static set (Children/Descendants/
        //    Subtree/Hierarchy/AncestorsAndSelf). The last branch is what makes
        //    a bare "scope:descendants" against an empty namespace return the
        //    full static catalog instead of nothing.
        var scopeWalks = parsed.Scope is QueryScope.Children
            or QueryScope.Descendants
            or QueryScope.Subtree
            or QueryScope.Hierarchy
            or QueryScope.AncestorsAndSelf
            or QueryScope.NextLevel;
        var hasQualifier = HasFieldFilter(parsed) || !string.IsNullOrEmpty(parsed.Path) || scopeWalks;

        // NextLevel = the populated frontier. The suppressor universe is EVERY static node
        // (a real node occupies its level even if it doesn't match the outer filter), so build
        // the frontier path set once over _allNodes, then emit only nodes on it. Mirrors the
        // Postgres anti-join + the pedestrian walk's frontier filter.
        HashSet<string>? frontierPaths = parsed.Scope == QueryScope.NextLevel
            ? NamespaceFrontier.Frontier(parsed.Path ?? "", _allNodes.Select(n => n.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        // Path == "" (an explicit `namespace:` root-children query) still scopes — only Path == null
        // means "no path constraint". Short-circuiting on empty string leaked every static node at
        // any depth into the home catalog's first-level "partition roots" leg.
        bool MatchesScope(MeshNode node) => frontierPaths != null
            ? frontierPaths.Contains(node.Path)
            : parsed.Path is null || MatchesAnyPath(node, parsed);

        if (hasQualifier)
        {
            foreach (var node in _providerNodes)
            {
                if (!MatchesScope(node)) continue;
                if (!_evaluator.Matches(node, parsed)) continue;
                if (IsExcludedByContext(node, context)) continue;
                if (parsed.IsMain == true && node.MainNode != node.Path) continue;
                if (node is T typed) items.Add(typed);
            }
        }

        // Registration nodes — the in-memory type/module/partition declarations AddMeshNodes
        // contributes — are not content. A surface that lists things for a person says so with
        // `is:content` and is not handed them; that is a statement each surface makes on its own
        // query, rather than a central list of context names that has to be kept in step with
        // every surface that exists. Without it the home screen was handed every NodeType,
        // ModuleDefinition, Partition and Role row alongside the user's actual spaces.
        var isSearch = string.Equals(context, MeshContexts.Search, StringComparison.OrdinalIgnoreCase);
        if (!isSearch && parsed.IsContent != true && hasQualifier)
        {
            foreach (var node in _configNodes)
            {
                if (!MatchesScope(node)) continue;
                if (!_evaluator.Matches(node, parsed)) continue;
                if (IsExcludedByContext(node, context)) continue;
                if (parsed.IsMain == true && node.MainNode != node.Path) continue;
                if (node is T typed) items.Add(typed);
            }
        }
        return items;
    }

    // namespace candidates a query targets = ExtractNamespaces + first segment
    // of Path. Mirrors MeshQuery.MergeQueryNamespaces (internal there) so the
    // self-filter in QueryAsync sees the same shape the aggregator's predicate
    // would have, were it to call Matches().
    private static IReadOnlyList<string> MergeNamespaceCandidates(ParsedQuery parsed)
    {
        var fromFilter = parsed.ExtractNamespaces();
        var firstSegment = string.IsNullOrEmpty(parsed.Path) ? null : parsed.Path.Split('/', 2)[0];
        if (string.IsNullOrEmpty(firstSegment))
            return fromFilter;
        if (fromFilter.Count == 0)
            return new[] { firstSegment };
        var combined = new List<string>(fromFilter.Count + 1);
        combined.AddRange(fromFilter);
        if (!combined.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            combined.Add(firstSegment);
        return combined;
    }

    /// <summary>
    /// Multi-value path support: <c>path:a|b|c</c> parses to
    /// <see cref="ParsedQuery.Paths"/> with three entries. <see cref="MatchesPath"/>
    /// only considers <see cref="ParsedQuery.Path"/> (the first value) — which
    /// silently drops ancestor matches for path-resolution queries
    /// (<see cref="MeshWeaver.Hosting.PathResolutionService"/> emits
    /// <c>path:a/b/c/d|a/b/c|a/b|a</c>). This helper iterates every path value
    /// and returns true on any match, mirroring the loop in
    /// <c>StorageAdapterMeshQueryProvider</c>.
    /// </summary>
    private static bool MatchesAnyPath(MeshNode node, ParsedQuery parsed)
    {
        if (parsed.Paths is { Count: > 1 } multi)
        {
            foreach (var p in multi)
            {
                if (MatchesPathValue(node, p, parsed.Scope))
                    return true;
            }
            return false;
        }
        return MatchesPath(node, parsed);
    }

    private static bool MatchesPathValue(MeshNode node, string path, QueryScope scope)
    {
        var nodePath = node.Path;
        var nodeNamespace = node.Namespace ?? "";
        // Empty base path = the root: Children → root-level nodes only; the walk scopes span all.
        if (path.Length == 0)
            return scope switch
            {
                QueryScope.Children => nodeNamespace.Length == 0,
                QueryScope.Exact => false,
                _ => true,
            };
        return scope switch
        {
            QueryScope.Exact =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase),
            QueryScope.Children =>
                string.Equals(nodeNamespace, path, StringComparison.OrdinalIgnoreCase),
            QueryScope.Descendants =>
                nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
            QueryScope.Subtree =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
            QueryScope.Hierarchy =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
            QueryScope.AncestorsAndSelf =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <inheritdoc />
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
    {
        // Pure in-memory — no I/O, so a single completed Observable.Return.
        T? result = default;
        var node = _allNodes.FirstOrDefault(n =>
            string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));
        if (node != null && typeof(MeshNode).GetProperty(property)?.GetValue(node) is T typedValue)
            result = typedValue;
        return Observable.Return(result);
    }

    /// <summary>
    /// Checks whether a node matches the query's path and scope constraints.
    /// Only applied to configuration nodes, not provider nodes.
    /// </summary>
    private static bool MatchesPath(MeshNode node, ParsedQuery parsed)
    {
        // Only a NULL path means "no path constraint". An empty-but-set path (`namespace:` →
        // Path == "" + Children) scopes to root-level nodes (namespace == ""); for the walk
        // scopes an empty base is the root, whose subtree/descendants span everything.
        if (parsed.Path is null)
            return true;
        if (parsed.Path.Length == 0)
            return parsed.Scope switch
            {
                QueryScope.Children => string.IsNullOrEmpty(node.Namespace),
                QueryScope.Exact => false,
                _ => true,
            };

        var path = parsed.Path;
        var nodePath = node.Path;
        var nodeNamespace = node.Namespace ?? "";

        return parsed.Scope switch
        {
            QueryScope.Exact =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase),

            QueryScope.Children =>
                string.Equals(nodeNamespace, path, StringComparison.OrdinalIgnoreCase),

            QueryScope.Descendants =>
                nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase),

            QueryScope.Subtree =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),

            QueryScope.Ancestors =>
                path.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase),

            QueryScope.AncestorsAndSelf =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase),

            QueryScope.Hierarchy =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase),

            _ => true
        };
    }

    /// <summary>
    /// Checks if a node is excluded from the specified context.
    /// </summary>
    private bool IsExcludedByContext(MeshNode node, string? context)
    {
        if (context == null) return false;
        if (_meshConfiguration?.IsExcludedFromContext(node.NodeType, context) == true)
            return true;
        if (node.IsExcludedFromContext(context))
            return true;
        return false;
    }

    /// <summary>
    /// Returns true when the query has field-level filters beyond just $type,
    /// or has a text search term.
    /// </summary>
    private static bool HasFieldFilter(ParsedQuery parsed)
    {
        if (!string.IsNullOrEmpty(parsed.TextSearch))
            return true;

        if (parsed.Filter == null)
            return false;

        return HasNonTypeCondition(parsed.Filter);
    }

    private static bool HasNonTypeCondition(QueryNode node)
    {
        return node switch
        {
            QueryComparison c => !c.Condition.Selector.Equals("$type", StringComparison.OrdinalIgnoreCase),
            QueryAnd a => a.Children.Any(HasNonTypeCondition),
            QueryOr o => o.Children.Any(HasNonTypeCondition),
            _ => false
        };
    }

    /// <summary>
    /// Extracts the nodeType filter value from the query AST, if present.
    /// </summary>
    private static string? GetNodeTypeFilterValue(QueryNode? node)
    {
        return node switch
        {
            QueryComparison c when c.Condition.Selector.Equals("nodeType", StringComparison.OrdinalIgnoreCase)
                => c.Condition.Values.FirstOrDefault(),
            QueryAnd a => a.Children.Select(GetNodeTypeFilterValue).FirstOrDefault(v => v != null),
            QueryOr o => o.Children.Select(GetNodeTypeFilterValue).FirstOrDefault(v => v != null),
            _ => null
        };
    }
}
