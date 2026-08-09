using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Mesh;

/// <summary>
/// The <c>Templates</c> partition — the mesh-wide home of the built-in
/// "operations as scripts" Code templates that library code seeds via
/// <c>AddMeshNodes</c>: <c>Templates/Export/{Pdf,Docx}</c> (MeshWeaver.Markdown.Export)
/// and <c>Templates/Import/{NodeCopy,Mirror}</c> (MeshWeaver.Graph).
///
/// <para>Every one of those templates runs by posting an <see cref="ExecuteScriptRequest"/>
/// at the template node, and that request is gated by
/// <c>[RequiresPermission(Permission.Execute)]</c> on the template's own path — so a caller
/// with no grant on <c>Templates</c> is refused with
/// <c>"Access denied: user 'x' lacks Execute permission on 'Templates/Export/Pdf'"</c>.
/// The templates shipped without any accompanying grant, so only users who happened to
/// already hold Execute there could export a deck or copy a node tree (issue #423).
/// <b>The gate is correct — the missing grant was the bug.</b></para>
///
/// <para><see cref="PublicExecuteGrant"/> is that grant, and it is deliberately the
/// SMALLEST one that makes an ordinary user's export work:</para>
/// <list type="bullet">
///   <item><b><see cref="WellKnownUsers.Public"/>, not <see cref="WellKnownUsers.Anonymous"/></b> —
///     signed-in users only. A run writes its <c>Activity</c> into the caller's home
///     (<c>ActivityParentPath = "{viewer}"</c>), which an unauthenticated visitor does not have,
///     so granting Anonymous would buy nothing and widen the surface for free.</item>
///   <item><b><see cref="Role.Viewer"/></b> (Read + Execute + Api) — Execute is the permission the
///     gate checks, and Read is needed to resolve the template node at all. Viewer is the
///     narrowest built-in role carrying Execute; it grants <b>no</b> Create / Update / Delete /
///     Export, so an ordinary user still cannot add, edit or remove a template.</item>
///   <item><b>Scoped to <see cref="Partition"/></b> via <c>MainNode</c> — NOT a root grant, and
///     NOT public read on anything else. It confers nothing outside <c>Templates</c>, which holds
///     only library-seeded static Code nodes. Running a template still executes under the
///     CALLER's identity, so a script can only read what the caller could already read and only
///     write where the caller could already write.</item>
/// </list>
///
/// <para>See <c>Doc/Architecture/AccessControl.md</c> → "The scope invariant".</para>
/// </summary>
public static class ScriptTemplates
{
    /// <summary>
    /// Partition (and access scope) the built-in script templates live under: <c>Templates</c>.
    /// </summary>
    public const string Partition = "Templates";

    /// <summary>Namespace holding the partition's <c>AccessAssignment</c> satellites.</summary>
    public const string AccessNamespace = $"{Partition}/_Access";

    /// <summary>Path of the grant returned by <see cref="PublicExecuteGrant"/>.</summary>
    public static readonly string PublicExecuteGrantPath =
        $"{AccessNamespace}/{WellKnownUsers.Public}_Access";

    /// <summary>
    /// The <c>AccessAssignment</c> that lets every authenticated user RUN the built-in script
    /// templates: <see cref="WellKnownUsers.Public"/> → <see cref="Role.Viewer"/>, scoped to
    /// <see cref="Partition"/>.
    ///
    /// <para>Seeded via <c>AddMeshNodes</c> alongside the templates themselves (from both
    /// <c>AddGraph()</c> and <c>AddMarkdownExport()</c>), which is what makes it land on a fresh
    /// mesh AND on every existing deployment with no migration: the template nodes it guards are
    /// themselves in-memory statics served by <c>StaticNodeQueryProvider</c> and are never
    /// persisted to Postgres, so the grant has to live in exactly the same place they do.</para>
    /// </summary>
    /// <returns>The Public/Viewer grant node for the <c>Templates</c> partition.</returns>
    public static MeshNode PublicExecuteGrant() =>
        new($"{WellKnownUsers.Public}_Access", AccessNamespace)
        {
            NodeType = SecurityCollections.AccessAssignmentNodeType,
            Name = $"{WellKnownUsers.Public} Access",
            Description =
                "Lets every authenticated user run the built-in script templates "
                + "(export, node copy, mirror). Read + Execute only — no write.",
            State = MeshNodeState.Active,
            // 🔒 The scope invariant: MainNode MUST name the scope the path encodes. An empty
            // MainNode here would be a ROOT grant merely filed under Templates/ — i.e. a data
            // superuser grant for every authenticated user. See AccessControl.md.
            MainNode = Partition,
            Content = new AccessAssignment
            {
                AccessObject = WellKnownUsers.Public,
                DisplayName = "All authenticated users",
                Roles = [new RoleAssignment { Role = Role.Viewer.Id }]
            }
        };
}
