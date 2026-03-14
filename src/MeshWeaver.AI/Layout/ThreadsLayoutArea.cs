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
    /// Uses MeshSearchControl for thread discovery sorted by last activity.
    /// </summary>
    [Browsable(false)]
    public static UiControl Threads(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();

        return Controls.MeshSearch
            .WithHiddenQuery($"nodeType:Thread namespace:{nodePath}/{ThreadNodeType.ThreadPartition}")
            .WithNamespace(nodePath)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCreateNodeType("Thread");
    }
}
