namespace MeshWeaver.Layout;

public record LayoutGridSkin : Skin<LayoutGridSkin>
{
    public bool AdaptiveRendering { get; init; }
    public JustifyContent Justify { get; init; }
    public int? Spacing { get; init; }

    public LayoutGridSkin WithAdaptiveRendering(bool adaptiveRendering) => this with { AdaptiveRendering = adaptiveRendering };
    public LayoutGridSkin WithJustify(JustifyContent justify) => this with { Justify = justify };
    public LayoutGridSkin WithSpacing(int spacing) => this with { Spacing = spacing };
}
