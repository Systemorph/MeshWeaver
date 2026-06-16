#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.AI.Parsing;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
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
/// Regression tests for the "executing a slash command / submitting empty text storms and crashes"
/// bug. Selecting an agent or running a chat command must NOT run an agent round: the command text is
/// cut out of the input, and what remains is empty — so there is NOTHING to submit. If a round were
/// dispatched anyway it would reach <c>CreateChatClient</c> with no input and storm
/// (<c>AgentChatClient.CreateAgentsSync</c> logs "No model selected" once per agent).
///
/// <para>The fix is twofold: (1) the submission surface (<see cref="HubThreadExtensions.SubmitMessage"/>
/// / <see cref="HubThreadExtensions.StartThread"/>) treats whitespace-only text as nothing-to-do and
/// enqueues no round; (2) the round itself (<c>ThreadExecution.ExecuteMessageAsync</c>) finishes
/// gracefully when there is genuinely nothing to send (no user turn AND no history) instead of calling
/// the model.</para>
/// </summary>
public class ChatCommandSubmissionTest : AITestBase
{
    private const string FakeResponseText = "fake agent ack";

    public ChatCommandSubmissionTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => true;

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
        return base.ConfigureClient(configuration);
    }

    // ─── (D) Parser cuts the command, leaving nothing to send ───

    /// <summary>
    /// A standalone slash command (the /agent /model /harness picker) is fully consumed by the
    /// pre-parser: the processed text is empty and <see cref="ParsedChatMessage.ShouldSendToAI"/> is
    /// false. This is the "command is cut out → nothing remains to submit" contract.
    /// </summary>
    [Theory]
    [InlineData("/agent")]
    [InlineData("/agent Worker")]
    [InlineData("/model claude-sonnet")]
    [InlineData("/harness MeshWeaver")]
    public void Command_IsCutFromInput_LeavingNothingToSend(string text)
    {
        var parsed = new ChatPreParser().Parse(text);
        parsed.Command.Should().NotBeNull();
        parsed.ProcessedText.Should().BeEmpty("the command text is cut out");
        parsed.ShouldSendToAI.Should().BeFalse("a picker command never submits a message round");
    }

    // ─── (D) Empty / whitespace submission → no round, finishes with nothing to do ───

    /// <summary>
    /// Submitting whitespace-only text into an existing thread (what's left after a command is cut)
    /// is a no-op: the submission watcher never claims a round, so the thread never executes and no
    /// cells are materialised. The reactive form of "wait to confirm nothing happened".
    /// </summary>
    [Fact]
    public async Task SubmitMessage_WhitespaceOnly_NoRoundRuns()
    {
        var threadPath = await SeedEmptyThread();
        var client = GetClient();

        client.SubmitMessage(
            threadPath,
            "   ",
            createdBy: "rbuergi@systemorph.com",
            authorName: "Tester");

        // No round may ever start: no execution, no pending message, no message cells.
        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is not null
                        && (t!.IsExecuting
                            || t.Messages.Count > 0
                            || t.PendingUserMessages.Count > 0
                            || t.UserMessageIds.Count > 0))
            .Should().NotEmit(750.Milliseconds());

        var final = await ReadThread(threadPath);
        final.Messages.Should().BeEmpty("nothing to submit → no round, no cells");
        final.PendingUserMessages.Should().BeEmpty();
        final.IsExecuting.Should().BeFalse();
    }

    /// <summary>
    /// Starting a thread with whitespace-only first text (a command consumed the input) creates the
    /// thread — so the composer selection has somewhere to live — but seeds NO pending message, so the
    /// submission watcher dispatches no round.
    /// </summary>
    [Fact]
    public async Task StartThread_WhitespaceOnly_CreatesThread_ButRunsNoRound()
    {
        var threadCreated = new System.Reactive.Subjects.AsyncSubject<MeshNode>();
        var client = GetClient();

        client.StartThread(
            MonolithMeshTestBase.TestPartition,
            "   ",
            agentName: "Assistant",
            createdBy: "rbuergi@systemorph.com",
            onCreated: node => { threadCreated.OnNext(node); threadCreated.OnCompleted(); });

        var created = await threadCreated.Should().Emit();
        var threadPath = created.Path!;

        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is not null
                        && (t!.IsExecuting || t.Messages.Count > 0 || t.PendingUserMessages.Count > 0))
            .Should().NotEmit(750.Milliseconds());

        var final = await ReadThread(threadPath);
        final.Messages.Should().BeEmpty("whitespace-only first message seeds no round");
        final.PendingUserMessages.Should().BeEmpty();
        final.IsExecuting.Should().BeFalse();
    }

    // Note: the "real message still runs after an empty one" non-regression is intentionally NOT
    // asserted here. Exercising a FULL round requires the per-thread submission watcher, which is
    // wired by the StartThread lifecycle — not by a raw CreateNode (SeedEmptyThread). Driving a real
    // round end-to-end belongs in the integration submission tests; the guards added here only short-
    // circuit EMPTY/whitespace input, so they cannot affect a non-empty submission. The empty-input
    // contract is covered above (and by StartThread_WhitespaceOnly, which uses the real lifecycle).

    // ─── Helpers ───

    private async Task<string> SeedEmptyThread()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Test Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = "rbuergi@systemorph.com" }
        }).Should().Emit();
        return threadPath;
    }

    private async Task<MeshThread> ReadThread(string threadPath)
    {
        var node = await ReadNode(threadPath).Should().Emit();
        node.Should().NotBeNull($"thread node {threadPath} must exist");
        var content = node!.Content as MeshThread;
        content.Should().NotBeNull($"thread {threadPath} must have MeshThread content");
        return content!;
    }

    private async Task<MeshThread> WaitForThread(
        string threadPath, Func<MeshThread, bool> predicate, int timeoutMs)
        => (await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(t => predicate(t!)))!;

    // ─── Fake chat client (minimal — same as ThreadSubmissionIntegrationTest) ───

    private sealed class FakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, FakeResponseText)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            System.Threading.CancellationToken cancellationToken = default)
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
            AgentConfiguration config, IAgentChat chat,
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
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }
}
