using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Part 1 of the fresh-pod compile-race hardening: the startup pre-warm
/// (<see cref="DynamicTypePreWarmer"/>) activates every dynamic NodeType's hub so its
/// Roslyn compile is front-loaded rather than firing on a user's first request.
///
/// <para>The contract this pins: the warm-up ENUMERATES and drives ALL dynamic types,
/// a type that fails to compile does NOT block a good one (both are reported, the good
/// one reaches a usable build), and the whole thing COMPLETES within budget — it never
/// hangs, so it could never wedge a readiness gate.</para>
/// </summary>
public class DynamicTypePreWarmerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "PreWarmTest";
    private const string GoodPath = $"{Partition}/GoodType";
    private const string BrokenPath = $"{Partition}/BrokenType";

    [Fact(Timeout = 120_000)]
    public async Task WarmDynamicTypes_DrivesGoodType_AndBrokenTypeDoesNotBlockIt()
    {
        // A good dynamic type — a trivial identity Configuration compiles to a usable
        // assembly (HasUsableBuild → true).
        await NodeFactory.CreateNode(new MeshNode("GoodType", Partition)
        {
            Name = "Good Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Compiles cleanly.",
                Configuration = "config => config"
            }
        }).Should().Within(30.Seconds()).Emit();

        // A broken dynamic type — invalid C#; its compile settles at Error.
        await NodeFactory.CreateNode(new MeshNode("BrokenType", Partition)
        {
            Name = "Broken Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Never compiles.",
                Configuration = "config => this is not valid C# at all (("
            }
        }).Should().Within(30.Seconds()).Emit();

        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("PreWarmTest");

        // Wait only for OUR two types among the outcomes (Take(2) + Timeout on the
        // ASSERT). The broken type must not stop the good one from being reported — if
        // a failure blocked the Merge, we'd never receive both and would time out.
        var outcomes = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, logger, perTypeBudget: TimeSpan.FromSeconds(90))
            .Where(o => o.TypePath == GoodPath || o.TypePath == BrokenPath)
            .Take(2)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(100))
            .ToTask();

        var good = outcomes.Single(o => o.TypePath == GoodPath);
        var broken = outcomes.Single(o => o.TypePath == BrokenPath);

        Output.WriteLine($"Good  → {good.Status} {good.Detail}");
        Output.WriteLine($"Broken → {broken.Status} {broken.Detail}");

        good.Status.Should().Be(PreWarmStatus.Compiled,
            "the pre-warmer must drive a healthy dynamic type to a usable compiled build");
        broken.Status.Should().Be(PreWarmStatus.CompileError,
            "a non-compiling type surfaces a bounded CompileError — never a hang, never blocking the good type");
    }

    /// <summary>
    /// 🚨 THE STORM GUARD. The warm-up must never activate several cold NodeType hubs at once.
    ///
    /// <para>It shipped with a concurrency knob defaulting to 4, and 4 is measurably harmful: on
    /// 2026-07-28 04:05 four compiles fired at memex in quick succession — the identical load shape
    /// — and within minutes SIX plugin roots fell to the "did not settle" overlay and needed a
    /// scale-to-zero. That is the storm this class's own summary cites as the reason it ships
    /// disabled, and the same one behind the 2026-07-22 "told I am dead" restart loop.</para>
    ///
    /// <para>The knob is now GONE rather than set to 1: the sweep runs through <c>Concat</c> in
    /// dependency order, so concurrency is structurally impossible instead of merely defaulted
    /// away — a dependency order cannot be honoured while its members run in parallel. What is
    /// still a tunable value, and so still worth pinning, is the PACING between types: the damage
    /// only shows at production scale, where no test runs.</para>
    /// </summary>
    [Fact]
    public void TypesArePaced_AndThereIsNoConcurrencyKnobToTurnUp()
    {
        DynamicTypePreWarmer.BetweenTypes.Should().BeGreaterThan(TimeSpan.Zero,
            "back-to-back cold activations still read as a burst even when strictly sequential");
        DynamicTypePreWarmer.BetweenTypes.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "…but the sweep must still finish in a sensible time on a mesh with many types");

        typeof(DynamicTypePreWarmer).GetField("DefaultMaxConcurrency").Should().BeNull(
            "re-introducing a concurrency knob would silently re-enable the storm — the sweep is "
            + "sequential by construction now");
        typeof(DynamicTypePreWarmer)
            .GetMethod(nameof(DynamicTypePreWarmer.WarmDynamicTypes))!
            .GetParameters().Should().NotContain(p => p.ParameterType == typeof(int),
                "no int knob on the warm entry point — concurrency is not a dial any more");
    }

    /// <summary>
    /// FAIL GRACEFULLY DOWNSTREAM — exercised through the warmer itself, not a re-implementation of
    /// its loop in the test.
    ///
    /// <para>A type that draws sources from a BROKEN type cannot build: its assembly would be
    /// missing exactly the sources the upstream owns. It must therefore be reported immediately as
    /// <see cref="PreWarmStatus.UpstreamFailed"/>, NAMING the blocker, instead of being attempted
    /// and burning its whole per-type budget on a guaranteed failure. And the blast radius must
    /// stay local: an unrelated healthy type still compiles.</para>
    ///
    /// <para>This closes the gap the pure graph tests cannot: they pin
    /// <c>FirstBlockedBy</c> and the topological order, but the propagation only holds because the
    /// warmer adds each SKIPPED type to the failed set as it goes. That wiring lives here.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task WarmDynamicTypes_SkipsDependentOfABrokenType_AndNamesTheBlocker()
    {
        const string upstream = $"{Partition}Down/BrokenUpstream";
        const string dependent = $"{Partition}Down/Dependent";
        const string unrelated = $"{Partition}Down/Unrelated";

        await NodeFactory.CreateNode(new MeshNode("BrokenUpstream", $"{Partition}Down")
        {
            Name = "Broken Upstream",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Never compiles — the upstream everything downstream waits on.",
                Configuration = "config => this is not valid C# at all (("
            }
        }).Should().Within(30.Seconds()).Emit();

        // Draws source OUT of the broken type's subtree — that is the dependency edge.
        await NodeFactory.CreateNode(new MeshNode("Dependent", $"{Partition}Down")
        {
            Name = "Dependent",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Compiles the upstream's source into its own assembly.",
                Configuration = "config => config",
                Sources = ["namespace:Source scope:subtree", $"shared=@{upstream}/Source"]
            }
        }).Should().Within(30.Seconds()).Emit();

        await NodeFactory.CreateNode(new MeshNode("Unrelated", $"{Partition}Down")
        {
            Name = "Unrelated",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Depends on nothing.",
                Configuration = "config => config"
            }
        }).Should().Within(30.Seconds()).Emit();

        var logger = Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("PreWarmDownstream");

        var outcomes = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, logger, perTypeBudget: TimeSpan.FromSeconds(90))
            .Where(o => o.TypePath == upstream || o.TypePath == dependent || o.TypePath == unrelated)
            .Take(3)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(110))
            .ToTask();

        foreach (var o in outcomes)
            Output.WriteLine($"{o.TypePath} → {o.Status} {o.Detail}");

        outcomes.Single(o => o.TypePath == upstream).Status
            .Should().Be(PreWarmStatus.CompileError);

        var blocked = outcomes.Single(o => o.TypePath == dependent);
        blocked.Status.Should().Be(PreWarmStatus.UpstreamFailed,
            "a dependent of a broken type must be skipped up front, not attempted for a full "
            + "per-type budget on a build that cannot succeed");
        blocked.Detail.Should().Contain(upstream,
            "the outcome must NAME the blocker — 'something upstream failed' is not actionable");

        outcomes.Single(o => o.TypePath == unrelated).Status
            .Should().Be(PreWarmStatus.Compiled,
                "a broken type contains its blast radius to its own dependents; unrelated types "
                + "must still be warmed");
    }
}
