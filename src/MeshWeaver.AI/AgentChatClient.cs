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
    private readonly Queue<ChatLayoutAreaContent> queuedLayoutAreaContent = new();
    private string currentThreadId = "default";
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

    public void SetThreadId(string threadId)
    {
        if (string.IsNullOrEmpty(threadId))
            throw new ArgumentException("Thread ID cannot be null or empty", nameof(threadId));

        currentThreadId = threadId;
        logger.LogInformation("Switched to thread: {ThreadId}", threadId);
    }

    public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Select which agent to use
        var agent = SelectAgent(messages.LastOrDefault());
        if (agent == null)
        {
            yield return new ChatMessage(ChatRole.Assistant, "No suitable agent found to handle the request.");
            yield break;
        }

        currentAgentName = agent.Name;

        // Build the user message with context
        var userMessage = BuildMessageWithContext(messages);

        // Get response from the agent
        var response = await agent.RunAsync(userMessage, cancellationToken: cancellationToken);

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
        // Select which agent to use
        var agent = SelectAgent(messages.LastOrDefault());
        if (agent == null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "No suitable agent found to handle the request.");
            yield break;
        }

        currentAgentName = agent.Name;

        // Build the user message with context
        var userMessage = BuildMessageWithContext(messages);

        // Get streaming response from the agent
        await foreach (var update in agent.RunStreamingAsync(userMessage, cancellationToken: cancellationToken))
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
    }

    private List<ChatMessage> PrepareHistoryWithContext(List<ChatMessage> conversationHistory)
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
