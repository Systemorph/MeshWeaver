using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Behaviour tests for <c>ThreadExecution.LoadFullConversationHistoryFromMesh</c>.
/// Three cases pinned by the loader's contract:
/// <list type="number">
///   <item>All cells have text â†’ loader returns the full ordered list.</item>
///   <item>Some cells time out / are unreadable â†’ loader logs a warning and
///     returns the partial list (the agent gets best-effort context).</item>
///   <item>Every expected cell fails â†’ loader throws <see cref="TimeoutException"/>
///     instead of returning an empty list (refuses to submit a corrupt context).</item>
/// </list>
/// </summary>
public class LoadConversationHistoryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";
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

    private string CreateThread(IMessageHub client, string text)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "TestUser");
        var resp = client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).Should().Within(60.Seconds()).Emit();
        resp.Message.Success.Should().BeTrue(resp.Message.Error ?? "");
        return resp.Message.Node!.Path!;
    }

    private void SubmitAndWaitForResponse(
        IMessageHub client, string threadPath, string text)
    {
        var responseMsgId = ThreadFlow.SubmitAndWait(client, threadPath, text,
            contextPath: ContextPath).Should().Within(60.Seconds()).Emit();
        // Wait for the response cell to reach a TERMINAL status. Earlier we
        // gated on `!IsNullOrEmpty(m.Text)`, but ThreadExecution stamps the
        // placeholder "Generating response..." onto the cell text very early
        // in the streaming loop â€” that text passes the non-empty check while
        // the real response is still mid-stream, so the history assertion
        // later read the placeholder instead of the FakeResponse.
        ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => m.Status is ThreadMessageStatus.Completed
                          or ThreadMessageStatus.Cancelled
                          or ThreadMessageStatus.Error).Should().Within(60.Seconds()).Emit();
    }

    // 60s timeout: two real ThreadFlow.SubmitAndWait calls + ReadThread predicate
    // waits â€” local runs ~3s, CI cold-start runs ~30s. Default 30s methodTimeout
    // tripped on CI (31.85s in run 26376715753).
    [Fact]
    public void AllCells_HaveText_ReturnsFullHistory()
    {
        var client = GetClient();
        var threadPath = CreateThread(client, "Loader history happy path");

        SubmitAndWaitForResponse(client, threadPath, "first question");
        SubmitAndWaitForResponse(client, threadPath, "second question");

        // After two real rounds the thread has 4 cells (user+assistant per round).
        // Wait until the thread node sees IsExecuting=false AND Messages.Count >= 4
        // so the loader sees the fully-settled state.
        var thread = ThreadFlow.ReadThread(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 4)
            .Should().Within(60.Seconds()).Emit();
        thread.Messages.Should().HaveCount(4);

        // The thread hub is the per-node hub for threadPath â€” that's the workspace
        // the loader queries via IMeshNodeStreamCache.
        var history = ThreadExecution.LoadFullConversationHistoryFromMesh(
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
    public void SomeCellsMissing_ReturnsPartialHistory_AndWarns()
    {
        var client = GetClient();
        var threadPath = CreateThread(client, "Loader partial-history test");

        // Round 1: real submit â†’ user+assistant cell, both with text.
        SubmitAndWaitForResponse(client, threadPath, "real question");
        var threadAfterRound1 = ThreadFlow.ReadThread(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 2)
            .Should().Within(60.Seconds()).Emit();
        threadAfterRound1.Messages.Should().HaveCount(2);

        // Append a phantom cell ID to Messages â€” no per-node hub will ever emit
        // content at threadPath/{phantom-id}, so the per-cell Timeout fires and
        // the cell is omitted from the result with a warning.
        var phantomCellId = "phantom-" + Guid.NewGuid().ToString("N")[..8];
        Mesh.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            if (node.Content is not MeshThread t) return node;
            return node with { Content = t with { Messages = t.Messages.Add(phantomCellId) } };
        }).Should().Emit();

        var history = ThreadExecution.LoadFullConversationHistoryFromMesh(
                Mesh, threadPath,
                excludeUserMessageId: null, excludeResponseMessageId: null,
                NullLogger.Instance,
                cellTimeout: 1.Seconds())
            .Should().Within(60.Seconds()).Emit();

        history.Should().HaveCount(2, "phantom cell never emits but the two real cells should still load");
        history.Select(m => m.Text!.TrimEnd()).Should().Equal("real question", FakeResponse);
    }

    [Fact]
    public void AllCellsMissing_ThrowsTimeoutException()
    {
        var client = GetClient();
        // This test stays async (verifies the loader observable errors with a
        // specific TimeoutException via ThrowAsync), so it must NOT use blocking
        // reactive .Should() assertions â€” inline the thread create with await.
        var createResp = client.Observe(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, "Loader all-fail test", "TestUser")),
            o => o.WithTarget(Mesh.Address)).Should().Within(60.Seconds()).Emit();
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var threadPath = createResp.Message.Node!.Path!;

        // Warm the cache with a request/response read so Content arrives as a
        // typed MeshThread (not JsonElement) â€” otherwise the workspace.Update
        // lambda below treats `node.Content is not MeshThread` as true and
        // short-circuits to a no-op, leaving Messages empty.
        ThreadFlow.ReadThread(client, threadPath, _ => true)
            .Should().Within(60.Seconds()).Emit();

        // Stamp two phantom cell IDs into Messages â€” no per-node hub will ever
        // emit content at those paths, so every per-cell read times out and the
        // loader's guard must refuse to return empty history.
        Mesh.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            if (node.Content is not MeshThread t) return node;
            return node with { Content = t with { Messages = ImmutableList.Create("phantom-1", "phantom-2") } };
        }).Should().Within(60.Seconds()).Emit();

        // Confirm the thread's Messages list actually carries the phantoms before
        // we kick off the loader â€” otherwise a stale cache snapshot would let the
        // loader sail through "cellIds.Count == 0" and miss the guard entirely.
        var settled = ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Contains("phantom-1") && t.Messages.Contains("phantom-2"))
            .Should().Within(60.Seconds()).Emit();
        settled.Messages.Should().HaveCount(2);

        // Loader must ERROR with TimeoutException (every phantom cell read times out).
        // Materialize folds the OnError into a value so we assert it reactively — no
        // await, no ThrowAsync. Within() must exceed the loader's own cellTimeout so
        // the loader's TimeoutException fires first.
        var loadResult = ThreadExecution.LoadFullConversationHistoryFromMesh(
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
