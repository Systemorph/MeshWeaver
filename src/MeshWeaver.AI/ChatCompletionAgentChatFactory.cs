using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace MeshWeaver.AI;

/// <summary>
/// Base factory for creating agent chats using ChatCompletionAgent.
/// ChatCompletionAgent is used for stateless chat completion scenarios without persistent assistant storage.
/// </summary>
public abstract class ChatCompletionAgentChatFactory(
    IMessageHub hub,
    IEnumerable<IAgentDefinition> agentDefinitions)
    : AgentChatFactoryBase<ChatCompletionAgent>(hub, agentDefinitions)
{
    protected override Task<IEnumerable<ChatCompletionAgent>> GetExistingAgentsAsync()
    {
        // ChatCompletionAgent doesn't have persistent storage, so we return empty collection
        // All agents will be created fresh each time
        return Task.FromResult(Enumerable.Empty<ChatCompletionAgent>());
    }

    protected override string GetAgentName(ChatCompletionAgent agent)
    {
        return agent.Name!;
    }

    protected override async Task<Agent> CreateOrUpdateAgentAsync(IAgentDefinition agentDefinition, ChatCompletionAgent? existingAgent)
    {
        // Since ChatCompletionAgent doesn't persist, we always create new agents
        var name = agentDefinition.AgentName;
        var description = agentDefinition.Description;
        var instructions = GetAgentInstructions(agentDefinition);

        // Create a new kernel for this agent using the derived class implementation
        var agentKernel = await CreateKernelAsync(agentDefinition);

        if (agentDefinition is IAgentWithPlugins agentWithPlugins)
            foreach (var plugin in agentWithPlugins.GetPlugins())
                agentKernel.Plugins.Add(plugin);

        // Create ChatCompletionAgent
        var agent = new ChatCompletionAgent()
        {
            Name = name,
            Description = description,
            Instructions = instructions,
            Kernel = agentKernel,
            Arguments = new KernelArguments(new PromptExecutionSettings() { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() }),
        };


        return agent;
    }    /// <summary>
         /// Creates a Kernel instance for the specified agent definition.
         /// Implementations should configure the kernel with their specific chat completion provider.
         /// </summary>
         /// <param name="agentDefinition">The agent definition for which to create the kernel</param>
         /// <returns>A configured Kernel instance</returns>
    protected abstract Task<Microsoft.SemanticKernel.Kernel> CreateKernelAsync(IAgentDefinition agentDefinition);

    protected override async Task UploadFileAsync(Agent assistant, AgentFileInfo file)
    {
        // ChatCompletionAgent doesn't support file uploads in the same way as persistent assistants
        // Files would need to be handled differently, potentially through the conversation context
        // This is a no-op for now, but could be extended to handle file content in messages
        await Task.CompletedTask;
        Logger.LogInformation("File upload not directly supported for ChatCompletionAgent: {FileName}", file.FileName);
    }

    public override async Task DeleteThreadAsync(string threadId)
    {
        // ChatCompletionAgent doesn't have persistent threads to delete
        // This is a no-op
        await Task.CompletedTask;
        Logger.LogInformation("Thread deletion not applicable for ChatCompletionAgent: {ThreadId}", threadId);
    }
}
