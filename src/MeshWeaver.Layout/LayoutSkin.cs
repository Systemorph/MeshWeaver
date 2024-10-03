namespace MeshWeaver.Layout;

/// <summary>
/// Represents a spacer control with customizable properties.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/spacer">Fluent UI Blazor Spacer documentation</a>.
/// </remarks>
public record SpacerControl() : UiControl<SpacerControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
