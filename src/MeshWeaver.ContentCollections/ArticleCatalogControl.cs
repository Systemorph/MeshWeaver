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
    public object? Collections { get; init; }

    /// <summary>
    /// List of collection configurations to be initialized
    /// </summary>
    public object? CollectionConfigurations { get; init; }

    /// <summary>
    /// Selected collection
    /// </summary>
    public object? SelectedCollection { get; init; }

    /// <summary>
    /// Returns a copy of this control with the provided collections value.
    /// </summary>
    public ArticleCatalogControl WithCollections(object? collections)
        => this with { Collections = collections };

    /// <summary>
    /// Returns a copy of this control with the provided collection configurations value.
    /// </summary>
    public ArticleCatalogControl WithCollectionConfigurations(object? collectionConfigurations)
        => this with { CollectionConfigurations = collectionConfigurations };

    /// <summary>
    /// Returns a copy of this control with the provided selected collection value.
    /// </summary>
    public ArticleCatalogControl WithSelectedCollection(object? selectedCollection)
        => this with { SelectedCollection = selectedCollection };

}
