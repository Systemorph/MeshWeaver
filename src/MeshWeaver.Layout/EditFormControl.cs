namespace MeshWeaver.Layout;

public record EditFormControl()
    : ContainerControlWithItemSkin<EditFormControl, EditFormSkin, EditFormItemSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
    protected override EditFormItemSkin CreateItemSkin(NamedAreaControl namedArea)
        => new();
}

public record EditFormSkin : Skin<EditFormSkin>;


public record EditFormItemSkin : Skin<EditFormItemSkin>
{
    public object Description { get; init; }
    public object Name { get; init; }
    public object Label { get; set; }
}
