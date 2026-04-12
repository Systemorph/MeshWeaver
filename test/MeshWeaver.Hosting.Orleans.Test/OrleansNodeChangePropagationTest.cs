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
/// End-to-end Orleans test for NodeChangeEntry propagation through delegation chains.
///
/// Exercises the FULL production flow:
/// 1. Client creates a thread (like ThreadChatView.SendMessageAsync)
/// 2. Client submits a message (SubmitMessageRequest → thread grain)
/// 3. Thread grain creates user + response cells via Observable
/// 4. Execution starts on _Exec hosted hub (streaming loop via InvokeAsync)
/// 5. Top-level agent calls Create tool (MeshPlugin) → NodeChangeEntry generated
/// 6. Top-level agent delegates to sub-agent → sub-thread created, SubmitMessage posted
/// 7. Sub-agent calls Patch tool → NodeChangeEntry generated in sub-thread
/// 8. Sub-thread completes → SubmitMessageResponse.UpdatedNodes propagates up
/// 9. Parent merges node changes via ForwardNodeChange → aggregated with min/max versions
/// 10. Parent completes → final NodeChangeEntry list on response message
///
/// This test specifically validates:
/// - No deadlocks in the delegation chain (execution hub, TCS resolution, callbacks)
/// - Access context propagation through all hops
/// - NodeChangeEntry aggregation across delegation boundaries
/// - Correct routing of deeply nested sub-thread paths in Orleans
/// </summary>
public class OrleansNodeChangePropagationTest(ITestOutputHelper output) : TestBase(output)
{
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<RlsChatSiloConfigurator>();
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

    /// <summary>
    /// Creates a client hub with user identity — same as the Blazor portal does
    /// when a user opens a chat panel.
    /// </summary>
    private async Task<IMessageHub> GetClientAsync()
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", "nodechange"),
            config =>
            {
                config.TypeRegistry.AddAITypes();
                return config.AddLayoutClient();
            });
        // Simulate Blazor CircuitContext — user identity for access control
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
        Output.WriteLine($"CreateNodeRequest: id={node.Id}, path={node.Path}, target={targetAddress}");
        var response = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(new Address(targetAddress)), ct);
        Output.WriteLine($"CreateNodeResponse: success={response.Message.Success}, error={response.Message.Error ?? "(none)"}, path={response.Message.Node?.Path ?? "(null)"}, nodeType={response.Message.Node?.NodeType ?? "(null)"}");
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!.Path!;
    }

    private IObservable<IReadOnlyList<string>> ObserveThreadMessages(IMessageHub client, string threadPath)
    {
        var workspace = client.GetWorkspace();
        return workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                var content = node?.Content as MeshThread;
                var ids = content?.Messages ?? [];
                Output.WriteLine($"[Stream] Thread {threadPath}: {ids.Count} message IDs");
                return (IReadOnlyList<string>)ids;
            });
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
    /// Full chain: top agent calls Create → delegates → sub-agent calls Patch → NodeChangeEntry propagates.
    /// Tests for deadlocks: the execution hub (InvokeAsync) blocks during streaming;
    /// delegation TCS resolution must not require the blocked scheduler.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Delegation_NodeChanges_PropagateFromSubThread()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create thread — exactly like ThreadChatView.SendMessageAsync does
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "NodeChange propagation test", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);
        Output.WriteLine($"Thread created: {threadPath}");

        // 2. Subscribe to messages (like ThreadChatView data-binding)
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // 3. Submit message — triggers the ToolCallDelegatingChatClient which:
        //    Turn 1: calls Create (creates a Markdown node)
        //    Turn 2: calls delegate_to_agent (Executor)
        //    Turn 3: returns summary text after delegation completes
        Output.WriteLine("Posting SubmitMessageRequest (Create + Delegate chain)...");
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Create a doc and delegate updates to Executor",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);
        Output.WriteLine("SubmitMessageRequest succeeded — cells created");

        // 4. Wait for message IDs
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // 5. Wait for execution to complete — poll response message
        //    If the delegation chain deadlocks, this times out.
        var responsePath = $"{threadPath}/{msgIds[1]}";
        ThreadMessage? responseMsg = null;
        for (var i = 0; i < 60; i++)
        {
            responseMsg = await GetHubContentAsync<ThreadMessage>(client, responsePath, ct);
            // Wait until we have tool calls AND text (execution complete)
            if (responseMsg?.ToolCalls is { Count: >= 2 } && !string.IsNullOrEmpty(responseMsg.Text))
            {
                Output.WriteLine($"  Poll {i}: text='{responseMsg.Text?[..Math.Min(80, responseMsg.Text.Length)]}', toolCalls={responseMsg.ToolCalls.Count}, updatedNodes={responseMsg.UpdatedNodes.Count}");
                break;
            }
            Output.WriteLine($"  Poll {i}: text len={responseMsg?.Text?.Length ?? 0}, toolCalls={responseMsg?.ToolCalls?.Count ?? 0}");
            await Task.Delay(500, ct);
        }

        // 6. Verify response message has tool calls
        responseMsg.Should().NotBeNull("response message should exist after execution");
        responseMsg!.ToolCalls.Should().NotBeNullOrEmpty("agent should have made tool calls");

        var createCall = responseMsg.ToolCalls.FirstOrDefault(t => t.Name == "Create");
        createCall.Should().NotBeNull("agent should have called Create tool");
        createCall!.IsSuccess.Should().BeTrue("Create tool should succeed");
        Output.WriteLine($"Create tool call: success={createCall.IsSuccess}, args={createCall.Arguments?[..Math.Min(60, createCall.Arguments?.Length ?? 0)]}");

        var delegateCall = responseMsg.ToolCalls.FirstOrDefault(t => t.Name?.Contains("delegate") == true);
        delegateCall.Should().NotBeNull("agent should have called delegate_to_agent");
        delegateCall!.DelegationPath.Should().NotBeNullOrEmpty("delegation should have a sub-thread path");
        Output.WriteLine($"Delegation: path={delegateCall.DelegationPath}, success={delegateCall.IsSuccess}");

        // 7. Verify the Markdown node was created by the Create tool
        var meshService = Cluster.Client.ServiceProvider.GetRequiredService<IMeshService>();
        var createdNodes = await meshService
            .QueryAsync<MeshNode>("path:User/Roland/test-doc-nodechange", ct: ct)
            .ToListAsync(ct);
        createdNodes.Should().ContainSingle("Create tool should have created the Markdown node");
        Output.WriteLine($"Created node: {createdNodes[0].Path}, name={createdNodes[0].Name}");

        // 8. Verify sub-thread exists and completed
        var subThreadPath = delegateCall.DelegationPath!;
        var subThread = await GetHubContentAsync<MeshThread>(client, subThreadPath, ct);
        subThread.Should().NotBeNull("sub-thread should exist");
        subThread!.Messages.Should().HaveCount(2, "sub-thread should have user + response messages");
        Output.WriteLine($"Sub-thread: {subThreadPath}, messages={subThread.Messages.Count}");

        // 9. Verify sub-thread response has Patch tool call
        var subResponsePath = $"{subThreadPath}/{subThread.Messages[1]}";
        ThreadMessage? subResponseMsg = null;
        for (var i = 0; i < 30; i++)
        {
            subResponseMsg = await GetHubContentAsync<ThreadMessage>(client, subResponsePath, ct);
            if (subResponseMsg?.ToolCalls is { Count: > 0 }) break;
            await Task.Delay(200, ct);
        }
        subResponseMsg.Should().NotBeNull("sub-thread response should exist");
        var patchCall = subResponseMsg!.ToolCalls.FirstOrDefault(t => t.Name == "Patch");
        patchCall.Should().NotBeNull("sub-agent should have called Patch tool");
        Output.WriteLine($"Sub-thread Patch: success={patchCall!.IsSuccess}, args={patchCall.Arguments?[..Math.Min(60, patchCall.Arguments?.Length ?? 0)]}");

        // 10. Verify NodeChangeEntry propagated to parent response
        responseMsg.UpdatedNodes.Should().NotBeNullOrEmpty(
            "parent response should have aggregated UpdatedNodes from both Create and sub-thread Patch");
        Output.WriteLine($"UpdatedNodes on parent response: {responseMsg.UpdatedNodes.Count} entries");
        foreach (var entry in responseMsg.UpdatedNodes)
            Output.WriteLine($"  {entry.Operation}: {entry.Path} v{entry.VersionBefore}→v{entry.VersionAfter}");

        // The same node (test-doc-nodechange) was Created by parent and Patched by sub-thread.
        // Aggregation should give: min(VersionBefore), max(VersionAfter)
        var docChanges = responseMsg.UpdatedNodes.Where(e => e.Path.Contains("test-doc-nodechange")).ToList();
        docChanges.Should().ContainSingle(
            "changes to same node should be aggregated into one entry");
        var docChange = docChanges[0];
        docChange.VersionAfter.Should().BeGreaterThan(docChange.VersionBefore ?? 0,
            "aggregated version should show progression from create to patch");
        Output.WriteLine($"Aggregated: {docChange.Path} {docChange.Operation} v{docChange.VersionBefore}→v{docChange.VersionAfter}");
    }

    /// <summary>
    /// Resubmit test: after execution completes, click "Resubmit" (ArrowSync).
    /// The HandleResubmitMessage handler must not deadlock — it uses
    /// meshService.CreateNode (Observable) + workspace.UpdateMeshNode (non-blocking).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Resubmit_AfterExecution_DoesNotDeadlock()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create and execute a thread
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Resubmit deadlock test", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);

        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "First message",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)), ct);

        var msgIds = await twoMessages;
        Output.WriteLine($"Initial messages: [{string.Join(", ", msgIds)}]");

        // Wait for execution to complete
        for (var i = 0; i < 30; i++)
        {
            var thread = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
            if (thread?.IsExecuting == false) break;
            await Task.Delay(500, ct);
        }
        Output.WriteLine("Initial execution complete");

        // 2. Resubmit — sends ResubmitMessageRequest to the thread grain.
        //    This was the original deadlock: the handler subscribed to workspace streams.
        //    Now uses Observable + workspace.UpdateMeshNode.
        // Resubmit keeps user message (index 0) + adds new response = 2 messages.
        // But the IDs change (old response removed, new one added).
        // Watch for the stream to show a DIFFERENT set of IDs than the initial ones.
        var resubmittedMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2 && !ids.SequenceEqual(msgIds))
            .FirstAsync()
            .ToTask(ct);

        Output.WriteLine("Posting ResubmitMessageRequest...");
        var resubmitDelivery = client.Post(new ResubmitMessageRequest
        {
            ThreadPath = threadPath,
            MessageId = msgIds[0],
            UserMessageText = "Resubmitted message"
        }, o => o.WithTarget(new Address(threadPath)));
        Output.WriteLine($"ResubmitMessageRequest delivery: {resubmitDelivery != null}");

        // 3. Wait for message IDs to change — if deadlocked, this times out
        var newMsgIds = await resubmittedMessages;
        newMsgIds.Should().HaveCount(2,
            "resubmit should keep user message and replace response");
        newMsgIds[0].Should().Be(msgIds[0], "user message should be preserved");
        newMsgIds[1].Should().NotBe(msgIds[1], "response should be a new cell");
        Output.WriteLine($"After resubmit: [{string.Join(", ", newMsgIds)}]");

        // 4. Resubmit succeeded — messages changed, no deadlock.
        // The execution will complete asynchronously (streaming on _Exec hub).
        Output.WriteLine("Resubmit completed — messages updated, no deadlock!");
    }
}

/// <summary>
/// Chat client that exercises the full tool-calling and delegation chain:
/// - Turn 1: calls Create tool (creates a Markdown node)
/// - Turn 2 (after Create result): calls delegate_to_agent (Executor)
/// - Turn 3 (after delegation result): returns summary text
/// </summary>
internal class ToolCallDelegatingChatClient : IChatClient
{
    private int _callCount;
    public ChatClientMetadata Metadata => new("ToolCallDelegating");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _callCount);
        var hasFunctionResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        var hasCreateResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>()
            .Any(f => f.CallId == "call_create"));
        var hasDelegateResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>()
            .Any(f => f.CallId == "call_delegate"));

        // After delegation completes: return summary text
        if (hasDelegateResult)
        {
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    "Created the document and delegated updates. All node changes should propagate.")));
        }

        // After Create tool returns: call delegate_to_agent
        if (hasCreateResult && options?.Tools?.Any(t => t.Name == "delegate_to_agent") == true)
        {
            var delegateCall = new FunctionCallContent("call_delegate", "delegate_to_agent",
                new Dictionary<string, object?>
                {
                    ["agentName"] = "Worker",
                    ["task"] = "Patch the node at User/Roland/test-doc-nodechange: set name to 'Updated by sub-agent'"
                });
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [delegateCall])));
        }

        // First call: Create a Markdown node via MeshPlugin
        if (options?.Tools?.Any(t => t.Name == "Create") == true)
        {
            var nodeJson = JsonSerializer.Serialize(new
            {
                id = "test-doc-nodechange",
                @namespace = "User/Roland",
                nodeType = "Markdown",
                name = "Test Doc for NodeChange",
                content = "# Initial Content"
            });
            var createCall = new FunctionCallContent("call_create", "Create",
                new Dictionary<string, object?> { ["node"] = nodeJson });
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [createCall])));
        }

        // Fallback
        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Done.")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Delegate to non-streaming for simplicity — the framework handles both
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
        // Stream text word by word
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
/// Sub-agent chat client (Worker/Executor) that calls Patch tool:
/// - Turn 1: calls Patch on the node created by the parent
/// - Turn 2 (after Patch result): returns text
/// </summary>
internal class PatchToolChatClient : IChatClient
{
    private int _callCount;
    public ChatClientMetadata Metadata => new("PatchTool");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _callCount);
        var hasFunctionResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());

        if (hasFunctionResult)
        {
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    "Patched the document successfully. Node changes tracked.")));
        }

        // First call: Patch the node
        if (options?.Tools?.Any(t => t.Name == "Patch") == true)
        {
            var fieldsJson = JsonSerializer.Serialize(new { name = "Updated by sub-agent" });
            var patchCall = new FunctionCallContent("call_patch", "Patch",
                new Dictionary<string, object?>
                {
                    ["path"] = "User/Roland/test-doc-nodechange",
                    ["fields"] = fieldsJson
                });
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [patchCall])));
        }

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "No Patch tool available.")));
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
/// Factory: top-level agents (Navigator/Planner/Orchestrator) get ToolCallDelegatingChatClient;
/// sub-agents (Worker/Executor/Coder) get PatchToolChatClient.
/// </summary>
internal class NodeChangeTestChatClientFactory : IChatClientFactory
{
    public string Name => "NodeChangeTestFactory";
    public IReadOnlyList<string> Models => ["fake-model"];
    public int Order => 0;

    public ChatClientAgent CreateAgent(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        var isTopLevel = config.IsDefault || config.Id is "Navigator" or "Planner" or "Orchestrator";
        IChatClient chatClient = isTopLevel
            ? new ToolCallDelegatingChatClient()
            : new PatchToolChatClient();

        return new ChatClientAgent(
            chatClient: chatClient,
            instructions: config.Instructions ?? "Test assistant with tools.",
            name: config.Id,
            description: config.Description ?? config.Id,
            tools: [], // Tools added by ChatClientAgentFactory
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

/// <summary>
/// Silo configurator: production-like setup with Graph + AI + RLS + NodeChangeTestChatClientFactory.
/// </summary>
public class NodeChangePropagationSiloConfigurator : ISiloConfigurator, IHostConfigurator
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
            .AddMeshNodes(
                new MeshNode("Roland", "User") { Name = "Roland", NodeType = "User" })
            .AddMeshNodes(PublicEditorAccess())
            .ConfigureServices(services =>
                services.AddSingleton<IChatClientFactory>(new NodeChangeTestChatClientFactory()))
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
