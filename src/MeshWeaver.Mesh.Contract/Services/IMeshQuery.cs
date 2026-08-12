using System.Runtime.CompilerServices;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Query service for searching MeshNodes and partition objects.
/// Separated from IMeshStorage to allow swappable implementations
/// (InMemory, ElasticSearch, Cosmos with vector search, etc.)
/// This is the scoped wrapper that automatically injects JsonSerializerOptions from IMessageHub.
/// </summary>
public interface IMeshQuery
{
    /// <summary>
    /// Creates an observable query that monitors data sources for changes and emits updates.
    /// The first emission contains the full initial result set (ChangeType = Initial).
    /// Subsequent emissions contain incremental changes (Added, Updated, Removed).
    /// </summary>
    /// <typeparam name="T">The type of objects to query (typically MeshNode).</typeparam>
    /// <param name="request">Query request with filters, path, scope, and options.</param>
    /// <returns>An observable that emits query result changes.</returns>
    /// <example>
    /// <code>
    /// var subscription = meshQuery
    ///     .Query&lt;MeshNode&gt;(MeshQueryRequest.FromQuery("path:ACME nodeType:Story scope:descendants"))
    ///     .Subscribe(change =&gt;
    ///     {
    ///         Console.WriteLine($"Change: {change.ChangeType}, Items: {change.Items.Count}");
    ///     });
    /// // Later: dispose to stop watching
    /// subscription.Dispose();
    /// </code>
    /// </example>
    IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request);

    /// <summary>
    /// Selects a single property value from a node at the given path (single-emission
    /// observable). Efficient way to get one property without loading the full Content blob.
    /// </summary>
    /// <typeparam name="T">The expected property type.</typeparam>
    /// <param name="path">The node path.</param>
    /// <param name="property">The property name on MeshNode.</param>
    /// <returns>The property value, or default if node not found or property is null.</returns>
    IObservable<T?> Select<T>(string path, string property);
}

/// <summary>
/// Request parameters for mesh queries.
/// Most query parameters are expressed in the query string itself.
/// </summary>
public record MeshQueryRequest
{
    /// <summary>
    /// GitHub-style query string with all parameters.
    /// Examples:
    /// - "nodeType:Story" - filter by property
    /// - "path:Org/Project nodeType:Story scope:descendants" - scoped search
    /// - "status:Open laptop" - filter + text search
    /// - "sort:lastAccessedAt-desc limit:20" - with ordering and limit
    /// - "source:activity" - query activity records
    ///
    /// <para>Single-query convenience. When <see cref="Queries"/> is also set,
    /// it takes precedence and this property is ignored.</para>
    /// </summary>
    public string Query { get; init; } = "";

    /// <summary>
    /// Multi-query union: when set, the mesh query engine runs every query in
    /// this list and yields the path-keyed union of their result sets as a
    /// single combined snapshot (the same de-dup the per-result <c>Items</c>
    /// list applies to a single query). Set this when you want a single live
    /// snapshot whose membership is "matches query A OR matches query B OR ..."
    /// without managing N parallel subscriptions in user code.
    ///
    /// <para>Sort/limit/skip are applied to the unioned result set using the
    /// first query's parameters (the others contribute membership only).</para>
    /// </summary>
    public IReadOnlyList<string>? Queries { get; init; }

    /// <summary>
    /// All effective query strings — <see cref="Queries"/> when set, otherwise
    /// a one-element view over <see cref="Query"/>. Engines that union over
    /// multiple queries iterate this; single-query engines call <c>.First()</c>.
    /// </summary>
    public IReadOnlyList<string> EffectiveQueries =>
        Queries is { Count: > 0 } qs ? qs : new[] { Query };

    /// <summary>
    /// The viewer this read runs behind — results are filtered to the nodes that user can access.
    ///
    /// <para>🚨 <b>Leaving this null does NOT mean "unfiltered".</b> It means "work out who is
    /// asking", and when nothing can be worked out the read is evaluated as
    /// <see cref="Security.WellKnownUsers.Anonymous"/> — the narrowest viewer there is. For a read
    /// scoped to somebody's own space that returns NOTHING, and an empty result set is
    /// indistinguishable from "the record does not exist". That confusion is the single most
    /// repeated defect on this surface; the doc comment here used to claim the opposite, which is
    /// how it kept spreading.</para>
    ///
    /// <list type="bullet">
    ///   <item>a real user id → that viewer;</item>
    ///   <item><see cref="Security.WellKnownUsers.System"/> → infrastructure read, row-level
    ///     security bypassed;</item>
    ///   <item><c>""</c> (empty, not null) → explicitly the anonymous visitor;</item>
    ///   <item><c>null</c> → resolve from the caller's ambient access context, then fall back per
    ///     <see cref="IdentityFallback"/>.</item>
    /// </list>
    ///
    /// <para>Prefer the named helpers — <see cref="ForViewer"/>, <see cref="AsPublicListing"/>,
    /// <see cref="RequireViewer"/>, <see cref="AsSystem"/> — over setting this by hand.</para>
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// What this read means when <see cref="UserId"/> is unset and no ambient identity can be
    /// recovered. Defaults to <see cref="QueryIdentityFallback.Anonymous"/>, which preserves the
    /// historical behaviour but no longer does it silently.
    ///
    /// <para>Say <see cref="QueryIdentityFallback.PublicListing"/> for a genuine mesh-wide public
    /// catalog and <see cref="QueryIdentityFallback.Fail"/> for a read whose empty result would be
    /// reported to a human as absence.</para>
    /// </summary>
    public QueryIdentityFallback IdentityFallback { get; init; } = QueryIdentityFallback.Anonymous;

    /// <summary>
    /// Runs this read as <paramref name="userId"/>. The explicit, always-correct form: it does not
    /// depend on the caller's ambient context surviving whatever scheduler, pool or change-feed hop
    /// lies between the call and the storage provider.
    /// </summary>
    /// <param name="userId">The viewer to evaluate access for.</param>
    /// <returns>A copy of this request stamped with that viewer.</returns>
    public MeshQueryRequest ForViewer(string userId) => this with { UserId = userId };

    /// <summary>
    /// Declares this read a mesh-wide PUBLIC listing: <see cref="Security.WellKnownUsers.Anonymous"/>
    /// is the intended viewer, not a fallback. Silences the unresolved-viewer diagnostic because
    /// there is nothing to diagnose.
    ///
    /// <para>🚨 Correct for a catalog of published content, where stamping the visitor would fold
    /// their own private copies in as duplicates. Wrong — and the diagnostic exists to catch
    /// exactly this — for anything that is supposed to show the caller THEIR content.</para>
    /// </summary>
    /// <returns>A copy of this request marked as a public listing.</returns>
    public MeshQueryRequest AsPublicListing()
        => this with { IdentityFallback = QueryIdentityFallback.PublicListing };

    /// <summary>
    /// Declares that this read is meaningless without a real viewer: if none can be resolved it
    /// throws <see cref="QueryIdentityUnresolvedException"/> instead of quietly answering with the
    /// Anonymous view.
    ///
    /// <para>Use it wherever an empty result would be shown to a human as "you have nothing here" —
    /// a user's own copies, drafts, history, installed items. A thrown exception is diagnosable;
    /// an empty list is not.</para>
    /// </summary>
    /// <returns>A copy of this request that fails closed on an unresolved viewer.</returns>
    public MeshQueryRequest RequireViewer()
        => this with { IdentityFallback = QueryIdentityFallback.Fail };

    /// <summary>
    /// Runs this read as the infrastructure identity, bypassing row-level security. For framework
    /// plumbing that must not be filtered (security-element loads, seeds, compile activities) —
    /// never for application code serving a user.
    /// </summary>
    /// <returns>A copy of this request stamped with the System identity.</returns>
    public MeshQueryRequest AsSystem() => this with { UserId = WellKnownUsers.System };

    /// <summary>
    /// Default path to use when no path is specified in the query.
    /// Set this from the current navigation context (e.g., INavigationService.CurrentNamespace)
    /// before calling query methods.
    /// </summary>
    public string? DefaultPath { get; init; }

    /// <summary>
    /// Context path for proximity-based scoring.
    /// When set, results closer to this path in the graph hierarchy receive a score boost.
    /// Typically set to the user's current namespace (e.g., "Systemorph/Marketing").
    /// </summary>
    public string? ContextPath { get; init; }

    /// <summary>
    /// Number of results to skip (for paging).
    /// </summary>
    public int? Skip { get; init; }

    /// <summary>
    /// Maximum number of results to return (takes precedence over limit in query string).
    ///
    /// <para>🚨 <b>Leaving this unset does NOT mean "everything".</b> An UNPINNED query (no
    /// <c>path:</c>, no <c>namespace:</c>) is served on Postgres by the cross-schema fan-out, which
    /// answers a request that states no limit with a PAGE — the most recently modified rows, capped
    /// at a default — and the caller cannot tell a page from the whole set. Say
    /// <see cref="Complete"/> when the read is an ENUMERATION rather than a search.</para>
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// The <see cref="Limit"/> value that means "no clip — return every match". Non-positive, which
    /// is what the merge layer has always treated as unbounded; the storage providers translate it
    /// to an unbounded SQL fetch rather than a literal <c>LIMIT -1</c>.
    /// </summary>
    public const int NoLimit = -1;

    /// <summary>
    /// Declares this read an ENUMERATION, not a page: every match must come back, because the
    /// caller iterates the result as the complete set.
    ///
    /// <para>🚨 Use it wherever a MISSING row silently changes behaviour rather than merely
    /// shortening a list — a fan-out over every configured sync source, a bake over every NodeType,
    /// a reconciliation. Both production incidents in this family had the same shape: the query
    /// stated no limit, the cross-schema fan-out substituted its default and ordered by
    /// <c>last_modified DESC</c>, and the rows that fell off the end were exactly the ones that had
    /// gone longest without being processed — so the omission was self-reinforcing and invisible.
    /// #1216 baked 237 NodeTypes against 50 Code nodes; #1326 left 9 of 43 Spaces permanently stale
    /// while the webhook reported success.</para>
    /// </summary>
    /// <returns>A copy of this request that must not be truncated.</returns>
    public MeshQueryRequest Complete() => this with { Limit = NoLimit };

    /// <summary>
    /// Context for visibility filtering. When set, nodes with this context
    /// in their ExcludeFromContext are excluded from results.
    /// Parsed context: qualifier in query string is used as fallback.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Partition hint resolved by query routing rules.
    /// When set, query is routed to this specific partition only (no fan-out).
    /// </summary>
    public string? PartitionHint { get; init; }

    /// <summary>
    /// Table hint resolved by query routing rules.
    /// When set, overrides the default table resolution in the storage adapter.
    /// </summary>
    public string? TableHint { get; init; }

    /// <summary>
    /// Creates a new request with the specified query string.
    /// </summary>
    public static MeshQueryRequest FromQuery(string query) => new() { Query = query };

    /// <summary>
    /// Creates a new request with query and user ID.
    /// </summary>
    public static MeshQueryRequest FromQuery(string query, string? userId)
        => new() { Query = query, UserId = userId };

    /// <summary>
    /// Creates a new request with query and default path.
    /// </summary>
    public static MeshQueryRequest FromQuery(string query, string? userId, string? defaultPath)
        => new() { Query = query, UserId = userId, DefaultPath = defaultPath };

    /// <summary>
    /// Creates a multi-query union request. The engine runs every query and
    /// returns the path-keyed union of matched nodes as a single snapshot.
    /// Sort/limit/skip are taken from the first query.
    /// </summary>
    public static MeshQueryRequest FromQueries(IEnumerable<string> queries, string? userId = null)
    {
        var list = (queries ?? throw new ArgumentNullException(nameof(queries))).ToList();
        if (list.Count == 0) throw new ArgumentException("At least one query required", nameof(queries));
        return new MeshQueryRequest { Queries = list, Query = list[0], UserId = userId };
    }
}

/// <summary>
/// Autocomplete suggestion result.
/// </summary>
/// <param name="Path">Full path to the node</param>
/// <param name="Name">Display name of the node</param>
/// <param name="NodeType">Type of the node (may be null)</param>
/// <param name="Score">Relevance score (higher is better match)</param>
/// <param name="Icon">Icon URL or identifier for display in UI (may be null)</param>
public record QuerySuggestion(string Path, string Name, string? NodeType, double Score, string? Icon = null);

/// <summary>
/// Autocomplete ordering mode.
/// </summary>
public enum AutocompleteMode
{
    /// <summary>
    /// Order by path length first, then score, then name.
    /// Best for path-based autocomplete (e.g., typing a path reference).
    /// </summary>
    PathFirst,

    /// <summary>
    /// Order by score first (name match > path match > other), then path length, then name.
    /// Best for node search/selection (e.g., context selector).
    /// </summary>
    RelevanceFirst
}
