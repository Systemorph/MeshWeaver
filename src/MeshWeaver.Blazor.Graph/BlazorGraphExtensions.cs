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
    /// Adds the Graph Blazor views (MeshNodeEditorView) to the configuration.
    /// </summary>
    public static MessageHubConfiguration AddGraphViews(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(typeof(MeshNodeEditorControl))
            .AddViews(registry => registry.WithView<MeshNodeEditorControl, MeshNodeEditorView>());
    }
}
