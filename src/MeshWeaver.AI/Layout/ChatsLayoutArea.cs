using System.ComponentModel;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Layout;

/// <summary>
/// Layout area for displaying node-scoped chats.
/// Registers the Chats view that displays chat history for a node.
/// Uses MeshSearchControl with reactive mode for automatic updates.
/// </summary>
public static class ChatsLayoutArea
{
    public const string ChatsArea = "Chats";

    /// <summary>
    /// Adds the Chats view to the layout.
    /// Call this after AddDefaultLayoutAreas() to register the Chats area.
    /// </summary>
    public static LayoutDefinition AddChatsLayoutArea(this LayoutDefinition layout)
        => layout.WithView(ChatsArea, Chats);

    /// <summary>
    /// Adds the Chats view to the hub's layout configuration.
    /// </summary>
    public static MessageHubConfiguration AddChatsLayoutArea(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.AddChatsLayoutArea());

    /// <summary>
    /// Renders the Chats area showing node-scoped chat history.
    /// Uses MeshSearchControl with reactive mode for live updates.
    /// Displays chats stored in the node's Chat partition.
    /// </summary>
    [Browsable(false)]
    public static UiControl Chats(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var chatPath = ChatPersistenceHelper.GetNodeChatPartition(nodePath);

        var stack = Controls.Stack.WithWidth("100%");

        // Header
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;")
            .WithView(Controls.Html("<h2 style=\"margin: 0;\">Chats</h2>"));

        stack = stack.WithView(headerStack);

        // Chat list using MeshSearchControl with reactive mode
        var searchControl = Controls.MeshSearch
            .WithHiddenQuery($"path:{chatPath}")
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
