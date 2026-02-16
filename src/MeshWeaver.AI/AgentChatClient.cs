using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.AI.Persistence;
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
    // Pattern to match @@path references (e.g., @@MeshWeaver/Documentation/AI/Tools/MeshPlugin#Get)
    private static readonly Regex InlineReferencePattern =
        new(@"@@([^\s#]+)(?:#(\w+))?", RegexOptions.Compiled);
    private readonly IMessageHub hub;
    private readonly ILogger<AgentChatClient> logger;
    private readonly IChatPersistenceService persistenceService;
    private readonly IMeshQuery? meshQuery;
    private readonly IReadOnlyList<IChatClientFactory> chatClientFactories;
    private readonly Dictionary<string, ChatClientAgent> agents = new();
    private readonly Queue<ChatLayoutAreaContent> queuedLayoutAreaContent = new();
    private List<AgentDisplayInfo> loadedAgents = [];
    private string? lastLoadedContextPath;
    private string currentThreadId = Guid.NewGuid().AsString();
    private string? currentAgentName;
    private AgentSession? sharedThread;
    private string? currentModelName;
    private bool agentsInitialized;

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

    private async Task<AgentSession> GetOrCreateThreadAsync(ChatClientAgent agent)
    {
        logger.LogDebug("[AgentChatClient] GetOrCreateThreadAsync called for agent: {AgentName}", agent.Name);

        // Use shared thread across all agents in this conversation
        if (sharedThread != null)
        {
            logger.LogDebug("[AgentChatClient] Using existing shared thread: {ThreadId}", currentThreadId);
            return sharedThread;
        }

        // Try to load persisted thread
        logger.LogDebug("[AgentChatClient] Loading persisted thread...");
        var serializedThread = await persistenceService.LoadThreadAsync(currentThreadId, "shared");

        if (serializedThread.HasValue)
        {
            logger.LogDebug("[AgentChatClient] Found persisted thread, deserializing...");
            sharedThread = await agent.DeserializeSessionAsync(serializedThread.Value, hub.JsonSerializerOptions);
            logger.LogDebug("[AgentChatClient] Thread deserialized successfully");
            return sharedThread;
        }

        logger.LogDebug("[AgentChatClient] Creating new shared thread: {ThreadId}", currentThreadId);
        sharedThread = await agent.GetNewSessionAsync();
        return sharedThread;
    }

    private async Task SaveThreadAsync(ChatClientAgent _, AgentSession thread, string? threadId = null)
    {
        // Save the thread with the specified key or current thread ID
        var serialized = thread.Serialize(hub.JsonSerializerOptions);
        var id = threadId ?? currentThreadId;
        await persistenceService.SaveThreadAsync(id, "shared", serialized);
        logger.LogInformation("Saved thread: {ThreadId}", id);
    }

    private async Task<string> BuildMessageWithContextAsync(IReadOnlyCollection<ChatMessage> messages)
    {
        var messageText = new StringBuilder();

        // Add context if available
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

        // Add resolved tool documentation
        var toolDocs = await LoadToolDocumentationAsync();
        if (!string.IsNullOrEmpty(toolDocs))
        {
            messageText.AppendLine("# Available Tools Documentation");
            messageText.AppendLine();
            messageText.AppendLine(toolDocs);
            messageText.AppendLine();
        }

        // Add user messages
        foreach (var message in messages)
        {
            messageText.Append(ExtractTextFromMessage(message));
        }

        return messageText.ToString();
    }

    /// <summary>
    /// Loads tool documentation from the mesh.
    /// </summary>
    private async Task<string> LoadToolDocumentationAsync()
    {
        var meshPlugin = new MeshPlugin(hub, this);
        var docs = await meshPlugin.Get("@MeshWeaver/Documentation/AI/Tools/MeshPlugin");

        if (docs.StartsWith("Not found") || docs.StartsWith("Error"))
            return string.Empty;

        // Try to extract just the markdown content from the JSON response
        try
        {
            var node = JsonSerializer.Deserialize<MeshNode>(docs, hub.JsonSerializerOptions);
            if (node?.Content is JsonElement contentElement && contentElement.ValueKind == JsonValueKind.String)
            {
                return contentElement.GetString() ?? string.Empty;
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
    /// Resolves @@path references in text and returns expanded content.
    /// Uses MeshPlugin.Get to load referenced documents.
    /// </summary>
    private async Task<string> ResolveInlineReferencesAsync(string text)
    {
        var matches = InlineReferencePattern.Matches(text);
        if (matches.Count == 0)
            return text;

        var result = text;
        var meshPlugin = new MeshPlugin(hub, this);

        foreach (Match match in matches)
        {
            var path = match.Groups[1].Value;
            var section = match.Groups[2].Success ? match.Groups[2].Value : null;

            // Load the document using Get
            var content = await meshPlugin.Get($"@{path}");

            // If section specified, extract just that section
            if (!string.IsNullOrEmpty(section) && !content.StartsWith("Not found"))
            {
                content = ExtractSection(content, section);
            }

            // Replace the reference with the content
            result = result.Replace(match.Value, content);
        }

        return result;
    }

    /// <summary>
    /// Extracts a markdown section by heading name.
    /// </summary>
    private static string ExtractSection(string markdown, string sectionName)
    {
        // Find ## SectionName and extract until next ## or end
        var pattern = $@"##\s+{Regex.Escape(sectionName)}[^\n]*\n([\s\S]*?)(?=\n##|\z)";
        var match = Regex.Match(markdown, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : markdown;
    }

    public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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

        // Build the user message with context
        var userMessage = await BuildMessageWithContextAsync(messages);

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
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        logger.LogDebug("[AgentChatClient] GetStreamingResponseAsync entered, selecting agent...");

        // Select which agent to use (async to avoid deadlock in Blazor context)
        var agent = await SelectAgentAsync(messages.LastOrDefault());
        if (agent == null)
        {
            logger.LogDebug("[AgentChatClient] No agent selected!");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "No suitable agent found to handle the request.");
            yield break;
        }

        logger.LogDebug("[AgentChatClient] Selected agent: {AgentName}", agent.Name);
        currentAgentName = agent.Name;

        // Get or create thread for this agent
        logger.LogDebug("[AgentChatClient] Getting or creating thread...");
        var thread = await GetOrCreateThreadAsync(agent);
        logger.LogDebug("[AgentChatClient] Got thread: {ThreadId}", currentThreadId);

        // Build the user message with context
        var userMessage = await BuildMessageWithContextAsync(messages);
        logger.LogDebug("[AgentChatClient] Built message with context, length: {Length}", userMessage.Length);

        // Get streaming response from the agent with thread
        logger.LogDebug("[AgentChatClient] Starting RunStreamingAsync on agent...");
        var streamUpdateCount = 0;
        await foreach (var update in agent.RunStreamingAsync(userMessage, thread, cancellationToken: cancellationToken))
        {
            streamUpdateCount++;
            if (streamUpdateCount == 1)
            {
                logger.LogDebug("[AgentChatClient] Got FIRST update from RunStreamingAsync!");
            }
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
                        logger.LogInformation("Agent {AgentName} received result from tool",
                            currentAgentName);

                        // Check if this is a delegation marker
                        var resultText = functionResult.Result?.ToString() ?? string.Empty;
                        if (resultText.StartsWith("__HANDOFF__|"))
                        {
                            // Parse the delegation marker: __HANDOFF__|{targetAgentName}|{message}
                            var parts = resultText.Split('|');
                            if (parts.Length >= 3)
                            {
                                var targetAgentName = parts[1];
                                var delegationMessage = string.Join('|', parts.Skip(2));

                                logger.LogInformation("Delegation detected from {SourceAgent} to {TargetAgent}",
                                    currentAgentName, targetAgentName);

                                // Yield delegation marker to UI
                                var delegationContent = new ChatDelegationContent(
                                    currentAgentName ?? "Assistant",
                                    targetAgentName,
                                    delegationMessage);

                                yield return new ChatResponseUpdate(ChatRole.Assistant, [delegationContent])
                                {
                                    AuthorName = currentAgentName ?? "Assistant"
                                };

                                // Invoke the target agent in streaming mode with ISOLATED thread
                                if (agents.TryGetValue(targetAgentName, out var targetAgent))
                                {
                                    // Create a NEW isolated thread for the delegated agent
                                    // This gives the agent its own context window without parent conversation history
                                    var isolatedThreadId = $"{currentThreadId}_delegation_{Guid.NewGuid().AsString()}";
                                    var targetThread = await targetAgent.GetNewSessionAsync();
                                    logger.LogInformation("Created isolated thread {ThreadId} for delegated agent {AgentName}",
                                        isolatedThreadId, targetAgentName);

                                    // Build message with context for the target agent
                                    var targetMessage = await BuildMessageWithContextAsync([new ChatMessage(ChatRole.User, delegationMessage)]);

                                    // Collect the delegated agent's response for potential return to parent
                                    var delegatedResponseBuilder = new StringBuilder();

                                    // Stream the target agent's response
                                    await foreach (var targetUpdate in targetAgent.RunStreamingAsync(
                                        targetMessage, targetThread, cancellationToken: cancellationToken))
                                    {
                                        // Yield function calls from the delegated agent
                                        if (targetUpdate.Contents.Count > 0)
                                        {
                                            foreach (var targetContent in targetUpdate.Contents)
                                            {
                                                if (targetContent is FunctionCallContent targetFunctionCall)
                                                {
                                                    logger.LogInformation("Delegated agent {AgentName} calling tool: {FunctionName}",
                                                        targetAgentName, targetFunctionCall.Name);

                                                    yield return new ChatResponseUpdate(ChatRole.Assistant, [targetContent])
                                                    {
                                                        AuthorName = targetAgentName
                                                    };
                                                }
                                            }
                                        }

                                        // Yield target agent's text updates with their name
                                        if (!string.IsNullOrEmpty(targetUpdate.Text))
                                        {
                                            delegatedResponseBuilder.Append(targetUpdate.Text);
                                            yield return new ChatResponseUpdate(ChatRole.Assistant, targetUpdate.Text)
                                            {
                                                AuthorName = targetAgentName
                                            };
                                        }
                                    }

                                    // Save the isolated thread for potential future reference
                                    await SaveThreadAsync(targetAgent, targetThread, isolatedThreadId);
                                    logger.LogDebug("Saved isolated thread {ThreadId} for delegated agent {AgentName}",
                                        isolatedThreadId, targetAgentName);

                                    // NOTE: After target agent completes, the original agent's stream will continue
                                    // and any remaining updates from the original agent will be yielded below
                                }
                                else
                                {
                                    logger.LogWarning("Target agent {TargetAgent} not found for delegation",
                                        targetAgentName);
                                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                                        $"Error: Agent '{targetAgentName}' not found")
                                    {
                                        AuthorName = currentAgentName ?? "Assistant"
                                    };
                                }
                            }
                        }
                        // Don't yield function results to the UI
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

        logger.LogDebug("[AgentChatClient] RunStreamingAsync completed, total updates: {Count}", streamUpdateCount);

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
    }

    // Pattern: @agent/AgentName (anywhere in message)
    private static readonly Regex AgentReferencePattern =
        new(@"@agent/(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private async Task<ChatClientAgent?> SelectAgentAsync(ChatMessage? lastMessage)
    {
        logger.LogDebug("[AgentChatClient] SelectAgentAsync called. Context: {Context}",
            Context != null ? $"Address={Context.Address}, LayoutArea={Context.LayoutArea?.Area}" : "null");

        // 1. Check for explicit @agent/Name reference in message
        if (lastMessage != null)
        {
            var text = ExtractTextFromMessage(lastMessage);
            var agentMatch = AgentReferencePattern.Match(text);
            if (agentMatch.Success)
            {
                var agentName = agentMatch.Groups[1].Value;
                if (agents.TryGetValue(agentName, out var agent))
                {
                    logger.LogDebug("Selected agent by @agent/ reference: {AgentName}", agentName);
                    return agent;
                }
                // Case-insensitive fallback
                var found = agents.FirstOrDefault(kvp =>
                    kvp.Key.Equals(agentName, StringComparison.OrdinalIgnoreCase));
                if (found.Value != null)
                {
                    logger.LogDebug("Selected agent by @agent/ reference (case-insensitive): {AgentName}", found.Key);
                    return found.Value;
                }
            }
        }

        // 2. Use ordered agents - first one is the best match for context
        // GetOrderedAgentsAsync already handles: context pattern matching, NodeType namespace, path relevance
        var orderedAgents = await GetOrderedAgentsAsync();
        if (orderedAgents.Count > 0)
        {
            var bestAgent = orderedAgents[0];
            if (agents.TryGetValue(bestAgent.Name, out var agent))
            {
                logger.LogDebug("[AgentChatClient] Selected best agent from ordered list: {AgentName}", bestAgent.Name);
                return agent;
            }
        }

        // 3. Use current agent if we have one
        if (!string.IsNullOrEmpty(currentAgentName) && agents.TryGetValue(currentAgentName, out var currentAgent))
        {
            logger.LogDebug("Using current agent: {AgentName}", currentAgentName);
            return currentAgent;
        }

        // 4. Return first agent as fallback
        var fallbackAgent = agents.Values.FirstOrDefault();
        logger.LogDebug("Using fallback agent: {AgentName}", fallbackAgent?.Name ?? "null");
        return fallbackAgent;
    }
    public void SetContext(AgentContext? applicationContext)
    {
        Context = applicationContext;
        logger.LogDebug("Context set to: {Context}", Context != null ? $"Address={Context.Address}, LayoutArea={Context.LayoutArea?.Area}" : "null");
    }

    /// <summary>
    /// Initializes the chat client by loading agents for the specified context path.
    /// This should be called after construction and before use.
    /// </summary>
    /// <param name="contextPath">The context path for agent resolution</param>
    /// <param name="modelName">Optional model name for agent creation</param>
    public async Task InitializeAsync(string? contextPath, string? modelName = null)
    {
        logger.LogDebug("[AgentChatClient] InitializeAsync called with contextPath: {ContextPath}, modelName: {ModelName}", contextPath, modelName);

        currentModelName = modelName;

        // Load and order agents from mesh
        loadedAgents = await LoadOrderedAgentsAsync(contextPath);
        lastLoadedContextPath = contextPath;

        logger.LogDebug("[AgentChatClient] Loaded {Count} agents", loadedAgents.Count);

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
        {
            logger.LogDebug("[AgentChatClient] IMeshQuery not available, returning empty list");
            return [];
        }

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
                        logger.LogDebug("[AgentChatClient] Found NodeType {NodeType} for {ContextPath}", nodeTypePath, contextPath);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[AgentChatClient] Error getting NodeType for {ContextPath}", contextPath);
            }
        }

        // 2. Query agents from context path hierarchy (or root if no context)
        try
        {
            var pathQuery = string.IsNullOrEmpty(contextPath)
                ? "nodeType:Agent scope:children"  // Root level: get direct children agents
                : $"path:{contextPath} nodeType:Agent scope:selfAndAncestors";

            await foreach (var node in meshQuery.QueryAsync<MeshNode>(pathQuery))
            {
                if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                {
                    agentsDict[config.Id] = (config, node.Path ?? "");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[AgentChatClient] Error querying path hierarchy {ContextPath}", contextPath ?? "root");
        }

        // 3. Query agents from NodeType hierarchy
        if (!string.IsNullOrEmpty(nodeTypePath))
        {
            try
            {
                await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{nodeTypePath} nodeType:Agent scope:selfAndAncestors"))
                {
                    if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                    {
                        agentsDict[config.Id] = (config, node.Path ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[AgentChatClient] Error querying NodeType hierarchy {NodeType}", nodeTypePath);
            }
        }

        // Convert to AgentDisplayInfo
        var displayInfos = agentsDict.Values.Select(x => new AgentDisplayInfo
        {
            Name = x.Config.Id,
            Path = x.Path,
            Description = x.Config.Description ?? x.Config.DisplayName ?? x.Config.Id,
            GroupName = x.Config.GroupName,
            DisplayOrder = x.Config.DisplayOrder,
            Icon = x.Config.Icon,
            CustomIconSvg = x.Config.CustomIconSvg,
            AgentConfiguration = x.Config
        }).ToList();

        // Order by relevance: own namespace > NodeType namespace > hierarchy (path > nodeType)
        var contextPathNorm = contextPath?.TrimStart('/') ?? "";
        var nodeTypePathNorm = nodeTypePath?.TrimStart('/') ?? "";

        return AgentOrderingHelper.OrderByRelevance(displayInfos, contextPathNorm, nodeTypePathNorm).ToList();
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
        {
            logger.LogDebug("[AgentChatClient] Agents already initialized, skipping");
            return;
        }

        // Select the appropriate factory based on the requested model
        var factory = GetFactoryForModel(currentModelName);
        if (factory == null)
        {
            logger.LogWarning("[AgentChatClient] No factory can serve model: {ModelName}", currentModelName);
            throw new ArgumentException($"No factory can serve model: {currentModelName}");
        }

        logger.LogInformation("[AgentChatClient] Using factory {FactoryName} for model {ModelName}",
            factory.Name, currentModelName ?? "default");

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
            logger.LogDebug("[AgentChatClient] Created agent: {AgentId}", agentConfig.Id);
        }

        // Second pass: Update agents with cyclic dependencies
        var cyclicAgents = FindCyclicDelegations(configs);
        foreach (var agentConfig in cyclicAgents)
        {
            var updatedAgent = await factory.CreateAgentAsync(
                agentConfig, this, createdAgents, configs, currentModelName);
            createdAgents[agentConfig.Id] = updatedAgent;
            agents[agentConfig.Id] = updatedAgent;
            logger.LogDebug("[AgentChatClient] Updated cyclic agent: {AgentId}", agentConfig.Id);
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
            // Return first factory ordered by DisplayOrder
            return chatClientFactories
                .OrderBy(f => f.DisplayOrder)
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
            .OrderBy(f => f.DisplayOrder)
            .FirstOrDefault();
    }

    /// <summary>
    /// Orders agents for creation: non-delegating first, delegating second, default last.
    /// </summary>
    private static IEnumerable<AgentConfiguration> OrderAgentsForCreation(IEnumerable<AgentConfiguration> configs)
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
    private static IEnumerable<AgentConfiguration> FindCyclicDelegations(IEnumerable<AgentConfiguration> configs)
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
        var currentContextPath = Context?.Address?.ToString();

        // Reload if context path changed
        if (currentContextPath != lastLoadedContextPath)
        {
            logger.LogDebug("[GetOrderedAgentsAsync] Context changed from {Old} to {New}, reloading agents",
                lastLoadedContextPath, currentContextPath);

            loadedAgents = await LoadOrderedAgentsAsync(currentContextPath);
            lastLoadedContextPath = currentContextPath;

            // Recreate agent instances for new context
            agentsInitialized = false;
            agents.Clear();
            await CreateAgentsAsync();
        }

        logger.LogDebug("[GetOrderedAgentsAsync] Returning {Count} agents, first: {First}",
            loadedAgents.Count, loadedAgents.FirstOrDefault()?.Name ?? "none");

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
