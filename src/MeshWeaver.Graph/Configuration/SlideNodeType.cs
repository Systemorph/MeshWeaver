namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The compiled residue of the retired built-in Slide node type. Slides are owned by the
/// Publish pack (<c>Publish/Slide</c>, a dynamic NodeType with in-mesh layout areas); V53
/// retyped every bare <c>Slide</c> instance mesh-wide (#1589). What stays compiled: this
/// type's <see cref="NodeType"/> const and suffix-aware <see cref="Matches"/> (persistence
/// parser + export gates) and the <see cref="SlideContent"/> record
/// (<c>MarkdownFileParser</c> builds it pre-activation).
/// </summary>
public static class SlideNodeType
{
    /// <summary>
    /// The NodeType value used to identify slide nodes.
    /// </summary>
    public const string NodeType = "Slide";

    /// <summary>
    /// True when <paramref name="nodeType"/> is a slide type: the built-in <c>Slide</c> OR any
    /// plugin-installed variant whose dynamic type identity ends in <c>/Slide</c> (a dynamic
    /// NodeType's identity is its install path, e.g. <c>Publish/Slide</c> — instances carry it
    /// verbatim). Every compiled gate that used to compare against <see cref="NodeType"/> with
    /// <c>==</c> must use this instead: an equality gate silently excludes all plugin-typed
    /// slides (education's 116 <c>Publish/Slide</c> nodes were invisible to the deck sibling
    /// query). Same precedent as <c>MarkdownFileParser.IsSlideNodeType</c>.
    /// </summary>
    public static bool Matches(string? nodeType) =>
        nodeType == NodeType
        || nodeType?.EndsWith("/" + NodeType, StringComparison.Ordinal) == true;
}

/// <summary>
/// The content of a Slide MeshNode — one page of a presentation deck.
/// Immutable; every mutation goes through
/// <c>workspace.GetMeshNodeStream(path).Update(...)</c>.
/// </summary>
public record SlideContent
{
    /// <summary>
    /// The slide body as markdown. Raw HTML and SVG pass through the markdown
    /// pipeline unchanged, so a slide can be anything from a bullet list to a
    /// full-bleed illustration.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Speaker notes (markdown). Shown only in the Notes view, never on the stage.
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>
    /// CSS background for the slide stage (e.g. a gradient like
    /// <c>linear-gradient(135deg, #667eea 0%, #764ba2 100%)</c>).
    /// When null, the stage uses the theme-aware default gradient.
    /// </summary>
    public string? Background { get; init; }

    /// <summary>
    /// Reserved for a future slide-transition effect. Default null (no transition).
    /// </summary>
    public string? Transition { get; init; }
}
