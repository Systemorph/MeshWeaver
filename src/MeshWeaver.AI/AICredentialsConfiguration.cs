using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

public class AICredentialsConfiguration
{
    public string Url { get; set; }
    public string ApiKey { get; set; }
    public IReadOnlyCollection<string> Models { get; set; }
}

public record AIConfiguration
{
    internal string SystemPrompt { get; init; } = "You are a helpful assistant.";

    public AIConfiguration WithSystemPrompt(string systemPrompt)
    {
        return this with{SystemPrompt = systemPrompt};
    }

    internal ImmutableList<Action<ChatOptions, ChatContext>> ChatOptionEnrichments { get; init; } = [];
    public void EnrichOptions(ChatOptions chatOptions, ChatContext context)
    {
        foreach (var optionEnrichment in ChatOptionEnrichments)
            optionEnrichment(chatOptions, context);
    }

    public AIConfiguration WithChatOptionEnrichment(Action<ChatOptions, ChatContext> optionEnricher)
    {
        return this with { ChatOptionEnrichments = ChatOptionEnrichments.Add(optionEnricher) };
    }
}

public record ChatContext(IMessageHub Hub, string Uri, string SelectedModel);
