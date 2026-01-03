namespace MeshWeaver.Layout;

/// <summary>
/// A control that wraps the Monaco editor for collaborative markdown editing
/// with comments and track changes support.
/// </summary>
public record CollaborativeMarkdownEditorControl()
    : UiControl<CollaborativeMarkdownEditorControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
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

    public CollaborativeMarkdownEditorControl WithValue(string value) => this with { Value = value };
    public CollaborativeMarkdownEditorControl WithDocumentId(string documentId) => this with { DocumentId = documentId };
    public CollaborativeMarkdownEditorControl WithReadonly(bool @readonly) => this with { Readonly = @readonly };
    public CollaborativeMarkdownEditorControl WithHeight(string height) => this with { Height = height };
    public CollaborativeMarkdownEditorControl WithTrackChanges(bool enabled) => this with { TrackChangesEnabled = enabled };
    public CollaborativeMarkdownEditorControl WithCommentsPanel(bool show) => this with { ShowCommentsPanel = show };
    public CollaborativeMarkdownEditorControl WithPreview(bool show) => this with { ShowPreview = show };
    public CollaborativeMarkdownEditorControl WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };
}
