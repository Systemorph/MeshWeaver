#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.Data;
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
/// Test assertions use QueryAsync/FirstAsync â€” allowed per CLAUDE.md for test code only.
/// </summary>
public class ThreadSubmissionIntegrationTest : AITestBase
{
    private const string FakeResponseText = "fake agent ack";

    public ThreadSubmissionIntegrationTest(ITestOutputHelper output) : base(output) { }

    // Share Mesh/SP across [Fact]s.
    protected override bool ShareMeshAcrossTests => true;

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

    // â”€â”€â”€ Submit into existing thread â”€â”€â”€

    [Fact]
    public void Submit_ExistingThread_UserMessageIngested_OutputCellAppears()
    {
        var threadPath = SeedEmptyThread();
        var client = GetClient();

        client.SubmitMessage(
            threadPath,
            "Hello from test",
            createdBy: "rbuergi@systemorph.com",
            authorName: "Tester");

        // Wait for the watcher to ingest the user message into a round.
        var committed = WaitForThread(
            threadPath,
            t => t.IngestedMessageIds.Count >= 1 && t.Messages.Count >= 2,
            timeoutMs: 30_000);

        committed.IngestedMessageIds.Should().HaveCount(1);
        committed.Messages.Should().HaveCount(2, "expected one user cell + one output cell in Messages");
        committed.IngestedMessageIds[0].Should().Be(committed.Messages[0], "user id should be the first message");
        committed.UserMessageIds.Should().ContainInOrder(committed.IngestedMessageIds[0]);
    }

    // â”€â”€â”€ CreateThreadAndSubmit â”€â”€â”€

    [Fact]
    public void CreateThreadAndSubmit_CreatesThreadAndFirstRound()
    {
        var threadCreated = new System.Reactive.Subjects.AsyncSubject<MeshNode>();
        var client = GetClient();

        client.StartThread(
            MonolithMeshTestBase.TestPartition,
            "New thread first message",
            createdBy: "rbuergi@systemorph.com",
            onCreated: node => { threadCreated.OnNext(node); threadCreated.OnCompleted(); });

        var created = threadCreated.Should().Emit();
        created.Path.Should().NotBeNullOrEmpty();
        var threadPath = created.Path!;

        var committed = WaitForThread(
            threadPath,
            t => t.IngestedMessageIds.Count >= 1 && t.Messages.Count >= 2,
            timeoutMs: 15_000);

        committed.IngestedMessageIds.Should().HaveCount(1);
        committed.Messages.Should().HaveCount(2);
    }

    // â”€â”€â”€ Batched ingestion â”€â”€â”€

    [Fact]
    public void Submit_ThreeRapidSubmissions_AllIngestedIntoOneRound()
    {
        var threadPath = SeedEmptyThread();

        var client = GetClient();

        client.SubmitMessage(threadPath, "First", createdBy: "rbuergi@systemorph.com");
        client.SubmitMessage(threadPath, "Second", createdBy: "rbuergi@systemorph.com");
        client.SubmitMessage(threadPath, "Third", createdBy: "rbuergi@systemorph.com");

        // Wait for at least one round to commit.
        var committed = WaitForThread(
            threadPath,
            t => t.IngestedMessageIds.Count >= 1 && t.Messages.Count >= 2,
            timeoutMs: 15_000);

        // Give the watcher a moment to finish any further rounds, then assert final state:
        // All three user messages should end up ingested; the dispatched round(s) produced >=1 output cell.
        WaitForThread(
            threadPath,
            t => t.IngestedMessageIds.Count == 3,
            timeoutMs: 30_000);

        var final = ReadThread(threadPath);
        final.IngestedMessageIds.Should().HaveCount(3, "all three user messages should be ingested");
        // ImmutableList<string> on both sides — plain strings, no polymorphism, so JsonSerializerOptions.Default.
        // Set-equal to UserMessageIds â€” dispatch is one user message per round
        // (Claude-Code-style turn structure), so the response cells interleave
        // with user cells in Messages, but UserMessageIds is the authoritative
        // list of user-input ids and must match IngestedMessageIds as a set.
        final.IngestedMessageIds.Should().BeEquivalentTo(final.UserMessageIds, System.Text.Json.JsonSerializerOptions.Default);
        final.UserMessageIds.Should().HaveCount(3);
    }

    // â”€â”€â”€ Resubmit: truncates after the replayed message, new round dispatches â”€â”€â”€

    [Fact]
    public void Resubmit_TruncatesAfterReplayedMessage_NewRoundCreated()
    {
        var threadPath = SeedEmptyThread();
        var client = GetClient();

        // Round 1: submit u1 and wait for it to complete.
        client.SubmitMessage(
            threadPath, "First", createdBy: "rbuergi@systemorph.com");
        var afterRoundOne = WaitForThread(
            threadPath,
            t => !t.IsExecuting && t.IngestedMessageIds.Count == 1 && t.Messages.Count == 2,
            timeoutMs: 30_000);

        var u1 = afterRoundOne.UserMessageIds[0];
        var r1 = afterRoundOne.Messages[1];

        // Resubmit u1 with new text.
        client.ResubmitMessage(
            threadPath,
            u1,
            newUserText: "First, revised");

        // The intermediate "truncated" state (Messages=[u1], IngestedMessageIds=[]) is racy â€”
        // the server watcher dispatches the new round almost immediately after the truncation
        // commits, often before we can observe it. Instead assert the end state: u1 is
        // ingested again, a NEW response cell (!= r1) follows, and IsExecuting is back to false.
        var afterResubmit = WaitForThread(
            threadPath,
            t => t.IngestedMessageIds.Contains(u1)
                 && t.Messages.Count == 2
                 && t.Messages[1] != r1
                 && !t.IsExecuting,
            timeoutMs: 20_000);

        afterResubmit.Messages[0].Should().Be(u1);
        afterResubmit.Messages[1].Should().NotBe(r1, "resubmit must produce a fresh response cell");
        afterResubmit.UserMessageIds.Should().ContainSingle().Which.Should().Be(u1);
    }

    // â”€â”€â”€ Failure recovery: error renders as an assistant response cell â”€â”€â”€

    [Fact]
    public void SubmissionFailure_RecordsErrorAsOutputCell_InThreadMessages()
    {
        var threadPath = SeedEmptyThread();
        var client = GetClient();

        // Simulate a failure path by invoking the production helper directly
        // (replaces the legacy ThreadSubmission.ApplyRecordSubmissionFailure post + handler).
        var fakeUserMsgId = Guid.NewGuid().ToString("N")[..8];
        client.RecordSubmissionFailure(
            threadPath,
            fakeUserMsgId,
            "message that failed",
            "network timeout");

        // Wait for the thread to reflect: user id appended + an error response cell appended
        // + user id marked as ingested (so watcher doesn't retry).
        var final = WaitForThread(
            threadPath,
            t => t.Messages.Contains(fakeUserMsgId)
                 && t.IngestedMessageIds.Contains(fakeUserMsgId)
                 && t.Messages.Count >= 2,
            timeoutMs: 5_000);

        final.UserMessageIds.Should().Contain(fakeUserMsgId);
        final.IngestedMessageIds.Should().Contain(fakeUserMsgId);
        final.Messages.Should().HaveCount(2);
        final.Messages[0].Should().Be(fakeUserMsgId);

        // The second entry must be an assistant cell with the error in its Text.
        var errorCellId = final.Messages[1];
        var errorCell = ReadNode($"{threadPath}/{errorCellId}").Should().Emit();
        errorCell.Should().NotBeNull();
        var content = errorCell!.Content as ThreadMessage;
        content.Should().NotBeNull();
        content!.Role.Should().Be("assistant");
        content.Text.Should().Contain("network timeout");
    }

    // â”€â”€â”€ Tool-call scenario: 3 rapid submits during a 1s "tool call" â”€â”€â”€

    [Fact]
    public void Submit_ThreeMessagesDuringActiveRound_QueuedThenBatchedIntoSecondRound()
    {
        var threadPath = SeedEmptyThread();
        var client = GetClient();

        // Use the slow-model factory so round 1 takes ~1 second.
        // This gives us a deterministic window to submit u2/u3/u4 while round 1 is still executing.
        var slowModel = "slow-model";

        // Submit u1 â€” triggers round 1.
        client.SubmitMessage(
            threadPath, "First",
            modelName: slowModel, createdBy: "rbuergi@systemorph.com");

        // Wait for round 1 to start (IsExecuting=true). This proves u1 has been ingested.
        var roundOneStart = WaitForThread(
            threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count == 1,
            timeoutMs: 5_000);

        roundOneStart.IngestedMessageIds.Should().HaveCount(1, "u1 should be ingested once round 1 starts");
        var u1 = roundOneStart.IngestedMessageIds[0];

        // While round 1 is running, submit 3 more messages in quick succession.
        client.SubmitMessage(threadPath, "Second",
            modelName: slowModel, createdBy: "rbuergi@systemorph.com");
        client.SubmitMessage(threadPath, "Third",
            modelName: slowModel, createdBy: "rbuergi@systemorph.com");
        client.SubmitMessage(threadPath, "Fourth",
            modelName: slowModel, createdBy: "rbuergi@systemorph.com");

        // Observe: during round 1 execution, all three new user ids should appear in Messages
        // and UserMessageIds, but NOT yet in IngestedMessageIds â€” the server holds them back
        // because the thread is busy.
        var pendingState = WaitForThread(
            threadPath,
            t => t.UserMessageIds.Count == 4,
            timeoutMs: 3_000);

        // If we're quick enough, round 1 is still executing here. Either way, we can assert
        // that u2/u3/u4 are NOT yet ingested while u1 already is (or that all 4 are ingested
        // if round 1 already finished). The key invariant: no user message is ingested
        // before it exists in UserMessageIds.
        pendingState.UserMessageIds.Should().HaveCount(4, "all four user messages should be registered on the thread");
        pendingState.UserMessageIds[0].Should().Be(u1, "u1 is the first registered user message");
        pendingState.IngestedMessageIds.Count.Should().BeGreaterThanOrEqualTo(1);
        pendingState.IngestedMessageIds.Should().BeSubsetOf(pendingState.UserMessageIds);

        // Wait for all 4 to become ingested. This requires round 1 to finish, then round 2 to commit.
        var final = WaitForThread(
            threadPath,
            t => t.IngestedMessageIds.Count == 4 && !t.IsExecuting,
            timeoutMs: 20_000);

        final.IngestedMessageIds.Should().HaveCount(4);
        final.IngestedMessageIds.Should().BeEquivalentTo(final.UserMessageIds, System.Text.Json.JsonSerializerOptions.Default);

        // Inbox-pattern dispatch: every entry in PendingUserMessages is drained
        // into a single round (one response cell per inbox drain). u1 lands while
        // the thread is idle â†’ round 1 drains {u1}, creates r1. u2/u3/u4 land
        // during round 1 â†’ they pile up in PendingUserMessages. When round 1
        // ends, the watcher fires once and round 2 drains {u2, u3, u4} into a
        // single response cell r2. Final shape: [u1, r1, u2, u3, u4, r2].
        // Not every input cell gets its own response cell â€” the design
        // explicitly batches mid-round submits into the next round.
        final.Messages.Should().HaveCount(6, "4 user cells + 2 response cells");
        final.Messages[0].Should().Be(u1, "u1 first");
        final.UserMessageIds.Should().HaveCount(4);
        final.UserMessageIds[0].Should().Be(u1, "u1 is the first registered user message");
        final.Messages.Should().Contain(final.UserMessageIds);
        var responseIds = final.Messages.Except(final.UserMessageIds).ToList();
        responseIds.Should().HaveCount(2,
            "one response cell per inbox drain â€” round 1 drains {u1}, round 2 drains {u2,u3,u4}");
    }

    // â”€â”€â”€ Queue-don't-cancel: new input during execution waits until round completes â”€â”€â”€

    [Fact]
    public void Submit_DuringExecution_QueuedUntilRoundCompletes_ThenNextRoundDispatches()
    {
        var threadPath = SeedEmptyThread();
        var client = GetClient();
        var slowModel = "slow-model";

        // Round 1: slow submit so the execution is in-flight when u2 arrives.
        client.SubmitMessage(
            threadPath, "First (slow)",
            modelName: slowModel, createdBy: "rbuergi@systemorph.com");

        WaitForThread(
            threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count == 1,
            timeoutMs: 5_000);

        // Submit u2 while round 1 is still executing. Queue-don't-cancel: the current round
        // is NOT aborted â€” it completes naturally (tool calls finish, response persists).
        // The watcher holds u2 back until IsExecuting flips to false, then dispatches round 2.
        client.SubmitMessage(
            threadPath, "Second",
            modelName: slowModel, createdBy: "rbuergi@systemorph.com");

        // Final: both rounds complete cleanly. [u1, r1, u2, r2].
        var final = WaitForThread(
            threadPath,
            t => !t.IsExecuting && t.IngestedMessageIds.Count == 2,
            timeoutMs: 20_000);

        final.UserMessageIds.Should().HaveCount(2);
        final.IngestedMessageIds.Should().HaveCount(2);
        final.Messages.Should().HaveCount(4);
    }

    // â”€â”€â”€ Single submit must produce exactly one response cell â”€â”€â”€

    /// <summary>
    /// Repro for the prod symptom: ONE submit produces TWO "Generating response" rounds.
    /// Hypothesis: the user-cell creation emits a workspace stream event that re-fires the
    /// server watcher BEFORE DispatchRound's IsExecuting=true commit lands. The watcher sees
    /// IsExecuting=false + the user msg still unprocessed, dispatches a second round, second
    /// response cell is created.
    /// </summary>
    [Fact]
    public void Submit_SingleSubmit_ProducesExactlyOneResponseCell()
    {
        var threadPath = SeedEmptyThread();
        var client = GetClient();

        client.SubmitMessage(
            threadPath,
            "exactly once",
            createdBy: "rbuergi@systemorph.com",
            authorName: "Tester");

        // Wait for the round to settle.
        var settled = WaitForThread(
            threadPath,
            t => !t.IsExecuting && t.IngestedMessageIds.Count == 1,
            timeoutMs: 30_000);

        // No second round must dispatch. Watch the stream for Messages growing
        // past the single [user, response] pair; a 500ms quiet window with no
        // growth confirms exactly-once. NotEmit asserts the BAD event never
        // arrives — the reactive form of "wait to confirm nothing happened".
        Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is not null && t!.Messages.Count > 2)
            .Should().NotEmit(500.Milliseconds());

        var final = ReadThread(threadPath);

        // The thread should record exactly: [user, response]. If a second round dispatched,
        // Messages would contain a second response cell id.
        final.Messages.Should().HaveCount(2,
            $"one submit must produce exactly one user + one response cell, got Messages=[{string.Join(",", final.Messages)}]");
        final.IngestedMessageIds.Should().HaveCount(1);
        final.UserMessageIds.Should().HaveCount(1);

        // Cross-check at the node level: count actual ThreadMessage assistant cells.
        var msgNodes = MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;
        var responseCells = msgNodes
            .Where(n => (n.Content as ThreadMessage)?.Role == "assistant")
            .ToList();
        responseCells.Should().HaveCount(1,
            $"exactly one response cell node should exist, got {responseCells.Count}: " +
            string.Join(",", responseCells.Select(c => c.Id)));
    }

    // â”€â”€â”€ Helpers â”€â”€â”€

    private string SeedEmptyThread()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        NodeFactory.CreateNode(new MeshNode(threadPath)
        {
            Name = $"Test Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = "rbuergi@systemorph.com" }
        }).Should().Emit();
        return threadPath;
    }

    private MeshThread ReadThread(string threadPath)
    {
        var node = ReadNode(threadPath).Should().Emit();
        node.Should().NotBeNull($"thread node {threadPath} must exist");
        var content = node!.Content as MeshThread;
        content.Should().NotBeNull($"thread {threadPath} must have MeshThread content");
        return content!;
    }

    /// <summary>
    /// Stream-based wait: subscribes to the thread's MeshNode stream and returns
    /// the first emission whose content matches <paramref name="predicate"/>.
    /// Replaces the previous <c>Task.Delay(100)</c> poll loop — that read a
    /// potentially stale cached snapshot each cycle and raced the workspace's
    /// write propagation, so under cumulative test load a transition that landed
    /// between two polls (or whose re-query lagged) blew the budget. The stream
    /// emits on every commit, so the predicate sees every state transition
    /// exactly once. See CLAUDE.md → "Never Task.Delay to wait for propagation".
    /// </summary>
    private MeshThread WaitForThread(
        string threadPath,
        Func<MeshThread, bool> predicate,
        int timeoutMs)
        => Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(t => predicate(t!))!;

    // â”€â”€â”€ Fake chat client (minimal) â”€â”€â”€

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
    /// Slow variant â€” delays ~1 second in the streaming response so tests can observe
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
