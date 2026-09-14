using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Core's DEFAULT left-hand index for a markdown page — what a page shows when no module's
/// <see cref="INodeNavigationProvider"/> claims it.
///
/// <para><b>What it indexes.</b> The tree under the page's <see cref="IndexRoot"/>: the node one
/// level below the partition (<c>Doc/Architecture</c> for every page under it,
/// <c>Infrastructure/Inference</c> for its sub-pages, <c>{viewer}/Notes</c> inside a home). Every
/// page of that tree shows the SAME index — the root's children in their declared order, an entry
/// with children as a collapsible group, the groups on the reader's path open, and the page being
/// read marked as current. The index used to be the page's OWN children, which is right on the
/// root of a document tree and wrong one level down: a sub-page has no children, so its index
/// vanished the moment the reader clicked into it, and nothing told them where they were
/// (reported 2026-09-14 on Infrastructure/Inference).</para>
///
/// <para><b>Why the second segment.</b> The first segment is the partition — a Space, a plugin, a
/// viewer's home — whose own overview lists its content already, so an index rooted there would
/// put every document of the Space beside every page of every document. One level down is where
/// a document tree starts. A page directly under the partition is its own root, so a childless
/// one keeps rendering with no rail, exactly as before.</para>
///
/// <para>Pure at its core: <see cref="Build"/> turns one subtree snapshot into the index, so
/// structure, ordering, exclusions and position marking are pinned without a mesh.</para>
/// </summary>
public static class DefaultNodeNavigation
{
    /// <summary>
    /// The root of the index the page at <paramref name="currentPath"/> shows: its first two path
    /// segments, or the page itself when it is not that deep.
    /// </summary>
    /// <param name="currentPath">The page being read.</param>
    public static string IndexRoot(string currentPath)
    {
        var segments = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : currentPath;
    }

    /// <summary>
    /// The live index for the page <paramref name="host"/> renders — null while the tree has no
    /// entries (a page with nothing beside or below it shows no rail). One <c>scope:subtree
    /// is:main</c> query on the <see cref="IndexRoot"/> through the UNIFIED
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
        return Guard(
            Observable.Defer(() => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{root} scope:subtree is:main"))
                .Scan(ImmutableDictionary<string, MeshNode>.Empty, Fold)
                .Select(tree => Build(root, tree.Values.ToList(), currentPath))),
            ex => logger?.LogWarning(ex,
                "The default index query for {Root} faulted — the page renders without its index",
                root));
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
    {
        ArgumentNullException.ThrowIfNull(subtree);
        var entries = ChildrenOf(root, subtree, currentPath);
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
        string parent, IReadOnlyCollection<MeshNode> subtree, string currentPath)
        => MarkdownOverviewLayoutArea
            .OrderSubNodes(subtree.Where(n => IsChildOf(n.Path, parent) && !LastSegment(n.Path).StartsWith('_')))
            .Select(n => new NodeNavigationEntry(
                n.Name ?? n.Id,
                n.Path,
                string.Equals(n.Path, currentPath, StringComparison.Ordinal),
                MeshNodeImageHelper.ResolveNodeIcon(n))
            {
                Children = ChildrenOf(n.Path, subtree, currentPath),
            })
            .ToList();

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
