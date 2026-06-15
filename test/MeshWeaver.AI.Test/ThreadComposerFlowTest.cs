#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the persisted-composer submission flow:
/// <list type="number">
///   <item><description><see cref="HubThreadExtensions.StartThread"/> with a composer snapshot
///   creates the thread under the CONTEXT namespace and copies the composer onto the thread
///   (<see cref="MeshThread.Composer"/>) with the draft emptied.</description></item>
///   <item><description><see cref="HubThreadExtensions.SubmitComposer"/> drains the thread
///   composer's draft into <see cref="MeshThread.PendingUserMessages"/> and empties the
///   composer in ONE atomic update; the message carries the composer's selection (as picked
///   node PATHS).</description></item>
///   <item><description>The execution boundary accepts selection PATHS — ids are normalized
///   via <see cref="SelectionId.IdOf"/> (<see cref="HarnessNodeType.ResolveHarness"/> resolves
///   <c>Harness/MeshWeaver</c> and <c>MeshWeaver</c> alike).</description></item>
///   <item><description><see cref="ThreadComposer"/> equality is value-based with attachments
///   compared by sequence — the guard that keeps the composer binding echo-dedup (and thus the
///   no-write-loop property) sound.</description></item>
/// </list>
/// </summary>
public class ThreadComposerFlowTest : AITestBase
{
    public ThreadComposerFlowTest(ITestOutputHelper output) : base(output) { }

    // Share Mesh/SP across [Fact]s.
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
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    // ─── StartThread: composer copy + context namespace ───

    [Fact]
    public async Task StartThread_WithComposer_CreatesUnderContext_AndEmbedsEmptiedComposer()
    {
        var client = GetClient();
        var threadCreated = new System.Reactive.Subjects.AsyncSubject<MeshNode>();

        // Selection as picked node PATHS — exactly what the data-bound pickers store.
        var composer = new ThreadComposer
        {
            MessageContent = "the draft that becomes the first message",
            Harness = $"{HarnessNodeType.RootNamespace}/{Harnesses.MeshWeaver}",
            AgentName = "Agent/Assistant",
            ModelName = "_Provider/Fake/fake-model",
            ContextPath = MonolithMeshTestBase.TestPartition,
            Attachments = ImmutableList.Create("TestData/SomeDoc"),
            OpenThreadPath = "stale/_Thread/previous" // must never carry onto the thread
        };

        client.StartThread(
            namespacePath: MonolithMeshTestBase.TestPartition,
            userText: composer.MessageContent!,
            agentName: composer.AgentName,
            modelName: composer.ModelName,
            harness: composer.Harness,
            contextPath: composer.ContextPath,
            createdBy: "rbuergi@systemorph.com",
            composer: composer,
            onCreated: node => { threadCreated.OnNext(node); threadCreated.OnCompleted(); });

        var created = await threadCreated.Should().Emit();
        created!.Path.Should().StartWith(
            $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/",
            "the thread must live under {context}/_Thread/{speakingId}");

        var thread = (created.Content as MeshThread)!;
        thread.Composer.Should().NotBeNull("StartThread must copy the composer onto the thread");
        thread.Composer!.MessageContent.Should().BeNull("the draft was consumed by the first message");
        thread.Composer.Attachments.Should().BeNull("attachments were consumed by the first message");
        thread.Composer.OpenThreadPath.Should().BeNull("the navigate signal never carries onto the thread");
        thread.Composer.Harness.Should().Be(composer.Harness, "selection survives as the picked path");
        thread.Composer.AgentName.Should().Be(composer.AgentName);
        thread.Composer.ModelName.Should().Be(composer.ModelName);

        // The round still dispatches normally (selection paths are normalized at execution).
        var committed = await WaitForThread(
            created.Path!,
            t => t.IngestedMessageIds.Count >= 1 && t.Messages.Count >= 2,
            timeoutMs: 30_000);
        committed.Messages.Should().HaveCount(2, "one user cell + one output cell");
    }

    // ─── SubmitComposer: drain + empty, one atomic update ───

    [Fact]
    public async Task SubmitComposer_DrainsDraft_CarriesSelection_AndEmptiesComposer()
    {
        var threadPath = await SeedThreadWithComposer(new ThreadComposer
        {
            MessageContent = "typed into the composer",
            Harness = $"{HarnessNodeType.RootNamespace}/{Harnesses.MeshWeaver}",
            AgentName = "Agent/Assistant",
            ModelName = "_Provider/Fake/fake-model",
            Attachments = ImmutableList.Create("TestData/Chip")
        });
        var client = GetClient();

        client.SubmitComposer(threadPath, createdBy: "rbuergi@systemorph.com", authorName: "Tester");

        // ONE atomic update: the draft is queued AND the composer is emptied together —
        // any emission with the message queued must already show the emptied composer.
        var queued = await WaitForThread(
            threadPath,
            t => t.UserMessageIds.Count >= 1,
            timeoutMs: 15_000);
        queued.Composer.Should().NotBeNull();
        queued.Composer!.MessageContent.Should().BeNull("the composer empties itself on submit");
        queued.Composer.Attachments.Should().BeNull("attachments are consumed by the message");
        queued.Composer.AgentName.Should().Be("Agent/Assistant", "selection is kept for the next round");

        // The round ingests and the materialised user cell carries the composer's
        // text + selection (as the picked paths — normalized only at execution).
        var committed = await WaitForThread(
            threadPath,
            t => t.IngestedMessageIds.Count >= 1 && t.Messages.Count >= 2,
            timeoutMs: 30_000);
        var userCell = await ReadNode($"{threadPath}/{committed.Messages[0]}").Should().Emit();
        var userMessage = (userCell!.Content as ThreadMessage)!;
        userMessage.Text.Should().Be("typed into the composer");
        userMessage.AgentName.Should().Be("Agent/Assistant");
        userMessage.Harness.Should().Be($"{HarnessNodeType.RootNamespace}/{Harnesses.MeshWeaver}");
        userMessage.Attachments.Should().Equal("TestData/Chip");
    }

    [Fact]
    public async Task SubmitComposer_ExplicitText_OverridesDraft()
    {
        var threadPath = await SeedThreadWithComposer(new ThreadComposer { MessageContent = "stale draft" });
        var client = GetClient();

        client.SubmitComposer(threadPath, userText: "typed live in monaco",
            createdBy: "rbuergi@systemorph.com");

        var committed = await WaitForThread(
            threadPath,
            t => t.IngestedMessageIds.Count >= 1 && t.Messages.Count >= 2,
            timeoutMs: 30_000);
        var userCell = await ReadNode($"{threadPath}/{committed.Messages[0]}").Should().Emit();
        (userCell!.Content as ThreadMessage)!.Text.Should().Be("typed live in monaco");
        committed.Composer!.MessageContent.Should().BeNull("draft is cleared even when explicit text was sent");
    }

    [Fact]
    public async Task SubmitComposer_NothingToSubmit_IsANoOp()
    {
        var threadPath = await SeedThreadWithComposer(new ThreadComposer());
        var client = GetClient();

        client.SubmitComposer(threadPath, createdBy: "rbuergi@systemorph.com");

        // Negative test — no positive signal to filter for; sanctioned "confirm nothing
        // happened" delay (CLAUDE.md → Task.Delay exceptions).
        await Task.Delay(750, TestContext.Current.CancellationToken);
        var thread = await ReadThread(threadPath);
        thread.UserMessageIds.Should().BeEmpty("empty composer + no text must not queue anything");
        thread.PendingUserMessages.Should().BeEmpty();
    }

    // ─── Path-aware selection resolution ───

    [Fact]
    public void ResolveHarness_AcceptsPathAndBareId()
    {
        var byId = HarnessNodeType.ResolveHarness(Mesh.ServiceProvider, Harnesses.MeshWeaver);
        var byPath = HarnessNodeType.ResolveHarness(
            Mesh.ServiceProvider, $"{HarnessNodeType.RootNamespace}/{Harnesses.MeshWeaver}");

        byId.Should().NotBeNull();
        byPath.Should().NotBeNull("a picked node path must resolve like the bare id");
        byPath!.Id.Should().Be(byId!.Id);
    }

    [Fact]
    public void SelectionId_IdOf_NormalizesPathsAndPassesIdsThrough()
    {
        SelectionId.IdOf(null).Should().BeNull();
        SelectionId.IdOf("").Should().Be("");
        SelectionId.IdOf("claude-sonnet-4-6").Should().Be("claude-sonnet-4-6");
        SelectionId.IdOf("_Provider/Anthropic/claude-sonnet-4-6").Should().Be("claude-sonnet-4-6");
        SelectionId.IdOf("Agent/Coder").Should().Be("Coder");
    }

    // ─── Composer value equality (the echo-dedup guard) ───

    [Fact]
    public void ThreadComposer_Equality_ComparesAttachmentsBySequence()
    {
        var a = new ThreadComposer
        {
            MessageContent = "x",
            Attachments = ImmutableList.Create("p/1", "p/2")
        };
        var b = a with { Attachments = ImmutableList.Create("p/1", "p/2") }; // distinct instance

        a.Equals(b).Should().BeTrue("equal-content attachment lists must compare equal (echo dedup)");
        a.GetHashCode().Should().Be(b.GetHashCode());

        var c = a with { Attachments = ImmutableList.Create("p/1") };
        a.Equals(c).Should().BeFalse();
        var d = a with { OpenThreadPath = "t/_Thread/x" };
        a.Equals(d).Should().BeFalse();
    }

    // ─── Helpers ───

    private async Task<string> SeedThreadWithComposer(ThreadComposer composer)
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Composer Test Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread
            {
                CreatedBy = "rbuergi@systemorph.com",
                Composer = composer
            }
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
        string threadPath,
        Func<MeshThread, bool> predicate,
        int timeoutMs)
        => (await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(t => predicate(t!)))!;

    // ─── Fake chat client (minimal) ───

    private sealed class FakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "composer fake ack")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "composer fake ack");
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
