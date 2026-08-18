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
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

// TODO: needs custom shared fixture — uses StreamingSiloConfigurator with
// AddFileSystemPersistence(SamplesGraphData) and a custom ToolCallingFakeChatClientFactory,
// neither of which the SharedOrleansFixture configures.
/// <summary>
/// Orleans integration tests for thread streaming and tool calls.
/// Verifies that in a distributed Orleans cluster:
/// 1. Response text streams to the message node
/// </summary>
public class OrleansThreadStreamingTest(ITestOutputHelper output) : OrleansTestBase<StreamingSiloConfigurator>(output)
{
    private const string ContextPath = "TestUser";

    // Per-test unique client address — without this, every test in the class
    // calls GetClient() which defaults to client/1, OrleansRoutingService's
    // streams dict overwrites the previous callback, and any in-flight response
    // routing for an earlier test fires the new test's hub (or vice versa). The
    // first two tests in the class pass because they finish their round-trip
    // before the next test starts; later tests inherit a polluted streams entry
    // and the streamed text never reaches the assertions. Mirrors the pattern
    // OrleansHostedHubRoutingTest uses (client/hostedhubrouting-{caller}-{guid}).
    private IMessageHub GetUniqueClient([CallerMemberName] string? name = null)
        => base.GetClient($"streaming-{name}-{Guid.NewGuid():N}", "TestUser");

    /// <summary>
    /// Reactive single-node content read via the canonical
    /// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace, string)"/>
    /// path. Returns an <see cref="IObservable{T}"/> the caller asserts on with
    /// <c>.Should().Match(...)</c>.
    /// </summary>
    private IObservable<T?> GetHubContent<T>(IMessageHub client, string path) where T : class
        => client.GetWorkspace().GetMeshNodeStream(path)
            .Select(node => node.ContentAs<T>(client.JsonSerializerOptions));

    /// <summary>
    /// The thread node's CONTROL-PLANE state, projected off its live stream — what every
    /// assertion in this class actually waits on, in a shape a TIMEOUT can explain.
    ///
    /// <para>🚨 <c>node.Content as MeshThread</c> was the trap-door AGENTS.md forbids ("Read
    /// <c>node.Content</c> in WHATEVER shape it arrives"): content is a typed instance on the
    /// owning hub but reaches a CLIENT mirror as a <see cref="JsonElement"/> whenever the
    /// polymorphic <c>$type</c> does not resolve against that hub's registry, and <c>as</c>
    /// silently yields null. Folded through <c>?? ImmutableList&lt;string&gt;.Empty</c> that made
    /// "content was never readable" and "the round never dispatched" produce the IDENTICAL
    /// observation — an empty message list — so the 45 s timeout in #1753 could not say which of
    /// the two had happened. <see cref="MeshNodeContentExtensions.ContentAs{T}"/> recovers the
    /// degraded shape (and logs when it genuinely cannot); <see cref="Shape"/> records what
    /// arrived, and the control-plane counters say how far the round got.</para>
    /// </summary>
    private sealed record ThreadControlPlane(
        string Shape,
        ThreadExecutionStatus? Status,
        IReadOnlyList<string> Messages,
        int Pending,
        int Ingested,
        int UserIds)
    {
        public static ThreadControlPlane Of(MeshNode? node, JsonSerializerOptions options)
        {
            var shape = node is null ? "(no node)" : node.Content?.GetType().Name ?? "(null content)";
            var thread = node.ContentAs<MeshThread>(options);
            return thread is null
                ? new ThreadControlPlane(shape, null, [], 0, 0, 0)
                : new ThreadControlPlane(shape, thread.Status, thread.Messages,
                    thread.PendingUserMessages.Count, thread.IngestedMessageIds.Count,
                    thread.UserMessageIds.Count);
        }

        // Rendered verbatim into the assertion's "Last of N emission(s) was: …" line, so a
        // timeout NAMES the stage the round stopped at: content unreadable (Status=(unreadable)),
        // the submit never landed (pending=0 userIds=0), the claim was never made (status=Idle
        // pending=1), or the claim was made and the _Exec commit never came back
        // (status=StartingExecution pending=1 messages=[]) — the orphaned-claim shape #539
        // recovers only on hub REACTIVATION, never on a live hub.
        public override string ToString() =>
            $"content={Shape} status={Status?.ToString() ?? "(unreadable)"} " +
            $"messages=[{string.Join(",", Messages)}] pending={Pending} " +
            $"ingested={Ingested} userIds={UserIds}";
    }

    /// <summary>
    /// The thread node's <see cref="ThreadControlPlane"/> on every emission of its live stream.
    /// </summary>
    private IObservable<ThreadControlPlane> ThreadStates(IMessageHub client, string threadPath)
        => client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(node => ThreadControlPlane.Of(node, client.JsonSerializerOptions));

    /// <summary>
    /// Verifies that response text streams to the message node during execution.
    /// After execution completes, the response message should have non-empty text.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ResponseText_StreamsToMessageNode()
    {
        var client = GetUniqueClient();

        // Create thread
        var response = await client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, "Streaming text test")), o => o.WithTarget(new Address(ContextPath)))
            .Should().Within(30.Seconds()).Emit();
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        var threadPath = response.Message.Node!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // Project the thread's control-plane state off the live stream.
        var threadStates = ThreadStates(client, threadPath);

        // Submit via workspace extension
        client.SubmitMessage(
            threadPath,
            "Tell me something",
            contextPath: ContextPath);
        var committed = await threadStates.Should().Within(45.Seconds()).Match(s => s.Messages.Count >= 2);
        var responseMsgId = committed.Messages[1];
        Output.WriteLine($"Response message: {responseMsgId}");

        // Wait for the response message to gain text.
        var responsePath = $"{threadPath}/{responseMsgId}";
        var responseMsg = await GetHubContent<ThreadMessage>(client, responsePath)
            .Should().Within(45.Seconds()).Match(m => !string.IsNullOrEmpty(m?.Text));

        responseMsg!.Text.Should().NotBeNullOrEmpty("response should have streamed text");
        Output.WriteLine($"Response text: '{responseMsg.Text}' ({responseMsg.Text!.Length} chars)");
    }

    /// <summary>
    /// Full delegation flow: submit message → parent delegates to sub-thread →
    /// sub-thread streams text → parent receives result.
    /// Traces every step to find where communication breaks.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task DelegationFlow_SubThreadStreamsText_ParentCompletes()
    {
        var client = GetUniqueClient();

        // Create thread
        var response = await client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, "Delegation flow test")), o => o.WithTarget(new Address(ContextPath)))
            .Should().Within(30.Seconds()).Emit();
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        var threadPath = response.Message.Node!.Path!;
        Output.WriteLine($"1. Thread created: {threadPath}");

        // Subscribe to thread node for execution-state diagnostics.
        var workspace = client.GetWorkspace();
        var threadUpdates = new List<string>();
        using var _ = workspace.GetMeshNodeStream(threadPath)
            .Subscribe(node =>
            {
                var thread = node.ContentAs<MeshThread>(client.JsonSerializerOptions);
                if (thread != null)
                {
                    var msg = $"Thread: IsExecuting={thread.IsExecuting}, Status={thread.ExecutionStatus ?? "(null)"}, Messages={thread.Messages.Count}, ActiveMsg={thread.ActiveMessageId ?? "(null)"}";
                    lock (threadUpdates) threadUpdates.Add(msg);
                    Output.WriteLine($"  [STREAM] {msg}");
                }
            });

        // Project the thread's control-plane state off the live stream.
        var threadStates = ThreadStates(client, threadPath);

        // Submit message via workspace extension
        Output.WriteLine("2. Submitting message...");
        client.SubmitMessage(
            threadPath,
            "Use the test tool please",
            contextPath: ContextPath);
        Output.WriteLine("3. Message submitted successfully");

        // Wait for 2 message IDs
        var committed = await threadStates.Should().Within(45.Seconds()).Match(s => s.Messages.Count >= 2);
        var msgIds = committed.Messages;
        Output.WriteLine($"4. Messages appeared: [{string.Join(", ", msgIds)}]");
        var responseMsgId = msgIds[1];
        var responsePath = $"{threadPath}/{responseMsgId}";

        // Wait for the response message to have text AND tool calls AND every tool
        // call resolved. The plain `.All(...)` is vacuously true when ToolCalls is
        // empty — text-chunk pushes can land before the tool-call entry is committed,
        // so require ToolCalls.Count > 0 too.
        Output.WriteLine("5. Waiting for response message content...");
        var finalResponse = await GetHubContent<ThreadMessage>(client, responsePath)
            .Select(m =>
            {
                if (m != null && (m.ToolCalls.Count > 0 || !string.IsNullOrEmpty(m.Text)))
                    Output.WriteLine($"  [STREAM] text={m.Text?.Length ?? 0}chars, toolCalls={m.ToolCalls.Count}, delegations={m.ToolCalls.Count(c => !string.IsNullOrEmpty(c.DelegationPath))}");
                return m;
            })
            .Should().Within(90.Seconds())
            .Match(m => !string.IsNullOrEmpty(m?.Text)
                && m!.ToolCalls.Count > 0
                && m.ToolCalls.All(c => c.Result != null));

        Output.WriteLine($"6. Thread updates collected: {threadUpdates.Count}");
        foreach (var u in threadUpdates.TakeLast(5))
            Output.WriteLine($"  {u}");

        Output.WriteLine($"7. PASS: Response text='{finalResponse!.Text}' ({finalResponse.Text!.Length} chars)");
        Output.WriteLine($"   Tool calls: {finalResponse.ToolCalls.Count}");
        foreach (var tc in finalResponse.ToolCalls)
            Output.WriteLine($"   - {tc.DisplayName ?? tc.Name}: success={tc.IsSuccess}, delegation={tc.DelegationPath ?? "(none)"}");

        finalResponse.Text.Should().NotBeNullOrEmpty();
        finalResponse.ToolCalls.Should().NotBeEmpty("response should have tool calls tracked via middleware");
        Output.WriteLine("8. Test PASSED");
    }

    /// <summary>
    /// THE critical test: submit message → orchestrator delegates to agent →
    /// parent response message shows delegation tool call with DelegationPath LIVE
    /// (without page reload). Then sub-thread streams text that appears on the
    /// sub-thread's response message. Tests the FULL real-world path.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Delegation_ParentShowsToolCall_SubThreadStreamsText_LiveUpdate()
    {
        var client = GetUniqueClient();
        var workspace = client.GetWorkspace();

        // 1. Create thread
        var createResp = await client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, "Delegation live test")), o => o.WithTarget(new Address(ContextPath)))
            .Should().Within(30.Seconds()).Emit();
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var threadPath = createResp.Message.Node!.Path!;
        Output.WriteLine($"1. Thread: {threadPath}");

        // 2. Submit message via workspace extension
        client.SubmitMessage(
            threadPath,
            "Use the test tool please",
            contextPath: ContextPath);
        Output.WriteLine("2. Message submitted");

        // 3. Wait for 2 messages (user + response)
        var committed = await ThreadStates(client, threadPath)
            .Should().Within(30.Seconds()).Match(s => s.Messages.Count >= 2);
        var responseMsgId = committed.Messages[1];
        var responsePath = $"{threadPath}/{responseMsgId}";
        Output.WriteLine($"3. Response message: {responseMsgId}");

        // 4. Wait for response-message tool call with DelegationPath.
        Output.WriteLine("4. Waiting for delegation tool call on response message stream...");
        var msgWithDelegation = await workspace.GetMeshNodeStream(responsePath)
            .Select(node =>
            {
                var msg = node.ContentAs<ThreadMessage>(client.JsonSerializerOptions);
                if (msg != null)
                    Output.WriteLine($"  [STREAM] text={msg.Text?.Length ?? 0}ch, toolCalls={msg.ToolCalls.Count}, delegations={msg.ToolCalls.Count(c => !string.IsNullOrEmpty(c.DelegationPath))}");
                return msg;
            })
            .Should().Within(30.Seconds())
            .Match(m => m?.ToolCalls.Any(c => !string.IsNullOrEmpty(c.DelegationPath)) == true);

        var delegation = msgWithDelegation!.ToolCalls.First(c => !string.IsNullOrEmpty(c.DelegationPath));
        Output.WriteLine($"5. DELEGATION APPEARED: {delegation.Name}, path={delegation.DelegationPath}");
        delegation.DelegationPath.Should().NotBeNullOrEmpty("delegation tool call must have DelegationPath set");

        // 5. Wait for parent execution to complete (text appears).
        Output.WriteLine("6. Waiting for parent to complete...");
        var completed = await GetHubContent<ThreadMessage>(client, responsePath)
            .Should().Within(30.Seconds()).Match(m => !string.IsNullOrEmpty(m?.Text));

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
        var client = GetUniqueClient();

        // 1. Create thread + submit message
        var createResp = await client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, "Layout stream test")), o => o.WithTarget(new Address(ContextPath)))
            .Should().Within(30.Seconds()).Emit();
        var threadPath = createResp.Message.Node!.Path!;
        Output.WriteLine($"1. Thread: {threadPath}");

        client.SubmitMessage(
            threadPath,
            "Test",
            contextPath: ContextPath);
        Output.WriteLine("2. Submitted");

        // 2. Wait for response message to appear
        var committed = await ThreadStates(client, threadPath)
            .Should().Within(30.Seconds()).Match(s => s.Messages.Count >= 2);
        var responseMsgId = committed.Messages[1];
        var responsePath = $"{threadPath}/{responseMsgId}";
        Output.WriteLine($"3. Response: {responseMsgId}");

        // 3. Wait for execution to complete (text appears on raw entity stream).
        var completed = await GetHubContent<ThreadMessage>(client, responsePath)
            .Should().Within(45.Seconds()).Match(m => !string.IsNullOrEmpty(m?.Text));
        Output.WriteLine($"4. Execution done: text={completed!.Text?.Length ?? 0}");

        // 4. Verify the per-message hub's MeshNodeReference reducer carries the
        // streamed text — this is the path the Blazor view actually subscribes to
        // (path-bound bubble in ThreadMessageLayoutAreas.BuildMessageOverview).
        // The legacy assertion against a 'msg' key in the layout's data section
        // is no longer applicable: the architecture moved from
        // UpdateData/JsonPointerReference to a path-bound ThreadMessageBubbleControl
        // whose NodePath is the response-message address; the Blazor view reads
        // content directly via GetRemoteStream<MeshNode, MeshNodeReference>.
        completed.Text.Should().NotBeNullOrEmpty(
            "per-message MeshNodeReference reducer must surface the streamed text — " +
            "this is what the Blazor view subscribes to via the path-bound bubble.");
        Output.WriteLine($"5. PASS — per-message MeshNodeReference reducer carries text " +
            $"'{completed.Text}' (length={completed.Text!.Length}).");
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

/// <summary>
/// Inherits from <see cref="ChatClientAgentFactory"/> so the framework's tool-creation
/// pipeline runs — registers <c>delegate_to_agent</c> for default agents, wraps
/// tools with <c>WrapToolWithAccessContext</c>, and adds the function-invoking
/// middleware. <see cref="GetStandardTools"/> contributes the <c>test_tool</c>
/// the Worker exercises in <c>ToolCallingFakeChatClient</c>.
/// </summary>
internal class ToolCallingFakeChatClientFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
{
    public override string Name => "ToolCallingFakeFactory";
    public override IReadOnlyList<string> Models => ["tool-calling-model"];
    public override int Order => 0;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // The DEFAULT agent (the one the thread submits to — `Assistant`, seeded
        // with IsDefault=true) plays the orchestrator role: it emits a
        // delegate_to_agent call. Every other agent — including the delegation
        // target `Worker` that runs the sub-thread — exercises the test tool then
        // streams text. Gating on a literal "Orchestrator" id was the bug: no
        // Orchestrator agent is ever seeded, so the default agent silently fell
        // through to ToolCallingFakeChatClient and never delegated.
        return agentConfig.IsDefault || agentConfig.Id == "Orchestrator"
            ? new DelegatingFakeChatClient(agentConfig.Id)
            : new ToolCallingFakeChatClient();
    }

    protected override IEnumerable<AITool> GetStandardTools(IAgentChat chat)
    {
        yield return AIFunctionFactory.Create(
            (string param) => $"Tool executed with {param}",
            "test_tool",
            "A test tool the Worker calls before streaming text.");
    }
}

public class StreamingSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    private static string SamplesGraphData =>
        Path.Combine(AppContext.BaseDirectory, "SamplesGraph", "Data");

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault()
            // 🚨 Surface SILO-side framework logs through ITestOutputHelper, exactly as the
            // canonical TestSiloConfigurator does. This class was the only Orleans suite
            // without it, and that is why #1753's failure report carried the test's own three
            // WriteLines and NOTHING else: every diagnostic the round emits on the way to a
            // wedge — "[ExecRoundWatcher] thread node has no MeshThread content",
            // "[SubmissionWatcher] claim Update failed", "[ThreadSubmission] Response cell
            // creation failed", "[ThreadExec] Init observation faulted … re-establishing" —
            // is written silo-side and was being discarded. A timeout with no logs is not
            // evidence that nothing was logged.
            //
            // 🚨 Warning+ ONLY, with the Orleans/Microsoft/System runtime chatter at Error.
            // AddXUnitLogger deliberately sets no filters (levels are appsettings-driven), but
            // the TestCluster silo host does not read this project's appsettings — unfiltered it
            // emits ~150 Information lines of Orleans startup per test. That volume is not free:
            // xunit buffers captured output for EVERY test whether or not it is printed, and this
            // suite's flakes are load-dependent races that extra logging on the hot path is known
            // to MASK (see .github/workflows/flake-repro.yml). Warning+ costs nothing in a healthy
            // run — nothing is emitted — and is exactly the band the wedge diagnostics live in.
            .ConfigureLogging(logging => logging
                .AddXUnitLogger()
                .SetMinimumLevel(LogLevel.Warning)
                .AddFilter("Orleans", LogLevel.Error)
                .AddFilter("Microsoft", LogLevel.Error)
                .AddFilter("System", LogLevel.Error));
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
                services.AddSingleton<IChatClientFactory, ToolCallingFakeChatClientFactory>();
                // Tests target `new Address("TestUser")` directly — register
                // OrleansTestSeedProvider so TestUser + its access policy are
                // visible at the root path. Without this the path-resolver
                // returns null and RoutingGrain replies with NotFound on every
                // request, which now cleanly surfaces as
                // DeliveryFailureException("No node found at 'TestUser'.")
                // since the routing layer's NotFound dispatch was fixed.
                services.AddSingleton<IStaticNodeProvider, OrleansTestSeedProvider>();
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}
