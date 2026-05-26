using System;
using System.Collections.Generic;
using System.Linq;
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
/// Pins the invariant that drove the 2026-05-21 redesign of
/// <c>ChatClientAgentFactory.ExecuteDelegationAsync</c>: across one delegation
/// lifecycle, the parent's response cell <c>ToolCalls</c> list contains
/// EXACTLY ONE entry for the delegation's <c>DelegationPath</c>. The previous
/// shape projected per-tick streaming previews onto the parent's
/// <c>ToolCallEntry.Result</c>, which interacted with the FCC streaming
/// loop's appender to occasionally produce duplicate entries (visible as
/// "sub-thread appears twice" in the GUI). The new shape writes ONCE at
/// terminal (Status flip + final Result for FCC's FunctionResultContent);
/// the live sub-agent body streams direct from the sub-thread cell into
/// the parent bubble's embedded sub-thread Streaming area.
///
/// <para>This test catches a regression that would silently re-introduce
/// the per-tick projection write â€” that pattern always risks duplicates
/// because the FCC append and the projection write race on
/// <c>DelegationPath</c>-by-lookup.</para>
/// </summary>
public class DelegationWriteCountTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // ðŸš¨ The test waits for cell.Status terminal; subsequent
            // completion writes (thread.Status=Idle, parent NotifyParentCompletion,
            // EmitCompletionNotification) are fire-and-forget and land AFTER
            // the test exits. The default 500 ms quiesce budget is too tight
            // for streaming-heavy rounds â€” observed 9 in-flight DataChangeRequests
            // at dispose ("X pending callback(s) after 0.50s" failure mode).
            // 5 s wasn't enough on CI (run 26376715753 still leaked); bump to 15 s.
            // We still fail hard if writes don't drain within the longer window.
            .ConfigureHub(c => c.WithQuiesceTimeout(TimeSpan.FromSeconds(15)))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory, CountingDelegationFactory>();
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact(Timeout = 30_000)]
    public async Task Delegation_ParentToolCalls_ContainsExactlyOneEntryPerDelegationPath()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // 1) Create parent thread + submit a message that triggers a delegation.
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "trigger delegation", "TestUser");
        var create = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);
        create.Message.Success.Should().BeTrue(create.Message.Error);
        var parentThreadPath = create.Message.Node!.Path!;

        // Subscribe to parent thread BEFORE submitting so we don't miss
        // Messages emissions during the round dispatch.
        var parentSyncStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(new Address(parentThreadPath), new MeshNodeReference());

        var twoMessages = parentSyncStream
            .Select(c => (c.Value?.Content as MeshThread)?.Messages ?? [])
            .Where(ids => ids.Count >= 2)
            .Take(1)
            .Timeout(20.Seconds())
            .ToTask(ct);

        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = parentThreadPath,
            UserText = "do it",
            ContextPath = ContextPath
        });

        var parentMsgIds = await twoMessages;
        var parentRespId = parentMsgIds[1];
        var parentRespPath = $"{parentThreadPath}/{parentRespId}";

        // 2) Wait for the parent's response cell to reach a terminal Status
        //    (= Completed/Cancelled/Error). At that point ExecuteDelegationAsync
        //    has already written its single terminal-status write onto the
        //    delegation ToolCallEntry, and FCC has produced its wrap-up.
        var responseStream = workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(new Address(parentRespPath), new MeshNodeReference());

        var completed = await responseStream
            .Select(c => c.Value?.Content as ThreadMessage)
            .Where(m => m is { Status: ThreadMessageStatus.Completed or ThreadMessageStatus.Cancelled or ThreadMessageStatus.Error })
            .Take(1)
            .Timeout(45.Seconds())
            .ToTask(ct);

        completed.Should().NotBeNull();
        Output.WriteLine($"Parent response terminal Status={completed!.Status}, " +
            $"Text='{(completed.Text ?? string.Empty)[..Math.Min(80, (completed.Text ?? string.Empty).Length)]}', " +
            $"ToolCalls.Count={completed.ToolCalls.Count}");

        // 3) Invariant: at most one ToolCallEntry per DelegationPath.
        //    If the per-tick projection-mirror sneaks back in (or the FCC
        //    streaming loop double-appends on delegation), this will trip.
        var delegationCalls = completed.ToolCalls
            .Where(tc => !string.IsNullOrEmpty(tc.DelegationPath))
            .ToList();

        // We expect exactly ONE delegation (the fake parent agent emits one
        // delegate_to_agent on the first turn, then text wrap-up after the
        // tool result lands).
        delegationCalls.Should().HaveCount(1,
            "exactly one delegation tool call should exist; previous projection-mirror logic " +
            "risked appending duplicates with the same DelegationPath");

        var byPath = delegationCalls.GroupBy(tc => tc.DelegationPath!).ToList();
        byPath.Should().OnlyContain(g => g.Count() == 1,
            "no duplicate ToolCallEntry per DelegationPath; the parent should write the " +
            "delegation entry once at FCC-append time and update it once at terminal");

        // 4) Verify the entry's terminal state is well-formed: Status non-Streaming.
        //    Result population depends on whether the underlying chat client emits
        //    FunctionResultContent in its stream output (SDK-specific â€” Claude does,
        //    test agents typically don't) OR on UpdateDelegationStatus stamping
        //    a Result via my mirror. The structural invariant we enforce here is
        //    "no duplicate entries per DelegationPath + Status reaches a terminal
        //    value", not "Result is always populated" â€” that's a separate concern
        //    that depends on the chat client and is exercised by Orleans tests.
        var entry = delegationCalls[0];
        entry.Status.Should().NotBe(ToolCallStatus.Streaming,
            "delegation entry should be at a terminal status by the time the parent response is Completed");
    }

    #region Fake agents â€” parent delegates once, sub completes quickly

    private sealed class DelegatingParentClient : IChatClient
    {
        // FCC iterates: turn 1 should emit one FunctionCallContent, turn 2+ should
        // emit only text. Detecting via FunctionResultContent in `messages` is
        // unreliable (different chat-client wrappers shape that differently â€”
        // FRC may or may not be propagated back to the model). Track it
        // explicitly with a counter so the test is fully deterministic.
        private int _streamingCallCount;

        public ChatClientMetadata Metadata => new("DelegatingParent");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var callIndex = Interlocked.Increment(ref _streamingCallCount);
            if (callIndex == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call1", "delegate_to_agent",
                        new Dictionary<string, object?>
                        {
                            ["agentName"] = "Worker",
                            ["task"] = "produce a quick reply"
                        })]);
                await Task.Yield();
                yield break;
            }

            foreach (var word in "Sub-thread done.".Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Delay(5, cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private sealed class QuickSubAgentClient : IChatClient
    {
        private const string Reply = "alpha beta gamma delta";

        public ChatClientMetadata Metadata => new("QuickSubAgent");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in Reply.Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Delay(5, cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private sealed class CountingDelegationFactory(IMessageHub hub)
        : ChatClientAgentFactory(hub)
    {
        public override string Name => "CountingDelegationFactory";
        public override IReadOnlyList<string> Models => ["counting-model"];
        public override int Order => 0;

        protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
            => agentConfig.IsDefault
                ? new DelegatingParentClient()
                : new QuickSubAgentClient();
    }

    #endregion
}
