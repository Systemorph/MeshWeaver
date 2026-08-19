using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
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
/// 🚨 The sibling of <see cref="AiPoolDrainJoinsRoundTest"/> for the OTHER place a round parks:
/// inside a <c>delegate_to_agent</c> TOOL CALL, waiting for a sub-agent.
///
/// <para><b>What #1879 fixed, and what it did not.</b> #1879 linked the pool's cancellation token
/// into the round, so a round parked on the MODEL call unwinds when <c>IoPool.Drain()</c> cancels
/// the pool. A round parked in a delegation never reaches that code: it is sitting on the
/// <c>Task&lt;string&gt;</c> the delegation tool returned, and nothing completed that Task when the
/// token fired. Its only exits were the sub-thread reaching a terminal state, a delegation
/// lifecycle event, or <c>WaitForDelegationResult</c>'s <b>10-minute</b> backstop — 20× the drain
/// budget, and the legacy path had no backstop at all.</para>
///
/// <para><b>Why that is a teardown defect.</b> The round holds one <c>IoPoolNames.Ai</c> gate
/// permit for its whole duration. <c>Drain()</c> — the join every teardown orchestrator performs
/// before disposing the service scope and unloading collectible node ALCs — cancels the pool token
/// and then re-acquires every permit, so a parked round makes it sit out its full 30&#160;s
/// <c>DrainTimeout</c> and then report the permit as a leaked leaf, after which teardown proceeds
/// over live code (the use-after-unload SIGSEGV precondition). Observed as
/// <see cref="DelegationSubThreadUsageTest"/> failing in TEARDOWN with
/// <c>teardown DIRTY — 1 pooled I/O leaf(s) still running</c> after a ~32&#160;006&#160;ms dispose,
/// every assertion in its body having passed (#1863; CI run 32271833370, shard 5). That test waits
/// on the SUB-thread's usage satellite, which <c>RecordUsage</c> writes as an independent side
/// effect deliberately NOT chained before the round's terminal write — so it can finish while the
/// PARENT round is still parked in its delegation.</para>
///
/// <para><b>Deterministic by construction.</b> The delegation here NEVER resolves — the factory
/// hands <c>DelegationTool</c> an <c>executeAsync</c> that returns <see cref="Observable.Never{T}"/>
/// — so the only way the drain can return 0 is the round's cancellation genuinely reaching the tool
/// call. Against the unfixed <c>DelegationTool</c> the drain blocks for its whole 30&#160;s budget
/// and returns 1.</para>
/// </summary>
public class DelegationDrainJoinsParkedToolCallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory, ParkedDelegationFactory>();
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
    public async Task DrainingTheAiPool_UnwindsARoundParkedInADelegation()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "delegation drain", "Roland");
        var createResp = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).Should().Within(30.Seconds()).Emit();
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var threadPath = createResp.Message.Node!.Path!;

        // Warm the remote stream before submitting so the IsExecuting transition is not raced
        // (same reason AiPoolDrainJoinsRoundTest and CancelThreadExecutionTest do it).
        await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(10.Seconds()).Match(t => t != null);

        client.SubmitMessage(threadPath, "delegate please", contextPath: ContextPath);

        await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(30.Seconds())
            .Match(t => t is { IsExecuting: true, ActiveMessageId: { Length: > 0 } });

        // The tool call has genuinely been ENTERED — the round is parked inside the delegation,
        // not merely dispatched. Without this the drain could join a leaf that never took a permit,
        // which would pass vacuously.
        ParkedDelegationFactory.DelegationEntered
            .Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken)
            .Should().BeTrue("the delegation tool must be executing before the drain proves anything");
        Output.WriteLine("Round is parked inside delegate_to_agent — draining the Ai pool.");

        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();

        // Drain on another thread: it is synchronous and, pre-fix, blocks for its full 30 s budget.
        // Bounding the wait here turns that into a fast, legible failure instead of a 30 s stall.
        var drain = Task.Run(registry.DrainAll, TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken));
        finished.Should().BeSameAs(drain,
            "Drain must join the round promptly once it cancels the pool token — if it is still "
            + "blocked after 20 s the round is parked inside a delegation Task that nothing "
            + "cancels, and Drain is sitting out its 30 s DrainTimeout before reporting the permit "
            + "as leaked");

        var residual = await drain;
        residual.Should().Be(0,
            "a delegation that observes the round's cancellation token releases the round, which "
            + "releases its gate permit — so the join is real and teardown may unload node ALCs");

        // …and it must unwind as a graceful CANCEL, never as a #147 wall-clock streaming timeout —
        // the same classification AiPoolDrainJoinsRoundTest pins for the model-call park. A pool
        // drain fires the round's linked timeout CTS WITHOUT executionCts, which is byte for byte
        // the shape that used to be converted into "AI streaming exceeded the maximum round
        // duration" and written to the user's response cell.
        var settled = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(30.Seconds()).Match(t => t is { IsExecuting: false });
        settled!.Status.Should().Be(ThreadExecutionStatus.Cancelled,
            "a round stopped by pool drain is a graceful shutdown cancel — including when it was "
            + "waiting on a delegated agent rather than on the model");
    }

    #region A delegation that never resolves — and nothing else

    /// <summary>
    /// The real <see cref="ChatClientAgentFactory"/>, with two seams replaced: the chat client
    /// always emits one <c>delegate_to_agent</c> call, and the delegation tool's launch observable
    /// NEVER emits or completes. Everything between them — ThreadExecution's Ai-pool leaf,
    /// FunctionInvokingChatClient, DelegationTool's Task bridge — is the production path.
    /// </summary>
    private sealed class ParkedDelegationFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
    {
        /// <summary>Set once the delegation launch has genuinely been subscribed.</summary>
        internal static readonly ManualResetEventSlim DelegationEntered = new(false);

        public override string Name => "ParkedDelegationFactory";
        public override IReadOnlyList<string> Models => ["parked-delegation-model"];
        public override int Order => 0;

        protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
            => new DelegatingParentClient();

        protected override IEnumerable<AITool> GetAgentTools(
            AgentConfiguration agentConfig,
            IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> allAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents)
        {
            yield return DelegationTool.CreateUnifiedDelegationTool(
                agentConfig,
                hierarchyAgents,
                executeAsync: (_, _, _, _) => Observable.Create<string>(_ =>
                {
                    DelegationEntered.Set();
                    // Never emits, never completes: the sub-agent is "still working" forever. The
                    // ONLY way out is the tool observing its cancellation token.
                    return Disposable.Empty;
                }));
        }
    }

    /// <summary>Turn 1 delegates; a later turn would stream a wrap-up that this test never reaches.</summary>
    private sealed class DelegatingParentClient : IChatClient
    {
        private int _streamingCallCount;

        public ChatClientMetadata Metadata => new("ParkedDelegationParent");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var callIndex = Interlocked.Increment(ref _streamingCallCount);
            if (callIndex == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call1", "delegate_to_agent",
                        new Dictionary<string, object?>
                        {
                            ["agentName"] = "Worker",
                            ["task"] = "produce a quick reply"
                        })]);
                await Task.Yield();
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "unreachable");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    #endregion
}
