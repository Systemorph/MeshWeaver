namespace MeshWeaver.Layout;

public record TabsControl() :
    ContainerControl<TabsControl, UiControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public TabsControl WithTab(object label, UiControl item, Func<NamedAreaControl, NamedAreaControl> options = null)
        => WithTab(new(label), item, options);
    public TabsControl WithTab(TabSkin skin, UiControl item, Func<NamedAreaControl, NamedAreaControl> options = null)
    {
        options ??= x => x;
        return WithItem(item.WithSkin(skin), x => options.Invoke(x.WithId(skin.Label)));
    }

    public object ActiveTabId { get; init; }
    public object Height { get; init; }
    public object Orientation { get; init; }
}
