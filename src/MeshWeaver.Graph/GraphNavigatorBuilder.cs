using System.Collections.Immutable;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// A real node at the current navigator level. <see cref="HasChildren"/> is true when the node also
/// has content inside its own namespace — the view shows a "drill in" affordance for those.
/// </summary>
public sealed record GraphNavNode(MeshNode Node, bool HasChildren, int ChildCount);

/// <summary>
/// A pure sub-namespace (a path segment that groups content but has NO node of its own). The view
/// renders these as drill-down navigation links — visually distinct from node cards — at the bottom.
/// </summary>
public sealed record GraphNavNamespace(string Name, string Path, int Count);

/// <summary>
/// The immutable view-model behind <c>MeshSearchRenderMode.GraphNavigator</c> — the Search layout
/// area on a mesh node. It splits the level into the <see cref="Nodes"/> at this level (real nodes,
/// shown as cards on top — with a drill affordance when they contain further content) and the
/// <see cref="Namespaces"/> below it (pure groupings with no node, shown as drill links at the
/// bottom), plus the <see cref="Ancestors"/> above for the breadcrumb rail.
/// </summary>
public sealed record GraphNavigatorModel(
    string RootPath,
    ImmutableList<MeshNode> Ancestors,
    MeshNode? Current,
    ImmutableList<GraphNavNode> Nodes,
    ImmutableList<GraphNavNamespace> Namespaces);

/// <summary>
/// Pure, testable builder for <see cref="GraphNavigatorModel"/>. Reuses <see cref="NamespaceTreeBuilder"/>
/// (one <c>scope:descendants</c> query in, the immediate level out) to distinguish, at the current
/// level: leaves (a node, no children), node-folders (a node that ALSO has children → drill), and
/// pure-namespace folders (children but no node → a navigation link). No N+1 child-count probes.
/// </summary>
public static class GraphNavigatorBuilder
{
    /// <summary>
    /// Assembles the navigation model. <paramref name="ancestorNodes"/> is the raw result of a
    /// <c>path:{root} scope:ancestors</c> query; <paramref name="descendants"/> the raw result of a
    /// <c>namespace:{root} scope:descendants</c> query (the whole subtree — only the immediate level
    /// is surfaced, the rest just classifies nodes vs namespaces). The current node, when known, is
    /// dropped from the ancestor rail.
    /// </summary>
    public static GraphNavigatorModel Build(
        string? rootPath,
        IReadOnlyCollection<MeshNode> ancestorNodes,
        IReadOnlyCollection<MeshNode> descendants,
        MeshNode? current = null)
    {
        var root = Normalize(rootPath);

        var ancestors = ancestorNodes
            .Where(n => !string.IsNullOrEmpty(n.Path))
            .Where(n => !PathEquals(n.Path, root))
            .DistinctBy(n => Normalize(n.Path), StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => SegmentCount(n.Path))
            .ThenBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();

        var nodes = new List<GraphNavNode>();
        var namespaces = new List<GraphNavNamespace>();

        foreach (var item in NamespaceTreeBuilder.Build(root, descendants))
        {
            switch (item)
            {
                case NamespaceTreeLeaf leaf:
                    // A node at this level with no content under it.
                    nodes.Add(new GraphNavNode(leaf.Node, HasChildren: false, ChildCount: 0));
                    break;
                case NamespaceTreeFolder { Node: { } node } folder:
                    // A node at this level that ALSO has content in its namespace → drill affordance.
                    nodes.Add(new GraphNavNode(node, HasChildren: true, ChildCount: folder.Count));
                    break;
                case NamespaceTreeFolder folder:
                    // A pure namespace (no node at this path) → a drill-down navigation link.
                    namespaces.Add(new GraphNavNamespace(folder.Name, folder.Path, folder.Count));
                    break;
            }
        }

        return new GraphNavigatorModel(
            root,
            ancestors,
            current,
            nodes
                .OrderBy(n => n.Node.Order ?? 0)
                .ThenBy(n => n.Node.Name ?? n.Node.Id, StringComparer.OrdinalIgnoreCase)
                .ToImmutableList(),
            namespaces
                .OrderBy(ns => ns.Name, StringComparer.OrdinalIgnoreCase)
                .ToImmutableList());
    }

    private static int SegmentCount(string? path)
        => string.IsNullOrEmpty(path) ? 0 : path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool PathEquals(string? a, string? b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? path) => path?.Trim('/') ?? "";
}
