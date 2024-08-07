namespace MeshWeaver.Layout
{
    public record NamedAreaControl(object Data)
        : UiControl<NamedAreaControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);
}
