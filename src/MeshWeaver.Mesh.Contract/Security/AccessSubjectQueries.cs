namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The ONE source of truth for the mesh queries that find access <b>subjects</b> (users and
/// groups) — used by the <c>[MeshNode]</c> attributes on <see cref="AccessAssignment.AccessObject"/> /
/// <see cref="GroupMembership.Member"/> / <see cref="GrantRule.AccessObject"/> and by every Access
/// Control picker in the GUI.
///
/// <para><b>Users are listed only to someone who may grant at the scope</b> (policy
/// <c>user-listing-needs-grant-right</c>, Doc/Architecture/PolicyNotProse). A picker never runs the
/// raw directory read (<see cref="Users"/>) as its viewer. It binds <see cref="UsersAt"/> /
/// <see cref="UsersTemplate"/> instead: a query on the reserved virtual namespace
/// <see cref="GrantSubjectsNamespace"/>, answered by ONE provider (<c>GrantSubjectsQueryProvider</c>,
/// MeshWeaver.Graph) that checks the viewer's manage-access right at the scope and only then reads
/// the directory as System. Nothing is stored under that namespace, so every storage backend answers
/// it with nothing and the provider's answer is the whole answer — identical on every backend.</para>
///
/// <para>Users live at the ROOT namespace (path = userId, namespace = ""). The legacy
/// <c>namespace:User</c> shape targets the pre-V27 <c>user</c> schema, which no longer exists — it
/// silently returns ZERO rows (#213). Never hand-roll these queries; reference the members of this
/// class.</para>
/// </summary>
public static class AccessSubjectQueries
{
    /// <summary>
    /// The raw user-directory read: root-namespace <c>User</c> nodes, served by the <c>auth</c>
    /// lookup mirror via <c>UserNodeType</c>'s routing rule. Usable in attributes (const).
    ///
    /// <para>🚨 NOT a picker query. Run as a viewer, its answer depends on the backend: PostgreSQL
    /// answers zero rows to a signed-in caller holding no read grant on the <c>auth</c> mirror schema
    /// (core's <c>Auth</c> partition declares none, so that is every caller unless a deployment
    /// added one), while
    /// the in-memory provider applies the User type's C# read rule and lists every user. Pickers bind
    /// <see cref="UsersAt"/>, which decides who may list users the same way everywhere; this string is
    /// what the provider behind it reads as System.</para>
    /// </summary>
    public const string Users = "nodeType:User namespace:\"\"";

    /// <summary>
    /// The reserved virtual namespace whose children are the users a viewer may pick as subjects at
    /// a scope: <c>Auth/_GrantSubjects/{scope}</c>. It is anchored in the <c>Auth</c> partition so
    /// every planner pins it to ONE schema (where nothing is ever stored) instead of fanning out.
    /// </summary>
    public const string GrantSubjectsNamespace = "Auth/_GrantSubjects";

    /// <summary>
    /// Template form of the users query for <c>[MeshNode]</c> attributes — <c>{node.namespace}</c>
    /// is substituted by the generic property editor, and the provider derives the scope from it
    /// (<see cref="ScopeOfAssignment"/>: <c>acme/_Access</c> → <c>acme</c>).
    /// </summary>
    public const string UsersTemplate = "namespace:" + GrantSubjectsNamespace + "/{node.namespace} nodeType:User";

    /// <summary>
    /// The users a viewer may pick as subjects when granting at <paramref name="scopePath"/>: every
    /// user in the mesh for a viewer holding the manage-access right there, nobody for anyone else.
    /// </summary>
    /// <param name="scopePath">The scope the grant is made at; null or empty is the root.</param>
    /// <returns>The query on <see cref="GrantSubjectsNamespace"/>.</returns>
    public static string UsersAt(string? scopePath)
    {
        var scope = (scopePath ?? "").Trim('/');
        return scope.Length == 0
            ? $"namespace:{GrantSubjectsNamespace} nodeType:User"
            : $"namespace:{GrantSubjectsNamespace}/{scope} nodeType:User";
    }

    /// <summary>
    /// The scope a grant-subjects namespace names: <c>Auth/_GrantSubjects/acme/Docs</c> →
    /// <c>acme/Docs</c>, <c>Auth/_GrantSubjects/acme/_Access</c> → <c>acme</c> (the attribute
    /// template's form), <c>Auth/_GrantSubjects</c> → <c>""</c> (the root). Null when
    /// <paramref name="ns"/> is not a grant-subjects namespace, or names an unresolved template. Pure.
    /// </summary>
    /// <param name="ns">A query namespace.</param>
    /// <returns>The scope, or null.</returns>
    public static string? GrantScopeOf(string? ns)
    {
        if (string.IsNullOrEmpty(ns))
            return null;
        var trimmed = ns.Trim('/');
        if (string.Equals(trimmed, GrantSubjectsNamespace, StringComparison.OrdinalIgnoreCase))
            return "";
        var prefix = GrantSubjectsNamespace + "/";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = trimmed[prefix.Length..].Trim('/');
        if (rest.Contains('{') || rest.Contains('*'))
            return null;
        return ScopeOfAssignment(rest).Trim('/');
    }

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
            // The root scope's subject picker offers every group in the mesh — mesh-wide by
            // nature, and it says so (#3202 — fan-out is opt-in).
            ? MeshWideQuery.OfType("Group")
            : $"nodeType:Group namespace:{partition} scope:subtree";
    }

    /// <summary>
    /// The subject-picker queries (users + groups) for granting access at
    /// <paramref name="scopePath"/> — what every Access Control picker binds to. The users half is
    /// <see cref="UsersAt"/>: listed only to a viewer who may grant at the scope.
    /// </summary>
    public static string[] ForScope(string? scopePath) => [UsersAt(scopePath), Groups(scopePath)];

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
