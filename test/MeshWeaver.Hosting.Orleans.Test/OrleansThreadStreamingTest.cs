using System;
using System.Collections.Generic;
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
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
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

    /// <summary>
    /// Full delegation flow: submit message → parent delegates to sub-thread →
    /// sub-thread streams text → parent receives result.
    /// Traces every step to find where communication breaks.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task DelegationFlow_SubThreadStreamsText_ParentCompletes()
    {
        var ct = new CancellationTokenSource(100.Seconds()).Token;
        var client = await GetClientAsync();

        // Create thread
        var response = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, "Delegation flow test")),
            o => o.WithTarget(new Address(ContextPath)), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        var threadPath = response.Message.Node!.Path!;
        Output.WriteLine($"1. Thread created: {threadPath}");

        // Subscribe to thread node for execution state
        var workspace = client.GetWorkspace();
        var threadUpdates = new List<string>();
        workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Subscribe(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                var thread = node?.Content as MeshThread;
                if (thread != null)
                {
                    var msg = $"Thread: IsExecuting={thread.IsExecuting}, Status={thread.ExecutionStatus ?? "(null)"}, Messages={thread.Messages.Count}, ActiveMsg={thread.ActiveMessageId ?? "(null)"}";
                    threadUpdates.Add(msg);
                    Output.WriteLine($"  [STREAM] {msg}");
                }
            });

        // Subscribe to messages appearing
        var twoMessages = workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                return (node?.Content as MeshThread)?.Messages ?? [];
            })
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // Submit message
        Output.WriteLine("2. Submitting message...");
        var submit = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Use the test tool please",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submit.Message.Success.Should().BeTrue(submit.Message.Error);
        Output.WriteLine("3. Message submitted successfully");

        // Wait for 2 message IDs
        var msgIds = await twoMessages;
        Output.WriteLine($"4. Messages appeared: [{string.Join(", ", msgIds)}]");
        var responseMsgId = msgIds[1];
        var responsePath = $"{threadPath}/{responseMsgId}";

        // Now poll the response message for tool calls AND text
        Output.WriteLine("5. Polling response message for content...");
        ThreadMessage? finalResponse = null;
        for (var i = 0; i < 100; i++)
        {
            var responseMsg = await GetHubContentAsync<ThreadMessage>(client, responsePath, ct);
            if (responseMsg != null)
            {
                var hasText = !string.IsNullOrEmpty(responseMsg.Text);
                var hasTools = responseMsg.ToolCalls.Count > 0;
                if (hasText || hasTools || i % 10 == 0)
                {
                    Output.WriteLine($"  [POLL {i}] text={responseMsg.Text?.Length ?? 0}chars, toolCalls={responseMsg.ToolCalls.Count}, delegations={responseMsg.ToolCalls.Count(c => !string.IsNullOrEmpty(c.DelegationPath))}");
                }
                if (hasText && responseMsg.ToolCalls.All(c => c.Result != null))
                {
                    finalResponse = responseMsg;
                    break;
                }
            }
            await Task.Delay(300, ct);
        }

        // Report final state
        Output.WriteLine($"6. Thread updates collected: {threadUpdates.Count}");
        foreach (var u in threadUpdates.TakeLast(5))
            Output.WriteLine($"  {u}");

        if (finalResponse != null)
        {
            Output.WriteLine($"7. PASS: Response text='{finalResponse.Text}' ({finalResponse.Text!.Length} chars)");
            Output.WriteLine($"   Tool calls: {finalResponse.ToolCalls.Count}");
            foreach (var tc in finalResponse.ToolCalls)
                Output.WriteLine($"   - {tc.DisplayName ?? tc.Name}: success={tc.IsSuccess}, delegation={tc.DelegationPath ?? "(none)"}");
        }
        else
        {
            // Dump everything we know
            Output.WriteLine("7. FAIL: Response message never got text");
            var lastThread = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
            Output.WriteLine($"   Thread: IsExecuting={lastThread?.IsExecuting}, Status={lastThread?.ExecutionStatus}");
            var lastMsg = await GetHubContentAsync<ThreadMessage>(client, responsePath, ct);
            Output.WriteLine($"   Response: text={lastMsg?.Text?.Length ?? 0}chars, toolCalls={lastMsg?.ToolCalls.Count ?? 0}");
        }

        finalResponse.Should().NotBeNull("response message should have text after tool execution completes");
        finalResponse!.Text.Should().NotBeNullOrEmpty();
        Output.WriteLine("8. Test PASSED");
    }

    /// <summary>
    /// THE critical test: submit message → orchestrator delegates to agent →
    /// parent response message shows delegation tool call with DelegationPath LIVE
    /// (without page reload). Then sub-thread streams text that appears on the
    /// sub-thread's response message. Tests the FULL real-world path.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Delegation_ParentShowsToolCall_SubThreadStreamsText_LiveUpdate()
    {
        var ct = new CancellationTokenSource(25.Seconds()).Token;
        var client = await GetClientAsync();
        var workspace = client.GetWorkspace();

        // 1. Create thread
        var createResp = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, "Delegation live test")),
            o => o.WithTarget(new Address(ContextPath)), ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);
        var threadPath = createResp.Message.Node!.Path!;
        Output.WriteLine($"1. Thread: {threadPath}");

        // 2. Submit message
        var submitResp = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Use the test tool please",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResp.Message.Success.Should().BeTrue(submitResp.Message.Error);
        Output.WriteLine("2. Message submitted");

        // 3. Wait for 2 messages (user + response)
        var msgIds = await workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                return (node?.Content as MeshThread)?.Messages ?? [];
            })
            .Where(ids => ids.Count >= 2)
            .Timeout(15.Seconds())
            .FirstAsync()
            .ToTask(ct);
        var responseMsgId = msgIds[1];
        var responsePath = $"{threadPath}/{responseMsgId}";
        Output.WriteLine($"3. Response message: {responseMsgId}");

        // 4. Subscribe to response message stream — wait for tool call with DelegationPath
        Output.WriteLine("4. Subscribing to response message stream for delegation tool call...");
        var responseStream = workspace.GetRemoteStream<MeshNode>(new Address(responsePath))!;
        var msgWithDelegation = await responseStream
            .Select(nodes =>
            {
                var msg = nodes?.FirstOrDefault(n => n.Path == responsePath)?.Content as ThreadMessage;
                if (msg != null)
                    Output.WriteLine($"  [STREAM] text={msg.Text?.Length ?? 0}ch, toolCalls={msg.ToolCalls.Count}, delegations={msg.ToolCalls.Count(c => !string.IsNullOrEmpty(c.DelegationPath))}");
                return msg;
            })
            .Where(m => m?.ToolCalls.Any(c => !string.IsNullOrEmpty(c.DelegationPath)) == true)
            .Timeout(20.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var delegation = msgWithDelegation!.ToolCalls.First(c => !string.IsNullOrEmpty(c.DelegationPath));
        Output.WriteLine($"5. DELEGATION APPEARED: {delegation.Name}, path={delegation.DelegationPath}");
        delegation.DelegationPath.Should().NotBeNullOrEmpty("delegation tool call must have DelegationPath set");

        // 5. Wait for parent execution to complete (text appears)
        Output.WriteLine("6. Waiting for parent to complete...");
        ThreadMessage? completed = null;
        for (var j = 0; j < 30; j++)
        {
            completed = await GetHubContentAsync<ThreadMessage>(client, responsePath, ct);
            if (!string.IsNullOrEmpty(completed?.Text)) break;
            await Task.Delay(500, ct);
        }

        completed!.Text.Should().NotBeNullOrEmpty("parent should have text after delegation completes");
        Output.WriteLine($"7. PARENT COMPLETE: text='{completed.Text}', toolCalls={completed.ToolCalls.Count}");
        Output.WriteLine("8. PASS — delegation with DelegationPath end-to-end");
    }

    /// <summary>
    /// Tests the EXACT path the layout area uses:
    /// 1. Post UpdateThreadMessageContent to response message hub
    /// 2. Layout area subscribes via GetStream(new MeshNodeReference())
    /// 3. Assert the layout data section gets updated with text
    /// This is what Blazor sees — the layout area stream, not the raw entity stream.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task LayoutArea_ReceivesUpdateThreadMessageContent_ViaLayoutStream()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();
        var workspace = client.GetWorkspace();

        // 1. Create thread + submit message
        var createResp = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, "Layout stream test")),
            o => o.WithTarget(new Address(ContextPath)), ct);
        var threadPath = createResp.Message.Node!.Path!;
        Output.WriteLine($"1. Thread: {threadPath}");

        var submitResp = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Test",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResp.Message.Success.Should().BeTrue();
        Output.WriteLine("2. Submitted");

        // 2. Wait for response message to appear
        var msgIds = await workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes => (nodes?.FirstOrDefault(n => n.Path == threadPath)?.Content as MeshThread)?.Messages ?? [])
            .Where(ids => ids.Count >= 2)
            .Timeout(15.Seconds()).FirstAsync().ToTask(ct);
        var responseMsgId = msgIds[1];
        var responsePath = $"{threadPath}/{responseMsgId}";
        Output.WriteLine($"3. Response: {responseMsgId}");

        // 3. Wait for execution to complete (text appears on raw entity stream)
        ThreadMessage? completed = null;
        for (var i = 0; i < 30; i++)
        {
            completed = await GetHubContentAsync<ThreadMessage>(client, responsePath, ct);
            if (!string.IsNullOrEmpty(completed?.Text)) break;
            await Task.Delay(500, ct);
        }
        completed.Should().NotBeNull();
        Output.WriteLine($"4. Execution done: text={completed!.Text?.Length ?? 0}");

        // 4. NOW subscribe to the LAYOUT AREA — this is what Blazor does
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(responsePath),
            new LayoutAreaReference(ThreadMessageNodeType.OverviewArea));

        var firstLayout = await layoutStream!
            .Where(ci => ci.Value.ValueKind == JsonValueKind.Object)
            .Timeout(10.Seconds()).FirstAsync().ToTask(ct);
        Output.WriteLine($"5. Layout rendered");

        // 5. Check the data section for the ThreadMessageViewModel
        var dataStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(responsePath),
            new LayoutAreaReference("data"));

        var data = await dataStream!
            .Where(ci => ci.Value.ValueKind == JsonValueKind.Object)
            .Timeout(10.Seconds()).FirstAsync().ToTask(ct);

        Output.WriteLine($"6. Data section: {data.Value}");

        // Does it have the msg view model with text?
        var hasMsg = data.Value.TryGetProperty("msg", out var msgVm);
        Output.WriteLine($"7. Has 'msg' key: {hasMsg}");
        if (hasMsg)
        {
            var hasText = msgVm.TryGetProperty("text", out var textProp);
            Output.WriteLine($"   Has 'text': {hasText}, value='{textProp}'");
            hasText.Should().BeTrue("data section should have msg.text from ThreadMessageViewModel");
            textProp.GetString().Should().NotBeNullOrEmpty("text should have content");
            Output.WriteLine("8. PASS — layout data section has text from UpdateThreadMessageContent");
        }
        else
        {
            // Dump all keys
            Output.WriteLine($"   Keys: [{string.Join(", ", data.Value.EnumerateObject().Select(p => p.Name))}]");
            Assert.Fail("Data section missing 'msg' key — SubscribeToDataStream not working");
        }
    }
}

/// <summary>
/// Fake chat client that calls delegate_to_agent, triggering REAL delegation.
/// The Orchestrator emits a delegate_to_agent function call.
/// FunctionInvokingChatClient intercepts it and calls the actual delegation tool.
/// The sub-thread runs with a simple fake that returns text.
/// </summary>
internal class DelegatingFakeChatClient : IChatClient
{
    private readonly string agentName;
    public DelegatingFakeChatClient(string agentName) => this.agentName = agentName;
    public ChatClientMetadata Metadata => new("DelegatingFake");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var hasFunctionResult = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();
        if (hasFunctionResult)
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Delegation complete.")));

        var call = new FunctionCallContent("del-1", "delegate_to_agent",
            new Dictionary<string, object?>
            {
                ["agentName"] = "Agent/Worker",
                ["task"] = "Do something simple",
            });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var hasFunctionResult = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();
        if (hasFunctionResult)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Delegation complete.");
            yield break;
        }

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("del-1", "delegate_to_agent",
                new Dictionary<string, object?>
                {
                    ["agentName"] = "Agent/Worker",
                    ["task"] = "Do something simple",
                })]
        };
        await Task.Delay(10, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IChatClient) ? this : null;
    public void Dispose() { }
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
    public bool IsPersistent => false;

    public ChatClientAgent CreateAgent(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        // Orchestrator delegates, Worker does simple text
        IChatClient client = config.Id == "Orchestrator"
            ? new DelegatingFakeChatClient(config.Id)
            : new ToolCallingFakeChatClient();

        var agent = new ChatClientAgent(chatClient: client,
            instructions: config.Instructions ?? "Test assistant.",
            name: config.Id, description: config.Description ?? config.Id,
            tools: [AIFunctionFactory.Create((string param) => $"Tool executed with {param}", "test_tool", "A test tool")],
            loggerFactory: null, services: null);

        // Wrap with function calling middleware — same as production ChatClientAgentFactory
        return agent.AsBuilder()
            .Use((AIAgent _, FunctionInvocationContext ctx,
                Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
                CancellationToken ct) =>
            {
                chat.ForwardToolCall?.Invoke(new ToolCallEntry
                {
                    Name = ctx.Function.Name,
                    DisplayName = ctx.Function.Name,
                    Arguments = ctx.Arguments?.Count > 0
                        ? string.Join(", ", ctx.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                        : null,
                    Timestamp = DateTime.UtcNow
                });
                return next(ctx, ct);
            })
            .Build() as ChatClientAgent ?? agent;
    }

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
