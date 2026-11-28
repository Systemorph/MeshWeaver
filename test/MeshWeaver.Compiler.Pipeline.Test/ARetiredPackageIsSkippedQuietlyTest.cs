using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// MeshWeaver#8619 — <b>a package the registry proved retired is skipped quietly, not parked with an
/// Error on every boot.</b> The shape is the #7336 leftover (a partition root declaring a module
/// version, nothing tracking it, no bundle, no adoption), which the untracked-module gate parks with
/// an Error. Two such types differ in exactly one fact: the registry's retired declaration covers the
/// first partition and not the second (the negative control — an undeclared leftover still parks, so
/// the quiet skip is never a loophole for anything the registry did not declare).
/// </summary>
public class ARetiredPackageIsSkippedQuietlyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SeededMvid = "0000dddd0000dddd";
    private const string DeclaredVersion = "1.1";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private NodeTypeCompileParkRegistry Parks => Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private async Task CreateVersionedRoot(string partition)
    {
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(new MeshNode(partition)
                {
                    NodeType = "Space",
                    Name = partition,
                    State = MeshNodeState.Active,
                    Content = JsonSerializer.SerializeToElement(new { description = "a module's partition root", version = DeclaredVersion }),
                })
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var read = await NodeTypeCompilationHelpers
            .ReadModuleVersion(Mesh, Access, $"{partition}/Widget", logger: null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        read.Should().Be(((string?)DeclaredVersion, true));
    }

    private async Task CreateType(string path)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = path[(path.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Ok,
                LatestAssemblyMvid = SeededMvid,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                AdoptedSourceFingerprint = null,
                BuildProvenance = BuildProvenance.Compiled,
                CurrentModuleVersion = DeclaredVersion,
            },
        };
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task RequestForcedRelease(string path)
    {
        using (Access.ImpersonateAsSystem())
            await Mesh.GetMeshNodeStream(path)
                .Update<NodeTypeDefinition>(d => d with
                {
                    RequestedReleaseAt = DateTimeOffset.UtcNow,
                    RequestedReleaseForce = true,
                    RequestedReleaseBy = "operator",
                })
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<NodeTypeDefinition> Settled(string path)
    {
        var node = await Mesh.GetMeshNodeStream(path).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && d.CompilationStatus == CompilationStatus.Error,
                "the request settles on the gate", cancellationToken: TestContext.Current.CancellationToken);
        return node!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
    }

    [Fact]
    public async Task ARetiredPackagesTypes_SettleWithoutAPark_WhileAnUndeclaredLeftoverStillParks()
    {
        const string retired = "oldpkg/Widget";
        const string leftover = "leftover/Widget";
        await CreateVersionedRoot("oldpkg");
        await CreateVersionedRoot("leftover");
        await CreateType(retired);
        await CreateType(leftover);

        Mesh.ServiceProvider.GetRequiredService<IRetiredPackages>()
            .Declare("Plugins", [new RetiredPackageDeclaration("Plugins", "oldpkg", "retired from the repository")]);

        await RequestForcedRelease(retired);
        var quiet = await Settled(retired);
        quiet.CompilationError.Should().Contain("retired by the registry").And.Contain("oldpkg");
        quiet.LatestAssemblyMvid.Should().Be(SeededMvid, "no Roslyn pass ran");
        Parks.IsParked(retired).Should().BeFalse("a retired package is skipped, never parked as a failure");
        Parks.GetCompileAttemptCount(retired).Should().Be(0, "no compile was dispatched");

        // CONTROL (negative): the registry declared nothing for this partition, so it parks, named.
        await RequestForcedRelease(leftover);
        var parked = await Settled(leftover);
        parked.CompilationError.Should().Contain("no install record").And.Contain("MeshWeaver#7336");
        Parks.IsParked(leftover).Should().BeTrue("an undeclared leftover is still an honest park");
    }
}
