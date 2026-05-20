using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Repro for the prod sub-thread deadlock observed at
/// <c>Systemorph/_Thread/add-markus-kleiner-as-admin-to-systemorp-c578/8721bdff/create-a-new-accessassignment-node-to-gr-a618</c>
/// on 2026-05-20.
///
/// Shape: parent agent delegates → sub-thread is created with <c>IsExecuting=true</c>
/// + a pending user message → sub-thread's hub activates and starts executing →
/// the sub-thread's <see cref="IChatClient"/> hangs (no streaming, no completion) →
/// the parent's 5-minute watchdog in
/// <see cref="ChatClientAgentFactory"/>'s delegation drain eventually closes the
/// PARENT's <c>IAsyncEnumerable</c>, but the <b>sub-thread itself</b> has no
/// self-cancellation and no propagated cancel — so its <c>IsExecuting</c> flag
/// stays <c>true</c> forever. The user sees a perpetually-"executing" sub-thread.
///
/// The existing cancel path (<see cref="MeshThread.RequestedCancellationAt"/> on the
/// parent → propagated to every active sub-thread via
/// <c>ThreadExecution</c>'s cancel watcher) only fires when the user clicks Stop.
/// Without that intervention, the sub-thread hangs.
///
/// <para>This test pins both shapes:</para>
/// <list type="number">
///   <item><c>HungSubThread_WithoutUserCancel_StaysExecuting</c> — documents the BUG:
///   after the sub-thread starts and its agent stalls, <c>IsExecuting</c> never
///   flips back to false on its own within a reasonable window.</item>
///   <item><c>HungSubThread_UserCancelOnParent_PropagatesAndStopsSubThread</c> —
///   confirms the EXISTING fallback works: user-initiated cancel on the parent
///   does propagate and unwind the hung sub-thread.</item>
/// </list>
///
/// <para>Fix shape (when applied): the parent's delegation drain
/// (<see cref="ChatClientAgentFactory"/>'s <c>ExecuteDelegationAsync</c>) must,
/// on watchdog/cancel, flip <c>RequestedCancellationAt</c> on the sub-thread —
/// the same write the user's Stop button performs. After the fix, the first
/// test becomes a real assertion (sub-thread settles even without user cancel)
/// and the second remains a regression guard for the manual-cancel path.</para>
/// </summary>
public class SubThreadHangRepro(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory, HangingSubAgentFactory>();
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// REPRO of the prod deadlock. Parent agent emits a delegate_to_agent call;
    /// sub-thread is created with <c>IsExecuting=true</c>; sub-thread's chat
    /// client hangs forever. We wait until <c>IsExecuting=true</c> is observed
    /// on the sub-thread (i.e. it started), then wait
    /// <see cref="ObservationWindow"/> seconds and assert <c>IsExecuting</c> is
    /// STILL true — the bug shape: sub-thread has no self-recovery, no parent
    /// watchdog propagation. In prod this stays true for hours.
    ///
    /// <para>When the fix lands (parent watchdog flips
    /// <c>RequestedCancellationAt</c> on the sub-thread, or sub-thread gets its
    /// own no-progress watchdog), invert this assertion to
    /// <c>Should().BeFalse</c> with a generous window — that's the regression
    /// guard.</para>
    /// </summary>
    [Fact]
    public async Task HungSubThread_WithoutUserCancel_StaysExecuting()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var parentPath = await CreateThreadAsync(client, "Delegate to a worker that hangs", ct);
        Output.WriteLine($"Parent thread: {parentPath}");

        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = parentPath,
            UserText = "delegate this",
            ContextPath = ContextPath,
        });

        // Wait for the parent's response message to materialise and the
        // delegation tool call to be stamped with a DelegationPath. That path
        // IS the sub-thread we'll observe.
        var subThreadPath = await WaitForDelegationPath(client, parentPath, ct);
        Output.WriteLine($"Sub-thread: {subThreadPath}");

        // Wait for the sub-thread to reach IsExecuting=true — proves the
        // sub-thread hub activated and its WatchForExecution started the
        // agent. The hanging IChatClient takes over from here.
        var subThread = await workspace.GetMeshNodeStream(subThreadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { IsExecuting: true })
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);
        subThread!.IsExecuting.Should().BeTrue();
        Output.WriteLine($"Sub-thread reached IsExecuting=true at {DateTime.UtcNow:O}");

        // Observation window: wait and confirm the sub-thread does NOT settle
        // on its own. In prod this stays true for hours; here we use a tight
        // window to keep the test fast. If/when a self-recovery watchdog is
        // added, flip the assertion below to BeFalse with a window that
        // exceeds the watchdog's timeout.
        await Task.Delay(ObservationWindow, ct);

        var stillExecuting = await workspace.GetMeshNodeStream(subThreadPath)
            .Select(n => n.Content as MeshThread)
            .Take(1)
            .Timeout(5.Seconds())
            .ToTask(ct);

        stillExecuting!.IsExecuting.Should().BeTrue(
            "BUG: sub-thread agent stalled (chat client never returns) — without " +
            "user-initiated cancel on the parent (RequestedCancellationAt), the " +
            "sub-thread has no auto-recovery. Parent's 5-min delegation watchdog " +
            "in ChatClientAgentFactory.ExecuteDelegationAsync only exits the " +
            "parent's IAsyncEnumerable; it does NOT flip cancel on the sub-thread. " +
            "Fix candidate: on watchdog/cancel, write RequestedCancellationAt to " +
            "the sub-thread MeshNode the same way the GUI Stop button does. After " +
            "the fix, invert this assertion to BeFalse.");
        Output.WriteLine("Confirmed: hung sub-thread does NOT self-recover.");
    }

    /// <summary>
    /// Regression guard for the cancel-propagation fix (commit "fix(thread-exec):
    /// cancel watcher unions DelegationPaths"). User flips
    /// <c>RequestedCancellationAt</c> on the parent — same primitive the GUI
    /// Stop button uses — and the sub-thread's <c>IsExecuting</c> must flip
    /// false within <see cref="CancelObservationWindow"/>.
    ///
    /// <para>Before the fix, this test failed: the parent's cancel watcher
    /// only walked <c>thread.StreamingToolCalls</c>, which stays stale while
    /// the streaming loop is blocked inside the in-flight delegation tool.
    /// The fix unions <c>StreamingToolCalls</c> with the live
    /// <c>AgentChatClient.DelegationPaths</c> registry — that's written
    /// synchronously by <c>ExecuteDelegationAsync</c> the moment the
    /// sub-thread is dispatched, so the union is never stale.</para>
    /// </summary>
    [Fact]
    public async Task HungSubThread_UserCancelOnParent_PropagatesAndStopsSubThread()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var parentPath = await CreateThreadAsync(client, "Cancel propagation test", ct);
        Output.WriteLine($"Parent thread: {parentPath}");

        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = parentPath,
            UserText = "delegate this",
            ContextPath = ContextPath,
        });

        var subThreadPath = await WaitForDelegationPath(client, parentPath, ct);
        Output.WriteLine($"Sub-thread: {subThreadPath}");

        // Wait for the sub-thread to actually start so cancel finds something
        // running (otherwise the test could race the spin-up).
        await workspace.GetMeshNodeStream(subThreadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { IsExecuting: true })
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);
        Output.WriteLine("Sub-thread reached IsExecuting=true.");

        // Stamp RequestedCancellationAt on the PARENT — same write the GUI
        // Stop button performs (see RequestViaStreamUpdate.md). The parent
        // hub's cancel watcher unions StreamingToolCalls with the live
        // AgentChatClient.DelegationPaths registry and propagates the flip
        // to every active sub-thread.
        await workspace.GetMeshNodeStream(parentPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedCancellationAt = DateTime.UtcNow } }
                : curr!)
            .FirstAsync().ToTask(ct);
        Output.WriteLine("Flipped RequestedCancellationAt on parent.");

        // Sub-thread should settle within ~20s of the cancel write reaching
        // the propagation watcher.
        var settled = await workspace.GetMeshNodeStream(subThreadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Take(1)
            .Timeout(CancelObservationWindow)
            .ToTask(ct);

        settled!.IsExecuting.Should().BeFalse(
            "FIX: user cancel on the parent must propagate to the hung " +
            "sub-thread via the cancel watcher's union of StreamingToolCalls " +
            "+ AgentChatClient.DelegationPaths. The live DelegationPaths " +
            "registry is the load-bearing source — the throttle-persisted " +
            "StreamingToolCalls is stale while the streaming loop is blocked " +
            "inside the tool call.");
        Output.WriteLine($"Sub-thread cancelled successfully at {DateTime.UtcNow:O}");
    }

    // 20s is enough time for the parent's cancel watcher to fire, the
    // RequestedCancellationAt write to land on the sub-thread, the sub-thread's
    // own cancel watcher to react, and the sub-thread's CTS cancellation to
    // unwind the hanging IChatClient.
    private static readonly TimeSpan CancelObservationWindow = TimeSpan.FromSeconds(20);

    // 10s is short enough to keep CI fast and long enough that any plausible
    // self-recovery mechanism would have fired (sub-thread streaming starts
    // within seconds when the agent is responsive).
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(10);

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "TestUser");
        var response = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!.Path!;
    }

    /// <summary>
    /// Waits for the parent's response message to stamp a non-null
    /// <c>DelegationPath</c> on its delegation tool call, then returns that
    /// path. This is the canonical signal that the parent's delegate_to_agent
    /// call has created a sub-thread and the sub-thread's hub is the next
    /// thing to observe.
    /// </summary>
    private static async Task<string> WaitForDelegationPath(
        IMessageHub client, string parentPath, CancellationToken ct)
    {
        var workspace = client.GetWorkspace();

        // Wait until the parent thread has its first assistant response cell.
        var thread = await workspace.GetMeshNodeStream(parentPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { Messages.Count: >= 2 })
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);

        var respMsgId = thread!.Messages[^1];
        var respPath = $"{parentPath}/{respMsgId}";

        // The sub-thread path lives on the response message's tool calls.
        var responseMsg = await workspace.GetMeshNodeStream(respPath)
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m?.ToolCalls != null && m.ToolCalls.Count > 0
                && m.ToolCalls[0].DelegationPath is { Length: > 0 })
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);

        return responseMsg!.ToolCalls![0].DelegationPath!;
    }

    #region Fake delegating agent + hanging sub-agent

    /// <summary>
    /// Parent: emits a delegate_to_agent function call on the FIRST turn (when
    /// no FunctionResultContent is in the conversation yet). Sub-agent: never
    /// returns.
    ///
    /// Target agent is <c>Worker</c> — one of the built-in agents registered
    /// by <c>.AddAI()</c>. The framework's hierarchy enumeration includes it
    /// in <c>allAgents</c>, so <c>ExecuteDelegationAsync</c> proceeds with the
    /// sub-thread create instead of short-circuiting with
    /// <c>"Agent ... not found"</c>.
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
            // Has FCC already invoked the delegation tool? If yes, this is a
            // follow-up turn — emit plain text to satisfy FCC's "continue
            // until no function call" loop. If we re-emit the function call,
            // FCC infinite-loops.
            //
            // Qualified Enumerable.SelectMany — the reactive Observable.SelectMany
            // is in scope via the file's `using System.Reactive.Linq` and the
            // unqualified call would prefer it on an IEnumerable<ChatMessage>.
            var hasToolResult = false;
            foreach (var msg in messages)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionResultContent)
                    {
                        hasToolResult = true;
                        break;
                    }
                }
                if (hasToolResult) break;
            }

            if (!hasToolResult)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call1", "delegate_to_agent",
                        new Dictionary<string, object?>
                        {
                            ["agentName"] = "Worker",
                            ["task"] = "do some work that hangs"
                        })]);
                await Task.Yield();
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant,
                "Delegation tool returned — parent finishing turn.");
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    /// <summary>
    /// Sub-agent that hangs forever. Models the prod symptom: agent allocation
    /// or the first LLM call never returns. Respects cancellation — the user
    /// cancel path uses this to settle the sub-thread.
    /// </summary>
    private sealed class HangingSubAgentChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("HangingSubAgent");

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
            // Block forever — never yields, never completes. Respects the token
            // so the user-cancel test can drive settlement.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break; // unreachable
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    /// <summary>
    /// Extends <see cref="ChatClientAgentFactory"/> so the framework registers
    /// the real <c>delegate_to_agent</c> tool. Routes the default agent's
    /// chat client to <see cref="DelegatingParentChatClient"/> (emits the
    /// function call); every other agent (the delegation target) gets
    /// <see cref="HangingSubAgentChatClient"/>.
    /// </summary>
    private sealed class HangingSubAgentFactory(IMessageHub hub)
        : ChatClientAgentFactory(hub)
    {
        public override string Name => "HangingSubAgentFactory";
        public override IReadOnlyList<string> Models => ["hanging-model"];
        public override int Order => 0;

        protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
        {
            return agentConfig.IsDefault
                ? new DelegatingParentChatClient()
                : new HangingSubAgentChatClient();
        }
    }

    #endregion
}
