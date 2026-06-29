namespace MeshWeaver.Layout;

/// <summary>
/// A control that triggers a client-side navigation to <see cref="Href"/> when rendered.
/// Placing this control in a layout area causes the Blazor renderer to navigate immediately.
/// </summary>
/// <param name="Href">The URL or mesh path to navigate to.</param>
public record RedirectControl(object Href) : UiControl<RedirectControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
