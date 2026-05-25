using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Core query service for searching MeshNodes and partition objects.
/// Separated from IStorageService to allow swappable implementations
/// (InMemory, ElasticSearch, Cosmos with vector search, etc.)
/// This is the internal interface that accepts JsonSerializerOptions per method.
/// Use IMeshService for the scoped wrapper that injects options automatically.
/// </summary>
public interface IMeshQueryProvider
{
    /// <summary>
    /// Stable identifier for this provider, used by the aggregator to
    /// deduplicate registrations. Plain
    /// <c>services.AddSingleton&lt;IMeshQueryProvider&gt;(factory)</c>
    /// can register the same provider twice when multiple persistence
    /// extensions are composed (StaticNodeQueryProvider was registered from
    /// both <c>AddPersistence</c> and <c>AddCoreAndWrapperServices</c>).
    /// The aggregator distincts by this Name so the same logical provider
    /// runs exactly once per query.
    /// </summary>
    string Name => GetType().FullName ?? GetType().Name;

    /// <summary>
    /// Predicate the top-level aggregator uses to decide whether to fan a
    /// <em>scoped</em> query out to this provider. It is consulted only
    /// when the query targets a specific partition — i.e.,
    /// <paramref name="queryNamespaces"/> is non-empty. Unscoped queries
    /// (no <c>namespace:</c> condition and no <c>path:</c> filter) fan
    /// to every provider unconditionally, so providers never need a
    /// "match-all" branch here.
    ///
    /// <para>The aggregator pre-extracts namespace candidates once per
    /// query (union of <see cref="ParsedQuery.ExtractNamespaces"/> and
    /// the first segment of <see cref="ParsedQuery.Path"/>) so every
    /// provider's predicate is a cheap O(k·m) set lookup against fields
    /// it stored at construction — no re-parsing per provider per query.</para>
    ///
    /// <para>Each provider compares against namespaces it owns (static
    /// providers) or excludes (storage providers that back the writable
    /// mesh and want to defer static partition reads).</para>
    /// </summary>
    bool Matches(IReadOnlyList<string> queryNamespaces);

    /// <summary>
    /// Autocomplete query - given a namespace, find best matching subnodes.
    /// Returns suggestions ordered by path length first (for path-based autocomplete).
    /// </summary>
    /// <param name="basePath">Base path to search from</param>
    /// <param name="prefix">Prefix to match (partial name/path)</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="limit">Maximum number of suggestions to return</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Suggestions ordered by path length, then score, then name</returns>
    IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Autocomplete query with specified ordering mode.
    /// </summary>
    /// <param name="basePath">Base path to search from</param>
    /// <param name="prefix">Prefix to match (partial name/path)</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="mode">Ordering mode (PathFirst or RelevanceFirst)</param>
    /// <param name="limit">Maximum number of suggestions to return</param>
    /// <param name="contextPath">Context path for proximity-based scoring (null for no proximity boost)</param>
    /// <param name="context">Context for visibility filtering (e.g., "search"). Nodes excluded from this context are hidden.</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Suggestions ordered according to mode</returns>
    IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        CancellationToken ct = default);

    /// <summary>
    /// Creates an observable query that monitors data sources for changes and emits updates.
    /// The first emission contains the full initial result set (ChangeType = Initial).
    /// Subsequent emissions contain incremental changes (Added, Updated, Removed).
    /// </summary>
    /// <typeparam name="T">The type of objects to query (typically MeshNode).</typeparam>
    /// <param name="request">Query request with filters, path, scope, and options.</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>An observable that emits query result changes.</returns>
    /// <example>
    /// <code>
    /// var subscription = meshQuery
    ///     .ObserveQuery&lt;MeshNode&gt;(MeshQueryRequest.FromQuery("path:ACME nodeType:Story scope:descendants"), jsonOptions)
    ///     .Subscribe(change =&gt;
    ///     {
    ///         Console.WriteLine($"Change: {change.ChangeType}, Items: {change.Items.Count}");
    ///     });
    /// // Later: dispose to stop watching
    /// subscription.Dispose();
    /// </code>
    /// </example>
    IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request, JsonSerializerOptions options);

    /// <summary>
    /// 🚨 NEW unified surface — every provider returns a live snapshot of
    /// <see cref="QueryResult"/> rows for <paramref name="request"/>. Each emission
    /// is the FULL current result list (not deltas). The aggregator combines
    /// across providers via <c>CombineLatest</c>, dedupes by <see cref="QueryResult.Path"/>,
    /// and sorts by score / OrderBy.
    /// <para>
    /// Providers that must hit external storage (Postgres, Cosmos, filesystem) MUST
    /// run the actual I/O inside a HOSTED HUB they own — never on the calling mesh
    /// hub's action block. See <c>Doc/Architecture/AsynchronousCalls.md</c> for the
    /// hosted-hub-handler pattern.
    /// </para>
    /// <para>Default implementation bridges to the legacy
    /// <see cref="ObserveQuery{T}"/> for back-compat during the migration. Concrete
    /// providers should override to take the hosted-hub path.</para>
    /// </summary>
    IObservable<IReadOnlyList<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
        => ObserveQuery<MeshNode>(request, options)
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset or QueryChangeType.Added or QueryChangeType.Updated)
            .Scan(
                new Dictionary<string, QueryResult>(StringComparer.OrdinalIgnoreCase),
                (acc, change) =>
                {
                    if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                        acc.Clear();
                    for (var i = 0; i < change.Items.Count; i++)
                    {
                        var node = change.Items[i];
                        if (string.IsNullOrEmpty(node.Path)) continue;
                        var score = change.Scores is { Count: > 0 } s && i < s.Count ? s[i] : 0;
                        acc[node.Path] = QueryResult.FromNode(node, score, providerName: Name);
                    }
                    return acc;
                })
            .Select(d => (IReadOnlyList<QueryResult>)d.Values.ToList());

    /// <summary>
    /// 🚨 NEW unified autocomplete surface — same snapshot semantics as
    /// <see cref="Query"/>. Aggregator wraps each provider's stream with
    /// <c>.StartWith(empty)</c> so <c>CombineLatest</c> emits partial results as
    /// soon as ANY provider produces (slow providers don't gate the UI).
    /// <para>Default impl bridges to the legacy
    /// <see cref="AutocompleteAsync(string, string, JsonSerializerOptions, AutocompleteMode, int, string?, string?, CancellationToken)"/>
    /// by draining the async-enumerable into a single snapshot list.</para>
    /// </summary>
    IObservable<IReadOnlyList<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
        int limit = 10,
        string? contextPath = null,
        string? context = null)
    {
        return System.Reactive.Linq.Observable.Create<IReadOnlyList<QueryResult>>(observer =>
        {
            var cts = new CancellationTokenSource();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var rows = new List<QueryResult>();
                    await foreach (var s in AutocompleteAsync(basePath, prefix, options, mode, limit, contextPath, context, cts.Token))
                    {
                        rows.Add(new QueryResult
                        {
                            Path = s.Path,
                            Name = s.Name,
                            NodeType = s.NodeType,
                            Icon = s.Icon,
                            Score = s.Score,
                            ProviderName = Name,
                        });
                    }
                    observer.OnNext(rows);
                    observer.OnCompleted();
                }
                catch (OperationCanceledException) { observer.OnCompleted(); }
                catch (Exception ex) { observer.OnError(ex); }
            }, cts.Token);
            return System.Reactive.Disposables.Disposable.Create(() => cts.Cancel());
        });
    }

    /// <summary>
    /// Selects a single property value from a node at the given path.
    /// </summary>
    Task<T?> SelectAsync<T>(string path, string property, JsonSerializerOptions options, CancellationToken ct = default);
}
