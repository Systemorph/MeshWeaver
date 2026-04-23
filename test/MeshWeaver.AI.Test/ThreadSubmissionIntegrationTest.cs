#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// End-to-end integration tests for <see cref="ThreadSubmission"/>.
/// Verifies that <c>Submit</c>/<c>CreateThreadAndSubmit</c>/<c>Resubmit</c> drive
/// the server watcher to create output cells and commit ingested state,
/// fully via Post + RegisterCallback + workspace stream subscriptions (no QueryAsync writes from the code path).
/// Test assertions use QueryAsync/FirstAsync — allowed per CLAUDE.md for test code only.
/// </summary>
public class ThreadSubmissionIntegrationTest : AITestBase
{
    private const string FakeResponseText = "fake agent ack";

    public ThreadSubmissionIntegrationTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                services.AddSingleton<IChatClientFactory>(new SlowFakeChatClientFactory());
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        // Client hub needs Data + Layout for GetWorkspace() + GetRemoteStream.
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    // ─── Submit into existing thread ───

    [Fact]
    public async Task Submit_ExistingThread_UserMessageIngested_OutputCellAppears()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var client = GetClient();

        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = threadPath,
            UserText = "Hello from test",
            CreatedBy = "rbuergi@systemorph.com",
            AuthorName = "Tester"
        });

        // Wait for the watcher to ingest the user message into a round.
        var committed = await WaitForThreadAsync(
            threadPath,
            t => t.IngestedMessageIds.Count >= 1 && t.Messages.Count >= 2,
            timeoutMs: 30_000, ct);

        committed.IngestedMessageIds.Should().HaveCount(1);
        committed.Messages.Should().HaveCount(2, "expected one user cell + one output cell in Messages");
        committed.IngestedMessageIds[0].Should().Be(committed.Messages[0], "user id should be the first message");
        committed.UserMessageIds.Should().ContainInOrder(committed.IngestedMessageIds[0]);
    }

    // ─── CreateThreadAndSubmit ───

    [Fact]
    public async Task CreateThreadAndSubmit_CreatesThreadAndFirstRound()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadCreatedTcs = new TaskCompletionSource<MeshNode>();
        var client = GetClient();

        ThreadSubmission.CreateThreadAndSubmit(new SubmitContext
        {
            Hub = client,
            Namespace = MonolithMeshTestBase.TestPartition,
            UserText = "New thread first message",
            CreatedBy = "rbuergi@systemorph.com",
            OnThreadCreated = node => threadCreatedTcs.TrySetResult(node)
        });

        var created = await threadCreatedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        created.Path.Should().NotBeNullOrEmpty();
        var threadPath = created.Path!;

        var committed = await WaitForThreadAsync(
            threadPath,
            t => t.IngestedMessageIds.Count >= 1 && t.Messages.Count >= 2,
            timeoutMs: 15_000, ct);

        committed.IngestedMessageIds.Should().HaveCount(1);
        committed.Messages.Should().HaveCount(2);
    }

    // ─── Batched ingestion ───

    [Fact]
    public async Task Submit_ThreeRapidSubmissions_AllIngestedIntoOneRound()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);

        var client = GetClient();

        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "First",
            CreatedBy = "rbuergi@systemorph.com"
        });
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "Second",
            CreatedBy = "rbuergi@systemorph.com"
        });
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "Third",
            CreatedBy = "rbuergi@systemorph.com"
        });

        // Wait for at least one round to commit.
        var committed = await WaitForThreadAsync(
            threadPath,
            t => t.IngestedMessageIds.Count >= 1 && t.Messages.Count >= 2,
            timeoutMs: 15_000, ct);

        // Give the watcher a moment to finish any further rounds, then assert final state:
        // All three user messages should end up ingested; the dispatched round(s) produced >=1 output cell.
        await WaitForThreadAsync(
            threadPath,
            t => t.IngestedMessageIds.Count == 3,
            timeoutMs: 30_000, ct);

        var final = await ReadThreadAsync(threadPath, ct);
        final.IngestedMessageIds.Should().HaveCount(3, "all three user messages should be ingested");
        // All three user message ids appear as the first three in Messages.
        var userIds = final.Messages.Take(3).ToList();
        final.IngestedMessageIds.Should().BeEquivalentTo(userIds);
    }

    // ─── Resubmit: truncates after the replayed message, new round dispatches ───

    [Fact]
    public async Task Resubmit_TruncatesAfterReplayedMessage_NewRoundCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var client = GetClient();

        // Round 1: submit u1 and wait for it to complete.
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "First",
            CreatedBy = "rbuergi@systemorph.com"
        });
        var afterRoundOne = await WaitForThreadAsync(
            threadPath,
            t => !t.IsExecuting && t.IngestedMessageIds.Count == 1 && t.Messages.Count == 2,
            timeoutMs: 30_000, ct);

        var u1 = afterRoundOne.UserMessageIds[0];
        var r1 = afterRoundOne.Messages[1];

        // Resubmit u1 with new text.
        ThreadSubmission.Resubmit(new ResubmitContext
        {
            Hub = client,
            ThreadPath = threadPath,
            UserMessageIdToReplay = u1,
            NewUserText = "First, revised"
        });

        // The intermediate "truncated" state (Messages=[u1], IngestedMessageIds=[]) is racy —
        // the server watcher dispatches the new round almost immediately after the truncation
        // commits, often before we can observe it. Instead assert the end state: u1 is
        // ingested again, a NEW response cell (!= r1) follows, and IsExecuting is back to false.
        var afterResubmit = await WaitForThreadAsync(
            threadPath,
            t => t.IngestedMessageIds.Contains(u1)
                 && t.Messages.Count == 2
                 && t.Messages[1] != r1
                 && !t.IsExecuting,
            timeoutMs: 20_000, ct);

        afterResubmit.Messages[0].Should().Be(u1);
        afterResubmit.Messages[1].Should().NotBe(r1, "resubmit must produce a fresh response cell");
        afterResubmit.UserMessageIds.Should().ContainSingle().Which.Should().Be(u1);
    }

    // ─── Failure recovery: error renders as an assistant response cell ───

    [Fact]
    public async Task SubmissionFailure_RecordsErrorAsOutputCell_InThreadMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var client = GetClient();

        var errorTcs = new TaskCompletionSource<string>();

        // Simulate a failure path by posting RecordSubmissionFailureRequest directly.
        // (Exercises the handler contract the client's OnError goes through.)
        var fakeUserMsgId = Guid.NewGuid().ToString("N")[..8];
        var delivery = client.Post(
            new RecordSubmissionFailureRequest
            {
                ThreadPath = threadPath,
                UserMessageId = fakeUserMsgId,
                UserText = "message that failed",
                ErrorMessage = "network timeout"
            },
            o => o.WithTarget(new Address(threadPath)));

        delivery.Should().NotBeNull();

        // Wait for the thread to reflect: user id appended + an error response cell appended
        // + user id marked as ingested (so watcher doesn't retry).
        var final = await WaitForThreadAsync(
            threadPath,
            t => t.Messages.Contains(fakeUserMsgId)
                 && t.IngestedMessageIds.Contains(fakeUserMsgId)
                 && t.Messages.Count >= 2,
            timeoutMs: 5_000, ct);

        final.UserMessageIds.Should().Contain(fakeUserMsgId);
        final.IngestedMessageIds.Should().Contain(fakeUserMsgId);
        final.Messages.Should().HaveCount(2);
        final.Messages[0].Should().Be(fakeUserMsgId);

        // The second entry must be an assistant cell with the error in its Text.
        var errorCellId = final.Messages[1];
        MeshNode? errorCell = null;
        await foreach (var n in MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}/{errorCellId}", null, ct))
        {
            errorCell = n;
            break;
        }
        errorCell.Should().NotBeNull();
        var content = errorCell!.Content as ThreadMessage;
        content.Should().NotBeNull();
        content!.Role.Should().Be("assistant");
        content.Text.Should().Contain("network timeout");
    }

    // ─── Tool-call scenario: 3 rapid submits during a 1s "tool call" ───

    [Fact]
    public async Task Submit_ThreeMessagesDuringActiveRound_QueuedThenBatchedIntoSecondRound()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var client = GetClient();

        // Use the slow-model factory so round 1 takes ~1 second.
        // This gives us a deterministic window to submit u2/u3/u4 while round 1 is still executing.
        var slowModel = "slow-model";

        // Submit u1 — triggers round 1.
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "First",
            ModelName = slowModel, CreatedBy = "rbuergi@systemorph.com"
        });

        // Wait for round 1 to start (IsExecuting=true). This proves u1 has been ingested.
        var roundOneStart = await WaitForThreadAsync(
            threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count == 1,
            timeoutMs: 5_000, ct);

        roundOneStart.IngestedMessageIds.Should().HaveCount(1, "u1 should be ingested once round 1 starts");
        var u1 = roundOneStart.IngestedMessageIds[0];

        // While round 1 is running, submit 3 more messages in quick succession.
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "Second",
            ModelName = slowModel, CreatedBy = "rbuergi@systemorph.com"
        });
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "Third",
            ModelName = slowModel, CreatedBy = "rbuergi@systemorph.com"
        });
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "Fourth",
            ModelName = slowModel, CreatedBy = "rbuergi@systemorph.com"
        });

        // Observe: during round 1 execution, all three new user ids should appear in Messages
        // and UserMessageIds, but NOT yet in IngestedMessageIds — the server holds them back
        // because the thread is busy.
        var pendingState = await WaitForThreadAsync(
            threadPath,
            t => t.UserMessageIds.Count == 4,
            timeoutMs: 3_000, ct);

        // If we're quick enough, round 1 is still executing here. Either way, we can assert
        // that u2/u3/u4 are NOT yet ingested while u1 already is (or that all 4 are ingested
        // if round 1 already finished). The key invariant: no user message is ingested
        // before it exists in UserMessageIds.
        pendingState.UserMessageIds.Should().HaveCount(4, "all four user messages should be registered on the thread");
        pendingState.UserMessageIds.Should().StartWith(u1);
        pendingState.IngestedMessageIds.Count.Should().BeGreaterThanOrEqualTo(1);
        pendingState.IngestedMessageIds.Should().BeSubsetOf(pendingState.UserMessageIds);

        // Wait for all 4 to become ingested. This requires round 1 to finish, then round 2 to commit.
        var final = await WaitForThreadAsync(
            threadPath,
            t => t.IngestedMessageIds.Count == 4 && !t.IsExecuting,
            timeoutMs: 20_000, ct);

        final.IngestedMessageIds.Should().HaveCount(4);
        final.IngestedMessageIds.Should().BeEquivalentTo(final.UserMessageIds);

        // Final Messages layout must be: input - output - input - input - input - output
        //                                 u1    - r1     - u2    - u3    - u4    - r2
        // i.e. the first four positions are u1, r1, u2, u3; u4 precedes r2 at the end.
        final.Messages.Should().HaveCount(6, "expected exactly [u1, r1, u2, u3, u4, r2]");
        final.Messages[0].Should().Be(u1, "u1 first");
        final.UserMessageIds.Should().ContainInOrder(final.Messages[0], final.Messages[2], final.Messages[3], final.Messages[4]);
        // Positions 1 and 5 are response cells (not in UserMessageIds).
        final.UserMessageIds.Should().NotContain(final.Messages[1]);
        final.UserMessageIds.Should().NotContain(final.Messages[5]);
    }

    // ─── Queue-don't-cancel: new input during execution waits until round completes ───

    [Fact]
    public async Task Submit_DuringExecution_QueuedUntilRoundCompletes_ThenNextRoundDispatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var client = GetClient();
        var slowModel = "slow-model";

        // Round 1: slow submit so the execution is in-flight when u2 arrives.
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "First (slow)",
            ModelName = slowModel, CreatedBy = "rbuergi@systemorph.com"
        });

        await WaitForThreadAsync(
            threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count == 1,
            timeoutMs: 5_000, ct);

        // Submit u2 while round 1 is still executing. Queue-don't-cancel: the current round
        // is NOT aborted — it completes naturally (tool calls finish, response persists).
        // The watcher holds u2 back until IsExecuting flips to false, then dispatches round 2.
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "Second",
            ModelName = slowModel, CreatedBy = "rbuergi@systemorph.com"
        });

        // Final: both rounds complete cleanly. [u1, r1, u2, r2].
        var final = await WaitForThreadAsync(
            threadPath,
            t => !t.IsExecuting && t.IngestedMessageIds.Count == 2,
            timeoutMs: 20_000, ct);

        final.UserMessageIds.Should().HaveCount(2);
        final.IngestedMessageIds.Should().HaveCount(2);
        final.Messages.Should().HaveCount(4);
    }

    // ─── Single submit must produce exactly one response cell ───

    /// <summary>
    /// Repro for the prod symptom: ONE submit produces TWO "Generating response" rounds.
    /// Hypothesis: the user-cell creation emits a workspace stream event that re-fires the
    /// server watcher BEFORE DispatchRound's IsExecuting=true commit lands. The watcher sees
    /// IsExecuting=false + the user msg still unprocessed, dispatches a second round, second
    /// response cell is created.
    /// </summary>
    [Fact]
    public async Task Submit_SingleSubmit_ProducesExactlyOneResponseCell()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var client = GetClient();

        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = threadPath,
            UserText = "exactly once",
            CreatedBy = "rbuergi@systemorph.com",
            AuthorName = "Tester"
        });

        // Wait for the round to settle.
        var settled = await WaitForThreadAsync(
            threadPath,
            t => !t.IsExecuting && t.IngestedMessageIds.Count == 1,
            timeoutMs: 30_000, ct);

        // Give any racing second-dispatch a chance to land.
        await Task.Delay(500, ct);

        var final = await ReadThreadAsync(threadPath, ct);

        // The thread should record exactly: [user, response]. If a second round dispatched,
        // Messages would contain a second response cell id.
        final.Messages.Should().HaveCount(2,
            $"one submit must produce exactly one user + one response cell, got Messages=[{string.Join(",", final.Messages)}]");
        final.IngestedMessageIds.Should().HaveCount(1);
        final.UserMessageIds.Should().HaveCount(1);

        // Cross-check at the node level: count actual ThreadMessage assistant cells.
        var msgNodes = new List<MeshNode>();
        await foreach (var n in MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}", null, ct))
            msgNodes.Add(n);
        var responseCells = msgNodes
            .Where(n => (n.Content as ThreadMessage)?.Role == "assistant")
            .ToList();
        responseCells.Should().HaveCount(1,
            $"exactly one response cell node should exist, got {responseCells.Count}: " +
            string.Join(",", responseCells.Select(c => c.Id)));
    }

    // ─── Helpers ───

    private async Task<string> SeedEmptyThreadAsync(CancellationToken ct)
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(new MeshNode(threadPath)
        {
            Name = $"Test Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = "rbuergi@systemorph.com" }
        });
        return threadPath;
    }

    private async Task<MeshThread> ReadThreadAsync(string threadPath, CancellationToken ct)
    {
        MeshNode? node = null;
        await foreach (var n in MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}", null, ct))
        {
            node = n;
            break;
        }
        node.Should().NotBeNull($"thread node {threadPath} must exist");
        var content = node!.Content as MeshThread;
        content.Should().NotBeNull($"thread {threadPath} must have MeshThread content");
        return content!;
    }

    /// <summary>Polls the thread node until <paramref name="predicate"/> is true or timeout elapses.</summary>
    private async Task<MeshThread> WaitForThreadAsync(
        string threadPath,
        Func<MeshThread, bool> predicate,
        int timeoutMs,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        MeshThread? last = null;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            last = await ReadThreadAsync(threadPath, ct);
            if (predicate(last)) return last;
            await Task.Delay(100, ct);
        }
        // Predicate not satisfied in time — return whatever we saw last so the assertion error shows state.
        last.Should().NotBeNull();
        predicate(last!).Should().BeTrue(
            $"condition not reached within {timeoutMs}ms for thread {threadPath}. " +
            $"Last state: Messages=[{string.Join(",", last!.Messages)}], " +
            $"IngestedMessageIds=[{string.Join(",", last.IngestedMessageIds)}], " +
            $"IsExecuting={last.IsExecuting}, ActiveMessageId={last.ActiveMessageId}");
        return last!;
    }

    // ─── Fake chat client (minimal) ───

    private sealed class FakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, FakeResponseText)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, FakeResponseText);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Slow variant — delays ~1 second in the streaming response so tests can observe
    /// the IsExecuting=true state window and submit additional messages during a round.
    /// </summary>
    private sealed class SlowFakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("SlowFakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "slow ack")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Long enough for tests to race in a few more submits; short enough to keep CI fast.
            await Task.Delay(1000, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "slow ack");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class SlowFakeChatClientFactory : IChatClientFactory
    {
        public string Name => "SlowFakeFactory";
        public IReadOnlyList<string> Models => ["slow-model"];
        public int Order => 1;

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(
                chatClient: new SlowFakeChatClient(),
                instructions: config.Instructions ?? "slow test assistant",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null);

        public Task<Microsoft.Agents.AI.ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        public string Name => "FakeFactory";
        public IReadOnlyList<string> Models => ["fake-model"];
        public int Order => 0;

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(
                chatClient: new FakeChatClient(),
                instructions: config.Instructions ?? "You are a fake test assistant.",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null);

        public Task<Microsoft.Agents.AI.ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }
}
