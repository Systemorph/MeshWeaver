using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace MeshWeaver.Assistant;

#pragma warning disable AOAI001

public class AssistantService(ChatClient chatClient, ChatCompletionOptions chatCompletionOptions)
{
    public async IAsyncEnumerable<AssistantChatReplyItem> GetReplyAsync(
        AssistantChatRequest request, 
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chatUpdates = chatClient.CompleteChatStreamingAsync(
            request.Messages.Select(m => 
                (ChatMessage)(m.IsAssistant ? new AssistantChatMessage(m.Text) : new UserChatMessage(m.Text))),
            chatCompletionOptions,
            cancellationToken
            );

        await foreach (var chatUpdate in chatUpdates)
        {
            var context = chatUpdate.GetAzureMessageContext();

            yield return new AssistantChatReplyItem()
            {
                Text = string.Join("", chatUpdate.ContentUpdate.Select(contentPart => contentPart.Text))
            };
        }
    }
 
}
