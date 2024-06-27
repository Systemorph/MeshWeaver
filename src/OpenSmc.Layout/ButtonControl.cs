namespace OpenSmc.Layout;

public record ButtonControl(object Data)
    : UiControl<ButtonControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);
