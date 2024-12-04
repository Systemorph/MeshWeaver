using MeshWeaver.Catalog.Layout;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;

namespace MeshWeaver.Catalog.ViewModel;

/// <summary>
/// MeshBrowser views and configurations.
/// </summary>
public static class CatalogViewModels
{
    /// <summary>
    /// Registers MeshBrowser areas to the provided MessageHub configuration.
    /// </summary>
    /// <param name="configuration">The MessageHub configuration to add MeshBrowser to.</param>
    /// <returns>The updated MessageHub configuration with MeshBrowser views and documentation added.</returns>
    /// <remarks>
    /// This method adds MeshBrowser views to the layout, adds menu items and documentation.
    /// </remarks>
    public static MessageHubConfiguration AddCatalogViewModels(
        this MessageHubConfiguration configuration
    )
        => configuration
            .WithTypes(typeof(CatalogItemControl))
            .AddLayout(layout => layout
                .AddCatalog()
            )
            ;
}
