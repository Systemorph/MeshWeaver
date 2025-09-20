using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace MeshWeaver.AI;

public class AgentChatClient(
    AgentChat agentChat,
    IReadOnlyDictionary<string, IAgentDefinition> agentDefinitions,
    IServiceProvider serviceProvider) : IAgentChat
{
    private readonly IMessageHub hub = serviceProvider.GetRequiredService<IMessageHub>();
    private readonly DelegationState delegationState = new();
    private readonly Queue<ChatLayoutAreaContent> queuedLayoutAreaContent = new();
    private string? currentRunningAgent;
    private string? lastContextAddress;
    private readonly AgentContext currentContext;

    public AgentContext? Context { get; private set; }

    public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AddUserMessages(messages);

        var maxDelegations = 10; // Prevent infinite delegation loops
        var delegationCount = 0;

        while (delegationCount <= maxDelegations)
        {
            var hasContent = false;
            await foreach (var content in agentChat.InvokeAsync(cancellationToken))
            {
                hasContent = true;
                var aiContent = ConvertToExtensionsAI(content);
                if (aiContent == null) continue;

                // Track the current running agent
                currentRunningAgent = content.AuthorName ?? "Assistant";


                var message = new ChatMessage(ChatRole.Assistant, [aiContent])
                {
                    AuthorName = new(content.AuthorName ?? "Assistant")
                };

                yield return message;

                // Check for any queued layout area content and yield as messages
                while (queuedLayoutAreaContent.Count > 0)
                {
                    var layoutAreaContent = queuedLayoutAreaContent.Dequeue();
                    var layoutAreaMessage = new ChatMessage(ChatRole.Assistant, [layoutAreaContent])
                    {
                        AuthorName = new(currentRunningAgent ?? "Assistant")
                    };
                    yield return layoutAreaMessage;
                }
            }

            // Only check for delegations if we actually got content from an agent
            if (!hasContent)
            {
                break; // No content, exit loop
            }

            // Check for pending delegations after agent completes
            if (delegationState.HasPendingDelegation)
            {
                var delegationInstruction = delegationState.PendingDelegation;
                var delegationMessage = delegationState.ProcessPendingDelegation();
                if (delegationMessage != null && delegationInstruction != null)
                {
                    // Create and yield delegation message for the GUI
                    var delegationContent = new ChatDelegationContent(
                        delegationInstruction.DelegatingAgent,
                        delegationInstruction.AgentName,
                        delegationInstruction.Message,
                        delegationInstruction.Type == DelegationType.ReplyTo);

                    var delegationChatMessage = new ChatMessage(ChatRole.Assistant, [delegationContent])
                    {
                        AuthorName = new(delegationInstruction.DelegatingAgent)
                    };

                    yield return delegationChatMessage;

                    // Add delegation message and continue the loop
                    agentChat.AddChatMessage(ConvertToAgentChat(ProcessMessageWithContext(delegationMessage)));
                    delegationCount++;
                }
                else
                {
                    break; // No delegation message to process
                }
            }
            else
            {
                break; // No more delegations
            }
        }
    }
    private void AddUserMessages(IEnumerable<ChatMessage> messages)
    {
        var messagesToProcess = new List<ChatMessage>();

        // Check for pending reply that needs user input
        if (delegationState.HasPendingReply)
        {
            var userMessage = messages.FirstOrDefault(m => m.Role == ChatRole.User);
            if (userMessage != null)
            {
                var userInput = ExtractTextFromMessage(userMessage);
                var delegationMessage = delegationState.ProcessPendingReply(userInput);
                if (delegationMessage != null)
                {
                    messagesToProcess.Add(delegationMessage);
                }
            }
        }

        // Add the original messages
        messagesToProcess.AddRange(messages);

        var processedMessages = messagesToProcess.Select(ProcessMessageWithContext).ToArray();
        agentChat.AddChatMessages(processedMessages.Select(ConvertToAgentChat).ToArray());
    }

    private ChatMessage ProcessMessageWithContext(ChatMessage message)
    {
        if (message.Role != ChatRole.User)
            return message;

        // Extract original text content
        var originalText = ExtractTextFromMessage(message);

        // Determine the selected agent
        var selectedAgent = DetermineSelectedAgent(originalText);

        // Update the current running agent if we're routing to a specific agent
        if (!string.IsNullOrEmpty(selectedAgent))
        {
            currentRunningAgent = selectedAgent;
        }

        // Format the final message with context
        var messageWithContext = FormatMessageWithContext(originalText, selectedAgent);

        // Always update the last context address after processing
        lastContextAddress = Context?.Address;

        return new ChatMessage(ChatRole.User, [new TextContent(messageWithContext)])
        {
            AuthorName = message.AuthorName
        };
    }

    private string? DetermineSelectedAgent(string originalText)
    {
        // Check for explicit agent targeting
        if (originalText.TrimStart().StartsWith("@"))
        {
            // Validate that it's actually targeting a valid agent
            var trimmedText = originalText.TrimStart();
            var spaceIndex = trimmedText.IndexOf(' ');
            var agentName = spaceIndex > 0 ? trimmedText.Substring(1, spaceIndex - 1) : trimmedText.Substring(1);

            var targetAgent = agentChat.Agents.FirstOrDefault(a => a.Name is not null && a.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase));
            if (targetAgent != null)
            {
                // Valid agent targeted - track it and don't override the message
                currentRunningAgent = targetAgent.Name;
                return null; // Don't add another agent directive
            }
            // If not a valid agent name, continue with normal agent selection logic
        }

        // Check if we should trigger agent reselection
        var currentAddress = Context?.Address;
        var shouldReselectAgent = ShouldReselectAgent(currentAddress);

        if (shouldReselectAgent)
        {
            // Trigger agent selection logic
            return SelectAgentForContext(Context);
        }
        else if (!string.IsNullOrEmpty(currentRunningAgent))
        {
            // Continue with current agent
            return currentRunningAgent;
        }

        return null; // Use default routing
    }

    private bool ShouldReselectAgent(string? currentAddress)
    {
        // If the address actually changed, reselect agent
        if (lastContextAddress != currentAddress)
            return true;

        // If we don't have a current running agent, try to select one
        if (string.IsNullOrEmpty(currentRunningAgent))
            return true;

        return false; // Keep current agent
    }

    private string FormatMessageWithContext(string originalText, string? selectedAgent)
    {
        var contextJson = JsonSerializer.Serialize(Context!, hub.JsonSerializerOptions);

        if (!string.IsNullOrEmpty(selectedAgent))
        {
            // Route to the selected agent
            var agentDirective = $"@{selectedAgent} {originalText}";
            return $"{agentDirective}\n<Context>\n{contextJson}\n</Context>";
        }
        else
        {
            // Use default routing or user-specified agent targeting
            return $"{originalText}\n<Context>\n{contextJson}\n</Context>";
        }
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AddUserMessages(messages);

        var maxDelegations = 10; // Prevent infinite delegation loops
        var delegationCount = 0;

        while (delegationCount <= maxDelegations)
        {
            var hasContent = false;
            await foreach (var update in agentChat.InvokeStreamingAsync(cancellationToken))
            {
                hasContent = true;
                var converted = ConvertToExtensionsAI(update);
                if (converted == null) continue;

                // Track the current running agent
                if (!string.IsNullOrEmpty(update.AuthorName))
                {
                    currentRunningAgent = update.AuthorName;
                }

                yield return converted;
            }

            // Check for any queued layout area content and yield as response updates
            while (queuedLayoutAreaContent.Count > 0)
            {
                var layoutAreaContent = queuedLayoutAreaContent.Dequeue();
                yield return new ChatResponseUpdate(ChatRole.Assistant, [layoutAreaContent])
                {
                    AuthorName = currentRunningAgent ?? "Assistant"
                };
            }

            // Only check for delegations if we actually got content from an agent
            if (!hasContent)
            {
                break; // No content, exit loop
            }

            // Check for pending delegations after agent completes
            if (delegationState.HasPendingDelegation)
            {
                var delegationInstruction = delegationState.PendingDelegation;
                var delegationMessage = delegationState.ProcessPendingDelegation();
                if (delegationMessage != null && delegationInstruction != null)
                {
                    // Create and yield delegation message for the GUI
                    var delegationContent = new ChatDelegationContent(
                        delegationInstruction.DelegatingAgent,
                        delegationInstruction.AgentName,
                        delegationInstruction.Message,
                        delegationInstruction.Type == DelegationType.ReplyTo);

                    yield return new ChatResponseUpdate(ChatRole.Assistant, [delegationContent])
                    {
                        AuthorName = delegationInstruction.DelegatingAgent
                    };

                    // Add delegation message and continue the loop
                    agentChat.AddChatMessage(ConvertToAgentChat(ProcessMessageWithContext(delegationMessage)));
                    delegationCount++;
                }
                else
                {
                    break; // No delegation message to process
                }
            }
            else
            {
                break; // No more delegations
            }
        }
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
            .Select(ConvertToAgentChat)
            .ToArray();

        agentChat.AddChatMessages(messagesToResume);
        return Task.CompletedTask;
    }

    public string Delegate(string agentName, string message, bool askUserFeedback = false)
    {
        agentName = agentName.TrimStart('@');
        // Check if the requested agent exists
        if (agentChat.Agents.All(a => a.Name != agentName))
        {
            return $"Agent '{agentName}' not found. Available agents: {string.Join(", ", agentChat.Agents.Select(a => a.Name))}";
        }

        // Capture the delegating agent
        var delegatingAgent = currentRunningAgent ?? "System";

        // Schedule the delegation to be processed when the chat is available
        var instruction = new DelegationInstruction(
            askUserFeedback ? DelegationType.ReplyTo : DelegationType.DelegateTo,
            agentName,
            message,
            delegatingAgent);

        if (askUserFeedback)
        {
            delegationState.SetPendingReply(instruction);
            return "Delegation scheduled. Please return to get user input to continue.";
        }
        else
        {
            delegationState.SetPendingDelegation(instruction);
            return $"Delegation to {agentName} scheduled. It will be processed after you return.";
        }
    }

    public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl)
    {
        var layoutAreaContent = new ChatLayoutAreaContent(layoutAreaControl);
        queuedLayoutAreaContent.Enqueue(layoutAreaContent);
    }

    private ChatMessageContent ConvertToAgentChat(ChatMessage message)
    {
        ChatMessageContentItemCollection collection = new();
        foreach (var messageContent in message.Contents)
        {
            var converted = ConvertItem(messageContent);
            if (converted is not null)
                collection.Add(converted);
        }
        return new(new(message.Role.Value), collection);
    }
    private KernelContent? ConvertItem(AIContent messageContent)
    {
        return messageContent switch
        {
            TextContent text => new Microsoft.SemanticKernel.TextContent(text.Text),
            DataContent data => new BinaryContent(data.Data, "application/octet-stream"),
            Microsoft.Extensions.AI.FunctionCallContent functionCall => new Microsoft.SemanticKernel.FunctionCallContent(
                functionCall.CallId,
                functionCall.Name,
                functionCall.Arguments != null ? JsonSerializer.Serialize(functionCall.Arguments) : null),
            Microsoft.Extensions.AI.FunctionResultContent functionResult => new Microsoft.SemanticKernel.FunctionResultContent(
                functionResult.CallId,
                functionResult.Result?.ToString()),
            _ => null
        };
    }

    private AIContent? ConvertToExtensionsAI(KernelContent content)
    {
        return content switch
        {
            ChatMessageContent chat => new TextContent(chat.Content ?? string.Empty),
            Microsoft.SemanticKernel.TextContent textContent => new TextContent(textContent.Text ?? string.Empty),
            ImageContent imageContent => new DataContent(imageContent.Data?.ToArray() ?? Convert.FromBase64String(imageContent.DataUri!.Split(',')[1]), imageContent.MimeType!),
            AudioContent audioContent => new DataContent(audioContent.Data?.ToArray() ?? [], audioContent.MimeType!),
            Microsoft.SemanticKernel.FunctionCallContent functionCall => new Microsoft.Extensions.AI.FunctionCallContent(functionCall.Id!, functionCall.FunctionName, functionCall.Arguments),
            Microsoft.SemanticKernel.FunctionResultContent functionResult => new Microsoft.Extensions.AI.FunctionResultContent(functionResult.CallId!, functionResult.Result),
            _ => null
        };
    }

    private ChatResponseUpdate? ConvertToExtensionsAI(StreamingKernelContent content)
    {
        return content switch
        {
            StreamingChatMessageContent chatMessage => ProcessStreamingText(chatMessage.Content ?? string.Empty, chatMessage.AuthorName),
            StreamingTextContent text => ProcessStreamingText(text.Text ?? string.Empty),
            StreamingFunctionCallUpdateContent functionContent => new ChatResponseUpdate(
                ChatRole.Assistant,
                [new Microsoft.Extensions.AI.FunctionCallContent(functionContent.CallId!, functionContent.Name!,
                    string.IsNullOrEmpty(functionContent.Arguments) ? null :
                    JsonSerializer.Deserialize<IDictionary<string, object?>>(functionContent.Arguments))]),
            _ => null
        };
    }

    private ChatResponseUpdate ProcessStreamingText(string text, string? authorName = null)
    {
        // For now, just pass through the text directly
        // The UI will handle code block detection and rendering
        return new ChatResponseUpdate(ChatRole.Assistant, text)
        {
            AuthorName = authorName
        };
    }

    /// <summary>
    /// Converts a ChatMessage to a ChatResponseUpdate for streaming.
    /// </summary>
    private ChatResponseUpdate ConvertToStreamingUpdate(ChatMessage message)
    {
        var textContent = message.Contents.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty;
        return new ChatResponseUpdate(message.Role, textContent)
        {
            AuthorName = message.AuthorName
        };
    }    /// <summary>
         /// Extracts text content from a chat message.
         /// </summary>
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

    /// <summary>
    /// Selects the appropriate agent based on context changes and agent capabilities.
    /// </summary>
    private string? SelectAgentForContext(AgentContext? newContext)
    {
        // If no context, use current agent or default
        if (newContext is null)
            return currentRunningAgent;


        if (currentRunningAgent is not null)
        {
            var currentAgent = agentDefinitions.GetValueOrDefault(currentRunningAgent);

            // Check if current agent implements IAgentWithContext and still matches
            if (currentAgent is IAgentWithContext currentContextAgent && !currentContextAgent.Matches(newContext))
                currentRunningAgent = null; // current agent not applicable.

            if (currentRunningAgent is not null)
                return currentRunningAgent;
        }

        // Find an agent that matches the new context
        var matchingAgent = agentDefinitions
            .Values.OfType<IAgentWithContext>()
            .FirstOrDefault(a => a.Matches(newContext));

        if (matchingAgent is not null)
            return matchingAgent.Name;


        // Default to null (which will use default routing)
        return currentRunningAgent;
    }


    /// <summary>
    /// Manages the state for delegation scenarios within this chat client.
    /// </summary>
    private class DelegationState
    {
        private DelegationInstruction? _pendingReply;
        private DelegationInstruction? _pendingDelegation;

        /// <summary>
        /// Gets whether there is a pending reply instruction waiting for user input.
        /// </summary>
        public bool HasPendingReply => _pendingReply != null;

        /// <summary>
        /// Gets whether there is a pending delegation waiting to be processed.
        /// </summary>
        public bool HasPendingDelegation => _pendingDelegation != null;

        /// <summary>
        /// Gets the pending reply instruction if one exists.
        /// </summary>
        public DelegationInstruction? PendingReply => _pendingReply;

        /// <summary>
        /// Gets the pending delegation instruction if one exists.
        /// </summary>
        public DelegationInstruction? PendingDelegation => _pendingDelegation;

        /// <summary>
        /// Sets a pending reply instruction that will wait for the next user message.
        /// </summary>
        public void SetPendingReply(DelegationInstruction instruction)
        {
            if (instruction.Type != DelegationType.ReplyTo)
            {
                throw new ArgumentException("Only ReplyTo instructions can be set as pending", nameof(instruction));
            }
            _pendingReply = instruction;
        }

        /// <summary>
        /// Sets a pending delegation instruction that will be processed with the next user message.
        /// </summary>
        public void SetPendingDelegation(DelegationInstruction instruction)
        {
            if (instruction.Type != DelegationType.DelegateTo)
            {
                throw new ArgumentException("Only DelegateTo instructions can be set as pending", nameof(instruction));
            }
            _pendingDelegation = instruction;
        }

        /// <summary>
        /// Processes the pending reply with user input and returns the delegation message.
        /// </summary>
        public ChatMessage? ProcessPendingReply(string userInput)
        {
            if (_pendingReply == null) return null;

            var delegationContent = CreateDelegationMessage(_pendingReply, userInput);
            _pendingReply = null; // Clear the pending reply

            return new ChatMessage(ChatRole.User, [new TextContent(delegationContent)]);
        }

        /// <summary>
        /// Processes the pending delegation and returns the delegation message.
        /// </summary>
        public ChatMessage? ProcessPendingDelegation()
        {
            if (_pendingDelegation == null) return null;

            var delegationContent = CreateDelegationMessage(_pendingDelegation);
            _pendingDelegation = null; // Clear the pending delegation

            return new ChatMessage(ChatRole.User, [new TextContent(delegationContent)]);
        }

        /// <summary>
        /// Creates a delegation message from the instruction.
        /// </summary>
        private static string CreateDelegationMessage(DelegationInstruction instruction, string? userInput = null)
        {
            var message = instruction.Type switch
            {
                DelegationType.DelegateTo => $"@{instruction.AgentName} {instruction.Message}",
                DelegationType.ReplyTo when userInput != null => $"@{instruction.AgentName} {instruction.Message}\n\nUser input: {userInput}",
                DelegationType.ReplyTo => $"@{instruction.AgentName} {instruction.Message}",
                _ => throw new ArgumentException($"Unknown delegation type: {instruction.Type}")
            };

            return message;
        }

        /// <summary>
        /// Clears all pending delegations.
        /// </summary>
        public void Clear()
        {
            _pendingReply = null;
            _pendingDelegation = null;
        }
    }

    /// <summary>
    /// Represents a delegation instruction.
    /// </summary>
    private record DelegationInstruction(DelegationType Type, string AgentName, string Message, string DelegatingAgent);

    /// <summary>
    /// The type of delegation.
    /// </summary>
    private enum DelegationType
    {
        /// <summary>
        /// Delegate to another agent immediately.
        /// </summary>
        DelegateTo,

        /// <summary>
        /// Reply to another agent after getting user feedback.
        /// </summary>
        ReplyTo
    }


}
