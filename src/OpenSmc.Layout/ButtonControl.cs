namespace OpenSmc.Layout;

public record ButtonControl(object Data)
    : UiControl<ButtonControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data)
{
    public object Icon { get; init; }

    public ButtonControl WithIcon(object icon) => this with {Icon = icon};
}
