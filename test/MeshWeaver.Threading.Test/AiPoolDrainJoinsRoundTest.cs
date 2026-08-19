using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// 🚨 An agent round runs as a leaf on the bounded <c>IoPoolNames.Ai</c> pool, and that leaf MUST
/// unwind when the pool's cancellation token fires. <c>IoPool.Drain()</c> — the join every teardown
/// orchestrator trusts before it disposes the service scope and unloads collectible node ALCs —
/// cancels the pool token and then re-acquires every gate permit. A leaf that never observes the
/// token holds its permit, so the join cannot complete: <c>Drain</c> sits out its full 30&#160;s
/// <c>DrainTimeout</c> and then reports the permit as a leaked leaf, and teardown proceeds over
/// live code. That is the use-after-unload SIGSEGV precondition, not a slow shutdown.
///
/// <para><b>The defect this pins.</b> <c>ThreadExecution</c> took the pool token as <c>poolCt</c>
/// and never read it — the round's linked CTS chained <c>executionCts</c> alone, on the stated
/// assumption that "executionCts is cancelled on hub disposal, so cancellation flows through it
/// regardless of the pool token". Drain is NOT hub disposal: it can run without the hub-disposal
/// cancel having fired, and that path left the round parked on its model call until the 30-minute
/// <c>MaxStreamingDuration</c> ceiling. Observed in CI as <c>DelegationSubThreadUsageTest</c>
/// failing in teardown with "1 pooled I/O leaf(s) still running" after a 32&#160;009&#160;ms
/// dispose — 30&#160;s of DrainTimeout plus overhead.</para>
///
/// <para>The assertion is <c>Drain() == 0</c> while a round is mid-stream. Pre-fix this returns 1
/// after burning the full DrainTimeout; post-fix the round's token is cancelled by the pool and the
/// leaf unwinds promptly. The stub below parks until ITS token fires and nothing else, so a pass
/// can only mean the cancellation genuinely reached the model call.</para>
/// </summary>
public class AiPoolDrainJoinsRoundTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new ParkingChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact(Timeout = 120_000)]
    public async Task DrainingTheAiPool_UnwindsAnInFlightRound()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "Ai pool drain", "Roland");
        var createResp = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).Should().Within(30.Seconds()).Emit();
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var threadPath = createResp.Message.Node!.Path!;

        // Warm the remote stream before submitting, so the IsExecuting transition is not raced
        // (same reason CancelThreadExecutionTest does it).
        await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(10.Seconds()).Match(t => t != null);

        client.SubmitMessage(threadPath, "park please", contextPath: ContextPath);

        var executing = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(30.Seconds())
            .Match(t => t is { IsExecuting: true, ActiveMessageId: { Length: > 0 } });
        var responseMsgId = executing!.ActiveMessageId!;

        // "Generating response..." is written AFTER the CTS is stored and immediately before the
        // streaming task starts — the deterministic "the pooled leaf is armed" signal. Draining
        // before it means joining a leaf that does not exist yet, which would pass vacuously.
        await Observable.Defer(() => workspace.GetMeshNodeStream($"{threadPath}/{responseMsgId}"))
            .Select(n => (n.Content as ThreadMessage)?.Text ?? "")
            .Where(t => t.StartsWith("Generating response", StringComparison.Ordinal))
            .Take(1)
            .RetryWhen(errors => errors
                .Select((ex, i) => i)
                .TakeWhile(i => i < 50)
                .SelectMany(_ => Observable.Timer(TimeSpan.FromMilliseconds(100))))
            .Should().Within(30.Seconds()).Emit();

        // The stub has actually been entered — the leaf is inside the model call, not merely
        // dispatched. Without this the round could still be in setup, holding no permit.
        ParkingChatClient.Entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            .Should().BeTrue("the streaming stub must be executing before the drain proves anything");
        Output.WriteLine("Round is parked inside the model call — draining the Ai pool.");

        // DrainAll() is exactly what the teardown orchestrator calls between DisposalCompleted and
        // scope disposal — so this drives the real path rather than a hand-picked pool.
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();

        // Drain on another thread: it is synchronous and, pre-fix, blocks for its full 30 s budget.
        // Bounding the wait here turns that into a fast, legible failure instead of a 30 s stall.
        var drain = Task.Run(registry.DrainAll, TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken));
        finished.Should().BeSameAs(drain,
            "Drain must join the round promptly once it cancels the pool token — if it is still "
            + "blocked after 20 s the round is ignoring poolCt and Drain is sitting out its "
            + "30 s DrainTimeout before reporting the permit as leaked");

        var residual = await drain;
        residual.Should().Be(0,
            "a round that observes the pool's cancellation token unwinds and releases its gate "
            + "permit, so the join is real and teardown may unload node ALCs safely");
    }

    #region A model call that parks until ITS token is cancelled — and nothing else

    private sealed class ParkingChatClientFactory : IChatClientFactory
    {
        public string Name => "ParkingFactory";
        public IReadOnlyList<string> Models => ["parking-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new ParkingChatClient(),
                instructions: config.Instructions ?? "You are a parking test assistant.",
                name: config.Id, description: config.Description ?? config.Id,
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    private sealed class ParkingChatClient : IChatClient
    {
        /// <summary>Set once the stub is genuinely inside the streaming call.</summary>
        internal static readonly ManualResetEventSlim Entered = new(false);

        public ChatClientMetadata Metadata => new("Parking");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // One update first, so the round is unambiguously streaming rather than stuck in setup.
            yield return new ChatResponseUpdate(ChatRole.Assistant, "thinking ");
            Entered.Set();
            // Park. The ONLY way out is this token — which is what makes a passing drain meaningful:
            // it proves the pool's cancellation actually reached the model call.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "unreachable");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    #endregion
}
