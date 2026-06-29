using System.Collections.Immutable;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// One entry in a namespace catalog tree — either a <see cref="NamespaceTreeLeaf"/>
/// (a node rendered as a thumbnail card) or a <see cref="NamespaceTreeFolder"/>
/// (a sub-namespace rendered as a collapsible section).
/// </summary>
public abstract record NamespaceTreeItem;

/// <summary>
/// A node that lives directly at the current namespace level and has no children
/// of its own — rendered as a thumbnail card.
/// </summary>
public sealed record NamespaceTreeLeaf(MeshNode Node) : NamespaceTreeItem;

/// <summary>
/// A sub-namespace rendered as a collapsible section.
/// </summary>
/// <param name="Name">Display name (the node's name when a node exists at the folder path, otherwise the path segment).</param>
/// <param name="Path">Absolute mesh path of the sub-namespace.</param>
/// <param name="Count">
/// Number of nodes the folder contains. For a tree built from a flat (subtree)
/// result set this is the number of result nodes strictly inside the folder;
/// for a lazily-loaded level it is the probed direct-children count.
/// </param>
/// <param name="Node">The node at the folder path itself, when one exists in the result set.</param>
/// <param name="Children">
/// Nested items when the tree was built from a flat result set
/// (<see cref="NamespaceTreeBuilder.Build"/>); empty for lazily-loaded levels
/// (<see cref="NamespaceTreeBuilder.BuildLevel"/>), whose content is queried on expand.
/// </param>
public sealed record NamespaceTreeFolder(
    string Name,
    string Path,
    int Count,
    MeshNode? Node,
    ImmutableList<NamespaceTreeItem> Children) : NamespaceTreeItem;

/// <summary>
/// Pure, testable builder for the namespace catalog tree used by
/// <c>MeshSearchRenderMode.NamespaceTree</c>. Mirrors the shape of
/// <c>NodeTypeLayoutAreas.BuildCodeTree</c>: flat node paths in, relativised
/// hierarchy out.
/// </summary>
public static class NamespaceTreeBuilder
{
    /// <summary>
    /// Builds a nested tree from a FLAT result set (e.g. a <c>scope:descendants</c>
    /// search), relativised to <paramref name="rootNamespace"/>.
    /// Nodes one segment below the root become leaves; deeper path segments create
    /// folders. A node whose own path is also an ancestor namespace of other results
    /// is absorbed into that folder (<see cref="NamespaceTreeFolder.Node"/>) instead
    /// of appearing twice. The node at the root path itself and nodes outside the
    /// root are ignored.
    /// </summary>
    public static ImmutableList<NamespaceTreeItem> Build(
        string rootNamespace,
        IReadOnlyCollection<MeshNode> nodes)
    {
        var root = Normalize(rootNamespace);
        var prefix = root.Length == 0 ? "" : root + "/";

        var scratch = new Scratch(root);
        foreach (var node in nodes)
        {
            var path = Normalize(node.Path);
            if (path.Length == 0)
                continue;
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
                continue; // the root node itself is not part of its own catalog
            string relative;
            if (prefix.Length == 0)
                relative = path;
            else if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                relative = path[prefix.Length..];
            else
                continue; // outside the catalog root
            scratch.Insert(relative.Split('/'), 0, node);
        }

        return scratch.ToItems();
    }

    /// <summary>
    /// Builds a SINGLE lazily-loaded level from the direct children of a namespace
    /// plus a per-child children-count probe result. Children with a positive count
    /// become (initially empty) folders whose content is queried on expand; the rest
    /// are leaves. Folders sort first (by name), then leaves (by Order, then Name) —
    /// the same ordering <see cref="Build"/> produces.
    /// </summary>
    public static ImmutableList<NamespaceTreeItem> BuildLevel(
        string namespacePath,
        IReadOnlyCollection<MeshNode> directChildren,
        IReadOnlyDictionary<string, int> childCounts)
    {
        var folders = new List<NamespaceTreeFolder>();
        var leaves = new List<NamespaceTreeLeaf>();

        foreach (var node in directChildren)
        {
            var path = Normalize(node.Path);
            if (path.Length == 0)
                continue;
            var count = childCounts.TryGetValue(path, out var c) ? c : 0;
            if (count > 0)
                folders.Add(new NamespaceTreeFolder(
                    DisplayName(node, path), path, count, node, ImmutableList<NamespaceTreeItem>.Empty));
            else
                leaves.Add(new NamespaceTreeLeaf(node));
        }

        return Order(folders, leaves);
    }

    private static ImmutableList<NamespaceTreeItem> Order(
        IEnumerable<NamespaceTreeFolder> folders,
        IEnumerable<NamespaceTreeLeaf> leaves)
        => folders
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Cast<NamespaceTreeItem>()
            .Concat(leaves
                .OrderBy(l => l.Node.Order ?? 0)
                .ThenBy(l => l.Node.Name ?? l.Node.Id, StringComparer.OrdinalIgnoreCase))
            .ToImmutableList();

    private static string DisplayName(MeshNode? node, string path)
    {
        if (!string.IsNullOrEmpty(node?.Name))
            return node!.Name!;
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path[(lastSlash + 1)..];
    }

    private static string Normalize(string? path)
        => string.IsNullOrEmpty(path) ? "" : path.Trim('/');

    /// <summary>
    /// Local scratch tree during <see cref="Build"/> — same pattern as
    /// <c>NodeTypeLayoutAreas.CodeTreeFolder</c>: builder-internal mutable state,
    /// immutable records on the public surface.
    /// </summary>
    private sealed class Scratch(string path)
    {
        private readonly Dictionary<string, Scratch> folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MeshNode> leaves = new();
        private MeshNode? node;

        public void Insert(string[] segments, int index, MeshNode item)
        {
            if (index == segments.Length - 1)
            {
                leaves.Add(item);
                return;
            }
            var segment = segments[index];
            if (!folders.TryGetValue(segment, out var folder))
                folders[segment] = folder = new Scratch(path.Length == 0 ? segment : $"{path}/{segment}");
            folder.Insert(segments, index + 1, item);
        }

        public ImmutableList<NamespaceTreeItem> ToItems()
        {
            // A leaf whose path doubles as a folder path is absorbed into the folder
            // so it isn't rendered twice (once as a card, once as a section).
            var remainingLeaves = new List<MeshNode>();
            foreach (var leaf in leaves)
            {
                var leafPath = leaf.Path?.Trim('/') ?? "";
                var lastSlash = leafPath.LastIndexOf('/');
                var segment = lastSlash < 0 ? leafPath : leafPath[(lastSlash + 1)..];
                if (folders.TryGetValue(segment, out var folder))
                    folder.node = leaf;
                else
                    remainingLeaves.Add(leaf);
            }

            var folderItems = folders.Values.Select(f => f.ToFolder());
            var leafItems = remainingLeaves.Select(l => new NamespaceTreeLeaf(l));
            return Order(folderItems, leafItems);
        }

        private NamespaceTreeFolder ToFolder()
            => new(DisplayName(node, path), path, ContainedCount(), node, ToItems());

        /// <summary>
        /// Result nodes strictly inside this folder. Absorbing a leaf into a
        /// subfolder (as its <see cref="NamespaceTreeFolder.Node"/>) does not remove
        /// it from this scratch's leaf list, so every node is counted exactly once:
        /// a subfolder's own node counts toward its PARENT (it sits strictly inside
        /// the parent), never toward itself.
        /// </summary>
        private int ContainedCount()
            => leaves.Count + folders.Values.Sum(f => f.ContainedCount());
    }
}
