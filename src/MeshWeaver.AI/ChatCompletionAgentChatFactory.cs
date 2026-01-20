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
    protected override Task<IEnumerable<ChatClientAgent>> GetExistingAgentsAsync()
    {
        // ChatClientAgent doesn't have persistent storage, so we return empty collection
        // All agents will be created fresh each time
        return Task.FromResult(Enumerable.Empty<ChatClientAgent>());
    }

    protected override string GetAgentName(ChatClientAgent agent)
    {
        return agent.Name!;
    }

    public override async Task DeleteThreadAsync(string threadId)
    {
        // ChatCompletionAgent doesn't have persistent threads to delete
        // This is a no-op
        await Task.CompletedTask;
        Logger.LogInformation("Thread deletion not applicable for ChatCompletionAgent: {ThreadId}", threadId);
    }

    protected override Task<ChatClientAgent> CreateOrUpdateAgentAsync(
        AgentConfiguration agentConfig,
        ChatClientAgent? existingAgent,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents)
    {
        // Since ChatClientAgent doesn't persist, we always create new agents
        var name = agentConfig.Id;
        var description = agentConfig.Description ?? string.Empty;
        var instructions = GetAgentInstructions(agentConfig);

        // Create a chat client for this agent using the derived class implementation
        var chatClient = CreateChatClient(agentConfig);

        // Get tools for this agent, passing the chat instance so plugins can access context
        // Get standard tools + delegation tools
        var tools = GetToolsForAgent(agentConfig, chat, allAgents).ToArray();

        // Add MeshPlugin tools for agents that need mesh operations
        // This replaces the IAgentWithTools pattern
        var meshPlugin = new MeshPlugin(Hub, chat);
        tools = tools.Concat(meshPlugin.CreateTools()).ToArray();

        // Create ChatClientAgent with all parameters
        var agent = new ChatClientAgent(
            chatClient: chatClient,
            instructions: instructions,
            name: name,
            description: description,
            tools: tools,
            loggerFactory: null,  // Optional: could be injected if needed
            services: null        // Optional: could be injected if needed
        );

        return Task.FromResult(agent);
    }
}
