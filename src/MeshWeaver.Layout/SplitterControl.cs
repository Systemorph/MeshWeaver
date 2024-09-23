namespace MeshWeaver.Layout
{
    public record SplitterControl() : 
        ContainerControlWithItemSkin<SplitterControl, SplitterSkin, SplitterPaneSkin>
        (ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new SplitterSkin())
    {
        protected override SplitterPaneSkin CreateItemSkin(NamedAreaControl namedArea)
            => new();
    }

    public record SplitterSkin : Skin<SplitterSkin>
    {
        public object Width { get; set; }
        public object Height { get; set; }

        public object Orientation { get; set; }
        public SplitterSkin WithWidth(object width) => this with { Width = width };
        public SplitterSkin WithHeight(object height) => this with { Height = height };
        public SplitterSkin WithOrientation(object orientation) => this with { Orientation = orientation };

    }
}
