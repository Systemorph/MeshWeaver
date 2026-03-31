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

    public MarkdownEditorControl WithValue(string value) => this with { Value = value };
    public MarkdownEditorControl WithDocumentId(string documentId) => this with { DocumentId = documentId };
    public MarkdownEditorControl WithReadonly(bool @readonly) => this with { Readonly = @readonly };
    public MarkdownEditorControl WithHeight(string height) => this with { Height = height };
    public MarkdownEditorControl WithMaxHeight(string maxHeight) => this with { MaxHeight = maxHeight };
    public MarkdownEditorControl WithTrackChanges(bool enabled) => this with { TrackChangesEnabled = enabled };
    public MarkdownEditorControl WithCommentsPanel(bool show) => this with { ShowCommentsPanel = show };
    public MarkdownEditorControl WithPreview(bool show) => this with { ShowPreview = show };
    public MarkdownEditorControl WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };
    public MarkdownEditorControl WithAutoSave(string hubAddress, string nodePath) => this with { AutoSaveAddress = hubAddress, NodePath = nodePath };
}
