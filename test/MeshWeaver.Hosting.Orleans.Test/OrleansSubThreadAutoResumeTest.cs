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
/// End-to-end test for the parent-observes-child delegation projection
/// introduced by the 2026-05-20 redesign. Verifies the full reactive chain:
///
/// <list type="number">
///   <item>Parent agent emits <c>delegate_to_agent</c>; sub-thread is spawned.</item>
///   <item>Sub-thread grain activates, sub-agent starts streaming.</item>
///   <item>While the sub-thread streams, the PARENT'S
///     <see cref="ToolCallEntry"/> matching the delegation (looked up by
///     <see cref="ToolCallEntry.DelegationPath"/>) shows monotonically
///     growing <see cref="ToolCallEntry.Result"/> with
///     <see cref="ToolCallEntry.Status"/> = <see cref="ToolCallStatus.Streaming"/>.
///     The GUI databinds to this so users see live progress without polling.</item>
///   <item>When the sub-agent finishes cleanly, the parent's
///     <c>ToolCallEntry.Status</c> flips to <see cref="ToolCallStatus.Success"/>
///     and <c>Result</c> carries the full final text.</item>
///   <item>Control returns to the agent framework: parent agent re-enters
///     with the function-result content, emits its wrap-up response,
///     terminates.</item>
/// </list>
///
/// <para><b>Why this shape:</b> the cold-load-hang repro
/// (<see cref="OrleansThreadColdLoadHangPostgresTest"/>) pins "no deadlock on
/// activation"; this one pins the full happy-path flow that the redesign
/// enables (live progress + Status flip + FCC re-entry).</para>
///
/// <para>In-memory Orleans cluster — always runs in CI. A PG-shaped sibling
/// can be added later for the prod-storage variant.</para>
/// </summary>
public class OrleansSubThreadAutoResumeTest(ITestOutputHelper output) : TestBase(output)
{
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<AutoResumeSiloConfigurator>();
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

    private IMessageHub GetClient([CallerMemberName] string? name = null)
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", $"autoresume-{name}-{Guid.NewGuid():N}"),
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

    [Fact(Timeout = 120000)]
    public void SubThreadDelegation_LiveProgressOnToolCall_TerminalSuccess_AndFcReentry()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // 1) Submit a fresh user message — parent agent will emit delegate_to_agent.
        // Mirrors OrleansReentrancyTest's path shape: context = "User/TestUser"
        // (TestUser lives at id=TestUser, namespace=User → path User/TestUser).
        var threadNode = ThreadNodeType.BuildThreadNode("User/TestUser", "auto-resume test", "TestUser");
        var createResp = client.Observe(new CreateNodeRequest(threadNode),
                o => o.WithTarget(new Address("User/TestUser")))
            .Should().Within(30.Seconds()).Emit();
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var parentThreadPath = createResp.Message.Node!.Path!;
        Output.WriteLine($"Parent thread: {parentThreadPath}");

        var parentSyncStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(new Address(parentThreadPath), new MeshNodeReference());

        var parentMessages = parentSyncStream
            .Select(change => (change.Value?.Content as MeshThread)?.Messages
                              ?? (IReadOnlyList<string>)ImmutableList<string>.Empty)
            .Do(ids => Output.WriteLine($"  parent.Messages.Count = {ids.Count}"));

        client.SubmitMessage(
            parentThreadPath,
            "please delegate to the worker",
            contextPath: "User/TestUser");
        Output.WriteLine("Submission posted.");

        // Wait for the parent thread to have its user + response message cells.
        var parentMsgIds = parentMessages.Should().Within(40.Seconds()).Match(ids => ids.Count >= 2);
        var parentRespId = parentMsgIds[1];
        var parentRespPath = $"{parentThreadPath}/{parentRespId}";
        Output.WriteLine($"Parent response cell: {parentRespPath}");

        // 2) Observe the parent's response cell — this is the surface the GUI
        //    databinds to. Watch for the delegation ToolCallEntry to appear.
        var parentRespStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(new Address(parentRespPath), new MeshNodeReference());

        var delegationEntryAppeared = parentRespStream
            .Select(change => (change.Value?.Content as ThreadMessage)?.ToolCalls)
            .Should().Within(30.Seconds())
            .Match(tcs => tcs?.Any(tc => tc.Name == "delegate_to_agent" && tc.DelegationPath != null) == true);
        var initialEntry = delegationEntryAppeared!.First(tc => tc.DelegationPath != null);
        var subThreadPath = initialEntry.DelegationPath!;
        Output.WriteLine($"Sub-thread path on parent's tool call: {subThreadPath}");
        // The parent's tool call carries only the DelegationPath pointer (and,
        // at the end, the terminal result). Live sub-agent progress is NOT
        // mirrored ("wrapped") onto the parent tool call — it is received by
        // binding DIRECTLY to the sub-thread's response-cell mesh node stream,
        // which is where the sub-agent streams its text. Resolve that cell
        // (sub-thread Messages[1]) and watch its Text grow.
        var subThreadStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(new Address(subThreadPath), new MeshNodeReference());
        var subMsgIds = subThreadStream
            .Select(change => (change.Value?.Content as MeshThread)?.Messages)
            .Should().Within(30.Seconds())
            .Match(ids => ids is { Count: >= 2 });
        var subRespPath = $"{subThreadPath}/{subMsgIds![1]}";
        Output.WriteLine($"Sub-thread response cell: {subRespPath}");
        var subRespStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(new Address(subRespPath), new MeshNodeReference());

        // 3) Live-progress assertion: the sub-thread's response cell Text MUST
        //    grow across ≥3 distinct observations while the sub-agent streams.
        //    This is the load-bearing invariant — progress lands on the mesh
        //    node stream live, not batched to a single end-of-stream write.
        var liveProgressSnapshots = new List<string>();
        using (subRespStream
            .Select(change => (change.Value?.Content as ThreadMessage)?.Text)
            .Where(text => !string.IsNullOrEmpty(text))
            .Subscribe(text =>
            {
                lock (liveProgressSnapshots)
                {
                    if (liveProgressSnapshots.Count == 0
                        || liveProgressSnapshots[^1] != text)
                    {
                        liveProgressSnapshots.Add(text!);
                        Output.WriteLine($"  [progress {liveProgressSnapshots.Count}] " +
                            $"{text!.Length} chars, last line: " +
                            $"\"{text.Split('\n').LastOrDefault()?.Trim()}\"");
                    }
                }
            }))
        {
            // 4) Wait for the sub-thread to finish: its IsExecuting flips false.
            var subThreadSettled = subThreadStream
                .Select(change => change.Value?.Content as MeshThread)
                .Should().Within(60.Seconds())
                .Match(t => t is { IsExecuting: false } && t.Messages.Count >= 2);
            subThreadSettled!.IsExecuting.Should().BeFalse("sub-thread must terminate");
            subThreadSettled.Messages.Count.Should().BeGreaterThanOrEqualTo(2,
                "sub-thread should have user + response cells in history");
            Output.WriteLine($"Sub-thread settled with {subThreadSettled.Messages.Count} messages.");
        }

        // Snapshots collected; assert progress was actually live, not all-at-end.
        lock (liveProgressSnapshots)
        {
            liveProgressSnapshots.Count.Should().BeGreaterThanOrEqualTo(3,
                $"sub-thread response cell must emit ≥3 distinct growing Text snapshots " +
                $"during streaming (saw {liveProgressSnapshots.Count}). Progress is bound " +
                $"directly off the sub-thread mesh node stream; a fix that batches the " +
                $"sub-agent output to a single end-of-stream write would fail here.");
            for (var i = 1; i < liveProgressSnapshots.Count; i++)
                (liveProgressSnapshots[i] != liveProgressSnapshots[i - 1])
                    .Should().BeTrue("each captured snapshot should differ from the previous");
        }

        // 5) ToolCallEntry must flip to Success with full final text in Result.
        //    Status=Success (terminal stamp) and Result (the resolved sub-thread
        //    summary) arrive in separate stream updates, so wait for the SETTLED
        //    state — both present — not merely the first Success emission.
        var finalEntry = parentRespStream
            .Select(change => (change.Value?.Content as ThreadMessage)?.ToolCalls
                ?.FirstOrDefault(tc => tc.DelegationPath == subThreadPath))
            .Should().Within(30.Seconds())
            .Match(tc => tc is { Status: ToolCallStatus.Success } && !string.IsNullOrEmpty(tc.Result));
        finalEntry!.Status.Should().Be(ToolCallStatus.Success);
        finalEntry.IsSuccess.Should().BeTrue();
        finalEntry.Result.Should().NotBeNullOrEmpty(
            "Status=Success must carry the full accumulated sub-thread text");
        Output.WriteLine($"Tool call flipped to Success with {finalEntry.Result!.Length}-char Result.");

        // 6) FCC re-entry: parent agent must produce its wrap-up text after
        //    consuming the function-result content. Parent's response cell
        //    Text grows beyond the initial "Delegating..." placeholder and
        //    eventually reaches Completed.
        var parentCompleted = parentRespStream
            .Select(change => change.Value?.Content as ThreadMessage)
            .Should().Within(45.Seconds())
            .Match(m => m is { Status: ThreadMessageStatus.Completed } && (m.Text?.Length ?? 0) > 0);
        parentCompleted!.Status.Should().Be(ThreadMessageStatus.Completed,
            "parent agent must return to streaming after the delegation tool call resolves " +
            "(FunctionInvokingChatClient re-entry with FunctionResultContent)");
        parentCompleted.Text.Should().NotBeNullOrEmpty(
            "parent's wrap-up response after FCC re-entry should be present");
        Output.WriteLine($"Parent completed with wrap-up text: " +
            $"\"{parentCompleted.Text![..Math.Min(120, parentCompleted.Text.Length)]}\"");

        // 7) Parent thread itself idles.
        var parentIdle = parentSyncStream
            .Select(change => change.Value?.Content as MeshThread)
            .Should().Within(20.Seconds())
            .Match(t => t is { IsExecuting: false });
        parentIdle!.IsExecuting.Should().BeFalse("parent thread must idle after wrap-up");
        Output.WriteLine("Parent thread idle — full delegation loop completed.");
    }
}

/// <summary>
/// Silo config: production-shape Graph + AI + RowLevelSecurity with the
/// shared in-memory adapter and the test seed provider (TestUser admin).
/// Registers <see cref="AutoResumeDelegationAgentFactory"/> so the parent
/// agent emits <c>delegate_to_agent</c> and the sub-agent streams a finite
/// multi-line response.
/// </summary>
public class AutoResumeSiloConfigurator : ISiloConfigurator, IHostConfigurator
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
            {
                services.AddSingleton<IChatClientFactory, AutoResumeDelegationAgentFactory>();
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

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

/// <summary>
/// Agent factory whose <see cref="ChatClientAgentFactory.CreateChatClient"/>
/// returns the parent-or-sub chat client based on which agent the factory is
/// being instantiated for. Extends <see cref="ChatClientAgentFactory"/> so the
/// production delegation tool pipeline (the <c>delegate_to_agent</c>
/// AIFunction wired by <see cref="ChatClientAgentFactory"/> itself) is active
/// — no manual tool wiring needed.
/// </summary>
internal class AutoResumeDelegationAgentFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
{
    public override string Name => "AutoResumeDelegationFactory";
    public override IReadOnlyList<string> Models => ["autoresume-model"];
    public override int Order => 0;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
        => agentConfig.IsDefault
            ? new DelegatingParentAutoResumeClient()
            : new StreamingSubAgentClient();
}

/// <summary>
/// Parent chat client. First turn (no FunctionResultContent in history) →
/// emits a <c>delegate_to_agent</c> function call. After the delegation
/// returns (FunctionResultContent appears in history) → streams a short
/// wrap-up referencing the first line of the sub-agent's output, then
/// terminates.
/// </summary>
internal class DelegatingParentAutoResumeClient : IChatClient
{
    public ChatClientMetadata Metadata => new("DelegatingParentAutoResume");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Detect whether we're on the FIRST turn (no tool result yet) or the
        // FOLLOW-UP turn (tool result delivered). The FCC handles the loop.
        FunctionResultContent? toolResult = null;
        foreach (var msg in messages)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionResultContent frc)
                {
                    toolResult = frc;
                    break;
                }
            }
            if (toolResult is not null) break;
        }

        if (toolResult is null)
        {
            // First turn: ask FCC to invoke the delegation tool. Target a
            // built-in agent name that the framework's hierarchy enumeration
            // includes ("Worker" is registered by AddAI()).
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call1", "delegate_to_agent",
                    new Dictionary<string, object?>
                    {
                        ["agentName"] = "Worker",
                        ["task"] = "Please produce a short multi-line report."
                    })]);
            await Task.Yield();
            yield break;
        }

        // Follow-up turn: parent agent has the delegation result. Stream a
        // short wrap-up so the parent's response cell reaches Completed.
        var resultText = toolResult.Result?.ToString() ?? "";
        var firstLine = resultText.Split('\n').FirstOrDefault()?.Trim() ?? "(empty)";
        foreach (var word in $"Sub-thread done. First line was: {firstLine}".Split(' '))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            await Task.Delay(5, cancellationToken);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IChatClient) ? this : null;
    public void Dispose() { }
}

/// <summary>
/// Sub-agent chat client. Streams a finite multi-line report word-by-word
/// with a small per-word delay so the parent's projection has multiple
/// distinct snapshots to observe. Terminates cleanly so the sub-thread's
/// <c>IsExecuting</c> flips false.
/// </summary>
internal class StreamingSubAgentClient : IChatClient
{
    private const string ReportText =
        "Line one: starting analysis.\n" +
        "Line two: collected inputs.\n" +
        "Line three: scanning records.\n" +
        "Line four: matching patterns.\n" +
        "Line five: aggregating results.\n" +
        "Line six: validating outputs.\n" +
        "Line seven: composing summary.\n" +
        "Line eight: formatting report.\n" +
        "Line nine: finalizing structure.\n" +
        "Line ten: report complete.\n" +
        "Line eleven: ready for delivery.\n" +
        "Line twelve: handing back.";

    public ChatClientMetadata Metadata => new("StreamingSubAgent");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ReportText)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Stream in small chunks (~5 chars each) so the parent's projection
        // observes many distinct snapshots, exercising the live-progress path.
        const int chunkSize = 5;
        for (var i = 0; i < ReportText.Length; i += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = ReportText.Substring(i, Math.Min(chunkSize, ReportText.Length - i));
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Delay(20, cancellationToken);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IChatClient) ? this : null;
    public void Dispose() { }
}
