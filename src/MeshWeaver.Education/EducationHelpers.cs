using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// The query/path helpers shared by the education navigation contributor — extracted from the
/// deleted <c>EducationLayoutAreas</c> (the course-shell areas now ship as in-mesh source in the
/// Edu plugin, Plugins#481; only the compiled navigation contributor remains platform-side).
/// </summary>
public static class EducationHelpers
{
    private const string GitHubSyncConfigNodeType = "GitHubSyncConfig";

    public const string ExerciseSubNamespace = "Exercise";

    public static bool IsDirectChildPage(ReadOnlySpan<char> path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var segment = path[prefix.Length..];
        return segment.Length > 0 && !segment.Contains('/') && segment[0] != '_';
    }

    public static ImmutableDictionary<string, MeshNode> ApplyQueryChange(
        ImmutableDictionary<string, MeshNode> nodes, QueryResultChange<MeshNode> change)
        => change.ChangeType switch
        {
            QueryChangeType.Initial or QueryChangeType.Reset => ImmutableDictionary<string, MeshNode>.Empty
                .SetItems(change.Items.Select(n => new KeyValuePair<string, MeshNode>(n.Path, n))),
            QueryChangeType.Removed => nodes.RemoveRange(change.Items.Select(n => n.Path)),
            _ => nodes.SetItems(change.Items.Select(n => new KeyValuePair<string, MeshNode>(n.Path, n))),
        };

    public static bool IsExerciseSegment(ReadOnlySpan<char> segment)
        => segment.Equals(ExerciseSubNamespace, StringComparison.OrdinalIgnoreCase)
           || segment.Equals(ExerciseSubNamespace + "s", StringComparison.OrdinalIgnoreCase);

    public static ReadOnlySpan<char> LastSegment(string path)
    {
        var last = path.LastIndexOf('/');
        return last < 0 ? path.AsSpan() : path.AsSpan(last + 1);
    }

    public static string? ResolveViewerHome(AccessService? accessService)
    {
        if (accessService is null)
            return null;
        foreach (var candidate in new[] { accessService.Context?.ObjectId, accessService.CircuitContext?.ObjectId })
            if (!string.IsNullOrEmpty(candidate)
                && candidate != WellKnownUsers.System
                && !string.Equals(candidate, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase)
                && !AccessService.LooksLikeHubPrincipal(candidate))
                return candidate;
        return null;
    }

    public static IReadOnlyList<MeshNode> SelectCoursePages(
        string parentPath, IReadOnlyCollection<MeshNode> mainNodes)
    {
        var prefix = parentPath + "/";
        return mainNodes
            .Where(n => !string.IsNullOrEmpty(n.Path)
                        && IsDirectChildPage(n.Path.AsSpan(), prefix)
                        && !GitHubSyncConfigNodeType.Equals(n.NodeType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Order)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? ResolveActivePath(string currentPath, IEnumerable<string> entryPaths)
    {
        string? deepestAncestor = null;
        foreach (var entry in entryPaths)
        {
            if (string.IsNullOrEmpty(entry))
                continue;
            if (string.Equals(entry, currentPath, StringComparison.Ordinal))
                return entry;                                       // an exact entry always wins
            if (currentPath.StartsWith(entry + "/", StringComparison.Ordinal)
                && (deepestAncestor is null || entry.Length > deepestAncestor.Length))
                deepestAncestor = entry;
        }
        return deepestAncestor;
    }

    public static bool IsExercise(MeshNode node)
        => IsExerciseNodeType(node.NodeType) || IsExerciseSegment(ParentSegment(node.Path));


    public static bool IsExerciseNodeType(string? nodeType)
        => nodeType is not null
           && (nodeType.Equals(ExerciseSubNamespace, StringComparison.OrdinalIgnoreCase)
               || nodeType.EndsWith("/" + ExerciseSubNamespace, StringComparison.OrdinalIgnoreCase));

    public static ReadOnlySpan<char> ParentSegment(string path)
    {
        var last = path.LastIndexOf('/');
        if (last <= 0)
            return [];
        var parent = path.AsSpan(0, last);
        var beforeParent = parent.LastIndexOf('/');
        return beforeParent < 0 ? parent : parent[(beforeParent + 1)..];
    }

}
