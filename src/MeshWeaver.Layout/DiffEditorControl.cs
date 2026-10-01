using MeshWeaver.Data;

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
    /// The original (left-side) content as a LITERAL string — text the producing area already holds.
    /// To show text that lives in data (a node field, a fed <c>/data</c> entry) use the bindable
    /// <see cref="Original"/> instead, which wins when set.
    /// </summary>
    public string OriginalContent { get; init; } = "";

    /// <summary>
    /// The modified (right-side) content as a LITERAL string. The bindable twin is
    /// <see cref="Modified"/>, which wins when set.
    /// </summary>
    public string ModifiedContent { get; init; } = "";

    /// <summary>
    /// The BINDABLE original (left-side) text: a literal, or a <see cref="JsonPointerReference"/>
    /// the renderer resolves and FOLLOWS. A relative pointer resolves against the control's
    /// DataContext — node-bound (<see cref="LayoutAreaReference.GetMeshNodeDataContext"/>, see
    /// <see cref="BindToNode"/>) or an ordinary <c>/data</c> context; an absolute pointer
    /// (<c>/data/…</c>) always reads the layout area's data. When set it takes precedence over
    /// <see cref="OriginalContent"/>; null falls back to it.
    /// </summary>
    public object? Original { get; init; }

    /// <summary>
    /// The BINDABLE modified (right-side) text — the twin of <see cref="Original"/>; takes
    /// precedence over <see cref="ModifiedContent"/> when set.
    /// </summary>
    public object? Modified { get; init; }

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

    /// <summary>Returns a copy whose original (left) text is <paramref name="original"/> — a literal
    /// or a <see cref="JsonPointerReference"/> the renderer follows. See <see cref="Original"/>.</summary>
    /// <param name="original">The text, or a pointer to it.</param>
    /// <returns>A new instance with the updated <see cref="Original"/>.</returns>
    public DiffEditorControl WithOriginal(object original) => this with { Original = original };

    /// <summary>Returns a copy whose modified (right) text is <paramref name="modified"/> — a literal
    /// or a <see cref="JsonPointerReference"/> the renderer follows. See <see cref="Modified"/>.</summary>
    /// <param name="modified">The text, or a pointer to it.</param>
    /// <returns>A new instance with the updated <see cref="Modified"/>.</returns>
    public DiffEditorControl WithModified(object modified) => this with { Modified = modified };

    /// <summary>
    /// Returns a copy that compares two fields of ONE node, both read live off its node stream:
    /// <paramref name="originalField"/> on the left, <paramref name="modifiedField"/> on the right.
    /// The producing area renders the diff at once and never loads the node — the renderer binds both
    /// panes and redraws when either field changes (Doc/GUI/DataBinding → "Binding a rich control to
    /// a node field"). For a text that does not live on that node, set <see cref="Original"/> /
    /// <see cref="Modified"/> to an absolute <c>/data/…</c> pointer instead.
    /// </summary>
    /// <param name="nodePath">Path of the node both fields live on.</param>
    /// <param name="originalField">The left-hand field, e.g. <c>"baselineText"</c>.</param>
    /// <param name="modifiedField">The right-hand field, e.g. <c>"text"</c>.</param>
    /// <param name="bindContent"><c>true</c> (default) resolves both fields against the node's
    /// <c>Content</c>; <c>false</c> against its top-level fields.</param>
    /// <returns>A new instance bound to the two node fields.</returns>
    public DiffEditorControl BindToNode(string nodePath, string originalField, string modifiedField, bool bindContent = true) =>
        this with
        {
            Original = new JsonPointerReference(originalField),
            Modified = new JsonPointerReference(modifiedField),
            DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent),
        };
}
