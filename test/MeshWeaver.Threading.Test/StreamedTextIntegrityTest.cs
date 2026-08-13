using System;
using System.Collections.Generic;
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
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Chat is the most user-visible surface in the product, and the streaming write now ships a SPLICE
/// rather than the cell's whole text (see <c>PatchStringSplice</c>). This test runs a REAL round
/// through <c>ThreadExecution</c> — long enough that <c>Sample(100 ms)</c> produces many ticks and
/// the text crosses <c>PatchStringSplice.MinSpliceLength</c> — and pins the three things a user
/// would notice if a splice ever landed at the wrong offset:
/// <list type="number">
///   <item>the finished text is EXACTLY what the model streamed, byte for byte;</item>
///   <item>every intermediate state a subscriber sees is a PREFIX of that text — never a scrambled,
///     duplicated or truncated middle;</item>
///   <item>a subscriber that joins LATE sees the whole text so far, not just the last chunk.</item>
/// </list>
/// <para>The wire-cost measurement that motivates the change is
/// <see cref="StreamingCellWriteByteCountTest"/>; it is kept separate because
/// <c>Sample(100 ms)</c> makes the tick count here a function of wall-clock timing.</para>
/// </summary>
public class StreamedTextIntegrityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";

    /// <summary>
    /// Longest state exempt from the prefix check. The framework writes short status placeholders
    /// before the first token ("Allocating agent…" = 19 chars, "Generating response…" = 22); the
    /// model's own chunks are an order of magnitude longer. Bounding the exemption by length rather
    /// than by matching those strings keeps it from ever excusing a corrupted model state — and the
    /// strings themselves are localizable, so matching them would be brittle as well as unsafe.
    /// </summary>
    private const int PlaceholderMaxLength = 64;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new LongAnswerChatClientFactory());
                return services;
            })
            // A streaming round leaves in-flight DataChangeRequest callbacks past the default
            // 500 ms quiesce budget; the round is the thing under test, so give teardown room.
            .ConfigureHub(c => c.WithQuiesceTimeout(TimeSpan.FromSeconds(15)))
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddData();
    }

    [Fact]
    public async Task ARealStreamedRound_LandsTheExactText_AndEveryStateSeenIsAPrefixOfIt()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "Streaming integrity", "TestUser");
        // Target the CONTEXT namespace, the way a real client creates a thread — not the root mesh
        // hub. Routing creation through the owning namespace is both the majority convention in the
        // thread tests and what keeps this representative of the production path.
        var created = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address(ContextPath)))
            .Should().Within(60.Seconds()).Emit();
        created.Message.Success.Should().BeTrue(created.Message.Error ?? "");
        var threadPath = created.Message.Node!.Path!;

        // Resolve the response cell as soon as the round allocates it, so we can watch the text grow
        // from (nearly) the start rather than only seeing the finished value.
        var cellPathTask = workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.ContentAs<MeshThread>(client.JsonSerializerOptions))
            .Where(t => !string.IsNullOrEmpty(t?.ActiveMessageId))
            .Select(t => $"{threadPath}/{t!.ActiveMessageId}")
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        var roundTask = ThreadFlow
            .SubmitAndWait(client, threadPath, "go", contextPath: ContextPath, timeout: 60.Seconds())
            .FirstAsync().ToTask();

        var cellPath = await cellPathTask;
        Output.WriteLine($"response cell: {cellPath}");

        var observed = new List<string>();
        using (workspace.GetMeshNodeStream(cellPath)
                   .Select(n => n.ContentAs<ThreadMessage>(client.JsonSerializerOptions))
                   .Where(m => m is not null)
                   .Subscribe(m => { lock (observed) observed.Add(m!.Text); }))
        {
            await roundTask;
        }

        var final = await ThreadFlow.ReadMessage(client, threadPath, cellPath.Split('/')[^1],
            m => m.CompletedAt != null && !string.IsNullOrEmpty(m.Text),
            timeout: 60.Seconds()).Should().Within(60.Seconds()).Emit();

        // 1. Byte-identical to what the model actually streamed.
        final.Text.Should().Be(LongAnswerChatClient.Answer,
            "the spliced streaming write must land exactly the text the model produced");
        LongAnswerChatClient.Answer.Length.Should()
            .BeGreaterThan(MeshWeaver.Data.Serialization.PatchStringSplice.MinSpliceLength * 3,
                "the answer must be long enough that the splice path actually engaged");

        // 2. Every state carrying model text was a PREFIX of the answer, and they grew monotonically.
        //    A splice applied at a wrong offset shows up here immediately — as a state that is not a
        //    prefix (scrambled / duplicated middle) or one that shrinks.
        //
        //    Exempt: the framework's own pre-stream status text ("Allocating agent…",
        //    "Generating response…"), which is not model output at all. The exemption is bounded by
        //    LENGTH rather than by matching those strings — they are localizable, and a length rule
        //    cannot accidentally excuse a long corrupted state.
        List<string> snapshot;
        lock (observed) snapshot = [.. observed];
        var nonEmpty = snapshot.Where(t => !string.IsNullOrEmpty(t)).ToList();
        var modelText = nonEmpty.Where(t => t.Length > PlaceholderMaxLength).ToList();
        var placeholders = nonEmpty.Where(t => t.Length <= PlaceholderMaxLength).ToList();

        Output.WriteLine($"observed {snapshot.Count} emissions; {modelText.Count} carrying model text; "
            + $"lengths {string.Join(",", modelText.Select(t => t.Length).Distinct().Take(12))}…");
        foreach (var p in placeholders.Distinct())
            Output.WriteLine($"pre-stream placeholder (len {p.Length}): {p}");

        modelText.Should().NotBeEmpty("the round must have been observed while streaming");
        var offenders = modelText
            .Where(t => !LongAnswerChatClient.Answer.StartsWith(t, StringComparison.Ordinal))
            .ToList();
        foreach (var o in offenders.Take(5))
            Output.WriteLine($"NOT A PREFIX (len {o.Length}): >>>{o[..Math.Min(200, o.Length)]}<<<");
        offenders.Should().BeEmpty("every state a viewer sees must be a prefix of the final answer");
        modelText.Select(t => t.Length).Should().BeInAscendingOrder(
            "the streamed text only ever grows — a splice can never shorten or rewrite it");
        modelText.Any(t => t.Length > MeshWeaver.Data.Serialization.PatchStringSplice.MinSpliceLength)
            .Should().BeTrue("states past MinSpliceLength are the ones written as splices");

        // 3. A subscriber that joins after the fact sees everything, not just the last splice.
        var lateClient = GetClient();
        var late = await lateClient.GetWorkspace().GetMeshNodeStream(cellPath)
            .Select(n => n.ContentAs<ThreadMessage>(lateClient.JsonSerializerOptions))
            .Where(m => m is not null && m.CompletedAt != null)
            .FirstAsync().Timeout(60.Seconds()).ToTask();
        late!.Text.Should().Be(LongAnswerChatClient.Answer,
            "a late subscriber reads the owner's committed state — the whole text, not a fragment");
    }

    #region Long-answer chat client — streams past MinSpliceLength over many Sample(100ms) ticks

    private sealed class LongAnswerChatClient : IChatClient
    {
        private const string Paragraph =
            "The cedent retains the first band of loss and the reinsurer attaches above it, so the "
            + "treaty's economics turn on where that attachment point sits relative to the modelled "
            + "severity curve; move it down and the premium rises faster than the expected recovery. ";

        private const int ChunkCount = 24;

        // Declared before Answer: static field initializers run in textual order.
        private static readonly string[] Chunks = Enumerable.Range(0, ChunkCount)
            .Select(i => $"[{i:D2}] {Paragraph}")
            .ToArray();

        /// <summary>~6 kB over 24 chunks — several times MinSpliceLength, ~3 s of streaming.</summary>
        internal static readonly string Answer = string.Concat(Chunks);

        public ChatClientMetadata Metadata => new("LongAnswerProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
                // > Sample(100 ms) so most chunks produce their own write rather than coalescing.
                await Task.Delay(130, cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class LongAnswerChatClientFactory : IChatClientFactory
    {
        public string Name => "LongAnswerFactory";
        public IReadOnlyList<string> Models => ["long-answer-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new LongAnswerChatClient(),
                instructions: config.Instructions ?? "You are a verbose test assistant.",
                name: config.Id, description: config.Description ?? config.Id,
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
