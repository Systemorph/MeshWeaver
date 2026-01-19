using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Services;
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
    IAgentResolver agentResolver,
    IServiceProvider serviceProvider)
    : IAgentChat
{
    private readonly IMessageHub hub = serviceProvider.GetRequiredService<IMessageHub>();
    private readonly ILogger<AgentChatClient> logger = serviceProvider.GetRequiredService<ILogger<AgentChatClient>>();
    private readonly IChatPersistenceService persistenceService = serviceProvider.GetRequiredService<IChatPersistenceService>();
    private readonly Dictionary<string, AIAgent> agents = new();
    private readonly Dictionary<string, AgentConfiguration> agentConfigsById = agentConfigurations.ToDictionary(a => a.Id);
    private readonly Queue<ChatLayoutAreaContent> queuedLayoutAreaContent = new();
    private string currentThreadId = Guid.NewGuid().AsString();
    private string? currentAgentName;
    private readonly Address? lastContextAddress;
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
        logger.LogDebug("Available agents: {Agents}", string.Join(", ", agents.Keys));
        logger.LogDebug("Agent configurations with ContextMatchPattern: {AgentConfigs}",
            string.Join(", ", agentConfigurations.Where(a => !string.IsNullOrEmpty(a.ContextMatchPattern)).Select(a => a.Id)));

        // Check for explicit agent mention using @agent/Name format
        if (lastMessage != null)
        {
            var text = ExtractTextFromMessage(lastMessage);
            var agentMatch = AgentReferencePattern.Match(text);
            if (agentMatch.Success)
            {
                var agentName = agentMatch.Groups[1].Value;
                // Try exact match first, then case-insensitive
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

        // Check if we should reselect agent based on context
        var currentAddress = Context?.Address;
        var shouldReselectAgent = lastContextAddress != currentAddress || string.IsNullOrEmpty(currentAgentName);

        logger.LogDebug("Should reselect agent: {ShouldReselect} (lastAddress={LastAddress}, currentAddress={CurrentAddress}, currentAgentName={CurrentAgentName})",
            shouldReselectAgent, lastContextAddress, currentAddress, currentAgentName);

        if (shouldReselectAgent)
        {
            // Try to find an agent that matches the context using ContextMatchPattern
            if (Context != null)
            {
                logger.LogDebug("[AgentChatClient] Looking for agents matching context...");
                var matchingAgents = await agentResolver.FindMatchingAgentsAsync(Context, null);
                logger.LogDebug("[AgentChatClient] Found {Count} agents matching context", matchingAgents.Count);

                foreach (var agentConfig in matchingAgents)
                {
                    if (agents.TryGetValue(agentConfig.Id, out var agent))
                    {
                        logger.LogDebug("Selected agent by context match: {AgentName}", agentConfig.Id);
                        return agent;
                    }
                    else
                    {
                        logger.LogWarning("Agent {AgentName} matches context but not found in agents dictionary", agentConfig.Id);
                    }
                }

                logger.LogDebug("No agent matched the context");

                // Try to find an agent from the node's NodeType namespace
                // E.g., if at ACME/ProductLaunch with NodeType=ACME/Project, search ACME/Project for agents
                var contextPath = Context.Address?.ToString();
                if (!string.IsNullOrEmpty(contextPath))
                {
                    var meshQuery = serviceProvider.GetService<IMeshQuery>();
                    if (meshQuery != null)
                    {
                        // Get the node at the context path to find its NodeType
                        var nodes = await meshQuery.QueryAsync<MeshNode>($"path:{contextPath} scope:self").ToListAsync();
                        var node = nodes.FirstOrDefault();
                        if (node?.NodeType != null && node.NodeType != "Markdown" && node.NodeType != "Agent")
                        {
                            // The NodeType might be a path like "ACME/Project" - search for agents there
                            logger.LogDebug("[AgentChatClient] Searching NodeType namespace: {NodeType}", node.NodeType);
                            var typeAgents = await agentResolver.GetAgentsForContextAsync(node.NodeType);

                            foreach (var agentConfig in typeAgents)
                            {
                                if (agents.TryGetValue(agentConfig.Id, out var agent))
                                {
                                    logger.LogDebug("Selected agent from NodeType namespace: {AgentName}", agentConfig.Id);
                                    return agent;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Use current agent if we have one
        if (!string.IsNullOrEmpty(currentAgentName) && agents.TryGetValue(currentAgentName, out var currentAgent))
        {
            logger.LogDebug("Using current agent: {AgentName}", currentAgentName);
            return currentAgent;
        }

        // Find default agent (using IsDefault property)
        var defaultAgentConfig = agentConfigurations.FirstOrDefault(a => a.IsDefault);

        if (defaultAgentConfig != null && agents.TryGetValue(defaultAgentConfig.Id, out var defaultAgent))
        {
            logger.LogDebug("Selected default agent: {AgentName}", defaultAgentConfig.Id);
            return defaultAgent;
        }

        // Return first agent as fallback
        var fallbackAgent = agents.Values.FirstOrDefault();
        logger.LogDebug("Using fallback agent: {AgentName}", fallbackAgent?.Name ?? "null");
        return fallbackAgent;
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
