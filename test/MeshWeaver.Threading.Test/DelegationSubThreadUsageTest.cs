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
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Regression test for GitHub issue #369: a delegation SUB-THREAD's token usage was
/// recorded under the model <c>(unknown)</c> — so its cost priced at $0 and it was
/// invisible in per-model roll-ups.
///
/// <para>Root cause: a delegation sub-thread's round carries a NULL
/// <c>RoundParams.ModelName</c> (the sub-thread's message/composer specify no model),
/// and the MeshWeaver <c>AgentChatClient</c> never stamped <c>ChatResponseUpdate.ModelId</c>,
/// so <c>ThreadExecution</c>'s <c>actualModel</c> stayed null and <c>RecordUsage</c> fell to the
/// <c>"(unknown)"</c> fallback. A top-level thread survived only because its
/// <c>request.ModelName</c> (the composer selection) is non-null. The fix stamps the resolved
/// <c>currentModelName</c> (the model that ACTUALLY ran — the resolved default for a
/// no-model sub-thread) onto every outgoing update, so the sub-thread's usage is keyed by the
/// real model and prices correctly.</para>
///
/// <para>The test seeds a priced, credentialed default model (so the sub-thread — which carries
/// NO model — resolves it at execution), triggers a real <c>delegate_to_agent</c> delegation,
/// and asserts the sub-thread's <c>_Usage</c> satellite is keyed by that real model id (not
/// <c>(unknown)</c>) and prices to a non-zero cost.</para>
/// </summary>
public class DelegationSubThreadUsageTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";

    // A model that carries a built-in ModelPricing default ($1 / $5 per 1M) — so it prices to a
    // non-zero cost, in contrast to "(unknown)" which matches no ModelPricing entry → $0.
    private const string ModelId = "claude-haiku-4-5";
    private const string UsageModelKey = "claude_haiku_4_5";  // TokenUsage sanitises non-alnum → '_'

    // Sub-agent (Worker) token counts. Distinct so an in/out swap is caught.
    private const int SubInTokens = 100;
    private const int SubOutTokens = 50;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Streaming-heavy delegation rounds leave fire-and-forget completion writes
            // in flight after the terminal Status; widen the quiesce budget (matches
            // DelegationWriteCountTest) so dispose doesn't trip on them.
            .ConfigureHub(c => c.WithQuiesceTimeout(TimeSpan.FromSeconds(15)))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory, UsageDelegationFactory>();
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
    public async Task DelegatedSubThread_RecordsUsageUnderRealModel_AndPricesNonZero()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // 1) Seed a priced, credentialed model so the sub-thread (which carries NO model)
        //    resolves it as the DEFAULT at execution.
        await SeedDefaultModel();

        // 2) Warm the credential resolver until the seeded model IS the resolvable default —
        //    otherwise the sub-thread's ApplyStaleModelFallback has nothing to resolve to and
        //    currentModelName stays null.
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.ResolveDefaultModelId())
            .Should().Within(30.Seconds()).Match(id => id == ModelId);

        // 3) Create the parent thread + submit a message that triggers a delegation to Worker.
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "trigger delegation", "TestUser");
        var create = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).Should().Within(30.Seconds()).Emit();
        create.Message.Success.Should().BeTrue(create.Message.Error ?? "");
        var parentThreadPath = create.Message.Node!.Path!;

        client.SubmitMessage(parentThreadPath, "do it", contextPath: ContextPath);

        // 4) Locate the parent's response cell, then the delegation sub-thread under it.
        var parentMsgIds = await workspace.GetMeshNodeStream(parentThreadPath)
            .Select(c => (c?.Content as MeshThread)?.Messages ?? (IReadOnlyList<string>)ImmutableList<string>.Empty)
            .Should().Within(30.Seconds()).Match(ids => ids.Count >= 2);
        var parentRespPath = $"{parentThreadPath}/{parentMsgIds[1]}";

        var subThreads = await MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{parentRespPath} nodeType:{ThreadNodeType.NodeType}"))
            .Should().Within(60.Seconds()).Match(c => c.Items.Count >= 1);
        subThreads.Items.Should().ContainSingle("the parent delegated exactly once");
        var subThreadPath = subThreads.Items[0].Path!;
        Output.WriteLine($"Sub-thread: {subThreadPath}");

        // 5) Wait for the sub-thread's round to COMPLETE (so RecordUsage has fired) before reading
        //    its usage — a point stream read on a not-yet-created satellite errors rather than waits.
        await workspace.GetMeshNodeStream(subThreadPath)
            .Select(n => n?.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(60.Seconds())
            .Match(t => !t!.IsExecuting && t.Messages.Count >= 2);

        // 6) Read the sub-thread's TokenUsage satellite — keyed by the REAL model, NOT "(unknown)".
        //    Pre-fix this node would be at .../_Usage/_unknown_. RecordUsage lands fire-and-forget
        //    shortly after the terminal status, so poll (absence-tolerant) until it materialises.
        var usagePath = $"{subThreadPath}/{TokenUsageNodeType.SatelliteSegment}/{UsageModelKey}";
        var usage = await Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
            .SelectMany(_ => workspace.GetMeshNodeStream(usagePath)
                .Take(1)
                .Select(n => n?.Content as TokenUsage)
                .Catch((Exception _) => Observable.Return<TokenUsage?>(null)))
            .Where(u => u is not null && u.InputTokens == SubInTokens && u.OutputTokens == SubOutTokens)
            .Select(u => u!)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(45))
            .ToTask();

        // 6) The fix: the sub-thread's usage carries the real model id and prices to > 0.
        usage.Model.Should().Be(ModelId, "the sub-thread's usage must be keyed by the model that actually ran");
        usage.Model.Should().NotBe("(unknown)");

        var rate = ModelPricing.Default(usage.Model);
        rate.Should().NotBeNull("a real model id resolves a ModelPricing rate (unlike '(unknown)')");
        rate!.Cost(usage.InputTokens, usage.OutputTokens).Should().BeGreaterThan(0m,
            "the sub-thread's tokens must price to a non-zero cost");

        // Guard the contrast the bug was about: "(unknown)" resolves NO price → $0.
        ModelPricing.Default("(unknown)").Should().BeNull();
    }

    /// <summary>
    /// Seeds a global-catalog ModelProvider (with a usable key) + a priced LanguageModel so
    /// <see cref="ChatClientCredentialResolver.ResolveDefaultModelId"/> returns it as the default.
    /// </summary>
    private async Task SeedDefaultModel()
    {
        const string providerPath = $"{ModelProviderNodeType.RootNamespace}/TestClaudeProvider";

        await NodeFactory.CreateNode(new MeshNode("TestClaudeProvider", ModelProviderNodeType.RootNamespace)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = "TestClaudeProvider",
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = "TestClaude",
                ApiKey = "sk-test-key-369",
                Endpoint = "https://example.invalid/v1",
                Label = "Test provider (#369)",
                CreatedAt = DateTimeOffset.UtcNow,
                Models = ImmutableArray.Create(ModelId)
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode(ModelId, providerPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = ModelId,
            State = MeshNodeState.Active,
            Content = new ModelDefinition
            {
                Id = ModelId,
                Provider = "TestClaude",
                ProviderRef = providerPath,
                // Lowest order → the default the resolver's ResolveDefaultModelId picks.
                Order = -1000
            }
        }).Should().Within(30.Seconds()).Emit();
    }

    #region Fake agents — parent delegates once to Worker; Worker emits text + usage

    /// <summary>
    /// Parent (default = Assistant): turn 1 emits a <c>delegate_to_agent</c> call to Worker;
    /// turn 2+ streams a short wrap-up. A call counter keeps it deterministic across chat-client
    /// wrappers (whether FunctionResultContent is echoed back varies).
    /// </summary>
    private sealed class DelegatingParentClient : IChatClient
    {
        private int _streamingCallCount;

        public ChatClientMetadata Metadata => new("DelegatingParent");

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

            foreach (var word in "Delegation complete.".Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Delay(5, cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    /// <summary>
    /// Worker sub-agent: streams a short text reply, then a <see cref="UsageContent"/> carrying
    /// the scripted token counts — so <c>ThreadExecution</c> records usage on the sub-thread's
    /// per-model satellite. This client sets NO ModelId (the exact production condition); the
    /// model must be supplied by the AgentChatClient's resolved <c>currentModelName</c>.
    /// </summary>
    private sealed class UsageSubAgentClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("UsageSubAgent");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "sub reply")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "alpha beta gamma");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[]
            {
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = SubInTokens,
                    OutputTokenCount = SubOutTokens,
                    TotalTokenCount = SubInTokens + SubOutTokens
                })
            });
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private sealed class UsageDelegationFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
    {
        public override string Name => "UsageDelegationFactory";
        public override IReadOnlyList<string> Models => [ModelId];
        public override int Order => 0;

        protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
            => agentConfig.IsDefault
                ? new DelegatingParentClient()
                : new UsageSubAgentClient();
    }

    #endregion
}
