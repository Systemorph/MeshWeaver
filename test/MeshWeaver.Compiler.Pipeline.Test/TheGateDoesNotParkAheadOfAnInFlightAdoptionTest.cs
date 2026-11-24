using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// MeshWeaver#8440: the untracked-module gate (#3583) must not park a type while the boot bundle
/// seeding pass that will adopt it is still running.
///
/// <para><b>Measured.</b> On the build instance's platform-4 roll pod, 2026-11-20 14:06:44.508, the
/// compile watcher parked <c>Essentials/ActionLink</c> ("no prebuilt build for it landed"); at .586
/// the registry pass logged <c>Prebuilt assembly ADOPTED for Essentials/ActionLink</c>. The park wrote
/// Error, the adoption's stamp was held until readiness, readiness waited on the bake, the bake read
/// the park, the dependent <c>Essentials/ActionLinkDefinition</c> went UpstreamFailed and counted as a
/// regression, and every new pod of the roll replayed it.</para>
///
/// <para><b>The ordering, reproduced.</b> The boot seeding is held (as the warm-up holds it), a forced
/// release drives two identical untracked module types with no build into the watcher, and the gate
/// would park both at once. While the hold stands, one of them is ADOPTED — written <c>Ok</c> with
/// usable coordinates, exactly the shape the seeder stamps — and then the hold is released. The adopted
/// type must never have been parked; the other one, which nothing adopted, must still park — the arm
/// that proves the gate was ordered, not disabled. Without the fix both park inside the hold.</para>
/// </summary>
public class TheGateDoesNotParkAheadOfAnInFlightAdoptionTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string StaleMvid = "0000cccc0000cccc";
    private const string AdoptedMvid = "1111dddd1111dddd";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private NodeTypeCompileParkRegistry Parks =>
        Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

    private NodeTypeAdoptionRegistry Adoptions =>
        Mesh.ServiceProvider.GetRequiredService<NodeTypeAdoptionRegistry>();

    private async Task CreateModuleTypeWithoutABuild(string path)
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
                LatestAssemblyMvid = StaleMvid,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                AdoptedSourceFingerprint = "bundlefingerprint0000000",
                BuildProvenance = BuildProvenance.AdoptedVerified,
            },
        };
        using (Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Messaging.AccessService>().ImpersonateAsSystem())
            await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
        await Mesh.GetMeshNodeStream(path).Should().Within(TestTimeouts.Convergence)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.LatestAssemblyMvid, StaleMvid, StringComparison.Ordinal),
                cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task Update(string path, Func<NodeTypeDefinition, NodeTypeDefinition> change)
    {
        using (Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Messaging.AccessService>().ImpersonateAsSystem())
            await Mesh.GetMeshNodeStream(path)
                .Update<NodeTypeDefinition>(change)
                .Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    private Task RequestForcedRelease(string path) => Update(path, d => d with
    {
        RequestedReleaseAt = DateTimeOffset.UtcNow,
        RequestedReleaseForce = true,
        RequestedReleaseBy = "operator",
    });

    /// <summary>What the seeder stamps when it adopts: Ok, usable coordinates for the live identity.</summary>
    private Task Adopt(string path) => Update(path, d => d with
    {
        CompilationStatus = CompilationStatus.Ok,
        CompilationError = null,
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = $"adopted/{path.Replace('/', '_')}.dll",
        LatestAssemblyMvid = AdoptedMvid,
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        BuildProvenance = BuildProvenance.AdoptedVerified,
    });

    /// <summary>Whether the record reaches Error within <paramref name="window"/>.</summary>
    private Task<bool> ReachesError(string path, TimeSpan window)
        => Mesh.GetMeshNodeStream(path)
            .Select(n => n?.Content is NodeTypeDefinition { CompilationStatus: CompilationStatus.Error })
            .Where(error => error)
            .Take(1)
            .Timeout(window)
            .Catch((TimeoutException _) => Observable.Return(false))
            .ObserveCompletion(
                ex => FileOutput.WriteLine($"late fault reading {path}: {ex}"),
                TestContext.Current.CancellationToken);

    [Fact]
    public async Task AnAdoptionThatLandsDuringTheBootSeeding_IsNeverParked_WhileAnUnadoptedTypeStillParks()
    {
        const string adopted = "untracked/ActionLink";
        const string unadopted = "untracked/Email";
        await CreateModuleTypeWithoutABuild(adopted);
        await CreateModuleTypeWithoutABuild(unadopted);

        var hold = Adoptions.HoldBootSeeding();
        try
        {
            await RequestForcedRelease(adopted);
            await RequestForcedRelease(unadopted);

            // ── THE RACE: without the ordering, the gate parks both types right here. ──
            (await ReachesError(adopted, TimeSpan.FromSeconds(5))).Should().BeFalse(
                "the gate must not judge a type while the boot bundle seeding that may adopt it is in flight (#8440)");
            Parks.IsParked(adopted).Should().BeFalse();
            Parks.IsParked(unadopted).Should().BeFalse(
                "no type is parked while the pass runs — the ordering holds for every type, not only the adopted one");

            // The pass reaches the type and adopts it.
            await Adopt(adopted);
        }
        finally
        {
            hold.Dispose();
        }

        // ── After the pass: the adopted type is served by its adoption, never parked. ──
        (await ReachesError(adopted, TimeSpan.FromSeconds(5))).Should().BeFalse(
            "an adoption that landed during the pass is the build; nothing parks over it");
        Parks.IsParked(adopted).Should().BeFalse();
        var adoptedNode = await Mesh.GetMeshNodeStream(adopted).Should().Within(TestTimeouts.Convergence)
            .Match(n => n?.Content is NodeTypeDefinition { CompilationStatus: CompilationStatus.Ok },
                cancellationToken: TestContext.Current.CancellationToken);
        adoptedNode.Content.Should().BeOfType<NodeTypeDefinition>()
            .Which.LatestAssemblyMvid.Should().Be(AdoptedMvid);

        // ── CONTROL: the type nothing adopted is judged once the pass is done, and parks. ──
        (await ReachesError(unadopted, TestTimeouts.CrossSilo)).Should().BeTrue(
            "the gate is ordered after the pass, not disabled: an untracked module type with no build still parks");
        Parks.IsParked(unadopted).Should().BeTrue();
    }

    [Fact]
    public void TheHold_IsReferenceCounted_AndClearsOnlyWhenTheLastPassEnds()
    {
        var registry = new NodeTypeAdoptionRegistry();
        registry.IsBootSeeding.Should().BeFalse();
        var first = registry.HoldBootSeeding();
        var second = registry.HoldBootSeeding();
        first.Dispose();
        first.Dispose();
        registry.IsBootSeeding.Should().BeTrue("one pass still holds it, and a double dispose releases once");
        second.Dispose();
        registry.IsBootSeeding.Should().BeFalse();
    }
}
