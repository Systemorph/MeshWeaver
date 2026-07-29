using MeshWeaver.Mesh;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Structural guard for <c>AccessAssignment</c> nodes: the grant's SCOPE must be the one its PATH
/// encodes.
///
/// <para><b>The bug this exists to make impossible.</b> A grant is scoped by its <c>MainNode</c>,
/// NOT by the folder it sits in. So <c>Admin/_Access/{user}_Access</c> with an EMPTY
/// <c>MainNode</c> is not "an admin of the Admin partition" — it is a <b>ROOT</b> grant that merely
/// happens to be filed under <c>Admin/</c>, i.e. a data superuser holding All on every partition,
/// every space and every user's private home, by scope inheritance. It reads as harmless in the
/// node tree and is catastrophic in the evaluator.</para>
///
/// <para>Measured on memex 2026-07-28: <b>43 accounts</b> had an empty <c>node_path_prefix</c> in
/// <c>admin.user_effective_permissions</c> — root scope — carrying Delete/Update/Create/Compile/
/// Execute/Export over the whole mesh, against exactly ONE correctly-scoped platform admin. They
/// accrued one-per-user over two weeks and were still being created that day, so this is not a
/// historical accident: some writer keeps producing the shape.</para>
///
/// <para><b>Why guard the shape rather than chase the writer.</b> Every known writer
/// (<c>UserScopeGrantHandler</c>, the Access-Control dialog) sets <c>MainNode</c> correctly today,
/// yet the rows keep appearing — so an unknown or historical path is producing them. A structural
/// invariant at the create boundary defeats all of them at once, including the one nobody has found
/// yet, and it cannot silently regress.</para>
/// </summary>
public static class AccessAssignmentGuard
{
    /// <summary>The node type this guard applies to.</summary>
    public const string AccessAssignmentNodeType = "AccessAssignment";

    /// <summary>The well-known folder that holds a scope's grants.</summary>
    public const string AccessFolder = "_Access";

    /// <summary>
    /// The scope a grant's PATH encodes: everything before the trailing <c>/_Access/{id}</c>.
    /// <c>Admin/_Access/x_Access</c> → <c>Admin</c>; <c>Store/Plugin/_Access/x</c> →
    /// <c>Store/Plugin</c> (scopes nest); a root-level <c>_Access/x</c> → <c>""</c> (the
    /// data-superuser shape). Returns <c>null</c> when the path is not a grant path at all.
    /// </summary>
    public static string? ScopeFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var marker = "/" + AccessFolder + "/";
        var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return path[..idx];

        // Root-level grant: "_Access/{id}" — scope is the root itself.
        return path.StartsWith(AccessFolder + "/", StringComparison.OrdinalIgnoreCase) ? "" : null;
    }

    /// <summary>
    /// The grant is internally inconsistent — its <c>MainNode</c> does not match the scope its path
    /// encodes. That mismatch is the dangerous shape: a grant filed under one partition while
    /// actually conferring rights somewhere else, most catastrophically at ROOT.
    ///
    /// <para>Non-AccessAssignment nodes, and nodes that are not on a grant path at all, are not this
    /// guard's business and always pass.</para>
    /// </summary>
    public static bool IsScopeInvalid(MeshNode? node, out string reason)
    {
        reason = "";
        if (node is null)
            return false;
        if (!string.Equals(node.NodeType, AccessAssignmentNodeType, StringComparison.OrdinalIgnoreCase))
            return false;

        var pathScope = ScopeFromPath(node.Path);
        if (pathScope is null)
            return false;                       // not a grant path — leave it alone

        var mainNode = node.MainNode ?? "";

        // 🔴 The ROOT path (`_Access/{subject}_Access`, scope "") is the data-superuser shape, and an
        // earlier revision of this guard refused it outright. It does not, because that rule is both
        // WIDER than the incident and load-bearing elsewhere:
        //
        //   • Every memex offender was a MISMATCH, not a consistent root grant. They sat in
        //     `admin.access` — i.e. `Admin/_Access/{user}_Access`, path scope "Admin" — with
        //     MainNode = "". The consistency rule below catches all of them, which is the whole
        //     point: the danger was a grant that LOOKED like a platform-admin grant while silently
        //     being scoped to root.
        //   • The deliberate, self-consistent root grant is how the test harness gives a subject
        //     mesh-wide rights: `AssignmentNodeFactory.UserRole(user, role)` with no scope, used at
        //     ~200 call sites, plus `TestUsers.PublicAdminAccess()`'s root entry. Refusing it here
        //     failed four of six CI shards — not because those tests were wrong about permissions,
        //     but because they could no longer be granted any.
        //
        // So the write boundary enforces CONSISTENCY (MainNode == the scope the path encodes), and
        // the root shape is kept off the interactive surface by `CanGrantAt` — a human clicking
        // through the access UI in a root context is offered no grant at all. What remains possible
        // is a root grant written deliberately, in code, with both halves agreeing; that is the
        // harness's convention, and narrowing it further means rescoping those call sites first.
        if (mainNode.Length == 0 && pathScope.Length == 0)
            return false;

        if (string.Equals(mainNode, pathScope, StringComparison.OrdinalIgnoreCase))
            return false;                       // consistent — the good case

        reason = mainNode.Length == 0
            ? $"AccessAssignment '{node.Path}' has an EMPTY MainNode. A grant is scoped by MainNode, "
              + $"NOT by its folder — so this grants ROOT (every partition), not '{pathScope}'. "
              + $"Set MainNode='{pathScope}'."
            : $"AccessAssignment '{node.Path}' has MainNode='{mainNode}' but its path encodes scope "
              + $"'{pathScope}'. A grant must be scoped to the node it is filed under; a mismatch "
              + "silently grants somewhere else.";
        return true;
    }

    /// <summary>
    /// A grant can be made at <paramref name="scopePath"/> — i.e. it names a partition/node to
    /// scope to. FALSE for the ROOT context (an empty navigation context), where a grant would be
    /// scoped to root and therefore a platform-wide superuser.
    ///
    /// <para>Used by the access-control UI so the add-grant surface is not even offered at root:
    /// the write boundary refuses the shape anyway, but a button that silently mints a superuser
    /// should not exist. The navigation context (<c>host.Hub.Address</c>) is empty at root, and
    /// both creation sites derive both the namespace and <c>MainNode</c> from it.</para>
    /// </summary>
    public static bool CanGrantAt(string? scopePath) => !string.IsNullOrWhiteSpace(scopePath);

    /// <summary>Throws when <see cref="IsScopeInvalid"/> holds. For call sites that cannot return a
    /// structured rejection.</summary>
    public static void EnsureScopeValid(MeshNode node)
    {
        if (IsScopeInvalid(node, out var reason))
            throw new InvalidOperationException(reason);
    }
}
