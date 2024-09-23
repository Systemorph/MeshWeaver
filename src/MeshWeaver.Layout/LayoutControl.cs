namespace MeshWeaver.Layout;

/// <summary>
/// Represents a layout control with customizable properties.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/layout">Fluent UI Blazor Layout documentation</a>.
/// </remarks>
public record LayoutControl() : ContainerControl<LayoutControl, LayoutSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
}

/// <summary>
/// Represents the skin for a layout control.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/layout">Fluent UI Blazor Layout documentation</a>.
/// </remarks>
public record LayoutSkin : Skin<LayoutSkin>;

/// <summary>
/// Represents the skin for a header control.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/header">Fluent UI Blazor Header documentation</a>.
/// </remarks>
public record HeaderSkin : Skin<HeaderSkin>;

/// <summary>
/// Represents the skin for a footer control.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/footer">Fluent UI Blazor Footer documentation</a>.
/// </remarks>
public record FooterSkin : Skin<FooterSkin>;
