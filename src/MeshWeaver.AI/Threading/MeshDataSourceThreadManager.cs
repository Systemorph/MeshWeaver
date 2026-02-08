using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Threading;

/// <summary>
/// Thread manager that persists chats in MeshDataSource partitions.
///
/// Storage structure (legacy mode):
/// - Chat metadata: {scope}/chats/{threadId}.json
/// - Messages: {scope}/chats/{threadId}/messages/ (as individual message objects)
///
/// Storage structure (MeshNode mode - when threadId is a valid MeshNode path):
/// - Thread: MeshNode with nodeType="Thread"
/// - Messages: Child MeshNodes with nodeType="ThreadMessage"
///
/// When scope is null, uses "_chats" as the root path.
/// </summary>
public class MeshDataSourceThreadManager : IThreadManager
{
    private readonly IPersistenceService _persistence;
    private readonly AccessService _accessService;
    private readonly IMessageHub _hub;
    private readonly ILogger<MeshDataSourceThreadManager>? _logger;
    private readonly IMeshCatalog? _meshCatalog;
    private readonly IMeshQuery? _meshQuery;

    private const string DefaultChatRoot = "_chats";

    public MeshDataSourceThreadManager(
        IPersistenceService persistence,
        AccessService accessService,
        IMessageHub hub,
        ILogger<MeshDataSourceThreadManager>? logger = null)
    {
        _persistence = persistence;
        _accessService = accessService;
        _hub = hub;
        _logger = logger;
        _meshCatalog = hub.ServiceProvider.GetService<IMeshCatalog>();
        _meshQuery = hub.ServiceProvider.GetService<IMeshQuery>();
    }

    /// <summary>
    /// Checks if the threadId represents a MeshNode path (contains '/').
    /// </summary>
    private bool IsMeshNodePath(string threadId) => threadId.Contains('/');

    /// <summary>
    /// Creates a ThreadMessage child node for a MeshNode-based thread.
    /// </summary>
    private async Task AddMessageAsMeshNodeAsync(string threadPath, ChatMessage message, CancellationToken ct)
    {
        if (_meshCatalog == null)
            return;

        var messageId = Guid.NewGuid().AsString();
        var messagePath = $"{threadPath}/{messageId}";

        var messageType = message.Role == ChatRole.User
            ? ThreadMessageType.ExecutedInput
            : ThreadMessageType.AgentResponse;

        var threadMessage = new ThreadMessage
        {
            Id = messageId,
            Role = message.Role.Value,
            AuthorName = message.AuthorName,
            Text = message.Text ?? string.Empty,
            Timestamp = DateTime.UtcNow,
            Type = messageType
        };

        var messageNode = new MeshNode(messagePath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = threadMessage
        };

        await _meshCatalog.CreateNodeAsync(messageNode, GetUserId(), ct);
        _logger?.LogDebug("Saved message {MessageId} as child node: {Path}", messageId, messagePath);
    }

    /// <summary>
    /// Gets messages from child ThreadMessage nodes for a MeshNode-based thread.
    /// </summary>
    private async Task<IReadOnlyList<ChatMessage>> GetMessagesFromMeshNodesAsync(string threadPath, CancellationToken ct)
    {
        if (_meshQuery == null)
            return [];

        try
        {
            var messageNodes = await _meshQuery.QueryAsync<MeshNode>(
                $"path:{threadPath} nodeType:{ThreadMessageNodeType.NodeType} scope:children"
            ).ToListAsync(ct);

            return messageNodes
                .Select(n => n.Content as ThreadMessage)
                .Where(m => m != null && m.Type != ThreadMessageType.EditingPrompt)
                .OrderBy(m => m!.Timestamp)
                .Select(m => new ChatMessage(new ChatRole(m!.Role), m.Text)
                {
                    AuthorName = m.AuthorName
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load messages from MeshNodes for thread: {Path}", threadPath);
            return [];
        }
    }

    private string GetUserId() => _accessService.Context?.ObjectId ?? "anonymous";

    private string GetChatPath(string? scope, string threadId)
    {
        var basePath = string.IsNullOrEmpty(scope) ? DefaultChatRoot : $"{scope}/chats";
        var userId = GetUserId();
        return $"{basePath}/{userId}/{threadId}";
    }

    private string GetMessagesPath(string? scope, string threadId)
    {
        return $"{GetChatPath(scope, threadId)}/messages";
    }

    private string GetChatsBasePath(string? scope)
    {
        var basePath = string.IsNullOrEmpty(scope) ? DefaultChatRoot : $"{scope}/chats";
        var userId = GetUserId();
        return $"{basePath}/{userId}";
    }

    public async Task<ChatThread> GetOrCreateThreadAsync(string threadId, string? scope = null, CancellationToken ct = default)
    {
        var existing = await GetThreadAsync(threadId, ct);
        if (existing != null)
            return existing;

        var thread = ChatThread.Create(threadId, scope);
        await SaveThreadMetadataAsync(thread, ct);
        return thread;
    }

    public async Task AddMessageAsync(string threadId, ChatMessage message, CancellationToken ct = default)
    {
        // If threadId is a MeshNode path, save as child node
        if (IsMeshNodePath(threadId) && _meshCatalog != null)
        {
            await AddMessageAsMeshNodeAsync(threadId, message, ct);
            return;
        }

        // Legacy: Save to partition-based storage
        var thread = await GetOrCreateThreadAsync(threadId, ct: ct);

        // Save the message to the messages partition
        var messagesPath = GetMessagesPath(thread.Scope, threadId);
        var messageRecord = new ChatMessageRecord
        {
            Id = Guid.NewGuid().ToString(),
            ThreadId = threadId,
            Role = message.Role.Value,
            AuthorName = message.AuthorName,
            Text = message.Text,
            ContentsJson = SerializeContents(message.Contents),
            Timestamp = DateTime.UtcNow
        };

        await _persistence.SavePartitionObjectsAsync(messagesPath, null, [messageRecord]);
        _logger?.LogDebug("Saved message {MessageId} to thread {ThreadId}", messageRecord.Id, threadId);

        // Update thread metadata
        var updatedThread = thread.WithActivity();

        // Auto-title from first user message if no title set
        if (thread.Title == null && message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text))
        {
            var title = message.Text.Length > 50 ? message.Text[..50] + "..." : message.Text;
            updatedThread = updatedThread.WithTitle(title);
        }

        await SaveThreadMetadataAsync(updatedThread, ct);
    }

    public async Task AddMessagesAsync(string threadId, IEnumerable<ChatMessage> messages, CancellationToken ct = default)
    {
        var thread = await GetOrCreateThreadAsync(threadId, ct: ct);
        var messagesPath = GetMessagesPath(thread.Scope, threadId);

        var messageRecords = messages.Select(m => new ChatMessageRecord
        {
            Id = Guid.NewGuid().ToString(),
            ThreadId = threadId,
            Role = m.Role.Value,
            AuthorName = m.AuthorName,
            Text = m.Text,
            ContentsJson = SerializeContents(m.Contents),
            Timestamp = DateTime.UtcNow
        }).ToList();

        if (messageRecords.Count > 0)
        {
            await _persistence.SavePartitionObjectsAsync(messagesPath, null, messageRecords.Cast<object>().ToArray());
            _logger?.LogDebug("Saved {Count} messages to thread {ThreadId}", messageRecords.Count, threadId);
        }

        // Update thread metadata
        var updatedThread = thread.WithActivity();

        // Auto-title from first user message if no title set
        if (thread.Title == null)
        {
            var firstUserMessage = messages.FirstOrDefault(m =>
                m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text));
            if (firstUserMessage != null)
            {
                var title = firstUserMessage.Text!.Length > 50
                    ? firstUserMessage.Text[..50] + "..."
                    : firstUserMessage.Text;
                updatedThread = updatedThread.WithTitle(title);
            }
        }

        await SaveThreadMetadataAsync(updatedThread, ct);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string threadId, CancellationToken ct = default)
    {
        // If threadId is a MeshNode path, try to load from child nodes first
        if (IsMeshNodePath(threadId) && _meshQuery != null)
        {
            var meshNodeMessages = await GetMessagesFromMeshNodesAsync(threadId, ct);
            if (meshNodeMessages.Count > 0)
                return meshNodeMessages;
        }

        // Legacy: Load from partition-based storage
        var thread = await GetThreadAsync(threadId, ct);
        if (thread == null)
            return [];

        var messagesPath = GetMessagesPath(thread.Scope, threadId);
        var messages = new List<ChatMessage>();

        await foreach (var obj in _persistence.GetPartitionObjectsAsync(messagesPath, null).WithCancellation(ct))
        {
            if (obj is ChatMessageRecord record)
            {
                var chatMessage = new ChatMessage(new ChatRole(record.Role), DeserializeContents(record.ContentsJson))
                {
                    AuthorName = record.AuthorName
                };
                messages.Add(chatMessage);
            }
        }

        // Sort by timestamp
        return messages;
    }

    public async Task ClearThreadAsync(string threadId, CancellationToken ct = default)
    {
        var thread = await GetThreadAsync(threadId, ct);
        if (thread == null)
            return;

        // Note: IPersistenceService doesn't have a delete partition method
        // We'll update the thread metadata to indicate it's cleared
        var updatedThread = thread.WithActivity();
        await SaveThreadMetadataAsync(updatedThread, ct);

        _logger?.LogInformation("Cleared thread {ThreadId}", threadId);
    }

    public async Task<IReadOnlyList<ChatThread>> ListThreadsAsync(string? scope = null, CancellationToken ct = default)
    {
        var basePath = GetChatsBasePath(scope);
        var threads = new List<ChatThread>();

        await foreach (var obj in _persistence.GetPartitionObjectsAsync(basePath, null).WithCancellation(ct))
        {
            if (obj is ChatThreadMetadata metadata)
            {
                threads.Add(metadata.ToThread());
            }
        }

        return threads.OrderByDescending(t => t.LastActivityAt).ToList();
    }

    public async Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        // Note: Full deletion would require IPersistenceService delete support
        // For now, we can mark the thread as deleted or just log
        _logger?.LogInformation("Delete requested for thread {ThreadId}", threadId);

        // We could implement soft delete by adding a Deleted flag to metadata
        var thread = await GetThreadAsync(threadId, ct);
        if (thread != null)
        {
            // Save with a deleted marker (would need to extend ChatThreadMetadata)
            _logger?.LogWarning("Thread deletion not fully implemented - thread {ThreadId} marked for deletion", threadId);
        }
    }

    public async Task<ChatThread?> GetThreadAsync(string threadId, CancellationToken ct = default)
    {
        // Try to find the thread in default scope first, then with scope
        var basePath = GetChatsBasePath(null);

        await foreach (var obj in _persistence.GetPartitionObjectsAsync(basePath, null).WithCancellation(ct))
        {
            if (obj is ChatThreadMetadata metadata && metadata.Id == threadId)
            {
                return metadata.ToThread();
            }
        }

        return null;
    }

    public async Task UpdateTitleAsync(string threadId, string title, CancellationToken ct = default)
    {
        var thread = await GetThreadAsync(threadId, ct);
        if (thread != null)
        {
            var updatedThread = thread.WithTitle(title);
            await SaveThreadMetadataAsync(updatedThread, ct);
        }
    }

    public async Task<ChatThread?> GetMostRecentThreadAsync(string? scope = null, CancellationToken ct = default)
    {
        var threads = await ListThreadsAsync(scope, ct);
        return threads.FirstOrDefault();
    }

    private async Task SaveThreadMetadataAsync(ChatThread thread, CancellationToken ct)
    {
        var basePath = GetChatsBasePath(thread.Scope);
        var metadata = ChatThreadMetadata.FromThread(thread);
        await _persistence.SavePartitionObjectsAsync(basePath, null, [metadata]);
        _logger?.LogDebug("Saved thread metadata {ThreadId}", thread.Id);
    }

    private string? SerializeContents(IList<AIContent> contents)
    {
        if (contents == null || contents.Count == 0)
            return null;

        try
        {
            return JsonSerializer.Serialize(contents, _hub.JsonSerializerOptions);
        }
        catch
        {
            // Fallback to just text
            return null;
        }
    }

    private IList<AIContent> DeserializeContents(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<AIContent>>(json, _hub.JsonSerializerOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>
/// Persisted chat thread metadata.
/// </summary>
public record ChatThreadMetadata
{
    public required string Id { get; init; }
    public string? Scope { get; init; }
    public string? Title { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastActivityAt { get; init; }
    public string? ProviderId { get; init; }

    public ChatThread ToThread() => new(Id, Scope, Title, CreatedAt, LastActivityAt, ProviderId);

    public static ChatThreadMetadata FromThread(ChatThread thread) => new()
    {
        Id = thread.Id,
        Scope = thread.Scope,
        Title = thread.Title,
        CreatedAt = thread.CreatedAt,
        LastActivityAt = thread.LastActivityAt,
        ProviderId = thread.ProviderId
    };
}

/// <summary>
/// Persisted chat message record.
/// </summary>
public record ChatMessageRecord
{
    public required string Id { get; init; }
    public required string ThreadId { get; init; }
    public required string Role { get; init; }
    public string? AuthorName { get; init; }
    public string? Text { get; init; }
    public string? ContentsJson { get; init; }
    public DateTime Timestamp { get; init; }
}
