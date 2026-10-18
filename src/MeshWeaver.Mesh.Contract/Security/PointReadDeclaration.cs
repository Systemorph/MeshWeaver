namespace MeshWeaver.Mesh.Security;

/// <summary>
/// A node type's DECLARED point read: who may read ONE node of this type at its OWN path, named by
/// the node itself, and nothing else (MeshWeaver#4306). It is the admission a public link needs:
/// the link's audience may open the link it holds the id of, and no listing, query or scope walk
/// ever names the link to them.
///
/// <para><b>What it admits.</b> Exactly a point read: a read addressed to the node's own path
/// (a <c>get</c>, a node stream, a subscription to the node's own hub). A listing, a query, a
/// <c>scope:children</c>/<c>descendants</c> walk, another path, a child, a satellite, and every
/// write are untouched — they never consult the declaration. It is NOT a grant row and is not
/// folded into the permission evaluator, so nothing that asks the fold (a menu, a query provider,
/// the Postgres permission table) sees it. See <see cref="DeclaredPointRead"/> for the seams.</para>
///
/// <para><b>Who may declare it.</b> Only a type the PLATFORM owns: a type node the host declares
/// (a configuration node) or one stored in a system-owned partition (a one-way <c>_GitSync</c>,
/// which grants nobody write). A declaration on any other type is ignored. And only a node the
/// platform ISSUED is admitted: its last write was made by <see cref="WellKnownUsers.System"/>
/// (a governed executor), so a person cannot open a node to the world by writing a subject into
/// it — that remains a broad grant, which only a governed activity may write.</para>
///
/// <para>Declared on the type's <c>NodeTypeDefinition</c> as <c>pointRead</c>; the property names
/// below are the node content's JSON property names. It never admits an API-token caller: the
/// permission fold clamps a token by its scope's live <c>api</c> policy, which a browser surface
/// must not reopen.</para>
/// </summary>
public sealed record PointReadDeclaration
{
    /// <summary>
    /// The content property holding the ONE grant subject admitted to the point read:
    /// <see cref="WellKnownUsers.Anonymous"/> (every caller, signed in or not),
    /// <see cref="WellKnownUsers.Public"/> (every signed-in user), a group path (its members,
    /// transitively), or a user id. A node whose property is absent or blank admits nobody.
    /// </summary>
    public string SubjectProperty { get; init; } = "";

    /// <summary>
    /// The namespace whose DIRECT children the point read is honoured for (e.g. <c>l</c> for public
    /// links). Required — a declaration without one admits nobody. It must be a namespace only the
    /// platform writes, so that a node there carrying the System stamp was issued there by the
    /// platform: a person who could create in it could otherwise carry a System-authored source's
    /// stamps onto a node of their own (see <c>CreateNodeRequest.AuthorshipFrom</c>).
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// The layout areas of the node a point read may RENDER for an admitted caller — an allow-list,
    /// each area the type has made safe to show its audience. <c>""</c> names the hub's DEFAULT area
    /// (a page opened at the node's bare path). Empty (the default) admits the node itself and no
    /// layout area at all.
    /// </summary>
    public IReadOnlyList<string> Areas { get; init; } = [];

    /// <summary>
    /// The content property that, when it holds any non-null value, revokes the point read
    /// (e.g. <c>revokedAt</c>). Optional.
    /// </summary>
    public string? RevokedProperty { get; init; }

    /// <summary>
    /// The content property holding the instant the point read ends (e.g. <c>expiresAt</c>).
    /// When declared, a node without a parseable instant in the future admits nobody. Optional.
    /// </summary>
    public string? ExpiresProperty { get; init; }
}
