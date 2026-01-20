using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.AI.Persistence;
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

/// <summary>
/// Delegate for creating ChatClientAgent instances.
/// Called by AgentChatClient when it needs to create agents for its context.
/// </summary>
public delegate Task<ChatClientAgent> AgentCreatorDelegate(
    AgentConfiguration config,
    IAgentChat chat,
    IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
    IReadOnlyList<AgentConfiguration> hierarchyAgents);

public class AgentChatClient : IAgentChat
{
    private readonly IMessageHub hub;
    private readonly ILogger<AgentChatClient> logger;
    private readonly IChatPersistenceService persistenceService;
    private readonly IMeshQuery? meshQuery;
    private readonly AgentCreatorDelegate? agentCreator;
    private readonly Dictionary<string, AIAgent> agents = new();
    private readonly Queue<ChatLayoutAreaContent> queuedLayoutAreaContent = new();
    private IReadOnlyList<AgentConfiguration> agentConfigurations = Array.Empty<AgentConfiguration>();
    private IReadOnlyList<AgentConfiguration> hierarchyAgents = Array.Empty<AgentConfiguration>();
    private string currentThreadId = Guid.NewGuid().AsString();
    private string? currentAgentName;
    private AgentThread? sharedThread;

    public AgentChatClient(IServiceProvider serviceProvider, AgentCreatorDelegate? agentCreator = null)
    {
        hub = serviceProvider.GetRequiredService<IMessageHub>();
        logger = serviceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        persistenceService = serviceProvider.GetRequiredService<IChatPersistenceService>();
        meshQuery = serviceProvider.GetService<IMeshQuery>();
        this.agentCreator = agentCreator;
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

    private async Task<AgentThread> GetOrCreateThreadAsync(AIAgent agent)
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
            sharedThread = agent.DeserializeThread(serializedThread.Value, hub.JsonSerializerOptions);
            logger.LogDebug("[AgentChatClient] Thread deserialized successfully");
            return sharedThread;
        }

        logger.LogDebug("[AgentChatClient] Creating new shared thread: {ThreadId}", currentThreadId);
        sharedThread = agent.GetNewThread();
        return sharedThread;
    }

    private async Task SaveThreadAsync(AIAgent _, AgentThread thread)
    {
        // Save the shared thread with a common key
        var serialized = thread.Serialize(hub.JsonSerializerOptions);
        await persistenceService.SaveThreadAsync(currentThreadId, "shared", serialized);
        logger.LogInformation("Saved shared thread: {ThreadId}",
            currentThreadId);
    }

    private string BuildMessageWithContext(IReadOnlyCollection<ChatMessage> messages)
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

        // Add user messages
        foreach (var message in messages)
        {
            messageText.Append(ExtractTextFromMessage(message));
        }

        return messageText.ToString();
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
        var userMessage = BuildMessageWithContext(messages);

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
        var userMessage = BuildMessageWithContext(messages);
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

                                // Invoke the target agent in streaming mode
                                if (agents.TryGetValue(targetAgentName, out var targetAgent))
                                {
                                    // Use the same shared thread - it already contains the full conversation history
                                    var targetThread = thread; // Same thread instance

                                    // Build message with context for the target agent
                                    var targetMessage = BuildMessageWithContext([new ChatMessage(ChatRole.User, delegationMessage)]);

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
                                            yield return new ChatResponseUpdate(ChatRole.Assistant, targetUpdate.Text)
                                            {
                                                AuthorName = targetAgentName
                                            };
                                        }
                                    }

                                    // NOTE: After target agent completes, the original agent's stream will continue
                                    // and any remaining updates from the original agent will be yielded below
                                    // Thread is already saved at the end of the outer streaming loop
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

    private async Task<AIAgent?> SelectAgentAsync(ChatMessage? lastMessage)
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

        // 2. Try to find agent based on context
        if (Context != null)
        {
            var selectedAgent = await FindAgentForContextAsync(Context);
            if (selectedAgent != null)
                return selectedAgent;
        }

        // 3. Use current agent if we have one
        if (!string.IsNullOrEmpty(currentAgentName) && agents.TryGetValue(currentAgentName, out var currentAgent))
        {
            logger.LogDebug("Using current agent: {AgentName}", currentAgentName);
            return currentAgent;
        }

        // 4. Find default agent from configurations
        var defaultAgentConfig = agentConfigurations.FirstOrDefault(a => a.IsDefault);
        if (defaultAgentConfig != null && agents.TryGetValue(defaultAgentConfig.Id, out var defaultAgent))
        {
            logger.LogDebug("Selected default agent: {AgentName}", defaultAgentConfig.Id);
            return defaultAgent;
        }

        // 5. Return first agent as fallback
        var fallbackAgent = agents.Values.FirstOrDefault();
        logger.LogDebug("Using fallback agent: {AgentName}", fallbackAgent?.Name ?? "null");
        return fallbackAgent;
    }

    /// <summary>
    /// Finds the best agent for a given context by:
    /// 1. Using pre-loaded agents from context.AvailableAgents if available
    /// 2. Otherwise, querying for agents using NodeType and path namespaces
    /// 3. Matching by ContextMatchPattern or returning closest default agent
    /// </summary>
    private async Task<AIAgent?> FindAgentForContextAsync(AgentContext context)
    {
        var contextPath = context.Address?.ToString();

        // If agents are already loaded in context, use them directly
        if (context.AvailableAgents?.Count > 0)
        {
            logger.LogDebug("[AgentChatClient] Using {Count} pre-loaded agents from context", context.AvailableAgents.Count);

            // Match by ContextMatchPattern
            foreach (var config in context.AvailableAgents)
            {
                if (!string.IsNullOrEmpty(config.ContextMatchPattern) && MatchesContext(config.ContextMatchPattern, context))
                {
                    if (agents.TryGetValue(config.Id, out var agent))
                    {
                        logger.LogDebug("[AgentChatClient] Selected agent by context pattern: {AgentName}", config.Id);
                        return agent;
                    }
                }
            }

            // Return closest default agent
            var defaultAgent = context.AvailableAgents.FirstOrDefault(a => a.IsDefault);
            if (defaultAgent != null && agents.TryGetValue(defaultAgent.Id, out var agent2))
            {
                logger.LogDebug("[AgentChatClient] Selected default agent: {AgentName}", defaultAgent.Id);
                return agent2;
            }

            // Return first available agent
            var firstAgent = context.AvailableAgents.FirstOrDefault();
            if (firstAgent != null && agents.TryGetValue(firstAgent.Id, out var agent3))
            {
                logger.LogDebug("[AgentChatClient] Selected first available agent: {AgentName}", firstAgent.Id);
                return agent3;
            }
        }

        // Fall back to query-based logic if no agents in context
        if (meshQuery == null || string.IsNullOrEmpty(contextPath)) return null;

        // Use NodeType directly from context's MeshNode if available
        var nodeTypePath = context.Node?.NodeType;

        // If NodeType not in context, fall back to querying
        if (string.IsNullOrEmpty(nodeTypePath))
        {
            try
            {
                await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{contextPath} scope:self"))
                {
                    if (!string.IsNullOrEmpty(node.NodeType) && node.NodeType != "Markdown" && node.NodeType != "Agent")
                    {
                        nodeTypePath = node.NodeType;
                        logger.LogDebug("[AgentChatClient] Found NodeType {NodeType} for context {Context}", nodeTypePath, contextPath);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[AgentChatClient] Error getting NodeType for context {Context}", contextPath);
            }
        }
        else
        {
            logger.LogDebug("[AgentChatClient] Using NodeType {NodeType} from context", nodeTypePath);
        }

        var foundAgents = new List<(AgentConfiguration Config, string Path)>();

        // Query agents from NodeType namespace (higher priority)
        // Use subtree scope to find agents that are children of the NodeType path
        if (!string.IsNullOrEmpty(nodeTypePath))
        {
            try
            {
                var query = $"path:{nodeTypePath} nodeType:Agent scope:hierarchy";
                logger.LogDebug("[AgentChatClient] Querying agents: {Query}", query);

                await foreach (var node in meshQuery.QueryAsync<MeshNode>(query))
                {
                    if (node.Content is AgentConfiguration config)
                    {
                        foundAgents.Add((config, node.Path ?? ""));
                        logger.LogDebug("[AgentChatClient] Found agent {AgentId} at {Path}", config.Id, node.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[AgentChatClient] Error querying agents in NodeType namespace {NodeType}", nodeTypePath);
            }
        }

        // Query agents from context path namespace
        try
        {
            var query = $"path:{contextPath} nodeType:Agent scope:selfAndAncestors";
            logger.LogDebug("[AgentChatClient] Querying agents: {Query}", query);

            await foreach (var node in meshQuery.QueryAsync<MeshNode>(query))
            {
                if (node.Content is AgentConfiguration config && !foundAgents.Any(a => a.Config.Id == config.Id))
                {
                    foundAgents.Add((config, node.Path ?? ""));
                    logger.LogDebug("[AgentChatClient] Found agent {AgentId} at {Path}", config.Id, node.Path);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[AgentChatClient] Error querying agents in path namespace {ContextPath}", contextPath);
        }

        // First try to match by ContextMatchPattern
        foreach (var (config, _) in foundAgents)
        {
            if (!string.IsNullOrEmpty(config.ContextMatchPattern) && MatchesContext(config.ContextMatchPattern, context))
            {
                if (agents.TryGetValue(config.Id, out var agent))
                {
                    logger.LogDebug("[AgentChatClient] Selected agent by context pattern: {AgentName}", config.Id);
                    return agent;
                }
            }
        }

        // Return closest default agent (longest path)
        var defaultAgentFromQuery = foundAgents
            .Where(a => a.Config.IsDefault)
            .OrderByDescending(a => a.Path.Split('/').Length)
            .FirstOrDefault();

        if (defaultAgentFromQuery.Config != null && agents.TryGetValue(defaultAgentFromQuery.Config.Id, out var agent4))
        {
            logger.LogDebug("[AgentChatClient] Selected closest default agent: {AgentName} at {Path}", defaultAgentFromQuery.Config.Id, defaultAgentFromQuery.Path);
            return agent4;
        }

        // Return any agent from NodeType namespace (closest first)
        var anyAgent = foundAgents
            .OrderByDescending(a => a.Path.Split('/').Length)
            .FirstOrDefault();

        if (anyAgent.Config != null && agents.TryGetValue(anyAgent.Config.Id, out var agent5))
        {
            logger.LogDebug("[AgentChatClient] Selected closest agent: {AgentName} at {Path}", anyAgent.Config.Id, anyAgent.Path);
            return agent5;
        }

        return null;
    }

    /// <summary>
    /// Simple RSQL-like pattern matching for context selection.
    /// </summary>
    private static bool MatchesContext(string pattern, AgentContext context)
    {
        if (context.Address == null)
            return false;

        var addressStr = context.Address.ToString();

        // Handle address=like=*value* patterns
        if (pattern.StartsWith("address=like="))
        {
            var likePattern = pattern["address=like=".Length..].Trim('*');
            return addressStr.Contains(likePattern, StringComparison.OrdinalIgnoreCase);
        }

        // Handle address.type==value patterns
        if (pattern.StartsWith("address.type=="))
        {
            var expectedType = pattern["address.type==".Length..];
            return context.Address.Type?.Equals(expectedType, StringComparison.OrdinalIgnoreCase) == true;
        }

        // Fallback: simple contains check
        return addressStr.Contains(pattern, StringComparison.OrdinalIgnoreCase);
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
    public async Task InitializeAsync(string? contextPath)
    {
        logger.LogDebug("[AgentChatClient] InitializeAsync called with contextPath: {ContextPath}", contextPath);

        // Load agent configurations from mesh
        agentConfigurations = await LoadAgentConfigurationsAsync(contextPath);

        // Build hierarchy agents ordered by depth (closest first)
        hierarchyAgents = agentConfigurations
            .OrderByDescending(a => a.Id.Split('/').Length)
            .ToList();

        logger.LogDebug("[AgentChatClient] Loaded {Count} agent configurations", agentConfigurations.Count);

        // Create agents if we have a creator
        if (agentCreator != null)
        {
            await CreateAgentsAsync();
        }
    }

    /// <summary>
    /// Creates ChatClientAgent instances for all loaded configurations.
    /// </summary>
    private async Task CreateAgentsAsync()
    {
        if (agentCreator == null)
        {
            logger.LogWarning("[AgentChatClient] No agent creator provided, cannot create agents");
            return;
        }

        var createdAgents = new Dictionary<string, ChatClientAgent>();

        // Order agents: non-delegating first, delegating second, default last
        var orderedAgents = OrderAgentsForCreation(agentConfigurations);

        // First pass: Create all agents in order
        foreach (var agentConfig in orderedAgents)
        {
            var agent = await agentCreator(agentConfig, this, createdAgents, hierarchyAgents);
            createdAgents[agentConfig.Id] = agent;
            agents[agentConfig.Id] = agent;
            logger.LogDebug("[AgentChatClient] Created agent: {AgentId}", agentConfig.Id);
        }

        // Second pass: Update agents with cyclic dependencies
        var cyclicAgents = FindCyclicDelegations(agentConfigurations);
        foreach (var agentConfig in cyclicAgents)
        {
            var updatedAgent = await agentCreator(agentConfig, this, createdAgents, hierarchyAgents);
            createdAgents[agentConfig.Id] = updatedAgent;
            agents[agentConfig.Id] = updatedAgent;
            logger.LogDebug("[AgentChatClient] Updated cyclic agent: {AgentId}", agentConfig.Id);
        }

        logger.LogInformation("[AgentChatClient] Created {Count} agents", agents.Count);
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
    /// Loads agent configurations from mesh for the specified context path.
    /// </summary>
    private async Task<IReadOnlyList<AgentConfiguration>> LoadAgentConfigurationsAsync(string? contextPath)
    {
        if (meshQuery == null)
        {
            logger.LogDebug("[AgentChatClient] IMeshQuery not available, returning empty agent list");
            return Array.Empty<AgentConfiguration>();
        }

        var agentsDict = new Dictionary<string, (AgentConfiguration Config, string Path)>();

        // 1. Get the NodeType of the current node (if contextPath is provided)
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
                        logger.LogDebug("[AgentChatClient] Found NodeType {NodeType} for context {ContextPath}", nodeTypePath, contextPath);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[AgentChatClient] Error querying current node for NodeType at {ContextPath}", contextPath);
            }
        }

        // 2. Query agents from the NodeType namespace (higher priority)
        // Use subtree scope to find agents that are children of the NodeType path
        if (!string.IsNullOrEmpty(nodeTypePath))
        {
            try
            {
                var query = $"path:{nodeTypePath} nodeType:Agent scope:hierarchy";
                await foreach (var node in meshQuery.QueryAsync<MeshNode>(query))
                {
                    if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                    {
                        agentsDict[config.Id] = (config, node.Path ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[AgentChatClient] Error loading agents from NodeType namespace {NodeType}", nodeTypePath);
            }
        }

        // 3. Query agents from the context path namespace
        try
        {
            var query = string.IsNullOrEmpty(contextPath)
                ? "nodeType:Agent scope:selfAndAncestors"
                : $"path:{contextPath} nodeType:Agent scope:selfAndAncestors";

            await foreach (var node in meshQuery.QueryAsync<MeshNode>(query))
            {
                if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                {
                    agentsDict[config.Id] = (config, node.Path ?? "");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AgentChatClient] Error loading agents from context namespace {ContextPath}", contextPath);
        }

        return agentsDict.Values
            .Select(x => x.Config)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.Id)
            .ToList();
    }

    /// <summary>
    /// Returns an ordered list of agents for the current context.
    /// The first agent is the recommended default.
    /// Order: context-matching agents first, then agents closest to context (by path relevance), then others.
    /// Searches both the path namespace and NodeType namespace upwards.
    /// </summary>
    public async Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync()
    {
        // Get all agents from both path and NodeType hierarchies
        var agentsWithPaths = await GetAgentsWithDisplayInfoAsync();

        var result = new List<AgentDisplayInfo>();
        var addedAgentIds = new HashSet<string>();

        // Get context paths for relevance scoring
        var contextPath = Context?.Address?.ToString()?.TrimStart('/') ?? "";
        var nodeTypePath = Context?.Node?.NodeType?.TrimStart('/') ?? "";

        // 1. First, add agents that match ContextMatchPattern (highest priority)
        if (Context != null)
        {
            foreach (var agentInfo in agentsWithPaths)
            {
                var config = agentInfo.AgentConfiguration;
                if (!string.IsNullOrEmpty(config.ContextMatchPattern) && MatchesContext(config.ContextMatchPattern, Context))
                {
                    if (agents.ContainsKey(config.Id) && addedAgentIds.Add(config.Id))
                    {
                        result.Add(agentInfo);
                        logger.LogDebug("[GetOrderedAgentsAsync] Added context-matching agent: {AgentId}", config.Id);
                    }
                }
            }
        }

        // 2. Get remaining agents ordered by path relevance (using shared helper)
        var remainingAgents = agentsWithPaths
            .Where(a => !addedAgentIds.Contains(a.Name) && agents.ContainsKey(a.Name));

        var orderedAgents = AgentOrderingHelper.OrderByRelevance(remainingAgents, contextPath, nodeTypePath);

        foreach (var agentInfo in orderedAgents)
        {
            if (addedAgentIds.Add(agentInfo.Name))
            {
                result.Add(agentInfo);
                logger.LogDebug("[GetOrderedAgentsAsync] Added agent: {AgentId} at path {Path}, relevance: {Relevance}",
                    agentInfo.Name, agentInfo.Path, AgentOrderingHelper.CalculatePathRelevance(agentInfo.Path, contextPath, nodeTypePath));
            }
        }

        logger.LogDebug("[GetOrderedAgentsAsync] Returning {Count} agents, first: {First}",
            result.Count, result.FirstOrDefault()?.Name ?? "none");

        return result;
    }

    /// <summary>
    /// Gets agents with display info by searching both the path namespace and NodeType namespace upwards.
    /// Uses the context's Address for path search and NodeType for type-based search.
    /// </summary>
    private async Task<IReadOnlyList<AgentDisplayInfo>> GetAgentsWithDisplayInfoAsync()
    {
        var agentsDict = new Dictionary<string, (AgentConfiguration Config, string Path)>();

        var contextPath = Context?.Address?.ToString();
        var nodeTypePath = Context?.Node?.NodeType;

        // 1. Query agents from the NodeType namespace (higher priority - closer to the "type" of content)
        // Use subtree scope to find agents that are children of the NodeType path
        if (meshQuery != null && !string.IsNullOrEmpty(nodeTypePath))
        {
            try
            {
                var query = $"path:{nodeTypePath} nodeType:Agent scope:hierarchy";
                logger.LogDebug("[GetAgentsWithDisplayInfoAsync] Querying NodeType namespace: {Query}", query);

                await foreach (var node in meshQuery.QueryAsync<MeshNode>(query))
                {
                    if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                    {
                        agentsDict[config.Id] = (config, node.Path ?? "");
                        logger.LogDebug("[GetAgentsWithDisplayInfoAsync] Found agent {AgentId} at NodeType path {Path}", config.Id, node.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[GetAgentsWithDisplayInfoAsync] Error querying NodeType namespace {NodeType}", nodeTypePath);
            }
        }

        // 2. Query agents from the context path namespace
        if (meshQuery != null)
        {
            try
            {
                var query = string.IsNullOrEmpty(contextPath)
                    ? "nodeType:Agent scope:selfAndAncestors"
                    : $"path:{contextPath} nodeType:Agent scope:selfAndAncestors";
                logger.LogDebug("[GetAgentsWithDisplayInfoAsync] Querying path namespace: {Query}", query);

                await foreach (var node in meshQuery.QueryAsync<MeshNode>(query))
                {
                    if (node.Content is AgentConfiguration config && !agentsDict.ContainsKey(config.Id))
                    {
                        agentsDict[config.Id] = (config, node.Path ?? "");
                        logger.LogDebug("[GetAgentsWithDisplayInfoAsync] Found agent {AgentId} at path {Path}", config.Id, node.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[GetAgentsWithDisplayInfoAsync] Error querying path namespace {ContextPath}", contextPath);
            }
        }

        // 3. If no agents found from queries, fall back to agentConfigurations
        if (agentsDict.Count == 0)
        {
            foreach (var config in agentConfigurations)
            {
                agentsDict[config.Id] = (config, "");
            }
            logger.LogDebug("[GetAgentsWithDisplayInfoAsync] Using {Count} fallback agent configurations", agentsDict.Count);
        }

        // Build display info list
        var result = agentsDict.Values
            .Select(x => new AgentDisplayInfo
            {
                Name = x.Config.Id,
                Path = x.Path,
                Description = x.Config.Description ?? x.Config.DisplayName ?? x.Config.Id,
                GroupName = x.Config.GroupName,
                DisplayOrder = x.Config.DisplayOrder,
                IndentLevel = 0, // Will be calculated if needed
                Icon = x.Config.Icon,
                CustomIconSvg = x.Config.CustomIconSvg,
                AgentConfiguration = x.Config
            })
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.Name)
            .ToList();

        logger.LogDebug("[GetAgentsWithDisplayInfoAsync] Returning {Count} agents", result.Count);
        return result;
    }

    public Task ResumeAsync(ChatConversation conversation)
    {
        // With AgentThread, we don't need to manually restore history
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
