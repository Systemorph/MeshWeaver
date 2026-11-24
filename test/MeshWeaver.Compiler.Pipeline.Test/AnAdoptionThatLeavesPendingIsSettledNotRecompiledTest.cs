using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 MeshWeaver#4413 — the combo gate recompiled 69 NodeTypes the bake had already built. The
/// seeder judged a snapshot that predated a release request's Pending flip, found the bytes
/// "already served" and wrote nothing; the compile watcher then waited for status Ok, gave up
/// after its stamp budget and ran Roslyn.
///
/// <para>The consumer here reports one adopted assembly and writes NOTHING — exactly that outcome.
/// A record that names a usable, clean build at Pending must be settled Ok with the build
/// untouched and no compile attempt. The NEGATIVE CONTROL names a build that is NOT usable here
/// (another framework): it must still compile, because a type that cannot adopt is never left
/// stranded at Pending (#2818).</para>
/// </summary>
public class AnAdoptionThatLeavesPendingIsSettledNotRecompiledTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string SeededMvid = "0000aaaa0000aaaa";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<IPrebuiltAssemblyConsumer>(new WritesNothing()));

    private sealed class WritesNothing : IPrebuiltAssemblyConsumer
    {
        public IObservable<int> SeedForTypes(IReadOnlyCollection<string> typePaths) => Observable.Return(1);
    }

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private async Task CreateAt(string path, string framework)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = path[(path.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Pending,
                LatestAssemblyCollection = "local",
                LatestAssemblyPath = "Adopted/v1-x.dll",
                LastCompiledVersion = 1,
                LatestAssemblyMvid = SeededMvid,
                CompiledFrameworkVersion = framework,
                BuildProvenance = BuildProvenance.AdoptedUnverified,
            },
        };
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem())
            await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 120_000)]
    public async Task AUsableCleanBuildAtPending_IsSettledOk_WithoutACompile()
    {
        var path = $"{TestPartition}/Settle{Guid.NewGuid().ToString("N")[..8]}";
        await CreateAt(path, NodeTypeCompilationHelpers.FrameworkVersion);

        var settled = await Mesh.GetMeshNodeStream(path)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(d => d is { CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error },
                "the watcher settles the type", TestContext.Current.CancellationToken);

        settled!.CompilationStatus.Should().Be(CompilationStatus.Ok, $"error: {settled.CompilationError}");
        settled.LatestAssemblyMvid.Should().Be(SeededMvid, "the adopted build is served, not replaced");
        settled.LatestAssemblyPath.Should().Be("Adopted/v1-x.dll");
        Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>()
            .GetCompileAttemptCount(path).Should().Be(0, "no Roslyn pass ran");
    }

    [Fact(Timeout = 120_000)]
    public async Task ABuildThatIsNotUsableHere_StillCompiles_AndIsNeverStranded()
    {
        var path = $"{TestPartition}/Control{Guid.NewGuid().ToString("N")[..8]}";
        await CreateAt(path, "ffffffffffffffff");

        var settled = await Mesh.GetMeshNodeStream(path)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(d => d is { CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error },
                "the type is not left at Pending", TestContext.Current.CancellationToken);

        settled!.LatestAssemblyMvid.Should().NotBe(SeededMvid, "a foreign-framework build cannot be settled in place; it compiles");
    }
}
