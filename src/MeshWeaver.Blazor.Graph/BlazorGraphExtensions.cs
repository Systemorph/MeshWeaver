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
    /// Adds the Graph Blazor views — the full MeshNode surface set (editor, thumbnail, card,
    /// collection, content/role editors, picker) — to the configuration. Also enables
    /// @ autocomplete for unified content references in markdown editors. Every view registered
    /// here now LIVES in this assembly: before the Group B extraction, four of these views were
    /// registered from MeshWeaver.Blazor.Components (a foreign assembly) and MeshNodeCardControl
    /// was double-registered, once here and once in the base registry — one registration, one
    /// home, gated by ViewPackRegistrationGateTest.
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
                .WithView<MeshNodeCollectionControl, MeshNodeCollectionView>()
                .WithView<MeshNodeContentEditorControl, MeshNodeContentEditorView>()
                .WithView<MeshNodeRoleEditorControl, MeshNodeRoleEditorView>()
                // The picker lives HERE (it is a Graph surface) but derives from the EntityViews
                // pack's FormComponentBase — it left the base pack with the form controls because
                // the base pack cannot reference the pack that references it.
                .WithView<MeshNodePickerControl, MeshNodePickerView>())
            .AddMeshNavigation();  // Enable @ autocomplete in markdown editors
    }
}
