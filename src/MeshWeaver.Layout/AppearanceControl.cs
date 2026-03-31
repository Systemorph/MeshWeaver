namespace MeshWeaver.Layout;

/// <summary>
/// Marker control for rendering the appearance/theme settings panel.
/// The Blazor renderer (AppearanceView) handles theme, color, and direction picking.
/// </summary>
public record AppearanceControl()
    : UiControl<AppearanceControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
