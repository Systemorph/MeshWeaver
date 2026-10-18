using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Layout;
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
        // through the page's own ?more={level}:{n} parameter, "Show all" opens the level in the
        // mesh search, and ?sort={level}:{mode} re-sorts ONE level (the dropdown on its heading).
        var more = ParseMore(host.Reference.HasParameter(MoreParameter)
            ? host.Reference.GetParameterValue(MoreParameter) : null);
        var sort = ParseSort(host.Reference.HasParameter(SortParameter)
            ? host.Reference.GetParameterValue(SortParameter) : null);
        int CapOf(string ns) => more is { } m && string.Equals(m.Level, ns, StringComparison.Ordinal) ? m.Cap : LevelCap;
        IndexSort SortOf(string ns) => sort is { } s && string.Equals(s.Level, ns, StringComparison.Ordinal) ? s.Sort : IndexSort.Order;
        // 🚨 A level reads only what it SHOWS, plus one row to know there is more: every leg below
        // is a query bounded to Cap + 1, ordered BY THE STORE in the key the level renders by
        // (LevelQueries). A two-key order (Order, then newest) is two legs — the ordered rows and
        // the newest rows — whose union holds the first Cap + 1 of the combined order, because
        // nulls sort last in both directions on every backend (QuerySyntax, "sort:").
        IObservable<(string Level, RankedRows Rows)> Level(string ns) =>
            LevelQueries(ns, SortOf(ns), CapOf(ns))
                .Select(query => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                    .Scan(RankedRows.Empty, FoldRanked)
                    // Each leg paints on its own: a slow leg never holds the others back.
                    .StartWith(RankedRows.Empty))
                .CombineLatest()
                .Select(legs => (ns, RankedRows.Union(legs)));
        var pathLevels = PathLevels(root, currentPath);
        var levels = pathLevels.Select(Level).ToImmutableList();
        // The reader's own branch: every node on the path below the root, read BY PATH (one light
        // row each, depth-bound like the levels). A level shows its first Cap entries, and the
        // page being read may sit beyond that cut, so its ancestors and itself are always added,
        // or the branch the reader is on would vanish and its children would have no parent.
        IObservable<MeshNode?> Anchor(string path) =>
            meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{path} select:{SelectFields}"))
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
        var locale = host.ViewerLocale();
        var labels = new IndexLabels(
            LocalizationCatalog.Get("common.loadMore", locale),
            LocalizationCatalog.Get("common.showAll", locale),
            mode => LocalizationCatalog.Get(SortLabelKey(mode), locale));
        return Guard(
            Observable.Defer(() => levels
                .CombineLatest()
                .CombineLatest(rootNode, anchorRows, (read, rootRow, onPath) =>
                {
                    var shown = read
                        .Select(l => (l.Level, Cap: CapOf(l.Level), Sort: SortOf(l.Level), l.Rows,
                            Visible: VisibleInOrder(l.Rows.Nodes.Values, SortOf(l.Level), l.Rows.RankOf)))
                        .ToImmutableList();
                    var views = shown.ToImmutableDictionary(
                        l => l.Level,
                        l => new IndexLevel
                        {
                            Sort = l.Sort,
                            ViewedRank = l.Rows.RankOf,
                            // Nothing more to load once a level sits at the hard bound — Show all
                            // still opens the whole level in the search.
                            MoreHref = l.Visible.Count > l.Cap && l.Cap < MaxCap
                                ? PageHref(currentPath, (MoreParameter, $"{l.Level}:{Math.Min(l.Cap + MoreStep, MaxCap)}"),
                                    sort is { } s ? (SortParameter, $"{s.Level}:{SortToken(s.Sort)}") : null)
                                : null,
                            ShowAllHref = l.Visible.Count > l.Cap ? ShowAllHref(l.Level, l.Sort) : null,
                            // Re-sorting resets the level's "Load more": the batch is a window
                            // over ONE order, and a new order starts at its first page.
                            SortOptions = SortChoices(l.Sort,
                                mode => PageHref(currentPath,
                                    (SortParameter, $"{l.Level}:{SortToken(mode)}"),
                                    more is { } m && !string.Equals(m.Level, l.Level, StringComparison.Ordinal)
                                        ? (MoreParameter, $"{m.Level}:{m.Cap}") : null),
                                labels.SortLabel),
                        },
                        StringComparer.Ordinal);
                    var nodes = RenderedRows(
                        shown.Select(l => (l.Visible, l.Cap)),
                        onPath.OfType<MeshNode>().Concat(rootRow is null ? [] : [rootRow]));
                    return Build(root, nodes, currentPath, views, labels);
                })),
            ex => logger?.LogWarning(ex,
                "The default index query for {Root} faulted — the page renders without its index",
                root));
    }

    /// <summary>The light row every index read selects — no content, just what a rail line shows
    /// and what the levels sort by.</summary>
    private const string SelectFields = "path,id,namespace,name,nodeType,order,icon,lastModified";

    /// <summary>
    /// The queries that read ONE level of the index for <paramref name="sort"/>, each bounded to
    /// <paramref name="cap"/> + 1 rows and ordered by the store in the key the level renders by — so
    /// the level never reads more than it shows plus the one row that says there is more. A two-key
    /// order is two legs whose union contains the first <c>cap + 1</c> rows of the combined order:
    /// <list type="bullet">
    ///   <item><see cref="IndexSort.Order"/> — the ordered rows (<c>sort:order-asc</c>; unordered
    ///     ones sort last) and the newest rows (<c>sort:lastModified-desc</c>).</item>
    ///   <item><see cref="IndexSort.Viewed"/> — the viewer's most recently opened rows
    ///     (<c>source:accessed</c>, an INNER join on their access log) and the newest rows, so a page
    ///     the viewer never opened is still listed after the ones they did.</item>
    ///   <item><see cref="IndexSort.Modified"/> and <see cref="IndexSort.Name"/> — one leg each.</item>
    /// </list>
    /// The read is a path-tree level (<c>namespace:{level}</c>, direct children) of main nodes. Pure.
    /// </summary>
    /// <param name="level">The namespace whose direct children the level lists.</param>
    /// <param name="sort">How the level is sorted.</param>
    /// <param name="cap">How many entries the level shows.</param>
    public static ImmutableList<string> LevelQueries(string level, IndexSort sort, int cap)
    {
        var read = $"namespace:{level} is:main select:{SelectFields} limit:{cap + 1}";
        var newest = $"{read} sort:lastModified-desc";
        return sort switch
        {
            IndexSort.Modified => [newest],
            IndexSort.Name => [$"{read} sort:name-asc"],
            IndexSort.Viewed => [$"{read} source:accessed", newest],
            _ => [$"{read} sort:order-asc", newest],
        };
    }

    /// <summary>The mesh-search URL that lists a level whole — its direct children, in the level's
    /// primary sort key ("Show all"). Pure.</summary>
    /// <param name="level">The namespace whose children the level lists.</param>
    /// <param name="sort">The level's sort; the search orders by its primary key.</param>
    public static string ShowAllHref(string level, IndexSort sort)
    {
        var order = sort switch
        {
            IndexSort.Name => "sort:name-asc",
            IndexSort.Order => "sort:order-asc",
            // The access log hides what the viewer never opened; the whole level is newest first.
            _ => "sort:lastModified-desc",
        };
        return $"/search?ns={Uri.EscapeDataString(level)}&hq={Uri.EscapeDataString($"namespace:{level} is:main {order}")}";
    }

    /// <summary>The page's own URL with the index parameters given — a null pair is left out. Pure.</summary>
    private static string PageHref(string currentPath, (string Name, string Value)? first, (string Name, string Value)? second)
    {
        var query = string.Join("&", new[] { first, second }
            .OfType<(string Name, string Value)>()
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
        return query.Length == 0 ? "/" + currentPath : $"/{currentPath}?{query}";
    }

    /// <summary>The dropdown of one level: every <see cref="IndexSort"/>, the one in effect marked. Pure.</summary>
    private static ImmutableList<NavSortOption> SortChoices(
        IndexSort current, Func<IndexSort, string> href, Func<IndexSort, string> label) =>
        [.. Enum.GetValues<IndexSort>().Select(mode => new NavSortOption(label(mode), href(mode), mode == current))];

    /// <summary>The localization key naming a sort mode in the dropdown.</summary>
    /// <param name="sort">The mode.</param>
    public static string SortLabelKey(IndexSort sort) => "nav.sort." + SortToken(sort);

    /// <summary>The URL spelling of a sort mode (<c>?sort={level}:{token}</c>).</summary>
    /// <param name="sort">The mode.</param>
    public static string SortToken(IndexSort sort) => sort switch
    {
        IndexSort.Modified => "modified",
        IndexSort.Viewed => "viewed",
        IndexSort.Name => "name",
        _ => "order",
    };

    /// <summary>Reads the DECODED value of <c>?sort={level}:{mode}</c>: the one level re-sorted, and
    /// how — null for a missing or malformed value. Pure.</summary>
    /// <param name="value">The parameter value.</param>
    public static (string Level, IndexSort Sort)? ParseSort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var colon = value.LastIndexOf(':');
        if (colon <= 0)
            return null;
        var token = value[(colon + 1)..];
        var mode = Enum.GetValues<IndexSort>()
            .Select(m => (IndexSort?)m)
            .FirstOrDefault(m => string.Equals(SortToken(m!.Value), token, StringComparison.OrdinalIgnoreCase));
        return mode is { } sort ? (value[..colon], sort) : null;
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
    /// ends with "Load more" and "Show all" entries (policy <c>explorer-navigation</c>).</summary>
    public const int LevelCap = 10;

    /// <summary>How many more children one "Load more" adds to its level.</summary>
    public const int MoreStep = 10;

    /// <summary>The page parameter the sort dropdown sets: <c>{level}:{mode}</c>.</summary>
    public const string SortParameter = "sort";

    /// <summary>The page parameter a "Load more" link sets: <c>{level}:{cap}</c>.</summary>
    public const string MoreParameter = "more";

    /// <summary>The most entries one level can ever be raised to; a level at this bound offers no
    /// further "Load more" — "Show all" opens the whole level in the mesh search.</summary>
    public const int MaxCap = 10 * LevelCap;

    /// <summary>The label of the load-more entry when the caller supplies none (English; the page
    /// passes the viewer's <c>common.loadMore</c>).</summary>
    public const string LoadMoreLabel = "Load more";

    /// <summary>The label of the show-all entry when the caller supplies none (English; the page
    /// passes the viewer's <c>common.showAll</c>).</summary>
    public const string ShowAllLabel = "Show all";

    /// <summary>One level's entries as the rail lists them in the default order: internal <c>_</c>
    /// entries left out, declared Order first, then the most recently modified. Pure.</summary>
    public static IReadOnlyList<MeshNode> VisibleInOrder(IEnumerable<MeshNode> level) =>
        VisibleInOrder(level, IndexSort.Order, null);

    /// <summary>One level's entries as the rail lists them for <paramref name="sort"/>: internal
    /// <c>_</c> entries left out, in <see cref="SortLevel"/>'s order. Pure.</summary>
    /// <param name="level">The level's rows.</param>
    /// <param name="sort">How the level is sorted.</param>
    /// <param name="viewedRank">For <see cref="IndexSort.Viewed"/>: each opened row's position in the
    /// viewer's access order (0 = most recent).</param>
    public static IReadOnlyList<MeshNode> VisibleInOrder(
        IEnumerable<MeshNode> level, IndexSort sort, IReadOnlyDictionary<string, int>? viewedRank) =>
        SortLevel(level.Where(n => !LastSegment(n.Path).StartsWith('_')), sort, viewedRank);

    /// <summary>
    /// The order of one level. <see cref="IndexSort.Order"/>, the default: declared
    /// <see cref="MeshNode.Order"/> first (unordered last), then the most recently modified.
    /// <see cref="IndexSort.Viewed"/>: the viewer's most recently opened first, then the most recently
    /// modified. <see cref="IndexSort.Modified"/>: newest first. <see cref="IndexSort.Name"/>: A–Z.
    /// Ties fall back to the name, so the order is total. Pure.
    /// </summary>
    /// <param name="level">The level's rows.</param>
    /// <param name="sort">How the level is sorted.</param>
    /// <param name="viewedRank">For <see cref="IndexSort.Viewed"/>: each opened row's access position.</param>
    public static ImmutableList<MeshNode> SortLevel(
        IEnumerable<MeshNode> level, IndexSort sort, IReadOnlyDictionary<string, int>? viewedRank = null)
    {
        static string NameOf(MeshNode n) => n.Name ?? n.Id;
        var ordered = sort switch
        {
            IndexSort.Name => level.OrderBy(NameOf, StringComparer.OrdinalIgnoreCase),
            IndexSort.Modified => level.OrderByDescending(n => n.LastModified),
            IndexSort.Viewed => level
                .OrderBy(n => viewedRank is not null && viewedRank.TryGetValue(n.Path, out var rank) ? rank : int.MaxValue)
                .ThenByDescending(n => n.LastModified),
            _ => level.OrderBy(n => n.Order ?? int.MaxValue).ThenByDescending(n => n.LastModified),
        };
        return [.. ordered.ThenBy(NameOf, StringComparer.OrdinalIgnoreCase)];
    }

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
    /// One leg's rows of a level, with the ORDER the store returned them in — which is the only
    /// place a <c>source:accessed</c> read carries the viewer's access recency (the row has no field
    /// for it). <see cref="RankOf"/> is that order by path.
    /// </summary>
    /// <param name="Nodes">The rows by path.</param>
    /// <param name="Rank">The paths in the store's order.</param>
    public sealed record RankedRows(ImmutableDictionary<string, MeshNode> Nodes, ImmutableList<string> Rank)
    {
        /// <summary>No rows yet.</summary>
        public static RankedRows Empty { get; } = new(
            ImmutableDictionary<string, MeshNode>.Empty.WithComparers(StringComparer.Ordinal), []);

        /// <summary>Each path's position in <see cref="Rank"/>.</summary>
        public ImmutableDictionary<string, int> RankOf =>
            Rank.Select((path, i) => (path, i)).ToImmutableDictionary(p => p.path, p => p.i, StringComparer.Ordinal);

        /// <summary>
        /// The legs of one level as one: every row by path, and the FIRST leg's order — the leg the
        /// level ranks by (the access log for <see cref="IndexSort.Viewed"/>; the others rank by row
        /// fields and ignore it). Pure.
        /// </summary>
        /// <param name="legs">The level's legs, the ranking one first.</param>
        public static RankedRows Union(IList<RankedRows> legs) =>
            new(legs.SelectMany(l => l.Nodes)
                    .GroupBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToImmutableDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal),
                legs.Count == 0 ? [] : legs[0].Rank);
    }

    /// <summary>
    /// <see cref="Fold"/> that also keeps the store's ORDER: Initial/Reset take the rows' order,
    /// an Added or Updated row moves to the FRONT (on an access-ordered read that is the page just
    /// opened), a Removed row leaves. Pure.
    /// </summary>
    /// <param name="rows">The rows so far.</param>
    /// <param name="change">The next change.</param>
    public static RankedRows FoldRanked(RankedRows rows, QueryResultChange<MeshNode> change)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(change);
        var nodes = Fold(rows.Nodes, change);
        var items = (change.Items ?? []).Where(n => !string.IsNullOrEmpty(n.Path)).Select(n => n.Path).ToList();
        var rank = change.ChangeType switch
        {
            QueryChangeType.Initial or QueryChangeType.Reset => items.Distinct(StringComparer.Ordinal).ToImmutableList(),
            QueryChangeType.Added or QueryChangeType.Updated =>
                items.Distinct(StringComparer.Ordinal).Concat(rows.Rank.Where(p => !items.Contains(p, StringComparer.Ordinal))).ToImmutableList(),
            QueryChangeType.Removed => rows.Rank.Where(p => !items.Contains(p, StringComparer.Ordinal)).ToImmutableList(),
            _ => rows.Rank,
        };
        return new RankedRows(nodes, rank);
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
        => Build(root, subtree, currentPath,
            (moreLinks ?? ImmutableDictionary<string, string>.Empty)
                .ToImmutableDictionary(kv => kv.Key, kv => new IndexLevel { MoreHref = "/" + kv.Value }, StringComparer.Ordinal),
            new IndexLabels(loadMoreLabel ?? LoadMoreLabel, ShowAllLabel, SortToken));

    /// <summary>
    /// The index of the loaded nodes, each loaded level rendered as <paramref name="levels"/> says:
    /// in its sort, offering its sort dropdown, and ending with "Load more" and "Show all" when it
    /// holds more than it shows. A level not named renders in the default order with no extras. Pure.
    /// </summary>
    /// <param name="root">The index root (<see cref="IndexRoot"/>).</param>
    /// <param name="subtree">The loaded nodes — the root itself included when it exists.</param>
    /// <param name="currentPath">The page being read.</param>
    /// <param name="levels">Per loaded level (by namespace): how it renders.</param>
    /// <param name="labels">The viewer's labels.</param>
    public static NodeNavigation? Build(
        string root, IReadOnlyCollection<MeshNode> subtree, string currentPath,
        IReadOnlyDictionary<string, IndexLevel> levels, IndexLabels labels)
    {
        ArgumentNullException.ThrowIfNull(subtree);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(labels);
        var entries = ChildrenOf(root, subtree, currentPath, levels, labels);
        if (entries.Count == 0)
            return null;

        var rootNode = subtree.FirstOrDefault(n => string.Equals(n.Path, root, StringComparison.Ordinal));
        return new NodeNavigation(rootNode?.Name ?? rootNode?.Id ?? LastSegment(root), entries)
        {
            TitlePath = root,
            // Resolved, not raw: a node without an icon of its own still reads as its type.
            Icon = MeshNodeImageHelper.ResolveNodeIcon(rootNode),
            SortOptions = levels.TryGetValue(root, out var top) ? top.SortOptions : [],
        };
    }

    private static IReadOnlyList<NodeNavigationEntry> ChildrenOf(
        string parent, IReadOnlyCollection<MeshNode> subtree, string currentPath,
        IReadOnlyDictionary<string, IndexLevel> levels, IndexLabels labels)
    {
        var level = levels.TryGetValue(parent, out var named) ? named : null;
        var children = VisibleInOrder(
                subtree.Where(n => IsChildOf(n.Path, parent)), level?.Sort ?? IndexSort.Order, level?.ViewedRank)
            .Select(n => new NodeNavigationEntry(
                n.Name ?? n.Id,
                n.Path,
                string.Equals(n.Path, currentPath, StringComparison.Ordinal),
                MeshNodeImageHelper.ResolveNodeIcon(n))
            {
                Children = ChildrenOf(n.Path, subtree, currentPath, levels, labels),
                // The dropdown sorts the entry's children, so it is offered only where they are
                // loaded — a folder off the reader's path is a plain link.
                SortOptions = levels.TryGetValue(n.Path, out var own) ? own.SortOptions : [],
            })
            .ToImmutableList();
        if (children.Count == 0 || level is null)
            return children;
        // A level that holds more than it shows ends with the entries that load the next batch
        // and that open the whole level in the search.
        if (level.MoreHref is { } more)
            children = children.Add(new NodeNavigationEntry(labels.LoadMore, more.TrimStart('/')) { Href = more });
        if (level.ShowAllHref is { } all)
            children = children.Add(new NodeNavigationEntry(labels.ShowAll, all.TrimStart('/')) { Href = all });
        return children;
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

/// <summary>How one level of the default index is sorted — the choices of its dropdown.</summary>
public enum IndexSort
{
    /// <summary>Declared <see cref="MeshNode.Order"/> first, then the most recently modified (the default).</summary>
    Order,
    /// <summary>The most recently modified first.</summary>
    Modified,
    /// <summary>The viewer's most recently opened first, then the most recently modified.</summary>
    Viewed,
    /// <summary>By name, A–Z.</summary>
    Name,
}

/// <summary>How one loaded level of the default index renders.</summary>
public sealed record IndexLevel
{
    /// <summary>The level's sort.</summary>
    public IndexSort Sort { get; init; } = IndexSort.Order;

    /// <summary>For <see cref="IndexSort.Viewed"/>: each opened row's access position (0 = most recent).</summary>
    public IReadOnlyDictionary<string, int>? ViewedRank { get; init; }

    /// <summary>The URL of the level's next batch, or null when there is none to load.</summary>
    public string? MoreHref { get; init; }

    /// <summary>The URL that lists the whole level in the mesh search, or null when it shows everything.</summary>
    public string? ShowAllHref { get; init; }

    /// <summary>The level's sort dropdown.</summary>
    public IReadOnlyList<NavSortOption> SortOptions { get; init; } = [];
}

/// <summary>The viewer's labels for the index's own lines.</summary>
/// <param name="LoadMore">The load-more entry.</param>
/// <param name="ShowAll">The show-all entry.</param>
/// <param name="SortLabel">Each sort mode's name in the dropdown.</param>
public sealed record IndexLabels(string LoadMore, string ShowAll, Func<IndexSort, string> SortLabel);
