using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Orleans.Test;

public static class OrleansTestMeshExtensions
{
    public static MeshBuilder ConfigurePortalMesh(this MeshBuilder builder)
    {
        var assemblyLocation = typeof(OrleansTestMeshExtensions).Assembly.Location;
        return builder
            .InstallAssemblies(assemblyLocation)
            .AddMeshNodes(MeshNode.FromPath($"{AddressExtensions.AppType}/HubFactory") with
            {
                Name = "HubFactory",
                AssemblyLocation = assemblyLocation,
                HubConfiguration = x => x
            })
            .AddMeshNodes(MeshNode.FromPath($"{AddressExtensions.AppType}/Kernel") with
            {
                Name = "Kernel",
                AssemblyLocation = assemblyLocation,
                HubConfiguration = x => x
            })
            .AddGraph()
            .AddAI()
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .AddKernel();
    }

    public static MessageHubConfiguration ConfigureOrleansTestApplication(this MessageHubConfiguration configuration)
        => configuration;
}

internal class FakeChatClient(string response) : IChatClient
{
    public ChatClientMetadata Metadata => new("FakeProvider");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var word in response.Split(' '))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            await Task.Delay(10, cancellationToken);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IChatClient) ? this : null;
    public void Dispose() { }
}

internal class FakeChatClientFactory : IChatClientFactory
{
    private const string ResponseText = "This is a test response from the fake agent.";
    public string Name => "FakeFactory";
    public IReadOnlyList<string> Models => ["fake-model"];
    public int Order => 0;

    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        return Task.FromResult(new ChatClientAgent(
            chatClient: new FakeChatClient(ResponseText),
            instructions: config.Instructions ?? "Test assistant.",
            name: config.Id, description: config.Description ?? config.Id,
            tools: [], loggerFactory: null, services: null));
    }
}
