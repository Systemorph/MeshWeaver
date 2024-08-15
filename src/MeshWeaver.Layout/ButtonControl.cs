namespace MeshWeaver.Layout;

public record ButtonControl(object Data)
    : UiControl<ButtonControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public object IconStart { get; init; }
    public object IconEnd { get; init; }
    public bool? Disabled { get; init; }
    public object Appearance { get; set; }

    public ButtonControl WithIconStart(object icon) => this with { IconStart = icon };

    public ButtonControl WithIconEnd(object icon) => this with { IconEnd = icon };

    public ButtonControl WithDisabled(bool disabled) => this with { Disabled = disabled };
}
