using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Behaviour tests for <c>ThreadExecution.LoadFullConversationHistoryFromMesh</c>.
/// Three cases pinned by the loader's contract:
/// <list type="number">
///   <item>All cells have text → loader returns the full ordered list.</item>
///   <item>Some cells time out / are unreadable → loader logs a warning and
///     returns the partial list (the agent gets best-effort context).</item>
///   <item>Every expected cell fails → loader throws <see cref="TimeoutException"/>
///     instead of returning an empty list (refuses to submit a corrupt context).</item>
/// </list>
/// </summary>
public class LoadConversationHistoryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string ContextPath = "User/TestUser";
    private const string FakeResponse = "Test response.";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddData();
    }

    private async Task<string> CreateThread(IMessageHub client, string text)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "TestUser");
        var resp = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).Should().Within(60.Seconds()).Emit();
        resp.Message.Success.Should().BeTrue(resp.Message.Error ?? "");
        return resp.Message.Node!.Path!;
    }

    private async Task SubmitAndWaitForResponse(
        IMessageHub client, string threadPath, string text)
    {
        var responseMsgId = await ThreadFlow.SubmitAndWait(client, threadPath, text,
            contextPath: ContextPath).Should().Within(60.Seconds()).Emit();
        // Wait for the response cell to reach a TERMINAL status. Earlier we
        // gated on `!IsNullOrEmpty(m.Text)`, but ThreadExecution stamps the
        // placeholder "Generating response..." onto the cell text very early
        // in the streaming loop — that text passes the non-empty check while
        // the real response is still mid-stream, so the history assertion
        // later read the placeholder instead of the FakeResponse.
        await ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => m.Status is ThreadMessageStatus.Completed
                          or ThreadMessageStatus.Cancelled
                          or ThreadMessageStatus.Error).Should().Within(60.Seconds()).Emit();
    }

    // 60s timeout: two real ThreadFlow.SubmitAndWait calls + ReadThread predicate
    // waits — local runs ~3s, CI cold-start runs ~30s. Default 30s methodTimeout
    // tripped on CI (31.85s in run 26376715753).
    [Fact]
    public async Task AllCells_HaveText_ReturnsFullHistory()
    {
        var client = GetClient();
        var threadPath = await CreateThread(client, "Loader history happy path");

        await SubmitAndWaitForResponse(client, threadPath, "first question");
        await SubmitAndWaitForResponse(client, threadPath, "second question");

        // After two real rounds the thread has 4 cells (user+assistant per round).
        // Wait until the thread node sees IsExecuting=false AND Messages.Count >= 4
        // so the loader sees the fully-settled state.
        var thread = await ThreadFlow.ReadThread(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 4)
            .Should().Within(60.Seconds()).Emit();
        thread.Messages.Should().HaveCount(4);

        // The thread hub is the per-node hub for threadPath — that's the workspace
        // the loader queries via IMeshNodeStreamCache.
        var history = await ThreadExecution.LoadFullConversationHistoryFromMesh(
                Mesh, threadPath,
                excludeUserMessageId: null, excludeResponseMessageId: null,
                NullLogger.Instance,
                cellTimeout: 5.Seconds())
            .Should().Within(60.Seconds()).Emit();

        history.Should().HaveCount(4, "two rounds = 2 user + 2 assistant = 4 messages");
        history.Select(m => m.Role).Should().Equal(
            ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant);
        history.Select(m => m.Text!.TrimEnd()).Should().Equal(
            "first question", FakeResponse, "second question", FakeResponse);
    }

    [Fact]
    public async Task SomeCellsMissing_ReturnsPartialHistory_AndWarns()
    {
        var client = GetClient();
        var threadPath = await CreateThread(client, "Loader partial-history test");

        // Round 1: real submit → user+assistant cell, both with text.
        await SubmitAndWaitForResponse(client, threadPath, "real question");
        var threadAfterRound1 = await ThreadFlow.ReadThread(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 2)
            .Should().Within(60.Seconds()).Emit();
        threadAfterRound1.Messages.Should().HaveCount(2);

        // Append a phantom cell ID to Messages — no per-node hub will ever emit
        // content at threadPath/{phantom-id}, so the per-cell Timeout fires and
        // the cell is omitted from the result with a warning.
        var phantomCellId = "phantom-" + Guid.NewGuid().ToString("N")[..8];
        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            if (node.Content is not MeshThread t) return node;
            return node with { Content = t with { Messages = t.Messages.Add(phantomCellId) } };
        }).Should().Emit();

        var history = await ThreadExecution.LoadFullConversationHistoryFromMesh(
                Mesh, threadPath,
                excludeUserMessageId: null, excludeResponseMessageId: null,
                NullLogger.Instance,
                cellTimeout: 1.Seconds())
            .Should().Within(60.Seconds()).Emit();

        history.Should().HaveCount(2, "phantom cell never emits but the two real cells should still load");
        history.Select(m => m.Text!.TrimEnd()).Should().Equal("real question", FakeResponse);
    }

    /// <summary>
    /// 🚨 #2290 — the MECHANISM test. The three consequence tests above only catch this defect
    /// by TIMING (a degraded cell never satisfies the predicate, so it looks exactly like a slow
    /// one); this one catches it by CONSTRUCTION.
    ///
    /// <para>The loader used to filter cells with <c>n.Content is ThreadMessage m</c> — the
    /// trap-door AGENTS.md forbids by name. That matches only when the value ALREADY is that CLR
    /// type, and misses silently for the untyped-JSON form the polymorphic converter DEGRADES an
    /// unresolvable <c>$type</c> to, for the as-written DOM, and for a same-short-named record
    /// from another collectible build. A miss is not "one lost message": the predicate never
    /// matches → <c>Take(1)</c> never fires → the per-cell <c>Timeout</c> trips → the cell is
    /// dropped as <c>HISTORY_CELL_DROP</c>; and once EVERY cell degrades — a cold start, where
    /// grains are not activated and cells are not yet re-typed — the zero-loaded guard throws and
    /// (since #2240 correctly made that terminal) HARD-ERRORS the user's round.</para>
    ///
    /// <para>The thread node two reads earlier already used <c>ContentAs</c>; the cells did not.
    /// This test pins that they now do, and the mechanism guard below fails LOUD if the seeded
    /// content ever stops arriving degraded — otherwise the test would keep passing while
    /// testing nothing.</para>
    /// </summary>
    [Fact]
    public async Task DegradedCells_AreReadAsThreadMessages_AndReachTheHistory()
    {
        var client = GetClient();
        var threadPath = await CreateThread(client, "Loader degraded-content test");

        // Two cells whose Content is UNTYPED JSON — the shape the polymorphic converter degrades
        // an unresolvable `$type` to. In production the degradation is TRANSIENT (it lasts until
        // the reading hub can re-type the content, which on a cold start is exactly the window the
        // round runs in); here it is made permanent by seeding the cells without a NodeType, so
        // nothing re-types them and the loader is deterministically handed the degraded form.
        // Distinct timestamps pin the ordering the loader must preserve.
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        await SeedDegradedCell(client, threadPath, "degraded-user", "user", "degraded question", t0);
        await SeedDegradedCell(client, threadPath, "degraded-assistant", "assistant", "degraded answer", t0.AddMinutes(1));

        await AppendCellIds(client, threadPath, "degraded-user", "degraded-assistant");

        // 🚨 Mechanism guard: the seeded content MUST arrive as something other than a typed
        // ThreadMessage, or this test is asserting the happy path under a misleading name.
        var seededNode = await Mesh.GetWorkspace().GetMeshNodeStream($"{threadPath}/degraded-user")
            .Where(n => n.Content is not null)
            .Take(1).Timeout(30.Seconds())
            .Should().Within(60.Seconds()).Emit();
        seededNode.Content.Should().NotBeOfType<ThreadMessage>(
            "the seeded cell must be DEGRADED (untyped JSON) — that is the whole mechanism under test");
        seededNode.ContentAs<ThreadMessage>(Mesh.JsonSerializerOptions).Should().NotBeNull(
            "…and ContentAs must be able to recover it — that is the fix");

        var history = await ThreadExecution.LoadFullConversationHistoryFromMesh(
                Mesh, threadPath,
                excludeUserMessageId: null, excludeResponseMessageId: null,
                NullLogger.Instance,
                cellTimeout: 5.Seconds())
            .Should().Within(60.Seconds()).Emit();

        history.Should().HaveCount(2,
            "degraded cells must be recovered by ContentAs, not dropped by a failed cast");
        history.Select(m => m.Role).Should().Equal(ChatRole.User, ChatRole.Assistant);
        history.Select(m => m.Text!.TrimEnd()).Should().Equal("degraded question", "degraded answer");
    }

    /// <summary>
    /// 🚨 #2290, the OTHER direction: tolerating a degraded cell must NOT become "tolerate
    /// anything". A cell whose content is genuinely not a <see cref="ThreadMessage"/> — here JSON
    /// missing the required <c>role</c>/<c>text</c> members — must still be DROPPED, and the
    /// <c>HISTORY_CELL_DROP</c> path must still log it. Silently substituting a blank message for
    /// unreadable content would be #2226's silent-wrong-answer defect in a new place.
    /// </summary>
    [Fact]
    public async Task UnreadableCell_IsStillDroppedAndLogged_WhileReadableOnesSurvive()
    {
        var client = GetClient();
        var threadPath = await CreateThread(client, "Loader unreadable-cell test");

        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await SeedDegradedCell(client, threadPath, "readable-user", "user", "readable question", t0);
        // Not a ThreadMessage in any shape: the record's `Role`/`Text` are `required`, so
        // deserialization throws and ContentAs returns null with a loud log.
        await SeedRawContentCell(client, threadPath, "garbage-cell",
            """{"somethingElse":"entirely","nested":{"n":1}}""");

        await AppendCellIds(client, threadPath, "readable-user", "garbage-cell");

        var logs = new CapturingLogger();
        var history = await ThreadExecution.LoadFullConversationHistoryFromMesh(
                Mesh, threadPath,
                excludeUserMessageId: null, excludeResponseMessageId: null,
                logs,
                cellTimeout: 1.Seconds())
            .Should().Within(60.Seconds()).Emit();

        history.Should().HaveCount(1, "the unreadable cell must be dropped, not invented");
        history.Single().Text!.TrimEnd().Should().Be("readable question");

        logs.Messages.Should().Contain(m => m.Contains("HISTORY_CELL_DROP") && m.Contains("garbage-cell"),
            "an unreadable cell must SAY it was dropped — a silent drop is what #2290 was about");
        logs.Messages.Should().Contain(m => m.Contains("HISTORY_PARTIAL"),
            "a partial load must still warn");
    }

    /// <summary>
    /// 🚨 #2290 — the empty case must stay empty, not throw. The zero-loaded guard fires only when
    /// cells were EXPECTED; a brand-new thread with no prior cells has nothing to load and must
    /// return an empty history without erroring the round.
    /// </summary>
    [Fact]
    public async Task NoPriorCells_ReturnsEmptyHistory_WithoutThrowing()
    {
        var client = GetClient();
        var threadPath = await CreateThread(client, "Loader no-prior-cells test");

        // Settle: the thread node exists and carries no cells yet.
        var thread = await ThreadFlow.ReadThread(client, threadPath, t => t.Messages.Count == 0)
            .Should().Within(60.Seconds()).Emit();
        thread.Messages.Should().BeEmpty();

        var history = await ThreadExecution.LoadFullConversationHistoryFromMesh(
                Mesh, threadPath,
                excludeUserMessageId: null, excludeResponseMessageId: null,
                NullLogger.Instance,
                cellTimeout: 1.Seconds())
            .Should().Within(60.Seconds()).Emit();

        history.Should().BeEmpty("no prior cells means no history — and no TimeoutException");
    }

    /// <summary>
    /// Seeds a cell node whose <c>Content</c> is the UNTYPED JSON form of a
    /// <see cref="ThreadMessage"/> — camelCase wire shape, no <c>$type</c>, so nothing can resolve
    /// it back to the CLR type and every reader sees a raw <c>JsonElement</c>.
    /// </summary>
    private Task SeedDegradedCell(
        IMessageHub client, string threadPath, string cellId, string role, string text, DateTime timestamp)
        => SeedRawContentCell(client, threadPath, cellId,
            $$"""
              {"role":"{{role}}","text":"{{text}}","timestamp":"{{timestamp:O}}","status":"Completed"}
              """);

    /// <summary>
    /// Creates a cell node carrying <paramref name="contentJson"/> verbatim.
    ///
    /// <para>🚨 <b>No <c>NodeType</c> deliberately.</b> A cell declared
    /// <c>NodeType = ThreadMessage</c> gets a per-node hub configured
    /// <c>WithContentType&lt;ThreadMessage&gt;()</c>, and that hub RE-TYPES any recoverable content
    /// before a reader ever sees it — which is precisely why the production degradation is a
    /// cold-start timing window rather than a steady state, and why it cannot be reproduced by
    /// seeding a well-formed typed cell. Omitting the NodeType removes the re-typing step, so the
    /// loader is handed the degraded form deterministically instead of racing a window.</para>
    /// </summary>
    private async Task SeedRawContentCell(
        IMessageHub client, string threadPath, string cellId, string contentJson)
    {
        var content = JsonDocument.Parse(contentJson).RootElement.Clone();
        var resp = await client.Observe(
            new CreateNodeRequest(new MeshNode(cellId, threadPath)
            {
                MainNode = ContextPath,
                Content = content
            }),
            o => o.WithTarget(Mesh.Address)).Should().Within(60.Seconds()).Emit();
        resp.Message.Success.Should().BeTrue(resp.Message.Error ?? "");
    }

    /// <summary>Appends cell ids to the thread's <c>Messages</c> and waits until the write settles.</summary>
    private async Task AppendCellIds(IMessageHub client, string threadPath, params string[] cellIds)
    {
        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.ContentAs<MeshThread>(Mesh.JsonSerializerOptions);
            return t is null ? node : node with { Content = t with { Messages = t.Messages.AddRange(cellIds) } };
        }).Should().Within(60.Seconds()).Emit();

        await ThreadFlow.ReadThread(client, threadPath, t => cellIds.All(t.Messages.Contains))
            .Should().Within(60.Seconds()).Emit();
    }

    /// <summary>
    /// Records what the loader said. The drop path is only useful if it is AUDIBLE, so the
    /// negative test asserts on the lines rather than on their absence of effect. Instance state
    /// (never static) — it dies with the test.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> messages = new();

        public IReadOnlyCollection<string> Messages => messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, exception));
    }

    [Fact]
    public async Task AllCellsMissing_ThrowsTimeoutException()
    {
        var client = GetClient();
        // This test stays async (verifies the loader observable errors with a
        // specific TimeoutException via ThrowAsync), so it must NOT use blocking
        // reactive .Should() assertions — inline the thread create with await.
        var createResp = await client.Observe(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, "Loader all-fail test", "TestUser")),
            o => o.WithTarget(Mesh.Address)).Should().Within(60.Seconds()).Emit();
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var threadPath = createResp.Message.Node!.Path!;

        // Warm the cache with a request/response read so Content arrives as a
        // typed MeshThread (not JsonElement) — otherwise the workspace.Update
        // lambda below treats `node.Content is not MeshThread` as true and
        // short-circuits to a no-op, leaving Messages empty.
        await ThreadFlow.ReadThread(client, threadPath, _ => true)
            .Should().Within(60.Seconds()).Emit();

        // Stamp two phantom cell IDs into Messages — no per-node hub will ever
        // emit content at those paths, so every per-cell read times out and the
        // loader's guard must refuse to return empty history.
        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            if (node.Content is not MeshThread t) return node;
            return node with { Content = t with { Messages = ImmutableList.Create("phantom-1", "phantom-2") } };
        }).Should().Within(60.Seconds()).Emit();

        // Confirm the thread's Messages list actually carries the phantoms before
        // we kick off the loader — otherwise a stale cache snapshot would let the
        // loader sail through "cellIds.Count == 0" and miss the guard entirely.
        var settled = await ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Contains("phantom-1") && t.Messages.Contains("phantom-2"))
            .Should().Within(60.Seconds()).Emit();
        settled.Messages.Should().HaveCount(2);

        // Loader must ERROR with TimeoutException (every phantom cell read times out).
        // Materialize folds the OnError into a value so we assert it reactively — no
        // await, no ThrowAsync. Within() must exceed the loader's own cellTimeout so
        // the loader's TimeoutException fires first.
        var loadResult = await ThreadExecution.LoadFullConversationHistoryFromMesh(
                Mesh, threadPath,
                excludeUserMessageId: null, excludeResponseMessageId: null,
                NullLogger.Instance,
                cellTimeout: 500.Milliseconds())
            .Materialize()
            .Should().Within(60.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
        loadResult.Exception.Should().BeOfType<TimeoutException>(
            "loader must refuse to return empty history when cells were expected");
    }

    #region Fake LLM

    private class FakeChatClient(string response) : IChatClient
    {
        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in response.Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Delay(10, cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class FakeChatClientFactory : IChatClientFactory
    {
        public string Name => "FakeFactory";
        public IReadOnlyList<string> Models => ["fake-model"];
        public int Order => 0;

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new FakeChatClient(FakeResponse),
                instructions: config.Instructions ?? "You are a test assistant.",
                name: config.Id, description: config.Description ?? config.Id,
                tools: [], loggerFactory: null, services: null);

        public Task<Microsoft.Agents.AI.ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
