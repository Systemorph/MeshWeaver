using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace MeshWeaver.AI;

/// <summary>
/// Base factory for creating agent chats using ChatClientAgent.
/// ChatClientAgent is used for stateless chat completion scenarios without persistent assistant storage.
/// </summary>
public abstract class ChatCompletionAgentChatFactory(
    IMessageHub hub,
    IEnumerable<IAgentDefinition> agentDefinitions)
    : AgentChatFactoryBase<AIAgent>(hub, agentDefinitions)
{
    protected override Task<IEnumerable<AIAgent>> GetExistingAgentsAsync()
    {
        // ChatClientAgent doesn't have persistent storage, so we return empty collection
        // All agents will be created fresh each time
        return Task.FromResult(Enumerable.Empty<AIAgent>());
    }

    protected override string GetAgentName(AIAgent agent)
    {
        return agent.Name!;
    }


    protected override async Task UploadFileAsync(AIAgent assistant, AgentFileInfo file)
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

    protected override Task<AIAgent> CreateOrUpdateAgentAsync(IAgentDefinition agentDefinition, AIAgent? existingAgent)
    {
        // Since ChatClientAgent doesn't persist, we always create new agents
        var name = agentDefinition.Name;
        var description = agentDefinition.Description;
        var instructions = GetAgentInstructions(agentDefinition);

        // Create a chat client for this agent using the derived class implementation
        var chatClient = CreateChatClient(agentDefinition);

        // Get tools for this agent
        var tools = GetToolsForAgent(agentDefinition);

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

        return Task.FromResult<AIAgent>(agent);
    }

}
