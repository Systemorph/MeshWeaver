using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// 🚨 THE ONE RULE for which <see cref="Permission"/> deleting a node demands — and therefore what
/// a MOVE demands of its source, because a move IS a create at the target plus a delete at the
/// source and must ask for nothing more and nothing less.
///
/// <para>Deleting ordinary CONTENT is an edit of the space it lives in: it demands
/// <see cref="Permission.Update"/>, which an <c>Editor</c> holds. Deleting what GOVERNS a space
/// keeps demanding <see cref="Permission.Delete"/>, which only an <c>Admin</c> holds:</para>
/// <list type="bullet">
/// <item><description>a partition root — a Space, a Partition, a user home: removing it tears the
/// whole partition down;</description></item>
/// <item><description>anything under a governance satellite — <c>_Access</c> (the grants),
/// <c>_Policy</c> (the caps), <c>_GitSync</c> (the sync wiring), <c>_Entitlements</c> (purchases):
/// removing one changes who may do what, which is an administrator's call.</description></item>
/// </list>
/// <para>A node type whose access rule decides Delete for itself (Space, Partition, the sync
/// config) keeps its own demand — this rule is the DEFAULT the rule-less paths apply, never an
/// override of a declared rule. <see cref="Role.Editor"/> and the permission numbers are unchanged:
/// the Delete bit still distinguishes an Admin, so <c>HasFlag(Permission.All)</c> keeps meaning
/// what it meant.</para>
/// </summary>
public static class DeletePermission
{
    /// <summary>Path segments under which deleting anything stays an administrator's call.</summary>
    public static readonly ImmutableHashSet<string> GovernanceSegments =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "_Access", "_Policy", "_GitSync", "_Entitlements");

    /// <summary>
    /// The permission deleting the node at <paramref name="path"/> demands:
    /// <see cref="Permission.Delete"/> for a partition root or anything under a
    /// <see cref="GovernanceSegments">governance satellite</see>, <see cref="Permission.Update"/>
    /// for every other node. An empty path is a root, so it demands Delete.
    /// </summary>
    public static Permission RequiredFor(string? path)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return Permission.Delete;
        foreach (var segment in segments)
            if (GovernanceSegments.Contains(segment))
                return Permission.Delete;
        return Permission.Update;
    }

    /// <summary>
    /// The partition a path lives in — its first segment. Two paths with the same partition stay
    /// under the same governance.
    /// </summary>
    public static string PartitionOf(string? path)
    {
        var trimmed = (path ?? string.Empty).Trim('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    /// <summary>True when <paramref name="source"/> and <paramref name="target"/> are in the same partition.</summary>
    public static bool SamePartition(string? source, string? target)
        => string.Equals(PartitionOf(source), PartitionOf(target), StringComparison.OrdinalIgnoreCase);
}
