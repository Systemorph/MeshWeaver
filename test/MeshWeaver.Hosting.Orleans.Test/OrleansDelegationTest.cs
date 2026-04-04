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

/// <summary>
/// Delegation tests using the PRODUCTION ChatClientAgentFactory pipeline.
/// The test factory extends ChatClientAgentFactory so delegation tools,
/// MeshPlugin, and function calling middleware are all registered automatically.
/// This tests the real delegation flow: agent calls delegate_to_agent →
/// sub-thread created → sub-agent executes → result propagates back.
/// </summary>
public class OrleansDelegationTest(ITestOutputHelper output) : TestBase(output)
{
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<DelegationTestSiloConfigurator>();
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
            new Address("client", "delegation-test"),
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
    /// Full delegation flow using the production agent pipeline:
    /// 1. Submit message to thread
    /// 2. Default agent (Orchestrator) calls delegate_to_agent tool
    /// 3. Sub-thread is created, sub-agent executes
    /// 4. Tool calls appear on the response message with DelegationPath
    /// 5. Parent completes with text
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Delegation_ToolCallsAppear_WithDelegationPath()
    {
        var ct = new CancellationTokenSource(25.Seconds()).Token;
        var client = await GetClientAsync();
        var workspace = client.GetWorkspace();

        // 1. Create thread
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Delegation tool call test", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);
        Output.WriteLine($"1. Thread: {threadPath}");

        // 2. Subscribe to messages
        var twoMessages = workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                return (node?.Content as MeshThread)?.Messages ?? [];
            })
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // 3. Submit message — triggers delegation via production ChatClientAgentFactory
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Please delegate this research task",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);
        Output.WriteLine("2. Message submitted");

        // 4. Wait for message cells
        var msgIds = await twoMessages;
        var responsePath = $"{threadPath}/{msgIds[1]}";
        Output.WriteLine($"3. Response message: {msgIds[1]}");

        // 5. Subscribe to response message — wait for tool calls
        Output.WriteLine("4. Waiting for tool calls on response...");
        var responseStream = workspace.GetRemoteStream<MeshNode>(new Address(responsePath))!;
        ThreadMessage? finalResponse = null;

        // Wait for execution to complete (text appears + all tool calls have results)
        finalResponse = await responseStream
            .Select(nodes =>
            {
                var msg = nodes?.FirstOrDefault(n => n.Path == responsePath)?.Content as ThreadMessage;
                if (msg != null && msg.ToolCalls.Count > 0)
                    Output.WriteLine($"  [STREAM] text={msg.Text?.Length ?? 0}ch, toolCalls={msg.ToolCalls.Count}, delegations={msg.ToolCalls.Count(c => !string.IsNullOrEmpty(c.DelegationPath))}");
                return msg;
            })
            .Where(m => !string.IsNullOrEmpty(m?.Text) && m!.ToolCalls.Count > 0 && m.ToolCalls.All(c => c.Result != null))
            .Timeout(20.Seconds())
            .FirstAsync()
            .ToTask(ct);

        // 6. Verify tool calls
        Output.WriteLine($"5. Response: text='{finalResponse!.Text?[..Math.Min(50, finalResponse.Text?.Length ?? 0)]}', toolCalls={finalResponse.ToolCalls.Count}");
        foreach (var tc in finalResponse.ToolCalls)
            Output.WriteLine($"   - {tc.Name}: success={tc.IsSuccess}, delegation={tc.DelegationPath ?? "(none)"}");

        finalResponse.ToolCalls.Should().NotBeEmpty("agent should have called delegate_to_agent");
        var delegationCall = finalResponse.ToolCalls.FirstOrDefault(c => c.Name.Contains("delegate"));
        delegationCall.Should().NotBeNull("should have a delegation tool call");
        delegationCall!.DelegationPath.Should().NotBeNullOrEmpty("delegation tool call should have DelegationPath");
        delegationCall.IsSuccess.Should().BeTrue("delegation should succeed");

        Output.WriteLine($"6. DelegationPath: {delegationCall.DelegationPath}");

        // 7. Verify sub-thread exists and completed
        var subThreadPath = delegationCall.DelegationPath!;
        var subThread = await GetHubContentAsync<MeshThread>(client, subThreadPath, ct);
        subThread.Should().NotBeNull("sub-thread should exist");
        subThread!.Messages.Should().HaveCount(2, "sub-thread should have user + response");
        Output.WriteLine($"7. Sub-thread: {subThreadPath}, messages={subThread.Messages.Count}");
        Output.WriteLine("8. PASSED — full delegation with DelegationPath");
    }

    /// <summary>
    /// Resubmit after delegation: verifies no deadlock when resubmitting
    /// a message that previously triggered delegation.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Resubmit_AfterDelegation_DoesNotDeadlock()
    {
        var ct = new CancellationTokenSource(25.Seconds()).Token;
        var client = await GetClientAsync();
        var workspace = client.GetWorkspace();

        // 1. Create thread and submit
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Resubmit delegation test", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);

        var twoMessages = workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes => (nodes?.FirstOrDefault(n => n.Path == threadPath)?.Content as MeshThread)?.Messages ?? [])
            .Where(ids => ids.Count >= 2)
            .FirstAsync().ToTask(ct);

        await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Delegate something",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)), ct);

        var msgIds = await twoMessages;
        Output.WriteLine($"1. Initial messages: [{string.Join(", ", msgIds)}]");

        // 2. Wait for execution to complete
        await workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes => (nodes?.FirstOrDefault(n => n.Path == threadPath)?.Content as MeshThread))
            .Where(t => t != null && !t.IsExecuting)
            .Timeout(20.Seconds())
            .FirstAsync().ToTask(ct);
        Output.WriteLine("2. Initial execution complete");

        // 3. Resubmit
        var resubmitted = workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes => (nodes?.FirstOrDefault(n => n.Path == threadPath)?.Content as MeshThread)?.Messages ?? [])
            .Where(ids => ids.Count >= 2 && !ids.SequenceEqual(msgIds))
            .Timeout(15.Seconds())
            .FirstAsync().ToTask(ct);

        client.Post(new ResubmitMessageRequest
        {
            ThreadPath = threadPath,
            MessageId = msgIds[0],
            UserMessageText = "Delegate something"
        }, o => o.WithTarget(new Address(threadPath)));

        var newMsgIds = await resubmitted;
        Output.WriteLine($"3. After resubmit: [{string.Join(", ", newMsgIds)}]");
        newMsgIds[0].Should().Be(msgIds[0], "user message preserved");
        newMsgIds[1].Should().NotBe(msgIds[1], "new response cell");
        Output.WriteLine("4. PASSED — resubmit after delegation, no deadlock");
    }
}

/// <summary>
/// Test factory that extends ChatClientAgentFactory — gets delegation tools,
/// MeshPlugin, and middleware automatically from the production pipeline.
/// Only overrides CreateChatClient to return a fake.
/// </summary>
internal class DelegationTestAgentFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
{
    public override string Name => "DelegationTestFactory";
    public override IReadOnlyList<string> Models => ["test-model"];
    public override int Order => 0;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Default agent (Orchestrator) delegates; sub-agents return text
        var isDefault = agentConfig.IsDefault || agentConfig.Id is "Orchestrator" or "Planner";
        return isDefault
            ? new DelegatingTestChatClient()
            : new SimpleTestChatClient("Sub-thread completed the research task successfully.");
    }
}

/// <summary>
/// Chat client that emits delegate_to_agent on first call, text after.
/// </summary>
internal class DelegatingTestChatClient : IChatClient
{
    private int _callCount;
    public ChatClientMetadata Metadata => new("DelegatingTest");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var hasFunctionResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        if (hasFunctionResult)
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "Delegation completed. Task done.")));

        // Check if delegate_to_agent tool is available
        if (options?.Tools?.Any(t => t.Name == "delegate_to_agent") == true)
        {
            var call = new FunctionCallContent("del-1", "delegate_to_agent",
                new Dictionary<string, object?>
                {
                    ["agentName"] = "Worker",
                    ["task"] = "Research the topic and report findings"
                });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "No delegation tool available.")));
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
/// Simple chat client that returns canned text.
/// </summary>
internal class SimpleTestChatClient(string response) : IChatClient
{
    public ChatClientMetadata Metadata => new("SimpleTest");

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

public class DelegationTestSiloConfigurator : ISiloConfigurator, IHostConfigurator
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
            // Register factory via DI — ChatClientAgentFactory resolves from hub ServiceProvider
            .ConfigureServices(services =>
                services.AddSingleton<IChatClientFactory, DelegationTestAgentFactory>())
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
