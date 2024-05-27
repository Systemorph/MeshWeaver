namespace OpenSmc.Layout;

public record LayoutAreaControl(object Address, LayoutAreaReference Reference)
    : UiControl<LayoutAreaControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null);
