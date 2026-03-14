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
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests the full SubmitMessageRequest handler flow:
/// Client creates thread → posts SubmitMessageRequest → handler creates
/// user message node + response message node → agent streams response.
/// </summary>
public class ExecuteThreadMessageTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FakeResponseText = "This is a test response from the fake agent.";

    private const string ContextPath = "User/TestUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .AddMeshNodes(new MeshNode("TestUser", "User") { Name = "Test User", NodeType = "User" })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(SubmitMessageResponse))
            .WithTypes(typeof(CreateThreadResponse));

    [Fact]
    public async Task SubmitMessage_CreatesUserAndResponseNodes()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;

        // 1. Create thread via CreateThreadRequest sent to the context node's hub
        //    (simulates ThreadChatView flow — sends to context node address)
        var client = GetClient();
        var createResponse = await client.AwaitResponse(
            new CreateThreadRequest
            {
                Namespace = ContextPath,
                UserMessageText = "Hello, can you help me?"
            },
            o => o.WithTarget(new Address(ContextPath)),
            ct);
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
        var threadPath = createResponse.Message.ThreadPath!;

        // Verify satellite MainNode: should point to _Thread's parent namespace
        var threadNode = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}").FirstOrDefaultAsync(ct);
        threadNode.Should().NotBeNull("thread node should exist");
        threadNode!.MainNode.Should().Be($"User/TestUser/{ThreadNodeType.ThreadPartition}",
            "satellite MainNode should point to the _Thread namespace");
        threadPath.Should().Contain($"/{ThreadNodeType.ThreadPartition}/",
            "thread path should use _Thread partition");

        // 2. Post SubmitMessageRequest (fire-and-forget, like the Blazor client does)
        client.Post(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Hello, can you help me?"
            },
            o => o.WithTarget(new Address(threadPath)));

        // 3. Poll for child message nodes with non-empty response text
        // The handler creates an empty response node first, then streams text into it.
        List<MeshNode> children = [];
        ThreadMessage? assistantMsg = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500, ct);
            children = await MeshQuery.QueryAsync<MeshNode>(
                $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"
            ).ToListAsync(ct);
            assistantMsg = children
                .Select(n => n.Content)
                .OfType<ThreadMessage>()
                .FirstOrDefault(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Text));
            Output.WriteLine($"Poll {i}: {children.Count} children, response text: '{assistantMsg?.Text?[..Math.Min(assistantMsg.Text.Length, 30)]}'");
            if (children.Count >= 2 && assistantMsg != null)
                break;
        }

        children.Should().HaveCountGreaterThanOrEqualTo(2,
            "should have user message + agent response");

        var messages = children
            .Select(n => n.Content)
            .OfType<ThreadMessage>()
            .ToList();

        // User message
        var userMsg = messages.FirstOrDefault(m => m.Role == "user");
        userMsg.Should().NotBeNull("should contain the user message");
        userMsg!.Text.Should().Be("Hello, can you help me?");
        userMsg.Type.Should().Be(ThreadMessageType.ExecutedInput);

        // Agent response
        assistantMsg.Should().NotBeNull("should contain the agent response with text");
        assistantMsg!.Text.Should().NotBeEmpty("agent should have generated a response");
        assistantMsg.Type.Should().Be(ThreadMessageType.AgentResponse);
    }

    [Fact]
    public async Task SubmitMessage_SecondMessage_IncrementsMessageNumbers()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        // Create thread via CreateThreadRequest sent to the context node's hub
        var client = GetClient();
        var createResponse = await client.AwaitResponse(
            new CreateThreadRequest
            {
                Namespace = ContextPath,
                UserMessageText = "First question"
            },
            o => o.WithTarget(new Address(ContextPath)),
            ct);
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
        var threadPath = createResponse.Message.ThreadPath!;

        // First message
        var response1 = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "First question"
            },
            o => o.WithTarget(new Address(threadPath)),
            ct);
        response1.Message.Success.Should().BeTrue(response1.Message.Error);

        // Second message
        var response2 = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Follow-up question"
            },
            o => o.WithTarget(new Address(threadPath)),
            ct);
        response2.Message.Success.Should().BeTrue(response2.Message.Error);

        // Verify 4 message nodes (2 user + 2 assistant)
        var children = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"
        ).ToListAsync(ct);

        children.Should().HaveCount(4, "should have 2 user messages + 2 agent responses");

        var ordered = children.OrderBy(n => n.Order).ToList();
        ordered[0].Order.Should().Be(1);
        ordered[1].Order.Should().Be(2);
        ordered[2].Order.Should().Be(3);
        ordered[3].Order.Should().Be(4);
    }

    [Fact]
    public async Task ObserveQuery_EmitsThreadMessageNodes_Reactively()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;

        // 1. Create thread
        var client = GetClient();
        var createResponse = await client.AwaitResponse(
            new CreateThreadRequest
            {
                Namespace = ContextPath,
                UserMessageText = "Reactive test"
            },
            o => o.WithTarget(new Address(ContextPath)),
            ct);
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
        var threadPath = createResponse.Message.ThreadPath!;
        Output.WriteLine($"Thread created at: {threadPath}");

        // 2. Subscribe to ObserveQuery BEFORE submitting message
        //    This mirrors what BuildCellsStream does in ThreadLayoutAreas
        var request = MeshQueryRequest.FromQuery(
            $"namespace:{threadPath} nodeType:ThreadMessage sort:Order-asc");

        var observedChanges = new List<QueryResultChange<MeshNode>>();
        var twoNodesAppeared = new TaskCompletionSource<bool>();
        var responseTextPopulated = new TaskCompletionSource<bool>();
        var allNodes = ImmutableList<MeshNode>.Empty;

        using var subscription = MeshQuery.ObserveQuery<MeshNode>(request)
            .Subscribe(change =>
            {
                observedChanges.Add(change);
                Output.WriteLine($"ObserveQuery change: {change.ChangeType}, items: {change.Items.Count}");
                foreach (var item in change.Items)
                {
                    var msg = item.Content as ThreadMessage;
                    Output.WriteLine($"  Node: {item.Path}, Role: {msg?.Role}, Text: '{msg?.Text?[..Math.Min(msg.Text?.Length ?? 0, 40)]}'");
                }

                // Accumulate nodes (same logic as BuildCellsStream.Scan)
                allNodes = change.ChangeType switch
                {
                    QueryChangeType.Initial or QueryChangeType.Reset =>
                        change.Items.ToImmutableList(),
                    QueryChangeType.Added =>
                        allNodes.AddRange(change.Items),
                    QueryChangeType.Updated =>
                        allNodes.Select(n => change.Items.FirstOrDefault(u => u.Path == n.Path) ?? n)
                            .ToImmutableList(),
                    _ => allNodes
                };

                Output.WriteLine($"  Total accumulated nodes: {allNodes.Count}");

                if (allNodes.Count >= 2 && !twoNodesAppeared.Task.IsCompleted)
                    twoNodesAppeared.TrySetResult(true);

                // Check if response text is populated
                var assistantMsg = allNodes
                    .Select(n => n.Content)
                    .OfType<ThreadMessage>()
                    .FirstOrDefault(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Text));
                if (assistantMsg != null && !responseTextPopulated.Task.IsCompleted)
                {
                    Output.WriteLine($"  Response text populated: '{assistantMsg.Text[..Math.Min(assistantMsg.Text.Length, 60)]}'");
                    responseTextPopulated.TrySetResult(true);
                }
            });

        // 3. Submit message AFTER subscription is active
        client.Post(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Reactive test"
            },
            o => o.WithTarget(new Address(threadPath)));

        // 4. Wait for ObserveQuery to emit 2 nodes (user + response)
        var nodesAppeared = await Task.WhenAny(twoNodesAppeared.Task, Task.Delay(10.Seconds(), ct));
        nodesAppeared.Should().Be(twoNodesAppeared.Task,
            "ObserveQuery should emit at least 2 ThreadMessage nodes within 10 seconds");

        // 5. Wait for response text to be populated (streaming completes)
        var textPopulated = await Task.WhenAny(responseTextPopulated.Task, Task.Delay(15.Seconds(), ct));
        textPopulated.Should().Be(responseTextPopulated.Task,
            "ObserveQuery should emit an Updated change with non-empty response text within 15 seconds");

        // 6. Verify final state
        allNodes.Should().HaveCountGreaterThanOrEqualTo(2, "should have user + assistant nodes");

        var userMsg = allNodes.Select(n => n.Content).OfType<ThreadMessage>()
            .FirstOrDefault(m => m.Role == "user");
        userMsg.Should().NotBeNull("should have a user message");
        userMsg!.Text.Should().Be("Reactive test");

        var assistantFinal = allNodes.Select(n => n.Content).OfType<ThreadMessage>()
            .FirstOrDefault(m => m.Role == "assistant");
        assistantFinal.Should().NotBeNull("should have an assistant message");
        assistantFinal!.Text.Should().NotBeEmpty("assistant should have response text");

        Output.WriteLine($"Total ObserveQuery changes received: {observedChanges.Count}");
        foreach (var c in observedChanges)
            Output.WriteLine($"  {c.ChangeType}: {c.Items.Count} items");
    }

    [Fact]
    public async Task SubmitMessage_UpdatesThreadCellReferences_InDataSource()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;

        // 1. Create thread
        var client = GetClient();
        var createResponse = await client.AwaitResponse(
            new CreateThreadRequest
            {
                Namespace = ContextPath,
                UserMessageText = "DataSource test"
            },
            o => o.WithTarget(new Address(ContextPath)),
            ct);
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
        var threadPath = createResponse.Message.ThreadPath!;
        Output.WriteLine($"Thread created at: {threadPath}");

        // 2. Submit message and wait for completion
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "DataSource test"
            },
            o => o.WithTarget(new Address(threadPath)),
            ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        // 3. Access the thread hub's workspace and verify ThreadCellReference collection
        var threadHub = Mesh.GetHostedHub(new Address(threadPath), HostedHubCreation.Never);
        threadHub.Should().NotBeNull("thread hub should exist after message submission");

        var workspace = threadHub!.ServiceProvider.GetRequiredService<IWorkspace>();
        var cellRefStream = workspace.GetStream<ThreadCellReference>();
        cellRefStream.Should().NotBeNull("ThreadCellReference should be registered in the DataSource");

        ThreadCellReference[]? cellRefs = null;
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500, ct);
            cellRefs = await cellRefStream!.FirstAsync();
            Output.WriteLine($"Poll {i}: {cellRefs?.Length ?? 0} cell refs");
            if (cellRefs is { Length: >= 2 })
                break;
        }

        cellRefs.Should().NotBeNull();
        cellRefs!.Length.Should().BeGreaterThanOrEqualTo(2,
            "should have at least 2 cell references (user + response)");

        var ordered = cellRefs.OrderBy(r => r.Order).ToArray();
        ordered[0].Order.Should().Be(1, "first cell should be order 1 (user message)");
        ordered[1].Order.Should().Be(2, "second cell should be order 2 (response)");
        ordered[0].Path.Should().Contain(threadPath, "cell path should be under thread path");
        ordered[1].Path.Should().Contain(threadPath, "cell path should be under thread path");

        Output.WriteLine($"Cell refs verified: {string.Join(", ", ordered.Select(r => $"{r.Path}(order={r.Order})"))}");
    }

    [Fact]
    public async Task SubmitMessage_CellsAppearInLayoutAreaStream()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;

        // 1. Create thread
        var client = GetClient();
        var createResponse = await client.AwaitResponse(
            new CreateThreadRequest
            {
                Namespace = ContextPath,
                UserMessageText = "LayoutArea stream test"
            },
            o => o.WithTarget(new Address(ContextPath)),
            ct);
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
        var threadPath = createResponse.Message.ThreadPath!;
        Output.WriteLine($"Thread created at: {threadPath}");

        // 2. Subscribe to the thread's layout area stream (like LayoutAreaView does)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(ThreadNodeType.ThreadArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath), reference);

        // 3. Set up a subscription that watches for cells data to appear
        var cellsAppeared = new TaskCompletionSource<JsonElement>();
        var changeLog = new List<string>();
        using var subscription = stream.Subscribe(change =>
        {
            var current = change.Value;
            if (current.ValueKind == JsonValueKind.Object &&
                current.TryGetProperty("data", out var dataSection) &&
                dataSection.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in dataSection.EnumerateObject())
                {
                    changeLog.Add($"data['{prop.Name}'] = {prop.Value.ValueKind}" +
                        (prop.Value.ValueKind == JsonValueKind.Array ? $"({prop.Value.GetArrayLength()})" : ""));
                    if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0)
                    {
                        cellsAppeared.TrySetResult(prop.Value);
                    }
                }
            }
            else
            {
                changeLog.Add($"value kind={current.ValueKind}");
            }
        });

        // 4. Submit message
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "LayoutArea stream test"
            },
            o => o.WithTarget(new Address(threadPath)),
            ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);
        Output.WriteLine("SubmitMessageResponse received, waiting for cells...");

        // 5. Wait for cells to appear in the subscription
        var completed = await Task.WhenAny(cellsAppeared.Task, Task.Delay(15.Seconds(), ct));

        // Log all changes for debugging
        Output.WriteLine($"Total stream changes observed: {changeLog.Count}");
        foreach (var log in changeLog)
            Output.WriteLine($"  {log}");

        completed.Should().Be(cellsAppeared.Task,
            "threadCells data should appear in the layout area stream within 15 seconds");
        var cellsData = await cellsAppeared.Task;
        cellsData.GetArrayLength().Should().BeGreaterThanOrEqualTo(2,
            "should have at least 2 cells (user + response)");
    }

    #region Fake Chat Client Infrastructure

    private class FakeChatClient : IChatClient
    {
        private readonly string response;

        public FakeChatClient(string response) => this.response = response;

        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in response.Split(' '))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private class FakeChatClientFactory : IChatClientFactory
    {
        public string Name => "FakeFactory";
        public IReadOnlyList<string> Models => ["fake-model"];
        public int Order => 0;

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
        {
            var agent = new ChatClientAgent(
                chatClient: new FakeChatClient(FakeResponseText),
                instructions: config.Instructions ?? "You are a helpful test assistant.",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null
            );
            return Task.FromResult(agent);
        }
    }

    #endregion
}
