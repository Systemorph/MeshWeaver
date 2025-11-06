using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace MeshWeaver.AI;

public class AgentChatClient : IAgentChat
{
    private readonly IMessageHub hub;
    private readonly ILogger<AgentChatClient> logger;
    private readonly IReadOnlyDictionary<string, IAgentDefinition> agentDefinitions;
    private readonly Workflow workflow;
    private readonly List<ChatMessage> conversationHistory = new();
    private readonly Queue<ChatLayoutAreaContent> queuedLayoutAreaContent = new();
    private Address? lastContextAddress;

    public AgentChatClient(
        IReadOnlyDictionary<string, IAgentDefinition> agentDefinitions,
        Workflow workflow,
        IServiceProvider serviceProvider)
    {
        this.agentDefinitions = agentDefinitions;
        this.workflow = workflow;
        this.hub = serviceProvider.GetRequiredService<IMessageHub>();
        this.logger = serviceProvider.GetRequiredService<ILogger<AgentChatClient>>();
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

        // Prepare the message with context
        var lastMessage = messages.LastOrDefault() ?? new ChatMessage(ChatRole.User, "");
        var messageText = ExtractTextFromMessage(lastMessage);
        var messageWithContext = PrepareMessageWithContext(messageText);

        var userMessage = new ChatMessage(ChatRole.User, messageWithContext);

        // Execute the workflow with the message
        var run = await InProcessExecution.StreamAsync(workflow, userMessage);

        // Trigger agent processing
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        // Watch for events and collect messages
        var responseMessages = new List<ChatMessage>();
        await foreach (WorkflowEvent evt in run.WatchStreamAsync())
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (evt is AgentRunUpdateEvent agentUpdate)
            {
                // Extract the response text from the agent update
                var responseText = agentUpdate.Data?.ToString();
                if (!string.IsNullOrEmpty(responseText))
                {
                    var responseMessage = new ChatMessage(ChatRole.Assistant, responseText)
                    {
                        AuthorName = agentUpdate.ExecutorId ?? "Assistant"
                    };
                    conversationHistory.Add(responseMessage);
                    responseMessages.Add(responseMessage);
                }
            }
        }

        // Yield collected messages
        foreach (var msg in responseMessages)
        {
            yield return msg;
        }

        // Check for any queued layout area content
        while (queuedLayoutAreaContent.Count > 0)
        {
            var layoutAreaContent = queuedLayoutAreaContent.Dequeue();
            var layoutAreaMessage = new ChatMessage(ChatRole.Assistant, [layoutAreaContent])
            {
                AuthorName = "Assistant"
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

        // Prepare the message with context
        var lastMessage = messages.LastOrDefault() ?? new ChatMessage(ChatRole.User, "");
        var messageText = ExtractTextFromMessage(lastMessage);
        var messageWithContext = PrepareMessageWithContext(messageText);

        var userMessage = new ChatMessage(ChatRole.User, messageWithContext);

        // Execute the workflow with streaming
        var run = await InProcessExecution.StreamAsync(workflow, userMessage);

        // Trigger agent processing
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        // Stream events in real-time
        await foreach (WorkflowEvent evt in run.WatchStreamAsync())
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (evt is AgentRunUpdateEvent agentUpdate)
            {
                // Stream the response text as it arrives
                var responseText = agentUpdate.Data?.ToString();
                if (!string.IsNullOrEmpty(responseText))
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, responseText)
                    {
                        AuthorName = agentUpdate.ExecutorId ?? "Assistant"
                    };

                    // Also add to history
                    var assistantMessage = new ChatMessage(ChatRole.Assistant, responseText)
                    {
                        AuthorName = agentUpdate.ExecutorId ?? "Assistant"
                    };
                    conversationHistory.Add(assistantMessage);
                }
            }
        }

        // Check for any queued layout area content
        while (queuedLayoutAreaContent.Count > 0)
        {
            var layoutAreaContent = queuedLayoutAreaContent.Dequeue();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [layoutAreaContent])
            {
                AuthorName = "Assistant"
            };
        }
    }

    private string PrepareMessageWithContext(string message)
    {
        if (Context == null)
            return message;

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

            User message: {message}
            """;

        return contextMessage;
    }

    private string ExtractTextFromMessage(ChatMessage message)
    {
        if (message.Text != null)
            return message.Text;

        var textContents = message.Contents
            .OfType<TextContent>()
            .Select(c => c.Text);

        return string.Join("\n", textContents);
    }

    public void SetContext(AgentContext? context)
    {
        Context = context;

        if (context?.Address != lastContextAddress)
        {
            lastContextAddress = context?.Address;
            logger.LogInformation("Context changed to: {Address}", context?.Address);
        }
    }

    public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl)
    {
        var content = new ChatLayoutAreaContent(layoutAreaControl);
        queuedLayoutAreaContent.Enqueue(content);
    }

    public Task ResumeAsync(ChatConversation conversation)
    {
        // Resume the conversation history
        conversationHistory.Clear();
        conversationHistory.AddRange(conversation.Messages);

        logger.LogInformation("Resumed conversation with {MessageCount} messages", conversation.Messages.Count);

        return Task.CompletedTask;
    }
}
