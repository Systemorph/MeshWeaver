namespace MeshWeaver.Layout;

public record IconControl(object Data)
    : UiControl<IconControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public string Color { get; init; }
    public object Width { get; init; }

    public IconControl WithWidth(object width) => this with { Width = width };
    public IconControl WithColor(string color) => this with { Color = color };
};
