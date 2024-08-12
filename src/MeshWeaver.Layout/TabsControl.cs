namespace MeshWeaver.Layout
{
    public record TabsControl() :
        ContainerControl<TabsControl, UiControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
    {
        public TabsControl WithTab(object label, UiControl item)
            => WithTab(label, item, x => x);
        public TabsControl WithTab(object label, UiControl item, Func<TabSkin, TabSkin> configuration)
            => WithItem(item, x => x.WithId(label).WithSkin(configuration.Invoke(new(label))));

        public object ActiveTabId { get; init; }
        public object Height { get; init; }
        public object Orientation { get; init; }
    }
}
