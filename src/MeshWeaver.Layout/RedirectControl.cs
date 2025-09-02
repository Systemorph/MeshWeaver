namespace MeshWeaver.Layout;

public record RedirectControl(object Href) : UiControl<RedirectControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
