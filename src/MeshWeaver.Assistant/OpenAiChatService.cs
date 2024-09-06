using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using OpenAI.Chat;

namespace MeshWeaver.Assistant;

#pragma warning disable AOAI001

public class OpenAiChatService(ChatClient chatClient)
{
    public async IAsyncEnumerable<ChatReplyChunk> GetReplyAsync(
        IEnumerable<ChatHistoryItem> chatHistory, 
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messages = chatHistory.Select(m =>
                (ChatMessage)(m.IsAssistant ? new OpenAI.Chat.AssistantChatMessage(m.Text) : new UserChatMessage(m.Text)));

        var chatUpdates = 
            chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken);

        await foreach (var chatUpdate in chatUpdates)
        {
            var context = chatUpdate.GetAzureMessageContext();

            yield return new ChatReplyChunk(
                string.Join("", chatUpdate.ContentUpdate.Select(contentPart => contentPart.Text)),
                context?.Citations
            );
        }
    }
}

public record ChatHistoryItem(string Text, bool IsAssistant);

public record ChatReplyChunk(string Text, IReadOnlyCollection<AzureChatCitation> Citations);
