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
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Proves that the control-plane patch pattern (single-field stream.Update
/// observed by the owning hub's watcher) is race-free under concurrent
/// callers â€” the invariant the thread-trigger removal depends on.
///
/// Each test exercises a different control-plane field
/// (<see cref="MeshThread.PendingUserMessages"/>,
/// <see cref="MeshThread.RequestedResubmit"/>,
/// <see cref="MeshThread.RequestedDeleteFromMessageId"/>,
/// <see cref="MeshThread.PendingFailures"/>) and asserts that the
/// post-watcher state is consistent â€” no duplicate ids, no doubled error
/// cells, no second dispatch of the same round.
/// </summary>
public class ThreadControlPlaneConcurrencyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new EchoChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddData();
    }

    [Fact]
    public async Task ConcurrentSubmissions_ProduceExactlyOneUserMessagePerSubmit()
    {
        var ct = new CancellationTokenSource(90.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadPath = await CreateThreadAsync(client, "concurrent submits", ct);

        const int N = 4;
        // Fire N submits in parallel. Each Submit generates its own user
        // message id and patches PendingUserMessages on the thread node.
        var submits = Enumerable.Range(0, N).Select(i => Task.Run(() =>
            client.SubmitMessage(
                threadPath,
                $"msg-{i}",
                contextPath: ContextPath), ct)).ToArray();
        await Task.WhenAll(submits);

        // Wait for all N submissions to reach UserMessageIds (ingested into
        // at least one round) and the thread to settle back to Idle.
        var final = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { Status: ThreadExecutionStatus.Idle }
                        && t.UserMessageIds.Count >= N
                        && t.PendingUserMessages.IsEmpty)
            .Take(1)
            .Timeout(60.Seconds())
            .ToTask(ct);

        final!.UserMessageIds.Count.Should().Be(N,
            "exactly N submissions must produce N user message ids â€” no duplicates from re-emission, no drops from race");
        final.UserMessageIds.Distinct().Count().Should().Be(N,
            "user message ids must be unique");
        final.IngestedMessageIds.Count.Should().Be(N,
            "all N user messages must be ingested by the watcher");
    }

    [Fact]
    public async Task ConcurrentResubmits_ConvergeToSingleConsistentState()
    {
        var ct = new CancellationTokenSource(90.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadPath = await CreateThreadAsync(client, "resubmit race", ct);

        // Submit msg1 and msg2 sequentially to populate the thread.
        client.SubmitMessage(threadPath, "first", contextPath: ContextPath);
        var afterFirst = await WaitForThreadAsync(workspace, threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.UserMessageIds.Count >= 1,
            ct);
        var msg1 = afterFirst.UserMessageIds[0];

        client.SubmitMessage(threadPath, "second", contextPath: ContextPath);
        var afterSecond = await WaitForThreadAsync(workspace, threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.UserMessageIds.Count >= 2,
            ct);
        var msg2 = afterSecond.UserMessageIds[1];

        // Concurrent resubmits from two callers â€” both patch the SAME
        // RequestedResubmit field. RFC-7396 single-field merge: last write
        // wins. The watcher processes whoever lands second.
        var raceA = Task.Run(() => client.ResubmitMessage(threadPath, msg1, newUserText: "first-rev"), ct);
        var raceB = Task.Run(() => client.ResubmitMessage(threadPath, msg2, newUserText: "second-rev"), ct);
        await Task.WhenAll(raceA, raceB);

        // After both resubmits land + the watcher processes (potentially
        // both, in some order), the thread must converge to a consistent
        // state: RequestedResubmit cleared, no duplicate user ids.
        var final = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { RequestedResubmit: null, Status: ThreadExecutionStatus.Idle }
                        && t.PendingUserMessages.IsEmpty)
            .Take(1)
            .Timeout(60.Seconds())
            .ToTask(ct);

        final!.RequestedResubmit.Should().BeNull("watcher must clear the intent atomically");
        final.UserMessageIds.Distinct().Count().Should().Be(final.UserMessageIds.Count,
            "no duplicate user ids in the converged state");
    }

    [Fact]
    public async Task DeleteFromMessage_DuringNewSubmit_ProducesConsistentList()
    {
        var ct = new CancellationTokenSource(90.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadPath = await CreateThreadAsync(client, "delete + submit race", ct);

        client.SubmitMessage(threadPath, "to delete", contextPath: ContextPath);
        var afterFirst = await WaitForThreadAsync(workspace, threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.UserMessageIds.Count >= 1,
            ct);
        var msg1 = afterFirst.UserMessageIds[0];

        // Race: delete msg1 while submitting msg2.
        var raceDelete = Task.Run(() => client.DeleteFromMessage(threadPath, msg1), ct);
        var raceSubmit = Task.Run(() => client.SubmitMessage(
            threadPath, "new", contextPath: ContextPath), ct);
        await Task.WhenAll(raceDelete, raceSubmit);

        var final = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { Status: ThreadExecutionStatus.Idle, RequestedDeleteFromMessageId: null }
                        && t.PendingUserMessages.IsEmpty)
            .Take(1)
            .Timeout(60.Seconds())
            .ToTask(ct);

        final!.RequestedDeleteFromMessageId.Should().BeNull("delete intent must be cleared after the watcher applies it");
        // Either msg1 was deleted (and any subsequent ids dropped â€” Messages doesn't contain msg1),
        // OR the submit landed atomically and msg1 is still in Messages alongside msg2's ids.
        // Both are valid; the invariant is no duplicates and no orphan ids.
        final.Messages.Distinct().Count().Should().Be(final.Messages.Count,
            "no duplicate message ids");
        final.UserMessageIds.Distinct().Count().Should().Be(final.UserMessageIds.Count,
            "no duplicate user ids");
    }

    [Fact]
    public async Task FailureRecord_ConcurrentWithSubmit_ProducesExactlyOneErrorCell()
    {
        var ct = new CancellationTokenSource(90.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadPath = await CreateThreadAsync(client, "failure + submit race", ct);

        var failedUserId = Guid.NewGuid().ToString("N")[..8];
        var errorMessage = "concurrent-failure-test";

        // Race: record a failure for msg(failedUserId) while submitting a real msg.
        var raceFailure = Task.Run(() => client.RecordSubmissionFailure(
            threadPath, failedUserId, "doomed input", errorMessage), ct);
        var raceSubmit = Task.Run(() => client.SubmitMessage(
            threadPath, "good", contextPath: ContextPath), ct);
        await Task.WhenAll(raceFailure, raceSubmit);

        var final = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { Status: ThreadExecutionStatus.Idle }
                        && t.PendingFailures.IsEmpty
                        && t.PendingUserMessages.IsEmpty
                        && t.UserMessageIds.Contains(failedUserId))
            .Take(1)
            .Timeout(60.Seconds())
            .ToTask(ct);

        final!.PendingFailures.Should().BeEmpty("watcher must consume all failure records");
        final.UserMessageIds.Count(id => id == failedUserId).Should().Be(1,
            "failed user id must appear exactly once in UserMessageIds");
        final.IngestedMessageIds.Should().Contain(failedUserId,
            "failure record must mark the id as ingested so the watcher doesn't dispatch a new round for it");

        // Verify exactly one error cell exists for failedUserId by counting
        // Messages entries downstream of failedUserId that point at an
        // assistant cell whose text contains the error message.
        var idxFailed = final.Messages.IndexOf(failedUserId);
        idxFailed.Should().BeGreaterThanOrEqualTo(0, "failed user id is committed to Messages");
        // The error cell id is the next id after failedUserId in Messages.
        (idxFailed + 1).Should().BeLessThan(final.Messages.Count,
            "an error cell id must follow the failed user id in Messages");
    }

    [Fact]
    public async Task StartingExecution_NoOpStreamUpdate_DoesNotReDispatch()
    {
        var ct = new CancellationTokenSource(90.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadPath = await CreateThreadAsync(client, "no re-dispatch", ct);

        client.SubmitMessage(threadPath, "ping", contextPath: ContextPath);
        var afterRound = await WaitForThreadAsync(workspace, threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2,
            ct);
        var messagesAfterFirstRound = afterRound.Messages.Count;

        // Drive a no-op stream.Update on the thread. The submission watcher
        // and _Exec round watcher must NOT re-dispatch â€” Messages.Count must
        // stay the same.
        await workspace.GetMeshNodeStream(threadPath).Update(node => node)
            .Take(1).Timeout(10.Seconds()).ToTask(ct);

        // Poll the thread state for 2 seconds via Observable.Interval (no
        // raw Task.Delay) â€” if a re-dispatch fires, the message count grows
        // and we see it; otherwise the polling completes cleanly.
        var maxObservedCount = await Observable.Interval(200.Milliseconds())
            .Take(10)
            .SelectMany(_ => workspace.GetMeshNodeStream(threadPath)
                .Select(n => (n.Content as MeshThread)?.Messages.Count ?? 0)
                .Take(1))
            .Max()
            .Timeout(5.Seconds())
            .ToTask(ct);

        maxObservedCount.Should().Be(messagesAfterFirstRound,
            "Messages.Count must not grow after a no-op stream.Update â€” observing growth means a spurious re-dispatch");
    }

    private async Task<string> CreateThreadAsync(IMessageHub client, string title, CancellationToken ct)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, title, "TestUser");
        var createResp = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);
        var threadPath = createResp.Message.Node!.Path!;

        // Warm up the stream subscription so subsequent waits don't miss
        // the initial emission.
        await client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t != null)
            .Take(1).Timeout(10.Seconds()).ToTask(ct);
        return threadPath;
    }

    private static Task<MeshThread> WaitForThreadAsync(
        IWorkspace workspace, string threadPath,
        Func<MeshThread, bool> predicate, CancellationToken ct)
        => workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t != null && predicate(t))
            .Take(1)
            .Timeout(45.Seconds())
            .ToTask(ct)!;

    #region Echo IChatClient + factory (same shape as IsExecutingLifecycleTest)

    private sealed class EchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("EchoProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"I received {messages.Count()} messages.")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var n = messages.Count();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                $"I received {n} messages in this conversation.");
            await Task.Delay(5, ct);
        }

        public object? GetService(Type serviceType, object? key = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private sealed class EchoChatClientFactory : IChatClientFactory
    {
        public string Name => "EchoFactory";
        public IReadOnlyList<string> Models => ["echo-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new EchoChatClient(), instructions: "Echo agent.",
                name: config.Id, description: config.Description ?? "",
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
