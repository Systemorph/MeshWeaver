namespace MeshWeaver.Layout;

/// <summary>
/// A control that wraps the Monaco editor for markdown editing
/// with comments and track changes support.
/// </summary>
public record MarkdownEditorControl()
    : UiControl<MarkdownEditorControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The markdown content to edit.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Document identifier for collaborative editing session.
    /// </summary>
    public object? DocumentId { get; init; }

    /// <summary>
    /// Whether the editor is read-only.
    /// </summary>
    public new object? Readonly { get; init; }

    /// <summary>
    /// The height of the editor (e.g., "400px", "100%").
    /// </summary>
    public object? Height { get; init; }

    /// <summary>
    /// The maximum height of the editor (e.g., "400px", "none").
    /// </summary>
    public object? MaxHeight { get; init; }

    /// <summary>
    /// Enable track changes mode. When enabled, edits are wrapped in markers
    /// that can be accepted/rejected.
    /// </summary>
    public object? TrackChangesEnabled { get; init; }

    /// <summary>
    /// Show the comments panel on the side.
    /// </summary>
    public object? ShowCommentsPanel { get; init; }

    /// <summary>
    /// Show the preview panel on the side.
    /// </summary>
    public object? ShowPreview { get; init; }

    /// <summary>
    /// Placeholder text when editor is empty.
    /// </summary>
    public object? Placeholder { get; init; }

    /// <summary>
    /// Target hub address for auto-save. When set, content changes are sent
    /// as DataChangeRequest to this address (debounced).
    /// </summary>
    public string? AutoSaveAddress { get; init; }

    /// <summary>
    /// The path of the node being edited. Used with AutoSaveAddress to identify
    /// which node's content to update.
    /// </summary>
    public string? NodePath { get; init; }

    /// <summary>Returns a copy with <paramref name="value"/> as the initial markdown content.</summary>
    /// <param name="value">The markdown text to display in the editor.</param>
    public MarkdownEditorControl WithValue(string value) => this with { Value = value };
    /// <summary>Returns a copy with <paramref name="documentId"/> set as the collaborative-editing session identifier.</summary>
    /// <param name="documentId">Unique identifier for the collaborative document session.</param>
    public MarkdownEditorControl WithDocumentId(string documentId) => this with { DocumentId = documentId };
    /// <summary>Returns a copy with <paramref name="readonly"/> controlling whether the editor is read-only.</summary>
    /// <param name="readonly">When <c>true</c>, the editor disables all input.</param>
    public MarkdownEditorControl WithReadonly(bool @readonly) => this with { Readonly = @readonly };
    /// <summary>Returns a copy with <paramref name="height"/> as the editor's CSS height (e.g. "400px", "100%").</summary>
    /// <param name="height">CSS height string for the editor container.</param>
    public MarkdownEditorControl WithHeight(string height) => this with { Height = height };
    /// <summary>Returns a copy with <paramref name="maxHeight"/> as the editor's CSS max-height (e.g. "600px", "none").</summary>
    /// <param name="maxHeight">CSS max-height string for the editor container.</param>
    public MarkdownEditorControl WithMaxHeight(string maxHeight) => this with { MaxHeight = maxHeight };
    /// <summary>Returns a copy with track-changes mode enabled or disabled.</summary>
    /// <param name="enabled">When <c>true</c>, edits are wrapped in accept/reject markers.</param>
    public MarkdownEditorControl WithTrackChanges(bool enabled) => this with { TrackChangesEnabled = enabled };
    /// <summary>Returns a copy with the side comments panel shown or hidden.</summary>
    /// <param name="show">When <c>true</c>, the comments panel is visible beside the editor.</param>
    public MarkdownEditorControl WithCommentsPanel(bool show) => this with { ShowCommentsPanel = show };
    /// <summary>Returns a copy with the markdown preview panel shown or hidden.</summary>
    /// <param name="show">When <c>true</c>, a live preview panel is shown beside the editor.</param>
    public MarkdownEditorControl WithPreview(bool show) => this with { ShowPreview = show };
    /// <summary>Returns a copy with <paramref name="placeholder"/> shown when the editor is empty.</summary>
    /// <param name="placeholder">Placeholder text displayed when there is no content.</param>
    public MarkdownEditorControl WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };
    /// <summary>
    /// Returns a copy configured for auto-save: content changes are sent as a
    /// <c>DataChangeRequest</c> to <paramref name="hubAddress"/> for the node at <paramref name="nodePath"/> (debounced).
    /// </summary>
    /// <param name="hubAddress">The hub address that will receive the auto-save request.</param>
    /// <param name="nodePath">The mesh-node path identifying which node to update.</param>
    public MarkdownEditorControl WithAutoSave(string hubAddress, string nodePath) => this with { AutoSaveAddress = hubAddress, NodePath = nodePath };
}
