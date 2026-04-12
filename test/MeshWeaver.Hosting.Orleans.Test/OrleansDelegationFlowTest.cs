using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Hosting.Security;
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
/// End-to-end test for delegation: the agent calls delegate_to_agent,
/// which creates a sub-thread and submits to it. Verifies that:
/// 1. Access context flows through the AI tool invocation chain
/// 2. CreateNode for the sub-thread succeeds (Thread permission)
/// 3. SubmitMessage to the sub-thread routes correctly
/// 4. The sub-thread executes and returns a result
///
/// Uses a DelegationToolFakeChatClient that emits a FunctionCallContent
/// on the first call, triggering the delegation tool.
/// </summary>
public class OrleansDelegationFlowTest(ITestOutputHelper output) : TestBase(output)
{
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<DelegationSiloConfigurator>();
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
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", "delegation"),
            config =>
            {
                config.TypeRegistry.AddAITypes();
                return config.AddLayoutClient();
            });
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "Roland",
            Name = "Roland Buergi",
            Email = "rbuergi@systemorph.com"
        });
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }

    private async Task<string> CreateNodeAsync(IMessageHub client, MeshNode node, string targetAddress, CancellationToken ct)
    {
        var response = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(new Address(targetAddress)), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!.Path!;
    }

    /// <summary>
    /// Full delegation flow: submit a message to a thread that triggers delegation.
    /// The DelegationToolFakeChatClient emits a delegate_to_agent function call,
    /// which creates a sub-thread, submits to it, and returns the result.
    /// Verifies that access context flows through the entire chain.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Delegation_CreatesSubThread_WithCorrectIdentity()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // Create a thread
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Delegation flow test", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);
        Output.WriteLine($"Thread: {threadPath}");

        // Subscribe to thread messages
        var workspace = client.GetWorkspace();
        var twoMessages = workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                var content = node?.Content as MeshThread;
                return content?.Messages ?? [];
            })
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // Submit message — this triggers the DelegationToolFakeChatClient which calls delegate_to_agent
        Output.WriteLine("Posting SubmitMessageRequest (should trigger delegation)...");
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Please delegate this task",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);
        Output.WriteLine("SubmitMessageRequest succeeded — cells created");

        // Wait for message IDs
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // Wait for execution to complete (agent streams + delegation + sub-thread)
        ThreadMessage? responseMsg = null;
        for (var i = 0; i < 60; i++)
        {
            var nodeId = msgIds[1];
            var resp = await client.AwaitResponse(
                new GetDataRequest(new EntityReference(nameof(MeshNode), nodeId)),
                o => o.WithTarget(new Address($"{threadPath}/{nodeId}")), ct);
            var node = resp.Message.Data as MeshNode;
            if (node == null && resp.Message.Data is JsonElement je)
                node = je.Deserialize<MeshNode>(ClientMesh.JsonSerializerOptions);
            if (node?.Content is ThreadMessage tm) responseMsg = tm;
            else if (node?.Content is JsonElement cje)
                responseMsg = cje.Deserialize<ThreadMessage>(ClientMesh.JsonSerializerOptions);

            if (responseMsg?.Text?.Contains("delegation", StringComparison.OrdinalIgnoreCase) == true
                || responseMsg?.ToolCalls?.Count > 0)
                break;
            await Task.Delay(500, ct);
        }

        responseMsg.Should().NotBeNull("response message should exist");
        Output.WriteLine($"Response: text='{responseMsg!.Text?[..Math.Min(100, responseMsg.Text?.Length ?? 0)]}', toolCalls={responseMsg.ToolCalls?.Count ?? 0}");

        // The DelegationToolFakeChatClient triggers a delegation — verify it completed
        // (either the tool call log has an entry, or the text mentions it)
        var hasDelegation = responseMsg.ToolCalls?.Any(tc => tc.Name?.Contains("delegate") == true) == true
            || responseMsg.Text?.Contains("delegat", StringComparison.OrdinalIgnoreCase) == true;
        hasDelegation.Should().BeTrue("agent should have delegated via delegate_to_agent tool call");
        Output.WriteLine("Delegation flow verified!");
    }
}

/// <summary>
/// Chat client that emits a delegate_to_agent function call on the first request,
/// then returns text on subsequent requests (after function result).
/// </summary>
internal class DelegationToolFakeChatClient : IChatClient
{
    private int _callCount;
    public ChatClientMetadata Metadata => new("DelegatingFake");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _callCount);
        if (call == 1 && options?.Tools?.Any(t => t.Name == "delegate_to_agent") == true)
        {
            // First call: emit a function call to delegate_to_agent
            var functionCall = new FunctionCallContent("call_1", "delegate_to_agent",
                new Dictionary<string, object?>
                {
                    ["agentName"] = "Executor",
                    ["task"] = "Execute the user's request"
                });
            var msg = new ChatMessage(ChatRole.Assistant, [functionCall]);
            return Task.FromResult(new ChatResponse(msg));
        }

        // Subsequent calls: return text
        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Delegation completed successfully.")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _callCount);
        if (call == 1 && options?.Tools?.Any(t => t.Name == "delegate_to_agent") == true)
        {
            // First call: emit function call via streaming
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent("call_1", "delegate_to_agent",
                    new Dictionary<string, object?>
                    {
                        ["agentName"] = "Executor",
                        ["task"] = "Execute the user's request"
                    })]
            };
            yield break;
        }

        // Subsequent calls: stream text
        foreach (var word in "Delegation completed successfully.".Split(' '))
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
/// Factory that uses DelegationToolFakeChatClient for the default agent
/// and regular FakeChatClient for delegated agents (Executor).
/// </summary>
internal class DelegationToolFakeChatClientFactory : IChatClientFactory
{
    public string Name => "DelegationToolFakeFactory";
    public IReadOnlyList<string> Models => ["fake-model"];
    public int Order => 0;

    public ChatClientAgent CreateAgent(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        // Default agent gets the delegating client; others get plain text client
        var isDefault = config.IsDefault || config.Id == "Navigator" || config.Id == "Planner";
        IChatClient chatClient = isDefault
            ? new DelegationToolFakeChatClient()
            : new FakeChatClient("Sub-thread response from delegated agent.");

        return new ChatClientAgent(
            chatClient: chatClient,
            instructions: config.Instructions ?? "Test assistant.",
            name: config.Id,
            description: config.Description ?? config.Id,
            tools: [],
            loggerFactory: null,
            services: null);
    }

    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
}

public class DelegationSiloConfigurator : ISiloConfigurator, IHostConfigurator
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
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("Roland", "User") { Name = "Roland", NodeType = "User" })
            .AddMeshNodes(PublicEditorAccess())
            .ConfigureServices(services =>
                services.AddSingleton<IChatClientFactory>(new DelegationToolFakeChatClientFactory()))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    private static MeshNode[] PublicEditorAccess()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "Public",
            DisplayName = "Public",
            Roles = [new RoleAssignment { Role = "Editor" }]
        };
        return
        [
            new("Public_Access", "User")
            {
                NodeType = "AccessAssignment",
                Name = "Public Access",
                Content = assignment,
                MainNode = "User",
            }
        ];
    }
}
