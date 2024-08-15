namespace MeshWeaver.Layout
{
    public record NamedAreaControl(object Area)
        : UiControl<NamedAreaControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        public object DisplayArea { get; init; }
        public object ShowProgress { get; init; }

        public NamedAreaControl WithArea(object area)
            => this with { Area = area };

        public NamedAreaControl WithDisplayArea(object displayArea)
        => this with { DisplayArea = displayArea };


    }
}
