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
    /// Adds the Graph Blazor views (MeshNodeEditorView, MeshNodeThumbnailView, MeshNodePickerView, …)
    /// to the configuration. Also enables @ autocomplete for unified content references in markdown
    /// editors.
    /// </summary>
    public static MessageHubConfiguration AddGraphViews(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(typeof(MeshNodeEditorControl), typeof(MeshNodeThumbnailControl), typeof(MeshNodeCardControl),
                typeof(MeshNodeContentEditorControl), typeof(MeshNodeRoleEditorControl))
            .AddViews(registry => registry
                .WithView<MeshNodeEditorControl, MeshNodeEditorView>()
                .WithView<MeshNodeThumbnailControl, MeshNodeThumbnailView>()
                .WithView<MeshNodeCardControl, MeshNodeCardView>()
                .WithView<MeshNodeContentEditorControl, MeshNodeContentEditorView>()
                .WithView<MeshNodeRoleEditorControl, MeshNodeRoleEditorView>()
                // The picker lives HERE (it is a Graph surface) but derives from the EntityViews
                // pack's FormComponentBase — it left the base pack with the form controls because
                // the base pack cannot reference the pack that references it.
                .WithView<MeshNodePickerControl, MeshNodePickerView>())
            .AddMeshNavigation();  // Enable @ autocomplete in markdown editors
    }
}
