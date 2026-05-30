using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "TestUser");
        var resp = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);
        resp.Message.Success.Should().BeTrue(resp.Message.Error);
        return resp.Message.Node!.Path!;
    }

    private async Task SubmitAndWaitForResponseAsync(
        IMessageHub client, string threadPath, string text, CancellationToken ct)
    {
        var responseMsgId = await ThreadFlow.SubmitAndWait(client, threadPath, text,
            contextPath: ContextPath).FirstAsync().ToTask(ct);
        // Wait for the response cell to reach a TERMINAL status. Earlier we
        // gated on `!IsNullOrEmpty(m.Text)`, but ThreadExecution stamps the
        // placeholder "Generating response..." onto the cell text very early
        // in the streaming loop â€” that text passes the non-empty check while
        // the real response is still mid-stream, so the history assertion
        // later read the placeholder instead of the FakeResponse.
        await ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => m.Status is ThreadMessageStatus.Completed
                          or ThreadMessageStatus.Cancelled
                          or ThreadMessageStatus.Error).FirstAsync().ToTask(ct);
    }

    // 60s timeout: two real ThreadFlow.SubmitAndWait calls + ReadThread predicate
    // waits â€” local runs ~3s, CI cold-start runs ~30s. Default 30s methodTimeout
    // tripped on CI (31.85s in run 26376715753).
    [Fact]
    public async Task AllCells_HaveText_ReturnsFullHistory()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;
        var client = GetClient();
        var threadPath = await CreateThreadAsync(client, "Loader history happy path", ct);

        await SubmitAndWaitForResponseAsync(client, threadPath, "first question", ct);
        await SubmitAndWaitForResponseAsync(client, threadPath, "second question", ct);

        // After two real rounds the thread has 4 cells (user+assistant per round).
        // Wait until the thread node sees IsExecuting=false AND Messages.Count >= 4
        // so the loader sees the fully-settled state.
        var thread = await ThreadFlow.ReadThread(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 4)
            .FirstAsync().ToTask(ct);
        thread.Messages.Should().HaveCount(4);

        // The thread hub is the per-node hub for threadPath â€” that's the workspace
        // the loader queries via IMeshNodeStreamCache.
        var history = await ThreadExecution.LoadFullConversationHistoryFromMesh(
                Mesh, threadPath,
                excludeUserMessageId: null, excludeResponseMessageId: null,
                NullLogger.Instance,
                cellTimeout: 5.Seconds())
            .FirstAsync().ToTask(ct);

        history.Should().HaveCount(4, "two rounds = 2 user + 2 assistant = 4 messages");
        history.Select(m => m.Role).Should().Equal(
            ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant);
        history.Select(m => m.Text!.TrimEnd()).Should().Equal(
            "first question", FakeResponse, "second question", FakeResponse);
    }

    [Fact]
    public async Task SomeCellsMissing_ReturnsPartialHistory_AndWarns()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;
        var client = GetClient();
        var threadPath = await CreateThreadAsync(client, "Loader partial-history test", ct);

        // Round 1: real submit â†’ user+assistant cell, both with text.
        await SubmitAndWaitForResponseAsync(client, threadPath, "real question", ct);
        var threadAfterRound1 = await ThreadFlow.ReadThread(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 2)
            .FirstAsync().ToTask(ct);
        threadAfterRound1.Messages.Should().HaveCount(2);

        // Append a phantom cell ID to Messages â€” no per-node hub will ever emit
        // content at threadPath/{phantom-id}, so the per-cell Timeout fires and
        // the cell is omitted from the result with a warning.
        var phantomCellId = "phantom-" + Guid.NewGuid().ToString("N")[..8];
        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            if (node.Content is not MeshThread t) return node;
            return node with { Content = t with { Messages = t.Messages.Add(phantomCellId) } };
        }).FirstAsync().ToTask(ct);

        var history = await ThreadExecution.LoadFullConversationHistoryFromMesh(
                Mesh, threadPath,
                excludeUserMessageId: null, excludeResponseMessageId: null,
                NullLogger.Instance,
                cellTimeout: 1.Seconds())
            .FirstAsync().ToTask(ct);

        history.Should().HaveCount(2, "phantom cell never emits but the two real cells should still load");
        history.Select(m => m.Text!.TrimEnd()).Should().Equal("real question", FakeResponse);
    }

    [Fact]
    public async Task AllCellsMissing_ThrowsTimeoutException()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;
        var client = GetClient();
        var threadPath = await CreateThreadAsync(client, "Loader all-fail test", ct);

        // Warm the cache with a request/response read so Content arrives as a
        // typed MeshThread (not JsonElement) â€” otherwise the workspace.Update
        // lambda below treats `node.Content is not MeshThread` as true and
        // short-circuits to a no-op, leaving Messages empty.
        await ThreadFlow.ReadThread(client, threadPath, _ => true)
            .FirstAsync().ToTask(ct);

        // Stamp two phantom cell IDs into Messages â€” no per-node hub will ever
        // emit content at those paths, so every per-cell read times out and the
        // loader's guard must refuse to return empty history.
        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            if (node.Content is not MeshThread t) return node;
            return node with { Content = t with { Messages = ImmutableList.Create("phantom-1", "phantom-2") } };
        }).FirstAsync().ToTask(ct);

        // Confirm the thread's Messages list actually carries the phantoms before
        // we kick off the loader â€” otherwise a stale cache snapshot would let the
        // loader sail through "cellIds.Count == 0" and miss the guard entirely.
        var settled = await ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Contains("phantom-1") && t.Messages.Contains("phantom-2"))
            .FirstAsync().ToTask(ct);
        settled.Messages.Should().HaveCount(2);

        Func<Task> act = async () =>
        {
            await ThreadExecution.LoadFullConversationHistoryFromMesh(
                    Mesh, threadPath,
                    excludeUserMessageId: null, excludeResponseMessageId: null,
                    NullLogger.Instance,
                    cellTimeout: 500.Milliseconds())
                .FirstAsync().ToTask(ct);
        };

        await act.Should().ThrowAsync<TimeoutException>(
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
