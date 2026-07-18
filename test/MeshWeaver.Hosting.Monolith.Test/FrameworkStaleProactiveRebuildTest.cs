using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Repro for issue #464, Defect 1: after a platform self-update changes the framework version,
/// a dynamic NodeType whose cached assembly was built against the PREVIOUS framework must be
/// rebuilt PROACTIVELY by its OWN hub — without waiting for an instance to be activated and
/// without a manual Compile click.
///
/// <para><b>Root cause.</b> A framework-stale NodeType is persisted as
/// <see cref="CompilationStatus.Ok"/> with the OLD <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>,
/// so nothing re-drives it: the first-build kickoff needs a <c>null</c> status, the recovery
/// kickoff needs <c>Compiling</c>, and the framework-stale self-heal in
/// <c>NodeTypeEnrichmentHelpers</c> only fires when an INSTANCE of the type is activated. A
/// NodeType with no live instances therefore stays stale (and <c>compile</c> / CreateRelease
/// up-to-date checks report it clean) — a runtime <c>MissingMethodException</c> timebomb — until
/// an operator manually rebuilds it.</para>
///
/// <para><b>The fix</b> adds an owner-side, level-triggered kickoff in
/// <c>NodeTypeCompilationHelpers.InstallCompileWatcher</c>: when the NodeType's own hub observes
/// a settled Ok/Error state whose cached assembly is framework-stale, it flips
/// <see cref="CompilationStatus.Pending"/> so the compile watcher rebuilds it against the CURRENT
/// framework. This test forces the framework-stale shape on the NodeType (NO instance activated),
/// then requires the NodeType to re-stamp the CURRENT framework version on its own. Before the fix
/// nothing rebuilds and the wait for convergence times out.</para>
/// </summary>
public class FrameworkStaleProactiveRebuildTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    // Two real Roslyn compiles (baseline + the proactive framework-stale rebuild) — widen the
    // watchdog to match, like FrameworkStaleInstanceRenderTest.
    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(90);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(180);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 180_000)]
    public async Task FrameworkStaleNodeType_ProactivelyRebuilds_WithoutInstanceActivation()
    {
        var typeId = $"ProactiveStale{Guid.NewGuid():N}";
        var nodeTypePath = $"type/{typeId}";

        var source = $$"""
            public record {{typeId}} { public string Title { get; init; } = string.Empty; }

            public static class {{typeId}}Config
            {
                public static MeshWeaver.Messaging.MessageHubConfiguration Configure(
                    MeshWeaver.Messaging.MessageHubConfiguration config) => config;
            }
            """;

        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        };
        await MeshService.CreateNode(typeNode).Should().Emit();
        await MeshService.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
        {
            NodeType = "Code",
            Name = "code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = source, Language = "csharp" }
        }).Should().Emit();

        var workspace = Mesh.GetWorkspace();

        // 1. Baseline: wait for the first-build compile to settle Ok with a usable build, and
        //    capture the REAL (live) framework version it stamped.
        await Mesh.Observe(new GetCompilationPathRequest(), o => o.WithTarget(new Address(nodeTypePath)))
            .Should().Within(90.Seconds()).Emit();
        var okNode = await workspace.GetMeshNodeStream(nodeTypePath)
            .Should().Within(60.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && d.LastCompileSucceededAt is not null
                && !string.IsNullOrEmpty(d.LatestAssemblyPath)
                && !string.IsNullOrEmpty(d.CompiledFrameworkVersion));
        var baselineDef = (NodeTypeDefinition)okNode.Content!;
        var realFv = baselineDef.CompiledFrameworkVersion!;
        var baselineSucceededAt = baselineDef.LastCompileSucceededAt!.Value;
        Output.WriteLine($"Baseline compile Ok — real framework version '{realFv}', succeededAt {baselineSucceededAt:O}.");

        // 2. Force the framework-stale shape: stamp a bogus CompiledFrameworkVersion while leaving
        //    Status=Ok and the assembly fields intact — exactly what a binary redeploy leaves
        //    behind. NO instance of the type is ever activated, so the reactive enrichment
        //    self-heal never runs: only the proactive OWNER-side kickoff can recover this.
        var bogusFv = $"STALE-{Guid.NewGuid():N}";
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Update(curr => curr.Content is NodeTypeDefinition d
                ? curr with { Content = d with { CompiledFrameworkVersion = bogusFv } }
                : curr)
            .Should().Emit();
        // 🚧 Barrier: confirm the bogus stamp actually LANDED before waiting for convergence.
        // GetMeshNodeStream replays the latest snapshot, so without this the convergence Match
        // below could match the pre-stamp baseline Ok (still carrying realFv) and pass without
        // any rebuild — masking a disabled kickoff. Observing bogusFv proves the stale state is
        // the current one, so realFv can only reappear via a genuine recompile.
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Should().Within(20.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d && d.CompiledFrameworkVersion == bogusFv);
        Output.WriteLine($"Forced framework-stale (bogus framework version '{bogusFv}').");

        // 3. The NodeType's OWN hub must proactively rebuild and re-stamp the CURRENT framework
        //    version — Status back to Ok, a usable assembly, and a STRICTLY NEWER
        //    LastCompileSucceededAt than the baseline (proving a genuine fresh compile, not a
        //    replayed old Ok) — WITHOUT any instance activation. Before the fix nothing re-drives
        //    the stale-Ok type, so this times out.
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && d.CompiledFrameworkVersion == realFv
                && !string.IsNullOrEmpty(d.LatestAssemblyPath)
                && d.LastCompileSucceededAt is { } s && s > baselineSucceededAt);
        Output.WriteLine("NodeType proactively rebuilt against the current framework version.");
    }
}
