using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins #1707 slice 3's "if yes, we take it; if no, we generate" at the release watcher: a
/// release request that arrives while the node already holds a VALID build of the CURRENT
/// sources is SATISFIED — the trigger is consumed (<c>LastReleaseRequestHandledAt</c> stamps)
/// without dispatching Roslyn — while <c>force</c> keeps its documented contract
/// (<c>RequestNodeTypeRelease(force: true)</c> "bypass the sources-unchanged short-circuit and
/// always run a fresh compile") and recompiles anyway.
///
/// <para>The no-recompile assertion is <c>LastCompileSucceededAt</c> staying EXACTLY the first
/// build's stamp: any dispatched compile — even of identical sources — rewrites it, so an
/// unchanged value is positive proof no compile ran, not an absence of evidence.</para>
/// </summary>
public class ReleaseRequestSatisfiedByCurrentBuildTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly string _cacheDir = Path.Combine(
        Path.GetTempPath(), $"MeshWeaverSatisfiedReleaseTest-{Guid.NewGuid():N}");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_cacheDir);
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = _cacheDir;
                    o.EnableCompilationCache = true;
                    o.EnableDiskCache = true;
                }));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_cacheDir))
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private const string Code = """
        using MeshWeaver.Layout.Composition;
        public static class SatisfiedLayoutAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>SATISFIED_V1</div>");
        }
        """;

    [Fact(Timeout = 120000)]
    public async Task ReleaseRequest_OnACurrentBuild_IsSatisfiedWithoutCompiling_UnlessForced()
    {
        var typePath = $"{TestPartition}/SatisfiedType";

        // 1. Create the NodeType + a source; the first-build kickoff compiles it.
        await NodeFactory.CreateNode(new MeshNode("SatisfiedType", TestPartition)
        {
            Name = "Satisfied Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Pin for the satisfied release-request path (#1707 slice 3).",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", SatisfiedLayoutAreas.Overview))",
            }
        }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = Code, Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();

        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(50.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && d.LastCompileSucceededAt is not null
                && !d.IsDirty
                && d.RequestedReleaseAt is null);
        var firstBuild = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition)
            .Select(n => (NodeTypeDefinition)n!.Content!)
            .FirstAsync().Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"=== first build settled Ok at {firstBuild.LastCompileSucceededAt:O} ===");

        // 2. A plain release request on the unchanged, current build: SATISFIED — the trigger is
        //    consumed without a compile.
        Mesh.RequestNodeTypeRelease(typePath,
            onError: msg => Output.WriteLine($"release request refused: {msg}"));

        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(30.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.LastReleaseRequestHandledAt is not null
                && d.CompilationStatus == CompilationStatus.Ok);

        var satisfied = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition)
            .Select(n => (NodeTypeDefinition)n!.Content!)
            .FirstAsync().Await(TestContext.Current.CancellationToken);
        satisfied.LastCompileSucceededAt.Should().Be(firstBuild.LastCompileSucceededAt,
            "a satisfied request must not dispatch Roslyn — any compile rewrites this stamp");
        Output.WriteLine("=== plain request satisfied without compiling ===");

        // 3. FORCE keeps its contract: an explicit force compiles even though nothing changed.
        Mesh.RequestNodeTypeRelease(typePath, force: true,
            onError: msg => Output.WriteLine($"forced request refused: {msg}"));

        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(60.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && d.LastCompileSucceededAt is { } later
                && later > firstBuild.LastCompileSucceededAt);
        Output.WriteLine("=== forced request recompiled ===");
    }
}
