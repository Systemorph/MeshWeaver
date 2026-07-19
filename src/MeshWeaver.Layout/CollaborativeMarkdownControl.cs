namespace MeshWeaver.Layout;

/// <summary>
/// A control that renders markdown content with collaborative annotation support
/// (track changes, comments, view mode switching).
/// Used in the read-only overview of markdown nodes.
/// </summary>
public record CollaborativeMarkdownControl()
    : UiControl<CollaborativeMarkdownControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The annotated markdown content (with annotation markers).
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// The node path for comment creation/resolution.
    /// </summary>
    public string? NodePath { get; init; }

    /// <summary>
    /// The hub address for DataChangeRequest (accept/reject changes).
    /// </summary>
    public string? HubAddress { get; init; }

    /// <summary>
    /// Whether the current user can add comments by selecting text.
    /// </summary>
    public bool CanComment { get; init; }

    /// <summary>
    /// Whether the current user can accept/reject tracked changes.
    /// </summary>
    public bool CanEdit { get; init; }

    /// <summary>
    /// Suppresses ALL collaboration UI: comment highlights/sidebar, tracked-change review,
    /// the selection-comment affordance, and the page-comment footer. Set for @@ embeds, whose
    /// collaboration surface belongs to the embedded node's own page, not the embedding one.
    /// Default-false so only the non-default (true) value ever needs to serialize.
    /// </summary>
    public bool HideAnnotations { get; init; }

    /// <summary>Returns a copy with <paramref name="value"/> as the annotated markdown content.</summary>
    /// <param name="value">The markdown string (with annotation markers) to render.</param>
    /// <returns>A new <see cref="CollaborativeMarkdownControl"/> with the updated value.</returns>
    public CollaborativeMarkdownControl WithValue(string value) => this with { Value = value };
    /// <summary>Returns a copy with <paramref name="nodePath"/> as the node path for comment anchoring.</summary>
    /// <param name="nodePath">The mesh node path used when creating or resolving comments.</param>
    /// <returns>A new <see cref="CollaborativeMarkdownControl"/> with the updated node path.</returns>
    public CollaborativeMarkdownControl WithNodePath(string nodePath) => this with { NodePath = nodePath };
    /// <summary>Returns a copy with <paramref name="hubAddress"/> as the hub address for change requests.</summary>
    /// <param name="hubAddress">The hub address string used to route accept/reject change requests.</param>
    /// <returns>A new <see cref="CollaborativeMarkdownControl"/> with the updated hub address.</returns>
    public CollaborativeMarkdownControl WithHubAddress(string hubAddress) => this with { HubAddress = hubAddress };
    /// <summary>Returns a copy with <paramref name="canComment"/> controlling whether the user can annotate text.</summary>
    /// <param name="canComment">True to enable text-selection comment creation.</param>
    /// <returns>A new <see cref="CollaborativeMarkdownControl"/> with the updated can-comment flag.</returns>
    public CollaborativeMarkdownControl WithCanComment(bool canComment) => this with { CanComment = canComment };
    /// <summary>Returns a copy with <paramref name="canEdit"/> controlling whether the user can accept/reject changes.</summary>
    /// <param name="canEdit">True to enable accept/reject tracked-change controls.</param>
    /// <returns>A new <see cref="CollaborativeMarkdownControl"/> with the updated can-edit flag.</returns>
    public CollaborativeMarkdownControl WithCanEdit(bool canEdit) => this with { CanEdit = canEdit };
    /// <summary>Returns a copy with <paramref name="hideAnnotations"/> controlling whether collaboration UI is suppressed.</summary>
    /// <param name="hideAnnotations">True to render the markdown without any comment/tracked-change UI (used for @@ embeds).</param>
    /// <returns>A new <see cref="CollaborativeMarkdownControl"/> with the updated hide-annotations flag.</returns>
    public CollaborativeMarkdownControl WithHideAnnotations(bool hideAnnotations) => this with { HideAnnotations = hideAnnotations };
}
