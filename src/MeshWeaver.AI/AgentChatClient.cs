using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace MeshWeaver.AI;

public class AgentChatClient(AgentChat agentChat, IServiceProvider serviceProvider) : IAgentChat
{
    private const int MaxDelegationDepth = 10; // Prevent infinite delegation loops

    private readonly DelegationState delegationState = new();
    private AgentContext? context; 
    public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Check if we have a pending reply that needs user input
        var firstMessage = messages.FirstOrDefault();
        if (delegationState.HasPendingReply && firstMessage != null)
        {
            var userText = ExtractTextFromMessage(firstMessage);

            // Show the reply message to the user (without user input appendix)
            if (delegationState.PendingReply != null)
            {
                var displayMessage = $"@{delegationState.PendingReply.AgentName} {delegationState.PendingReply.Content}";
                yield return new ChatMessage(ChatRole.Assistant, [new TextContent(displayMessage)])
                {
                    AuthorName = new("Assistant")
                };
            }

            var replyMessage = delegationState.ProcessPendingReply(userText);
            if (replyMessage != null)
            {
                AddUserMessages([replyMessage]);
            }
        }
        else
        {
            AddUserMessages(messages);
        }

        int delegationDepth = 0;
        string? originalAuthorName = null;

        while (delegationDepth < MaxDelegationDepth)
        {
            var responseMessages = new List<ChatMessage>();
            var fullResponseText = new StringBuilder();

            await foreach (var content in agentChat.InvokeAsync(cancellationToken))
            {
                // Capture the original author name from the first content
                originalAuthorName ??= content.AuthorName ?? "Assistant";

                var aiContent = ConvertToExtensionsAI(content);
                if (aiContent == null) continue;

                var message = new ChatMessage(ChatRole.Assistant, [aiContent])
                {
                    AuthorName = new(content.AuthorName ?? "Assistant")
                };

                responseMessages.Add(message);

                // Accumulate text content for delegation detection
                if (aiContent is TextContent textContent)
                {
                    fullResponseText.Append(textContent.Text);
                }
            }

            if (responseMessages.Count == 0)
            {
                // No content was generated, end the workflow
                yield break;
            }

            // Parse the response for code blocks
            var responseText = fullResponseText.ToString();
            var (delegationInstruction, cleanedMessages) = ProcessResponseForDelegation(responseMessages, responseText);

            if (delegationInstruction == null)
            {
                // No delegation found, return cleaned messages
                foreach (var message in cleanedMessages)
                {
                    yield return message;
                }
                yield break;
            }

            // Return cleaned messages before delegation
            foreach (var message in cleanedMessages)
            {
                yield return message;
            }

            if (delegationInstruction.Type == DelegationType.ReplyTo)
            {
                // For reply_to, set pending state and yield break
                delegationState.SetPendingReply(delegationInstruction);
                yield break;
            }            // Handle delegate_to
            delegationDepth++;

            var delegationMessage = CreateDelegationMessageWithContext(delegationInstruction);

            AddUserMessages([delegationMessage]);            // Add delegation message showing actual content for the user
            // Add newline to ensure proper separation from previous content  
            var delegationDisplay = $"\n@{delegationInstruction.AgentName} {delegationInstruction.Content}";
            yield return new ChatMessage(ChatRole.Assistant,
                [new TextContent(delegationDisplay)])
            { AuthorName = new(originalAuthorName) };
        }
        yield return new ChatMessage(ChatRole.Assistant,
            [new TextContent($"[System: Maximum delegation depth ({MaxDelegationDepth}) reached. Ending workflow to prevent infinite loops.]")])
        { AuthorName = new(originalAuthorName ?? "System") };
    }
    private void AddUserMessages(IEnumerable<ChatMessage> messages)
    {
        var processedMessages = messages.Select(message => ProcessMessageWithContext(message)).ToArray();
        agentChat.AddChatMessages(processedMessages.Select(ConvertToAgentChat).ToArray());
    }

    private ChatMessage ProcessMessageWithContext(ChatMessage message)
    {
        if (context == null || message.Role != ChatRole.User)
            return message;

        // Extract original text content
        var originalText = ExtractTextFromMessage(message);

        // Create new message with context template
        var contextJson = JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = false });
        var messageWithContext = $"{originalText}\n<Context>\n{contextJson}\n</Context>";

        return new ChatMessage(ChatRole.User, [new TextContent(messageWithContext)])
        {
            AuthorName = message.AuthorName
        };
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Check if we have a pending reply that needs user input
        var firstMessage = messages.FirstOrDefault();
        if (delegationState.HasPendingReply && firstMessage != null)
        {
            var userText = ExtractTextFromMessage(firstMessage);

            // Show the reply message to the user (without user input appendix)
            if (delegationState.PendingReply != null)
            {
                var displayMessage = $"@{delegationState.PendingReply.AgentName} {delegationState.PendingReply.Content}";
                yield return new ChatResponseUpdate(ChatRole.Assistant, displayMessage) { AuthorName = "Assistant" };
            }

            var replyMessage = delegationState.ProcessPendingReply(userText);
            if (replyMessage != null)
            {
                AddUserMessages([replyMessage]);
            }
        }
        else
        {
            AddUserMessages(messages);
        }

        int delegationDepth = 0;
        string? originalAuthorName = null;

        while (delegationDepth < MaxDelegationDepth)
        {
            var codeBlockTracker = new CodeBlockTracker();
            var responseBuilder = new StringBuilder();
            var delegationDetected = false;
            DelegationInstruction? delegationInstruction = null; await foreach (var update in agentChat.InvokeStreamingAsync(cancellationToken))
            {
                var converted = ConvertToExtensionsAI(update);
                if (converted == null) continue;

                responseBuilder.Append(converted.Text);

                // Update originalAuthorName whenever it changes during streaming
                if (!string.IsNullOrEmpty(converted.AuthorName))
                {
                    originalAuthorName = converted.AuthorName;
                }

                foreach (var c in converted.Text)
                {
                    if (delegationDetected)
                    {
                        // After delegation detected, continue buffering but don't output
                        continue;
                    }

                    var streamContent = codeBlockTracker.ProcessCharacter(c);
                    if (streamContent != null)
                    {
                        // Normal content that can be streamed immediately
                        yield return new ChatResponseUpdate(converted.Role, streamContent) { AuthorName = converted.AuthorName };
                    }

                    // Check if we have a completed code block
                    if (codeBlockTracker.HasCompleteBlock && codeBlockTracker.CompletedBlock != null)
                    {
                        var result = CodeBlockParser.ProcessCodeBlock(codeBlockTracker.CompletedBlock);

                        if (result.Type == CodeBlockType.Delegation && result.DelegationInstruction != null)
                        {
                            delegationDetected = true;
                            delegationInstruction = result.DelegationInstruction;

                            if (result.DelegationInstruction.Type == DelegationType.DelegateTo)
                            {
                                // For delegate_to, we continue to the next agent immediately
                                break;
                            }
                            else if (result.DelegationInstruction.Type == DelegationType.ReplyTo)
                            {
                                // For reply_to, we set pending state and yield break
                                delegationState.SetPendingReply(result.DelegationInstruction);
                                yield break;
                            }
                        }
                        else
                        {
                            // Regular code block, output it normally
                            yield return new ChatResponseUpdate(converted.Role, FormatCodeBlock(codeBlockTracker.CompletedBlock))
                            { AuthorName = converted.AuthorName };
                        }

                        codeBlockTracker.Reset();
                    }
                }
            }

            // Flush any remaining buffered content
            var remainingContent = codeBlockTracker.Flush();
            if (!delegationDetected && !string.IsNullOrEmpty(remainingContent))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, remainingContent) { AuthorName = originalAuthorName };
            }

            if (!delegationDetected)
            {
                yield break;
            }

            // Handle delegation
            if (delegationInstruction == null)
            {
                yield break;
            }
            delegationDepth++; var delegationMessage = CreateDelegationMessageWithContext(delegationInstruction);

            AddUserMessages([delegationMessage]); if (agentChat.Agents.Any(a => a.Name == delegationInstruction.AgentName))
            {
                // Show the actual delegation message content, not just a generic message
                // Add newline to ensure proper separation from previous content
                var delegationDisplay = $"\n@{delegationInstruction.AgentName} {delegationInstruction.Content}";
                yield return new ChatResponseUpdate(ChatRole.Assistant, delegationDisplay)
                { AuthorName = originalAuthorName };
            }
            else
            {
                yield break;
            }
        }
        yield return new ChatResponseUpdate(ChatRole.Assistant,
          $"Maximum delegation depth ({MaxDelegationDepth}) reached. Ending workflow to prevent infinite loops.")
        { AuthorName = originalAuthorName ?? "System" };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null
            ? serviceProvider.GetService(serviceType)
            : serviceProvider.GetKeyedServices(serviceType, serviceKey);

    public void SetContext(AgentContext applicationContext)
    {
        context = applicationContext;
    }

    public Task ResumeAsync(ChatConversation conversation)
    {
        agentChat.AddChatMessages(conversation.Messages.Select(ConvertToAgentChat).ToArray());
        return Task.CompletedTask;
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
            StreamingChatMessageContent chatMessage => new ChatResponseUpdate(ChatRole.Assistant, chatMessage.Content ?? string.Empty) { AuthorName = chatMessage.AuthorName },
            StreamingTextContent text => new ChatResponseUpdate(ChatRole.Assistant, text.Text ?? string.Empty),
            StreamingFunctionCallUpdateContent functionContent => new ChatResponseUpdate(
                ChatRole.Assistant,
                [new Microsoft.Extensions.AI.FunctionCallContent(functionContent.CallId!, functionContent.Name!,
                    string.IsNullOrEmpty(functionContent.Arguments) ? null :
                    JsonSerializer.Deserialize<IDictionary<string, object?>>(functionContent.Arguments))]),
            _ => null
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
    /// Formats a code block for output.
    /// </summary>
    private string FormatCodeBlock(CodeBlock codeBlock)
    {
        var fence = "```";
        if (!string.IsNullOrEmpty(codeBlock.Language))
        {
            return $"{fence}{codeBlock.Language}\n{codeBlock.Content}\n{fence}";
        }
        return $"{fence}\n{codeBlock.Content}\n{fence}";
    }

    /// <summary>
    /// Processes a response for delegation instructions using code block parsing.
    /// </summary>
    private (DelegationInstruction? delegationInstruction, List<ChatMessage> cleanedMessages) ProcessResponseForDelegation(
        List<ChatMessage> responseMessages, string responseText)
    {
        var codeBlockTracker = new CodeBlockTracker();
        List<ChatMessage> cleanedMessages;
        DelegationInstruction? foundDelegation = null;

        // Process the response text character by character to find code blocks
        foreach (var c in responseText)
        {
            codeBlockTracker.ProcessCharacter(c); if (codeBlockTracker.HasCompleteBlock && codeBlockTracker.CompletedBlock != null)
            {
                var result = CodeBlockParser.ProcessCodeBlock(codeBlockTracker.CompletedBlock);
                if (result.Type == CodeBlockType.Delegation && result.DelegationInstruction != null && foundDelegation == null)
                {
                    foundDelegation = result.DelegationInstruction;
                }
                codeBlockTracker.Reset();
            }
        }

        // If delegation found, clean the messages by removing delegation code blocks
        if (foundDelegation != null)
        {
            cleanedMessages = RemoveDelegationCodeBlocks(responseMessages, foundDelegation);
        }
        else
        {
            cleanedMessages = responseMessages;
        }

        return (foundDelegation, cleanedMessages);
    }

    /// <summary>
    /// Removes delegation code blocks from response messages.
    /// </summary>
    private List<ChatMessage> RemoveDelegationCodeBlocks(List<ChatMessage> messages, DelegationInstruction delegationInstruction)
    {
        var cleanedMessages = new List<ChatMessage>();

        foreach (var message in messages)
        {
            var cleanedContents = new List<AIContent>();

            foreach (var content in message.Contents)
            {
                if (content is TextContent textContent)
                {
                    var cleanedText = RemoveCodeBlocksFromText(textContent.Text, delegationInstruction);
                    if (!string.IsNullOrWhiteSpace(cleanedText))
                    {
                        cleanedContents.Add(new TextContent(cleanedText));
                    }
                }
                else
                {
                    cleanedContents.Add(content);
                }
            }

            if (cleanedContents.Count > 0)
            {
                cleanedMessages.Add(new ChatMessage(message.Role, cleanedContents) { AuthorName = message.AuthorName });
            }
        }

        return cleanedMessages;
    }

    /// <summary>
    /// Removes code blocks from text that match the delegation instruction.
    /// </summary>
    private string RemoveCodeBlocksFromText(string text, DelegationInstruction delegationInstruction)
    {
        var result = new StringBuilder();
        var codeBlockTracker = new CodeBlockTracker();

        foreach (char c in text)
        {
            var streamContent = codeBlockTracker.ProcessCharacter(c);

            if (streamContent != null)
            {
                // Normal content - add to result
                result.Append(streamContent);
            }
            if (codeBlockTracker.HasCompleteBlock && codeBlockTracker.CompletedBlock != null)
            {
                var result_block = CodeBlockParser.ProcessCodeBlock(codeBlockTracker.CompletedBlock);
                if (result_block.Type != CodeBlockType.Delegation ||
                    result_block.DelegationInstruction == null ||
                    result_block.DelegationInstruction.AgentName != delegationInstruction.AgentName ||
                    result_block.DelegationInstruction.Type != delegationInstruction.Type)
                {
                    // Not a delegation block or different delegation - include it
                    result.Append(FormatCodeBlock(codeBlockTracker.CompletedBlock));
                }
                // If it's the delegation block, we skip it (don't add to result)

                codeBlockTracker.Reset();
            }
        }

        // Flush any remaining content
        var remaining = codeBlockTracker.Flush();
        if (!string.IsNullOrEmpty(remaining))
        {
            result.Append(remaining);
        }

        return result.ToString();
    }

    /// <summary>
    /// Creates a delegation message that preserves the current context.
    /// </summary>
    private ChatMessage CreateDelegationMessageWithContext(DelegationInstruction delegationInstruction)
    {
        var delegationContent = CodeBlockParser.CreateDelegationMessage(delegationInstruction);

        // If we have context, append it to the delegation message
        if (context != null)
        {
            var contextJson = JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = false });
            delegationContent = $"{delegationContent}\n<Context>\n{contextJson}\n</Context>";
        }

        return new ChatMessage(ChatRole.User, [new TextContent(delegationContent)]);
    }
}
