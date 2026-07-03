namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The ONE source of truth for the mesh queries that find access <b>subjects</b> (users and
/// groups) — used by the <c>[MeshNode]</c> attributes on <see cref="AccessAssignment.AccessObject"/> /
/// <see cref="GroupMembership.Member"/> and by every Access Control picker in the GUI.
///
/// <para>Users live at the ROOT namespace (path = userId, namespace = "") — the post-v10
/// placement. A path-less <c>nodeType:User</c> query is pinned to the central <c>auth</c>
/// lookup mirror by <c>UserNodeType</c>'s query-routing rule, so one query covers every user
/// in the mesh. The legacy <c>namespace:User</c> shape targets the pre-V27 <c>user</c> schema,
/// which no longer exists — it silently returns ZERO rows (the 2026-07 "cannot select a user
/// in the Subject dropdown" bug, issue #213). Never hand-roll these queries again; reference
/// the members of this class.</para>
/// </summary>
public static class AccessSubjectQueries
{
    /// <summary>
    /// All users in the mesh: root-namespace <c>User</c> nodes, served by the <c>auth</c>
    /// lookup mirror via <c>UserNodeType</c>'s routing rule. Usable in attributes (const).
    /// </summary>
    public const string Users = "nodeType:User namespace:\"\"";

    /// <summary>
    /// Template form of the groups query for <c>[MeshNode]</c> attributes —
    /// <c>{node.namespace}</c> is substituted by the generic property editor at render time.
    /// Code that knows the scope path calls <see cref="Groups"/> instead.
    /// </summary>
    public const string GroupsTemplate = "nodeType:Group namespace:{node.namespace} scope:subtree";

    /// <summary>
    /// Groups eligible as subjects when granting at <paramref name="scopePath"/>: every
    /// <c>Group</c> node in the scope's PARTITION subtree (first path segment — groups defined
    /// anywhere in the space/user partition are grantable on any node inside it). At the root
    /// scope the query is unscoped and fans out across partitions, access-filtered.
    /// </summary>
    public static string Groups(string? scopePath)
    {
        var partition = Partition(scopePath);
        return string.IsNullOrEmpty(partition)
            ? "nodeType:Group"
            : $"nodeType:Group namespace:{partition} scope:subtree";
    }

    /// <summary>
    /// The subject-picker queries (users + groups) for granting access at
    /// <paramref name="scopePath"/> — what every Access Control picker binds to.
    /// </summary>
    public static string[] ForScope(string? scopePath) => [Users, Groups(scopePath)];

    /// <summary>
    /// The scope a satellite node governs: the path prefix before its satellite segment
    /// (e.g. <c>rsalzmann/Games/Lolo/_Access/alice_Access</c> → <c>rsalzmann/Games/Lolo</c>;
    /// a root-level <c>_Access/x</c> → <c>""</c>). A path without an <c>_Access</c> segment
    /// is returned unchanged (it IS the scope).
    /// </summary>
    public static string ScopeOfAssignment(string? assignmentPath)
    {
        if (string.IsNullOrEmpty(assignmentPath)) return "";
        var idx = assignmentPath.IndexOf("_Access", StringComparison.Ordinal);
        while (idx >= 0)
        {
            var atStart = idx == 0 || assignmentPath[idx - 1] == '/';
            var end = idx + "_Access".Length;
            var atEnd = end == assignmentPath.Length || assignmentPath[end] == '/';
            if (atStart && atEnd)
                return idx == 0 ? "" : assignmentPath[..(idx - 1)];
            idx = assignmentPath.IndexOf("_Access", idx + 1, StringComparison.Ordinal);
        }
        return assignmentPath;
    }

    /// <summary>The partition (first path segment) of <paramref name="path"/>, or <c>""</c> for root.</summary>
    public static string Partition(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var trimmed = path.Trim('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }
}
