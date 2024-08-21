namespace MeshWeaver.Layout;

public record ButtonControl(object Data)
    : UiControl<ButtonControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public object IconStart { get; init; }
    public object IconEnd { get; init; }
    public object Disabled { get; init; }
    public object Appearance { get; set; }

    public ButtonControl WithIconStart(object icon) => this with { IconStart = icon };

    public ButtonControl WithIconEnd(object icon) => this with { IconEnd = icon };

    public ButtonControl WithDisabled(object disabled) => this with { Disabled = disabled };

    public ButtonControl WithAppearance(object appearance) => this with { Appearance = appearance };
}
