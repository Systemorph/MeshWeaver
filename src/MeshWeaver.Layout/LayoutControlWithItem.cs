namespace MeshWeaver.Layout;

public record LayoutControl() : ContainerControl<LayoutControl, LayoutSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{

}


public record LayoutSkin : Skin<LayoutSkin>;
public record HeaderSkin : Skin<HeaderSkin>;

public record FooterSkin : Skin<FooterSkin>;
