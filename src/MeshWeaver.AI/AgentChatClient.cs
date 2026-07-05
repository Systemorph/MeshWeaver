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
using MeshWeaver.ContentCollections;
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

/// <summary>
/// Default <c>IAgentChat</c> implementation. Selects an agent from the workspace's
/// synced agent collection, assembles the contextual system prompt, and streams the
/// model response — text, tool calls, layout-area content and agent handoffs — back to
/// the thread-execution layer while holding the in-memory conversation session.
/// </summary>
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
    // The user's explicit picker selection, kept as the FULL node PATH
    // ("AgenticPension/Agent/Datenextraktion"). Resolution prefers an exact path
    // match against loadedAgents so a space-scoped agent never collides with a
    // built-in of the same last segment; SelectionId.IdOf gives the bare-id fallback.
    private string? currentAgentPath;
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
    /// Set by <see cref="ApplyStaleModelFallback"/> when the chat-selected model no longer resolves
    /// (deleted from the catalog / provider unconfigured — the case that used to 404 the whole
    /// thread) and we transparently swap to the default model. Surfaced ONCE as a note prepended to
    /// the next response so the model swap is VISIBLE to the user ("model X unavailable — using
    /// default Y") instead of silently changing which model answered — then cleared. A genuinely
    /// unrecoverable model failure (no working default) is surfaced separately by
    /// <see cref="lastAgentCreationError"/> and the <c>ThreadExecution</c> error cell.
    /// </summary>
    private string? staleModelNotice;

    /// <summary>
    /// Formats the no-agent failure into an APPROPRIATE chat output — never a crash.
    /// The common cause is "no model configured" (every agent skipped via the unconfigured
    /// catch-all factory); surface that with a clear, actionable hint plus the raw detail.
    /// </summary>
    private static string FormatNoAgentError(string? detail)
    {
        detail ??= "No suitable agent found to handle the request.";
        var noModel = detail.Contains("must be configured", StringComparison.OrdinalIgnoreCase)
                      || detail.Contains("no model", StringComparison.OrdinalIgnoreCase)
                      || detail.Contains("no chat-client factory", StringComparison.OrdinalIgnoreCase)
                      || detail.Contains("no registered IChatClientFactory", StringComparison.OrdinalIgnoreCase);
        return noModel
            ? "⚠️ No AI model is available to run this request. Configure a language-model provider "
              + "(Settings → Language Models) and select a model, or switch to the Claude Code harness.\n\n"
              + $"_Details: {detail}_"
            : detail;
    }

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

    /// <summary>
    /// The current application context (node, path, address) the agent reasons about.
    /// Set via <c>SetContext</c>, which strips satellite path segments at the boundary.
    /// Null until a context is assigned.
    /// </summary>
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
    /// <c>DelegationPaths</c> dictionary (which keyed by transient
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

    /// <summary>
    /// Switches the active conversation to the given thread, resetting the shared
    /// in-memory session so the next response starts a fresh agent session.
    /// </summary>
    /// <param name="threadId">Identifier of the thread to switch to; must be non-empty.</param>
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
            sharedThread = await agent.CreateSessionAsync(persistentThreadId).ConfigureAwait(false);
            logger.LogInformation("Resumed persistent thread: {PersistentThreadId}", persistentThreadId);
            return sharedThread;
        }

        if (isPersistentFactory)
        {
            // For persistent factories without an existing thread, create a new server-side session
            sharedThread = await agent.CreateSessionAsync(currentThreadId).ConfigureAwait(false);
            persistentThreadId = currentThreadId;
            logger.LogInformation("Created new persistent thread: {PersistentThreadId}", persistentThreadId);
        }
        else
        {
            sharedThread = await agent.CreateSessionAsync().ConfigureAwait(false);
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

            var toolDocs = cachedToolDocs ?? await LoadToolDocumentationAsync().ConfigureAwait(false);
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

        // Turn repetition into a reusable Skill — PROACTIVE. The trigger is a repeating
        // user, so one-shot / utility agents (NodeInitializer, DescriptionWriter, …) simply
        // never fire it. Kept in the shared base prompt so every conversational agent offers it.
        messageText.AppendLine("# Turn repetition into a Skill (proactive)");
        messageText.AppendLine();
        messageText.AppendLine("When the user asks for the SAME multi-step task more than once (in this thread or across threads), proactively offer to save it as a reusable **Skill** — e.g. \"Want me to save this as a `/<name>` skill you can re-run anytime?\". Don't wait to be asked.");
        messageText.AppendLine();
        messageText.AppendLine("\"Create a skill\" means create a `nodeType:Skill` node (the same mechanism behind `/agent`, `/model`, `/harness`). Use `create` with `content` = a `SkillDefinition`:");
        messageText.AppendLine("- node **id** = the slash word (`/<id>`); **name** + **description** = its display name and help text.");
        messageText.AppendLine("- `Instructions` = a how-to (the SKILL.md body) the agent loads on demand — for \"run these steps\" skills — and/or `Action` = a behaviour (`Pick` a node by a query and write it to the composer, `OpenContent`, `Connect`/`Disconnect`).");
        messageText.AppendLine("- Place it under the user's own namespace + `/Skill` (private to them) or the Space's `{space}/Skill` (shared with everyone in that Space); the platform-wide catalog is `Skill`.");
        messageText.AppendLine("Once created, `/<id>` works in chat immediately — no code. Full SkillDefinition shape: `/Doc/AI/ChatCommands`.");
        messageText.AppendLine();

        // Dynamic part: context (changes per navigation)
        if (Context != null)
        {
            messageText.AppendLine("# Current Application Context");
            messageText.AppendLine();
            messageText.AppendLine(
                "The node, layout area, and query parameters the user is viewing right now (JSON, serialized with the mesh's standard options). This is a REFERENCE — load node CONTENT on demand with the Get tool; it is NOT inlined here.");
            messageText.AppendLine("```json");
            // 🎯 JSON-serialize the navigation context with the mesh's normal options: the OWNER
            // (main-node) address, the layout area + id, the optional query parameters as KVP, and
            // the current node's IDENTITY (path/type/name) — never its content (reference + tool-load).
            messageText.AppendLine(JsonSerializer.Serialize(new
            {
                address = Context.Address?.ToString() ?? Context.Context,
                area = Context.LayoutArea?.Area,
                areaId = Context.LayoutArea?.Id,
                parameters = Context.Parameters,
                node = Context.Node is null
                    ? null
                    : new { path = Context.Node.Path, nodeType = Context.Node.NodeType, name = Context.Node.Name }
            }, hub.JsonSerializerOptions));
            messageText.AppendLine("```");
            messageText.AppendLine();

            if (Context.Node != null)
                messageText.AppendLine("The current node already exists. To modify it, use Get then Update — do NOT Create it again.");
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
                            // Load binary from the node's default content collection (the
                            // `content:` UCR prefix addresses the default collection by design).
                            var effectivePath = nodePath ?? Context?.Path;
                            var stream = await contentService.GetContentAsync(
                                ContentCollectionsExtensions.DefaultCollectionName, fileName).ConfigureAwait(false);
                            if (stream != null)
                            {
                                using (stream)
                                {
                                    using var ms = new MemoryStream();
                                    await stream.CopyToAsync(ms).ConfigureAwait(false);
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
                    var content = await meshPlugin.Get($"@{cleanPath}").ConfigureAwait(false);
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
            text = await InlineReferenceResolver.ResolveAsync(text, hub, this).ConfigureAwait(false);
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
        var docs = await meshPlugin.Get("@Doc/AI/Tools/MeshPlugin").ConfigureAwait(false);

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
                content = await InlineReferenceResolver.ResolveAsync(content, hub, this).ConfigureAwait(false);
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


    /// <summary>
    /// Runs the selected agent over the given messages and yields the complete response
    /// messages (text, tool calls, queued layout-area content and any handoffs). Surfaces
    /// a chat-formatted error instead of throwing when no agent can be selected.
    /// </summary>
    /// <param name="messages">The conversation messages; the last one drives agent selection.</param>
    /// <param name="cancellationToken">Cancels the in-flight agent run.</param>
    /// <returns>An async stream of assistant <c>ChatMessage</c>s.</returns>
    public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Detect agent attachments (for context content filtering)
        DetectAgentAttachments();

        // Detect agent @references in message text (for selection override)
        var lastMessageText = messages.LastOrDefault() is { } last ? ExtractTextFromMessage(last) : null;
        DetectMessageAgentReferences(lastMessageText);

        // Pure in-memory lookup against the synced agent cache.
        // 🚨 Callers MUST await `WhenInitialized` before calling here on a
        // cold path — this method does not gate, because the gate must run
        // on a non-hub thread (Task.Run in ThreadExecution.ExecuteMessageAsync).
        // Awaiting WhenInitialized here would block the hub's ActionBlock if
        // GetResponseAsync runs on it.
        var agent = SelectAgent(messages.LastOrDefault());
        if (agent == null)
        {
            // Never crash — surface the real reason AS the chat output. The common case
            // is "no model configured" (every agent skipped via the unconfigured catch-all
            // factory), so make that actionable.
            yield return new ChatMessage(ChatRole.Assistant, FormatNoAgentError(lastAgentCreationError));
            yield break;
        }

        currentAgentName = agent.Name;

        // Surface a transparent stale-model fallback (selected model 404'd → default stepped in)
        // ONCE, so the swap is visible instead of silently changing which model answered.
        if (staleModelNotice is { } modelNotice)
        {
            staleModelNotice = null;
            yield return new ChatMessage(ChatRole.Assistant, modelNotice) { AuthorName = currentAgentName ?? "Assistant" };
        }

        // Get or create thread for this agent
        var thread = await GetOrCreateThreadAsync(agent).ConfigureAwait(false);

        // Build the user message with context and agent instructions
        var (userText, binaryParts) = await BuildMessageWithContextAsync(messages, currentAgentName).ConfigureAwait(false);
        currentAttachments = null; // Clear after use

        // Build ChatMessage with mixed content (text + binary attachments)
        var chatMessage = BuildChatMessage(userText, binaryParts);

        // Get response from the agent with thread
        var response = await agent.RunAsync(chatMessage, thread, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Save the updated thread
        _ = SaveThreadAsync(agent, thread); // no-op; method returns Task.CompletedTask

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
            var handoffResponse = await targetAgent.RunAsync(handoff.Message, thread, cancellationToken: cancellationToken).ConfigureAwait(false);
            _ = SaveThreadAsync(targetAgent, thread); // no-op; method returns Task.CompletedTask

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

    /// <summary>
    /// Streams the selected agent's response incrementally — text deltas, tool-call and
    /// tool-result content, token usage and handoffs — as they arrive from the model.
    /// Surfaces a chat-formatted error instead of throwing when no agent can be selected.
    /// </summary>
    /// <param name="messages">The conversation messages; the last one drives agent selection.</param>
    /// <param name="cancellationToken">Cancels the in-flight streaming run.</param>
    /// <returns>An async stream of <c>ChatResponseUpdate</c> deltas.</returns>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Detect agent attachments (for context content filtering)
        DetectAgentAttachments();

        // Detect agent @references in message text (for selection override)
        var lastMessageTextStreaming = messages.LastOrDefault() is { } lastMsg ? ExtractTextFromMessage(lastMsg) : null;
        DetectMessageAgentReferences(lastMessageTextStreaming);

        // 🚨 Callers MUST await `WhenInitialized` before invoking on a cold
        // path — gate is at the caller, not here, because awaiting would block
        // the hub ActionBlock if GetStreamingResponseAsync is called on it.
        var agent = SelectAgent(messages.LastOrDefault());
        if (agent == null)
        {
            // Never crash — surface the real reason AS the streamed chat output.
            yield return new ChatResponseUpdate(ChatRole.Assistant, FormatNoAgentError(lastAgentCreationError));
            yield break;
        }

        currentAgentName = agent.Name;

        // Surface a transparent stale-model fallback ONCE (see GetResponseAsync) so the user sees
        // the model swap rather than it changing silently underneath them.
        if (staleModelNotice is { } modelNotice)
        {
            staleModelNotice = null;
            yield return new ChatResponseUpdate(ChatRole.Assistant, modelNotice + "\n\n");
        }

        // Get or create thread for this agent
        var thread = await GetOrCreateThreadAsync(agent).ConfigureAwait(false);

        // Pass all messages as separate turns with system prompt prepended.
        // The agent's ChatClient includes FunctionInvokingChatClient for tool calls.
        var turnMessages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(agent.Instructions))
            turnMessages.Add(new ChatMessage(ChatRole.System, agent.Instructions));
        turnMessages.AddRange(messages);
        logger.LogDebug("[AgentChat] Sending {Count} messages (+ system) to {Agent}",
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
        // Mid-round inbox: inject check_inbox so the agent can drain follow-up messages
        // queued DURING the in-flight round and fold them inline into the current response.
        // This is the reactive two-stage channel the prior disable-note prescribed — NOT the
        // old TCS-on-hub-scheduler gate that deadlocked. Stage 1: the submission watcher
        // OFFERS newly-pending messages into the per-thread ThreadInboxChannel (in-memory,
        // no node write). Stage 2: check_inbox DRAINS that channel SYNCHRONOUSLY — no
        // stream.Update, no Subscribe onto the hub action-block scheduler — so the
        // TaskCompletionSource bridge can no longer resume a continuation on the hub thread
        // (the old deadlock/"thread disappears" cause is gone). The thread node is written
        // only at round boundaries (start commit + terminal fold). See ThreadInboxChannel.
        tools.Add(InboxTool.CreateCheckInboxTool(hub, logger));
        chatOptions.Tools = tools;
        // Tools marked [HiddenTool] are internal plumbing: their calls must not reach the chat
        // UI as tool-call chrome nor the Information logs. The filter stays generic; with the
        // inbox disabled there are currently no hidden tools, so it is a no-op. Collect their
        // names once; we drop the paired FunctionCallContent / FunctionResultContent below
        // instead of forwarding them to ThreadExecution.
        var hiddenToolNames = tools
            .OfType<AIFunction>()
            .Where(Attributes.HiddenToolAttribute.IsHidden)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);
        var hiddenCallIds = new HashSet<string>(StringComparer.Ordinal);
        // ConfigureAwait(false): keep the agent-stream iteration on the ThreadPool (the
        // IoPool's domain), never resuming on a captured hub/grain action-block scheduler.
        // This generator is consumed by ThreadExecution's round-streaming await foreach; a
        // captured hub context that isn't pumped under a 2-core runner stalls the round
        // (missed-observation deadlock). See ThreadExecution's STREAM await foreach.
        await foreach (var update in agent.ChatClient.GetStreamingResponseAsync(turnMessages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            // Forward the complete update with all contents (including FunctionCallContent)
            if (update.Contents.Count > 0)
            {
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent functionCall)
                    {
                        // Hidden tool (e.g. check_inbox): swallow the call entirely — no log,
                        // no UI tool-call chrome. Remember the CallId so the paired result is
                        // suppressed too. The tool still executes inside FunctionInvokingChatClient
                        // and its result still reaches the model; only the surfacing is dropped.
                        if (hiddenToolNames.Contains(functionCall.Name))
                        {
                            if (functionCall.CallId is { Length: > 0 } hiddenCallId)
                                hiddenCallIds.Add(hiddenCallId);
                            continue;
                        }

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
                        // Suppress the result of a hidden tool call (see FunctionCallContent above).
                        if (functionResult.CallId is { Length: > 0 } resultCallId
                            && hiddenCallIds.Contains(resultCallId))
                            continue;

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
        _ = SaveThreadAsync(agent, thread); // no-op; method returns Task.CompletedTask

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

            // Run target agent streaming on the same shared thread.
            // ConfigureAwait(false): same rule — never resume the handoff stream on a
            // captured hub scheduler (keep it on the ThreadPool / IoPool domain).
            await foreach (var update in targetAgent.RunStreamingAsync(handoff.Message, thread, cancellationToken: cancellationToken).ConfigureAwait(false))
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

            _ = SaveThreadAsync(targetAgent, thread); // no-op; method returns Task.CompletedTask

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

        // 3. Use the explicitly selected agent (from the dropdown / composer) if set.
        //    The picker stores the FULL node PATH, so resolve by path FIRST: a
        //    space-scoped agent ("AgenticPension/Agent/Datenextraktion") must never be
        //    confused with a built-in sharing its last segment. Bare-id is the fallback
        //    (legacy bare-name selections + built-ins picked by id).
        //    🚨 An EXPLICIT-but-unresolvable selection does NOT silently fall through to
        //    a different agent (steps 4-6): that produced the wrong-agent / NotFound
        //    confusion in prod. Surface a clear, named error instead — GetResponseAsync /
        //    GetStreamingResponseAsync render lastAgentCreationError as the chat output.
        if (!string.IsNullOrEmpty(currentAgentPath) || !string.IsNullOrEmpty(currentAgentName))
        {
            var explicitlySelected = ResolveSelectedAgent(out var selectionMatched);
            if (explicitlySelected != null)
                return explicitlySelected;

            // 🚨 "Not found" is TRUE only when the selection matched no loaded agent.
            // When the agent WAS matched but could not be built (no factory for the
            // model, factory threw, config missing), ResolveSelectedAgent has already
            // set the TRUTHFUL lastAgentCreationError — overwriting it here with
            // "agent not found" masked the real failure ("no chat-client factory for
            // model X") behind a message that contradicted the printed agent list
            // (the 2026-07-01 e2e-portal symptom: 'Agent/Assistant' reported as not
            // found while first in the list).
            if (!selectionMatched)
            {
                var requested = currentAgentPath ?? currentAgentName;
                // 🚨 Distinguish "stale selection" from "EMPTY catalog" (#201). With zero
                // loaded agents there is nothing to "pick from the list" — blaming the
                // selection ("moved or renamed — pick another") dead-ends the user. An
                // empty catalog means the agent query returned nothing for THIS hub's
                // identity/context (still converging on a cold start, or agents not
                // visible to this principal) — say that, and that a retry can succeed
                // once the catalog emits. The stale-selection wording stays reserved for
                // the non-empty case, where picking another agent is real advice.
                // (The root-cause of the empty catalog — a lost synced-query Initial
                // gate — is fixed in SyncedQueryMeshNodes; this message covers the
                // residual "genuinely no agents visible" case.)
                lastAgentCreationError = loadedAgents.Count == 0
                    ? $"Selected agent '{requested}' could not be validated: the agent "
                      + "catalog is empty — no agents are loaded in this context. Either "
                      + "the agent query has not emitted yet (cold start — retry the "
                      + "message) or no agents are visible to this identity/context. If "
                      + "this persists, verify agents exist under the 'Agent', "
                      + "'{space}/Agent' or '{user}/Agent' namespaces and are readable "
                      + "in this context."
                    : $"Selected agent '{requested}' was not found among the available agents "
                      + $"([{string.Join(", ", loadedAgents.Select(a => a.Path ?? a.Name))}]). "
                      + "It may have been moved, renamed, or is not available in this context — "
                      + "pick another agent from the list.";
                logger.LogWarning("[AgentChatClient] {Error}", lastAgentCreationError);
            }
            return null;
        }

        // 4. Prefer the configuration-marked default agent (IsDefault=true).
        //    🚨 Without this, the fallback below routes to loadedAgents[0]
        //    which depends on AgentOrderingHelper.OrderByRelevance — order
        //    can vary across runs because the synced query's emission timing
        //    is non-deterministic. Concrete failure: SubThreadHangRepro's
        //    second [Fact] routed to NodeInitializer instead of the
        //    Assistant default, sending the test through
        //    HangingSubAgentChatClient (which hangs forever) instead of
        //    DelegatingParentChatClient. Symptom: STREAM_BEGIN logs, then
        //    no further activity — first MoveNext on the inner client never
        //    returns.
        var defaultAgentInfo = loadedAgents.FirstOrDefault(a => a.AgentConfiguration?.IsDefault == true);
        if (defaultAgentInfo is not null && agents.TryGetValue(defaultAgentInfo.Name, out var defaultAgent))
            return defaultAgent;

        // 5. Use the synced ordered list — best match for context, kept fresh
        // by the workspace synced query (see Initialize).
        if (loadedAgents.Count > 0)
        {
            var bestAgent = loadedAgents[0];
            if (agents.TryGetValue(bestAgent.Name, out var agent))
                return agent;
        }

        // 6. Return first agent as fallback
        return agents.Values.FirstOrDefault();
    }

    /// <summary>
    /// Resolves the user's explicit picker selection (<see cref="currentAgentPath"/> /
    /// <see cref="currentAgentName"/>) to a <see cref="ChatClientAgent"/>, or null when the
    /// selection matches no loaded agent OR the matched agent cannot be built.
    /// <para>Match order: exact FULL PATH against <see cref="loadedAgents"/> first (so a
    /// space-scoped agent is never confused with a built-in sharing its last segment),
    /// then bare id. The created-agents dictionary is keyed by the bare id
    /// (<see cref="AgentConfiguration.Id"/>) because delegation/hand-off resolve by id —
    /// so when two loaded agents share a last segment (a built-in and a space override),
    /// the dictionary holds only one. When the dictionary entry is NOT the path-matched
    /// config (a genuine collision), build the right agent on demand from the matched
    /// config so the user gets the agent they actually picked.</para>
    /// <para><paramref name="selectionMatched"/> reports whether the selection matched a
    /// loaded agent at all. On every matched-but-unbuildable path this method sets a
    /// TRUTHFUL <see cref="lastAgentCreationError"/> naming the agent AND the real
    /// failure (missing factory / model, factory exception, missing configuration) —
    /// the caller must NOT overwrite it with the "agent not found" message, which is
    /// reserved for a genuinely unmatched selection.</para>
    /// </summary>
    private ChatClientAgent? ResolveSelectedAgent(out bool selectionMatched)
    {
        // a) Exact full-path match (the picker stores the node path).
        var matched = !string.IsNullOrEmpty(currentAgentPath)
            ? loadedAgents.FirstOrDefault(a =>
                string.Equals(a.Path, currentAgentPath, StringComparison.OrdinalIgnoreCase))
            : null;

        // b) Bare-id match (legacy bare-name selection, or a built-in picked by id).
        matched ??= !string.IsNullOrEmpty(currentAgentName)
            ? loadedAgents.FirstOrDefault(a =>
                string.Equals(a.Name, currentAgentName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.AgentConfiguration?.Id, currentAgentName, StringComparison.OrdinalIgnoreCase))
            : null;

        selectionMatched = matched is not null;
        if (matched is null)
            return null; // genuinely unmatched — the caller reports "not found".

        var selectedLabel = matched.Path ?? matched.Name;
        if (matched.AgentConfiguration is not { } config)
        {
            // The agent WAS matched — a "not found" message here would be a lie. The node
            // simply carries no runnable configuration (content missing / failed to deserialize).
            lastAgentCreationError =
                $"Selected agent '{selectedLabel}' was found but carries no agent configuration "
                + "(the node's content is missing or failed to deserialize) — it cannot be run. "
                + "Fix or re-import the agent definition, or pick another agent.";
            logger.LogWarning("[AgentChatClient] {Error}", lastAgentCreationError);
            return null;
        }

        // The created-agents dict is keyed by bare id. The common (no-collision) case:
        // the dict entry IS this config — return it.
        if (agents.TryGetValue(config.Id, out var existing)
            && string.Equals(existing.Instructions, config.Instructions, StringComparison.Ordinal))
            return existing;

        // Collision (or the agent was skipped during the batch build — e.g. the whole
        // batch failed because no factory/model resolves): construct the path-matched
        // agent on demand so the selection resolves to the RIGHT one rather than
        // whichever same-id agent won the dictionary slot.
        var factory = GetFactoryForModel(currentModelName);
        if (factory == null)
        {
            if (existing != null)
                return existing; // best effort: serve the same-id agent already built.
            // Matched agent, no factory, nothing built: the failure is MODEL/FACTORY
            // resolution — say exactly that (never "agent not found").
            lastAgentCreationError = DescribeFactoryResolutionFailure(selectedLabel);
            logger.LogWarning("[AgentChatClient] {Error}", lastAgentCreationError);
            return null;
        }

        try
        {
            return factory.CreateAgent(config, this, agents,
                loadedAgents.Select(a => a.AgentConfiguration).ToImmutableList(), currentModelName);
        }
        catch (Exception ex)
        {
            lastAgentCreationError =
                $"Selected agent '{selectedLabel}' was found, but creating it failed via factory "
                + $"'{factory.Name}' for model '{currentModelName ?? "(none selected)"}': {ex.Message}";
            logger.LogWarning(ex, "[AgentChatClient] {Error}", lastAgentCreationError);
            return existing;
        }
    }

    /// <summary>
    /// Actionable description of a factory/model resolution failure for a MATCHED agent:
    /// names the agent, the selected model, and every configured factory (with its
    /// declared models) so the operator can see which piece of the chain is missing.
    /// </summary>
    private string DescribeFactoryResolutionFailure(string? selectedLabel)
    {
        var factories = chatClientFactories.Count == 0
            ? "(none registered — add e.g. AddOpenAICompatible / AddAzureFoundry / AddAnthropic in the host configuration)"
            : string.Join(", ", chatClientFactories
                .OrderBy(f => f.Order)
                .Select(f => f.Models is { Count: > 0 } models
                    ? $"{f.Name} (models: {string.Join(", ", models)})"
                    : f.Name));
        return
            $"Agent '{selectedLabel}' matched but no chat-client factory resolves model "
            + $"'{currentModelName ?? "(none selected)"}' — available factories/models: [{factories}]. "
            + "Configure a language-model provider (Settings → Language Models) or select a different model.";
    }

    /// <inheritdoc />
    public void SetSelectedAgent(string? agentName)
    {
        // The picker stores the node PATH ("Agent/Assistant",
        // "AgenticPension/Agent/Datenextraktion"); a bare name is also accepted.
        // Keep BOTH forms: the full path drives an exact-path match in SelectAgent
        // (so a space-scoped agent isn't confused with a built-in sharing its last
        // segment), and the bare id is the fallback / dictionary key.
        currentAgentPath = string.IsNullOrEmpty(agentName) ? null : agentName;
        currentAgentName = SelectionId.IdOf(agentName);
    }

    /// <summary>
    /// Sets the application context for the agent, normalising away satellite path
    /// segments, and re-initialises the synced-agent subscription when the context
    /// node's NodeType changes (so NodeType-scoped agents surface).
    /// </summary>
    /// <param name="applicationContext">The new context, or null to clear it.</param>
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
    /// Uses Query (reactive) — no await, no blocking, no deadlock.
    /// Subscribe to this and chain the streaming loop after it emits.
    /// </summary>
    /// <summary>
    /// Returns an IObservable that emits the initialized AgentChatClient when agents are ready.
    /// Re-emits when agent definitions change (system prompt updates, new agents added).
    /// Uses Query (reactive) — no await, no blocking, no deadlock.
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
        // The model arrives as the picked node PATH (the composer's persisted form,
        // "Provider/{provider}/{modelId}"). Resolve it to the model's REGISTERED ModelDefinition.Id
        // via the credential resolver's node-path lookup — NOT SelectionId.IdOf's last-segment
        // heuristic. IdOf over-strips a model whose id ITSELF contains '/' (an org/model slug like
        // "z-ai/glm-5.2" → "glm-5.2"), so it no longer Resolve()s and the round falls back to the
        // default while telling the user the model is "unavailable" — for a model that is configured
        // (the memex.meshweaver.cloud "glm-5.2 unavailable → z-ai/glm-5.2" report). ResolveModelId
        // returns the value unchanged for a bare id or a path it can't find, so IdOf is only the
        // last-ditch fallback when the resolver service is absent.
        currentModelName = hub.ServiceProvider.GetService<ChatClientCredentialResolver>()?.ResolveModelId(modelName)
                           ?? SelectionId.IdOf(modelName);
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
        // does its own Query" chain that drifted from the picker and
        // produced "No suitable agent" even though the dropdown showed 9 of
        // them. The synced query runs on the workspace of THIS hub (the thread
        // hub passed in via the ctor's service provider), not the _Exec child
        // — _Exec is blocked by the streaming Task.Run.
        var workspace = hub.GetWorkspace();
        // The chatting user's home namespace — where a user drops their OWN agents, surfaced via the
        // namespace:{userHome} alternation in AgentPickerProjection.BuildAgentQuery. Skips system/hub
        // principals (they own no user namespace). Safe even for guests: it's a namespace MEMBERSHIP
        // filter value, never a point-read, so a no-match is a silent no-op (unlike the model-selection
        // point-read that would storm a guest partition).
        var userHome = ResolveAgentUserHome(hub);
        agentsSubscription?.Dispose();
        modelsSubscription?.Dispose();
        selectionSubscription?.Dispose();

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
        agentsSubscription = AgentPickerProjection
            .ObserveAgents(hub, userHome, AgentPickerProjection.PartitionOf(contextPath))
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
                    // AFTER ApplyAgents — CreateAgentsSync resets
                    // lastAgentCreationError at the start of each attempt, so
                    // recording the fault first would be wiped. Surfacing the
                    // REAL load failure here means the chat output shows why
                    // no agents are available instead of a stale-selection
                    // "not found" message (issue #201).
                    lastAgentCreationError = $"Agent loading failed: {ex.Message}";
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

        // Provider selection drives BOTH the model picker and the resolver's
        // use-without-see watches. Subscribe to the user's selection node; each
        // change rebuilds the model subscription + resolver watches.
        // StartWith(empty) means the picker loads immediately with the default
        // set (root + context + nodeType) — byte-for-byte the previous behaviour
        // for users who never touched the selection picker, so no regression.
        // .Catch keeps a selection-read failure from breaking the picker.
        // (Same "no Timeout fallback" rule as agents: the synced query emits
        // Initial then quiesces; a Timeout wrapper would wipe loadedModels.)
        var accessService = hub.ServiceProvider.GetService<MeshWeaver.Messaging.AccessService>();
        var selectionContext = accessService?.Context;
        var selectionUserId = selectionContext?.ObjectId;
        // 🚨 Guests (VUser / IsVirtual identities) own NO ModelProvider or
        // LanguageModel nodes — they consume the root + shared catalog only.
        // Watching a guest's partition makes ChatClientCredentialResolver.ReadSnapshot
        // fan out `namespace:{VUser/id}/_Memex scope:descendants` per guest
        // session: a descendants walk on the `vuser` schema that returns nothing
        // yet pins a DB connection (ListChildPaths + node reads). With many
        // concurrent guests that storms the connection pool to exhaustion
        // ("pool exhausted, currently 50" — prod 2026-06-04). Only real
        // users/spaces have their own providers; guests use the default catalog.
        var watchOwnProviders = ShouldWatchOwnProviderPartition(selectionContext);
        var credentialResolver = hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
        if (watchOwnProviders)
            credentialResolver?.WatchPartition(selectionUserId!);

        selectionSubscription?.Dispose();
        // 🚨 Read the user's provider selection via a QUERY, never a point
        // `GetMeshNodeStream(SelectionPath)` node-access. A point-subscribe to a
        // node that does NOT exist — every PRE-EXISTING user partition that
        // predates the `_Selection` seed (ModelProviderNodeType) — routes to a
        // RoutingGrain `NotFound` DeliveryFailure + SYNC_STREAM `OnError`.
        // Initialize re-runs on every agent/model rebuild during streaming, so
        // the failing subscribe is re-issued in a tight loop: the resubscribe-
        // storm that starved the `portal/<user>` action block until unrelated
        // SubscribeRequests went stale >30s and the circuit FROZE (2026-06-09).
        // A `GetQuery` over the namespace returns an EMPTY set when the selection
        // node is absent (the documented "empty ⇒ default catalog" behaviour) —
        // no NotFound, no resubscribe, nothing to storm. GetQuery returns typed
        // Content (deserialised through this hub's options), so
        // ExtractSelectedProviderPaths reads `node.Content` directly. Seeding the
        // node only ever covered NEW users; querying fixes the whole class.
        var selectionStream = !watchOwnProviders
            ? Observable.Return(ImmutableArray<string>.Empty)
            : workspace.GetQuery(
                    $"{ModelProviderNodeType.SelectionNodeType}|{selectionUserId}",
                    $"namespace:{ModelProviderNodeType.UserNamespacePath(selectionUserId!)} nodeType:{ModelProviderNodeType.SelectionNodeType}")
                .Select(nodes => ExtractSelectedProviderPaths(nodes.FirstOrDefault()))
                .Catch<ImmutableArray<string>, Exception>(_ => Observable.Return(ImmutableArray<string>.Empty))
                .StartWith(ImmutableArray<string>.Empty)
                .DistinctUntilChanged(SelectedPathsComparer.Instance);

        selectionSubscription = selectionStream.Subscribe(selectedPaths =>
        {
            // Resolver: make each selected (org/shared) provider usable under
            // use-without-see — system-identity ingest + per-user Read gate.
            if (!string.IsNullOrEmpty(selectionUserId) && credentialResolver != null)
                foreach (var p in selectedPaths)
                    credentialResolver.WatchSharedProvider(p, selectionUserId);

            // Picker: (re)subscribe models to include the selected subtrees AND
            // the user's own {user}/_Memex provider/model nodes (userPath).
            modelsSubscription?.Dispose();
            modelsSubscription = AgentPickerProjection
                .ObserveModels(workspace, hub, contextPath, nodeTypePath, selectedPaths, selectionUserId)
                .Subscribe(
                    models =>
                    {
                        logger.LogDebug("[AgentChatClient] Model emission: {Count} (selected={Sel})",
                            models.Count, selectedPaths.Length);
                        loadedModels = models.ToImmutableList();
                    },
                    ex => logger.LogDebug(ex, "[AgentChatClient] Model subscription faulted — picker dropdown will be empty"));
        });

        return this;
    }

    private IDisposable? modelsSubscription;
    private IDisposable? selectionSubscription;

    /// <summary>Extracts the user's selected provider paths from the selection node (tolerates JsonElement content + absent node).</summary>
    private ImmutableArray<string> ExtractSelectedProviderPaths(MeshNode? node)
    {
        var sel = node?.Content switch
        {
            ModelProviderSelection s => s,
            System.Text.Json.JsonElement je => TryDeserializeSelection(je),
            _ => null,
        };
        if (sel is null) return ImmutableArray<string>.Empty;
        var arr = sel.SelectedProviderPaths;
        return arr.IsDefault ? ImmutableArray<string>.Empty : arr;
    }

    private ModelProviderSelection? TryDeserializeSelection(System.Text.Json.JsonElement je)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<ModelProviderSelection>(je.GetRawText(), hub.JsonSerializerOptions); }
        catch { return null; }
    }

    private sealed class SelectedPathsComparer : IEqualityComparer<ImmutableArray<string>>
    {
        public static readonly SelectedPathsComparer Instance = new();
        public bool Equals(ImmutableArray<string> x, ImmutableArray<string> y) => x.SequenceEqual(y);
        public int GetHashCode(ImmutableArray<string> obj)
        {
            var hc = new HashCode();
            foreach (var s in obj) hc.Add(s);
            return hc.ToHashCode();
        }
    }

    /// <summary>
    /// Stash agents from <see cref="AgentPickerProjection.ObserveAgents"/>
    /// into <see cref="loadedAgents"/> and rebuild the <see cref="agents"/>
    /// dictionary. Models are populated independently in their own
    /// subscription — see <c>Initialize</c>.
    /// <para>Internal so the selection/resolution tests can drive the exact
    /// production code path (the synced-query Subscribe callback calls this)
    /// without standing up a mesh + synced query.</para>
    /// </summary>
    internal void ApplyAgents(
        IReadOnlyList<AgentDisplayInfo> agentInfos,
        string? contextPath)
    {
        loadedAgents = AgentOrderingHelper.OrderByRelevance(
            agentInfos.ToImmutableList(),
            contextPath?.TrimStart('/') ?? string.Empty,
            string.Empty).ToImmutableList();

        logger.LogDebug("[AgentChatClient] {Count} agents: [{Agents}]",
            loadedAgents.Count, string.Join(", ", loadedAgents.Select(a => a.Name)));

        // 🚨 Do NOT pre-wipe `agents` here. CreateAgentsSync builds the new
        // dict LOCALLY and atomic-swaps at the end (see "Atomic publish"
        // comment in CreateAgentsSync). Wiping here would leave `agents`
        // empty for the entire rebuild window — concurrent SelectAgent
        // calls would return null and the request would surface as
        // "No suitable agent found to handle the request." (the
        // SubThreadHangRepro second-Fact symptom). Keep the OLD dict in
        // place; readers see EITHER the old full dict OR the new full
        // dict, never an empty intermediate.
        agentsInitialized = false;
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

        // 🩹 Self-heal a stale / deleted pinned model. The composer can carry a model id that no
        // longer resolves to a live LanguageModel (catalog refactor, deleted provider) — building a
        // chat client for it 404s the WHOLE thread (atioz sglauser / rsalzmann threads). When the
        // selected model doesn't resolve, fall back to the DEFAULT available model so the thread runs
        // on the default instead of crashing.
        ApplyStaleModelFallback();

        // Factory selection from the chat dropdown selection (currentModelName) — the
        // single source of truth for the model, independent of the agent. Selecting the
        // factory for that model ensures we don't try to serve, e.g., an OpenAI model on
        // an Azure Foundry factory.
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
        logger.LogDebug("[AgentChatClient] Factory {FactoryName} for chat-selected model {ModelName} (persistent={IsPersistent})",
            defaultFactory.Name, currentModelName ?? "default", isPersistentFactory);

        var configs = loadedAgents.Select(a => a.AgentConfiguration).ToImmutableList();
        var createdAgents = ImmutableDictionary<string, ChatClientAgent>.Empty;
        var orderedConfigs = OrderAgentsForCreation(configs);

        // 🛑 Anti-storm: a GLOBAL build failure (e.g. "No model selected" — no model is
        // configured) throws IDENTICALLY for every agent, so the per-agent catch below would
        // log the same warning once per agent (≈22 lines in ~1ms = the log storm). De-dupe by
        // message: log each distinct build error ONCE per build. lastAgentCreationError still
        // carries the latest message to the GUI regardless.
        var loggedBuildErrors = new HashSet<string>(StringComparer.Ordinal);

        // 🚨 Build the dict LOCALLY, then ATOMICALLY swap into `agents` at
        // the end. The previous shape mutated the shared `agents` field per
        // iteration (one-by-one SetItem) — every concurrent SelectAgent
        // saw a PARTIAL dict, biased toward agents added first
        // (Researcher, DescriptionWriter, …). The default
        // Assistant is added LAST per `OrderAgentsForCreation`, so during
        // the window, SelectAgent's `loadedAgents[0]` lookup (which finds
        // "Assistant" in loadedAgents) ran `agents.TryGetValue("Assistant")`
        // → false → fell through to `agents.Values.FirstOrDefault()` →
        // returned a non-default agent. Concrete failure:
        // SubThreadHangRepro's second [Fact] routed to DescriptionWriter
        // (HangingSubAgentChatClient) instead of Assistant
        // (DelegatingParentChatClient) — hung forever.
        //
        // Per-agent try/catch is mandatory: factory misconfig (e.g. an
        // Azure Foundry endpoint not set) throws inside CreateAgent and used
        // to escape the synced-query Subscribe callback as an unhandled
        // exception that killed the portal process. Now we log + skip the
        // misconfigured agent so the rest of the catalog still loads and
        // SelectAgent can fall back to a working one.
        foreach (var agentConfig in orderedConfigs)
        {
            // Model is the chat composer's selection — fully independent of the agent.
            var effectiveModel = currentModelName;
            var factory = GetFactoryForModel(effectiveModel) ?? defaultFactory;
            try
            {
                var agent = factory.CreateAgent(agentConfig, this, createdAgents, configs, effectiveModel);
                createdAgents = createdAgents.SetItem(agentConfig.Id, agent);
            }
            catch (Exception ex)
            {
                lastAgentCreationError =
                    $"Failed to create agent '{agentConfig.Id}' via factory '{factory?.Name}' for model '{effectiveModel}': {ex.Message}";
                // Only log a given underlying error message ONCE per build — a global
                // failure (e.g. "No model selected") repeats per agent and would storm.
                if (loggedBuildErrors.Add(ex.Message))
                    logger.LogWarning(ex,
                        "[AgentChatClient] Skipping agent {Agent} ({Factory}/{Model}): {Message}",
                        agentConfig.Id, factory?.Name, effectiveModel, ex.Message);
            }
        }

        var cyclicAgents = FindCyclicDelegations(configs);
        foreach (var agentConfig in cyclicAgents)
        {
            // Model is the chat composer's selection — fully independent of the agent.
            var effectiveModel = currentModelName;
            var factory = GetFactoryForModel(effectiveModel) ?? defaultFactory;
            try
            {
                var updatedAgent = factory.CreateAgent(agentConfig, this, createdAgents, configs, effectiveModel);
                createdAgents = createdAgents.SetItem(agentConfig.Id, updatedAgent);
            }
            catch (Exception ex)
            {
                // Same per-build de-dupe as the main loop above.
                if (loggedBuildErrors.Add(ex.Message))
                    logger.LogWarning(ex,
                        "[AgentChatClient] Skipping cyclic agent {Agent}: {Message}",
                        agentConfig.Id, ex.Message);
            }
        }

        // Atomic publish — readers see EITHER the previous full dict OR the
        // new full dict, never a half-built one.
        agents = createdAgents;
        agentsInitialized = true;
        logger.LogDebug("[AgentChatClient] Created {Count} agents", agents.Count);
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
        logger.LogDebug("[AgentChatClient] Using factory {FactoryName} for model {ModelName} (persistent={IsPersistent})",
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
        logger.LogDebug("[AgentChatClient] Created {Count} agents", agents.Count);
    }

    /// <summary>
    /// If the chat-selected model (<see cref="currentModelName"/>) no longer resolves to a live model
    /// — deleted / refactored out of the catalog so its provider endpoint 404s — swap it for the
    /// DEFAULT available model (lowest-Order resolvable model in the live catalog). Best-effort and
    /// fully synchronous: reads the credential resolver's warm snapshot. No-ops when no resolver is
    /// registered, the selected model already resolves, or no working default exists (so deployments
    /// whose models bypass the catalog are unaffected).
    /// </summary>
    private void ApplyStaleModelFallback()
    {
        var resolver = hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
        if (resolver is null)
            return;
        // The selected model already resolves → nothing to heal. (A non-empty, resolvable selection.)
        if (!string.IsNullOrEmpty(currentModelName)
            && resolver.Resolve(currentModelName!) != CredentialResolution.Missing)
            return;
        // Either NO model is selected, or the selected one no longer resolves (deleted/refactored out of
        // the catalog, or its provider is unconfigured). Seed the DEFAULT available model (lowest-Order
        // resolvable) so the round runs on a working model instead of dead-ending on the first factory
        // (OpenAI) with "(none selected)". When nothing resolves, `fallback` is empty → currentModelName
        // stays as-is → the round surfaces the clear "No AI model available" message (FormatNoAgentError).
        var fallback = resolver.ResolveDefaultModelId();
        if (string.IsNullOrEmpty(fallback)
            || string.Equals(fallback, currentModelName, StringComparison.OrdinalIgnoreCase))
            return;
        var stale = currentModelName;
        currentModelName = fallback;
        // Only NOTIFY the user when we swapped AWAY from a model they had explicitly pinned. Seeding a
        // default for an empty selection is silent — there was no user choice to override.
        if (!string.IsNullOrEmpty(stale))
        {
            logger.LogWarning(
                "[AgentChatClient] Selected model '{Stale}' does not resolve to a live model; "
                + "falling back to default model '{Default}'.", stale, fallback);
            // Surface the swap (GetResponseAsync / GetStreamingResponseAsync emit it once). Without this
            // the model silently changes under the user — they think their pinned model answered when it
            // actually 404'd and the default stepped in.
            staleModelNotice =
                $"*The selected model `{stale}` is unavailable — it may have been removed from the catalog "
                + $"or its provider is no longer configured. Using the default model `{fallback}` for this response.*";
        }
        else
        {
            logger.LogDebug(
                "[AgentChatClient] No model was selected; seeding default model '{Default}'.", fallback);
        }
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
        // model env-vars were removed in favour of the chat composer selection.
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
    /// Whether a chat client should watch its caller's OWN partition for
    /// ModelProvider / LanguageModel nodes. True only for a real, non-virtual
    /// identity. Guests (<c>VUser</c> / <see cref="MeshWeaver.Messaging.AccessContext.IsVirtual"/>)
    /// own no providers — they consume the root + shared catalog — so watching
    /// their partition fans out a per-session <c>namespace:{VUser/id}/_Memex
    /// scope:descendants</c> query against the <c>vuser</c> schema that returns
    /// nothing yet pins a DB connection; with many concurrent guests that storms
    /// the connection pool to exhaustion (prod 2026-06-04).
    /// </summary>
    internal static bool ShouldWatchOwnProviderPartition(MeshWeaver.Messaging.AccessContext? context)
        => !string.IsNullOrEmpty(context?.ObjectId) && context.IsVirtual != true;

    /// <summary>
    /// The chatting user's home namespace — the namespace under which they place their OWN agents,
    /// fed to <see cref="AgentPickerProjection.BuildAgentQuery"/> as the per-user alternation value.
    /// Mirrors the chat view's <c>ResolveUserHome</c>: the AccessContext ObjectId, skipping the
    /// system identity and hub principals (which own no user namespace).
    /// </summary>
    private static string? ResolveAgentUserHome(IMessageHub hub)
    {
        var accessSvc = hub.ServiceProvider.GetService<MeshWeaver.Messaging.AccessService>();
        if (accessSvc is null) return null;
        foreach (var candidate in new[] { accessSvc.Context?.ObjectId, accessSvc.CircuitContext?.ObjectId })
            if (!string.IsNullOrEmpty(candidate)
                && candidate != MeshWeaver.Mesh.Security.WellKnownUsers.System
                && !MeshWeaver.Messaging.AccessService.LooksLikeHubPrincipal(candidate))
                return candidate;
        return null;
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

    /// <summary>
    /// Resumes a prior conversation. A no-op under the in-memory <c>AgentSession</c>
    /// model: the thread already carries the conversation state, so nothing is restored.
    /// </summary>
    /// <param name="conversation">The conversation to resume.</param>
    /// <returns>A completed task.</returns>
    public Task ResumeAsync(ChatConversation conversation)
    {
        // With AgentSession, we don't need to manually restore history
        // The thread already contains the conversation state
        return Task.CompletedTask;
    }

    /// <summary>
    /// Queues a layout-area control to be emitted as assistant content once the current
    /// agent response completes.
    /// </summary>
    /// <param name="layoutAreaControl">The layout-area control to display in the chat.</param>
    public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl)
    {
        var layoutAreaContent = new ChatLayoutAreaContent(layoutAreaControl);
        queuedLayoutAreaContent = queuedLayoutAreaContent.Enqueue(layoutAreaContent);
    }

    /// <summary>
    /// Records a pending handoff to another agent; it is processed at the end of the
    /// current response, transferring the shared thread to the target agent.
    /// </summary>
    /// <param name="request">The handoff request (source agent, target agent and message).</param>
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
