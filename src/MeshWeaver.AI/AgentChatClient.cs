using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace MeshWeaver.AI;

public class AgentChatClient : IAgentChat
{
    private readonly IMessageHub hub;
    private readonly ILogger<AgentChatClient> logger;
    private readonly IMeshService? meshQuery;
    private readonly ImmutableList<IChatClientFactory> chatClientFactories;
    private ImmutableDictionary<string, ChatClientAgent> agents = ImmutableDictionary<string, ChatClientAgent>.Empty;
    private ImmutableQueue<ChatLayoutAreaContent> queuedLayoutAreaContent = ImmutableQueue<ChatLayoutAreaContent>.Empty;
    private HandoffRequest? pendingHandoff;
    private ImmutableList<AgentDisplayInfo> loadedAgents = ImmutableList<AgentDisplayInfo>.Empty;
    private string? lastLoadedContextPath;
    private string? lastLoadedNodeTypePath;
    private string currentThreadId = Guid.NewGuid().AsString();
    private string? currentAgentName;
    private AgentSession? sharedThread;
    private string? currentModelName;
    private string? persistentThreadId;
    private IReadOnlyList<string>? currentAttachments;
    private bool isPersistentFactory;
    private bool agentsInitialized;
    private string? cachedToolDocs;
    private string? cachedSystemPrompt;

    // Tracks which attachment paths are agent nodes (for context filtering)
    private ImmutableHashSet<string>? agentAttachmentPaths;

    // Tracks the first agent found in @references in the user's message text (for selection override)
    private string? firstMessageAgentPath;

    // Conversation history loaded from persisted ThreadMessage nodes for resume
    private IReadOnlyList<ThreadMessage>? conversationHistory;

    /// <summary>
    /// Captured during <see cref="CreateAgentsSync"/> when an
    /// <see cref="IChatClientFactory"/> throws while materialising an agent
    /// (typical cause: factory matches the model via <c>Supports(...)</c>
    /// but its underlying config — Endpoint / ApiKey — isn't set). Surfaced
    /// in the chat response when <see cref="SelectAgent"/> returns null, so
    /// the user sees the actual misconfiguration instead of the opaque
    /// "No suitable agent found" string.
    /// </summary>
    private string? lastAgentCreationError;

    /// <summary>
    /// Mesh-discovered models — bring-your-own-model entries from
    /// <c>nodeType:Model</c> nodes. Surfaced into ThreadChatView's picker
    /// alongside the factory-provided defaults.
    /// </summary>
    private ImmutableList<ModelInfo> loadedModels = ImmutableList<ModelInfo>.Empty;

    /// <summary>
    /// Snapshot of the synced model collection. Read by the chat view on
    /// every <see cref="WhenInitialized"/> emission.
    /// </summary>
    public IReadOnlyList<ModelInfo> LoadedModels => loadedModels;

    // Live subscription to the workspace-level synced agent collection. Disposed
    // and replaced on every Initialize() call so the current context's queries
    // become the active source. The synced query itself is cached at the
    // workspace level (per (contextPath, nodeType) tuple), so subscribing
    // again to the same id reuses one upstream subscription across instances.
    private IDisposable? agentsSubscription;

    // Latches the current loadedAgents emission as a hot observable so callers
    // (ThreadExecution + tests) can opt into an explicit ready-gate without
    // forcing Initialize itself to be async.
    private readonly ReplaySubject<AgentChatClient> agentsLoadedSubject = new(bufferSize: 1);

    /// <summary>
    /// Hot observable that emits this client every time the synced agent
    /// collection refreshes (initial load + every subsequent change). Replays
    /// the latest emission to new subscribers, so callers can use
    /// <c>chat.WhenInitialized.Take(1)</c> as a "wait until agents are ready"
    /// gate without coupling to the cold/warm distinction inside
    /// <see cref="Initialize"/>.
    /// </summary>
    public IObservable<AgentChatClient> WhenInitialized => agentsLoadedSubject;

    /// <summary>
    /// Constructs a chat client with optional prior conversation history —
    /// passed in from the caller (typically <c>ThreadExecution</c> on a fresh
    /// grain after restart). The client itself stays out of the history-load
    /// concern: callers fetch prior messages and inject them via this ctor.
    /// </summary>
    public AgentChatClient(IServiceProvider serviceProvider, IReadOnlyList<ThreadMessage>? priorMessages = null)
    {
        hub = serviceProvider.GetRequiredService<IMessageHub>();
        logger = serviceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        meshQuery = serviceProvider.GetService<IMeshService>();
        chatClientFactories = serviceProvider.GetServices<IChatClientFactory>().ToImmutableList();
        if (priorMessages is { Count: > 0 })
            conversationHistory = priorMessages;
    }

    /// <summary>
    /// Strips any trailing satellite segments (segments starting with '_', e.g. "_Thread/&lt;slug&gt;",
    /// "_Comment/&lt;id&gt;") from a context path so the agent reasons about the main node, not the
    /// satellite. Returns the input unchanged when null/empty or when no '_' segment is present.
    /// </summary>
    private static string? NormalizeContextPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('_'))
                return string.Join('/', segments, 0, i);
        }
        return path;
    }

    public AgentContext? Context { get; private set; }

    /// <inheritdoc />
    public ThreadExecutionContext? ExecutionContext { get; private set; }

    /// <summary>
    /// Backing Subject for <see cref="Delegations"/>. ExecuteDelegationAsync
    /// emits onto this directly; subscribers (cancel watcher, tool-call
    /// stamper) receive the events on the emitting thread — they're
    /// expected to defer real work to a Hub.Post handler rather than
    /// mutating state inline (see plan Slice 2c).
    /// </summary>
    private readonly System.Reactive.Subjects.Subject<MeshWeaver.AI.Delegation.DelegationEvent> _delegations = new();

    /// <inheritdoc />
    public IObservable<MeshWeaver.AI.Delegation.DelegationEvent> Delegations => _delegations;

    /// <summary>
    /// Internal hook for ExecuteDelegationAsync to emit lifecycle events.
    /// Public would invite outside-the-agent writes; <c>internal</c> keeps
    /// the emit surface tied to the agent factory in this assembly.
    /// </summary>
    internal void EmitDelegationEvent(MeshWeaver.AI.Delegation.DelegationEvent evt)
    {
        switch (evt.Phase)
        {
            case MeshWeaver.AI.Delegation.DelegationLifecycle.Dispatched:
                ImmutableInterlocked.Update(ref _activeDelegationPaths, set => set.Add(evt.SubThreadPath));
                break;
            case MeshWeaver.AI.Delegation.DelegationLifecycle.Terminal:
                ImmutableInterlocked.Update(ref _activeDelegationPaths, set => set.Remove(evt.SubThreadPath));
                break;
        }
        _delegations.OnNext(evt);
    }

    /// <summary>
    /// In-memory set of sub-thread paths currently in flight on this chat
    /// session. Maintained by <see cref="EmitDelegationEvent"/>: Dispatched
    /// adds, Terminal removes. Read by the cancel watcher in
    /// <c>ThreadExecution.SetupCancellationWatcher</c> to propagate cancel
    /// to sub-threads whose paths haven't yet been persisted onto
    /// <c>Thread.StreamingToolCalls[].DelegationPath</c>, and by the
    /// streaming-loop's stamp pass that walks unmatched
    /// <c>delegate_to_agent</c> tool-call entries. Replaces the legacy
    /// <see cref="DelegationPaths"/> dictionary (which keyed by transient
    /// display name).
    /// </summary>
    private ImmutableHashSet<string> _activeDelegationPaths = ImmutableHashSet<string>.Empty;

    /// <summary>Snapshot of active delegation sub-thread paths.</summary>
    public ImmutableHashSet<string> ActiveDelegationPaths => _activeDelegationPaths;

    /// <inheritdoc />
    public Action<ToolCallEntry>? ForwardToolCall { get; set; }

    /// <inheritdoc />
    public Action<NodeChangeEntry>? ForwardNodeChange { get; set; }

    /// <summary>Sets the execution context for delegation sub-thread creation.</summary>
    public void SetExecutionContext(ThreadExecutionContext? ctx) => ExecutionContext = ctx;

    public void SetThreadId(string threadId)
    {
        if (string.IsNullOrEmpty(threadId))
            throw new ArgumentException("Thread ID cannot be null or empty", nameof(threadId));

        currentThreadId = threadId;
        sharedThread = null; // Reset shared thread when switching conversations
        logger.LogInformation("Switched to thread: {ThreadId}", threadId);
    }

    /// <summary>
    /// Sets the persistent thread ID for server-side conversation history.
    /// When set, the agent session will link to this server-managed thread.
    /// </summary>
    public void SetPersistentThreadId(string? persistentId)
    {
        persistentThreadId = persistentId;
        if (!string.IsNullOrEmpty(persistentId))
        {
            logger.LogInformation("Set persistent thread ID: {PersistentThreadId}", persistentId);
        }
    }

    /// <summary>
    /// Returns the ChatClientAgent for the given name (or default).
    /// Used by ThreadExecution to call agent.ChatClient.GetStreamingResponseAsync directly.
    /// </summary>
    public ChatClientAgent? GetAgent(string? agentName = null)
    {
        if (!string.IsNullOrEmpty(agentName) && agents.TryGetValue(agentName, out var named))
            return named;
        // Default agent (first in order)
        return agents.Values.FirstOrDefault();
    }

    /// <summary>
    /// Sets attachment paths whose content will be loaded and included in the next message.
    /// </summary>
    public void SetAttachments(IReadOnlyList<string>? paths)
    {
        currentAttachments = paths is { Count: > 0 } ? paths : null;
    }

    /// <summary>
    /// True once an <see cref="AgentSession"/> has been created for this client —
    /// i.e. at least one streaming response has run, so the agent is carrying the
    /// conversation in memory. The thread-execution layer uses this as the
    /// "is this a fresh grain?" signal to decide whether persisted history needs
    /// to be re-loaded from the mesh.
    /// </summary>
    public bool HasActiveSession => sharedThread != null;

    /// <summary>
    /// Sets conversation history from persisted ThreadMessage nodes.
    /// Injected once into the next message context, then cleared (the AgentSession
    /// accumulates subsequent messages going forward).
    /// </summary>
    public void SetConversationHistory(IReadOnlyList<ThreadMessage> history)
    {
        conversationHistory = history is { Count: > 0 } ? history : null;
    }

    private async Task<AgentSession> GetOrCreateThreadAsync(ChatClientAgent agent)
    {
        // Use shared thread across all agents in this conversation
        if (sharedThread != null)
            return sharedThread;

        // For persistent factories with a persistent thread ID, create a session linked to the server-side thread
        if (isPersistentFactory && !string.IsNullOrEmpty(persistentThreadId))
        {
            sharedThread = await agent.CreateSessionAsync(persistentThreadId);
            logger.LogInformation("Resumed persistent thread: {PersistentThreadId}", persistentThreadId);
            return sharedThread;
        }

        if (isPersistentFactory)
        {
            // For persistent factories without an existing thread, create a new server-side session
            sharedThread = await agent.CreateSessionAsync(currentThreadId);
            persistentThreadId = currentThreadId;
            logger.LogInformation("Created new persistent thread: {PersistentThreadId}", persistentThreadId);
        }
        else
        {
            sharedThread = await agent.CreateSessionAsync();
        }

        return sharedThread;
    }

    private Task SaveThreadAsync(ChatClientAgent agent, AgentSession thread, string? threadId = null)
    {
        // Session state is ephemeral — persisted via Thread MeshNodes, not IChatPersistenceService
        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates the Thread MeshNode with PersistentThreadId and ProviderType if they were newly set.
    /// Routes the update through the IMeshNodeStreamCache so the patch lands on
    /// the owning per-thread hub's stream — no separate DataChangeRequest →
    /// owner round-trip that can dangle as a pending callback if the owner
    /// is mid-streaming (the SubThreadHangRepro tests caught this exact leak).
    /// </summary>
    private Task UpdateThreadPersistentIdAsync(string threadNodePath)
    {
        var factory = GetFactoryForModel(currentModelName);
        var workspace = hub.GetWorkspace();
        workspace.GetMeshNodeStream(threadNodePath)
            .Update(node =>
            {
                if (node?.Content is not Thread threadContent) return node!;
                if (!string.IsNullOrEmpty(threadContent.PersistentThreadId)) return node;
                return node with
                {
                    Content = threadContent with
                    {
                        PersistentThreadId = persistentThreadId,
                        ProviderType = factory?.Name
                    }
                };
            })
            .Subscribe(
                _ => logger.LogInformation(
                    "Updated thread {Path} with PersistentThreadId={PersistentThreadId}",
                    threadNodePath, persistentThreadId),
                ex => logger.LogWarning(ex,
                    "Failed to update PersistentThreadId on thread {Path}", threadNodePath));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the static system prompt (agent instructions + tool docs) once,
    /// then appends dynamic parts (context, attachments, history) on each call.
    /// </summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tiff"
    };

    private static readonly Dictionary<string, string> ExtensionToMediaType = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".tiff"] = "image/tiff"
    };

    private async Task<(string Text, ImmutableList<DataContent> BinaryAttachments)> BuildMessageWithContextAsync(IReadOnlyCollection<ChatMessage> messages, string? agentName = null)
    {
        var messageText = new StringBuilder();

        // Static part: agent instructions + tool docs (built once, cached)
        if (cachedSystemPrompt == null && !isPersistentFactory)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(agentName))
            {
                var agentInfo = loadedAgents.FirstOrDefault(a => a.Name == agentName);
                var agentInstructions = agentInfo?.AgentConfiguration?.Instructions;
                if (!string.IsNullOrEmpty(agentInstructions))
                {
                    sb.AppendLine("# Agent Identity and Instructions");
                    sb.AppendLine();
                    sb.AppendLine("You are acting as the following agent. Follow these instructions strictly:");
                    sb.AppendLine();
                    sb.AppendLine(agentInstructions);
                    sb.AppendLine();
                }
            }

            var toolDocs = cachedToolDocs ?? await LoadToolDocumentationAsync();
            if (!string.IsNullOrEmpty(toolDocs))
            {
                sb.AppendLine("# Available Tools Documentation");
                sb.AppendLine();
                sb.AppendLine(toolDocs);
                sb.AppendLine();
            }

            cachedSystemPrompt = sb.ToString();
        }

        if (!string.IsNullOrEmpty(cachedSystemPrompt))
            messageText.Append(cachedSystemPrompt);

        // User identity
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var userContext = accessService?.Context ?? accessService?.CircuitContext;
        if (userContext != null)
        {
            messageText.AppendLine("# Current User");
            messageText.AppendLine();
            messageText.AppendLine($"- **User ID:** `{userContext.ObjectId}`");
            if (!string.IsNullOrEmpty(userContext.Name))
                messageText.AppendLine($"- **Name:** {userContext.Name}");
            messageText.AppendLine($"- **User namespace:** `User/{userContext.ObjectId}`");
            messageText.AppendLine();
        }

        // Dynamic part: context (changes per navigation)
        if (Context != null)
        {
            var contextPath = Context.Path ?? Context.Address?.ToString();
            messageText.AppendLine("# Current Application Context");
            messageText.AppendLine();

            // Show the current node
            if (Context.Node != null)
            {
                messageText.AppendLine($"**Current node:** `{Context.Node.Path}` (type: {Context.Node.NodeType}, name: {Context.Node.Name})");
                messageText.AppendLine("This node already exists. To modify it, use Get then Update — do NOT Create it again.");
            }
            else if (!string.IsNullOrEmpty(contextPath))
            {
                messageText.AppendLine($"**Current path:** `{contextPath}`");
            }
            messageText.AppendLine();

            // 🚨 No await foreach over IMeshService.QueryAsync here.
            // That used to enumerate children of the context path to inject
            // into the system prompt. The fan-out through IMeshQueryProvider
            // bridges back through hub messaging in a way that can park the
            // streaming task indefinitely (chat stuck on "Generating
            // response..."). The agent has a `Search` tool — it can pull the
            // children itself when it actually needs them.

            messageText.AppendLine("When creating nodes, first explore the structure to find the best place:");
            messageText.AppendLine("- `Search('namespace:{path} scope:descendants')` to see the full tree");
            messageText.AppendLine("- Create under the current node or an appropriate sub-node");
            messageText.AppendLine("- If no suitable sub-node exists, create one first, then put content inside it");
            messageText.AppendLine();
        }

        // Load and add attachment content (text + binary)
        var attachmentPaths = currentAttachments;
        var binaryAttachments = ImmutableList<DataContent>.Empty;
        var attachmentHeaderWritten = false;
        if (attachmentPaths is { Count: > 0 })
        {
            var meshPlugin = new MeshPlugin(hub, this);
            var contentService = hub.ServiceProvider.GetService<ContentCollections.IContentService>();

            foreach (var path in attachmentPaths)
            {
                try
                {
                    var cleanPath = path.TrimStart('@');
                    if (agentAttachmentPaths?.Contains(cleanPath) == true)
                        continue;

                    // Check for content: prefix — binary file from content collection
                    var contentIdx = cleanPath.IndexOf("content:", StringComparison.OrdinalIgnoreCase);
                    if (contentIdx >= 0 && contentService != null)
                    {
                        var nodePath = contentIdx > 0 ? cleanPath[..(contentIdx - 1)]
                            : (Context?.Path ?? Context?.Address?.ToString());
                        var fileName = cleanPath[(contentIdx + "content:".Length)..];
                        var ext = System.IO.Path.GetExtension(fileName);

                        if (BinaryExtensions.Contains(ext))
                        {
                            // Load binary from content collection on the node's hub
                            var effectivePath = nodePath ?? Context?.Path;
                            var stream = await contentService.GetContentAsync("content", fileName);
                            if (stream != null)
                            {
                                using (stream)
                                {
                                    using var ms = new MemoryStream();
                                    await stream.CopyToAsync(ms);
                                    var mediaType = ExtensionToMediaType.GetValueOrDefault(ext, "application/octet-stream");
                                    binaryAttachments = binaryAttachments.Add(
                                        new DataContent(ms.ToArray(), mediaType) { Name = fileName });
                                    logger.LogInformation("Loaded binary attachment: {FileName} ({MediaType}, {Size} bytes)",
                                        fileName, mediaType, ms.Length);
                                }
                            }
                            continue;
                        }
                    }

                    // Text attachment — load via MeshPlugin.Get
                    var content = await meshPlugin.Get($"@{cleanPath}");
                    if (!string.IsNullOrEmpty(content) && !content.StartsWith("Not found") && !content.StartsWith("Error"))
                    {
                        if (content.Length > 8000)
                            content = content[..8000] + "\n... (truncated)";
                        if (!attachmentHeaderWritten)
                        {
                            messageText.AppendLine("# Attached Content");
                            messageText.AppendLine();
                            attachmentHeaderWritten = true;
                        }
                        messageText.AppendLine($"## Attachment: {path}");
                        messageText.AppendLine();
                        messageText.AppendLine(content);
                        messageText.AppendLine();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Error loading attachment content for: {Path}", path);
                }
            }
        }

        // Inject conversation history from persisted ThreadMessage nodes (resume scenario)
        if (conversationHistory is { Count: > 0 })
        {
            messageText.AppendLine("# Conversation History");
            messageText.AppendLine();
            messageText.AppendLine("Previous messages in this thread:");
            messageText.AppendLine();
            foreach (var histMsg in conversationHistory)
            {
                var roleName = histMsg.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "User" : "Assistant";
                messageText.AppendLine($"**{roleName}:** {histMsg.Text}");
                messageText.AppendLine();
            }
            conversationHistory = null; // Only inject once; AgentSession accumulates subsequent messages
        }

        // Add user messages (with @@ inline reference resolution)
        foreach (var message in messages)
        {
            var text = ExtractTextFromMessage(message);
            text = await InlineReferenceResolver.ResolveAsync(text, hub, this);
            messageText.Append(text);
        }

        return (messageText.ToString(), binaryAttachments);
    }

    /// <summary>
    /// Loads tool documentation from the mesh and resolves any @@ references within it.
    /// </summary>
    private async Task<string> LoadToolDocumentationAsync()
    {
        var meshPlugin = new MeshPlugin(hub, this);
        var docs = await meshPlugin.Get("@Doc/AI/Tools/MeshPlugin");

        if (docs.StartsWith("Not found") || docs.StartsWith("Error"))
            return string.Empty;

        // Try to extract just the markdown content from the JSON response
        try
        {
            var node = JsonSerializer.Deserialize<MeshNode>(docs, hub.JsonSerializerOptions);
            if (node?.Content is JsonElement contentElement && contentElement.ValueKind == JsonValueKind.String)
            {
                var content = contentElement.GetString() ?? string.Empty;
                // Resolve @@ references in tool documentation (e.g., @@QuerySyntax, @@UnifiedPath)
                content = await InlineReferenceResolver.ResolveAsync(content, hub, this);
                return content;
            }
            // If content is not a simple string, return empty
            return string.Empty;
        }
        catch
        {
            // If parsing fails, the content might be raw markdown
            return docs;
        }
    }


    public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Detect agent attachments (for context content filtering)
        DetectAgentAttachments();

        // Detect agent @references in message text (for selection override)
        var lastMessageText = messages.LastOrDefault() is { } last ? ExtractTextFromMessage(last) : null;
        DetectMessageAgentReferences(lastMessageText);

        // 🚨 Cold-start gate: wait for the synced agent query to emit its Initial
        // before SelectAgent — otherwise on a fresh Orleans silo the agents
        // collection is empty and SelectAgent returns null, producing the
        // "No suitable agent found" failure mode (Orleans.Test ChatHistory /
        // delegation / chat-portal tests, ~9 of 19 failing test signatures).
        // 30 s budget matches the cache-read timeout in HandleStartExecutionOnExec.
        try
        {
            await WhenInitialized
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) { /* fall through — SelectAgent will return the canned error */ }

        // Pure in-memory lookup against the synced agent cache.
        var agent = SelectAgent(messages.LastOrDefault());
        if (agent == null)
        {
            yield return new ChatMessage(ChatRole.Assistant,
                lastAgentCreationError ?? "No suitable agent found to handle the request.");
            yield break;
        }

        currentAgentName = agent.Name;

        // Get or create thread for this agent
        var thread = await GetOrCreateThreadAsync(agent);

        // Build the user message with context and agent instructions
        var (userText, binaryParts) = await BuildMessageWithContextAsync(messages, currentAgentName);
        currentAttachments = null; // Clear after use

        // Build ChatMessage with mixed content (text + binary attachments)
        var chatMessage = BuildChatMessage(userText, binaryParts);

        // Get response from the agent with thread
        var response = await agent.RunAsync(chatMessage, thread, cancellationToken: cancellationToken);

        // Save the updated thread
        await SaveThreadAsync(agent, thread);

        foreach (var responseMsg in response.Messages)
        {
            // Log function calls and results
            foreach (var content in responseMsg.Contents)
            {
                if (content is FunctionCallContent functionCall)
                {
                    logger.LogInformation("Agent {AgentName} calling tool: {FunctionName}",
                        currentAgentName, functionCall.Name);
                }
                else if (content is FunctionResultContent functionResult)
                {
                    logger.LogInformation("Agent {AgentName} received result from tool: {CallId}",
                        currentAgentName, functionResult.CallId);
                }
            }

            // Yield the complete message with all contents (including FunctionCallContent)
            yield return responseMsg;
        }

        // Check for any queued layout area content
        while (!queuedLayoutAreaContent.IsEmpty)
        {
            queuedLayoutAreaContent = queuedLayoutAreaContent.Dequeue(out var layoutAreaContent);
            var layoutAreaMessage = new ChatMessage(ChatRole.Assistant, [layoutAreaContent])
            {
                AuthorName = currentAgentName ?? "Assistant"
            };
            yield return layoutAreaMessage;
        }

        // Handle handoff: target agent takes over the shared thread
        while (pendingHandoff != null)
        {
            var handoff = pendingHandoff;
            pendingHandoff = null;

            // Emit handoff content for UI
            var handoffMessage = new ChatMessage(ChatRole.Assistant, [
                new ChatHandoffContent(handoff.SourceAgentName, handoff.TargetAgentName, handoff.Message)
            ])
            {
                AuthorName = handoff.SourceAgentName
            };
            yield return handoffMessage;

            // Resolve target agent
            var targetId = handoff.TargetAgentName.Split('/').Last();
            if (!agents.TryGetValue(targetId, out var targetAgent))
            {
                logger.LogWarning("Handoff target agent '{TargetAgent}' not found", handoff.TargetAgentName);
                yield return new ChatMessage(ChatRole.Assistant, $"Handoff failed: agent '{handoff.TargetAgentName}' not found.");
                break;
            }

            // Switch active agent
            currentAgentName = targetId;
            logger.LogInformation("Handoff: {Source} -> {Target}, running target agent on shared thread",
                handoff.SourceAgentName, targetId);

            // Run target agent on the same shared thread with the handoff message
            var handoffResponse = await targetAgent.RunAsync(handoff.Message, thread, cancellationToken: cancellationToken);
            await SaveThreadAsync(targetAgent, thread);

            foreach (var msg in handoffResponse.Messages)
            {
                yield return msg;
            }

            // Yield any queued layout area content from the handoff target
            while (!queuedLayoutAreaContent.IsEmpty)
            {
                queuedLayoutAreaContent = queuedLayoutAreaContent.Dequeue(out var lac);
                yield return new ChatMessage(ChatRole.Assistant, [lac])
                {
                    AuthorName = currentAgentName ?? "Assistant"
                };
            }
            // Loop continues if target agent also requested a handoff (chained handoffs)
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Detect agent attachments (for context content filtering)
        DetectAgentAttachments();

        // Detect agent @references in message text (for selection override)
        var lastMessageTextStreaming = messages.LastOrDefault() is { } lastMsg ? ExtractTextFromMessage(lastMsg) : null;
        DetectMessageAgentReferences(lastMessageTextStreaming);

        // 🚨 Same cold-start gate as GetResponseAsync — wait for synced agent
        // query Initial before SelectAgent, else cold Orleans silos return
        // "No suitable agent found" with an empty cache.
        try
        {
            await WhenInitialized
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) { /* fall through */ }

        // Pure in-memory lookup against the synced agent cache.
        var agent = SelectAgent(messages.LastOrDefault());
        if (agent == null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                lastAgentCreationError ?? "No suitable agent found to handle the request.");
            yield break;
        }

        currentAgentName = agent.Name;

        // Get or create thread for this agent
        var thread = await GetOrCreateThreadAsync(agent);

        // Pass all messages as separate turns with system prompt prepended.
        // The agent's ChatClient includes FunctionInvokingChatClient for tool calls.
        var turnMessages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(agent.Instructions))
            turnMessages.Add(new ChatMessage(ChatRole.System, agent.Instructions));
        turnMessages.AddRange(messages);
        logger.LogInformation("[AgentChat] Sending {Count} messages (+ system) to {Agent}",
            messages.Count, agent.Name);
        currentAttachments = null;

        // ChatOptions MUST include the agent's tools. Without them, the inner
        // client (AzureClaudeChatClient) never sends tool definitions to Claude,
        // and FunctionInvokingChatClient has nothing to match against.
        // Get tools from FunctionInvokingChatClient.AdditionalTools (where the
        // ChatClientAgent constructor places them).
        var functionInvoker = agent.ChatClient.GetService<FunctionInvokingChatClient>();
        var chatOptions = new ChatOptions();
        var tools = functionInvoker?.AdditionalTools is { Count: > 0 } additionalTools
            ? additionalTools.ToList()
            : new List<AITool>();
        // Always inject check_inbox so the agent can poll for user messages
        // queued during the in-flight turn. The tool drains
        // Thread.PendingUserMessages atomically and returns the texts so the
        // agent can fold them into the current response — see InboxTool.
        tools.Add(InboxTool.CreateCheckInboxTool(hub, logger));
        chatOptions.Tools = tools;
        await foreach (var update in agent.ChatClient.GetStreamingResponseAsync(turnMessages, chatOptions, cancellationToken))
        {
            // Forward the complete update with all contents (including FunctionCallContent)
            if (update.Contents.Count > 0)
            {
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent functionCall)
                    {
                        logger.LogInformation("Agent {AgentName} calling tool: {FunctionName}",
                            currentAgentName, functionCall.Name);

                        // Yield the function call content so the UI can display it properly
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [content])
                        {
                            AuthorName = currentAgentName ?? "Assistant"
                        };
                    }
                    else if (content is FunctionResultContent functionResult)
                    {
                        logger.LogInformation("Agent {AgentName} received result from tool: {CallId}",
                            currentAgentName, functionResult.CallId);

                        // Yield the result so ThreadExecution can track completed tool calls
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [content])
                        {
                            AuthorName = currentAgentName ?? "Assistant"
                        };
                    }
                    else if (content is UsageContent)
                    {
                        // Forward token-usage content so ThreadExecution can record
                        // InputTokens / OutputTokens / TotalTokens on the response cell.
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [content])
                        {
                            AuthorName = currentAgentName ?? "Assistant"
                        };
                    }
                }
            }

            // Convert from agent updates to chat response updates - only yield if there's text
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, update.Text)
                {
                    AuthorName = currentAgentName ?? "Assistant"
                };
            }
        }

        // Save the updated thread
        await SaveThreadAsync(agent, thread);

        // Check for any queued layout area content
        while (!queuedLayoutAreaContent.IsEmpty)
        {
            queuedLayoutAreaContent = queuedLayoutAreaContent.Dequeue(out var layoutAreaContent);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [layoutAreaContent])
            {
                AuthorName = currentAgentName ?? "Assistant"
            };
        }

        // Handle handoff: target agent takes over the shared thread (streaming)
        while (pendingHandoff != null)
        {
            var handoff = pendingHandoff;
            pendingHandoff = null;

            // Emit handoff content for UI
            yield return new ChatResponseUpdate(ChatRole.Assistant, [
                new ChatHandoffContent(handoff.SourceAgentName, handoff.TargetAgentName, handoff.Message)
            ])
            {
                AuthorName = handoff.SourceAgentName
            };

            // Resolve target agent
            var targetId = handoff.TargetAgentName.Split('/').Last();
            if (!agents.TryGetValue(targetId, out var targetAgent))
            {
                logger.LogWarning("Handoff target agent '{TargetAgent}' not found", handoff.TargetAgentName);
                yield return new ChatResponseUpdate(ChatRole.Assistant, $"Handoff failed: agent '{handoff.TargetAgentName}' not found.");
                break;
            }

            // Switch active agent
            currentAgentName = targetId;
            logger.LogInformation("Handoff (streaming): {Source} -> {Target}, running target agent on shared thread",
                handoff.SourceAgentName, targetId);

            // Run target agent streaming on the same shared thread
            await foreach (var update in targetAgent.RunStreamingAsync(handoff.Message, thread, cancellationToken: cancellationToken))
            {
                if (update.Contents.Count > 0)
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent functionCall)
                        {
                            logger.LogInformation("Agent {AgentName} calling tool: {FunctionName}",
                                currentAgentName, functionCall.Name);
                            yield return new ChatResponseUpdate(ChatRole.Assistant, [content])
                            {
                                AuthorName = currentAgentName ?? "Assistant"
                            };
                        }
                        else if (content is FunctionResultContent functionResult)
                        {
                            // Previously dropped: handoff path forwarded FunctionCallContent
                            // but never the matching FunctionResultContent, so tool calls
                            // made by a handoff target stayed "pending" forever in
                            // ThreadExecution.toolCallLog — visible to the user as a tool
                            // call with no result. Forward results too.
                            logger.LogInformation("Agent {AgentName} received result from tool: {CallId}",
                                currentAgentName, functionResult.CallId);
                            yield return new ChatResponseUpdate(ChatRole.Assistant, [content])
                            {
                                AuthorName = currentAgentName ?? "Assistant"
                            };
                        }
                        else if (content is UsageContent)
                        {
                            yield return new ChatResponseUpdate(ChatRole.Assistant, [content])
                            {
                                AuthorName = currentAgentName ?? "Assistant"
                            };
                        }
                    }
                }

                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, update.Text)
                    {
                        AuthorName = currentAgentName ?? "Assistant"
                    };
                }
            }

            await SaveThreadAsync(targetAgent, thread);

            // Yield any queued layout area content from the handoff target
            while (!queuedLayoutAreaContent.IsEmpty)
            {
                queuedLayoutAreaContent = queuedLayoutAreaContent.Dequeue(out var lac);
                yield return new ChatResponseUpdate(ChatRole.Assistant, [lac])
                {
                    AuthorName = currentAgentName ?? "Assistant"
                };
            }
            // Loop continues if target agent also requested a handoff (chained handoffs)
        }
    }

    // Pattern: @agent/AgentName (anywhere in message)
    private static readonly Regex AgentReferencePattern =
        new(@"@agent/(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Pure in-memory selection over the synced agent cache (<see cref="loadedAgents"/>
    /// + <see cref="agents"/>). No queries, no awaits — both collections are
    /// kept fresh by the workspace-level synced query subscription wired up in
    /// <see cref="Initialize"/>. Anyone who needs the agent list elsewhere
    /// must read from those caches; do NOT add per-call <c>QueryAsync</c>
    /// fallbacks here.
    /// </summary>
    private ChatClientAgent? SelectAgent(ChatMessage? lastMessage)
    {
        // 1. Check for explicit @agent/Name reference in message (highest priority)
        if (lastMessage != null)
        {
            var text = ExtractTextFromMessage(lastMessage);
            var agentMatch = AgentReferencePattern.Match(text);
            if (agentMatch.Success)
            {
                var agentName = agentMatch.Groups[1].Value;
                if (agents.TryGetValue(agentName, out var agent))
                    return agent;
                // Case-insensitive fallback
                var found = agents.FirstOrDefault(kvp =>
                    kvp.Key.Equals(agentName, StringComparison.OrdinalIgnoreCase));
                if (found.Value != null)
                    return found.Value;
            }
        }

        // 2. Use first agent found in @references in message text (e.g., @Agent/Research)
        if (!string.IsNullOrEmpty(firstMessageAgentPath))
        {
            var agentId = firstMessageAgentPath.Split('/').Last();
            if (agents.TryGetValue(agentId, out var refAgent))
                return refAgent;
            // Case-insensitive fallback
            var foundRef = agents.FirstOrDefault(kvp =>
                kvp.Key.Equals(agentId, StringComparison.OrdinalIgnoreCase));
            if (foundRef.Value != null)
                return foundRef.Value;
        }

        // 3. Use explicitly selected agent (from dropdown) if set
        if (!string.IsNullOrEmpty(currentAgentName) && agents.TryGetValue(currentAgentName, out var selectedAgent))
            return selectedAgent;

        // 4. Use the synced ordered list — best match for context, kept fresh
        // by the workspace synced query (see Initialize).
        if (loadedAgents.Count > 0)
        {
            var bestAgent = loadedAgents[0];
            if (agents.TryGetValue(bestAgent.Name, out var agent))
                return agent;
        }

        // 5. Return first agent as fallback
        return agents.Values.FirstOrDefault();
    }
    /// <inheritdoc />
    public void SetSelectedAgent(string? agentName)
    {
        currentAgentName = agentName;
    }

    public void SetContext(AgentContext? applicationContext)
    {
        // Normalize at the boundary: strip satellite segments (e.g. "_Thread/<slug>") so
        // the agent reasons about the main context node, not the thread/comment under it.
        if (applicationContext is { Path: { Length: > 0 } p })
        {
            var normalized = NormalizeContextPath(p);
            if (!string.IsNullOrEmpty(normalized) && !string.Equals(normalized, p, StringComparison.Ordinal))
                applicationContext = applicationContext with { Path = normalized };
        }
        Context = applicationContext;

        // Re-initialise the synced-agent subscription whenever the context node's
        // NodeType changes — that's what determines the third per-NodeType query
        // in BuildAgentQueries (`namespace:{nodeTypePath} ... scope:selfAndAncestors`).
        // Without this, agents defined under the NodeType path (e.g. TodoAgent at
        // ACME/Project) never surface for an instance whose NodeType points at it.
        var newNodeTypePath = applicationContext?.Node?.NodeType;
        if (lastLoadedContextPath != null && newNodeTypePath != lastLoadedNodeTypePath)
            Initialize(lastLoadedContextPath, currentModelName, newNodeTypePath);
    }

    /// <summary>
    /// Returns an IObservable that emits once when agent initialization is complete.
    /// Uses ObserveQuery (reactive) — no await, no blocking, no deadlock.
    /// Subscribe to this and chain the streaming loop after it emits.
    /// </summary>
    /// <summary>
    /// Returns an IObservable that emits the initialized AgentChatClient when agents are ready.
    /// Re-emits when agent definitions change (system prompt updates, new agents added).
    /// Uses ObserveQuery (reactive) — no await, no blocking, no deadlock.
    /// </summary>
    /// <summary>
    /// Synchronously binds this chat client to the workspace's shared synced
    /// agent collection for the given context. Returns immediately; agents
    /// populate when the underlying <see cref="SyncedQueryDataSourceExtensions.GetQuery(IWorkspace, object, string[])"/>
    /// emits — synchronously when warm-cached, asynchronously on first cold
    /// load. Callers that need an explicit ready-gate should subscribe to
    /// <see cref="WhenInitialized"/>.
    /// </summary>
    public AgentChatClient Initialize(string? contextPath, string? modelName = null, string? nodeTypePath = null)
    {
        // Normalize at entry so satellite paths (e.g. "ACME/Project/_Thread/<slug>") collapse to
        // their main-node path before any downstream query/cache key uses them.
        contextPath = NormalizeContextPath(contextPath);
        currentModelName = modelName;
        lastLoadedContextPath = contextPath;
        // Default the NodeType-search namespace to the context node's NodeType when the
        // caller didn't supply one. AgentPickerProjection.BuildAgentQueries will only
        // emit the third query (`namespace:{nodeTypePath} scope:selfAndAncestors`) when
        // this is non-null — that's what surfaces NodeType-defined agents (e.g. TodoAgent
        // at ACME/Project for an instance with NodeType=ACME/Project).
        nodeTypePath ??= Context?.Node?.NodeType;
        lastLoadedNodeTypePath = nodeTypePath;

        // First-time init or context switch: subscribe to the SAME synced-query
        // pipe the chat picker UI uses (AgentPickerProjection.ObserveAgents/
        // ObserveModels). One source of truth — no separate "AgentChatClient
        // does its own ObserveQuery" chain that drifted from the picker and
        // produced "No suitable agent" even though the dropdown showed 9 of
        // them. The synced query runs on the workspace of THIS hub (the thread
        // hub passed in via the ctor's service provider), not the _Exec child
        // — _Exec is blocked by the streaming Task.Run.
        var workspace = hub.GetWorkspace();
        agentsSubscription?.Dispose();
        modelsSubscription?.Dispose();

        // 🚨 Subscribe to agents and models INDEPENDENTLY. Models are not
        // required for agent selection — only for the picker UI's model
        // dropdown. Tying readiness to a CombineLatest of both means a slow
        // model emission delays the first user-visible reply by however long
        // the model query takes; we observed multi-second extra latency on
        // Postgres-backed deploys with cold synced-query caches. Decouple:
        // WhenInitialized fires as soon as agents are ready; models populate
        // their own loadedModels in the background.
        // Subscribe to the live synced agent stream — every emission updates
        // loadedAgents and rebuilds the agents dict. NO Timeout fallback: the
        // synced query emits ONE Initial event and then goes quiet until
        // agents change; a Timeout(8s, emptyFallback) wrapper would interpret
        // that quiescence as a failure and wipe loadedAgents 8s after the
        // genuine Initial emission, breaking every subsequent chat round.
        // The 5-min no-progress watchdog in ThreadExecution is the canonical
        // safety net for genuinely stuck pipelines.
        var readinessFired = false;
        agentsSubscription = AgentPickerProjection.ObserveAgents(workspace, hub, contextPath, nodeTypePath)
            .Subscribe(
                agents =>
                {
                    logger.LogDebug("[AgentChatClient] Agent emission: {Count} for ctx={Ctx}",
                        agents.Count, contextPath ?? "(null)");
                    ApplyAgents(agents, contextPath);
                    // Synced queries emit Initial first, then incremental changes.
                    // Initial-with-0-agents is the legitimate "no agents configured"
                    // state — not "still loading". Gating readiness on count>0
                    // hangs WhenInitialized forever in that case (the only-Initial
                    // synced query then quiesces). Fire on every emission; callers
                    // inspect loadedAgents to decide what to do with an empty list.
                    if (!readinessFired)
                        readinessFired = true;
                    agentsLoadedSubject.OnNext(this);
                },
                ex =>
                {
                    logger.LogWarning(ex, "[AgentChatClient] Agent subscription faulted — unblocking with empty");
                    ApplyAgents(Array.Empty<AgentDisplayInfo>(), contextPath);
                    readinessFired = true;
                    agentsLoadedSubject.OnNext(this);
                },
                () =>
                {
                    if (!readinessFired)
                    {
                        readinessFired = true;
                        agentsLoadedSubject.OnNext(this);
                    }
                });

        // No timer fallback: cold-start synced queries (especially on sub-thread
        // workspaces) can take longer than any arbitrary timer to populate.
        // Forcing readiness with empty agents only causes "No suitable agent
        // found" false-positives. Genuine "synced query never emits" cases are
        // covered by the OnError / OnCompleted handlers above and by the
        // ThreadSubmission cancel button — manual cancellation is preferable
        // to an arbitrary deadline.

        // Same shape: live subscription with no Timeout fallback —
        // the synced query emits Initial then quiesces, so a Timeout(8s,
        // empty) wrapper would wipe loadedModels 8s after the real Initial.
        modelsSubscription = AgentPickerProjection.ObserveModels(workspace, hub, contextPath, nodeTypePath)
            .Subscribe(
                models =>
                {
                    logger.LogDebug("[AgentChatClient] Model emission: {Count}", models.Count);
                    loadedModels = models.ToImmutableList();
                },
                ex => logger.LogDebug(ex, "[AgentChatClient] Model subscription faulted — picker dropdown will be empty"));

        return this;
    }

    private IDisposable? modelsSubscription;

    /// <summary>
    /// Stash agents from <see cref="AgentPickerProjection.ObserveAgents"/>
    /// into <see cref="loadedAgents"/> and rebuild the <see cref="agents"/>
    /// dictionary. Models are populated independently in their own
    /// subscription — see <c>Initialize</c>.
    /// </summary>
    private void ApplyAgents(
        IReadOnlyList<AgentDisplayInfo> agentInfos,
        string? contextPath)
    {
        loadedAgents = AgentOrderingHelper.OrderByRelevance(
            agentInfos.ToImmutableList(),
            contextPath?.TrimStart('/') ?? string.Empty,
            string.Empty).ToImmutableList();

        logger.LogInformation("[AgentChatClient] {Count} agents: [{Agents}]",
            loadedAgents.Count, string.Join(", ", loadedAgents.Select(a => a.Name)));

        agentsInitialized = false;
        agents = ImmutableDictionary<string, ChatClientAgent>.Empty;
        CreateAgentsSync();
    }


    // (Legacy LoadOrderedAgentsAsync removed — its 5 parallel QueryAsync calls
    // were the source of the chat-load deadlock. Agents are now sourced
    // exclusively from the workspace synced query subscription wired up in
    // Initialize. Anyone who wants the agent list reads loadedAgents.)

    /// <summary>
    /// Creates ChatClientAgent instances synchronously — no await, no deadlock.
    /// Uses CreateAgent (sync) on the factory which skips async reference resolution.
    /// </summary>
    private void CreateAgentsSync()
    {
        if (chatClientFactories.Count == 0)
        {
            lastAgentCreationError =
                "No IChatClientFactory is registered. Add e.g. AddAzureFoundryClaude / AddAzureOpenAI in your host configuration.";
            logger.LogWarning("[AgentChatClient] {Error}", lastAgentCreationError);
            return;
        }

        if (agentsInitialized) return;
        // Reset before this attempt — a previous failure shouldn't be surfaced
        // if the new attempt succeeds.
        lastAgentCreationError = null;

        // Per-agent factory selection. The agent's PreferredModel is the source
        // of truth (see ChatClientAgentFactory subclasses' CreateChatClient
        // precedence). currentModelName (the chat dropdown selection) is only
        // used when the agent doesn't pin a model. Selecting the factory for
        // each agent's effective model ensures we don't try to serve, e.g.,
        // an OpenAI model on an Azure Foundry factory.
        // isPersistentFactory tracks the default factory's persistence mode for
        // legacy paths that haven't been threaded through per-agent.
        var defaultFactory = GetFactoryForModel(currentModelName);
        if (defaultFactory == null)
        {
            lastAgentCreationError =
                $"No registered IChatClientFactory accepts model '{currentModelName ?? "(none selected)"}'. "
                + $"Configured factories: [{string.Join(", ", chatClientFactories.Select(f => f.Name))}].";
            logger.LogWarning("[AgentChatClient] {Error}", lastAgentCreationError);
            return;
        }
        isPersistentFactory = defaultFactory.IsPersistent;
        logger.LogInformation("[AgentChatClient] Default factory {FactoryName} for model {ModelName} (persistent={IsPersistent}); agents may select per-agent factories via PreferredModel",
            defaultFactory.Name, currentModelName ?? "default", isPersistentFactory);

        var configs = loadedAgents.Select(a => a.AgentConfiguration).ToImmutableList();
        var createdAgents = ImmutableDictionary<string, ChatClientAgent>.Empty;
        var orderedConfigs = OrderAgentsForCreation(configs);

        // 🚨 Per-agent try/catch is mandatory: factory misconfig (e.g. an
        // Azure Foundry endpoint not set) throws inside CreateAgent and used
        // to escape the synced-query Subscribe callback as an unhandled
        // exception that killed the portal process. Now we log + skip the
        // misconfigured agent so the rest of the catalog still loads and
        // SelectAgent can fall back to a working one.
        foreach (var agentConfig in orderedConfigs)
        {
            var effectiveModel = agentConfig.PreferredModel ?? currentModelName;
            var factory = GetFactoryForModel(effectiveModel) ?? defaultFactory;
            try
            {
                var agent = factory.CreateAgent(agentConfig, this, createdAgents, configs, effectiveModel);
                createdAgents = createdAgents.SetItem(agentConfig.Id, agent);
                agents = agents.SetItem(agentConfig.Id, agent);
            }
            catch (Exception ex)
            {
                lastAgentCreationError =
                    $"Failed to create agent '{agentConfig.Id}' via factory '{factory?.Name}' for model '{effectiveModel}': {ex.Message}";
                logger.LogWarning(ex,
                    "[AgentChatClient] Skipping agent {Agent} ({Factory}/{Model}): {Message}",
                    agentConfig.Id, factory?.Name, effectiveModel, ex.Message);
            }
        }

        var cyclicAgents = FindCyclicDelegations(configs);
        foreach (var agentConfig in cyclicAgents)
        {
            var effectiveModel = agentConfig.PreferredModel ?? currentModelName;
            var factory = GetFactoryForModel(effectiveModel) ?? defaultFactory;
            try
            {
                var updatedAgent = factory.CreateAgent(agentConfig, this, createdAgents, configs, effectiveModel);
                createdAgents = createdAgents.SetItem(agentConfig.Id, updatedAgent);
                agents = agents.SetItem(agentConfig.Id, updatedAgent);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[AgentChatClient] Skipping cyclic agent {Agent}: {Message}",
                    agentConfig.Id, ex.Message);
            }
        }

        agentsInitialized = true;
        logger.LogInformation("[AgentChatClient] Created {Count} agents", agents.Count);
    }

    /// <summary>
    /// Creates ChatClientAgent instances for all loaded configurations.
    /// </summary>
    [Obsolete("Use CreateAgentsSync — CreateAgentsAsync deadlocks in Orleans")]
    private async Task CreateAgentsAsync()
    {
        if (chatClientFactories.Count == 0)
        {
            logger.LogWarning("[AgentChatClient] No IChatClientFactory available, cannot create agents");
            return;
        }

        if (agentsInitialized)
            return;

        // Select the appropriate factory based on the requested model
        var factory = GetFactoryForModel(currentModelName);
        if (factory == null)
        {
            logger.LogWarning("[AgentChatClient] No factory can serve model: {ModelName}", currentModelName);
            throw new ArgumentException($"No factory can serve model: {currentModelName}");
        }

        isPersistentFactory = factory.IsPersistent;
        logger.LogInformation("[AgentChatClient] Using factory {FactoryName} for model {ModelName} (persistent={IsPersistent})",
            factory.Name, currentModelName ?? "default", isPersistentFactory);

        var configs = loadedAgents.Select(a => a.AgentConfiguration).ToImmutableList();
        var createdAgents = ImmutableDictionary<string, ChatClientAgent>.Empty;

        // Order agents: non-delegating first, delegating second, default last
        var orderedConfigs = OrderAgentsForCreation(configs);

        // First pass: Create all agents in order
        foreach (var agentConfig in orderedConfigs)
        {
            var agent = await factory.CreateAgentAsync(
                agentConfig, this, createdAgents, configs, currentModelName);
            createdAgents = createdAgents.SetItem(agentConfig.Id, agent);
            agents = agents.SetItem(agentConfig.Id, agent);
        }

        // Second pass: Update agents with cyclic dependencies
        var cyclicAgents = FindCyclicDelegations(configs);
        foreach (var agentConfig in cyclicAgents)
        {
            var updatedAgent = await factory.CreateAgentAsync(
                agentConfig, this, createdAgents, configs, currentModelName);
            createdAgents = createdAgents.SetItem(agentConfig.Id, updatedAgent);
            agents = agents.SetItem(agentConfig.Id, updatedAgent);
        }

        agentsInitialized = true;
        logger.LogInformation("[AgentChatClient] Created {Count} agents", agents.Count);
    }

    /// <summary>
    /// Finds the appropriate factory that can serve the requested model.
    /// </summary>
    private IChatClientFactory? GetFactoryForModel(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName))
        {
            // Return first factory ordered by Order
            return chatClientFactories
                .OrderBy(f => f.Order)
                .FirstOrDefault();
        }

        // Ask each factory whether it serves this model. Concrete factories
        // implement shape-aware predicates (e.g. AzureClaude → "claude-*",
        // AzureFoundry → catch-all for non-Claude). Falling back on Models[]
        // (the legacy mechanism) only matters for factories that haven't
        // overridden Supports — and Models[] is empty by default now since
        // model env-vars were removed in favour of agent-declared PreferredModel.
        var factory = chatClientFactories
            .OrderBy(f => f.Order)
            .FirstOrDefault(f => f.Supports(modelName));

        if (factory != null)
        {
            return factory;
        }

        // Fallback: return first factory
        logger.LogWarning("[AgentChatClient] Model {ModelName} not served by any factory, using first available", modelName);
        return chatClientFactories
            .OrderBy(f => f.Order)
            .FirstOrDefault();
    }

    /// <summary>
    /// Orders agents for creation: non-delegating first, delegating second, default last.
    /// </summary>
    internal static IEnumerable<AgentConfiguration> OrderAgentsForCreation(IEnumerable<AgentConfiguration> configs)
    {
        var agentList = configs.ToList();

        var nonDelegating = agentList
            .Where(a => (a.Delegations == null || a.Delegations.Count == 0) && !a.IsDefault);

        var delegating = agentList
            .Where(a => a.Delegations is { Count: > 0 } && !a.IsDefault);

        var defaultAgent = agentList.Where(a => a.IsDefault);

        return nonDelegating.Concat(delegating).Concat(defaultAgent);
    }

    /// <summary>
    /// Finds agents that have cyclic delegations.
    /// </summary>
    internal static IEnumerable<AgentConfiguration> FindCyclicDelegations(IEnumerable<AgentConfiguration> configs)
    {
        var delegatingAgents = configs.Where(a => a.Delegations is { Count: > 0 }).ToImmutableList();
        var cyclicAgents = ImmutableHashSet<string>.Empty;

        foreach (var agent in delegatingAgents)
        {
            var delegatedAgentPaths = agent.Delegations!.Select(d => d.AgentPath).ToImmutableHashSet();

            foreach (var delegatedPath in delegatedAgentPaths)
            {
                var delegatedId = delegatedPath.Split('/').Last();
                var delegatedAgent = delegatingAgents.FirstOrDefault(a => a.Id == delegatedId);

                if (delegatedAgent?.Delegations != null)
                {
                    var backDelegations = delegatedAgent.Delegations.Select(d => d.AgentPath.Split('/').Last()).ToImmutableHashSet();
                    if (backDelegations.Contains(agent.Id))
                    {
                        cyclicAgents = cyclicAgents.Add(agent.Id).Add(delegatedId);
                    }
                }
            }
        }

        return configs.Where(a => cyclicAgents.Contains(a.Id));
    }

    /// <summary>
    /// Returns the synced agent collection — populated by the workspace-level
    /// synced query subscription wired up in <see cref="Initialize"/>.
    /// If the explicit context has changed since the last init, re-binds the
    /// subscription to the new context's id (the workspace's per-id cache
    /// makes this cheap when warm-cached) and returns whatever the synced
    /// collection currently holds. Task-shaped only for
    /// <see cref="IAgentChat"/> compat — no awaits.
    /// </summary>
    public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync()
    {
        var currentContextPath = Context?.Address?.ToString();
        var currentNodeTypePath = Context?.Node?.NodeType;
        if (currentContextPath != null
            && (currentContextPath != lastLoadedContextPath
                || currentNodeTypePath != lastLoadedNodeTypePath))
            Initialize(currentContextPath, currentModelName, currentNodeTypePath);

        return Task.FromResult<IReadOnlyList<AgentDisplayInfo>>(loadedAgents);
    }

    public Task ResumeAsync(ChatConversation conversation)
    {
        // With AgentSession, we don't need to manually restore history
        // The thread already contains the conversation state
        return Task.CompletedTask;
    }

    public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl)
    {
        var layoutAreaContent = new ChatLayoutAreaContent(layoutAreaControl);
        queuedLayoutAreaContent = queuedLayoutAreaContent.Enqueue(layoutAreaContent);
    }

    public void RequestHandoff(HandoffRequest request)
    {
        pendingHandoff = request;
        logger.LogInformation("Handoff requested from {Source} to {Target}", request.SourceAgentName, request.TargetAgentName);
    }

    private bool IsAgentPath(string path)
    {
        // Check if the path matches a loaded agent's path or resolves to an agent ID
        return loadedAgents.Any(a =>
            (a.Path != null && a.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ||
            a.Name.Equals(path.Split('/').Last(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds a ChatMessage with mixed content: text + binary attachments (PDFs, images).
    /// If no binary attachments, returns a simple text-only message.
    /// </summary>
    private static ChatMessage BuildChatMessage(string text, ImmutableList<DataContent> binaryAttachments)
    {
        if (binaryAttachments.Count == 0)
            return new ChatMessage(ChatRole.User, text);

        var contents = new List<AIContent> { new TextContent(text) };
        contents.AddRange(binaryAttachments);
        return new ChatMessage(ChatRole.User, contents);
    }

    private void DetectAgentAttachments()
    {
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        if (currentAttachments is { Count: > 0 })
        {
            foreach (var path in currentAttachments)
            {
                var cleanPath = path.TrimStart('@');
                if (IsAgentPath(cleanPath))
                    builder.Add(cleanPath);
            }
        }
        agentAttachmentPaths = builder.ToImmutable();
    }

    private void DetectMessageAgentReferences(string? messageText)
    {
        firstMessageAgentPath = null;
        if (string.IsNullOrEmpty(messageText))
            return;

        var referencePaths = MarkdownReferenceExtractor.GetUniquePaths(messageText);
        foreach (var refPath in referencePaths)
        {
            if (IsAgentPath(refPath))
            {
                firstMessageAgentPath = refPath;
                return; // First agent wins
            }
        }
    }

    private string ExtractTextFromMessage(ChatMessage message)
    {
        // First check the Text property (set by simple string constructor)
        if (!string.IsNullOrEmpty(message.Text))
            return message.Text;

        // Fallback to Contents collection
        var textBuilder = new StringBuilder();
        foreach (var content in message.Contents)
        {
            if (content is TextContent textContent)
            {
                textBuilder.Append(textContent.Text);
            }
        }
        return textBuilder.ToString();
    }
}
