using MeshWeaver.Blazor.Components;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Graph;

/// <summary>
/// Extensions for adding Graph Blazor views to the application.
/// </summary>
public static class BlazorGraphExtensions
{
    /// <summary>
    /// Adds the Graph Blazor views (MeshNodeEditorView, MeshNodeThumbnailView) to the configuration.
    /// Also enables @ autocomplete for unified content references in markdown editors.
    /// </summary>
    public static MessageHubConfiguration AddGraphViews(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(typeof(MeshNodeEditorControl), typeof(MeshNodeThumbnailControl), typeof(MeshNodeCardControl))
            .AddViews(registry => registry
                .WithView<MeshNodeEditorControl, MeshNodeEditorView>()
                .WithView<MeshNodeThumbnailControl, MeshNodeThumbnailView>()
                .WithView<MeshNodeCardControl, MeshNodeCardView>())
            .AddMeshNavigation();  // Enable @ autocomplete in markdown editors
    }
}
