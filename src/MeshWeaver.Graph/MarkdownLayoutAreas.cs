using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Registers dedicated views for Markdown nodes.
/// The actual view logic is in:
/// - MarkdownOverviewLayoutArea (read-only with CollaborativeMarkdownControl)
/// - MarkdownEditLayoutArea (editor with auto-save)
/// - MarkdownNotebookLayoutArea (notebook cells)
/// </summary>
public static class MarkdownLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string EditArea = "Edit";
    public const string SuggestArea = "Suggest";
    public const string NotebookArea = "Notebook";

    public static MessageHubConfiguration AddMarkdownViews(this MessageHubConfiguration configuration)
        => configuration
            .Set(new PageLayoutOptions { MaxWidth = "1280px" })
            .AddNodeMenuItems(SuggestMenuProvider)
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, MarkdownOverviewLayoutArea.Overview)
                .WithView(EditArea, MarkdownEditLayoutArea.Edit)
                .WithView(SuggestArea, MarkdownEditLayoutArea.Suggest)
                .WithView(NotebookArea, MarkdownNotebookLayoutArea.Notebook)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, MarkdownOverviewLayoutArea.Thumbnail)
            .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    private static async IAsyncEnumerable<NodeMenuItemDefinition> SuggestMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var perms = await PermissionHelper.GetEffectivePermissionsAsync(
            host.Hub, host.Hub.Address.ToString());
        if (perms.HasFlag(Permission.Update))
            yield return new NodeMenuItemDefinition("Suggest", SuggestArea,
                RequiredPermission: Permission.Update, DisplayOrder: 11);
    }
}
