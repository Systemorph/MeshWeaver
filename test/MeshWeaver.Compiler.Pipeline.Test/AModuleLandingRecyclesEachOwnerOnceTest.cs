using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A module landing recycles each owner address ONCE (#6729 recurrence).
///
/// <para>Measured on memex-cloud 2026-10-15 00:49Z, on pods already running the #6756 fix: one
/// landing that swapped two modules live killed <c>Hosting/PlatformBuilds</c> as three distinct
/// activations inside two seconds — once per NodeType definition whose <see cref="RecycleCascade"/>
/// the swap's wave set off (<c>Hosting/InstanceAction</c>, <c>Hosting/Build</c>), once more for the
/// second module's own wave — and every sync stream on it gave up on "4 DISTINCT owner activations".
/// The subscriber read it correctly: the address really was recycling in a loop. These cases pin the
/// two producers of that loop.</para>
///
/// <para>The modules are real Roslyn emits installed through the real <c>MeshBuilder</c>; the
/// mesh, its hubs and the recycles are real. Nothing is mocked: the cascade instrument is the
/// framework's own <see cref="RecycleCascade"/> seam, hung on a real hub.</para>
/// </summary>
public sealed class AModuleLandingRecyclesEachOwnerOnceTest : MonolithMeshTestBase
{
    private const string ModuleA = "MeshWeaver.Test.WaveA";
    private const string ModuleB = "MeshWeaver.Test.WaveB";
    private const string ProbePath = "WaveProbe";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "live-wave-" + Guid.NewGuid().ToString("N"));
    // Written when the mesh is configured — which the base runs from ITS constructor, before this
    // class's constructor body.
    private string? a1Path;
    private string? b1Path;
    private string A1 => a1Path ??= Write(ModuleA, "g1", SourceA(version: 1));
    private string B1 => b1Path ??= Write(ModuleB, "g1", SourceB(reachesEveryHub: false));

    /// <summary>Creates the test over the shared monolith mesh base.</summary>
    public AModuleLandingRecyclesEachOwnerOnceTest(ITestOutputHelper output) : base(output)
    {
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(A1, B1);

    private ModuleLiveUpdater Updater => Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>();

    /// <summary>
    /// A hub torn down by a swap's recycle wave does NOT run its <see cref="RecycleCascade"/>: the
    /// wave already named every hub of this process bound to the swapped generation, and a cascade on
    /// top of it reaches the same addresses again, seconds later, after their subscribers re-activated
    /// them. Red before the fix: the wave's request carried no <see cref="DisposeRequest.CascadedFrom"/>,
    /// so the seam fired.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ASwapsRecycleWave_DoesNotSetOffARecycleCascade()
    {
        var ct = TestContext.Current.CancellationToken;
        var cascades = new ReplaySubject<DisposeRequest>();
        Hang(await LiveProbe(ct), cascades);

        var outcome = await Updater.Swap(Write(ModuleA, "g2", SourceA(version: 2)), "test: one landing")
            .Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        outcome.Recycled.Should().BeGreaterThan(0, "the probe hub is bound to the swapped generation's node");
        // The swap answers only after every hub of its wave is DEAD, and HandleDispose runs the seam
        // on the recycle's own turn, before the teardown — so by now the seam has fired or never will.
        cascades.OnCompleted();
        var fired = await cascades.ToList().Timeout(Budget).Await(ct);
        fired.Should().BeEmpty(
            "the wave IS the dependency network of a module swap — a cascade from a hub in it tears down "
            + "successor activations the wave already made room for (#6729). Fired for: "
            + string.Join(" | ", fired.Select(r => $"{r.Reason} (cascadedFrom={r.CascadedFrom ?? "(direct)"})")));
    }

    /// <summary>
    /// NEGATIVE CONTROL for the case above: the SAME instrument on the SAME kind of hub fires for a
    /// direct recycle — so its silence above is a measurement, not a seam that never runs.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ADirectRecycle_StillSetsOffTheCascade()
    {
        var ct = TestContext.Current.CancellationToken;
        var cascades = new ReplaySubject<DisposeRequest>();
        var probe = await LiveProbe(ct);
        Hang(probe, cascades);

        Mesh.NodeOperationIssuingHub().Post(new DisposeRequest { Reason = "test: a direct recycle" },
            o => o.WithTarget(new Address(ProbePath)));

        var fired = await cascades.FirstAsync().Timeout(Budget).Await(ct);
        fired.CascadedFrom.Should().BeNull("a direct recycle is the main bit — it cascades");
    }

    /// <summary>
    /// Two modules landing together are ONE recycle wave: the probe, bound to both (module A owns its
    /// node, module B's new generation reaches every per-node hub), is torn down once — not once per
    /// module. A live subscriber holds the probe open throughout, so a second wave would meet the
    /// successor that subscriber re-activated, exactly as on memex-cloud. Red before the fix: one wave
    /// per module.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TwoModulesLandingTogether_RecycleEachOwnerOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await LiveProbe(ct);
        // A live reader of the probe — the subscriber that re-activates it the moment it goes.
        using var reader = Mesh.GetMeshNodeStream(ProbePath).Subscribe(_ => { }, _ => { });

        // Every probe activation created while the swap runs, recorded SYNCHRONOUSLY as the registry
        // adds it (#6800 review): a timer sample could miss a successor created and torn down between
        // two ticks, and the count below would then pass over a double recycle.
        var seen = ImmutableHashSet.Create<IMessageHub>(ReferenceEqualityComparer.Instance, first);
        using var sampler = Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>().HubAdded
            .Where(IsProbe)
            .Subscribe(hub => ImmutableInterlocked.Update(ref seen, s => s.Add(hub)));

        var outcomes = await Updater.SwapAll(
                [Write(ModuleA, "g2", SourceA(version: 2)), Write(ModuleB, "g2", SourceB(reachesEveryHub: true))],
                "test: two modules landed together")
            .Timeout(Budget).Await(ct);

        outcomes.Select(o => o.Kind).Should().OnlyContain(k => k == ModuleSwapKind.Live,
            string.Join(" | ", outcomes.Select(o => o.Reason)));
        (await ReadNode(ProbePath).Timeout(Budget).Await(ct))?.Name.Should().Be("v2",
            "the probe serves module A's new generation after the swap");
        sampler.Dispose();

        var current = HostedProbe();
        var killed = Volatile.Read(ref seen).Where(h => !ReferenceEquals(h, current)).ToImmutableList();
        killed.Should().HaveCount(1,
            "one landing is one recycle: the activation that served the old generations is torn down, and "
            + "the successor its reader re-activated must not be torn down again by a second module's wave "
            + $"(#6729). Torn down: {killed.Count} activation(s)");
    }

    /// <summary>
    /// A later module that FAULTS while it is prepared does not cost the modules committed before it
    /// their wave (#6800 review). The swap collects every module before its one recycle, so a fault
    /// that escaped the second module's preparation used to fail the whole swap: module A was already
    /// current, yet its probe was never recycled and kept serving the old generation, and A was
    /// reported Failed. The fault here is a path no file system accepts (an embedded NUL), which
    /// throws inside the preparation itself.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ALaterModuleThatFaults_DoesNotCostTheCommittedOnesTheirWave()
    {
        var ct = TestContext.Current.CancellationToken;
        await LiveProbe(ct);

        var outcomes = await Updater.SwapAll(
                [Write(ModuleA, "g2", SourceA(version: 2)), Path.Combine(root, "faulting\0module.dll")],
                "test: the second module of one landing faults")
            .Timeout(Budget).Await(ct);

        Output.WriteLine(string.Join(" | ", outcomes.Select(o => $"{o.Module}: {o.Kind} — {o.Reason}")));
        outcomes.Should().HaveCount(2);
        outcomes[0].Kind.Should().Be(ModuleSwapKind.Live,
            "module A committed before the second module faulted — it is current and must be recycled: "
            + outcomes[0].Reason);
        outcomes[0].Recycled.Should().BeGreaterThan(0, "the probe is bound to module A's node");
        outcomes[1].Kind.Should().Be(ModuleSwapKind.Failed, "the faulting module is its own failure: " + outcomes[1].Reason);
        (await ReadNode(ProbePath).Timeout(Budget).Await(ct))?.Name.Should().Be("v2",
            "the probe was recycled onto module A's new generation, not left serving the old one");
    }

    /// <summary>Activates the probe and returns its live hub.</summary>
    private async Task<IMessageHub> LiveProbe(CancellationToken ct)
    {
        (await ReadNode(ProbePath).Timeout(Budget).Await(ct))?.Name.Should().Be("v1",
            "the arrangement: the probe runs module A's boot generation");
        return HostedProbe() ?? throw new InvalidOperationException($"{ProbePath} has no live hub after a read");
    }

    private IMessageHub? HostedProbe() =>
        Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>().Hubs
            .FirstOrDefault(h => IsProbe(h) && h.RunLevel is MessageHubRunLevel.Starting or MessageHubRunLevel.Started);

    private static bool IsProbe(IMessageHub hub) =>
        string.Equals(ActivationRecycle.PathOf(hub), ProbePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Hangs the instrument on <paramref name="hub"/>: the framework's own cascade seam,
    /// recording every request that ran it.</summary>
    private static void Hang(IMessageHub hub, ISubject<DisposeRequest> cascades) =>
        hub.Set(new RecycleCascade(request => cascades.OnNext(request)));

    private static string SourceA(int version) => $$"""
        [assembly: MeshWeaver.Test.WaveA.Module]
        namespace MeshWeaver.Test.WaveA;
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("{{ProbePath}}") { Name = "v{{version}}", NodeType = "Markdown" }];
        }
        """;

    private static string SourceB(bool reachesEveryHub) => $$"""
        [assembly: MeshWeaver.Test.WaveB.Module]
        namespace MeshWeaver.Test.WaveB;
        public sealed record WaveMarker(string Text);
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<System.Func<MeshWeaver.Mesh.MeshBuilder, MeshWeaver.Mesh.MeshBuilder>> BuilderConfigurations =>
            [
                builder => builder
                    {{(reachesEveryHub
                        ? ".ConfigureDefaultNodeHub(config => { config.TypeRegistry.WithType(typeof(WaveMarker), \"WaveMarker\"); return config; })"
                        : "")}}
            ];
        }
        """;

    private string Write(string module, string generation, string source)
    {
        var compilation = CSharpCompilation.Create(module, [CSharpSyntaxTree.ParseText(source)], PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{module}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
