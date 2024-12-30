namespace MeshWeaver.Layout;

public record EditorControl()
    : ContainerControlWithItemSkin<EditorControl, EditorSkin, EditFormItemSkin>(ModuleSetup.ModuleName,
        ModuleSetup.ApiVersion, new())
{
    protected override EditFormItemSkin CreateItemSkin(NamedAreaControl namedArea) => new();
}

public record EditorSkin : Skin<EditorSkin>;
