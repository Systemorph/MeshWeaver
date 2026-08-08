using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 ONE SOURCE SNAPSHOT PER COMPILE — the discovery cost, and the source-set consistency,
/// that issue #686 is about.
///
/// <para>A compile used to materialize the full source set THREE separate times, each one an
/// independent race between a live <see cref="IMeshService.Query"/> probe and the cached synced
/// query: once to fold <c>MAX(LastModified)</c> for the disk-cache key, once in
/// <c>CompileCore</c> for the actual compile inputs, once more to record
/// <see cref="NodeTypeDefinition.CompiledSources"/>. Counted on this very suite, that was 3
/// discovery races and 5–6 live mesh reads per compile where 1 race and N reads suffice (N =
/// the number of expanded source queries). On a fresh mesh those reads cost ~0 ms, which is why
/// the fresh-mesh comparison in #686 showed nothing; on memex they are cross-partition and were
/// measured at ~0.25 s each, so the multiplication is paid in seconds against a mesh the compile
/// is already loading.</para>
///
/// <para>It was equally a CORRECTNESS bug: three independent observations of a LIVE collection
/// can disagree, so the cache key could be folded from one set while a different set compiled
/// and a third was recorded as <c>CompiledSources</c> — and <c>CompiledSources</c> is exactly
/// what the recompile-needed check compares against. This test pins both halves: the compile
/// reports its discovery as REUSED (not re-run), and the set recorded in <c>CompiledSources</c>
/// is precisely the set the log says was compiled.</para>
/// </summary>
public class CompileSourceSnapshotReuseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 60_000)]
    public async Task Compile_TakesOneSourceSnapshot_AndEveryStageReusesIt()
    {
        const string nodeTypePath = "type/SnapReuse";
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = "SnapReuse",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<SnapReuse>()"
            },
            State = MeshNodeState.Active
        };
        await MeshService.CreateNode(typeNode).Should().Within(30.Seconds()).Emit();

        // TWO source nodes, so "the same set everywhere" is a claim with content.
        await MeshService.CreateNode(SourceNode(nodeTypePath, "model",
                "public record SnapReuse { public string Title { get; init; } = string.Empty; }"))
            .Should().Within(30.Seconds()).Emit();
        await MeshService.CreateNode(SourceNode(nodeTypePath, "helper",
                "public static class SnapReuseHelper { public static int Answer => 42; }"))
            .Should().Within(30.Seconds()).Emit();

        // Drive the leaf directly with NO sourcesOverride — the first-compile shape, the one
        // that used to re-discover at every stage.
        var compilationService = Mesh.ServiceProvider
            .GetRequiredService<IMeshNodeCompilationService>();
        var result = await compilationService.CompileAndGetConfigurations(typeNode)
            .Should().Within(40.Seconds()).Emit();

        result.Should().NotBeNull();
        result!.AssemblyLocation.Should().NotBeNullOrEmpty(
            $"the compile must succeed; log: {Flatten(result)}");

        var infos = result.Log!.Infos().Select(m => m.Message).ToList();

        // 1. Exactly ONE snapshot is taken, and it is announced as such.
        infos.Should().ContainSingle(m => m.Contains("source snapshot") && m.Contains("reused by every stage"),
            "the compile takes its source snapshot exactly once — a second line here means a "
            + "second discovery race crept back in");

        // 2. The compile stage REUSES it instead of running its own discovery. Before the fix
        //    this line read "(synced query)" — a second independent race over live sources.
        infos.Should().Contain(m => m.Contains("Source discovery took") && m.Contains("reused source snapshot"),
            "CompileCore must consume the already-taken snapshot, not re-discover");
        infos.Should().NotContain(m => m.Contains("Source discovery took") && m.Contains("(synced query)"),
            "a compile that re-enters the synced query is paying discovery twice");

        // 3. The set recorded as CompiledSources is EXACTLY the set the compile consumed.
        //    These came from two independent races before; now they are one snapshot, so the
        //    log's matched-paths line and CompiledSources cannot disagree.
        result.CompiledSources.Should().NotBeNull();
        result.CompiledSources!.Keys.Order().Should().Equal(
            new[] { $"{nodeTypePath}/Source/helper", $"{nodeTypePath}/Source/model" },
            "CompiledSources must record the snapshot the compile actually consumed");

        var matched = infos.Single(m => m.Contains("Source discovery matched"));
        foreach (var path in result.CompiledSources!.Keys)
            matched.Should().Contain(path,
                "every path recorded in CompiledSources must be one the compile reported consuming");
    }

    private static MeshNode SourceNode(string nodeTypePath, string name, string code) =>
        new(name, $"{nodeTypePath}/Source")
        {
            NodeType = "Code",
            Name = name,
            Content = new CodeConfiguration { Code = code, Language = "csharp" },
            State = MeshNodeState.Active
        };

    private static string Flatten(NodeCompilationResult result) =>
        result.Log is null ? "(no log)" : string.Join(" | ", result.Log.Messages.Select(m => m.Message));
}
