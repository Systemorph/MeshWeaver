using System.Text;
using System.Text.Json;
using MeshWeaver.AI.Streaming;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Per-hub persistent state for Thread nodes.
/// Owns the AgentChatClient, StreamingChatSession, and cancellation management.
/// Registered as a singleton in the Thread hub's DI container.
/// </summary>
public class ThreadSession : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ThreadSession> _logger;

    private AgentChatClient? _chatClient;
    private StreamingChatSession? _streamingSession;
    private CancellationTokenSource? _activeCts;
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    private string? _initializedContextPath;
    private string? _initializedModelName;
    private string? _initializedAgentName;
    private bool _isDisposed;

    public ThreadSession(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<ThreadSession>>();
    }

    /// <summary>
    /// Lazily initializes the AgentChatClient for the given context/model/agent.
    /// Reuses existing client if already initialized with the same parameters.
    /// </summary>
    public async Task EnsureInitializedAsync(string threadPath, string? contextPath, string? modelName, string? agentName)
    {
        if (_chatClient != null
            && _initializedContextPath == contextPath
            && _initializedModelName == modelName)
        {
            // Already initialized — just update agent selection if changed
            if (!string.IsNullOrEmpty(agentName) && agentName != _initializedAgentName)
            {
                _chatClient.SetSelectedAgent(agentName);
                _initializedAgentName = agentName;
            }
            return;
        }

        _chatClient = new AgentChatClient(_serviceProvider);
        _chatClient.SetThreadId(threadPath);
        await _chatClient.InitializeAsync(contextPath, modelName);

        if (!string.IsNullOrEmpty(agentName))
            _chatClient.SetSelectedAgent(agentName);

        // Load persistent thread ID from thread content if present
        var meshQuery = _serviceProvider.GetService<IMeshService>();
        if (meshQuery != null)
        {
            MeshNode? threadNode = null;
            await foreach (var n in meshQuery.QueryAsync<MeshNode>($"path:{threadPath}"))
            {
                threadNode = n;
                break;
            }
            if (threadNode?.Content is Thread threadContent
                && !string.IsNullOrEmpty(threadContent.PersistentThreadId))
            {
                _chatClient.SetPersistentThreadId(threadContent.PersistentThreadId);
            }

            // Load existing ThreadMessage children for conversation history resume
            var messageNodes = await meshQuery.QueryAsync<MeshNode>(
                $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType} sort:Order-asc")
                .ToListAsync();
            var history = messageNodes
                .Select(n => n.Content)
                .OfType<ThreadMessage>()
                .Where(m => m.Type != ThreadMessageType.EditingPrompt && !string.IsNullOrEmpty(m.Text))
                .ToList();
            if (history.Count > 0)
            {
                _logger.LogInformation("Resuming thread {ThreadPath} with {Count} history messages", threadPath, history.Count);
                _chatClient.SetConversationHistory(history);
            }
        }

        _streamingSession = new StreamingChatSession(_chatClient,
            _serviceProvider.GetService<ILogger<StreamingChatSession>>());

        _initializedContextPath = contextPath;
        _initializedModelName = modelName;
        _initializedAgentName = agentName;
    }

    /// <summary>
    /// Submits a message to the thread. Cancels any active stream first.
    /// Creates user + response nodes, then streams the agent response.
    /// </summary>
    public async Task SubmitMessageAsync(
        SubmitMessageRequest request,
        IMessageHub hub,
        IMessageDelivery<SubmitMessageRequest> delivery,
        CancellationToken ct)
    {
        await _queueLock.WaitAsync(ct);
        try
        {
            // Cancel any active stream
            CancelCurrentStreamInternal();

            // Initialize agent if needed
            await EnsureInitializedAsync(request.ThreadPath, request.ContextPath, request.ModelName, request.AgentName);

            if (request.Attachments is { Count: > 0 })
                _chatClient!.SetAttachments(request.Attachments);

            // Compute next message number from existing children
            var nextNumber = await ComputeNextMessageNumberAsync(hub, request.ThreadPath);

            // Create user message node
            var userMessage = new ThreadMessage
            {
                Id = nextNumber.ToString(),
                Role = "user",
                Text = request.UserMessageText,
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            };
            var userNodePath = await CreateMessageNodeAsync(hub, request.ThreadPath, nextNumber, userMessage);

            // Create empty response node
            var responseNumber = nextNumber + 1;
            var responseMessage = new ThreadMessage
            {
                Id = responseNumber.ToString(),
                Role = "assistant",
                Text = "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            };
            var responsePath = await CreateMessageNodeAsync(hub, request.ThreadPath, responseNumber, responseMessage);

            // Note: Thread.ThreadMessages is updated by HandleSubmitMessage in the execution block.
            // Start streaming on background task
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var streamCts = _activeCts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await StreamResponseAsync(hub, delivery, request, responsePath, streamCts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error streaming response for thread {ThreadPath}", request.ThreadPath);
                    hub.Post(new SubmitMessageResponse { Success = false, Error = ex.Message },
                        o => o.ResponseFor(delivery));
                }
            }, CancellationToken.None);
        }
        finally
        {
            _queueLock.Release();
        }
    }

    /// <summary>
    /// Cancels the currently active streaming response.
    /// </summary>
    /// <summary>
    /// Streams the agent response for a StartStreamingRequest.
    /// Called from the _Exec sub-hub handler — runs on a background thread.
    /// Only posts updates back to the thread hub via hub.Post (no AwaitResponse).
    /// </summary>
    public async Task StreamAsync(StartStreamingRequest request, IMessageHub hub)
    {
        await _queueLock.WaitAsync();
        try
        {
            CancelCurrentStreamInternal();

            // Initialize agent if needed
            await EnsureInitializedAsync(request.ThreadPath, null, null, null);

            _activeCts = new CancellationTokenSource();
            var ct = _activeCts.Token;

            var chatMessage = new ChatMessage(ChatRole.User, request.UserMessageText);
            var responseText = new StringBuilder();
            var lastUpdate = DateTimeOffset.MinValue;

            try
            {
                await foreach (var update in _chatClient!.GetStreamingResponseAsync([chatMessage], ct))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        responseText.Append(update.Text);

                        if (DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(200))
                        {
                            UpdateResponseNode(hub, request.ResponsePath, responseText.ToString());
                            lastUpdate = DateTimeOffset.UtcNow;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Stream cancelled for thread {ThreadPath}", request.ThreadPath);
            }

            // Final update with complete text
            UpdateResponseNode(hub, request.ResponsePath, responseText.ToString());
            await TouchThreadLastModifiedAsync(hub, request.ThreadPath);
        }
        finally
        {
            _queueLock.Release();
        }
    }

    /// <summary>
    /// Exposes the agent's streaming response. Must call EnsureInitializedAsync first.
    /// </summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IReadOnlyCollection<ChatMessage> messages, CancellationToken ct)
        => _chatClient!.GetStreamingResponseAsync(messages, ct);

    public void CancelCurrentStream()
    {
        CancelCurrentStreamInternal();
    }

    private void CancelCurrentStreamInternal()
    {
        if (_activeCts is { IsCancellationRequested: false })
        {
            _logger.LogInformation("Cancelling active stream");
            _activeCts.Cancel();
        }
        _streamingSession?.CancelCurrentStream();
    }

    private async Task StreamResponseAsync(
        IMessageHub hub,
        IMessageDelivery<SubmitMessageRequest> delivery,
        SubmitMessageRequest request,
        string responsePath,
        CancellationToken ct)
    {
        var chatMessage = new ChatMessage(ChatRole.User, request.UserMessageText);
        var responseText = new StringBuilder();
        var lastUpdate = DateTimeOffset.MinValue;

        try
        {
            await foreach (var update in _chatClient!.GetStreamingResponseAsync([chatMessage], ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    responseText.Append(update.Text);

                    if (DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(200))
                    {
                        UpdateResponseNode(hub, responsePath, responseText.ToString());
                        lastUpdate = DateTimeOffset.UtcNow;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stream cancelled for thread {ThreadPath}", request.ThreadPath);
            // Save partial response
        }

        // Final update with complete text + agent/model info
        UpdateResponseNode(hub, responsePath, responseText.ToString(), request.AgentName, request.ModelName);
        await TouchThreadLastModifiedAsync(hub, request.ThreadPath);

        hub.Post(new SubmitMessageResponse { Success = true },
            o => o.ResponseFor(delivery));
    }

    private static async Task<int> ComputeNextMessageNumberAsync(IMessageHub hub, string threadPath)
    {
        var meshQuery = hub.ServiceProvider.GetService<IMeshService>();
        if (meshQuery == null)
            return 1;

        var messageNodes = await meshQuery.QueryAsync<MeshNode>(
            $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"
        ).ToListAsync();

        if (messageNodes.Count == 0)
            return 1;

        return messageNodes
            .Select(n => n.Path?.Split('/').LastOrDefault())
            .Where(id => id != null && int.TryParse(id, out _))
            .Select(id => int.Parse(id!))
            .DefaultIfEmpty(0)
            .Max() + 1;
    }

    private static async Task<string> CreateMessageNodeAsync(
        IMessageHub hub, string threadPath, int messageNumber, ThreadMessage message)
    {
        var nodeFactory = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var messagePath = $"{threadPath}/{messageNumber}";

        var messageNode = new MeshNode(messagePath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Order = messageNumber,
            Content = message
        };

        await nodeFactory.CreateNodeAsync(messageNode);
        return messagePath;
    }

    private static void UpdateResponseNode(IMessageHub hub, string responsePath, string text,
        string? agentName = null, string? modelName = null)
    {
        var nodeId = responsePath.Split('/').Last();
        var updatedMessage = new ThreadMessage
        {
            Id = nodeId,
            Role = "assistant",
            Text = text,
            Timestamp = DateTime.UtcNow,
            Type = ThreadMessageType.AgentResponse,
            AgentName = agentName,
            ModelName = modelName
        };
        var node = new MeshNode(responsePath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = updatedMessage
        };
        var nodeJson = JsonSerializer.SerializeToElement(node, hub.JsonSerializerOptions);
        hub.Post(new DataChangeRequest { Updates = [nodeJson] },
            o => o.WithTarget(new Address(responsePath)));
    }

    private static async Task TouchThreadLastModifiedAsync(IMessageHub hub, string threadPath)
    {
        try
        {
            var meshQuery = hub.ServiceProvider.GetService<IMeshService>();
            MeshNode? existingNode = null;
            if (meshQuery != null)
            {
                await foreach (var n in meshQuery.QueryAsync<MeshNode>($"path:{threadPath}"))
                {
                    existingNode = n;
                    break;
                }
            }

            if (existingNode != null)
            {
                var updatedNode = existingNode with { LastModified = DateTime.UtcNow };
                var nodeJson = JsonSerializer.SerializeToElement(updatedNode, hub.JsonSerializerOptions);
                hub.Post(new DataChangeRequest { Updates = [nodeJson] },
                    o => o.WithTarget(new Address(threadPath)));
            }
        }
        catch
        {
            // Best effort
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _streamingSession?.Dispose();
        _queueLock.Dispose();
    }
}
