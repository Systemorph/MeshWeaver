#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.ShortGuid;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the PROMPT-CACHE PREFIX invariant: nothing that changes between two rounds may sit in
/// the cached prefix — the system message and everything before the current user turn.
///
/// <para>🚨 Why this is a test and not a comment. Every provider's prompt cache is a PREFIX
/// match, so the first differing token invalidates everything behind it.
/// <see cref="CurrentTimeContext"/> renders the UTC instant at SECONDS precision, and it used to
/// be composed into the block folded into the agent's SYSTEM message — the FIRST message of the
/// turn. The clock ticking between two rounds therefore invalidated the instructions, the
/// application context, the whole conversation history and every document the agent had pulled.</para>
///
/// <para>Measured against Azure Foundry DeepSeek on 2026-08-23, same 13,040-token prompt: with
/// the timestamp in the system message the hit rate was <b>0%</b> on every call; with an
/// identical prefix and the timestamp at the tail it reached <b>98.2%</b>. Production sat at 53%.</para>
///
/// <para>It is worse than a lost discount on drivers that cache EXPLICITLY: the Anthropic client
/// marks the system prompt with an ephemeral <c>cache_control</c> breakpoint, so a volatile system
/// prompt made it WRITE a cache entry every round at the 1.25x premium and never read one back —
/// a live thread showed <c>cacheWriteTokens: 9068</c> against <c>cacheRead: 0</c>.</para>
///
/// <para>The failure is entirely silent — correct answers, correct token counts, just a bill
/// several times larger than it should be — so only an assertion keeps it fixed.</para>
/// </summary>
public class PromptCachePrefixStabilityTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public PromptCachePrefixStabilityTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new CapturingFactory());
                return services;
            })
            .AddGraph()
            .AddAI()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());

    #region capture harness

    private sealed class CapturingClient(List<ChatMessage> capture) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            capture.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            capture.AddRange(messages);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "OK");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class CapturingFactory : IChatClientFactory
    {
        public List<ChatMessage> Captured { get; } = [];
        public string Name => "CapturingFactory";
        public IReadOnlyList<string> Models => ["capturing-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => new(chatClient: new CapturingClient(Captured),
                instructions: config.Instructions ?? "Test assistant.",
                name: config.Id, description: config.Description ?? config.Id,
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    private static string TextOf(ChatMessage m) =>
        m.Text ?? string.Join("", m.Contents.OfType<TextContent>().Select(t => t.Text));

    private async Task<(AgentChatClient Chat, CapturingFactory Factory)> SetupAsync(CancellationToken ct)
    {
        var factory = (CapturingFactory)Mesh.ServiceProvider.GetRequiredService<IChatClientFactory>();
        var agentChat = new AgentChatClient(Mesh.ServiceProvider);
        await agentChat.Initialize("ACME").WhenInitialized.FirstAsync().ToTask(ct);
        agentChat.SetThreadId($"ACME/{Guid.NewGuid().AsString()}");
        var agents = await agentChat.GetOrderedAgentsAsync();
        agents.Should().NotBeEmpty();
        agentChat.SetSelectedAgent(agents[0].Name);
        return (agentChat, factory);
    }

    #endregion

    /// <summary>
    /// STREAMING path (what the chat UI actually runs). The clock must NOT be in the system
    /// message — that message is the cached prefix, and a seconds-precision timestamp in it
    /// invalidates the instructions, the history and every document behind it on every round.
    /// </summary>
    [Fact]
    public async Task Streaming_TheClock_IsNotInTheSystemMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAsync(ct);

        await foreach (var _ in agentChat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], ct)) { }

        var system = factory.Captured.Where(m => m.Role == ChatRole.System).Select(TextOf).ToList();
        system.Should().NotBeEmpty("the streaming turn folds instructions + context into a system message");

        foreach (var s in system)
            s.Should().NotContain(CurrentTimeContext.Heading,
                "the system message is the CACHED PREFIX — a seconds-precision timestamp in it "
                + "invalidates everything behind it (0% vs 98.2% measured on Foundry DeepSeek)");
    }

    /// <summary>Streaming: the clock still reaches the model, riding the LAST USER message (the tail).</summary>
    [Fact]
    public async Task Streaming_TheClock_RidesTheLastUserMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAsync(ct);

        await foreach (var _ in agentChat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], ct)) { }

        var lastUser = factory.Captured.LastOrDefault(m => m.Role == ChatRole.User);
        lastUser.Should().NotBeNull();
        TextOf(lastUser!).Should().Contain(CurrentTimeContext.Heading,
            "the agent still needs the date — it moved to the tail, it did not disappear (#1651)");
    }

    /// <summary>
    /// Streaming: the system message is byte-identical across two rounds of the SAME thread.
    /// This is the property the cache keys on, and the one a future edit is most likely to break
    /// by adding "just one more" dynamic line to the system block.
    ///
    /// <para>⚠️ On its own this cannot catch a CLOCK regression, and the limitation is recorded
    /// here rather than discovered later: two rounds run milliseconds apart, so a re-introduced
    /// seconds-precision timestamp usually renders identically in both and this test still passes.
    /// Verified by mutation on 2026-08-23 — putting the clock back in the prefix left this test
    /// green while <see cref="Streaming_TheClock_IsNotInTheSystemMessage"/> and both non-streaming
    /// tests failed. Those three are the load-bearing ones for the clock; this one guards the
    /// broader invariant (any per-round value creeping into the system block — a counter, a GUID,
    /// a re-serialised context) that they would not notice.</para>
    /// </summary>
    [Fact]
    public async Task Streaming_TheCachedPrefix_IsIdenticalAcrossRounds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAsync(ct);

        await foreach (var _ in agentChat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "first")], ct)) { }
        var first = factory.Captured.Where(m => m.Role == ChatRole.System).Select(TextOf).ToList();
        first.Should().NotBeEmpty("guards against a vacuous comparison of two empty lists");

        factory.Captured.Clear();
        await foreach (var _ in agentChat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "second")], ct)) { }
        var second = factory.Captured.Where(m => m.Role == ChatRole.System).Select(TextOf).ToList();

        second.Should().Equal(first,
            "the system message IS the cached prefix; if it differs between two rounds of the "
            + "same thread the provider re-charges full price for the whole prompt");
    }

    /// <summary>
    /// NON-STREAMING path assembles ONE combined user message instead of a system message, so the
    /// same invariant reads as ordering: the clock must come AFTER the user's own text, i.e. at
    /// the very tail of the assembled prompt.
    /// </summary>
    [Fact]
    public async Task NonStreaming_TheClock_ComesAfterTheUserText()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAsync(ct);

        const string userText = "what is the launch status?";
        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, userText)], ct)) { }

        var prompt = factory.Captured.Where(m => m.Role == ChatRole.User).Select(TextOf).LastOrDefault();
        prompt.Should().NotBeNullOrEmpty();

        var clockIdx = prompt!.IndexOf(CurrentTimeContext.Heading, StringComparison.Ordinal);
        var userIdx = prompt.IndexOf(userText, StringComparison.Ordinal);

        clockIdx.Should().BeGreaterThan(0, "the agent still needs the date (#1651)");
        userIdx.Should().BeGreaterThanOrEqualTo(0, "the user's text is in the assembled prompt");
        clockIdx.Should().BeGreaterThan(userIdx,
            "the volatile clock belongs at the TAIL; everything before it is the prefix a "
            + "provider can cache across rounds");
    }

    /// <summary>
    /// Non-streaming: the stable PREFIX — everything before the clock — is identical across two
    /// rounds once the user's own text is discounted. Compares the segment that precedes the
    /// clock heading, which is exactly what a prefix cache would match on.
    /// </summary>
    [Fact]
    public async Task NonStreaming_ThePrefixBeforeTheClock_IsStableAcrossRounds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAsync(ct);

        // 🚨 The markers must be strings that CANNOT occur in the agent's own instructions.
        // "first"/"second" do — the built-in Assistant prompt says "from the first user message
        // to the last reply" — so normalising them rewrote the instructions in round one only and
        // failed this test on its own substitution rather than on the prompt.
        const string firstMarker = "ZZQ-ROUND-ONE";
        const string secondMarker = "ZZQ-ROUND-TWO";

        static string PrefixOf(string prompt, string userText)
        {
            var idx = prompt.IndexOf(CurrentTimeContext.Heading, StringComparison.Ordinal);
            idx.Should().BeGreaterThan(0, "the clock must be present to split on");
            return prompt[..idx].Replace(userText, "<USER>", StringComparison.Ordinal);
        }

        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, firstMarker)], ct)) { }
        var firstPrompt = factory.Captured.Where(m => m.Role == ChatRole.User).Select(TextOf).Last();
        var firstPrefix = PrefixOf(firstPrompt, firstMarker);
        firstPrefix.Should().NotBeNullOrWhiteSpace("guards against comparing two empty strings");
        firstPrefix.Should().Contain("<USER>", "the marker must actually be found and normalised");

        factory.Captured.Clear();
        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, secondMarker)], ct)) { }
        var secondPrompt = factory.Captured.Where(m => m.Role == ChatRole.User).Select(TextOf).Last();

        PrefixOf(secondPrompt, secondMarker).Should().Be(firstPrefix,
            "with the clock at the tail, everything ahead of it is byte-identical between "
            + "rounds — that segment is what the provider's prompt cache can hit");
    }
}
