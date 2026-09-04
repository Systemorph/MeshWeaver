namespace MeshWeaver.Layout;

/// <summary>
/// A control that wraps the Monaco diff editor for side-by-side comparison.
///
/// <para>🚨 <see cref="Height"/> sizes the view's HOST box, and that is not the same thing as sizing
/// the editor. The renderer delegates to BlazorMonaco, which emits its editor container as
/// <c>&lt;div id="…" class="…"&gt;</c> with no style attribute — so the container needs a stylesheet
/// rule of its own, or it collapses to zero pixels inside a perfectly correct 600px host and the
/// comparison is invisible with no error anywhere. That is MeshWeaver#3288, and the guard that pins
/// it is <c>MonacoEditorContainerSizingGuard</c> beside the view in MeshWeaver.Plugins. A renderer
/// added for this control elsewhere (React, MAUI) inherits the same obligation: give the editor's
/// own element a box.</para>
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
