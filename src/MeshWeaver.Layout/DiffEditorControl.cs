namespace MeshWeaver.Layout;

/// <summary>
/// A control that wraps the Monaco diff editor for side-by-side comparison.
/// </summary>
public record DiffEditorControl() : UiControl<DiffEditorControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The original (left-side) content.
    /// </summary>
    public string OriginalContent { get; init; } = "";

    /// <summary>
    /// The modified (right-side) content.
    /// </summary>
    public string ModifiedContent { get; init; } = "";

    /// <summary>
    /// Label for the original content (e.g., "Version 3").
    /// </summary>
    public string OriginalLabel { get; init; } = "Original";

    /// <summary>
    /// Label for the modified content (e.g., "Current").
    /// </summary>
    public string ModifiedLabel { get; init; } = "Current";

    /// <summary>
    /// The language for syntax highlighting (e.g., "markdown", "json").
    /// </summary>
    public string Language { get; init; } = "markdown";

    /// <summary>
    /// The height of the diff editor (e.g., "500px", "100%").
    /// </summary>
    public string Height { get; init; } = "500px";
}
