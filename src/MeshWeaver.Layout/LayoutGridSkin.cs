namespace MeshWeaver.Layout;


public record LayoutGridControl() : ContainerControlWithItemSkin<LayoutGridControl, LayoutGridSkin, LayoutGridItemSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
    protected override LayoutGridItemSkin CreateItemSkin(NamedAreaControl namedArea)
    {
        return new();
    }
}
public record LayoutGridSkin : Skin<LayoutGridSkin>
{
    public object AdaptiveRendering { get; init; }
    public object Justify { get; init; }
    public object Spacing { get; init; }

    public LayoutGridSkin WithAdaptiveRendering(object adaptiveRendering) => this with { AdaptiveRendering = adaptiveRendering };
    public LayoutGridSkin WithJustify(object justify) => this with { Justify = justify };
    public LayoutGridSkin WithSpacing(object spacing) => this with { Spacing = spacing };
}
