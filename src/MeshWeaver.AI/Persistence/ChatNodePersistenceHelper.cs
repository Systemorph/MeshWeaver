using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.AI.Persistence;

/// <summary>
/// Static helper for chat CRUD operations using message-based data access.
/// Uses Post + RegisterCallback for non-blocking async communication with chat node hubs.
/// </summary>
public static class ChatNodePersistenceHelper
{
    /// <summary>
    /// Creates a new Chat MeshNode with the given content.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="content">The chat content to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path of the created chat node.</returns>
    public static async Task<string> CreateChatNodeAsync(
        IMessageHub hub,
        string userId,
        ChatNodeContent content,
        CancellationToken ct = default)
    {
        var chatId = Guid.NewGuid().AsString();
        var chatPath = $"{ChatNodeType.GetUserChatsPath(userId)}/{chatId}";

        var node = new MeshNode(chatPath)
        {
            Name = content.Title ?? content.DisplayTitle,
            NodeType = ChatNodeType.NodeType,
            Content = content
        };

        var address = new Address(chatPath);
        var nodeJson = JsonSerializer.SerializeToElement(node, hub.JsonSerializerOptions);

        var delivery = hub.Post(
            new DataChangeRequest { Updates = [nodeJson] },
            o => o.WithTarget(address));

        if (delivery != null)
            await hub.RegisterCallback(delivery, d => d, ct);

        return chatPath;
    }

    /// <summary>
    /// Updates an existing Chat MeshNode's content.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="chatPath">The full path to the chat node.</param>
    /// <param name="content">The updated chat content.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task UpdateChatNodeAsync(
        IMessageHub hub,
        string chatPath,
        ChatNodeContent content,
        CancellationToken ct = default)
    {
        var address = new Address(chatPath);

        // First get the existing node
        var getDelivery = hub.Post(
            new GetDataRequest(new EntityReference(nameof(MeshNode), chatPath)),
            o => o.WithTarget(address));

        MeshNode? existingNode = null;
        if (getDelivery != null)
        {
            await hub.RegisterCallback(getDelivery, d =>
            {
                if (d.Message is GetDataResponse response && response.Data is MeshNode node)
                {
                    existingNode = node;
                }
                return d;
            }, ct);
        }

        var updatedNode = existingNode != null
            ? existingNode with
            {
                Name = content.Title ?? content.DisplayTitle,
                Content = content
            }
            : new MeshNode(chatPath)
            {
                Name = content.Title ?? content.DisplayTitle,
                NodeType = ChatNodeType.NodeType,
                Content = content
            };

        var nodeJson = JsonSerializer.SerializeToElement(updatedNode, hub.JsonSerializerOptions);
        var updateDelivery = hub.Post(
            new DataChangeRequest { Updates = [nodeJson] },
            o => o.WithTarget(address));

        if (updateDelivery != null)
            await hub.RegisterCallback(updateDelivery, d => d, ct);
    }

    /// <summary>
    /// Loads a Chat MeshNode by path.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="chatPath">The full path to the chat node.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The chat node content if found, null otherwise.</returns>
    public static async Task<ChatNodeContent?> LoadChatNodeAsync(
        IMessageHub hub,
        string chatPath,
        CancellationToken ct = default)
    {
        var address = new Address(chatPath);

        var delivery = hub.Post(
            new GetDataRequest(new EntityReference(nameof(MeshNode), chatPath)),
            o => o.WithTarget(address));

        ChatNodeContent? result = null;
        if (delivery != null)
        {
            await hub.RegisterCallback(delivery, d =>
            {
                if (d.Message is GetDataResponse response && response.Data is MeshNode node)
                {
                    result = node.Content as ChatNodeContent;
                }
                return d;
            }, ct);
        }

        return result;
    }

    /// <summary>
    /// Lists all Chat MeshNodes for a user.
    /// </summary>
    /// <param name="meshQuery">The mesh query service.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of chat nodes.</returns>
    public static async Task<IReadOnlyList<MeshNode>> ListUserChatNodesAsync(
        IMeshQuery meshQuery,
        string userId,
        CancellationToken ct = default)
    {
        var userChatsPath = ChatNodeType.GetUserChatsPath(userId);
        var query = $"path:{userChatsPath} nodeType:{ChatNodeType.NodeType} scope:children";

        var nodes = new List<MeshNode>();
        await foreach (var node in meshQuery.QueryAsync<MeshNode>(query).WithCancellation(ct))
        {
            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Lists delegation sub-chats for a parent chat.
    /// </summary>
    /// <param name="meshQuery">The mesh query service.</param>
    /// <param name="parentChatPath">The parent chat path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of delegation chat nodes.</returns>
    public static async Task<IReadOnlyList<MeshNode>> ListDelegationsAsync(
        IMeshQuery meshQuery,
        string parentChatPath,
        CancellationToken ct = default)
    {
        var query = $"path:{parentChatPath} nodeType:{ChatNodeType.NodeType} scope:children";

        var nodes = new List<MeshNode>();
        await foreach (var node in meshQuery.QueryAsync<MeshNode>(query).WithCancellation(ct))
        {
            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Creates a delegation sub-chat under a parent chat.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="parentChatPath">The parent chat path.</param>
    /// <param name="targetAgentName">The name of the delegated agent.</param>
    /// <param name="delegationMessage">The delegation message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path of the created delegation node.</returns>
    public static async Task<string> CreateDelegationNodeAsync(
        IMessageHub hub,
        string parentChatPath,
        string targetAgentName,
        string delegationMessage,
        CancellationToken ct = default)
    {
        var delegationId = Guid.NewGuid().AsString();
        var delegationPath = $"{parentChatPath}/{targetAgentName}-{delegationId}";

        // Truncate title if message is too long
        var titlePreview = delegationMessage.Length > 50
            ? delegationMessage[..50] + "..."
            : delegationMessage;

        var content = new ChatNodeContent
        {
            Title = $"Delegation to {targetAgentName}: {titlePreview}",
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            Messages = []
        };

        var node = new MeshNode(delegationPath)
        {
            Name = $"Delegation to {targetAgentName}",
            NodeType = ChatNodeType.NodeType,
            Content = content
        };

        var address = new Address(delegationPath);
        var nodeJson = JsonSerializer.SerializeToElement(node, hub.JsonSerializerOptions);

        var delivery = hub.Post(
            new DataChangeRequest { Updates = [nodeJson] },
            o => o.WithTarget(address));

        if (delivery != null)
            await hub.RegisterCallback(delivery, d => d, ct);

        return delegationPath;
    }

    /// <summary>
    /// Deletes a Chat MeshNode.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="chatPath">The full path to the chat node.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task DeleteChatNodeAsync(
        IMessageHub hub,
        string chatPath,
        CancellationToken ct = default)
    {
        var address = new Address(chatPath);

        var delivery = hub.Post(
            new DataChangeRequest { Deletions = [chatPath] },
            o => o.WithTarget(address));

        if (delivery != null)
            await hub.RegisterCallback(delivery, d => d, ct);
    }

    /// <summary>
    /// Adds a message to a chat node.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="chatPath">The full path to the chat node.</param>
    /// <param name="message">The message to add.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task AddMessageAsync(
        IMessageHub hub,
        string chatPath,
        ChatMessageContent message,
        CancellationToken ct = default)
    {
        var existingContent = await LoadChatNodeAsync(hub, chatPath, ct);

        var messages = existingContent?.Messages?.ToList() ?? [];
        messages.Add(message);

        var updatedContent = (existingContent ?? new ChatNodeContent()) with
        {
            LastActivityAt = DateTime.UtcNow,
            Messages = messages
        };

        await UpdateChatNodeAsync(hub, chatPath, updatedContent, ct);
    }

    /// <summary>
    /// Converts ChatNodeContent messages to Microsoft.Extensions.AI.ChatMessage format.
    /// </summary>
    /// <param name="content">The chat node content.</param>
    /// <returns>List of ChatMessage objects.</returns>
    public static List<Microsoft.Extensions.AI.ChatMessage> ConvertToAgentChatMessages(
        ChatNodeContent? content)
    {
        if (content?.Messages == null || content.Messages.Count == 0)
            return [];

        var result = new List<Microsoft.Extensions.AI.ChatMessage>();
        foreach (var msg in content.Messages)
        {
            var role = new Microsoft.Extensions.AI.ChatRole(msg.Role);
            var chatMessage = new Microsoft.Extensions.AI.ChatMessage(role, msg.Text)
            {
                AuthorName = msg.AuthorName
            };
            result.Add(chatMessage);
        }

        return result;
    }

    /// <summary>
    /// Converts Microsoft.Extensions.AI.ChatMessage to ChatMessageContent format.
    /// </summary>
    /// <param name="messages">The chat messages.</param>
    /// <returns>List of ChatMessageContent objects.</returns>
    public static List<ChatMessageContent> ConvertFromAgentChatMessages(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        var result = new List<ChatMessageContent>();
        foreach (var msg in messages)
        {
            var chatContent = new ChatMessageContent
            {
                Id = Guid.NewGuid().AsString(),
                Role = msg.Role.Value,
                AuthorName = msg.AuthorName,
                Text = msg.Text ?? string.Empty,
                Timestamp = DateTime.UtcNow
            };
            result.Add(chatContent);
        }

        return result;
    }
}
