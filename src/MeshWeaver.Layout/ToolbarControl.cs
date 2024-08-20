namespace MeshWeaver.Layout
{
    public record ToolbarControl() : ContainerControl<ToolbarControl,ToolbarSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new());

    public record ToolbarSkin : Skin<ToolbarSkin>
    {
        public object Orientation { get; set; }
        public ToolbarSkin WithOrientation(object orientation) => this with { Orientation = orientation };
    }
}
