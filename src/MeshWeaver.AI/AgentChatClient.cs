using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Layout;
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
    private readonly IChatPersistenceService persistenceService;
    private readonly IMeshQuery? meshQuery;
    private readonly IReadOnlyList<IChatClientFactory> chatClientFactories;
    private readonly Dictionary<string, ChatClientAgent> agents = new();
    private readonly Queue<ChatLayoutAreaContent> queuedLayoutAreaContent = new();
    private HandoffRequest? pendingHandoff;
    private List<AgentDisplayInfo> loadedAgents = [];
    private string? lastLoadedContextPath;
    private string currentThreadId = Guid.NewGuid().AsString();
    private string? currentAgentName;
    private AgentSession? sharedThread;
    private string? currentModelName;
    private string? persistentThreadId;
    private IReadOnlyList<string>? currentAttachments;
    private bool isPersistentFactory;
    private bool agentsInitialized;

    // Tracks which attachment paths are agent nodes (for context filtering)
    private HashSet<string>? agentAttachmentPaths;

    // Tracks the first agent found in @references in the user's message text (for selection override)
    private string? firstMessageAgentPath;

    public AgentChatClient(IServiceProvider serviceProvider)
    {
        hub = serviceProvider.GetRequiredService<IMessageHub>();
        logger = serviceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        persistenceService = serviceProvider.GetRequiredService<IChatPersistenceService>();
        meshQuery = serviceProvider.GetService<IMeshQuery>();
        chatClientFactories = serviceProvider.GetServices<IChatClientFactory>().ToList();
    }

    public AgentContext? Context { get; private set; }

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
    /// Sets attachment paths whose content will be loaded and included in the next message.
    /// </summary>
    public void SetAttachments(IReadOnlyList<string>? paths)
    {
        currentAttachments = paths is { Count: > 0 } ? paths : null;
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

        // Try to load persisted thread
        var serializedThread = await persistenceService.LoadThreadAsync(currentThreadId, "shared");

        if (serializedThread.HasValue)
        {
            sharedThread = await agent.DeserializeSessionAsync(serializedThread.Value, hub.JsonSerializerOptions);
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

    private async Task SaveThreadAsync(ChatClientAgent agent, AgentSession thread, string? threadId = null)
    {
        // Save the thread with the specified key or current thread ID
        var serialized = await agent.SerializeSessionAsync(thread, hub.JsonSerializerOptions);
        var id = threadId ?? currentThreadId;
        await persistenceService.SaveThreadAsync(id, "shared", serialized);
        logger.LogInformation("Saved thread: {ThreadId}", id);

        // For persistent threads, update the Thread MeshNode with the PersistentThreadId
        if (isPersistentFactory && !string.IsNullOrEmpty(persistentThreadId))
        {
            await UpdateThreadPersistentIdAsync(id);
        }
    }

    /// <summary>
    /// Updates the Thread MeshNode with PersistentThreadId and ProviderType if they were newly set.
    /// </summary>
    private async Task UpdateThreadPersistentIdAsync(string threadNodePath)
    {
        try
        {
            var meshQuery = hub.ServiceProvider.GetRequiredService<IMeshQuery>();
            var node = await meshQuery.QueryAsync<MeshNode>($"path:{threadNodePath} scope:exact")
                .FirstOrDefaultAsync();
            if (node?.Content is not Thread threadContent)
                return;

            // Only update if not already set
            if (!string.IsNullOrEmpty(threadContent.PersistentThreadId))
                return;

            var factory = GetFactoryForModel(currentModelName);
            var updatedContent = threadContent with
            {
                PersistentThreadId = persistentThreadId,
                ProviderType = factory?.Name
            };

            var updatedNode = node with { Content = updatedContent };
            var nodeJson = System.Text.Json.JsonSerializer.SerializeToElement(updatedNode, hub.JsonSerializerOptions);
            hub.Post(new Data.DataChangeRequest { Updates = [nodeJson] }, o => o.WithTarget(new Messaging.Address(threadNodePath)));

            logger.LogInformation("Updated thread {Path} with PersistentThreadId={PersistentThreadId}",
                threadNodePath, persistentThreadId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update PersistentThreadId on thread {Path}", threadNodePath);
        }
    }

    private async Task<string> BuildMessageWithContextAsync(IReadOnlyCollection<ChatMessage> messages, string? agentName = null)
    {
        var messageText = new StringBuilder();

        // For persistent agents, skip agent instructions and tool docs — they're stored server-side.
        // Only send the current context and user message.
        if (!isPersistentFactory)
        {
            // Add selected agent's instructions as persona identity
            if (!string.IsNullOrEmpty(agentName))
            {
                var agentInfo = loadedAgents.FirstOrDefault(a => a.Name == agentName);
                var agentInstructions = agentInfo?.AgentConfiguration?.Instructions;
                if (!string.IsNullOrEmpty(agentInstructions))
                {
                    messageText.AppendLine("# Agent Identity and Instructions");
                    messageText.AppendLine();
                    messageText.AppendLine("You are acting as the following agent. Follow these instructions strictly:");
                    messageText.AppendLine();
                    messageText.AppendLine(agentInstructions);
                    messageText.AppendLine();
                }
            }
        }

        // Add context if available (always sent, even for persistent agents)
        if (Context != null)
        {
            var contextJson = JsonSerializer.Serialize(Context, hub.JsonSerializerOptions);
            messageText.AppendLine("# Current Application Context");
            messageText.AppendLine();
            messageText.AppendLine("The user is currently viewing the following page/entity in the application:");
            messageText.AppendLine();
            messageText.AppendLine("```json");
            messageText.AppendLine(contextJson);
            messageText.AppendLine("```");
            messageText.AppendLine();
            messageText.AppendLine("Key information:");
            messageText.AppendLine($"- Address Type: {Context.Address?.Type ?? "N/A"}");
            messageText.AppendLine($"- Address ID: {Context.Address?.Id ?? "N/A"}");
            messageText.AppendLine($"- Layout Area: {Context.LayoutArea?.Area ?? "N/A"}");
            messageText.AppendLine($"- Layout ID: {Context.LayoutArea?.Id ?? "N/A"}");
            messageText.AppendLine();
            messageText.AppendLine("Use this context information when answering the user's questions or performing actions.");
            messageText.AppendLine();
        }

        // For persistent agents, skip tool documentation (already on server-side agent definition)
        if (!isPersistentFactory)
        {
            // Add resolved tool documentation
            var toolDocs = await LoadToolDocumentationAsync();
            if (!string.IsNullOrEmpty(toolDocs))
            {
                messageText.AppendLine("# Available Tools Documentation");
                messageText.AppendLine();
                messageText.AppendLine(toolDocs);
                messageText.AppendLine();
            }
        }

        // Load and add attachment content
        var attachmentPaths = currentAttachments;
        if (attachmentPaths is { Count: > 0 })
        {
            var meshPlugin = new MeshPlugin(hub, this);
            var loadTasks = attachmentPaths.Select(async path =>
            {
                try
                {
                    // Skip agent attachments — they override agent selection, not context content
                    var cleanPath = path.TrimStart('@');
                    if (agentAttachmentPaths?.Contains(cleanPath) == true)
                        return (Path: path, Content: (string?)null);

                    var content = await meshPlugin.Get($"@{cleanPath}");
                    if (!string.IsNullOrEmpty(content) && !content.StartsWith("Not found") && !content.StartsWith("Error"))
                    {
                        // Truncate individual attachments to prevent prompt overflow
                        if (content.Length > 8000)
                            content = content[..8000] + "\n... (truncated)";
                        return (Path: path, Content: content);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Error loading attachment content for: {Path}", path);
                }
                return (Path: path, Content: (string?)null);
            });

            var results = await Task.WhenAll(loadTasks);
            var loadedAttachments = results.Where(r => r.Content != null).ToList();

            if (loadedAttachments.Count > 0)
            {
                messageText.AppendLine("# Attached Content");
                messageText.AppendLine();
                foreach (var (path, content) in loadedAttachments)
                {
                    messageText.AppendLine($"## Attachment: {path}");
                    messageText.AppendLine();
                    messageText.AppendLine(content);
                    messageText.AppendLine();
                }
            }
        }

        // Add user messages
        foreach (var message in messages)
        {
            messageText.Append(ExtractTextFromMessage(message));
        }

        return messageText.ToString();
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

        // Select which agent to use (async to avoid deadlock in Blazor context)
        var agent = await SelectAgentAsync(messages.LastOrDefault());
        if (agent == null)
        {
            yield return new ChatMessage(ChatRole.Assistant, "No suitable agent found to handle the request.");
            yield break;
        }

        currentAgentName = agent.Name;

        // Get or create thread for this agent
        var thread = await GetOrCreateThreadAsync(agent);

        // Build the user message with context and agent instructions
        var userMessage = await BuildMessageWithContextAsync(messages, currentAgentName);
        currentAttachments = null; // Clear after use

        // Get response from the agent with thread
        var response = await agent.RunAsync(userMessage, thread, cancellationToken: cancellationToken);

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
        while (queuedLayoutAreaContent.Count > 0)
        {
            var layoutAreaContent = queuedLayoutAreaContent.Dequeue();
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
            while (queuedLayoutAreaContent.Count > 0)
            {
                var lac = queuedLayoutAreaContent.Dequeue();
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

        // Select which agent to use (async to avoid deadlock in Blazor context)
        var agent = await SelectAgentAsync(messages.LastOrDefault());
        if (agent == null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "No suitable agent found to handle the request.");
            yield break;
        }

        currentAgentName = agent.Name;

        // Get or create thread for this agent
        var thread = await GetOrCreateThreadAsync(agent);

        // Build the user message with context and agent instructions
        var userMessage = await BuildMessageWithContextAsync(messages, currentAgentName);
        currentAttachments = null; // Clear after use

        // Get streaming response from the agent with thread
        await foreach (var update in agent.RunStreamingAsync(userMessage, thread, cancellationToken: cancellationToken))
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
        while (queuedLayoutAreaContent.Count > 0)
        {
            var layoutAreaContent = queuedLayoutAreaContent.Dequeue();
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
            while (queuedLayoutAreaContent.Count > 0)
            {
                var lac = queuedLayoutAreaContent.Dequeue();
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

    private async Task<ChatClientAgent?> SelectAgentAsync(ChatMessage? lastMessage)
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

        // 4. Use ordered agents - first one is the best match for context
        // GetOrderedAgentsAsync already handles: context pattern matching, NodeType namespace, path relevance
        var orderedAgents = await GetOrderedAgentsAsync();
        if (orderedAgents.Count > 0)
        {
            var bestAgent = orderedAgents[0];
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
        Context = applicationContext;
    }

    /// <summary>
    /// Initializes the chat client by loading agents for the specified context path.
    /// This should be called after construction and before use.
    /// </summary>
    /// <param name="contextPath">The context path for agent resolution</param>
    /// <param name="modelName">Optional model name for agent creation</param>
    public async Task InitializeAsync(string? contextPath, string? modelName = null)
    {
        currentModelName = modelName;

        // Load and order agents from mesh
        loadedAgents = await LoadOrderedAgentsAsync(contextPath);
        lastLoadedContextPath = contextPath;

        // Create AIAgent instances
        await CreateAgentsAsync();
    }

    /// <summary>
    /// Loads agents from mesh and returns them ordered by relevance.
    /// Two queries: path hierarchy + NodeType hierarchy.
    /// </summary>
    private async Task<List<AgentDisplayInfo>> LoadOrderedAgentsAsync(string? contextPath)
    {
        if (meshQuery == null)
            return [];

        var agentsDict = new Dictionary<string, (AgentConfiguration Config, string Path)>();

        // 1. Get NodeType of current node
        string? nodeTypePath = null;
        if (!string.IsNullOrEmpty(contextPath))
        {
            try
            {
                await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{contextPath} scope:self"))
                {
                    if (!string.IsNullOrEmpty(node.NodeType) && node.NodeType != "Agent" && node.NodeType != "Markdown")
                    {
                        nodeTypePath = node.NodeType;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error getting NodeType for {ContextPath}", contextPath);
            }
        }

        // 2. Query agents from context path hierarchy (or root if no context)
        try
        {
            var pathQuery = string.IsNullOrEmpty(contextPath)
                ? "namespace: nodeType:Agent"  // Root level: get direct children agents
                : $"path:{contextPath} nodeType:Agent scope:AncestorsAndSelf";

            await foreach (var node in meshQuery.QueryAsync<MeshNode>(pathQuery))
            {
                if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                    agentsDict[config.Id] = (config, node.Path ?? "");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error querying path hierarchy for {ContextPath}", contextPath ?? "root");
        }

        // 3. Query agents from root namespace subtree (to find sibling agents)
        if (!string.IsNullOrEmpty(contextPath))
        {
            try
            {
                // Extract root namespace (first segment of the path)
                var rootNamespace = contextPath.Split('/').FirstOrDefault(s => !string.IsNullOrEmpty(s));
                if (!string.IsNullOrEmpty(rootNamespace))
                {
                    var subtreeQuery = $"path:{rootNamespace} nodeType:Agent scope:Subtree";
                    await foreach (var node in meshQuery.QueryAsync<MeshNode>(subtreeQuery))
                    {
                        if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                            agentsDict[config.Id] = (config, node.Path ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error querying root namespace subtree for {ContextPath}", contextPath);
            }
        }

        // 4. Query agents from NodeType hierarchy
        if (!string.IsNullOrEmpty(nodeTypePath))
        {
            try
            {
                var nodeTypeQuery = $"path:{nodeTypePath} nodeType:Agent scope:AncestorsAndSelf";
                await foreach (var node in meshQuery.QueryAsync<MeshNode>(nodeTypeQuery))
                {
                    if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                        agentsDict[config.Id] = (config, node.Path ?? "");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error querying NodeType hierarchy for {NodeType}", nodeTypePath);
            }
        }

        // 5. Query default agents from Agent namespace
        try
        {
            var agentNamespaceQuery = "path:Agent nodeType:Agent scope:Subtree";
            await foreach (var node in meshQuery.QueryAsync<MeshNode>(agentNamespaceQuery))
            {
                if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                    agentsDict[config.Id] = (config, node.Path ?? "");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error querying Agent namespace");
        }

        // Convert to AgentDisplayInfo
        var displayInfos = agentsDict.Values.Select(x => new AgentDisplayInfo
        {
            Name = x.Config.Id,
            Path = x.Path,
            Description = x.Config.Description ?? x.Config.DisplayName ?? x.Config.Id,
            GroupName = x.Config.GroupName,
            Order = x.Config.Order,
            Icon = x.Config.Icon,
            CustomIconSvg = x.Config.CustomIconSvg,
            AgentConfiguration = x.Config
        }).ToList();

        // Order by relevance: own namespace > NodeType namespace > hierarchy (path > nodeType)
        var contextPathNorm = contextPath?.TrimStart('/') ?? "";
        var nodeTypePathNorm = nodeTypePath?.TrimStart('/') ?? "";

        var result = AgentOrderingHelper.OrderByRelevance(displayInfos, contextPathNorm, nodeTypePathNorm).ToList();
        logger.LogDebug("Loaded {Count} agents for context {ContextPath}: [{Agents}]",
            result.Count, contextPath ?? "(none)", string.Join(", ", result.Select(a => a.Name)));
        return result;
    }

    /// <summary>
    /// Creates ChatClientAgent instances for all loaded configurations.
    /// </summary>
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

        var configs = loadedAgents.Select(a => a.AgentConfiguration).ToList();
        var createdAgents = new Dictionary<string, ChatClientAgent>();

        // Order agents: non-delegating first, delegating second, default last
        var orderedConfigs = OrderAgentsForCreation(configs);

        // First pass: Create all agents in order
        foreach (var agentConfig in orderedConfigs)
        {
            var agent = await factory.CreateAgentAsync(
                agentConfig, this, createdAgents, configs, currentModelName);
            createdAgents[agentConfig.Id] = agent;
            agents[agentConfig.Id] = agent;
        }

        // Second pass: Update agents with cyclic dependencies
        var cyclicAgents = FindCyclicDelegations(configs);
        foreach (var agentConfig in cyclicAgents)
        {
            var updatedAgent = await factory.CreateAgentAsync(
                agentConfig, this, createdAgents, configs, currentModelName);
            createdAgents[agentConfig.Id] = updatedAgent;
            agents[agentConfig.Id] = updatedAgent;
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

        // Find factory that has this model
        var factory = chatClientFactories
            .FirstOrDefault(f => f.Models.Contains(modelName));

        if (factory != null)
        {
            return factory;
        }

        // Fallback: return first factory
        logger.LogWarning("[AgentChatClient] Model {ModelName} not found in any factory, using first available", modelName);
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
        var delegatingAgents = configs.Where(a => a.Delegations is { Count: > 0 }).ToList();
        var cyclicAgents = new HashSet<string>();

        foreach (var agent in delegatingAgents)
        {
            var delegatedAgentPaths = agent.Delegations!.Select(d => d.AgentPath).ToHashSet();

            foreach (var delegatedPath in delegatedAgentPaths)
            {
                var delegatedId = delegatedPath.Split('/').Last();
                var delegatedAgent = delegatingAgents.FirstOrDefault(a => a.Id == delegatedId);

                if (delegatedAgent?.Delegations != null)
                {
                    var backDelegations = delegatedAgent.Delegations.Select(d => d.AgentPath.Split('/').Last()).ToHashSet();
                    if (backDelegations.Contains(agent.Id))
                    {
                        cyclicAgents.Add(agent.Id);
                        cyclicAgents.Add(delegatedId);
                    }
                }
            }
        }

        return configs.Where(a => cyclicAgents.Contains(a.Id));
    }

    /// <summary>
    /// Returns the ordered list of agents for the current context.
    /// Reloads agents when context path changes.
    /// </summary>
    public async Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync()
    {
        // Only check for context changes if Context has been explicitly set via SetContext()
        // This prevents reloading with null when agents were already loaded by InitializeAsync
        var currentContextPath = Context?.Address?.ToString();

        // Only reload if:
        // 1. Context has been set (not null), AND
        // 2. It's different from what was already loaded
        if (currentContextPath != null && currentContextPath != lastLoadedContextPath)
        {
            loadedAgents = await LoadOrderedAgentsAsync(currentContextPath);
            lastLoadedContextPath = currentContextPath;

            // Recreate agent instances for new context
            agentsInitialized = false;
            agents.Clear();
            await CreateAgentsAsync();
        }

        return loadedAgents;
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
        queuedLayoutAreaContent.Enqueue(layoutAreaContent);
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

    private void DetectAgentAttachments()
    {
        agentAttachmentPaths = new(StringComparer.OrdinalIgnoreCase);
        if (currentAttachments is not { Count: > 0 })
            return;

        foreach (var path in currentAttachments)
        {
            var cleanPath = path.TrimStart('@');
            if (IsAgentPath(cleanPath))
                agentAttachmentPaths.Add(cleanPath);
        }
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
