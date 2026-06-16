using System.Collections.Immutable;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// The immutable view-model behind <c>MeshSearchRenderMode.GraphNavigator</c>: for a current node,
/// the ancestors <b>above</b> it (a clickable breadcrumb rail), the current node itself, and the
/// next populated level <b>below</b> it (the nearest real nodes — see
/// <see cref="MeshWeaver.Mesh.NamespaceFrontier"/> / <see cref="QueryScope.NextLevel"/>). Built by
/// <see cref="GraphNavigatorBuilder"/> so the Blazor view stays thin and the ordering is unit-tested.
/// </summary>
/// <param name="RootPath">Absolute path of the node the navigator is rooted at (may be empty = mesh root).</param>
/// <param name="Ancestors">Real ancestor nodes ordered shallow → deep (root-most first); excludes the current node.</param>
/// <param name="Current">The node at <paramref name="RootPath"/> when one was supplied.</param>
/// <param name="Below">The next populated level, ordered by Order then Name.</param>
public sealed record GraphNavigatorModel(
    string RootPath,
    ImmutableList<MeshNode> Ancestors,
    MeshNode? Current,
    ImmutableList<MeshNode> Below);

/// <summary>
/// Pure, testable builder for <see cref="GraphNavigatorModel"/>. Mirrors the shape of
/// <see cref="NamespaceTreeBuilder"/>: raw query result nodes in, ordered navigation model out.
/// </summary>
public static class GraphNavigatorBuilder
{
    /// <summary>
    /// Assembles the navigation model. <paramref name="ancestorNodes"/> is the raw result of a
    /// <c>path:{root} scope:ancestors</c> query (any order); <paramref name="belowNodes"/> the raw
    /// result of a <c>namespace:{root} scope:nextLevel</c> query. The current node, when known, is
    /// dropped from both lists so it never doubles as its own ancestor or child.
    /// </summary>
    public static GraphNavigatorModel Build(
        string? rootPath,
        IReadOnlyCollection<MeshNode> ancestorNodes,
        IReadOnlyCollection<MeshNode> belowNodes,
        MeshNode? current = null)
    {
        var root = Normalize(rootPath);

        var ancestors = ancestorNodes
            .Where(n => !string.IsNullOrEmpty(n.Path))
            .Where(n => !PathEquals(n.Path, root))
            .DistinctBy(n => Normalize(n.Path), StringComparer.OrdinalIgnoreCase)
            // Shallow → deep so the rail reads root-most first; path tiebreak keeps it deterministic.
            .OrderBy(n => SegmentCount(n.Path))
            .ThenBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();

        var below = belowNodes
            .Where(n => !string.IsNullOrEmpty(n.Path))
            .Where(n => !PathEquals(n.Path, root))
            .DistinctBy(n => Normalize(n.Path), StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n.Order ?? 0)
            .ThenBy(n => n.Name ?? n.Id, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();

        return new GraphNavigatorModel(root, ancestors, current, below);
    }

    private static int SegmentCount(string? path)
        => string.IsNullOrEmpty(path) ? 0 : path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool PathEquals(string? a, string? b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? path) => path?.Trim('/') ?? "";
}
