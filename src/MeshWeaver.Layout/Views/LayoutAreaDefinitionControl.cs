namespace MeshWeaver.Layout.Views;

public record LayoutAreaDefinitionControl(
    LayoutAreaDefinition Definition,
    string? LightThumbnailUrl = null,
    string? DarkThumbnailUrl = null,
    string? ThumbnailHash = null
) : UiControl<LayoutAreaDefinitionControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
