using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Apps;

/// <summary>
/// Answers the RESERVED virtual namespace <c>{user}/_Apps</c> with one synthetic
/// <see cref="AppNodeType.NodeType"/> row per app the viewer can see — the launcher's
/// <c>Home:AppSource = Directory</c> source. The rows have the same shape as the legacy
/// <c>{user}/_App</c> records (content <see cref="App"/>: <see cref="App.Plugin"/> = app id,
/// <see cref="App.OpenPath"/> / <see cref="MeshNode.MainNode"/> = entry point, group and order from
/// the viewer's <see cref="LauncherArrangement"/>), so the launcher view paints them unchanged.
/// <para><b>Nothing is stored under <c>_Apps</c></b>; no other namespace is answered, and every
/// other query gets an empty <c>Initial</c> at once. Only the owner (or the System identity of a
/// process-wide synced query, whose consumer re-applies RLS) is answered; anyone else gets nothing.</para>
/// <para>A drag on a directory tile does not write the row — the view routes it to
/// <see cref="LauncherArrangementPaths.Place"/>.</para>
/// </summary>
public sealed class AppDirectoryQueryProvider(IServiceProvider services) : IMeshQueryProvider
{
    private readonly QueryParser parser = new();

    /// <inheritdoc />
    public string Name => "MeshWeaver.Graph.Apps.AppDirectory";

    /// <inheritdoc />
    public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        // Matches is not consulted by the aggregator, so EVERY query reaches this provider: each
        // is parsed once here, and anything but a directory query gets an empty Initial carrying
        // its own parse (the merge may take its sort and limit from the first Initial it sees).
        var parsedQueries = request.EffectiveQueries.Select(q => parser.Parse(q)).ToList();
        var target = Target(parsedQueries);
        if (target is null || !typeof(T).IsAssignableFrom(typeof(MeshNode)))
            return Observable.Return(Empty<T>(parsedQueries.FirstOrDefault() ?? ParsedQuery.Empty));
        var (owner, parsed) = target.Value;
        var viewer = request.UserId;
        if (!string.Equals(viewer, owner, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(viewer, WellKnownUsers.System, StringComparison.Ordinal))
            return Observable.Return(Empty<T>(parsed));

        var cache = services.GetRequiredService<AppDirectoryCache>();
        var arrangements = services.GetRequiredService<LauncherArrangementSource>();
        // First directory render for this viewer: the seed of their arrangement is COMPOSED here
        // — rows wait for it, so a drop always has an arrangement node to write to.
        var arrangement = arrangements.ObserveSeeded(owner);

        return cache.ForViewer(owner)
            .CombineLatest(arrangement, (entries, placed) => Rows(owner, entries, placed, options))
            .Select(rows => Filter(rows, parsed))
            .Scan((Previous: (ImmutableDictionary<string, MeshNode>?)null, Changes: (IReadOnlyList<QueryResultChange<T>>)[]),
                (state, rows) =>
                {
                    var current = rows.ToImmutableDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
                    return (current, Diff<T>(state.Previous, current, parsed));
                })
            .SelectMany(state => state.Changes);
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

    /// <summary>
    /// Whether one of <paramref name="queries"/> asks for a directory, and whose:
    /// <c>path:{user}/_Apps scope:children</c> or <c>namespace:{user}/_Apps</c>.
    /// </summary>
    internal static (string Owner, ParsedQuery Parsed)? Target(IEnumerable<ParsedQuery> queries)
    {
        foreach (var parsed in queries)
        {
            if (parsed.Scope == QueryScope.Children && OwnerOf(parsed.Path) is { } owner)
                return (owner, parsed);
            foreach (var ns in parsed.ExtractNamespaces())
                if (OwnerOf(ns) is { } nsOwner)
                    return (nsOwner, parsed);
        }
        return null;
    }

    private static string? OwnerOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var parts = path.Trim('/').Split('/');
        return parts.Length == 2 && parts[0].Length > 0
            && string.Equals(parts[1], LauncherArrangementPaths.DirectoryNamespace, StringComparison.Ordinal)
            ? parts[0]
            : null;
    }

    /// <summary>
    /// The synthetic rows: one <see cref="AppNodeType.NodeType"/> node per visible app, its group
    /// from the viewer's arrangement, else the root's category. Pure.
    /// </summary>
    public static ImmutableList<MeshNode> Rows(
        string owner, IEnumerable<AppDirectoryEntry> entries, LauncherArrangement? arrangement,
        JsonSerializerOptions options) =>
        entries.Select(entry =>
        {
            var placed = arrangement?.For(entry.Id);
            return new MeshNode(LauncherArrangementPaths.RowIdFor(entry.Id), LauncherArrangementPaths.DirectoryNamespaceFor(owner))
            {
                NodeType = AppNodeType.NodeType,
                Name = entry.Name,
                Icon = entry.Icon,
                Description = entry.Description,
                Category = entry.Category,
                MainNode = entry.OpenPath,
                State = MeshNodeState.Active,
                Content = new App
                {
                    Plugin = entry.BuiltIn ? "" : entry.Id,
                    OpenPath = entry.OpenPath,
                    Group = placed is null ? entry.Category : placed.Group,
                    Order = placed?.Order ?? 0,
                    Source = "directory",
                    LabelKey = entry.LabelKey,
                },
            };
        }).ToImmutableList();

    /// <summary>Honours the query's <c>nodeType:</c> filter — the only filter a launcher sends.</summary>
    private static ImmutableList<MeshNode> Filter(ImmutableList<MeshNode> rows, ParsedQuery parsed) =>
        parsed.ExtractNodeType() is { } nodeType
        && !string.Equals(nodeType, AppNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
            ? ImmutableList<MeshNode>.Empty
            : rows;

    /// <summary>The first emission is the <c>Initial</c>; every later one the deltas against the
    /// last — removals, additions and updates, each its own change (live consumers fold by path),
    /// and nothing when the rows did not change. Pure.</summary>
    internal static IReadOnlyList<QueryResultChange<T>> Diff<T>(
        ImmutableDictionary<string, MeshNode>? previous, ImmutableDictionary<string, MeshNode> current, ParsedQuery parsed)
    {
        if (previous is null)
            return [Change<T>(QueryChangeType.Initial, current.Values, parsed)];
        var changes = new List<QueryResultChange<T>>(3);
        var removed = previous.Values.Where(n => !current.ContainsKey(n.Path)).ToList();
        var added = current.Values.Where(n => !previous.ContainsKey(n.Path)).ToList();
        var updated = current.Values
            .Where(n => previous.TryGetValue(n.Path, out var before) && !SameRow(before, n)).ToList();
        if (removed.Count > 0)
            changes.Add(Change<T>(QueryChangeType.Removed, removed, parsed));
        if (added.Count > 0)
            changes.Add(Change<T>(QueryChangeType.Added, added, parsed));
        if (updated.Count > 0)
            changes.Add(Change<T>(QueryChangeType.Updated, updated, parsed));
        return changes;
    }

    private static bool SameRow(MeshNode a, MeshNode b) =>
        a.Name == b.Name && a.Icon == b.Icon && a.Description == b.Description && a.Category == b.Category
        && a.MainNode == b.MainNode && Equals(a.Content, b.Content);

    private static QueryResultChange<T> Change<T>(QueryChangeType type, IEnumerable<MeshNode> nodes, ParsedQuery parsed) =>
        new()
        {
            ChangeType = type,
            Items = nodes.Cast<T>().ToList(),
            Query = parsed,
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static QueryResultChange<T> Empty<T>(ParsedQuery parsed) =>
        new()
        {
            ChangeType = QueryChangeType.Initial,
            Items = [],
            Query = parsed,
            Timestamp = DateTimeOffset.UtcNow,
        };
}
