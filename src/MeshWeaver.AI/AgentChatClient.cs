using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

public class AgentChatClient(
    IReadOnlyDictionary<string, AIAgent> agents,
    IReadOnlyDictionary<string, IAgentDefinition> agentDefinitions,
    IServiceProvider serviceProvider) : IAgentChat
{
    private readonly IMessageHub hub = serviceProvider.GetRequiredService<IMessageHub>();
    private readonly List<ChatMessage> conversationHistory = new();
    private readonly Queue<ChatLayoutAreaContent> queuedLayoutAreaContent = new();
    private string? currentAgentName;
    private Address? lastContextAddress;

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

        // Process messages and get responses
        var processedMessages = messages.Select(ProcessMessageWithContext).ToList();

        // Select which agent to use
        var agent = SelectAgent(processedMessages.LastOrDefault());
        if (agent == null)
        {
            yield return new ChatMessage(ChatRole.Assistant, "No suitable agent found to handle the request.");
            yield break;
        }

        currentAgentName = agent.Name;

        // Get response from the agent
        var response = await agent.RunAsync(conversationHistory, cancellationToken: cancellationToken);

        if (response.Messages != null)
        {
            foreach (var responseMsg in response.Messages)
            {
                conversationHistory.Add(responseMsg);
                yield return responseMsg;
            }
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

        // Process messages and get responses
        var processedMessages = messages.Select(ProcessMessageWithContext).ToList();

        // Select which agent to use
        var agent = SelectAgent(processedMessages.LastOrDefault());
        if (agent == null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "No suitable agent found to handle the request.");
            yield break;
        }

        currentAgentName = agent.Name;

        // Get streaming response from the agent
        await foreach (var update in agent.RunStreamingAsync(conversationHistory, cancellationToken: cancellationToken))
        {
            // Convert from agent updates to chat response updates
            if (update.Text != null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, update.Text)
                {
                    AuthorName = currentAgentName ?? "Assistant"
                };
            }
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

    private ChatMessage ProcessMessageWithContext(ChatMessage message)
    {
        if (message.Role != ChatRole.User)
            return message;

        // Extract original text content
        var originalText = ExtractTextFromMessage(message);

        // Format the final message with context
        var messageWithContext = FormatMessageWithContext(originalText);

        // Always update the last context address after processing
        lastContextAddress = Context?.Address;

        return new ChatMessage(ChatRole.User, [new TextContent(messageWithContext)])
        {
            AuthorName = message.AuthorName
        };
    }

    private string FormatMessageWithContext(string originalText)
    {
        if (Context == null)
            return originalText;

        var contextJson = JsonSerializer.Serialize(Context!, hub.JsonSerializerOptions);
        return $"{originalText}\n<Context>\n{contextJson}\n</Context>";
    }

    private AIAgent? SelectAgent(ChatMessage? lastMessage)
    {
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
                    return agent;
                }
            }
        }

        // Check if we should reselect agent based on context
        var currentAddress = Context?.Address;
        var shouldReselectAgent = lastContextAddress != currentAddress || string.IsNullOrEmpty(currentAgentName);

        if (shouldReselectAgent)
        {
            // Try to find an agent that matches the context
            if (Context != null)
            {
                var matchingAgent = agentDefinitions.Values
                    .OfType<IAgentWithContext>()
                    .FirstOrDefault(a => a.Matches(Context));

                if (matchingAgent != null && agents.TryGetValue(matchingAgent.Name, out var agent))
                {
                    return agent;
                }
            }
        }

        // Use current agent if we have one
        if (!string.IsNullOrEmpty(currentAgentName) && agents.TryGetValue(currentAgentName, out var currentAgent))
        {
            return currentAgent;
        }

        // Find default agent
        var defaultAgentDefinition = agentDefinitions.Values
            .FirstOrDefault(a => a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());

        if (defaultAgentDefinition != null && agents.TryGetValue(defaultAgentDefinition.Name, out var defaultAgent))
        {
            return defaultAgent;
        }

        // Return first agent as fallback
        return agents.Values.FirstOrDefault();
    }

    public void SetContext(AgentContext applicationContext)
    {
        Context = applicationContext;
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

    public string Delegate(string agentName, string message, bool askUserFeedback = false)
    {
        agentName = agentName.TrimStart('@');

        // Check if the requested agent exists
        if (!agents.ContainsKey(agentName))
        {
            return $"Agent '{agentName}' not found. Available agents: {string.Join(", ", agents.Keys)}";
        }

        // For now, simple delegation - just switch to the agent
        currentAgentName = agentName;
        return $"Delegated to {agentName}. You can now interact with this agent.";
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
