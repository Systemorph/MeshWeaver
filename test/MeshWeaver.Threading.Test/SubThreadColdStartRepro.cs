using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.AI.Delegation;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Repro + regression guard for the prod symptom "the sub-thread did not start"
/// (the 2026-07-15 <c>can-you-check-my-mail</c> thread): the parent's heartbeat
/// watcher (<see cref="DelegationHandlers"/>) used a SINGLE window for both
/// time-to-first-token and the inter-token gap, so a sub-agent still in first-token
/// latency (agent allocation + model TTFT + a slow first tool — an inbox/Graph call,
/// a reasoning model) was cancelled after the short cold-start grace, before it ever
/// emitted a delta. From the user it read as a sub-thread that "didn't look like
/// starting" — it started, sat silent during cold start, and got killed.
///
/// <para>The fix splits the windows: a sub-thread that has produced NO activity yet
/// (<c>LastActivityAt == null</c>) is judged by
/// <see cref="DelegationHeartbeatOptions.FirstActivityBudget"/>; only once it HAS
/// stamped an activity does the inter-activity
/// <see cref="DelegationHeartbeatOptions.HeartbeatTimeout"/> apply. Both are made
/// small here via a registered <see cref="DelegationHeartbeatOptions"/> so the two
/// branches are exercised in seconds, deterministically — no wall-clock guesswork.</para>
/// </summary>
public abstract class SubThreadHeartbeatTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected const string ContextPath = "User/TestUser";

    // Small, well-separated windows so both heartbeat branches fire in seconds.
    // FirstActivityBudget (5 s) ≫ HeartbeatTimeout (1 s): a sub-agent silent during
    // first-token latency for longer than the inter-activity timeout, but within the
    // first-activity budget, must survive — that IS the bug being pinned.
    protected static readonly DelegationHeartbeatOptions FastOptions = new()
    {
        HeartbeatTimeout = TimeSpan.FromSeconds(1),
        ColdStartGrace = TimeSpan.FromSeconds(1),
        FirstActivityBudget = TimeSpan.FromSeconds(5),
    };

    /// <summary>The delegation target's chat client — the behaviour under test.</summary>
    protected abstract IChatClient CreateSubAgent();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // A cancelled/stalled sub-thread leaves the framework's per-emission
            // DataChangeRequest → Observe callbacks pending until the RequestTimeout;
            // give teardown room so leak-detection doesn't trip on expected-pending work.
            .ConfigureHub(c => c.WithQuiesceTimeout(TimeSpan.FromSeconds(45)))
            .ConfigureServices(services =>
            {
                // Register the small heartbeat windows (resolved by the parent thread hub's
                // HandleHeartbeatTick) and the factory that routes default→parent, other→sub-agent.
                // The factory extends ChatClientAgentFactory (so the real delegate_to_agent tool
                // is wired) and needs the hub — resolved from the container at construction time.
                services.AddSingleton(FastOptions);
                services.AddSingleton<IChatClientFactory>(sp =>
                    new HeartbeatTestSubAgentFactory(sp.GetRequiredService<IMessageHub>(), CreateSubAgent));
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    protected async Task<string> CreateThread(IMessageHub client, string text)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "TestUser");
        var response = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).Should().Within(15.Seconds()).Emit();
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        return response.Message.Node!.Path!;
    }

    /// <summary>
    /// Waits for the parent's response cell to stamp a non-null <c>DelegationPath</c>
    /// on its delegate_to_agent tool call and returns that sub-thread path.
    /// </summary>
    protected static async Task<string> WaitForDelegationPath(IMessageHub client, string parentPath)
    {
        var workspace = client.GetWorkspace();
        var thread = await workspace.GetMeshNodeStream(parentPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(15.Seconds()).Match(t => t is { Messages.Count: >= 2 });
        var respPath = $"{parentPath}/{thread!.Messages[^1]}";
        var responseMsg = await workspace.GetMeshNodeStream(respPath)
            .Select(n => n.Content as ThreadMessage)
            .Should().Within(30.Seconds()).Match(m => m?.ToolCalls is { Count: > 0 }
                && m.ToolCalls[0].DelegationPath is { Length: > 0 });
        return responseMsg!.ToolCalls![0].DelegationPath!;
    }

    #region Harness — delegating parent + configurable sub-agent factory

    /// <summary>
    /// Parent: emits a single delegate_to_agent call on the first turn (no
    /// FunctionResultContent yet), then plain text once the tool result is present so
    /// the function-calling loop terminates. Target "Worker" is a built-in agent from
    /// <c>.AddAI()</c>, so the delegation proceeds to sub-thread creation.
    /// </summary>
    private sealed class DelegatingParentChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("DelegatingParent");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var hasToolResult = false;
            foreach (var msg in messages)
            foreach (var content in msg.Contents)
                if (content is FunctionResultContent) { hasToolResult = true; break; }

            if (!hasToolResult)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call1", "delegate_to_agent",
                        new Dictionary<string, object?>
                        {
                            ["agentName"] = "Worker",
                            ["task"] = "do the work"
                        })]);
                await Task.Yield();
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "Delegation returned — finishing.");
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    /// <summary>
    /// Routes the default agent to <see cref="DelegatingParentChatClient"/>; every
    /// other agent (the delegation target) gets the test-supplied sub-agent. Extends
    /// <see cref="ChatClientAgentFactory"/> so the framework wires the real
    /// <c>delegate_to_agent</c> tool; <paramref name="hub"/> is passed to the base only,
    /// <paramref name="subAgentFactory"/> is captured (no CS9107).
    /// </summary>
    private sealed class HeartbeatTestSubAgentFactory(IMessageHub hub, Func<IChatClient> subAgentFactory)
        : ChatClientAgentFactory(hub)
    {
        public override string Name => "HeartbeatTestSubAgentFactory";
        public override IReadOnlyList<string> Models => ["heartbeat-test-model"];
        public override int Order => 0;

        protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
            => agentConfig.IsDefault ? new DelegatingParentChatClient() : subAgentFactory();
    }

    #endregion
}

/// <summary>
/// THE bug repro: a sub-agent that is silent through first-token latency (longer than
/// the 1 s inter-activity timeout, but within the 5 s first-activity budget) then emits
/// and completes must NOT be cancelled by the heartbeat. Before the fix it was cancelled
/// during cold start and never produced anything.
/// </summary>
public class SubThreadColdStartRepro(ITestOutputHelper output) : SubThreadHeartbeatTestBase(output)
{
    private const string ColdStartDoneText = "cold start done";

    protected override IChatClient CreateSubAgent()
        => new SlowFirstTokenSubAgent(TimeSpan.FromSeconds(3), ColdStartDoneText);

    [Fact]
    public async Task SlowFirstToken_NotCancelledDuringColdStart_Completes()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var parentPath = await CreateThread(client, "delegate to a slow-starting worker");
        await workspace.GetMeshNodeStream(parentPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(10.Seconds()).Match(t => t != null);

        client.SubmitMessage(parentPath, "delegate this", contextPath: ContextPath);

        var subThreadPath = await WaitForDelegationPath(client, parentPath);
        Output.WriteLine($"Sub-thread: {subThreadPath}");

        // The sub-agent stays silent for 3 s (> the 1 s inter-activity timeout, < the
        // 5 s first-activity budget), then emits "cold start done" and completes. The
        // ONLY way the sub-thread reaches a completed assistant response carrying that
        // text is if the heartbeat did NOT cancel it during first-token latency —
        // exactly the behaviour the fix restores.
        var settled = await Observable.Defer(() =>
                workspace.GetMeshNodeStream(subThreadPath)
                    .Select(n => n.Content as MeshThread)
                    .Where(t => t is { IsExecuting: false, Messages.Count: >= 2 })
                    .Take(1))
            .Catch<MeshThread?, Exception>(_ =>
                Observable.Empty<MeshThread?>().Delay(200.Milliseconds()))
            .Repeat()
            .Should().Within(20.Seconds()).Emit();

        settled!.IsExecuting.Should().BeFalse();

        // The response cell must carry the sub-agent's post-cold-start output — proof it
        // ran to completion rather than being cancelled silent. Wait for the ACTUAL text
        // (not merely non-empty: the round opens the cell with a "Generating response..."
        // placeholder, which a wrongful cold-start cancel would leave in place forever).
        var responseId = settled.Messages[^1];
        var response = await workspace.GetMeshNodeStream($"{subThreadPath}/{responseId}")
            .Select(n => n.Content as ThreadMessage)
            .Should().Within(15.Seconds()).Match(m => m?.Text != null && m.Text.Contains(ColdStartDoneText),
                "the sub-agent emitted its first token only AFTER the cold-start window — a " +
                "single-window heartbeat would have cancelled it during first-token latency, " +
                "leaving the cell stuck on the 'Generating response...' placeholder.");
        Output.WriteLine($"Sub-thread completed with: '{response!.Text}'");
    }

    /// <summary>Silent through first-token latency, then emits + completes. Honors
    /// cancellation so a wrongful cold-start cancel settles the round without the text.</summary>
    private sealed class SlowFirstTokenSubAgent(TimeSpan firstTokenDelay, string doneText) : IChatClient
    {
        public ChatClientMetadata Metadata => new("SlowFirstTokenSubAgent");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, doneText)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // No delta yet → LastActivityAt stays null (the never-active window).
            await Task.Delay(firstTokenDelay, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, doneText);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }
}

/// <summary>
/// Regression guard for the OTHER branch: a sub-agent that emits a first token (stamps
/// LastActivityAt) and THEN goes silent forever is a genuine stall and MUST still be
/// cancelled by the inter-activity timeout. Ensures the cold-start fix did not disable
/// stall detection.
/// </summary>
public class SubThreadStallRepro(ITestOutputHelper output) : SubThreadHeartbeatTestBase(output)
{
    protected override IChatClient CreateSubAgent() => new StreamsThenStallsSubAgent();

    [Fact]
    public async Task StreamsThenStalls_IsCancelledByInterActivityTimeout()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var parentPath = await CreateThread(client, "delegate to a worker that stalls mid-stream");
        await workspace.GetMeshNodeStream(parentPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(10.Seconds()).Match(t => t != null);

        client.SubmitMessage(parentPath, "delegate this", contextPath: ContextPath);

        var subThreadPath = await WaitForDelegationPath(client, parentPath);

        // It emits one token (→ IsExecuting, LastActivityAt stamped) then hangs forever.
        await workspace.GetMeshNodeStream(subThreadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(15.Seconds()).Match(t => t is { IsExecuting: true });

        // The inter-activity timeout (1 s, past the 1 s grace) must fire and cancel it.
        var settled = await Observable.Defer(() =>
                workspace.GetMeshNodeStream(subThreadPath)
                    .Select(n => n.Content as MeshThread)
                    .Where(t => t is { IsExecuting: false })
                    .Take(1))
            .Catch<MeshThread?, Exception>(_ =>
                Observable.Empty<MeshThread?>().Delay(200.Milliseconds()))
            .Repeat()
            .Should().Within(20.Seconds()).Emit();

        settled!.IsExecuting.Should().BeFalse(
            "a sub-agent that streamed then went silent past the inter-activity timeout " +
            "must still be cancelled — the cold-start fix only relaxes the FIRST-token window.");
    }

    /// <summary>Emits one delta (stamps LastActivityAt) then blocks forever, honoring cancellation.</summary>
    private sealed class StreamsThenStallsSubAgent : IChatClient
    {
        public ChatClientMetadata Metadata => new("StreamsThenStallsSubAgent");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ContinueWith<ChatResponse>(_ => throw new TaskCanceledException(),
                    TaskContinuationOptions.OnlyOnCanceled);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "starting ");
            await Task.Yield();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break; // unreachable
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }
}
