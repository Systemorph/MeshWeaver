using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Base factory for creating agent chats using ChatClientAgent.
/// ChatClientAgent is used for stateless chat completion scenarios without persistent assistant storage.
/// </summary>
public abstract class ChatCompletionAgentChatFactory(
    IMessageHub hub,
    IEnumerable<IAgentDefinition> agentDefinitions)
    : AgentChatFactoryBase(hub, agentDefinitions)
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


    protected override async Task UploadFileAsync(ChatClientAgent assistant, AgentFileInfo file)
    {
        // ChatClientAgent doesn't support file uploads in the same way as persistent assistants
        // Files would need to be handled differently, potentially through the conversation context
        // This is a no-op for now, but could be extended to handle file content in messages
        await Task.CompletedTask;
        Logger.LogInformation("File upload not directly supported for ChatClientAgent: {FileName}", file.FileName);
    }

    public override async Task DeleteThreadAsync(string threadId)
    {
        // ChatCompletionAgent doesn't have persistent threads to delete
        // This is a no-op
        await Task.CompletedTask;
        Logger.LogInformation("Thread deletion not applicable for ChatCompletionAgent: {ThreadId}", threadId);
    }

    protected override async Task<ChatClientAgent> CreateOrUpdateAgentAsync(
        IAgentDefinition agentDefinition,
        ChatClientAgent? existingAgent,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents)
    {
        // Since ChatClientAgent doesn't persist, we always create new agents
        var name = agentDefinition.Name;
        var description = agentDefinition.Description;
        var instructions = GetAgentInstructions(agentDefinition);

        // Create a chat client for this agent using the derived class implementation
        var chatClient = CreateChatClient(agentDefinition);

        // Get tools for this agent, passing the chat instance so plugins can access context
        // Method will filter delegation tools based on [DefaultAgent] attribute
        var tools = await GetToolsForAgentAsync(agentDefinition, chat, allAgents);

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

        return agent;
    }

}
