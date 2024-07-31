namespace OpenSmc.Layout;

public record LayoutGridControl : LayoutStackControl
{
    public bool AdaptiveRendering { get; init; }
    public JustifyContent Justify { get; init; }
    public int? Spacing { get; init; }

    public LayoutGridControl WithAdaptiveRendering(bool adaptiveRendering) => this with { AdaptiveRendering = adaptiveRendering };
    public LayoutGridControl WithJustify(JustifyContent justify) => this with { Justify = justify };
    public LayoutGridControl WithSpacing(int spacing) => this with { Spacing = spacing };
}
