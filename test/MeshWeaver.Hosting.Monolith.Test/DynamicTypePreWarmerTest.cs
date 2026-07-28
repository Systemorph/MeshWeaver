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
    /// 🚨 THE STORM GUARD. The warm-up must default to ONE activation at a time.
    ///
    /// <para>It shipped defaulting to 4, and 4 is measurably harmful: on 2026-07-28 04:05 four
    /// compiles fired at memex in quick succession — the identical load shape — and within minutes
    /// SIX plugin roots fell to the "did not settle" overlay and needed a scale-to-zero. That is the
    /// storm this class's own summary cites as the reason it ships disabled, and the same one behind
    /// the 2026-07-22 "told I am dead" restart loop.</para>
    ///
    /// <para>Concurrency buys nothing here — the compiles already serialize on the Compile IoPool —
    /// so the only thing it adds is simultaneous cold ACTIVATIONS, which is the expensive part
    /// (issue #686: the same NodeType shape is 0ms discovery on a fresh mesh and 45.20s on memex).
    /// Pinned as a value because the damage only shows up at production scale, where no test runs.</para>
    /// </summary>
    [Fact]
    public void DefaultConcurrency_IsOne_AndTypesArePaced()
    {
        DynamicTypePreWarmer.DefaultMaxConcurrency.Should().Be(1,
            "warming several cold NodeType hubs at once is what knocks roots offline; the sweep has "
            + "no deadline, so it must trickle");

        DynamicTypePreWarmer.BetweenTypes.Should().BeGreaterThan(TimeSpan.Zero,
            "back-to-back cold activations still read as a burst even at concurrency 1");
        DynamicTypePreWarmer.BetweenTypes.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "…but the sweep must still finish in a sensible time on a mesh with many types");
    }
}
