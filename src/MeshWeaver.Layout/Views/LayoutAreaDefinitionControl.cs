namespace MeshWeaver.Layout.Views;

/// <summary>
/// UI control that wraps a <see cref="LayoutAreaDefinition"/> for display in catalog and
/// navigation views. Carries optional thumbnail URLs (light and dark variants) and a hash
/// for cache-busting, enabling server-rendered previews of registered layout areas.
/// </summary>
/// <param name="Definition">The layout area definition this control represents.</param>
/// <param name="LightThumbnailUrl">URL of the thumbnail image for light-mode display; <c>null</c> if not available.</param>
/// <param name="DarkThumbnailUrl">URL of the thumbnail image for dark-mode display; <c>null</c> if not available.</param>
/// <param name="ThumbnailHash">Cache-busting hash appended to thumbnail URLs; <c>null</c> when no hash is available.</param>
public record LayoutAreaDefinitionControl(
    LayoutAreaDefinition Definition,
    string? LightThumbnailUrl = null,
    string? DarkThumbnailUrl = null,
    string? ThumbnailHash = null
) : UiControl<LayoutAreaDefinitionControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
