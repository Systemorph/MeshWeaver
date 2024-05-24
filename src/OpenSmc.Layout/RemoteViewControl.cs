namespace OpenSmc.Layout;

public record RemoteViewControl(object Address, LayoutAreaReference Reference)
    : UiControl<RemoteViewControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null);
