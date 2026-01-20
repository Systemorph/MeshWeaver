using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph.Configuration;
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

public class AgentChatClient(
    IReadOnlyList<AgentConfiguration> agentConfigurations,
    IServiceProvider serviceProvider)
    : IAgentChat
{
    private readonly IMessageHub hub = serviceProvider.GetRequiredService<IMessageHub>();
    private readonly ILogger<AgentChatClient> logger = serviceProvider.GetRequiredService<ILogger<AgentChatClient>>();
    private readonly IChatPersistenceService persistenceService = serviceProvider.GetRequiredService<IChatPersistenceService>();
    private readonly IMeshQuery? meshQuery = serviceProvider.GetService<IMeshQuery>();
    private readonly Dictionary<string, AIAgent> agents = new();
    private readonly Dictionary<string, AgentConfiguration> agentConfigsById = agentConfigurations.ToDictionary(a => a.Id);
    private readonly Queue<ChatLayoutAreaContent> queuedLayoutAreaContent = new();
    private string currentThreadId = Guid.NewGuid().AsString();
    private string? currentAgentName;
    private AgentThread? sharedThread;

    public void AddAgent(string name, AIAgent agent)
    {
        agents[name] = agent;
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

        // 2. Try to find agent based on context (using direct mesh query)
        var contextPath = Context?.Address?.ToString();
        if (!string.IsNullOrEmpty(contextPath) && meshQuery != null)
        {
            var selectedAgent = await FindAgentForContextAsync(contextPath);
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
    /// Finds the best agent for a given context path by:
    /// 1. Getting the node at the path to find its NodeType
    /// 2. Querying for agents in the NodeType namespace (scope:myselfAndAncestors)
    /// 3. Querying for agents in the path namespace (scope:myselfAndAncestors)
    /// 4. Matching by ContextMatchPattern or returning closest default agent
    /// </summary>
    private async Task<AIAgent?> FindAgentForContextAsync(string contextPath)
    {
        if (meshQuery == null) return null;

        // Get the current node to find its NodeType
        string? nodeTypePath = null;
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

        var foundAgents = new List<(AgentConfiguration Config, string Path)>();

        // Query agents from NodeType namespace (higher priority)
        if (!string.IsNullOrEmpty(nodeTypePath))
        {
            try
            {
                var query = $"path:{nodeTypePath} nodeType:Agent scope:myselfAndAncestors";
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
            var query = $"path:{contextPath} nodeType:Agent scope:myselfAndAncestors";
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
        if (Context != null)
        {
            foreach (var (config, _) in foundAgents)
            {
                if (!string.IsNullOrEmpty(config.ContextMatchPattern) && MatchesContext(config.ContextMatchPattern, Context))
                {
                    if (agents.TryGetValue(config.Id, out var agent))
                    {
                        logger.LogDebug("[AgentChatClient] Selected agent by context pattern: {AgentName}", config.Id);
                        return agent;
                    }
                }
            }
        }

        // Return closest default agent (longest path)
        var defaultAgent = foundAgents
            .Where(a => a.Config.IsDefault)
            .OrderByDescending(a => a.Path.Split('/').Length)
            .FirstOrDefault();

        if (defaultAgent.Config != null && agents.TryGetValue(defaultAgent.Config.Id, out var agent2))
        {
            logger.LogDebug("[AgentChatClient] Selected closest default agent: {AgentName} at {Path}", defaultAgent.Config.Id, defaultAgent.Path);
            return agent2;
        }

        // Return any agent from NodeType namespace (closest first)
        var anyAgent = foundAgents
            .OrderByDescending(a => a.Path.Split('/').Length)
            .FirstOrDefault();

        if (anyAgent.Config != null && agents.TryGetValue(anyAgent.Config.Id, out var agent3))
        {
            logger.LogDebug("[AgentChatClient] Selected closest agent: {AgentName} at {Path}", anyAgent.Config.Id, anyAgent.Path);
            return agent3;
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
