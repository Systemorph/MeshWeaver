using System;
using System.Collections.Generic;
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
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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

// TODO: needs custom shared fixture â€” uses ReentrancyTestSiloConfigurator with a custom
// ReentrancyTestChatClientFactory whose tool-calling behavior is essential to the test.
// Migration would require swapping the chat factory per-test (violates structural-only rule).
/// <summary>
/// Tests that prove/disprove grain reentrancy during AI execution.
/// Hypothesis: the grain scheduler deadlocks when a tool call (inside InvokeAsync)
/// needs to process a response that arrives as a grain call.
///
/// Test pattern:
/// 1. Submit a message that triggers a tool call (Get or Patch)
/// 2. The tool call makes a round-trip through the hub
/// 3. If reentrant: the response interleaves, tool completes, execution finishes
/// 4. If deadlocked: timeout
/// </summary>
public class OrleansReentrancyTest(ITestOutputHelper output) : TestBase(output)
{
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<ReentrancyTestSiloConfigurator>();
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

    private async Task<IMessageHub> GetClientAsync(string id)
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", id),
            config =>
            {
                config.TypeRegistry.AddAITypes();
                return config.AddLayoutClient();
            });
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "Roland", Name = "Roland", Email = "rbuergi@test.com"
        });
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        // Canonical CQRS-correct read via per-node MeshNodeReference reducer.
        var response = await client.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path))).FirstAsync().ToTask(ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(ClientMesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(ClientMesh.JsonSerializerOptions);
        return null;
    }

    /// <summary>
    /// The fake agent calls a tool that requires a round-trip through the hub.
    /// If reentrancy works: tool completes, response has tool call result + text.
    /// If deadlocked: test times out at 15s.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task ToolCall_DuringStreaming_DoesNotDeadlock()
    {
        var ct = new CancellationTokenSource(12.Seconds()).Token;
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var client = await GetClientAsync($"reent-{suffix}");

        // Create thread
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Reentrancy test", "Roland");
        var createResp = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("User/Roland"))).FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);
        var threadPath = createResp.Message.Node!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // Subscribe to messages
        var workspace = client.GetWorkspace();
        var twoMessages = workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.Cast<MeshNode>().FirstOrDefault(n => n.Path == threadPath);
                return (node?.Content as MeshThread)?.Messages ?? [];
            })
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // Submit message â€” the ToolCallingReentrancyClient will call a tool
        Output.WriteLine("Submitting message...");
        var submitResp = await client.Observe(new AppendUserMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageId = Guid.NewGuid().ToString("N")[..8],
                UserText = "Call a tool please",
                ContextPath = "User/Roland"
            }, o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);
        submitResp.Message.Success.Should().BeTrue(submitResp.Message.Error);
        Output.WriteLine("Submitted");

        // Wait for message cells
        var msgIds = await twoMessages;
        Output.WriteLine($"Messages: [{string.Join(", ", msgIds)}]");

        // Poll for response text + tool calls
        var responsePath = $"{threadPath}/{msgIds[1]}";
        ThreadMessage? response = null;
        for (var i = 0; i < 30; i++)
        {
            response = await GetHubContentAsync<ThreadMessage>(client, responsePath, ct);
            if (!string.IsNullOrEmpty(response?.Text) && response?.ToolCalls.Count > 0)
            {
                Output.WriteLine($"Poll {i}: text={response.Text.Length}ch, toolCalls={response.ToolCalls.Count}");
                break;
            }
            await Task.Delay(300, ct);
        }

        // Verify: execution completed with tool call results
        response.Should().NotBeNull("response should exist");
        response!.Text.Should().NotBeNullOrEmpty("agent should produce text after tool call");
        response.ToolCalls.Should().NotBeEmpty("tool calls should be tracked");
        response.ToolCalls.First().Result.Should().NotBeNull("tool call should have completed with a result");

        Output.WriteLine($"PASSED â€” text='{response.Text[..Math.Min(50, response.Text.Length)]}', toolCalls={response.ToolCalls.Count}");
    }
}

/// <summary>
/// Fake chat client that always calls a tool before producing text.
/// The tool call (Get) requires a round-trip through the hub.
/// If the grain is deadlocked, the tool call never completes.
/// </summary>
internal class ToolCallingReentrancyClient : IChatClient
{
    public ChatClientMetadata Metadata => new("ReentrancyTest");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var hasFunctionResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        if (hasFunctionResult)
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "Tool call completed successfully. Reentrancy works.")));

        // Call Get tool â€” requires round-trip through the hub
        if (options?.Tools?.Any(t => t.Name == "Get") == true)
        {
            var call = new FunctionCallContent("test-get", "Get",
                new Dictionary<string, object?> { ["path"] = "@User/Roland" });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "No Get tool available.")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        var msg = response.Messages.First();
        var functionCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
        if (functionCalls.Count > 0)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [..functionCalls]
            };
            yield break;
        }
        foreach (var word in (msg.Text ?? "Done.").Split(' '))
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

internal class ReentrancyTestChatClientFactory : IChatClientFactory
{
    public string Name => "ReentrancyTestFactory";
    public IReadOnlyList<string> Models => ["test-model"];
    public int Order => 0;

    public ChatClientAgent CreateAgent(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => new(chatClient: new ToolCallingReentrancyClient(),
            instructions: "Test assistant.",
            name: config.Id, description: config.Description ?? config.Id,
            tools: [], loggerFactory: null, services: null);

    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
}

public class ReentrancyTestSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .ConfigurePortalMesh()
            .AddGraph()
            .AddAI()
            .AddRowLevelSecurity()
            .AddMeshNodes(new MeshNode("Roland", "User") { Name = "Roland", NodeType = "User" })
            .AddMeshNodes(PublicEditorAccess())
            .ConfigureServices(services =>
                services.AddSingleton<IChatClientFactory>(new ReentrancyTestChatClientFactory()))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    private static MeshNode[] PublicEditorAccess()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "Public",
            DisplayName = "Public",
            Roles = [new RoleAssignment { Role = "Admin" }]
        };
        return [new("Public_Access", "User")
        {
            NodeType = "AccessAssignment",
            Name = "Public Access",
            Content = assignment,
            MainNode = "User",
        }];
    }
}
