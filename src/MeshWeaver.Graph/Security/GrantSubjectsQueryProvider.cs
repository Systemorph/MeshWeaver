using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text;
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
        };
        var mesh = services.GetRequiredService<IMeshService>();
        var viewer = request.UserId;
        if (string.Equals(viewer, WellKnownUsers.System, StringComparison.Ordinal))
            return mesh.Query<T>(directoryRequest.AsSystem());
        if (!WellKnownUsers.IsAuthenticated(viewer) || !AccessAssignmentGuard.CanGrantAt(scope))
            return Observable.Return(Empty<T>(parsed));

        var hub = services.GetRequiredService<IMessageHub>();
        var logger = services.GetService<ILogger<GrantSubjectsQueryProvider>>();
        return hub.CheckPermissionOutcome(scope, viewer!, Permission.Delete)
            // The continuation SUBSCRIBES a query: it must not run inside the permission fold's gate.
            .TakeDecisionOutsideGate()
            .SelectMany(outcome =>
            {
                if (outcome.IsGranted)
                    return mesh.Query<T>(directoryRequest.AsSystem());
                if (outcome.IsUndetermined)
                    logger?.LogWarning(
                        "Grant subjects at '{Scope}' for {Viewer}: the permission check reached no verdict ({Reason}) — no users listed",
                        scope, viewer, outcome.UndeterminedReason);
                return Observable.Return(Empty<T>(parsed));
            });
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
        var kept = Terms(query).Where(term =>
            !StartsWith(term, "namespace:") && !StartsWith(term, "path:")
            && !StartsWith(term, "scope:") && !StartsWith(term, "nodeType:"));
        var rest = string.Join(' ', kept);
        return rest.Length == 0 ? AccessSubjectQueries.Users : $"{AccessSubjectQueries.Users} {rest}";
    }

    private static bool StartsWith(string term, string key)
        => term.StartsWith(key, StringComparison.OrdinalIgnoreCase);

    /// <summary>The query's terms: split on spaces that are not inside a double-quoted value. Pure.</summary>
    private static ImmutableList<string> Terms(string query)
    {
        var terms = ImmutableList.CreateBuilder<string>();
        var term = new StringBuilder();
        var quoted = false;
        foreach (var c in query ?? string.Empty)
        {
            if (c == '"')
                quoted = !quoted;
            if (c == ' ' && !quoted)
            {
                if (term.Length > 0)
                    terms.Add(term.ToString());
                term.Clear();
                continue;
            }
            term.Append(c);
        }
        if (term.Length > 0)
            terms.Add(term.ToString());
        return terms.ToImmutable();
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
