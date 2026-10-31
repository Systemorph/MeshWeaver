using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A live module swap's recycle wave does NOT tear a hub down in the middle of a step it owns
/// (#7324). It recycles that hub when the step ends.
///
/// <para>Measured on memex 2026-10-31: the module reload <c>Admin/_ModuleReload/auto-644261f5d8f67ec6</c>
/// was torn down mid-Landing by a <c>ModuleLiveUpdate</c> wave that swapped the modules its own
/// landing had landed. The next activation ran the whole landing again (about 12 minutes), so a
/// reload re-entered Landing once per wave. The executor's step holds its hub through
/// <see cref="MessageHubExtensions.BeginAsyncOperation"/>. This test takes that same hold on a real
/// per-node hub, swaps a module whose next generation reaches EVERY per-node hub, and reads what the
/// wave did with the held hub and with an idle one.</para>
///
/// <para>Real Roslyn-emitted modules, installed through the real <c>MeshBuilder</c>, on the real
/// monolith mesh. Nothing is mocked.</para>
/// </summary>
public sealed class ASwapWaveDefersAHubWithAStepInFlightTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.StepWave";
    private const string HeldPath = "StepWaveHeld";
    private const string IdlePath = "StepWaveIdle";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "step-wave-" + Guid.NewGuid().ToString("N"));
    // Written when the mesh is configured — which the base runs from ITS constructor.
    private string? g1Path;
    private string G1 => g1Path ??= Write("g1", Source(reachesEveryHub: false));

    /// <summary>Creates the test over the shared monolith mesh base.</summary>
    public ASwapWaveDefersAHubWithAStepInFlightTest(ITestOutputHelper output) : base(output)
    {
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(G1);

    private ModuleLiveUpdater Updater => Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>();

    private CollectibleContextUnloads Unloads => Mesh.ServiceProvider.GetRequiredService<CollectibleContextUnloads>();

    /// <summary>
    /// The wave defers the held hub: the swap answers Live while that hub is still running its step
    /// and is not disposing. When the step ends, the SAME wave recycles it, with the wave's own
    /// reason. Red before the fix: the swap answered only after EVERY hub of its wave was dead,
    /// the held one included, so the step was dropped.
    /// <para>The idle hub in the same wave is the NEGATIVE CONTROL. It is recycled by the time the
    /// swap answers, so the wave really did reach these hubs, and the held hub's survival is a
    /// measurement, not a wave that never ran.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AHeldHub_IsRecycledWhenItsStepEnds_AnIdleOneAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var held = await Live(HeldPath, ct);
        var idle = await Live(IdlePath, ct);

        var retirements = Unloads.Retirements;

        // The step: the very hold ModuleReloadExecutor's step takes (StepHold → BeginAsyncOperation).
        var step = held.BeginAsyncOperation();
        try
        {
            var outcome = await Updater.Swap(Write("g2", Source(reachesEveryHub: true)), "test: a wave during a held step")
                .Timeout(Budget).Await(ct);

            outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);

            // Negative control: the idle hub is recycled by the time the swap answers.
            idle.IsDisposing.Should().BeTrue(
                "the idle hub is bound to the swapped generation like the held one, and nothing defers it. "
                + "The wave must recycle it at once. Outcome: " + outcome.Reason);

            // The subject: the held hub is still running its step.
            held.IsDisposing.Should().BeFalse(
                "a hub with a step in flight is recycled when the step ends, never under it (#7324). "
                + $"Torn down by: {(held as MessageHub)?.DisposalAttribution}. Outcome: {outcome.Reason}");
            held.RunLevel.Should().Be(MessageHubRunLevel.Started);
            outcome.Deferred.Should().Be(1, "exactly the held hub was deferred: " + outcome.Reason);
            Unloads.Retirements.Should().Be(retirements,
                "the held hub still runs the old generation, and its step holds no module lease — the old "
                + "generation must not be retired under it (#7727 review). Outcome: " + outcome.Reason);
        }
        finally
        {
            // The step ends, in a finally, as the executor's Observable.Using ends it.
            step.Dispose();
        }

        // …and the wave's deferred recycle fires now: the held hub goes down with the wave's reason.
        await held.RunLevelChanged.LastOrDefaultAsync().Timeout(Budget).Await(ct);
        held.IsDisposing.Should().BeTrue("the held hub is recycled once its step ended");
        ((held as MessageHub)?.DisposalAttribution ?? "").Should().Contain("ModuleLiveUpdate",
            "the deferred recycle is the wave's own request, carrying its reason");

        // …and only now, with every hub of the wave gone, is the old generation retired.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Unloads.Retirements)
            .Where(r => r > retirements)
            .FirstAsync()
            .Timeout(Budget).Await(ct);
    }

    /// <summary>
    /// Two swaps during ONE held step are ONE deferred recycle of that activation, carrying both
    /// waves' reasons (#7727 review). Two would be two address-targeted disposals, and the second
    /// would reach the activation the first made room for. The posts happen synchronously when the
    /// step ends, so they are all recorded by the time <c>Dispose</c> returns.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TwoSwapsDuringOneHeldStep_AreOneDeferredRecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var held = await Live(HeldPath, ct);
        var issued = new System.Collections.Concurrent.ConcurrentQueue<(string Path, string Reason)>();
        using var recorder = Updater.DeferredRecyclesIssued.Subscribe(issued.Enqueue);

        var step = held.BeginAsyncOperation();
        try
        {
            var first = await Updater.Swap(Write("g2", Source(reachesEveryHub: true)), "test: the FIRST wave")
                .Timeout(Budget).Await(ct);
            var second = await Updater.Swap(Write("g3", Source(reachesEveryHub: true)), "test: the SECOND wave")
                .Timeout(Budget).Await(ct);
            first.Kind.Should().Be(ModuleSwapKind.Live, first.Reason);
            second.Kind.Should().Be(ModuleSwapKind.Live, second.Reason);
            held.IsDisposing.Should().BeFalse("both waves defer the held hub: " + second.Reason);
        }
        finally
        {
            step.Dispose();
        }

        var posts = issued.Where(i => string.Equals(i.Path, HeldPath, StringComparison.OrdinalIgnoreCase)).ToList();
        posts.Should().HaveCount(1,
            "one activation, one deferred recycle — however many waves deferred it. Posted: "
            + string.Join(" || ", posts.Select(p => p.Reason)));
        posts[0].Reason.Should().Contain("the FIRST wave");
        posts[0].Reason.Should().Contain("the SECOND wave");
        await held.RunLevelChanged.LastOrDefaultAsync().Timeout(Budget).Await(ct);
    }

    /// <summary>
    /// A held hub that is torn down for ANOTHER reason first gets no deferred recycle at all (#7727
    /// review). The teardown itself releases the step (it is registered for disposal, as the reload
    /// executor's step pipeline is), and that release fires the idle signal. Cancelling on the hub's
    /// DEATH would come too late, and a request posted then would reach the address's next activation.
    /// The deferral ends at the FIRST instant of the shutdown instead.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AHeldHubTornDownOtherwise_GetsNoDeferredRecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var held = await Live(HeldPath, ct);
        var issued = new System.Collections.Concurrent.ConcurrentQueue<(string Path, string Reason)>();
        using var recorder = Updater.DeferredRecyclesIssued.Subscribe(issued.Enqueue);

        // The teardown releases the step, exactly as it ends the executor's step pipeline.
        held.RegisterForDisposal(held.BeginAsyncOperation());
        var outcome = await Updater.Swap(Write("g2", Source(reachesEveryHub: true)), "test: a wave, then an unrelated recycle")
            .Timeout(Budget).Await(ct);
        outcome.Deferred.Should().Be(1, outcome.Reason);

        Mesh.NodeOperationIssuingHub().Post(new DisposeRequest { Reason = "test: an unrelated recycle" },
            o => o.WithTarget(new Address(HeldPath)));
        await held.RunLevelChanged.LastOrDefaultAsync().Timeout(Budget).Await(ct);

        ((held as MessageHub)?.DisposalAttribution ?? "").Should().Contain("an unrelated recycle");
        issued.Where(i => string.Equals(i.Path, HeldPath, StringComparison.OrdinalIgnoreCase)).Should().BeEmpty(
            "the hub was already going down when its step was released — a deferred request posted then "
            + "would tear down the NEXT activation at that address");
    }

    /// <summary>
    /// A step held on a hosted CHILD of the per-node hub defers the per-node hub (#7727 review): the
    /// wave reads the owner's marker, and recycling the owner would tear the child down mid-step.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AStepHeldOnAHostedChild_DefersItsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var held = await Live(HeldPath, ct);
        var child = held.GetHostedHub(new Address("step-child", Guid.NewGuid().ToString("N")), c => c, HostedHubCreation.Always)
                    ?? throw new InvalidOperationException("HostedHubCreation.Always always yields a hub");

        var step = child.BeginAsyncOperation();
        try
        {
            var outcome = await Updater.Swap(Write("g2", Source(reachesEveryHub: true)), "test: a wave during a child's step")
                .Timeout(Budget).Await(ct);
            outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
            held.IsDisposing.Should().BeFalse(
                "the owner of a child with a step in flight is deferred, never recycled under the child: "
                + $"{(held as MessageHub)?.DisposalAttribution}. Outcome: {outcome.Reason}");
            outcome.Deferred.Should().Be(1, outcome.Reason);
        }
        finally
        {
            step.Dispose();
        }
        await held.RunLevelChanged.LastOrDefaultAsync().Timeout(Budget).Await(ct);
    }

    /// <summary>Activates the node at <paramref name="path"/> and returns its live hub.</summary>
    private async Task<IMessageHub> Live(string path, CancellationToken ct)
    {
        (await ReadNode(path).Timeout(Budget).Await(ct)).Should().NotBeNull($"the arrangement: {path} exists");
        return Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>().Hubs
                   .FirstOrDefault(h => string.Equals(ActivationRecycle.PathOf(h), path, StringComparison.OrdinalIgnoreCase)
                                        && h.RunLevel is MessageHubRunLevel.Starting or MessageHubRunLevel.Started)
               ?? throw new InvalidOperationException($"{path} has no live hub after a read");
    }

    private static string Source(bool reachesEveryHub) => $$"""
        [assembly: MeshWeaver.Test.StepWave.Module]
        namespace MeshWeaver.Test.StepWave;
        public sealed record StepWaveMarker(string Text);
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
            [
                new MeshWeaver.Mesh.MeshNode("{{HeldPath}}") { Name = "held", NodeType = "Markdown" },
                new MeshWeaver.Mesh.MeshNode("{{IdlePath}}") { Name = "idle", NodeType = "Markdown" },
            ];

            public override System.Collections.Generic.IEnumerable<System.Func<MeshWeaver.Mesh.MeshBuilder, MeshWeaver.Mesh.MeshBuilder>> BuilderConfigurations =>
            [
                builder => builder
                    {{(reachesEveryHub
                        ? ".ConfigureDefaultNodeHub(config => { config.TypeRegistry.WithType(typeof(StepWaveMarker), \"StepWaveMarker\"); return config; })"
                        : "")}}
            ];
        }
        """;

    private string Write(string generation, string source)
    {
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)], PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Module}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
