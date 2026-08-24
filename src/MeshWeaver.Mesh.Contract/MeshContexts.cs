namespace MeshWeaver.Mesh;

/// <summary>
/// The visibility contexts a query can declare with <c>context:</c>.
///
/// <para>A context is how a surface says what it is FOR, which is what lets a node opt out of it
/// declaratively via <see cref="MeshNode.ExcludeFromContext"/>: <c>{"search"}</c> keeps a node out
/// of the search box while leaving it creatable, <c>{"content"}</c> keeps it out of the lists people
/// browse. Every query provider — static, Postgres, Cosmos — already honours that property, per node
/// and (when the marked node is a NodeType definition) per type. The only thing a surface owes is to
/// name itself honestly.</para>
///
/// <para>The names are constants rather than literals so a module can state its own visibility next
/// to its own registration instead of being enumerated in a list somewhere else. See
/// <c>MeshNodeVisibilityExtensions</c> for the fluent form.</para>
/// </summary>
public static class MeshContexts
{
    /// <summary>The search box.</summary>
    public const string Search = "search";

    /// <summary>Create menus and type pickers — the surfaces that are ABOUT node types.</summary>
    public const string Create = "create";

    /// <summary>The header / navigation chrome.</summary>
    public const string Header = "header";

    /// <summary>
    /// Browsing CONTENT — the home screen's list, a node's children, any surface showing a person
    /// things they can open. Implied by <c>is:content</c>, so a surface names itself once and gets
    /// both halves: registration nodes are withheld, and anything marked
    /// <c>ExcludeFromContext: ["content"]</c> stays out.
    ///
    /// <para>Before this existed, those surfaces passed <c>context:search</c> — claiming to be the
    /// search box, because that was the only context whose filtering happened to do what they
    /// wanted. Borrowing a name you do not mean is why each of them ALSO carried its own
    /// <c>-nodeType:…</c> patch on top.</para>
    /// </summary>
    public const string Content = "content";
}
