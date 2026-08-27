using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 An agent tool call MUST observe the cancellation token it is invoked with, and MUST settle on
/// every terminal of the source it waits on.
///
/// <para><b>The defect (#1956, the per-site audit #1908 deferred).</b> Five tools bridged an
/// observable to a <c>Task</c> through a hand-rolled <see cref="TaskCompletionSource{TResult}"/>
/// with a 2-argument <c>Subscribe</c>. <c>VersionPlugin</c>'s four tools had no
/// <see cref="CancellationToken"/> parameter at all; <c>SkillTool</c>, <c>PlanStorageTool</c> and
/// <c>CollaborationPlugin</c> declared one and never referenced it (in <c>CollaborationPlugin</c>
/// the only mentions were XML doc comments). So the returned task could not be cancelled, and an
/// empty completion settled nothing.</para>
///
/// <para><b>Why that is a teardown defect and not a slow tool.</b> The whole round runs as a leaf on
/// the bounded <c>IoPoolNames.Ai</c> pool, holding one gate permit for its entire duration.
/// <c>IoPool.Drain()</c> — the join every teardown orchestrator performs before disposing the
/// service scope and unloading collectible node ALCs — cancels the pool token and re-acquires every
/// permit. A round parked inside an uncancellable tool call never reaches the code that would
/// notice: <c>Drain</c> sits out its full 30&#160;s budget and teardown proceeds over live code.
/// It also makes the Stop button a lie — a user cancelling mid tool call fires the round's
/// <c>executionCts</c>, which reaches the tool as exactly this token.</para>
///
/// <para>These tests invoke the tools through their <see cref="AIFunction"/> surface — the same
/// surface the agent loop uses, and the one that binds the invocation token — so they compile
/// unchanged against the unfixed code and fail on BEHAVIOUR rather than on a signature.</para>
///
/// <para><b>What is proven where.</b> The four version tools are driven against an injected version
/// store that accepts the read and never answers, which is the real park; all four fail on
/// <c>origin/main</c> with a <see cref="TimeoutException"/> because nothing can end the wait. The
/// other four sites now share ONE bridge with them (<see cref="ToolTask.Bridge{T}"/>), whose
/// terminals — including the empty completion behind a <c>Timeout</c> — are pinned by
/// <see cref="ToolTaskSettlementTest"/>.</para>
/// </summary>
public class AgentToolCancellationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — the per-test container costs more than these tests do.</summary>
    protected override bool ShareMeshAcrossTests => true;

    /// <summary>
    /// A version store that ACCEPTS the read and then never answers — the shape of a stalled
    /// storage leaf. It records disposal so the tests can assert the read actually stopped, not
    /// merely that the caller stopped waiting.
    /// </summary>
    private sealed class ParkingVersionQuery : IVersionQuery
    {
        private int disposals;
        private int subscriptions;
        /// <summary>How many times a subscription to this store has been disposed.</summary>
        public int Disposals => Volatile.Read(ref disposals);

        /// <summary>
        /// How many times this store has been SUBSCRIBED — i.e. how many calls have actually
        /// reached the park. The disposal assertions depend on a call having got this far, and
        /// nothing else in the pipeline tells you that it did; see
        /// <see cref="AssertParkedThenCancellable"/>.
        /// </summary>
        public int Subscriptions => Volatile.Read(ref subscriptions);

        private IObservable<T> Park<T>() => Observable.Create<T>(_ =>
        {
            Interlocked.Increment(ref subscriptions);
            return Disposable.Create(() => Interlocked.Increment(ref disposals));
        });

        public IObservable<MeshNodeVersion> GetVersions(string path) => Park<MeshNodeVersion>();
        public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options) => Park<MeshNode?>();
        public IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options) => Park<MeshNode?>();
    }

    private static readonly ParkingVersionQuery VersionStore = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IVersionQuery>(VersionStore);
                return services;
            })
            // A real, routable partition. load_skill's park needs the ADDRESS to resolve while the
            // NODE is absent — that is the case the framework's own comments describe ("for a path
            // that does not exist yet that hub never activates"). An unregistered partition instead
            // fails routing outright, which is an answer, not a park.
            .AddMeshNodes(new MeshNode("Skills", "") { Name = "Skills", NodeType = "Markdown", Content = "seed" })
            .AddGraph()
            .AddAI();

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (name, value) in values)
            dict[name] = value;
        return new AIFunctionArguments(dict);
    }

    /// <summary>
    /// Asserts that <paramref name="call"/> is genuinely parked, then that cancelling the round
    /// unwinds it promptly. Every failure mode is distinguishable: a call that resolves before the
    /// cancel fails the first check, and the bounded wait's own giving-up is a
    /// <see cref="TimeoutException"/>, which is NOT an <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task AssertParkedThenCancellable(
        Task call, CancellationTokenSource round, string because, int storeSubscriptionsBefore)
    {
        var ct = TestContext.Current.CancellationToken;

        // 🚨 Wait for the call to actually REACH the park, on the store's own signal (#2346).
        //
        // Every one of these tools runs `GateOnRead(path).SelectMany(canRead => versionQuery…)` —
        // a permission read against the mesh that must COMPLETE before the parking store is
        // subscribed at all. This used to proceed on a fixed 500 ms sleep, which establishes only
        // that the call has not answered — and a call still inside `GateOnRead` has not answered
        // either. Cancel there and the store was never subscribed, so there is nothing to dispose
        // and `Disposals` never moves: `Expected 1 to be greater than 1`, in 0.7 s, on a loaded
        // runner and never locally. The sleep was standing in for a condition it could not see.
        //
        // Waiting on the store's subscription count makes the precondition the assertion depends on
        // an observed fact. Reactive poll because the source is a counter, not an observable — the
        // shape WritingTests.md sanctions for exactly that case.
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Where(_ => VersionStore.Subscriptions > storeSubscriptionsBefore)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask(ct);

        // Now the negative check means what it says: the call is parked IN THE STORE and the store
        // never answers, so nothing may settle it. (A sleep is correct here — this is the sanctioned
        // "confirm nothing happened" case, where there is no positive signal to wait for.)
        var settledEarly = await Task.WhenAny(call, Task.Delay(500, ct));
        settledEarly.Should().NotBeSameAs(call,
            "the source never answers — the tool call must not resolve before the round is cancelled");

        await round.CancelAsync();

        var act = async () => await call.WaitAsync(5.Seconds(), ct);
        await act.Should().ThrowAsync<OperationCanceledException>(because);
    }

    /// <summary>
    /// <c>get_versions</c> against a version store that never answers.
    ///
    /// <para><b>Non-vacuity.</b> On <c>origin/main</c> <c>VersionPlugin</c> has no
    /// <see cref="CancellationToken"/> parameter anywhere in the file, so nothing can end this wait
    /// — the bounded assertion below fails with a <see cref="TimeoutException"/> instead of the
    /// <see cref="OperationCanceledException"/> asserted here.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GetVersions_ParkedOnAStalledStore_UnwindsWhenTheRoundIsCancelled()
    {
        var before = VersionStore.Disposals;
        var subscribedBefore = VersionStore.Subscriptions;
        var plugin = new VersionPlugin(Mesh);
        var tool = (AIFunction)plugin.CreateTools()[0];
        using var round = new CancellationTokenSource();

        var call = tool.InvokeAsync(Args(("path", "TestPartition/some-doc")), round.Token).AsTask();

        await AssertParkedThenCancellable(call, round,
            "a round parked in get_versions holds an Ai-pool gate permit that IoPool.Drain() cannot "
            + "re-acquire, so teardown proceeds over live code", subscribedBefore);

        VersionStore.Disposals.Should().BeGreaterThan(before,
            "cancelling must dispose the version read — otherwise the storage work runs on unobserved");
    }

    /// <summary>
    /// <c>get_version</c> — same store, same contract. Covered separately because each of the four
    /// version tools has its own bridge, and the audit found the token missing from all four.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GetVersion_ParkedOnAStalledStore_UnwindsWhenTheRoundIsCancelled()
    {
        var subscribedBefore = VersionStore.Subscriptions;
        var plugin = new VersionPlugin(Mesh);
        var tool = (AIFunction)plugin.CreateTools()[1];
        using var round = new CancellationTokenSource();

        var call = tool.InvokeAsync(Args(("path", "TestPartition/some-doc"), ("version", 3L)), round.Token).AsTask();

        await AssertParkedThenCancellable(call, round, "get_version must observe the round's token", subscribedBefore);
    }

    /// <summary>
    /// <c>restore_version</c> — the write-side twin; a parked restore is the worst one to leave
    /// running, since teardown may unload the assemblies it is writing through.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RestoreVersion_ParkedOnAStalledStore_UnwindsWhenTheRoundIsCancelled()
    {
        var subscribedBefore = VersionStore.Subscriptions;
        var plugin = new VersionPlugin(Mesh);
        var tool = (AIFunction)plugin.CreateTools()[2];
        using var round = new CancellationTokenSource();

        var call = tool.InvokeAsync(Args(("path", "TestPartition/some-doc"), ("version", 3L)), round.Token).AsTask();

        await AssertParkedThenCancellable(call, round, "restore_version must observe the round's token", subscribedBefore);
    }

    /// <summary>
    /// <c>restore_from_point_in_time</c> — the fourth version tool.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RestoreFromPointInTime_ParkedOnAStalledStore_UnwindsWhenTheRoundIsCancelled()
    {
        var subscribedBefore = VersionStore.Subscriptions;
        var plugin = new VersionPlugin(Mesh);
        var tool = (AIFunction)plugin.CreateTools()[3];
        using var round = new CancellationTokenSource();

        var call = tool.InvokeAsync(
            Args(("path", "TestPartition/some-doc"), ("timestamp", "2026-03-25T14:30:00Z")), round.Token).AsTask();

        await AssertParkedThenCancellable(call, round, "restore_from_point_in_time must observe the round's token", subscribedBefore);
    }

    /// <summary>
    /// <c>load_skill</c> against a path no node owns must ANSWER — naming the path — rather than
    /// leave the round waiting.
    ///
    /// <para>Scope note, stated so the coverage is not overclaimed: on this in-memory mesh an
    /// absent node fails ROUTING (<c>DeliveryFailureException</c>), so the read errors rather than
    /// parks and this case answers on <c>origin/main</c> too. <c>SkillTool</c>'s two genuinely
    /// missing terminals — the round's token and the EMPTY completion that
    /// <c>Timeout</c> passes straight through — are pinned in
    /// <see cref="ToolTaskSettlementTest"/> against the exact pipeline shape it builds
    /// (<c>Where → Take(1) → Timeout</c>), because a mesh cannot be driven into an empty stream
    /// completion from a test. The four version tools below carry the tool-level proof.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task LoadSkill_AbsentSkillNode_Answers()
    {
        var tool = (AIFunction)SkillTool.Create(Mesh, new NoopAgentChat());

        var result = await tool.InvokeAsync(Args(("skillPath", "Skills/NoSuchSkill")),
                TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(20.Seconds(), TestContext.Current.CancellationToken);

        result?.ToString().Should().Contain("Skills/NoSuchSkill");
    }

    /// <summary>
    /// The happy paths still answer: cancellation wiring must not swallow a real result, and an
    /// absent node must produce the ABSENCE answer rather than silence.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task LoadSkill_WithNoPath_StillAnswersImmediately()
    {
        var tool = (AIFunction)SkillTool.Create(Mesh, new NoopAgentChat());

        var result = await tool.InvokeAsync(Args(("skillPath", "  ")), TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        result?.ToString().Should().Contain("search nodeType:Skill");
    }

    /// <summary>Minimal <see cref="IAgentChat"/> stub — the tools under test read only Context/ExecutionContext.</summary>
    private sealed class NoopAgentChat : IAgentChat
    {
        public AgentContext? Context => null;

        public void SetContext(AgentContext? applicationContext) => throw new NotImplementedException();
        public void SetSelectedAgent(string? agentName) => throw new NotImplementedException();
        public Task ResumeAsync(AI.Persistence.ChatConversation conversation) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync() => throw new NotImplementedException();
        public IAsyncEnumerable<ChatMessage> GetResponseAsync(
            IReadOnlyCollection<ChatMessage> messages,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IReadOnlyCollection<ChatMessage> messages,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void SetThreadId(string threadId) => throw new NotImplementedException();
        public void DisplayLayoutArea(MeshWeaver.Layout.LayoutAreaControl layoutAreaControl) => throw new NotImplementedException();
    }
}
