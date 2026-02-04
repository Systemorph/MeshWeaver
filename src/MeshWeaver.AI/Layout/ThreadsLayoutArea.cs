using System.ComponentModel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Layout;

/// <summary>
/// Layout area for displaying node-scoped threads.
/// Registers the Threads view that displays thread history for a node.
/// Uses MeshSearchControl with reactive mode for automatic updates.
/// </summary>
public static class ThreadsLayoutArea
{
    public const string ThreadsArea = "Threads";

    /// <summary>
    /// Adds the Threads view to the layout.
    /// Call this after AddDefaultLayoutAreas() to register the Threads area.
    /// </summary>
    public static LayoutDefinition AddThreadsLayoutArea(this LayoutDefinition layout)
        => layout.WithView(ThreadsArea, Threads);

    /// <summary>
    /// Adds the Threads view to the hub's layout configuration.
    /// </summary>
    public static MessageHubConfiguration AddThreadsLayoutArea(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.AddThreadsLayoutArea());

    /// <summary>
    /// Renders the Threads area showing node-scoped thread history.
    /// Uses MeshSearchControl with reactive mode for live updates.
    /// Displays threads stored under the node's Threads path.
    /// </summary>
    [Browsable(false)]
    public static UiControl Threads(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var threadsPath = ThreadNodeType.GetContextThreadsPath(nodePath);

        var stack = Controls.Stack.WithWidth("100%");

        // Header
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;")
            .WithView(Controls.Html("<h2 style=\"margin: 0;\">Threads</h2>"));

        stack = stack.WithView(headerStack);

        // Thread list using MeshSearchControl with reactive mode
        var searchControl = Controls.MeshSearch
            .WithHiddenQuery($"path:{threadsPath} nodeType:{ThreadNodeType.NodeType}")
            .WithShowSearchBox(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithSortBy("LastActivityAt", ascending: false)
            .WithReactiveMode(true)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false);

        stack = stack.WithView(searchControl);
        return stack;
    }
}
