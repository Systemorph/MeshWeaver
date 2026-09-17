namespace MeshWeaver.Layout.Composition;

/// <summary>
/// What a node's LANDING PAGE does about provenance — the <c>Type · Created · Updated</c> line that
/// answers "who wrote this, and when".
///
/// <para>🚨 <b>Why a verdict exists at all.</b> Until Systemorph/MeshWeaver#4500 the line rode on
/// ONE renderer: a node type that registered its own landing page replaced that renderer, and the
/// line went with it. Nothing recorded the loss, so an omission that someone had WEIGHED (an
/// Activity page reports its own run state; a Thread reports per message) and one that nobody had
/// THOUGHT ABOUT were indistinguishable from outside — both were simply an absence. Measured on
/// 2026-09-16 across the fleet, 86 of the 99 landing pages that replace the framework's renderer
/// shipped without the line; 82 of those are in MeshWeaver.Plugins, where no core reviewer sees
/// them. At that ratio, adding call sites is not a fix.</para>
///
/// <para>The verdict makes the state REPRESENTABLE. Every landing page registered through
/// <c>MeshNodeLayoutAreas.WithNodePage</c> records one, the default one CARRIES the line, and a
/// page that wants none has to say so in a sentence somebody can read. "Nobody decided" stops
/// being expressible — which is the whole defect, in one line.</para>
///
/// <para>🚨 The verdict lives on <see cref="LayoutDefinition"/> beside the renderer it describes,
/// and <see cref="LayoutDefinition.WithNamedRenderer(string, ObservableRenderer)"/> REMOVES it —
/// so it can never outlive the page it was recorded for. A stale verdict would be worse than none:
/// it would make the guard pass having checked a renderer that no longer exists.</para>
///
/// <para>Lives in <c>MeshWeaver.Layout</c> rather than <c>MeshWeaver.Graph</c> for the same reason
/// <see cref="LayoutAreaSlots"/> does: the layout engine has to be able to name the thing without
/// taking a type dependency on Graph.</para>
/// </summary>
public sealed record NodePageProvenance
{
    private NodePageProvenance(NodePageProvenanceKind kind, string? reason)
    {
        Kind = kind;
        Reason = reason;
    }

    /// <summary>Which of the three answers this page gives.</summary>
    public NodePageProvenanceKind Kind { get; }

    /// <summary>
    /// Why the page declines the line — required for <see cref="NodePageProvenanceKind.Declined"/>,
    /// null for the other two. This is the sentence the next reader gets instead of an absence.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// The framework composes the provenance line above the page's own content. This is the
    /// DEFAULT: a landing page registered without an opinion gets the line, so forgetting produces
    /// a correct page rather than a silently incomplete one.
    /// </summary>
    public static readonly NodePageProvenance FrameworkSupplied =
        new(NodePageProvenanceKind.FrameworkSupplied, null);

    /// <summary>
    /// The page renders the line itself — it calls <c>MeshNodeLayoutAreas.BuildHeader</c> or
    /// <c>BuildMetaRow</c> somewhere in its own composition. The framework adds nothing, because
    /// two provenance lines on one page is a worse answer than one.
    /// </summary>
    public static readonly NodePageProvenance RenderedByThePage =
        new(NodePageProvenanceKind.RenderedByThePage, null);

    /// <summary>
    /// The page carries no provenance line, deliberately — an emergency card for a node that could
    /// not compile, a page whose subject is a PERSON rather than a document, a run page that
    /// reports its own start and end instead.
    ///
    /// <para>🚨 The reason is MANDATORY and is refused when blank. An empty decline is exactly the
    /// state this type exists to abolish: it would record that a decision was made without
    /// recording what it was, and the next reader would be back to guessing at an absence.</para>
    /// </summary>
    /// <param name="reason">Why this page answers the question some other way, or not at all.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null, empty or whitespace.</exception>
    public static NodePageProvenance Declined(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "A declined node page must say WHY in a sentence a reader can act on — an empty "
                + "reason records that somebody decided without recording what they decided, which "
                + "is the state NodePageProvenance exists to abolish (#4500).",
                nameof(reason));
        return new NodePageProvenance(NodePageProvenanceKind.Declined, reason);
    }

    /// <inheritdoc />
    public override string ToString() =>
        Reason is null ? Kind.ToString() : $"{Kind}: {Reason}";
}

/// <summary>The three answers a node landing page can give about provenance.</summary>
public enum NodePageProvenanceKind
{
    /// <summary>The framework composes the provenance line above the page — the default.</summary>
    FrameworkSupplied,

    /// <summary>The page renders the line itself (it calls <c>BuildHeader</c> / <c>BuildMetaRow</c>).</summary>
    RenderedByThePage,

    /// <summary>The page deliberately carries none; <see cref="NodePageProvenance.Reason"/> says why.</summary>
    Declined
}
