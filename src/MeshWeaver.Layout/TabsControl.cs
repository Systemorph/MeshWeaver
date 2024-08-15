namespace MeshWeaver.Layout;

public record TabsControl() :
    ContainerControlWithItemSkin<TabsControl,TabsSkin,TabSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{


    protected override TabSkin CreateItemSkin(NamedAreaControl ret)
    {
        return new TabSkin(ret.Id);
    }
}

public record TabsSkin : Skin<TabsSkin>
{
    public object ActiveTabId { get; init; }
    public object Orientation { get; set; }
    public object Height { get; set; }
}

public record TabSkin(object Label) : Skin<TabSkin>;
