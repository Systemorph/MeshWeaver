using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Base factory for creating agent chats using ChatClientAgent.
/// ChatClientAgent is used for stateless chat completion scenarios without persistent assistant storage.
/// </summary>
public abstract class ChatCompletionAgentChatFactory(IMessageHub hub)
    : AgentChatFactoryBase(hub)
{
    public override async Task DeleteThreadAsync(string threadId)
    {
        // ChatCompletionAgent doesn't have persistent threads to delete
        // This is a no-op
        await Task.CompletedTask;
        Logger.LogInformation("Thread deletion not applicable for ChatCompletionAgent: {ThreadId}", threadId);
    }

    protected override Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration agentConfig,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents)
    {
        var name = agentConfig.Id;
        var description = agentConfig.Description ?? string.Empty;
        var instructions = GetAgentInstructions(agentConfig, hierarchyAgents);

        // Create a chat client for this agent using the derived class implementation
        var chatClient = CreateChatClient(agentConfig);

        // Get tools for this agent, passing the chat instance so plugins can access context
        // Get standard tools + delegation tools
        var tools = GetToolsForAgent(agentConfig, chat, existingAgents, hierarchyAgents).ToArray();

        // Add MeshPlugin tools for agents that need mesh operations
        var meshPlugin = new MeshPlugin(Hub, chat);
        tools = tools.Concat(meshPlugin.CreateTools()).ToArray();

        // Create ChatClientAgent with all parameters
        var agent = new ChatClientAgent(
            chatClient: chatClient,
            instructions: instructions,
            name: name,
            description: description,
            tools: tools,
            loggerFactory: null,
            services: null
        );

        return Task.FromResult(agent);
    }
}
