namespace MeshWeaver.Layout;

/// <summary>
/// Wraps the control it is added to in a collapsible section: a header row carrying
/// <see cref="Title"/> (and, optionally, a one-line <see cref="Summary"/>) that the viewer clicks to
/// show or hide the content.
/// </summary>
/// <remarks>
/// <para>Add it LAST, so it is the outermost skin: <c>Controls.Stack.WithView(…).AddSkin(Skins.Expander("Details"))</c>.
/// The renderers pop the last skin first, so an expander added after a container's own skin wraps
/// the whole container.</para>
/// <para>🚨 <b><see cref="Expanded"/> is a DECLARATION, never state.</b> The layout area computes it
/// (typically from the node it renders — "while the run executes, the execution section is open"),
/// and the viewer's click toggles a local, per-viewer state that is written nowhere: not onto the
/// node, not into the area's data. The renderer keeps the viewer's choice across re-renders that
/// declare the SAME value, and re-applies the declaration when a later emission declares a
/// DIFFERENT one — so a page whose state moves on opens the section that now matters, while a
/// re-render caused by anything else (a clock tick, a sibling's update) leaves what the viewer
/// opened alone.</para>
/// <para>For more information on the Blazor rendering, visit the
/// <a href="https://www.fluentui-blazor.net/accordion">Fluent UI Blazor Accordion documentation</a>.</para>
/// </remarks>
public record ExpanderSkin : Skin<ExpanderSkin>
{
    /// <summary>The header text, always visible. A string, or a data binding.</summary>
    public object? Title { get; init; }

    /// <summary>
    /// An optional one-line summary shown beside the title — what a reader needs from a COLLAPSED
    /// section without opening it. A string, or a data binding.
    /// </summary>
    public object? Summary { get; init; }

    /// <summary>
    /// Whether the section is declared open. <c>null</c> means open. A <see cref="bool"/>, or a data
    /// binding. See the remarks on <see cref="ExpanderSkin"/>: a declaration the viewer may override,
    /// never persisted state.
    /// </summary>
    public object? Expanded { get; init; }

    /// <summary>Returns a copy with <paramref name="title"/> as the header text.</summary>
    /// <param name="title">The header text, or a data binding.</param>
    /// <returns>A new <see cref="ExpanderSkin"/> with the specified title.</returns>
    public ExpanderSkin WithTitle(object? title) => This with { Title = title };

    /// <summary>Returns a copy with <paramref name="summary"/> as the one-line summary beside the title.</summary>
    /// <param name="summary">The summary text, or a data binding; <c>null</c> shows none.</param>
    /// <returns>A new <see cref="ExpanderSkin"/> with the specified summary.</returns>
    public ExpanderSkin WithSummary(object? summary) => This with { Summary = summary };

    /// <summary>Returns a copy that declares the section open (<c>true</c>) or collapsed (<c>false</c>).</summary>
    /// <param name="expanded">The declared state, or a data binding.</param>
    /// <returns>A new <see cref="ExpanderSkin"/> with the specified declaration.</returns>
    public ExpanderSkin WithExpanded(object? expanded) => This with { Expanded = expanded };
}
