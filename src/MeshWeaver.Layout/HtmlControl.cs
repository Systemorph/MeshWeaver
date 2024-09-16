namespace MeshWeaver.Layout
{
    public record HtmlControl(object Data)
        : UiControl<HtmlControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
}
