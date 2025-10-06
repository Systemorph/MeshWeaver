using MeshWeaver.Layout;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Control for displaying an article catalog with optional collection picker
/// </summary>
public record ArticleCatalogControl()
    : UiControl<ArticleCatalogControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// List of collection names to be displayed
    /// </summary>
    public object Collections { get; init; }


    /// <summary>
    /// List of addresses to be displayed.
    /// </summary>
    public object Addresses { get; init; }

}
