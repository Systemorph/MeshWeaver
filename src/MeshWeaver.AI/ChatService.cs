using Microsoft.Extensions.AI;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Basic implementation of IChatService
/// </summary>
public class ChatService : IChatService
{
    private readonly AIConfiguration _config;

    public ChatService(AIConfiguration config)
    {
        _config = config;
    }

    public string SystemPrompt { get; set; } = "You are a helpful AI assistant.";

    public IChatClient Get()
    {
        // This is a placeholder implementation
        // In a real implementation, this would return a configured chat client
        throw new NotImplementedException("ChatService.Get() needs to be implemented with a real AI client.");
    }

    public ChatOptions GetOptions(IMessageHub hub, string path)
    {
        var options = new ChatOptions();

        // Apply enrichments from configuration
        foreach (var enrichment in _config.GetChatOptionEnrichments())
        {
            enrichment(options, hub.ServiceProvider);
        }

        return options;
    }

    public ProgressMessage GetProgressMessage(object functionCall)
    {
        return new ProgressMessage
        {
            Icon = "⏳",
            Message = "Processing...",
            Progress = 0
        };
    }
}
