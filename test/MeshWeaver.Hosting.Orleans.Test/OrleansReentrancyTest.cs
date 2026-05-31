using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
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

/// <summary>
/// Tests that prove/disprove grain reentrancy during AI execution.
/// Hypothesis: the grain scheduler deadlocks when a tool call (inside InvokeAsync)
/// needs to process a response that arrives as a grain call.
///
/// Test pattern:
/// 1. Submit a message that triggers a tool call (Get)
/// 2. The tool call makes a round-trip through the hub
/// 3. If reentrant: the response interleaves, tool completes, execution finishes
/// 4. If deadlocked: timeout
///
/// Per-class TestCluster (not SharedOrleansFixture): the fake factory must
/// extend <see cref="ChatClientAgentFactory"/> so MeshPlugin tools (Get) are
/// wired in by the production pipeline, and that subclass needs the silo's
/// <see cref="IMessageHub"/> at construction time. Mirrors the pattern used by
/// <see cref="OrleansDelegationTest"/>.
/// </summary>
public class OrleansReentrancyTest(ITestOutputHelper output) : TestBase(output)
{
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
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

    private IMessageHub GetClient(string id)
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
            ObjectId = "TestUser", Name = "TestUser", Email = "testuser@meshweaver.io"
        });
        Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStream(client.Address, client.DeliverMessage);
        return client;
    }

    /// <summary>
    /// The fake agent calls a tool that requires a round-trip through the hub.
    /// If reentrancy works: tool completes, response has tool call result + text.
    /// If deadlocked: test times out.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ToolCall_DuringStreaming_DoesNotDeadlock()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var client = GetClient($"reent-{suffix}");

        // Create thread
        var threadNode = ThreadNodeType.BuildThreadNode("User/TestUser", "Reentrancy test", "TestUser");
        var createResp = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("User/TestUser"))).FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var threadPath = createResp.Message.Node!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // Cache the per-node hub's MeshNodeReference sync stream — the authoritative
        // live stream from the executing thread hub. The older GetRemoteStream<MeshNode>
        // (CollectionReference) overload returns the MESH HUB's MeshNode cache, which is
        // fed by fan-out and lags the thread hub's own state — IsExecuting/Messages
        // emissions seen through that cache can interleave or miss the post-commit tick
        // that proves the response cell landed.
        //
        // Holding the ISynchronizationStream<MeshNode> reference (rather than wrapping
        // it in .Select(...).Replay(1)) preserves the stream's own Current cache so
        // each downstream Subscribe replays the latest server-side value directly —
        // no risk of the Replay buffer holding a stale projection.
        var workspace = client.GetWorkspace();
        var threadSyncStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(new Address(threadPath), new MeshNodeReference());

        var twoMessages = threadSyncStream
            .Select(change => (change.Value?.Content as MeshThread)?.Messages ?? [])
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // Submit message — the ToolCallingReentrancyClient will call a tool
        Output.WriteLine("Submitting message...");
        client.SubmitMessage(
            threadPath,
            "Call a tool please",
            contextPath: "User/TestUser");
        Output.WriteLine("Submitted");

        // Wait for message cells
        var msgIds = await twoMessages;
        Output.WriteLine($"Messages: [{string.Join(", ", msgIds)}]");

        // Wait for execution to finish — IsExecuting goes false. If the tool
        // call deadlocked the grain, this never flips and the test times out.
        var completed = await threadSyncStream
            .Select(change => change.Value?.Content as MeshThread)
            .Where(t => t != null && !t.IsExecuting)
            .Timeout(40.Seconds())
            .FirstAsync()
            .ToTask(ct);

        // Verification: the streaming loop completed (no deadlock) and produced
        // both a user-message cell and a response cell.
        completed.Should().NotBeNull("thread content should be observable");
        completed!.IsExecuting.Should().BeFalse("execution must complete — proves the tool-call round-trip did not deadlock the grain");
        completed.Messages.Count.Should().BeGreaterThanOrEqualTo(2, "user message + response message cells");

        Output.WriteLine($"PASSED — IsExecuting={completed.IsExecuting}, messages={completed.Messages.Count}");
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

        // Call Get tool — requires round-trip through the hub
        if (options?.Tools?.Any(t => t.Name == "Get") == true)
        {
            var call = new FunctionCallContent("test-get", "Get",
                new Dictionary<string, object?> { ["path"] = "@/User/TestUser" });
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

/// <summary>
/// Test factory that extends <see cref="ChatClientAgentFactory"/> — gets MeshPlugin
/// tools (including Get) and the function-calling middleware automatically from
/// the production pipeline. Only overrides <c>CreateChatClient</c> to return the
/// tool-calling fake.
/// </summary>
internal class ReentrancyTestAgentFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
{
    public override string Name => "ReentrancyTestFactory";
    public override IReadOnlyList<string> Models => ["test-model"];
    public override int Order => 0;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
        => new ToolCallingReentrancyClient();
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
            .AddInMemoryPersistence()
            .ConfigurePortalMesh()
            .AddGraph()
            .AddAI()
            .AddRowLevelSecurity()
            .AddMeshNodes(new MeshNode("TestUser", "User") { Name = "TestUser", NodeType = "User" })
            .AddMeshNodes(TestUserAdminAccess())
            .ConfigureServices(services =>
                services.AddSingleton<IChatClientFactory, ReentrancyTestAgentFactory>())
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    // TestUser-specific Admin (mirrors samples/Graph/Data/User/_Access/TestUser_Access.json).
    // Namespace MUST end in "/_Access" — see SecurityService.ComputeScopeRoles.
    private static MeshNode[] TestUserAdminAccess()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "TestUser",
            DisplayName = "Test User",
            Roles = [new RoleAssignment { Role = "Admin" }]
        };
        return [new("TestUser_Access", "User/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "TestUser Access",
            Content = assignment,
            MainNode = "User",
        }];
    }
}
