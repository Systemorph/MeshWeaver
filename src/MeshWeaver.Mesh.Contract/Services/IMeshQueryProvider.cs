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
    ///     .Query&lt;MeshNode&gt;(MeshQueryRequest.FromQuery("path:ACME nodeType:Story scope:descendants"), jsonOptions)
    ///     .Subscribe(change =&gt;
    ///     {
    ///         Console.WriteLine($"Change: {change.ChangeType}, Items: {change.Items.Count}");
    ///     });
    /// // Later: dispose to stop watching
    /// subscription.Dispose();
    /// </code>
    /// </example>
    IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options);

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
    /// <see cref="Query{T}"/> for back-compat during the migration. Concrete
    /// providers should override to take the hosted-hub path.</para>
    /// </summary>
    IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
        => Query<MeshNode>(request, options)
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
            .Select(d => (IReadOnlyCollection<QueryResult>)d.Values.ToList());

    /// <summary>
    /// Unified autocomplete surface — a LIVE snapshot observable of <see cref="QueryResult"/>
    /// rows (each emission is the full current suggestion list). No async surface: a provider
    /// backed by external storage runs its I/O leaf inside the <c>IIoPool</c> (the PG provider's
    /// <c>ToArrayAsync</c> over the npgsql reader) and bridges to this observable; in-memory
    /// providers project synchronously via <c>IEnumerable.ToObservable()</c>. The aggregator
    /// wraps each provider's stream with <c>.StartWith(empty)</c> so <c>CombineLatest</c> emits
    /// as soon as ANY provider produces. Score (incl. vector similarity) rides on
    /// <see cref="QueryResult.Score"/>.
    /// </summary>
    IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
        int limit = 10,
        string? contextPath = null,
        string? context = null);

    /// <summary>
    /// Selects a single property value from a node at the given path (single-emission
    /// observable). The provider's I/O leaf runs in the IIoPool and bridges to this observable.
    /// </summary>
    IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options);
}
