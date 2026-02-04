using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Persistence;

/// <summary>
/// Static helper for thread CRUD operations using message-based data access.
/// Uses Post + RegisterCallback for non-blocking async communication with thread node hubs.
/// </summary>
public static class ThreadNodePersistenceHelper
{
    /// <summary>
    /// Creates a new Thread MeshNode with the given content at a context-aware path.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="basePath">The base path for storage (e.g., "User/{userId}/Threads" or "ACME/ProductLaunch/Threads").</param>
    /// <param name="name">The display name for the thread.</param>
    /// <param name="content">The thread content to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path of the created thread node.</returns>
    public static async Task<string> CreateThreadNodeAsync(
        IMessageHub hub,
        string basePath,
        string name,
        MeshThread content,
        CancellationToken ct = default)
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{basePath}/{threadId}";

        var node = new MeshNode(threadPath)
        {
            Name = name,
            NodeType = ThreadNodeType.NodeType,
            Content = content
        };

        var catalog = hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        await catalog.CreateNodeAsync(node, null, ct);

        return threadPath;
    }

    /// <summary>
    /// Updates an existing Thread MeshNode's content.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="threadPath">The full path to the thread node.</param>
    /// <param name="content">The updated thread content.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task UpdateThreadNodeAsync(
        IMessageHub hub,
        string threadPath,
        MeshThread content,
        CancellationToken ct = default)
    {
        var address = new Address(threadPath);

        // First get the existing node
        var getDelivery = hub.Post(
            new GetDataRequest(new EntityReference(nameof(MeshNode), threadPath)),
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
            ? existingNode with { Content = content }
            : new MeshNode(threadPath)
            {
                NodeType = ThreadNodeType.NodeType,
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
    /// Loads a Thread MeshNode by path.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="threadPath">The full path to the thread node.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The thread node content if found, null otherwise.</returns>
    public static async Task<MeshThread?> LoadThreadNodeAsync(
        IMessageHub hub,
        string threadPath,
        CancellationToken ct = default)
    {
        var address = new Address(threadPath);

        var delivery = hub.Post(
            new GetDataRequest(new EntityReference(nameof(MeshNode), threadPath)),
            o => o.WithTarget(address));

        MeshThread? result = null;
        if (delivery != null)
        {
            await hub.RegisterCallback(delivery, d =>
            {
                if (d.Message is { Data: MeshNode node })
                {
                    result = node.Content as MeshThread;
                }
                return d;
            }, ct);
        }

        return result;
    }

    /// <summary>
    /// Lists all Thread MeshNodes for a user.
    /// </summary>
    /// <param name="meshQuery">The mesh query service.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of thread nodes.</returns>
    public static async Task<IReadOnlyList<MeshNode>> ListUserThreadNodesAsync(
        IMeshQuery meshQuery,
        string userId,
        CancellationToken ct = default)
    {
        var userThreadsPath = ThreadNodeType.GetUserThreadsPath(userId);
        return await ListThreadsFromPathAsync(meshQuery, userThreadsPath, ct);
    }

    /// <summary>
    /// Lists Thread MeshNodes from a specific base path.
    /// </summary>
    /// <param name="meshQuery">The mesh query service.</param>
    /// <param name="basePath">The base path to query threads from (e.g., "User/{userId}/Threads" or "ACME/ProductLaunch/Threads").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of thread nodes.</returns>
    public static async Task<IReadOnlyList<MeshNode>> ListThreadsFromPathAsync(
        IMeshQuery meshQuery,
        string basePath,
        CancellationToken ct = default)
    {
        var query = $"path:{basePath} nodeType:{ThreadNodeType.NodeType} scope:children";

        var nodes = new List<MeshNode>();
        await foreach (var node in meshQuery.QueryAsync<MeshNode>(query).WithCancellation(ct))
        {
            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Lists Thread MeshNodes from multiple paths (e.g., user threads + context threads).
    /// Results are deduplicated by path and ordered by LastModified descending.
    /// </summary>
    /// <param name="meshQuery">The mesh query service.</param>
    /// <param name="paths">The base paths to query threads from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of thread nodes ordered by LastModified descending.</returns>
    public static async Task<IReadOnlyList<MeshNode>> ListThreadsFromPathsAsync(
        IMeshQuery meshQuery,
        IEnumerable<string> paths,
        CancellationToken ct = default)
    {
        var allNodes = new Dictionary<string, MeshNode>();

        foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p)))
        {
            var nodes = await ListThreadsFromPathAsync(meshQuery, path, ct);
            foreach (var node in nodes)
            {
                var nodePath = node.Path ?? node.Id;
                // Deduplicate by path (in case same thread appears in multiple queries)
                if (!allNodes.ContainsKey(nodePath))
                {
                    allNodes[nodePath] = node;
                }
            }
        }

        // Order by LastModified descending
        return allNodes.Values
            .OrderByDescending(n => n.LastModified)
            .ToList();
    }

    /// <summary>
    /// Lists delegation sub-threads for a parent thread.
    /// </summary>
    /// <param name="meshQuery">The mesh query service.</param>
    /// <param name="parentThreadPath">The parent thread path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of delegation thread nodes.</returns>
    public static async Task<IReadOnlyList<MeshNode>> ListDelegationsAsync(
        IMeshQuery meshQuery,
        string parentThreadPath,
        CancellationToken ct = default)
    {
        var query = $"path:{parentThreadPath} nodeType:{ThreadNodeType.NodeType} scope:children";

        var nodes = new List<MeshNode>();
        await foreach (var node in meshQuery.QueryAsync<MeshNode>(query).WithCancellation(ct))
        {
            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Creates a delegation sub-thread under a parent thread.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="parentThreadPath">The parent thread path.</param>
    /// <param name="targetAgentName">The name of the delegated agent.</param>
    /// <param name="delegationMessage">The delegation message.</param>
    /// <param name="userId">The user ID creating the delegation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path of the created delegation node.</returns>
    public static async Task<string> CreateDelegationNodeAsync(
        IMessageHub hub,
        string parentThreadPath,
        string targetAgentName,
        string delegationMessage,
        string? userId = null,
        CancellationToken ct = default)
    {
        var delegationId = Guid.NewGuid().AsString();
        var delegationPath = $"{parentThreadPath}/{targetAgentName}-{delegationId}";

        var content = new MeshThread
        {
            Messages = []
        };

        var node = new MeshNode(delegationPath)
        {
            Name = $"Delegation to {targetAgentName}",
            NodeType = ThreadNodeType.NodeType,
            Content = content
        };

        var catalog = hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        await catalog.CreateNodeAsync(node, userId, ct);

        return delegationPath;
    }

    /// <summary>
    /// Deletes a Thread MeshNode.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="threadPath">The full path to the thread node.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task DeleteThreadNodeAsync(
        IMessageHub hub,
        string threadPath,
        CancellationToken ct = default)
    {
        var address = new Address(threadPath);

        var delivery = hub.Post(
            new DataChangeRequest { Deletions = [threadPath] },
            o => o.WithTarget(address));

        if (delivery != null)
            await hub.RegisterCallback(delivery, d => d, ct);
    }

    /// <summary>
    /// Adds a message to a thread node.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <param name="threadPath">The full path to the thread node.</param>
    /// <param name="message">The message to add.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task AddMessageAsync(
        IMessageHub hub,
        string threadPath,
        ThreadMessage message,
        CancellationToken ct = default)
    {
        var existingContent = await LoadThreadNodeAsync(hub, threadPath, ct);

        var messages = existingContent?.Messages?.ToList() ?? [];
        messages.Add(message);

        var updatedContent = (existingContent ?? new MeshThread()) with
        {
            Messages = messages
        };

        await UpdateThreadNodeAsync(hub, threadPath, updatedContent, ct);
    }

    /// <summary>
    /// Converts Thread messages to Microsoft.Extensions.AI.ChatMessage format.
    /// </summary>
    /// <param name="content">The thread node content.</param>
    /// <returns>List of ChatMessage objects.</returns>
    public static List<Microsoft.Extensions.AI.ChatMessage> ConvertToAgentChatMessages(
        MeshThread? content)
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
    /// Converts Microsoft.Extensions.AI.ChatMessage to ThreadMessage format.
    /// </summary>
    /// <param name="messages">The chat messages.</param>
    /// <returns>List of ThreadMessage objects.</returns>
    public static List<ThreadMessage> ConvertFromAgentChatMessages(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        var result = new List<ThreadMessage>();
        foreach (var msg in messages)
        {
            var threadContent = new ThreadMessage
            {
                Id = Guid.NewGuid().AsString(),
                Role = msg.Role.Value,
                AuthorName = msg.AuthorName,
                Text = msg.Text ?? string.Empty,
                Timestamp = DateTime.UtcNow
            };
            result.Add(threadContent);
        }

        return result;
    }
}
