using MeshWeaver.Documentation;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.MeshBrowser.ViewModel;

/// <summary>
/// MeshBrowser views and configurations.
/// </summary>
public static class MeshBrowserViewModels
{
    /// <summary>
    /// Registers MeshBrowser views to the provided MessageHub configuration.
    /// </summary>
    /// <param name="configuration">The MessageHub configuration to add MeshBrowser to.</param>
    /// <returns>The updated MessageHub configuration with MeshBrowser views and documentation added.</returns>
    /// <remarks>
    /// This method adds MeshBrowser views to the layout, adds menu items and documentation.
    /// </remarks>
    public static MessageHubConfiguration AddMeshBrowserViewModels(
        this MessageHubConfiguration configuration
    )
        => configuration
            .AddLayout(layout => layout
                .WithPageLayout()
                .AddCatalog()
            )
            ;
}
