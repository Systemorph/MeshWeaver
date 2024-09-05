using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace MeshWeaver.Assistant;

#pragma warning disable AOAI001

public class ChatService(ChatClient chatClient)
{
    public async IAsyncEnumerable<ChatReply> GetReplyAsync(
        ChatRequest request, 
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messages =
            request.Messages.Select(m =>
                (ChatMessage)(m.IsAssistant ? new OpenAI.Chat.AssistantChatMessage(m.Text) : new UserChatMessage(m.Text)));

        var chatUpdates = 
            chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken);

        await foreach (var chatUpdate in chatUpdates)
        {
            var context = chatUpdate.GetAzureMessageContext();

            yield return new ChatReply()
            {
                Text = string.Join("", chatUpdate.ContentUpdate.Select(contentPart => contentPart.Text)),
                Citations = context?.Citations
            };
        }
    }
}
