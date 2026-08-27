using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services.LanguageServer;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 The second half of the #1956 audit: the seventeen tools <see cref="AgentToolCancellationTest"/>
/// did NOT cover.
///
/// <para><b>The defect.</b> Every tool on <see cref="MeshPlugin"/> (thirteen — <c>get</c>,
/// <c>search</c>, <c>create</c>, <c>update</c>, <c>patch</c>, <c>edit_content</c>, <c>delete</c>,
/// <c>move</c>, <c>copy</c>, <c>get_diagnostics</c>, <c>recycle</c>, <c>navigate_to</c>,
/// <c>run_tests</c>) and every tool on <see cref="LspPlugin"/> (four) ended in
/// <c>.FirstAsync().ToTask()</c> — the overload that takes NO
/// <see cref="CancellationToken"/> — and declared no token parameter at all. So the returned
/// <c>Task</c> could not be cancelled by anything: not the Stop button, not
/// <c>IoPool.Drain()</c>, not the round's own timeout.</para>
///
/// <para><b>Why that is a teardown defect and not a slow tool.</b> Identical to the version tools:
/// the round runs as a leaf on the bounded <c>IoPoolNames.Ai</c> pool and holds one gate permit for
/// its whole duration, and <c>Drain()</c> — the join teardown performs before disposing the service
/// scope and unloading collectible node <c>AssemblyLoadContext</c>s — cancels the pool token and
/// re-acquires every permit. A round parked in an uncancellable tool call never reaches the code
/// that would notice. The LSP tools are the sharpest case of it: a speculative Roslyn compilation
/// over a NodeType's whole source set is one of the longest things an agent does, and the running
/// compilation is holding the very ALC teardown is about to unload.</para>
///
/// <para><b>Non-vacuity.</b> The park test drives the tools through their <see cref="AIFunction"/>
/// surface — the same surface the agent loop uses, and the one that binds the invocation token — so
/// it compiles unchanged against the unfixed code and fails on BEHAVIOUR: on <c>origin/main</c>
/// nothing can end the wait and the bounded assertion fails with a <see cref="TimeoutException"/>
/// rather than the <see cref="OperationCanceledException"/> asserted here. The ratchet
/// (<see cref="EveryToolBindsTheRoundsCancellationToken"/>) is the guard that keeps the next tool
/// from re-introducing it.</para>
/// </summary>
public class MeshToolCancellationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — the per-test container costs more than these tests do.</summary>
    protected override bool ShareMeshAcrossTests => true;

    /// <summary>
    /// A language service that ACCEPTS the request and then never answers — the shape of a Roslyn
    /// compilation that does not come back. It records disposal so the tests can assert the
    /// compilation actually STOPPED, not merely that the caller stopped waiting.
    /// </summary>
    private sealed class ParkingLanguageService : IMeshLanguageService
    {
        private int disposals;
        private int subscriptions;

        /// <summary>How many subscriptions to this service have been disposed.</summary>
        public int Disposals => Volatile.Read(ref disposals);

        /// <summary>
        /// How many calls have actually REACHED the park. The disposal assertion depends on a call
        /// having got this far and nothing else tells you that it did — see
        /// <see cref="AssertParkedThenCancellable"/>, and #2346 for why a sleep cannot stand in.
        /// </summary>
        public int Subscriptions => Volatile.Read(ref subscriptions);

        private IObservable<T> Park<T>() => Observable.Create<T>(_ =>
        {
            Interlocked.Increment(ref subscriptions);
            return Disposable.Create(() => Interlocked.Increment(ref disposals));
        });

        public IObservable<NodeDiagnosticsOutcome> GetDiagnostics(string nodeTypePath) => Park<NodeDiagnosticsOutcome>();

        public IObservable<HoverInfo?> GetHover(string nodeTypePath, string sourcePath, SourcePosition position) =>
            Park<HoverInfo?>();

        public IObservable<IReadOnlyList<CompletionEntry>> GetCompletions(
            string nodeTypePath, string sourcePath, SourcePosition position, int maxResults = 20) =>
            Park<IReadOnlyList<CompletionEntry>>();

        public IObservable<IReadOnlyList<CompletionEntry>> GetCompletions(
            string nodeTypePath, string sourcePath, string sourceText, SourcePosition position, int maxResults = 20) =>
            Park<IReadOnlyList<CompletionEntry>>();

        public IObservable<IReadOnlyList<DiagnosticInfo>> CheckSpeculative(
            string nodeTypePath, string sourcePath, string proposedCode) =>
            Park<IReadOnlyList<DiagnosticInfo>>();

        public void RecordCompletionAccepted(string prefix, string label, CompletionKind kind) { }

        public void Evict(string nodeTypePath) { }
    }

    private static readonly ParkingLanguageService LanguageService = new();

    // 🚨 The parking service is registered on the HUB, and AFTER AddGraph() — both halves matter.
    // AddGraph() registers the real MeshNodeLanguageService inside
    // `ConfigureHub(c => c.WithServices(...))`, i.e. in the hub's own container, which shadows a
    // mesh-level `builder.ConfigureServices` registration entirely; and within one container the
    // LAST plain AddSingleton is what GetService<T>() returns. Register at the wrong level or in the
    // wrong order and the tests silently drive the REAL compiler, which ANSWERS ("No workspace for
    // 'ACME/Story' — read status Absent") rather than parking — so the park precondition is never
    // established and the failure reads as a timeout in the assertion helper.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddGraph()
            .AddAI()
            .ConfigureHub(config => config.WithServices(services =>
                services.AddSingleton<IMeshLanguageService>(LanguageService)));

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (name, value) in values)
            dict[name] = value;
        return new AIFunctionArguments(dict);
    }

    /// <summary>
    /// Asserts <paramref name="call"/> is genuinely parked, then that cancelling the round unwinds
    /// it promptly. Every failure mode stays distinguishable: a call that resolves before the cancel
    /// fails the first check, and the bounded wait's own giving-up is a
    /// <see cref="TimeoutException"/>, which is NOT an <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task AssertParkedThenCancellable(
        Task call, CancellationTokenSource round, string because, int subscriptionsBefore)
    {
        var ct = TestContext.Current.CancellationToken;

        // Wait for the call to actually REACH the park, on the service's own signal — never on a
        // fixed sleep, which establishes only that the call has not answered yet (#2346).
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Where(_ => LanguageService.Subscriptions > subscriptionsBefore)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask(ct);

        // Now the negative check means what it says: the call is parked IN THE SERVICE and the
        // service never answers, so nothing may settle it. (A sleep is correct here — the sanctioned
        // "confirm nothing happened" case, where there is no positive signal to wait for.)
        var settledEarly = await Task.WhenAny(call, Task.Delay(500, ct));
        settledEarly.Should().NotBeSameAs(call,
            "the language service never answers — the tool call must not resolve before the round is cancelled");

        await round.CancelAsync();

        var act = async () => await call.WaitAsync(5.Seconds(), ct);
        await act.Should().ThrowAsync<OperationCanceledException>(because);
    }

    private LspPlugin Lsp() => new(Mesh, new NoopChat());

    /// <summary><c>LspCheckNode</c> — the speculative Roslyn compile, against a service that never answers.</summary>
    [Fact(Timeout = 60_000)]
    public async Task LspCheckNode_ParkedOnAStalledCompiler_UnwindsWhenTheRoundIsCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var disposalsBefore = LanguageService.Disposals;
        var subscribedBefore = LanguageService.Subscriptions;
        var tool = (AIFunction)Lsp().CreateTools()[0];
        using var round = new CancellationTokenSource();

        var call = tool.InvokeAsync(
            Args(("nodeTypePath", "ACME/Story"), ("sourcePath", "ACME/Story/Source/S.cs"), ("proposedCode", "class S{}")),
            round.Token).AsTask();

        await AssertParkedThenCancellable(call, round,
            "a round parked in LspCheckNode holds an Ai-pool gate permit that IoPool.Drain() cannot "
            + "re-acquire, so teardown unloads the ALC the compilation is still running in",
            subscribedBefore);

        // 🚨 WAIT for the disposal; do not read the counter and hope (#2346). ToolTask.Bridge
        // disposes before it settles, but the pipeline hops schedulers, and Rx runs a sequence's
        // UNSUBSCRIBE on the scheduler it subscribed on — so the teardown lands shortly after the
        // caller's task has already thrown. The contract is "cancelling disposes the compile"; the
        // way to observe an asynchronous fact is to wait for it, bounded, so a disposal that never
        // comes still fails loudly rather than hanging.
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Where(_ => LanguageService.Disposals > disposalsBefore)
            .FirstAsync()
            .Timeout(20.Seconds())
            .ToTask(ct);

        LanguageService.Disposals.Should().BeGreaterThan(disposalsBefore,
            "cancelling must dispose the speculative compilation — otherwise it runs on unobserved");
    }

    /// <summary><c>LspDiagnosticsForNode</c> — same service, same contract.</summary>
    [Fact(Timeout = 60_000)]
    public async Task LspDiagnosticsForNode_ParkedOnAStalledCompiler_UnwindsWhenTheRoundIsCancelled()
    {
        var subscribedBefore = LanguageService.Subscriptions;
        var tool = (AIFunction)Lsp().CreateTools()[1];
        using var round = new CancellationTokenSource();

        var call = tool.InvokeAsync(Args(("nodeTypePath", "ACME/Story")), round.Token).AsTask();

        await AssertParkedThenCancellable(call, round,
            "LspDiagnosticsForNode must observe the round's token", subscribedBefore);
    }

    /// <summary><c>LspHoverForNode</c> — the third of the four.</summary>
    [Fact(Timeout = 60_000)]
    public async Task LspHoverForNode_ParkedOnAStalledCompiler_UnwindsWhenTheRoundIsCancelled()
    {
        var subscribedBefore = LanguageService.Subscriptions;
        var tool = (AIFunction)Lsp().CreateTools()[2];
        using var round = new CancellationTokenSource();

        var call = tool.InvokeAsync(
            Args(("nodeTypePath", "ACME/Story"), ("sourcePath", "ACME/Story/Source/S.cs"),
                 ("line", 0), ("character", 0)),
            round.Token).AsTask();

        await AssertParkedThenCancellable(call, round,
            "LspHoverForNode must observe the round's token", subscribedBefore);
    }

    /// <summary><c>LspCompletionsForNode</c> — the fourth.</summary>
    [Fact(Timeout = 60_000)]
    public async Task LspCompletionsForNode_ParkedOnAStalledCompiler_UnwindsWhenTheRoundIsCancelled()
    {
        var subscribedBefore = LanguageService.Subscriptions;
        var tool = (AIFunction)Lsp().CreateTools()[3];
        using var round = new CancellationTokenSource();

        var call = tool.InvokeAsync(
            Args(("nodeTypePath", "ACME/Story"), ("sourcePath", "ACME/Story/Source/S.cs"),
                 ("line", 0), ("character", 0)),
            round.Token).AsTask();

        await AssertParkedThenCancellable(call, round,
            "LspCompletionsForNode must observe the round's token", subscribedBefore);
    }

    /// <summary>
    /// The positive control the cancellation tests need: an UNcancelled mesh tool still answers.
    /// Without this, "everything throws OperationCanceledException" would pass the tests above.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MeshTool_WithALiveToken_StillAnswers()
    {
        var plugin = new MeshPlugin(Mesh, new NoopChat());

        var answer = await plugin.Search("nodeType:Markdown", limit: 1, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        answer.Should().NotBeNullOrWhiteSpace("the search must still produce its JSON envelope");
    }

    /// <summary>
    /// A round that is ALREADY cancelled when the tool is called must come back cancelled rather
    /// than doing the work.
    ///
    /// <para>The token is registered before the source is subscribed
    /// (<c>ToolTask.Bridge</c>), which is what makes an already-cancelled token settle first.
    /// On <c>origin/main</c> this test does not compile at all: <c>MeshPlugin.Get</c> had no
    /// <see cref="CancellationToken"/> parameter to pass — which is the defect stated as a
    /// signature rather than as a hang.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MeshTool_WithAnAlreadyCancelledRound_DoesNotRun()
    {
        var plugin = new MeshPlugin(Mesh, new NoopChat());
        using var round = new CancellationTokenSource();
        await round.CancelAsync();

        var act = async () => await plugin.Get("Doc/Overview", round.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a tool called with a cancelled round must unwind instead of holding its Ai-pool permit");
    }

    /// <summary>
    /// 🚨 THE RATCHET. Every agent tool these plugins expose must bind the round's cancellation
    /// token — checked on the tool objects themselves, so a new tool added without one fails here
    /// rather than in a teardown three months later.
    ///
    /// <para><c>AIFunctionFactory</c> binds a <see cref="CancellationToken"/> parameter from the
    /// invocation and keeps it OUT of the JSON schema, so declaring one costs the model nothing —
    /// there is no reason for an AWAITING tool not to have it, and therefore no exemption list.</para>
    ///
    /// <para>The obligation is on tools that WAIT, i.e. whose method returns a <see cref="Task"/> or
    /// <see cref="ValueTask"/>; a tool that computes its answer synchronously and returns a
    /// <see cref="string"/> (<c>handoff_to_agent</c>, <c>submit_message</c>, <c>check_inbox</c>) has
    /// no wait to cancel, so the check is keyed off the return type rather than listing exceptions.
    /// Scope: the five plugins that can be constructed from (hub, chat) alone — the tools built by
    /// static factories with wider dependencies are not reachable from here.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void EveryToolBindsTheRoundsCancellationToken()
    {
        var chat = new NoopChat();
        var tools = new List<AITool>();
        tools.AddRange(new MeshPlugin(Mesh, chat).CreateAllTools());
        tools.AddRange(new LspPlugin(Mesh, chat).CreateTools());
        tools.AddRange(new VersionPlugin(Mesh).CreateTools());
        tools.AddRange(new CollaborationPlugin(Mesh, chat).CreateTools());
        tools.AddRange(new AgentFilesPlugin(Mesh, chat).CreateTools());

        tools.Should().NotBeEmpty("the plugins under test must actually expose tools");

        static bool Awaits(Type returnType) =>
            typeof(Task).IsAssignableFrom(returnType)
            || returnType == typeof(ValueTask)
            || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>));

        var missing = tools
            .OfType<AIFunction>()
            .Where(f => f.UnderlyingMethod is not null)
            .Where(f => Awaits(f.UnderlyingMethod!.ReturnType))
            .Where(f => !f.UnderlyingMethod!.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken)))
            .Select(f => $"{f.UnderlyingMethod!.DeclaringType?.Name}.{f.UnderlyingMethod!.Name}")
            .ToList();

        tools.OfType<AIFunction>().Count(f => Awaits(f.UnderlyingMethod?.ReturnType ?? typeof(void)))
            .Should().BeGreaterThan(20,
                "the ratchet must actually be looking at the awaiting tools — a filter that matches "
                + "nothing passes silently");

        missing.Should().BeEmpty(
            "an agent tool that cannot be cancelled parks its round's IoPoolNames.Ai gate permit "
            + "through IoPool.Drain(), so teardown proceeds over live code and the Stop button lies");
    }

    /// <summary>Minimal <see cref="IAgentChat"/> stub — the tools under test read only Context/ExecutionContext.</summary>
    private sealed class NoopChat : IAgentChat
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
