namespace MeshWeaver.Layout.Composition;

/// <summary>
/// What a node's LANDING PAGE does about the node's object actions: the <b>Edit</b> button and the
/// <b>⋯ More</b> dropdown that lists every option the node menu (<c>$Menu:Node</c>) offers (move,
/// copy, delete, pin, files, data, recycle, request signature and whatever else a module contributes).
///
/// <para>🚨 <b>Why a verdict exists at all.</b> The actions row rode on ONE renderer, the standard
/// header (<c>MeshNodeLayoutAreas.BuildHeader</c>). A node type that registered its own landing page
/// replaced that renderer, and the row went with it: a Markdown page showed every option, while an
/// Agent, a Store plugin or a desk showed none. The fix is the same inversion
/// <see cref="NodePageProvenance"/> made for the provenance line: the DEFAULT carries the menu, a page
/// that draws it itself says so, and a page that must not carry it says WHY.</para>
///
/// <para>Recorded on the <see cref="LayoutDefinition"/> beside the renderer it describes, and removed
/// with it by <see cref="LayoutDefinition.WithNamedRenderer(string, ObservableRenderer)"/>, for the
/// same reason the provenance verdict is: a verdict that outlived its renderer would describe a page
/// that is no longer registered.</para>
/// </summary>
public sealed record NodePageMenu
{
    private NodePageMenu(NodePageMenuKind kind, string? reason)
    {
        Kind = kind;
        Reason = reason;
    }

    /// <summary>Which of the three answers this page gives.</summary>
    public NodePageMenuKind Kind { get; }

    /// <summary>
    /// Why the page carries no actions row — required for <see cref="NodePageMenuKind.Declined"/>,
    /// null for the other two.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// The framework composes the actions row (Edit and ⋯ More) above the page. This is the default
    /// for a landing page registered without an opinion, so every node page offers the same options
    /// the Markdown page does.
    /// </summary>
    public static readonly NodePageMenu FrameworkSupplied = new(NodePageMenuKind.FrameworkSupplied, null);

    /// <summary>
    /// The page draws the actions row itself, through the standard header
    /// (<c>MeshNodeLayoutAreas.BuildHeader</c>) or the header actions slot. The framework adds
    /// nothing, because two ⋯ menus on one page is a worse page than one.
    /// </summary>
    public static readonly NodePageMenu RenderedByThePage = new(NodePageMenuKind.RenderedByThePage, null);

    /// <summary>
    /// The page carries no actions row, deliberately: a view that is embedded many times inside
    /// another page (a chat message inside its thread), an input surface with its own toolbar.
    ///
    /// <para>🚨 The reason is MANDATORY and is refused when blank, so a missing menu is always a
    /// sentence the next reader can check rather than an absence they have to guess about.</para>
    /// </summary>
    /// <param name="reason">Why this page carries no actions row.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null, empty or whitespace.</exception>
    public static NodePageMenu Declined(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "A node page that carries no actions row must say WHY in a sentence a reader can act "
                + "on — an empty reason records that somebody decided without recording what.",
                nameof(reason));
        return new NodePageMenu(NodePageMenuKind.Declined, reason);
    }

    /// <summary>
    /// The menu verdict a page gets when it states only a provenance verdict.
    ///
    /// <list type="bullet">
    /// <item><see cref="NodePageProvenanceKind.FrameworkSupplied"/> — the page draws no chrome, so the
    /// framework composes the menu: <see cref="FrameworkSupplied"/>.</item>
    /// <item><see cref="NodePageProvenanceKind.RenderedByThePage"/> — the page draws the standard
    /// header, which carries the menu: <see cref="RenderedByThePage"/>.</item>
    /// <item><see cref="NodePageProvenanceKind.Declined"/> — the page made a considered call to carry
    /// no node chrome, and several of these pages are embedded many times inside another page (each
    /// message of a chat thread is its message node's landing page). Composing a menu onto every
    /// one of them would put a row of buttons on every chat message, so the decline carries over:
    /// <see cref="Declined"/>, citing the provenance reason. Such a page opts in by passing
    /// <see cref="FrameworkSupplied"/> explicitly.</item>
    /// </list>
    /// </summary>
    /// <param name="provenance">The page's provenance verdict.</param>
    public static NodePageMenu For(NodePageProvenance provenance) => provenance.Kind switch
    {
        NodePageProvenanceKind.RenderedByThePage => RenderedByThePage,
        NodePageProvenanceKind.Declined => Declined(
            "The page declined the node chrome when it declined the provenance line: " + provenance.Reason),
        _ => FrameworkSupplied,
    };

    /// <inheritdoc />
    public override string ToString() => Reason is null ? Kind.ToString() : $"{Kind}: {Reason}";
}

/// <summary>The three answers a node landing page can give about the node's actions row.</summary>
public enum NodePageMenuKind
{
    /// <summary>The framework composes Edit and ⋯ More above the page — the default.</summary>
    FrameworkSupplied,

    /// <summary>The page draws the actions row itself (the standard header, or the header actions slot).</summary>
    RenderedByThePage,

    /// <summary>The page deliberately carries none; <see cref="NodePageMenu.Reason"/> says why.</summary>
    Declined
}
