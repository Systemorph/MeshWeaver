using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Regression for the "30s Response did not arrive in time" GUI stall: a cross-hub
/// <c>stream.Update</c> on a thread node MUST emit as soon as the activity is STARTED
/// (the patch is accepted), NOT wait for the in-flight round to FINISH.
///
/// <para>Before the fix, <c>UpdateRemote</c> awaited the owner's <c>PatchDataResponse</c>
/// for up to 30s. While the per-thread hub is busy executing a round, that ack queues
/// behind the round, so a user editing/submitting to the thread during execution stalled
/// for the full 30s (surfaced in the GUI as a request timeout). After the fix the wait is
/// bounded (<c>UpdateResponseWaitBound</c>, ~2s) and falls back to the optimistic snapshot,
/// so the write emits promptly while the round is still running.</para>
///
/// <para>The companion fail-fast half — a denied write still surfaces
/// <c>UnauthorizedAccessException</c> within the bound — is covered by
/// <c>RlsIntegrationTests.StreamUpdate_WithoutUpdateRights_IsDeniedAndErrors</c>.</para>
/// </summary>
public class UpdateEmitsDuringRoundTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/Roland";

    // The mid-round write must emit FAR below the old 30s response wait. The production
    // bound is ~2s + optimistic fallback; 10s gives generous CI headroom while still
    // failing hard if the 30s stall ever returns.
    private static readonly TimeSpan PromptEmitBudget = TimeSpan.FromSeconds(10);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new LongRoundChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task StreamUpdate_DuringExecutingRound_EmitsPromptly_NotAfterRoundFinishes()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Create the thread.
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "Emit-timing test", "Roland");
        var createResp = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).Should().Within(30.Seconds()).Emit();
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var threadPath = createResp.Message.Node!.Path!;

        // Warm the remote-stream subscription so the IsExecuting transition is observable
        // (same warm-up CancelThreadExecutionTest uses).
        var baseline = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(10.Seconds()).Match(t => t != null);
        baseline!.IsExecuting.Should().BeFalse("thread should not be executing yet");

        // Submit → a long (~9s) round starts and occupies the per-thread hub.
        client.SubmitMessage(threadPath, "Tell me a long story", contextPath: ContextPath);

        var executing = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(15.Seconds()).Match(t => t is { IsExecuting: true });
        executing!.IsExecuting.Should().BeTrue();

        // ACT — while the round is executing, issue a benign cross-hub stream.Update on the
        // thread node (a MeshNode-level Name change — does not perturb the round) and TIME it.
        // This is the exact path SubmitMessage / the Stop button take during execution.
        var sw = Stopwatch.StartNew();
        var updated = await workspace.GetMeshNodeStream(threadPath)
            .Update(curr => curr with { Name = "touched-mid-round" })
            .Should().Within(PromptEmitBudget).Emit();
        sw.Stop();
        Output.WriteLine($"Mid-round stream.Update emitted in {sw.ElapsedMilliseconds}ms");

        // ASSERT — it emitted promptly (emit-when-started), NOT after the ~9s round finished,
        // and FAR below the old 30s response wait.
        updated.Name.Should().Be("touched-mid-round",
            "the optimistic post-patch snapshot must carry the write");
        sw.Elapsed.Should().BeLessThan(PromptEmitBudget,
            "a content write must emit as soon as the activity is started, never wait for the round (was a 30s stall)");

        // CORROBORATE — the round was STILL executing when the write emitted: the write did
        // not block until the round finished.
        var stillExecuting = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t != null)
            .Take(1)
            .Timeout(5.Seconds())
            .ToTask();
        stillExecuting!.IsExecuting.Should().BeTrue(
            "the mid-round write emitted while the round was still running — it did not wait for completion");
    }

    #region Long-round chat client (~9s, streams 200ms/word)

    private sealed class LongRoundChatClient : IChatClient
    {
        private const string LongResponse =
            "Once upon a time in a land far away there lived a wise old wizard who knew many " +
            "secrets about the universe and spent his days reading ancient books in his tall " +
            "tower overlooking the vast ocean that stretched endlessly toward the horizon where " +
            "ships sailed carrying merchants and explorers seeking new worlds and adventures " +
            "beyond the known maps of their civilization and culture";

        public ChatClientMetadata Metadata => new("LongRoundProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, LongResponse)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in LongResponse.Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Delay(200, cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private sealed class LongRoundChatClientFactory : IChatClientFactory
    {
        public string Name => "LongRoundFactory";
        public IReadOnlyList<string> Models => ["long-round-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new LongRoundChatClient(),
                instructions: config.Instructions ?? "You are a slow test assistant.",
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
