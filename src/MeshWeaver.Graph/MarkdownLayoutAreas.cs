using MeshWeaver.Layout;
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
    public const string NotebookArea = "Notebook";

    public static MessageHubConfiguration AddMarkdownViews(this MessageHubConfiguration configuration)
        => configuration
            .Set(new PageLayoutOptions { MaxWidth = "960px" })
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, MarkdownOverviewLayoutArea.Overview)
                .WithView(EditArea, MarkdownEditLayoutArea.Edit)
                .WithView(NotebookArea, MarkdownNotebookLayoutArea.Notebook)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, MarkdownOverviewLayoutArea.Thumbnail)
            .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));
}
