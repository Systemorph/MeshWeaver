using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Threading;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Layout;

/// <summary>
/// Layout area for displaying node-scoped chats.
/// Registers the Chats view that displays chat history for a node.
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
    /// Displays chats stored in the node's Chat partition.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Chats(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        if (meshQuery == null)
        {
            return Observable.Return<UiControl?>(
                Controls.Markdown("**Query service not available.**\n\nPlease configure IMeshQuery."));
        }

        // Query chats from the node's Chat partition using ObserveQuery
        var chatPath = ChatPersistenceHelper.GetNodeChatPartition(nodePath);
        var query = MeshQueryRequest.FromQuery($"path:{chatPath}");

        return meshQuery.ObserveQuery<Chat>(query)
            .Select(change => change.Items.OrderByDescending(c => c.LastActivityAt).ToList())
            .Select(chats => BuildChatsView(nodePath, chats));
    }

    private static UiControl BuildChatsView(string nodePath, IReadOnlyList<Chat> chats)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Header
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;")
            .WithView(Controls.Html("<h2 style=\"margin: 0;\">Chats</h2>"));

        stack = stack.WithView(headerStack);

        if (chats.Count == 0)
        {
            stack = stack.WithView(Controls.Markdown(
                "*No chats yet.*\n\nChats created from this node's context will appear here."));
            return stack;
        }

        // Chat list as cards
        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

        foreach (var chat in chats)
        {
            var card = BuildChatCard(chat);
            grid = grid.WithView(card, itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
        }

        stack = stack.WithView(grid);
        return stack;
    }

    private static UiControl BuildChatCard(Chat chat)
    {
        var cardContent = Controls.Stack
            .WithStyle("padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; background-color: var(--neutral-layer-1); cursor: pointer; transition: all 0.2s ease;");

        // Title
        cardContent = cardContent.WithView(Controls.Html(
            $"<div style=\"font-weight: 600; margin-bottom: 8px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;\">{chat.DisplayTitle}</div>"));

        // Metadata
        var meta = $"{chat.LastActivityAt:MM/dd/yyyy HH:mm}";
        if (!string.IsNullOrEmpty(chat.ProviderId))
        {
            meta += $" | {chat.ProviderId}";
        }
        cardContent = cardContent.WithView(Controls.Html(
            $"<div style=\"font-size: 0.875rem; color: var(--neutral-foreground-hint);\">{meta}</div>"));

        return cardContent;
    }
}
