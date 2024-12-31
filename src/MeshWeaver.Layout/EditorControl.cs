namespace MeshWeaver.Layout;

public record EditorControl()
    : ContainerControlWithItemSkin<EditorControl, EditorSkin, PropertySkin>(ModuleSetup.ModuleName,
        ModuleSetup.ApiVersion, new())
{
    protected override PropertySkin CreateItemSkin(NamedAreaControl namedArea) => new();
}

public record EditorSkin : Skin<EditorSkin>;
