using MeshWeaver.Utils;

namespace MeshWeaver.Layout;

public record LayoutAreaControl(object Address, LayoutAreaReference Reference)
    : UiControl<LayoutAreaControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public object DisplayArea { get; init; } = Reference.Area.Wordify();
    public object ShowProgress { get; init; } = true;

    public LayoutAreaControl WithDisplayArea(string displayArea) => this with { DisplayArea = displayArea };

}
