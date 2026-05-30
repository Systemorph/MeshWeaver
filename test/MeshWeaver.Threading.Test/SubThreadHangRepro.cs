using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
/// Shape: parent agent delegates â†’ sub-thread is created with <c>IsExecuting=true</c>
/// + a pending user message â†’ sub-thread's hub activates and starts executing â†’
/// the sub-thread's <see cref="IChatClient"/> hangs (no streaming, no completion) â†’
/// the parent's 5-minute watchdog in
/// <see cref="ChatClientAgentFactory"/>'s delegation drain eventually closes the
/// PARENT's <c>IAsyncEnumerable</c>, but the <b>sub-thread itself</b> has no
/// self-cancellation and no propagated cancel â€” so its <c>IsExecuting</c> flag
/// stays <c>true</c> forever. The user sees a perpetually-"executing" sub-thread.
///
/// The existing cancel path (<see cref="MeshThread.RequestedCancellationAt"/> on the
/// parent â†’ propagated to every active sub-thread via
/// <c>ThreadExecution</c>'s cancel watcher) only fires when the user clicks Stop.
/// Without that intervention, the sub-thread hangs.
///
/// <para>This test pins both shapes:</para>
/// <list type="number">
///   <item><c>HungSubThread_WithoutUserCancel_StaysExecuting</c> â€” documents the BUG:
///   after the sub-thread starts and its agent stalls, <c>IsExecuting</c> never
///   flips back to false on its own within a reasonable window.</item>
///   <item><c>HungSubThread_UserCancelOnParent_PropagatesAndStopsSubThread</c> â€”
///   confirms the EXISTING fallback works: user-initiated cancel on the parent
///   does propagate and unwind the hung sub-thread.</item>
/// </list>
///
/// <para>Fix shape (when applied): the parent's delegation drain
/// (<see cref="ChatClientAgentFactory"/>'s <c>ExecuteDelegationAsync</c>) must,
/// on watchdog/cancel, flip <c>RequestedCancellationAt</c> on the sub-thread â€”
/// the same write the user's Stop button performs. After the fix, the first
/// test becomes a real assertion (sub-thread settles even without user cancel)
/// and the second remains a regression guard for the manual-cancel path.</para>
/// </summary>
public class SubThreadHangRepro(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // The hanging sub-thread leaves DataChangeRequests pending on the
            // sub-thread hub (it's hung in IChatClient, can't process writes).
            // JsonSynchronizationStream's per-emission DataChangeRequest â†’
            // Observe chain doesn't dispose until the framework's 60s
            // RequestTimeout fires. Without bumping QuiesceTimeout, the test
            // base's 500ms leak-detection trips on these expected-pending
            // callbacks. 75s here = framework's 60s RequestTimeout + 15s
            // grace for the cancel-propagated chain to fully unwind.
            .ConfigureHub(c => c.WithQuiesceTimeout(TimeSpan.FromSeconds(75)))
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
    /// STILL true â€” the bug shape: sub-thread has no self-recovery, no parent
    /// watchdog propagation. In prod this stays true for hours.
    ///
    /// <para>When the fix lands (parent watchdog flips
    /// <c>RequestedCancellationAt</c> on the sub-thread, or sub-thread gets its
    /// own no-progress watchdog), invert this assertion to
    /// <c>Should().BeFalse</c> with a generous window â€” that's the regression
    /// guard.</para>
    /// </summary>
    [Fact]
    public void HungSubThread_WithoutUserCancel_StaysExecuting()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var parentPath = CreateThread(client, "Delegate to a worker that hangs");
        Output.WriteLine($"Parent thread: {parentPath}");

        // Warm up the parent thread stream BEFORE submit â€” see CancelStream
        // test for why this matters (submission watcher races first
        // cache.GetStream and can stall the chain at Status=StartingExecution).
        workspace.GetMeshNodeStream(parentPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(10.Seconds()).Match(t => t != null);

        client.SubmitMessage(
            parentPath,
            "delegate this",
            contextPath: ContextPath);

        // Wait for the parent's response message to materialise and the
        // delegation tool call to be stamped with a DelegationPath. That path
        // IS the sub-thread we'll observe.
        var subThreadPath = WaitForDelegationPath(client, parentPath);
        Output.WriteLine($"Sub-thread: {subThreadPath}");

        // Wait for the sub-thread to reach IsExecuting=true â€” proves the
        // sub-thread hub activated and its WatchForExecution started the
        // agent. The hanging IChatClient takes over from here.
        //
        // ðŸš¨ Race: WaitForDelegationPath returns as soon as the parent's
        // tool call has DelegationPath stamped (synchronous), but
        // ExecuteDelegationAsync's meshService.CreateNode(subThreadNode) is
        // fire-and-forget â€” the per-node hub may not have activated yet.
        // GetMeshNodeStream surfaces "missing satellite" as OnError
        // (DeliveryFailureException) almost immediately (post-2026-05-24
        // cache change f103be08a). .Catch+Defer retries with backoff so
        // we wait for the create to land instead of failing fast.
        var subThread = Observable.Defer(() =>
                workspace.GetMeshNodeStream(subThreadPath)
                    .Select(n => n.Content as MeshThread)
                    .Where(t => t is { IsExecuting: true })
                    .Take(1))
            .Catch<MeshThread?, Exception>(_ =>
                Observable.Empty<MeshThread?>().Delay(200.Milliseconds()))
            .Repeat()
            .Should().Within(15.Seconds()).Emit();
        subThread!.IsExecuting.Should().BeTrue();
        Output.WriteLine($"Sub-thread reached IsExecuting=true at {DateTime.UtcNow:O}");

        // REGRESSION GUARD for the watchdog-propagates-cancel fix:
        //
        // ChatClientAgentFactory.ExecuteDelegationAsync has a 30s safety
        // timeout (CancellationTokenSource at line 544). When it fires, the
        // exit path at line ~634 writes RequestedCancellationAt to the
        // sub-thread MeshNode â€” same primitive the GUI Stop button uses â€”
        // which propagates through the sub-thread's cancel watcher and
        // tears down its CTS, causing HangingSubAgentChatClient's
        // Task.Delay to throw OperationCanceled. Sub-thread settles
        // IsExecuting=false.
        //
        // Without the fix, the sub-thread stays IsExecuting=true forever
        // and the user sees a perpetually-"executing" bubble.
        //
        // The wait window: 30s watchdog + 15s slack for propagation through
        // parent stream emission â†’ sub-thread cancel watcher â†’ CTS cancel â†’
        // streaming loop exit â†’ terminal Status flip. 45s total.
        var settled = Observable.Defer(() =>
                workspace.GetMeshNodeStream(subThreadPath)
                    .Select(n => n.Content as MeshThread)
                    .Where(t => t is { IsExecuting: false })
                    .Take(1))
            .Catch<MeshThread?, Exception>(_ =>
                Observable.Empty<MeshThread?>().Delay(500.Milliseconds()))
            .Repeat()
            .Should().Within(45.Seconds()).Emit();

        settled!.IsExecuting.Should().BeFalse(
            "FIX: sub-thread settled within the 30s watchdog + 15s propagation " +
            "slack. ChatClientAgentFactory.ExecuteDelegationAsync's safety " +
            "timeout fires on a never-completing sub-thread, writes " +
            "RequestedCancellationAt to the sub-thread MeshNode (same write " +
            "the GUI Stop button performs), and the sub-thread's cancel " +
            "watcher unwinds its CTS â€” HangingSubAgentChatClient's Task.Delay " +
            "throws OperationCanceled and the streaming loop exits clean.");
        Output.WriteLine($"Sub-thread settled at {DateTime.UtcNow:O} (no user cancel â€” watchdog did it)");
    }

    /// <summary>
    /// Regression guard for the cancel-propagation fix (commit "fix(thread-exec):
    /// cancel watcher unions DelegationPaths"). User flips
    /// <c>RequestedCancellationAt</c> on the parent â€” same primitive the GUI
    /// Stop button uses â€” and the sub-thread's <c>IsExecuting</c> must flip
    /// false within <see cref="CancelObservationWindow"/>.
    ///
    /// <para>Before the fix, this test failed: the parent's cancel watcher
    /// only walked <c>thread.StreamingToolCalls</c>, which stays stale while
    /// the streaming loop is blocked inside the in-flight delegation tool.
    /// The fix unions <c>StreamingToolCalls</c> with the live
    /// <c>AgentChatClient.DelegationPaths</c> registry â€” that's written
    /// synchronously by <c>ExecuteDelegationAsync</c> the moment the
    /// sub-thread is dispatched, so the union is never stale.</para>
    /// </summary>
    [Fact]
    public void HungSubThread_UserCancelOnParent_PropagatesAndStopsSubThread()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var parentPath = CreateThread(client, "Cancel propagation test");
        Output.WriteLine($"Parent thread: {parentPath}");

        // Warm up the parent thread stream BEFORE submit. Without this the
        // submission watcher races the first cache.GetStream and the chain
        // stalls at Status=StartingExecution.
        workspace.GetMeshNodeStream(parentPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(10.Seconds()).Match(t => t != null);

        client.SubmitMessage(
            parentPath,
            "delegate this",
            contextPath: ContextPath);

        var subThreadPath = WaitForDelegationPath(client, parentPath);
        Output.WriteLine($"Sub-thread: {subThreadPath}");

        // Wait for the sub-thread to actually start so cancel finds something
        // running (otherwise the test could race the spin-up).
        workspace.GetMeshNodeStream(subThreadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(15.Seconds()).Match(t => t is { IsExecuting: true });
        Output.WriteLine("Sub-thread reached IsExecuting=true.");

        // Set RequestedStatus = Cancelled on the PARENT â€” same write the GUI
        // Stop button performs (see RequestViaStreamUpdate.md). The parent
        // hub's cancel watcher unions StreamingToolCalls with the live
        // AgentChatClient.DelegationPaths registry and propagates the request
        // to every active sub-thread.
        workspace.GetMeshNodeStream(parentPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                : curr!)
            .Should().Emit();
        Output.WriteLine("Set RequestedStatus = Cancelled on parent.");

        // Sub-thread should settle within ~20s of the cancel write reaching
        // the propagation watcher.
        var settled = workspace.GetMeshNodeStream(subThreadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(CancelObservationWindow).Match(t => t is { IsExecuting: false });

        settled!.IsExecuting.Should().BeFalse(
            "FIX: user cancel on the parent must propagate to the hung " +
            "sub-thread via the cancel watcher's union of StreamingToolCalls " +
            "+ AgentChatClient.DelegationPaths. The live DelegationPaths " +
            "registry is the load-bearing source â€” the throttle-persisted " +
            "StreamingToolCalls is stale while the streaming loop is blocked " +
            "inside the tool call.");
        Output.WriteLine($"Sub-thread cancelled successfully at {DateTime.UtcNow:O}");
    }

    // 20s is enough time for the parent's cancel watcher to fire, the
    // RequestedCancellationAt write to land on the sub-thread, the sub-thread's
    // own cancel watcher to react, and the sub-thread's CTS cancellation to
    // unwind the hanging IChatClient.
    private static readonly TimeSpan CancelObservationWindow = TimeSpan.FromSeconds(20);

    // 10s for CI; 12min (720s) for a watchdog-debug run â€” parent
    // ExecuteDelegationAsync has a 5-min CancellationTokenSource that SHOULD
    // propagate RequestedCancellationAt to the sub-thread (see
    // ChatClientAgentFactory.cs:634-643). If the sub-thread still hasn't
    // settled by 12 min, something is jamming the propagation.
    private static readonly TimeSpan ObservationWindow =
        Environment.GetEnvironmentVariable("MESHWEAVER_HANG_DEBUG") == "1"
            ? TimeSpan.FromSeconds(720)
            : TimeSpan.FromSeconds(10);

    private string CreateThread(IMessageHub client, string text)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "TestUser");
        var response = client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).Should().Within(15.Seconds()).Emit();
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        return response.Message.Node!.Path!;
    }

    /// <summary>
    /// Waits for the parent's response message to stamp a non-null
    /// <c>DelegationPath</c> on its delegation tool call, then returns that
    /// path. This is the canonical signal that the parent's delegate_to_agent
    /// call has created a sub-thread and the sub-thread's hub is the next
    /// thing to observe.
    /// </summary>
    private static string WaitForDelegationPath(
        IMessageHub client, string parentPath)
    {
        var workspace = client.GetWorkspace();

        // Wait until the parent thread has its first assistant response cell.
        var thread = workspace.GetMeshNodeStream(parentPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(15.Seconds()).Match(t => t is { Messages.Count: >= 2 });

        var respMsgId = thread!.Messages[^1];
        var respPath = $"{parentPath}/{respMsgId}";

        // The sub-thread path lives on the response message's tool calls.
        // 30s budget â€” 15s tripped on slow CI runners (run 26376715753).
        var responseMsg = workspace.GetMeshNodeStream(respPath)
            .Select(n => n.Content as ThreadMessage)
            .Should().Within(30.Seconds()).Match(m => m?.ToolCalls != null && m.ToolCalls.Count > 0
                && m.ToolCalls[0].DelegationPath is { Length: > 0 });

        return responseMsg!.ToolCalls![0].DelegationPath!;
    }

    #region Fake delegating agent + hanging sub-agent

    /// <summary>
    /// Parent: emits a delegate_to_agent function call on the FIRST turn (when
    /// no FunctionResultContent is in the conversation yet). Sub-agent: never
    /// returns.
    ///
    /// Target agent is <c>Worker</c> â€” one of the built-in agents registered
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
            // follow-up turn â€” emit plain text to satisfy FCC's "continue
            // until no function call" loop. If we re-emit the function call,
            // FCC infinite-loops.
            //
            // Qualified Enumerable.SelectMany â€” the reactive Observable.SelectMany
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
                "Delegation tool returned â€” parent finishing turn.");
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    /// <summary>
    /// Sub-agent that hangs forever. Models the prod symptom: agent allocation
    /// or the first LLM call never returns. Respects cancellation â€” the user
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
            // Block forever â€” never yields, never completes. Respects the token
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
