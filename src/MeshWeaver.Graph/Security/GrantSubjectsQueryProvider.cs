using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Answers the reserved virtual namespace <see cref="AccessSubjectQueries.GrantSubjectsNamespace"/>
/// (<c>Auth/_GrantSubjects/{scope}</c>): the users a viewer may pick as subjects when granting
/// access at <c>{scope}</c>. This is the ONE place the Access Control pickers' users half goes
/// through (policy <c>user-listing-needs-grant-right</c>).
///
/// <para><b>Authorize as the caller, execute as System</b>
/// (Doc/Architecture/AuthorizeAsCallerExecuteAsSystem). The viewer must hold the manage-access
/// right at the scope: the right the Access Control page itself demands before it grants
/// (<see cref="Permission.Delete"/> on the scope), at a scope a grant may be made on at all
/// (<see cref="AccessAssignmentGuard.CanGrantAt"/>). Only then is the user directory
/// (<see cref="AccessSubjectQueries.Users"/>) read, as System, with the caller's other terms
/// (free text, sort, limit) carried over. Anyone else gets an empty <c>Initial</c>, and so does a
/// check that reached no verdict (logged), which fails closed.</para>
///
/// <para><b>Why a provider, not a wider read rule.</b> The raw directory read answers differently
/// per backend when run as a viewer: PostgreSQL answers nothing to a signed-in caller holding no read
/// grant on the <c>auth</c> mirror (core's <c>Auth</c> partition declares none, so that is every
/// caller unless a deployment added one), the in-memory provider lists every user (it applies
/// the User type's C# read rule). Widening the Postgres access clause would let every signed-in
/// user enumerate every account. Instead, this provider decides who may list users — the same way
/// on every backend — and nothing is stored under the reserved namespace, so every storage
/// provider answers the same query with nothing.</para>
/// </summary>
public sealed class GrantSubjectsQueryProvider(IServiceProvider services) : IMeshQueryProvider
{
    private readonly QueryParser parser = new();

    /// <inheritdoc />
    public string Name => "MeshWeaver.Graph.Security.GrantSubjects";

    /// <inheritdoc />
    public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        // Every query reaches this provider (as with the app directory); anything that is not a
        // grant-subjects query gets an empty Initial carrying its own parse.
        var target = Target(request.EffectiveQueries);
        if (target is null || !typeof(T).IsAssignableFrom(typeof(MeshNode)))
            return Observable.Return(Empty<T>(ParseFirst(request)));
        var (scope, query, parsed) = target.Value;
        if (!string.Equals(parsed.ExtractNodeType(), UserNodeTypeName, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(Empty<T>(parsed));

        var directoryRequest = request with
        {
            Query = DirectoryQuery(query),
            Queries = null,
            // The directory read is GLOBAL: a caller's default or context path must not be
            // substituted for its empty path, which would exclude the root-level User nodes.
            DefaultPath = null,
            ContextPath = null,
        };
        var viewer = request.UserId;
        // 🚨 Only a real, signed-in person is answered. A System request is refused, not served:
        // the shared query cache (workspace/hub GetQuery) loads its queries AS SYSTEM and re-applies
        // only the per-row Read rule for each subscriber — and a root User profile is readable by
        // every signed-in viewer, so serving System here would hand the directory to anyone who
        // subscribed through that cache. System never needs this namespace: it reads
        // AccessSubjectQueries.Users directly.
        if (!WellKnownUsers.IsAuthenticated(viewer)
            || string.Equals(viewer, WellKnownUsers.System, StringComparison.Ordinal)
            || !AccessAssignmentGuard.CanGrantAt(scope))
            return Observable.Return(Empty<T>(parsed));

        var mesh = services.GetRequiredService<IMeshService>();
        var hub = services.GetRequiredService<IMessageHub>();
        var logger = services.GetService<ILogger<GrantSubjectsQueryProvider>>();
        // LIVE: the decision follows the viewer's permission at the scope, so a revoked right
        // withdraws the rows (and stops the System directory read) on the same subscription.
        return hub.CheckPermissionOutcome(scope, viewer!, Permission.Delete)
            .Do(outcome =>
            {
                if (outcome.IsUndetermined)
                    logger?.LogWarning(
                        "Grant subjects at '{Scope}' for {Viewer}: the permission check reached no verdict ({Reason}) — no users listed",
                        scope, viewer, outcome.UndeterminedReason);
            })
            .Select(outcome => outcome.IsGranted)
            .DistinctUntilChanged()
            // The continuation SUBSCRIBES a query: it must not run on the permission fold's gate.
            .Select(granted => Observable.Return(granted, System.Reactive.Concurrency.Scheduler.Default))
            .Concat()
            .Select(granted => granted
                ? Rows(mesh.Query<MeshNode>(directoryRequest.AsSystem()))
                : Observable.Return(Folded.Empty))
            .Switch()
            .Scan((Previous: (Folded?)null, Changes: (IReadOnlyList<QueryResultChange<T>>)[]),
                (state, folded) => (folded, Diff<T>(state.Previous, folded, parsed)))
            .SelectMany(state => state.Changes);
    }

    /// <summary>
    /// The directory read folded to its current rows PLUS what the read itself said about its own
    /// completeness and ranking. A partial directory read (a silent provider, an incomplete
    /// snapshot) must reach the picker as partial, never as a complete user list, and the
    /// relevance <see cref="QueryResultChange{T}.Scores"/> must survive so the picker ranks.
    /// </summary>
    internal sealed record Folded(
        ImmutableList<MeshNode> Rows,
        bool SnapshotIncomplete = false,
        IReadOnlyList<string>? SilentProviders = null,
        IReadOnlyList<string>? PartiallyReadPartitions = null,
        IReadOnlyList<string>? Degradations = null,
        ImmutableDictionary<string, double>? Scores = null)
    {
        public static Folded Empty { get; } = new(ImmutableList<MeshNode>.Empty);

        internal bool SameMeta(Folded other)
            => SnapshotIncomplete == other.SnapshotIncomplete
               && (SilentProviders ?? []).SequenceEqual(other.SilentProviders ?? [])
               && (PartiallyReadPartitions ?? []).SequenceEqual(other.PartiallyReadPartitions ?? [])
               && (Degradations ?? []).SequenceEqual(other.Degradations ?? []);
    }

    /// <summary>
    /// A query's change feed folded into its current rows, in the order they arrived
    /// (Initial/Reset restart, Removed drops, Added/Updated replace in place or append), carrying
    /// the completeness flags and scores of the frames. Pure.
    /// </summary>
    internal static IObservable<Folded> Rows(IObservable<QueryResultChange<MeshNode>> changes)
        => changes.Scan(Folded.Empty, (state, change) =>
        {
            var restart = change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset;
            var rows = restart ? ImmutableList<MeshNode>.Empty : state.Rows;
            var scores = restart || state.Scores is null
                ? ImmutableDictionary<string, double>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
                : state.Scores;
            // A restart replaces what the read said about itself; a delta can only add to it.
            var incomplete = change.SnapshotIncomplete || (!restart && state.SnapshotIncomplete);
            var silent = Union(restart ? null : state.SilentProviders, change.SilentProviders);
            var partial = Union(restart ? null : state.PartiallyReadPartitions, change.PartiallyReadPartitions);
            var degraded = Union(restart ? null : state.Degradations, change.Degradations);

            if (change.ChangeType == QueryChangeType.Removed)
            {
                rows = rows.RemoveAll(r => change.Items.Any(i => SamePath(i, r)));
                foreach (var gone in change.Items)
                    if (gone.Path is { } gonePath)
                        scores = scores.Remove(gonePath);
            }
            else
            {
                for (var i = 0; i < change.Items.Count; i++)
                {
                    var item = change.Items[i];
                    var at = rows.FindIndex(r => SamePath(r, item));
                    rows = at >= 0 ? rows.SetItem(at, item) : rows.Add(item);
                    if (change.Scores is { } sc && sc.Count == change.Items.Count && item.Path is { } path)
                        scores = scores.SetItem(path, sc[i]);
                }
            }
            return new Folded(rows, incomplete, silent, partial, degraded, scores);
        });

    private static IReadOnlyList<string>? Union(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        => (a, b) switch
        {
            (null, null) => null,
            (null, _) => b,
            (_, null) => a,
            _ => a.Union(b, StringComparer.Ordinal).ToList(),
        };

    private static bool SamePath(MeshNode a, MeshNode b)
        => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first snapshot is the <c>Initial</c>; every later one the deltas against the last —
    /// removals, additions and updates, and nothing when the rows and their completeness did not
    /// change. A change in completeness alone is announced as an empty <c>Updated</c> frame
    /// carrying the flags. Pure.
    /// </summary>
    internal static IReadOnlyList<QueryResultChange<T>> Diff<T>(
        Folded? previous, Folded current, ParsedQuery parsed)
    {
        if (previous is null)
            return [Change<T>(QueryChangeType.Initial, current.Rows, parsed, current)];
        var removed = previous.Rows.Where(p => !current.Rows.Any(c => SamePath(c, p))).ToList();
        var added = current.Rows.Where(c => !previous.Rows.Any(p => SamePath(c, p))).ToList();
        var updated = current.Rows.Where(c => previous.Rows.Any(p => SamePath(c, p) && !ReferenceEquals(c, p) && !Equals(c, p))).ToList();
        var changes = ImmutableList.CreateBuilder<QueryResultChange<T>>();
        if (removed.Count > 0) changes.Add(Change<T>(QueryChangeType.Removed, removed, parsed, current));
        if (added.Count > 0) changes.Add(Change<T>(QueryChangeType.Added, added, parsed, current));
        if (updated.Count > 0) changes.Add(Change<T>(QueryChangeType.Updated, updated, parsed, current));
        if (changes.Count == 0 && !previous.SameMeta(current))
            changes.Add(Change<T>(QueryChangeType.Updated, [], parsed, current));
        return changes.ToImmutable();
    }

    private static QueryResultChange<T> Change<T>(
        QueryChangeType type, IEnumerable<MeshNode> nodes, ParsedQuery parsed, Folded state)
    {
        var items = nodes.ToList();
        IReadOnlyList<double>? scores = null;
        if (state.Scores is { Count: > 0 } map && items.Count > 0
            && items.Any(n => n.Path is { } p && map.ContainsKey(p)))
            scores = items.Select(n => n.Path is { } p && map.TryGetValue(p, out var v) ? v : 0d).ToList();
        return new QueryResultChange<T>
        {
            ChangeType = type,
            Items = items.Cast<T>().ToList(),
            Query = parsed,
            Timestamp = DateTimeOffset.UtcNow,
            Scores = scores,
            SnapshotIncomplete = state.SnapshotIncomplete,
            SilentProviders = state.SilentProviders,
            PartiallyReadPartitions = state.PartiallyReadPartitions,
            Degradations = state.Degradations,
        };
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
        string? contextPath = null, string? context = null)
        => Observable.Return<IReadOnlyCollection<QueryResult>>(Array.Empty<QueryResult>());

    /// <inheritdoc />
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
        => Observable.Return(default(T));

    private const string UserNodeTypeName = "User";

    /// <summary>
    /// The first of <paramref name="queries"/> that asks for grant subjects: its scope, the query
    /// text and its parse. Null when none does. Pure apart from parsing.
    /// </summary>
    private (string Scope, string Query, ParsedQuery Parsed)? Target(IEnumerable<string> queries)
    {
        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query)
                || query.IndexOf(AccessSubjectQueries.GrantSubjectsNamespace, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var parsed = parser.Parse(query);
            foreach (var ns in parsed.ExtractNamespaces())
                if (AccessSubjectQueries.GrantScopeOf(ns) is { } scope)
                    return (scope, query, parsed);
            if (AccessSubjectQueries.GrantScopeOf(parsed.Path) is { } pathScope
                && (parsed.Paths is not { Count: > 1 } || parsed.Paths.All(p => Equals(AccessSubjectQueries.GrantScopeOf(p), pathScope))))
                return (pathScope, query, parsed);
        }
        return null;
    }

    private ParsedQuery ParseFirst(MeshQueryRequest request)
        => request.EffectiveQueries.FirstOrDefault(q => !string.IsNullOrWhiteSpace(q)) is { } q
            ? parser.Parse(q)
            : ParsedQuery.Empty;

    /// <summary>
    /// The directory read for a grant-subjects query: <see cref="AccessSubjectQueries.Users"/> plus
    /// every term of <paramref name="query"/> except its location (the reserved <c>namespace:</c> /
    /// <c>path:</c> and any <c>scope:</c>) and its <c>nodeType:</c>, which the directory read
    /// already fixes. Free text, sort and content filters carry over. Pure.
    /// </summary>
    /// <param name="query">The grant-subjects query as the picker issued it.</param>
    /// <returns>The query to run against the user directory.</returns>
    public static string DirectoryQuery(string query)
    {
        // Cut by THE parser's own tokenizer, so quoted values (single or double), tabs and other
        // whitespace are removed or kept exactly as they parse.
        var parser = new QueryParser();
        var rest = query ?? "";
        foreach (var qualifier in new[] { "namespace", "path", "scope", "nodeType" })
            rest = parser.RemoveQualifier(rest, qualifier);
        rest = parser.RemoveEmptyGroups(rest).Trim();
        return rest.Length == 0 ? AccessSubjectQueries.Users : $"{AccessSubjectQueries.Users} {rest}";
    }

    private static QueryResultChange<T> Empty<T>(ParsedQuery parsed) =>
        new()
        {
            ChangeType = QueryChangeType.Initial,
            Items = [],
            Query = parsed,
            Timestamp = DateTimeOffset.UtcNow,
        };
}
