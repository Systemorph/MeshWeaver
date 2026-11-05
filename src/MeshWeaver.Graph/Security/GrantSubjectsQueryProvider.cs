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
        var none = ImmutableList<MeshNode>.Empty;
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
                : Observable.Return(none))
            .Switch()
            .Scan((Previous: (ImmutableList<MeshNode>?)null, Changes: (IReadOnlyList<QueryResultChange<T>>)[]),
                (state, rows) => (rows, Diff<T>(state.Previous, rows, parsed)))
            .SelectMany(state => state.Changes);
    }

    /// <summary>
    /// A query's change feed folded into its current rows, in the order they arrived
    /// (Initial/Reset restart, Removed drops, Added/Updated replace in place or append). Pure.
    /// </summary>
    private static IObservable<ImmutableList<MeshNode>> Rows(IObservable<QueryResultChange<MeshNode>> changes)
        => changes.Scan(ImmutableList<MeshNode>.Empty, (rows, change) =>
        {
            if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                rows = ImmutableList<MeshNode>.Empty;
            if (change.ChangeType == QueryChangeType.Removed)
                return rows.RemoveAll(r => change.Items.Any(i => SamePath(i, r)));
            foreach (var item in change.Items)
            {
                var at = rows.FindIndex(r => SamePath(r, item));
                rows = at >= 0 ? rows.SetItem(at, item) : rows.Add(item);
            }
            return rows;
        });

    private static bool SamePath(MeshNode a, MeshNode b)
        => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first snapshot is the <c>Initial</c>; every later one the deltas against the last —
    /// removals, additions and updates, and nothing when the rows did not change. Pure.
    /// </summary>
    internal static IReadOnlyList<QueryResultChange<T>> Diff<T>(
        ImmutableList<MeshNode>? previous, ImmutableList<MeshNode> current, ParsedQuery parsed)
    {
        if (previous is null)
            return [Change<T>(QueryChangeType.Initial, current, parsed)];
        var removed = previous.Where(p => !current.Any(c => SamePath(c, p))).ToList();
        var added = current.Where(c => !previous.Any(p => SamePath(c, p))).ToList();
        var updated = current.Where(c => previous.Any(p => SamePath(c, p) && !ReferenceEquals(c, p) && !Equals(c, p))).ToList();
        var changes = ImmutableList.CreateBuilder<QueryResultChange<T>>();
        if (removed.Count > 0) changes.Add(Change<T>(QueryChangeType.Removed, removed, parsed));
        if (added.Count > 0) changes.Add(Change<T>(QueryChangeType.Added, added, parsed));
        if (updated.Count > 0) changes.Add(Change<T>(QueryChangeType.Updated, updated, parsed));
        return changes.ToImmutable();
    }

    private static QueryResultChange<T> Change<T>(QueryChangeType type, IEnumerable<MeshNode> nodes, ParsedQuery parsed) =>
        new()
        {
            ChangeType = type,
            Items = nodes.Cast<T>().ToList(),
            Query = parsed,
            Timestamp = DateTimeOffset.UtcNow,
        };

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
            if (AccessSubjectQueries.GrantScopeOf(parsed.Path) is { } pathScope)
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
