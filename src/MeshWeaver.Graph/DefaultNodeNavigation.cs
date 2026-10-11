using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Core's DEFAULT left-hand index for a markdown page — what a page shows when no module's
/// <see cref="INodeNavigationProvider"/> claims it.
///
/// <para><b>What it indexes.</b> The tree of the page's SPACE — its <see cref="IndexRoot"/>, the
/// first path segment (<c>Infrastructure</c> for every page under it, <c>Doc</c> for the docs,
/// <c>{viewer}</c> inside a home). Every page of the space shows the SAME index — the space's
/// children in their declared order, an entry with children as a collapsible group, the groups on
/// the reader's path open, and the page being read marked as current. The index used to be the
/// page's OWN children, which is right on the root of a document tree and wrong one level down: a
/// sub-page has no children, so its index vanished the moment the reader clicked into it, and
/// nothing told them where they were (reported 2026-09-14 on Infrastructure/Inference). A first
/// fix rooted the index one level below the space, which put the space's own pages —
/// <c>Infrastructure/Options</c> beside <c>Infrastructure/Inference</c> — outside every index
/// again: a page directly under the space is not its own document, it is a chapter of the space's
/// (reported 2026-09-15 on Infrastructure/Options: "Infra has one index; show it, with where we
/// are").</para>
///
/// <para><b>Why the space.</b> A space is the document; its overview is its title page. One index
/// per space, the same on every page, is what a reader can hold in their head — a deep tree stays
/// legible because only the groups on the reader's path open. A space's own root page shows the
/// index too, so the rail is the one place that never moves.</para>
///
/// <para>Pure at its core: <see cref="Build(string, IReadOnlyCollection{MeshNode}, string)"/> turns one subtree snapshot into the index, so
/// structure, ordering, exclusions and position marking are pinned without a mesh.</para>
/// </summary>
public static class DefaultNodeNavigation
{
    /// <summary>
    /// The root of the index the page at <paramref name="currentPath"/> shows: its space — the
    /// first path segment — or the page itself when the path has none.
    /// </summary>
    /// <param name="currentPath">The page being read.</param>
    public static string IndexRoot(string currentPath)
    {
        var segments = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 1 ? segments[0] : currentPath;
    }

    /// <summary>
    /// The live index for the page <paramref name="host"/> renders — null while the tree has no
    /// entries (a page with nothing beside or below it shows no rail). One direct-children query
    /// per level on the reader's path (<see cref="PathLevels"/>) through the UNIFIED
    /// <see cref="IMeshService"/> — a hub-scoped query answers from the page's own snapshot on a
    /// distributed mesh and would never see the siblings. The query is a CHANGE FEED — only
    /// Initial/Reset carry the whole tree; Added/Updated/Removed carry the rows that changed — so
    /// it is folded into a snapshot first (<see cref="Fold"/>), and every emission rebuilds the
    /// index from the whole tree: an added, renamed, re-ordered or deleted page re-renders it live.
    /// <para>🚨 <b>The page never waits for the index.</b> The stream emits <c>null</c> at once and
    /// the index when the query answers, and a query that faults is logged and read as "no
    /// index" — the same two guards <c>MarkdownOverviewLayoutArea.SuppliedNavigation</c> puts on a
    /// module's provider, for the same reason: the Overview combines this with the node and
    /// permission streams, so a stream that stays silent holds the WHOLE page back. Without them
    /// this stream did exactly that in the first set it shipped in (core CD #8599, 2026-09-14):
    /// on a test mesh whose partition has no persisted root the subtree query never answered, and
    /// two read-view tests that had passed on the previous set timed out at 20 s with nothing
    /// rendered. A page with its index a beat late is a page; a page that never renders is not.</para>
    /// </summary>
    /// <param name="host">The layout-area host rendering the page; its hub address IS the node path.</param>
    public static IObservable<NodeNavigation?> Observe(LayoutAreaHost host)
    {
        var currentPath = host.Hub.Address.ToString();
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (string.IsNullOrEmpty(currentPath) || meshService is null)
            return Observable.Return<NodeNavigation?>(null);

        var root = IndexRoot(currentPath);
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.DefaultNodeNavigation");
        // 🚨 NEVER the whole partition (policy explorer-navigation). One DIRECT-CHILDREN read per
        // level on the reader's path — the root's children, each ancestor's children, the page's
        // own children — so the cost grows with the page's DEPTH, never with the size of the
        // space. A folder off the path is listed but not opened; the reader drills into it by
        // opening it, and its level loads on that page.
        // A level SHOWS at most LevelCap entries; "Load more" raises ONE level's cap by MoreStep
        // through the page's own ?more={level}:{n} parameter.
        var more = ParseMore(host.Reference.HasParameter(MoreParameter)
            ? host.Reference.GetParameterValue(MoreParameter) : null);
        int CapOf(string ns) => more is { } m && string.Equals(m.Level, ns, StringComparison.Ordinal) ? m.Cap : LevelCap;
        // The cap is applied in the order the level is RENDERED (declared Order, then name, internal
        // `_` entries left out) — never in the store's row order, which would let hidden satellites
        // eat the budget and push an Order = 1 page behind "Load more". So each level reads LIGHT
        // rows (no content) up to the hard bound, and the cut happens after the render order.
        // 🚨 The read itself is ordered by PATH, stated in the query: a filter-only query is
        // otherwise clipped newest-first, so which rows a very large level contributes would
        // change with every edit. The bound is honest about what it is: a level holding more
        // than MaxCap rows lists the first MaxCap + 1 BY LOCATION, rendered in declared order
        // within that window; a row beyond the window is reached by search, never by this rail.
        // Rendering order applied by the store before its clip (two keys, nulls last) is not
        // expressible in a query today — policy explorer-navigation lists it as owed.
        IObservable<(string Level, ImmutableDictionary<string, MeshNode> Nodes)> Level(string ns) =>
            meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{ns} is:main select:path,id,namespace,name,nodeType,order,icon sort:path-asc limit:{MaxCap + 1}"))
                .Scan(ImmutableDictionary<string, MeshNode>.Empty, Fold)
                // Each level paints on its own: a slow level never holds the others back.
                .StartWith(ImmutableDictionary<string, MeshNode>.Empty)
                .Select(nodes => (ns, nodes));
        var pathLevels = PathLevels(root, currentPath);
        var levels = pathLevels.Select(Level).ToImmutableList();
        // The reader's own branch: every node on the path below the root, read BY PATH (one light
        // row each, depth-bound like the levels). A level shows its first LevelCap entries, and the
        // page being read may sit beyond that cut — or beyond the level's read window — so its
        // ancestors and itself are always added, or the branch the reader is on would vanish and
        // its children would have no parent to hang from.
        IObservable<MeshNode?> Anchor(string path) =>
            meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{path} select:path,id,namespace,name,nodeType,order,icon"))
                .Scan(ImmutableDictionary<string, MeshNode>.Empty, Fold)
                .Select(rows => rows.TryGetValue(path, out var row) ? row : null)
                .StartWith((MeshNode?)null);
        var anchors = pathLevels.Skip(1).Select(Anchor).ToImmutableList();
        var anchorRows = anchors.IsEmpty
            ? Observable.Return((IList<MeshNode?>)ImmutableList<MeshNode?>.Empty)
            : anchors.CombineLatest();
        // The root node (its title and icon head the rail) through the workspace's authoritative
        // partition-root stream, never a by-path query against the index.
        // On the root's OWN page the root is the node being read: ObservePartitionRoot answers a
        // constant null for a single-segment path (no DISTINCT root), which would drop the root's
        // name and icon from the rail heading exactly there.
        var rootNode = string.Equals(currentPath, root, StringComparison.Ordinal)
            ? host.Workspace.GetMeshNodeStream()
                .Select(node => (MeshNode?)node)
                .Catch<MeshNode?, Exception>(ex =>
                    MeshWeaver.Layout.AreaErrorClassifier.IsExpectedUserActionFailure(ex)
                        ? Observable.Return<MeshNode?>(null)
                        : Observable.Throw<MeshNode?>(ex))
                .StartWith((MeshNode?)null)
            : host.Workspace.ObservePartitionRoot(currentPath).StartWith((MeshNode?)null);
        var loadMore = LocalizationCatalog.Get("common.loadMore", host.ViewerLocale());
        return Guard(
            Observable.Defer(() => levels
                .CombineLatest()
                .CombineLatest(rootNode, anchorRows, (read, rootRow, onPath) =>
                {
                    var shown = read.Select(l => (l.Level, Cap: CapOf(l.Level), Visible: VisibleInOrder(l.Nodes.Values))).ToImmutableList();
                    var moreLinks = shown
                        // Nothing more to offer once a level sits at the hard bound.
                        .Where(l => l.Visible.Count > l.Cap && l.Cap < MaxCap)
                        .ToImmutableDictionary(
                            l => l.Level,
                            l => $"{currentPath}?{MoreParameter}={Uri.EscapeDataString(l.Level)}:{Math.Min(l.Cap + MoreStep, MaxCap)}",
                            StringComparer.Ordinal);
                    var nodes = RenderedRows(
                        shown.Select(l => (l.Visible, l.Cap)),
                        onPath.OfType<MeshNode>().Concat(rootRow is null ? [] : [rootRow]));
                    return Build(root, nodes, currentPath, moreLinks, loadMore);
                })),
            ex => logger?.LogWarning(ex,
                "The default index query for {Root} faulted — the page renders without its index",
                root));
    }

    /// <summary>
    /// The rows the rail renders: each level's first <c>Cap</c> visible entries, plus the rows that
    /// are always shown whatever the cut — the reader's own branch and the root. Duplicates are
    /// dropped by path. Pure.
    /// </summary>
    /// <param name="levels">Each level's visible entries in rendered order, and its cap.</param>
    /// <param name="alwaysShown">The rows on the reader's path and the root row.</param>
    public static ImmutableList<MeshNode> RenderedRows(
        IEnumerable<(IReadOnlyList<MeshNode> Visible, int Cap)> levels, IEnumerable<MeshNode> alwaysShown) =>
        levels
            .SelectMany(l => l.Visible.Take(l.Cap))
            .Concat(alwaysShown)
            .GroupBy(n => n.Path, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToImmutableList();

    /// <summary>The most children one level of the index lists at first; a level that holds more
    /// ends with a "Load more" entry (policy <c>explorer-navigation</c>).</summary>
    public const int LevelCap = 50;

    /// <summary>How many more children one "Load more" adds to its level.</summary>
    public const int MoreStep = 50;

    /// <summary>The page parameter a "Load more" link sets: <c>{level}:{cap}</c>.</summary>
    public const string MoreParameter = "more";

    /// <summary>The most entries one level can ever be raised to; a level at this bound offers no
    /// further "Load more" (the reader opens the folder, or searches it).</summary>
    public const int MaxCap = 10 * LevelCap;

    /// <summary>The label of the load-more entry when the caller supplies none (English; the page
    /// passes the viewer's <c>common.loadMore</c>).</summary>
    public const string LoadMoreLabel = "Load more";

    /// <summary>One level's entries as the rail lists them: internal <c>_</c> entries left out, in
    /// the rendered order (<see cref="MarkdownOverviewLayoutArea.OrderSubNodes"/>). Pure.</summary>
    public static IReadOnlyList<MeshNode> VisibleInOrder(IEnumerable<MeshNode> level) =>
        MarkdownOverviewLayoutArea.OrderSubNodes(level.Where(n => !LastSegment(n.Path).StartsWith('_')))
            .ToImmutableList();

    /// <summary>Reads the DECODED value of <c>?more={level}:{cap}</c>: the one level whose cap is raised, and to what —
    /// null for a missing or malformed value, and a cap below <see cref="LevelCap"/> is never
    /// applied. Pure.</summary>
    public static (string Level, int Cap)? ParseMore(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        // Already URI-decoded by the area reference's parameter parsing — decoding again would turn
        // a literal %2F in a level name into a separator.
        var raw = value;
        var colon = raw.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(raw[(colon + 1)..], out var cap) || cap <= LevelCap)
            return null;
        return (raw[..colon], Math.Min(cap, MaxCap));
    }

    /// <summary>
    /// The namespaces whose DIRECT children the index loads for the page at
    /// <paramref name="currentPath"/>: the root, every ancestor down from it, and the page itself —
    /// in that order, each once. A page outside <paramref name="root"/> loads the root alone. Pure.
    /// </summary>
    /// <param name="root">The index root (<see cref="IndexRoot"/>).</param>
    /// <param name="currentPath">The page being read.</param>
    public static ImmutableList<string> PathLevels(string root, string currentPath)
    {
        var levels = ImmutableList.Create(root);
        if (!currentPath.StartsWith(root + "/", StringComparison.Ordinal))
            return levels;
        var path = root;
        foreach (var segment in currentPath[(root.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = $"{path}/{segment}";
            levels = levels.Add(path);
        }
        return levels;
    }

    /// <summary>
    /// The two guards that keep an index stream from holding the page: an immediate first
    /// emission (<c>null</c> — no index yet), and a fault turned into "no index" after
    /// <paramref name="onFault"/> has seen it. Pure over the stream, so the shape is pinned by a
    /// test that never touches a mesh: a source that stays silent still lets the page render, and
    /// one that throws still lets it render.
    /// </summary>
    /// <param name="index">The index stream as the query produces it.</param>
    /// <param name="onFault">Sees the fault before it is swallowed — the log line.</param>
    public static IObservable<NodeNavigation?> Guard(
        IObservable<NodeNavigation?> index, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(onFault);
        return index
            .Catch((Exception ex) =>
            {
                onFault(ex);
                return Observable.Return<NodeNavigation?>(null);
            })
            .StartWith((NodeNavigation?)null);
    }

    /// <summary>
    /// One step of the change feed into the tree snapshot, by path: Initial/Reset replace it,
    /// Added/Updated set the changed rows, Removed drops them. Pure — pinned by the tests, since
    /// projecting <c>change.Items</c> straight to the rail read as correct and rebuilt the index
    /// from ONE changed row after the first live change.
    /// </summary>
    /// <param name="tree">The snapshot so far.</param>
    /// <param name="change">The next change.</param>
    public static ImmutableDictionary<string, MeshNode> Fold(
        ImmutableDictionary<string, MeshNode> tree, QueryResultChange<MeshNode> change)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(change);
        var items = change.Items ?? [];
        if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            return items.Where(n => !string.IsNullOrEmpty(n.Path))
                .ToImmutableDictionary(n => n.Path, StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Path))
                continue;
            tree = change.ChangeType switch
            {
                QueryChangeType.Added or QueryChangeType.Updated => tree.SetItem(item.Path, item),
                QueryChangeType.Removed => tree.Remove(item.Path),
                _ => tree,
            };
        }
        return tree;
    }

    /// <summary>
    /// The index of one subtree snapshot. The root's children become the entries, each child's
    /// children its nested entries, every level ordered by <see cref="MeshNode.Order"/> then name
    /// (<see cref="MarkdownOverviewLayoutArea.OrderSubNodes"/>); internal satellites (a segment
    /// starting with <c>_</c>) are left out; the entry whose path IS <paramref name="currentPath"/>
    /// is marked current. Null when the root has no children — no rail, as before. Pure.
    /// </summary>
    /// <param name="root">The index root (<see cref="IndexRoot"/>).</param>
    /// <param name="subtree">The root's subtree — the root itself included when it exists.</param>
    /// <param name="currentPath">The page being read.</param>
    public static NodeNavigation? Build(string root, IReadOnlyCollection<MeshNode> subtree, string currentPath)
        => Build(root, subtree, currentPath, moreLinks: null, loadMoreLabel: null);

    /// <summary>
    /// <see cref="Build(string, IReadOnlyCollection{MeshNode}, string)"/> for a lazily loaded tree: a
    /// level named in <paramref name="moreLinks"/> ends with an entry (labelled
    /// <paramref name="loadMoreLabel"/>, else <see cref="LoadMoreLabel"/>) whose path is the link
    /// that loads the level's next batch. A separate overload, so the three-parameter one keeps the
    /// metadata signature compiled plugins bind. Pure.
    /// </summary>
    /// <param name="root">The index root (<see cref="IndexRoot"/>).</param>
    /// <param name="subtree">The loaded nodes — the root itself included when it exists.</param>
    /// <param name="currentPath">The page being read.</param>
    /// <param name="moreLinks">Per level that holds more than it shows: the link that loads more.</param>
    /// <param name="loadMoreLabel">The viewer's label for that entry.</param>
    public static NodeNavigation? Build(
        string root, IReadOnlyCollection<MeshNode> subtree, string currentPath,
        IReadOnlyDictionary<string, string>? moreLinks, string? loadMoreLabel)
    {
        ArgumentNullException.ThrowIfNull(subtree);
        var entries = ChildrenOf(root, subtree, currentPath, moreLinks, loadMoreLabel ?? LoadMoreLabel);
        if (entries.Count == 0)
            return null;

        var rootNode = subtree.FirstOrDefault(n => string.Equals(n.Path, root, StringComparison.Ordinal));
        return new NodeNavigation(rootNode?.Name ?? rootNode?.Id ?? LastSegment(root), entries)
        {
            TitlePath = root,
            // Resolved, not raw: a node without an icon of its own still reads as its type.
            Icon = MeshNodeImageHelper.ResolveNodeIcon(rootNode),
        };
    }

    private static IReadOnlyList<NodeNavigationEntry> ChildrenOf(
        string parent, IReadOnlyCollection<MeshNode> subtree, string currentPath,
        IReadOnlyDictionary<string, string>? moreLinks, string loadMoreLabel)
    {
        var children = MarkdownOverviewLayoutArea
            .OrderSubNodes(subtree.Where(n => IsChildOf(n.Path, parent) && !LastSegment(n.Path).StartsWith('_')))
            .Select(n => new NodeNavigationEntry(
                n.Name ?? n.Id,
                n.Path,
                string.Equals(n.Path, currentPath, StringComparison.Ordinal),
                MeshNodeImageHelper.ResolveNodeIcon(n))
            {
                Children = ChildrenOf(n.Path, subtree, currentPath, moreLinks, loadMoreLabel),
            })
            .ToImmutableList();
        // A level that holds more than it shows ends with the entry that loads the next batch.
        return children.Count > 0 && moreLinks is not null && moreLinks.TryGetValue(parent, out var more)
            ? children.Add(new NodeNavigationEntry(loadMoreLabel, more, false, null))
            : children;
    }

    private static bool IsChildOf(string path, string parent)
        => path.Length > parent.Length + 1
           && path.StartsWith(parent + "/", StringComparison.Ordinal)
           && path.IndexOf('/', parent.Length + 1) < 0;

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }
}
