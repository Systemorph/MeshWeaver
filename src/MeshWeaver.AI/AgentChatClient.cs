using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
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
    private readonly Dictionary<string, AIAgent> agents = new();
    private readonly IReadOnlyDictionary<string, IAgentDefinition> agentDefinitions;
    private readonly List<ChatMessage> conversationHistory = new();
    private readonly Queue<ChatLayoutAreaContent> queuedLayoutAreaContent = new();
    private string? currentAgentName;
    private Address? lastContextAddress;

    public AgentChatClient(
        IReadOnlyDictionary<string, IAgentDefinition> agentDefinitions,
        IServiceProvider serviceProvider)
    {
        this.agentDefinitions = agentDefinitions;
        this.hub = serviceProvider.GetRequiredService<IMessageHub>();
        this.logger = serviceProvider.GetRequiredService<ILogger<AgentChatClient>>();
    }

    public void AddAgent(string name, AIAgent agent)
    {
        agents[name] = agent;
    }

    public AgentContext? Context { get; private set; }

    public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Add user messages to history
        foreach (var message in messages)
        {
            conversationHistory.Add(message);
        }

        // Add context as system message if we have context
        var historyWithContext = PrepareHistoryWithContext();

        // Select which agent to use
        var agent = SelectAgent(messages.LastOrDefault());
        if (agent == null)
        {
            yield return new ChatMessage(ChatRole.Assistant, "No suitable agent found to handle the request.");
            yield break;
        }

        currentAgentName = agent.Name;

        // Get response from the agent with context included
        var response = await agent.RunAsync(historyWithContext, cancellationToken: cancellationToken);

        foreach (var responseMsg in response.Messages)
        {
            conversationHistory.Add(responseMsg);

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
        // Add user messages to history
        foreach (var message in messages)
        {
            conversationHistory.Add(message);
        }

        // Add context as system message if we have context
        var historyWithContext = PrepareHistoryWithContext();

        // Select which agent to use
        var agent = SelectAgent(messages.LastOrDefault());
        if (agent == null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "No suitable agent found to handle the request.");
            yield break;
        }

        currentAgentName = agent.Name;

        // Get streaming response from the agent with context included
        await foreach (var update in agent.RunStreamingAsync(historyWithContext, cancellationToken: cancellationToken))
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
                        logger.LogInformation("Agent {AgentName} received result from tool",
                            currentAgentName);
                        // Don't yield function results to the UI
                    }
                }
            }

            // Convert from agent updates to chat response updates
            yield return new ChatResponseUpdate(ChatRole.Assistant, update.Text)
            {
                AuthorName = currentAgentName ?? "Assistant"
            };
        }

        // Check for any queued layout area content
        while (queuedLayoutAreaContent.Count > 0)
        {
            var layoutAreaContent = queuedLayoutAreaContent.Dequeue();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [layoutAreaContent])
            {
                AuthorName = currentAgentName ?? "Assistant"
            };
        }

        // Handle pending delegation
        if (pendingDelegationAgent != null && pendingDelegationMessage != null)
        {
            var delegateToAgent = pendingDelegationAgent;
            var delegateMessage = pendingDelegationMessage;

            // Clear pending delegation
            pendingDelegationAgent = null;
            pendingDelegationMessage = null;

            // Switch to the delegated agent
            if (agents.TryGetValue(delegateToAgent, out var delegatedAgent))
            {
                currentAgentName = delegateToAgent;
                logger.LogInformation("Executing delegation to {AgentName}", delegateToAgent);

                // Add the delegation message to history
                var delegationUserMessage = new ChatMessage(ChatRole.User, delegateMessage);
                conversationHistory.Add(delegationUserMessage);

                // Get context and run the delegated agent
                var delegationHistory = PrepareHistoryWithContext();

                // Stream response from delegated agent
                await foreach (var delegateUpdate in delegatedAgent.RunStreamingAsync(delegationHistory, cancellationToken: cancellationToken))
                {
                    // Forward the complete update with all contents (including FunctionCallContent)
                    if (delegateUpdate.Contents.Count > 0)
                    {
                        foreach (var content in delegateUpdate.Contents)
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
                                // Don't yield function results to the UI
                            }
                        }
                    }

                    yield return new ChatResponseUpdate(ChatRole.Assistant, delegateUpdate.Text)
                    {
                        AuthorName = currentAgentName ?? "Assistant"
                    };
                }
            }
        }
    }

    private List<ChatMessage> PrepareHistoryWithContext()
    {
        var history = new List<ChatMessage>();

        // Add context as a system message at the start if we have context
        if (Context != null)
        {
            var contextJson = JsonSerializer.Serialize(Context, hub.JsonSerializerOptions);
            var contextMessage = $"""
                # Current Application Context

                The user is currently viewing the following page/entity in the application:

                ```json
                {contextJson}
                ```

                Key information:
                - Address Type: {Context.Address?.Type ?? "N/A"}
                - Address ID: {Context.Address?.Id ?? "N/A"}
                - Layout Area: {Context.LayoutArea?.Area ?? "N/A"}

                Use this context information when answering the user's questions or performing actions.
                For example, if the user asks about "this pricing" or "current files", they are referring to the entity specified in the context above.
                """;

            history.Add(new ChatMessage(ChatRole.System, contextMessage));
            logger.LogDebug("Added context system message: Address={Address}, LayoutArea={LayoutArea}",
                Context.Address, Context.LayoutArea?.Area);
        }

        // Add all conversation history
        history.AddRange(conversationHistory);

        // Update last context address for change detection
        lastContextAddress = Context?.Address;

        return history;
    }

    private AIAgent? SelectAgent(ChatMessage? lastMessage)
    {
        logger.LogDebug("SelectAgent called. Current context: {Context}", Context != null ? $"Address={Context.Address}, LayoutArea={Context.LayoutArea?.Area}" : "null");
        logger.LogDebug("Available agents: {Agents}", string.Join(", ", agents.Keys));
        logger.LogDebug("Agent definitions with IAgentWithContext: {AgentDefinitions}",
            string.Join(", ", agentDefinitions.Values.OfType<IAgentWithContext>().Select(a => a.Name)));

        // Check for explicit agent mention
        if (lastMessage != null)
        {
            var text = ExtractTextFromMessage(lastMessage);
            if (text.TrimStart().StartsWith("@"))
            {
                var trimmedText = text.TrimStart();
                var spaceIndex = trimmedText.IndexOf(' ');
                var agentName = spaceIndex > 0 ? trimmedText.Substring(1, spaceIndex - 1) : trimmedText.Substring(1);

                if (agents.TryGetValue(agentName, out var agent))
                {
                    logger.LogDebug("Selected agent by explicit mention: {AgentName}", agentName);
                    return agent;
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
            // Try to find an agent that matches the context
            if (Context != null)
            {
                var agentsWithContext = agentDefinitions.Values.OfType<IAgentWithContext>().ToList();
                logger.LogDebug("Checking {Count} agents with context", agentsWithContext.Count);

                foreach (var agentDef in agentsWithContext)
                {
                    var matches = agentDef.Matches(Context);
                    logger.LogDebug("Agent {AgentName} matches context: {Matches}", agentDef.Name, matches);

                    if (matches)
                    {
                        if (agents.TryGetValue(agentDef.Name, out var agent))
                        {
                            logger.LogDebug("Selected agent by context match: {AgentName}", agentDef.Name);
                            return agent;
                        }
                        else
                        {
                            logger.LogWarning("Agent {AgentName} matches context but not found in agents dictionary", agentDef.Name);
                        }
                    }
                }

                logger.LogDebug("No agent matched the context");
            }
        }

        // Use current agent if we have one
        if (!string.IsNullOrEmpty(currentAgentName) && agents.TryGetValue(currentAgentName, out var currentAgent))
        {
            logger.LogDebug("Using current agent: {AgentName}", currentAgentName);
            return currentAgent;
        }

        // Find default agent
        var defaultAgentDefinition = agentDefinitions.Values
            .FirstOrDefault(a => a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());

        if (defaultAgentDefinition != null && agents.TryGetValue(defaultAgentDefinition.Name, out var defaultAgent))
        {
            logger.LogDebug("Selected default agent: {AgentName}", defaultAgentDefinition.Name);
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
        // Filter out UI-specific content that should not be passed to agents
        var messagesToResume = conversation.Messages
            .Where(m => !m.Contents.Any(c => c is ChatLayoutAreaContent or ChatDelegationContent))
            .ToList();

        conversationHistory.AddRange(messagesToResume);
        return Task.CompletedTask;
    }

    private string? pendingDelegationAgent;
    private string? pendingDelegationMessage;

    public string Delegate(string agentName, string message, bool askUserFeedback = false)
    {
        agentName = agentName.TrimStart('@');

        // Check if the requested agent exists
        if (!agents.ContainsKey(agentName))
        {
            return $"Agent '{agentName}' not found. Available agents: {string.Join(", ", agents.Keys)}";
        }

        // Schedule the delegation for after current agent completes
        pendingDelegationAgent = agentName;
        pendingDelegationMessage = message;

        logger.LogInformation("Delegation scheduled to {AgentName} with message: {Message}",
            agentName, message);

        return $"Delegating to {agentName}...";
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
