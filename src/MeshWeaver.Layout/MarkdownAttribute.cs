namespace MeshWeaver.Layout;

/// <summary>
/// Attribute for markdown fields that configures display with MarkdownControl
/// and editing with MarkdownEditorControl.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class MarkdownAttribute : UiControlAttribute
{
    public MarkdownAttribute()
        : base(typeof(MarkdownControl), typeof(MarkdownEditorControl))
    {
        SeparateEditView = true; // Default: own edit button
    }

    /// <summary>
    /// Height of the markdown editor.
    /// </summary>
    public string EditorHeight { get; set; } = "300px";

    /// <summary>
    /// Whether to show preview in the editor.
    /// </summary>
    public bool ShowPreview { get; set; } = true;

    /// <summary>
    /// Whether to enable track changes functionality.
    /// </summary>
    public bool TrackChanges { get; set; }

    /// <summary>
    /// Placeholder text shown in the editor.
    /// </summary>
    public string Placeholder { get; set; } = "Enter content (supports Markdown formatting)";
}
