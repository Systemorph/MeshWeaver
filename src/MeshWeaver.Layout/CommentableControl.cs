namespace MeshWeaver.Layout;

/// <summary>
/// Wraps ANY rendered content in the select-to-comment affordance: the reader selects text, a
/// floating "Comment" button appears, and the comment is anchored to that text on
/// <see cref="NodePath"/>'s node.
///
/// <para>This is the node-type-agnostic half of commenting. The comment LIST has always been
/// generic (<c>AddComments()</c> registers the <c>Comments</c> area for every node type), but the
/// affordance that CREATES an anchored comment lived inside <c>CollaborativeMarkdownView</c>, so
/// only markdown nodes had it. A social post, a rendered HTML block, a composed stack — anything
/// that shows text — was limited to whole-node comments.</para>
///
/// <para>Anchoring is text-based and always was: <c>CommentRendering.Capture</c> locates the
/// selection in <see cref="AnchorText"/> as a (Start, Length) range, and the highlight is
/// RECOMPUTED from that capture at display time, so it survives edits and needs no marker in the
/// stored content. Nothing in it is markdown-specific — which is why wrapping is enough.</para>
///
/// <para>🚨 <see cref="AnchorText"/> is the node's SOURCE text, not the rendered markup. The
/// rendered form may add chrome (an author line, a fold preview, styling) that does not exist in
/// the source; a selection inside such chrome simply fails to capture and falls back to an
/// unanchored comment — the same fallback a selection spanning markers already takes.</para>
///
/// <para>A container with ONE area: the wrapped content is a normal child area (the framework
/// renders children as <c>NamedAreaControl</c>s, never as a nested control property), so anything
/// that can be rendered can be wrapped — <c>Controls.Html</c>, a markdown block, a whole stack.</para>
/// </summary>
public record CommentableControl()
    : ContainerControl<CommentableControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The mesh node the comment is anchored to — the comment satellite is created under
    /// <c>{NodePath}/_Comment/</c> with <c>MainNode = NodePath</c>. Without it there is nothing to
    /// anchor to and the affordance stays hidden.
    /// </summary>
    public string? NodePath { get; init; }

    /// <summary>
    /// The node's plain SOURCE text that <c>CommentRendering.Capture</c> searches for the
    /// selection. For a markdown node this is the content with annotation markers stripped; for a
    /// plain-text node (a social post, a description) it is the text itself.
    /// </summary>
    public string? AnchorText { get; init; }

    /// <summary>
    /// Whether the viewer may create comments here. False renders the wrapped content untouched —
    /// no button, no selection listener — so a read-only or anonymous view costs nothing.
    /// Default-true so only the non-default value ever needs to serialize.
    /// </summary>
    public bool CanComment { get; init; } = true;

    /// <summary>Returns a copy anchored to <paramref name="nodePath"/>.</summary>
    public CommentableControl WithNodePath(string nodePath) => this with { NodePath = nodePath };

    /// <summary>Returns a copy whose selections are captured against <paramref name="anchorText"/>.</summary>
    public CommentableControl WithAnchorText(string anchorText) => this with { AnchorText = anchorText };

    /// <summary>Returns a copy with the affordance enabled or suppressed.</summary>
    public CommentableControl WithCanComment(bool canComment) => this with { CanComment = canComment };
}
