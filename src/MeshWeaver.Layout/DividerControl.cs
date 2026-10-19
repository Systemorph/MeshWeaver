namespace MeshWeaver.Layout;

/// <summary>
/// A thin rule that separates two regions of a page — a header from its body, one group of facts
/// from the next — instead of a hand-built <c>Controls.Html("&lt;hr style=…&gt;")</c>. Renderers draw
/// it in the theme's neutral stroke colour, so it follows light and dark themes, and give it the
/// <c>separator</c> role. Spacing around it is the caller's: compose it in a stack with a gap, or
/// add a margin with <c>WithStyle</c>.
/// </summary>
public record DividerControl() : UiControl<DividerControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// <see cref="Layout.Orientation.Horizontal"/> (the default) draws a full-width rule between
    /// stacked content; <see cref="Layout.Orientation.Vertical"/> a full-height rule between
    /// side-by-side content.
    /// </summary>
    public Orientation Orientation { get; init; } = Orientation.Horizontal;

    /// <summary>Sets the orientation (<see cref="Orientation"/>).</summary>
    /// <param name="orientation">Horizontal or vertical.</param>
    /// <returns>A new <see cref="DividerControl"/> with the orientation set.</returns>
    public DividerControl WithOrientation(Orientation orientation) => this with { Orientation = orientation };
}
