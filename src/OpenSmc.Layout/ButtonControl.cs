namespace OpenSmc.Layout;

public record ButtonControl(object Data)
    : UiControl<ButtonControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data)
{
    public object IconStart { get; init; }
    public object IconEnd { get; init; }

    public ButtonControl WithIconStart(object icon) => this with { IconStart = icon };

    public ButtonControl WithIconEnd(object icon) => this with { IconEnd = icon };
}
