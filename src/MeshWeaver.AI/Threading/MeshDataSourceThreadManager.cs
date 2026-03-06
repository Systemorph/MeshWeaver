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
/// Thread manager that persists chats as MeshNode hierarchies.
///
/// Storage structure:
/// - Thread: MeshNode with nodeType="Thread"
/// - Messages: Child MeshNodes with nodeType="ThreadMessage"
/// </summary>
public class MeshDataSourceThreadManager : IThreadManager
{
    private readonly AccessService _accessService;
    private readonly IMessageHub _hub;
    private readonly ILogger<MeshDataSourceThreadManager>? _logger;
    private readonly IMeshNodePersistence _nodeFactory;
    private readonly IMeshQuery _meshQuery;

    internal MeshDataSourceThreadManager(
        AccessService accessService,
        IMessageHub hub,
        ILogger<MeshDataSourceThreadManager>? logger = null)
    {
        _accessService = accessService;
        _hub = hub;
        _logger = logger;
        _nodeFactory = hub.ServiceProvider.GetRequiredService<IMeshNodePersistence>();
        _meshQuery = hub.ServiceProvider.GetRequiredService<IMeshQuery>();
    }

    private string GetUserId() => _accessService.Context?.ObjectId ?? "anonymous";

    public async Task<ChatThread> GetOrCreateThreadAsync(string threadId, string? scope = null, CancellationToken ct = default)
    {
        var existing = await GetThreadAsync(threadId, ct);
        if (existing != null)
            return existing;

        var thread = ChatThread.Create(threadId, scope);

        var threadNode = new MeshNode(threadId)
        {
            NodeType = "Thread",
            Name = thread.Title ?? threadId,
            Content = new ChatThreadMetadata
            {
                Id = thread.Id,
                Scope = thread.Scope,
                Title = thread.Title,
                CreatedAt = thread.CreatedAt,
                LastActivityAt = thread.LastActivityAt,
                ProviderId = thread.ProviderId
            }
        };
        await _nodeFactory.CreateNodeAsync(threadNode, GetUserId(), ct);

        return thread;
    }

    public async Task AddMessageAsync(string threadId, ChatMessage message, CancellationToken ct = default)
    {
        var messageId = Guid.NewGuid().AsString();
        var messagePath = $"{threadId}/{messageId}";

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

        await _nodeFactory.CreateNodeAsync(messageNode, GetUserId(), ct);
        _logger?.LogDebug("Saved message {MessageId} as child node: {Path}", messageId, messagePath);

        // Auto-title from first user message
        if (message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text))
        {
            var thread = await GetThreadAsync(threadId, ct);
            if (thread?.Title == null)
            {
                var title = message.Text.Length > 50 ? message.Text[..50] + "..." : message.Text;
                await UpdateTitleAsync(threadId, title, ct);
            }
        }
    }

    public async Task AddMessagesAsync(string threadId, IEnumerable<ChatMessage> messages, CancellationToken ct = default)
    {
        foreach (var message in messages)
            await AddMessageAsync(threadId, message, ct);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string threadId, CancellationToken ct = default)
    {
        try
        {
            var messageNodes = await _meshQuery.QueryAsync<MeshNode>(
                $"path:{threadId} nodeType:{ThreadMessageNodeType.NodeType} scope:children"
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
            _logger?.LogDebug(ex, "Failed to load messages for thread: {Path}", threadId);
            return [];
        }
    }

    public async Task ClearThreadAsync(string threadId, CancellationToken ct = default)
    {
        await _nodeFactory.DeleteNodeAsync(threadId, ct: ct);
        _logger?.LogInformation("Cleared thread {ThreadId}", threadId);
    }

    public async Task<IReadOnlyList<ChatThread>> ListThreadsAsync(string? scope = null, CancellationToken ct = default)
    {
        var queryString = "nodeType:Thread";
        if (!string.IsNullOrEmpty(scope))
            queryString += $" parent:{scope}";

        var threadNodes = await _meshQuery.QueryAsync<MeshNode>(queryString).ToListAsync(ct);

        return threadNodes
            .Select(n => n.Content as ChatThreadMetadata)
            .Where(m => m != null)
            .Select(m => m!.ToThread())
            .OrderByDescending(t => t.LastActivityAt)
            .ToList();
    }

    public async Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        await _nodeFactory.DeleteNodeAsync(threadId, ct: ct);
        _logger?.LogInformation("Deleted thread {ThreadId}", threadId);
    }

    public async Task<ChatThread?> GetThreadAsync(string threadId, CancellationToken ct = default)
    {
        try
        {
            var node = await _meshQuery.QueryAsync<MeshNode>($"path:{threadId} scope:exact")
                .FirstOrDefaultAsync(ct);

            if (node?.Content is ChatThreadMetadata metadata)
                return metadata.ToThread();

            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task UpdateTitleAsync(string threadId, string title, CancellationToken ct = default)
    {
        var node = await _meshQuery.QueryAsync<MeshNode>($"path:{threadId} scope:exact")
            .FirstOrDefaultAsync(ct);

        if (node != null)
        {
            var updated = node with { Name = title };
            if (updated.Content is ChatThreadMetadata meta)
                updated = updated with { Content = meta with { Title = title } };
            _hub.Post(new UpdateNodeRequest(updated));
        }
    }

    public async Task<ChatThread?> GetMostRecentThreadAsync(string? scope = null, CancellationToken ct = default)
    {
        var threads = await ListThreadsAsync(scope, ct);
        return threads.FirstOrDefault();
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
