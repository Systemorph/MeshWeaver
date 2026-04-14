#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
            timeoutMs: 10_000,
            ct);

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
            timeoutMs: 15_000,
            ct);

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
            timeoutMs: 15_000,
            ct);

        // Give the watcher a moment to finish any further rounds, then assert final state:
        // All three user messages should end up ingested; the dispatched round(s) produced >=1 output cell.
        await WaitForThreadAsync(
            threadPath,
            t => t.IngestedMessageIds.Count == 3,
            timeoutMs: 10_000,
            ct);

        var final = await ReadThreadAsync(threadPath, ct);
        final.IngestedMessageIds.Should().HaveCount(3, "all three user messages should be ingested");
        // All three user message ids appear as the first three in Messages.
        var userIds = final.Messages.Take(3).ToList();
        final.IngestedMessageIds.Should().BeEquivalentTo(userIds);
    }

    // ─── Helpers ───

    private async Task<string> SeedEmptyThreadAsync(CancellationToken ct)
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNodeAsync(new MeshNode(threadPath)
        {
            Name = $"Test Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = "rbuergi@systemorph.com" }
        }, ct);
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
