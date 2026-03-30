using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans integration tests for thread streaming and tool calls.
/// Verifies that in a distributed Orleans cluster:
/// 1. Response text streams to the message node
/// </summary>
public class OrleansThreadStreamingTest(ITestOutputHelper output) : TestBase(output)
{
    private const string ContextPath = "User/TestUser";
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<StreamingSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<IMessageHub> GetClientAsync()
    {
        MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
        {
            config.TypeRegistry.AddAITypes();
            return config.AddLayoutClient();
        }

        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", "streaming"), ConfigureClient);
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        var nodeId = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        var response = await client.AwaitResponse(
            new GetDataRequest(new EntityReference(nameof(MeshNode), nodeId)),
            o => o.WithTarget(new Address(path)), ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(ClientMesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(ClientMesh.JsonSerializerOptions);
        return null;
    }

    /// <summary>
    /// Verifies that response text streams to the message node during execution.
    /// After execution completes, the response message should have non-empty text.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ResponseText_StreamsToMessageNode()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // Create thread
        var response = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, "Streaming text test")),
            o => o.WithTarget(new Address(ContextPath)), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        var threadPath = response.Message.Node!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // Subscribe to messages
        var twoMessages = client.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                return (node?.Content as MeshThread)?.Messages ?? [];
            })
            .Where(ids => ids.Count >= 2)
            .FirstAsync().ToTask(ct);

        // Submit
        var submit = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Tell me something",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submit.Message.Success.Should().BeTrue(submit.Message.Error);

        var msgIds = await twoMessages;
        var responseMsgId = msgIds[1];
        Output.WriteLine($"Response message: {responseMsgId}");

        // Poll for response text
        ThreadMessage? responseMsg = null;
        for (var i = 0; i < 50; i++)
        {
            responseMsg = await GetHubContentAsync<ThreadMessage>(
                client, $"{threadPath}/{responseMsgId}", ct);
            if (!string.IsNullOrEmpty(responseMsg?.Text)) break;
            await Task.Delay(200, ct);
        }

        responseMsg.Should().NotBeNull();
        responseMsg!.Text.Should().NotBeNullOrEmpty("response should have streamed text");
        Output.WriteLine($"Response text: '{responseMsg.Text}' ({responseMsg.Text.Length} chars)");
    }

}

/// <summary>
/// Fake chat client that issues a tool call before producing text.
/// This simulates the real flow where agents call tools during execution.
/// </summary>
internal class ToolCallingFakeChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("ToolCallingFake");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done with tools.")));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // First: emit a function call
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("test-call-1", "test_tool", new Dictionary<string, object?> { ["param"] = "value" })]
        };

        // Simulate tool execution delay
        await Task.Delay(500, cancellationToken);

        // Emit function result
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionResultContent("test-call-1", "Tool result: success")]
        };

        // Then: stream text response
        foreach (var word in "This is the response after tool execution.".Split(' '))
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

internal class ToolCallingFakeChatClientFactory : IChatClientFactory
{
    public string Name => "ToolCallingFakeFactory";
    public IReadOnlyList<string> Models => ["tool-calling-model"];
    public int Order => 0;

    public ChatClientAgent CreateAgent(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => new(chatClient: new ToolCallingFakeChatClient(),
            instructions: config.Instructions ?? "Test assistant with tools.",
            name: config.Id, description: config.Description ?? config.Id,
            tools: [AIFunctionFactory.Create((string param) => $"Tool executed with {param}", "test_tool", "A test tool")],
            loggerFactory: null, services: null);

    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
}

public class StreamingSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    private static string SamplesGraphData =>
        Path.Combine(AppContext.BaseDirectory, "SamplesGraph", "Data");

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddFileSystemPersistence(SamplesGraphData)
            .ConfigurePortalMesh()
            .AddGraph()
            .AddAI()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new ToolCallingFakeChatClientFactory());
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}
