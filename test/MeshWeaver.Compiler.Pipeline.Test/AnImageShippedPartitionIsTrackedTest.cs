using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// MeshWeaver#6930, the 2026-11-20 recurrence: a partition the RUNNING IMAGE materializes through
/// a registered <see cref="IStaticRepoSource"/> (<c>Doc</c> on every portal) is a TRACKED partition
/// for the untracked-module gate (#3583). Its live source is what the image ships — the static-repo
/// import rewrites it on every boot — so compiling it is honest, and it must never be parked as
/// "an install's leftover copy".
///
/// <para><b>Measured.</b> On the company instance, after its roll to platform 4, the pods' shelf
/// held the image documentation's bundles only from a NEWER platform build (4.0.13721 over the
/// running 4.0.13719), so the seeder declined them on their platform range. Four documentation
/// NodeTypes (<c>Doc/Architecture/BusinessRules/Cession</c>,
/// <c>Doc/DataMesh/PythonPandasNode/PandasExplorer</c>, <c>Doc/DataMesh/SocialMedia/Post</c>,
/// <c>Doc/DataMesh/SocialMedia/Profile</c>) held only a platform-3 build, which does not load on
/// platform 4, and the gate parked each at Error: "partition 'Doc' tracks no source on this mesh".
/// The doc pages that render them lost their types.</para>
///
/// <para><b>A controlled experiment, in one mesh.</b> The SAME record — a module's content (adoption
/// provenance), with NO usable build — is created in two partitions that differ in exactly one fact:
/// <c>shipped</c> is materialized by a registered static-repo source, <c>untracked</c> is not. The
/// mesh has the real GitHub tracking provider registered, so the gate is armed; neither partition
/// has a <c>_GitSync</c>. Without the fix BOTH park; with it the shipped one compiles and the
/// untracked one still parks — the arm that proves the gate was not simply disabled.</para>
/// </summary>
public class AnImageShippedPartitionIsTrackedTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string StaleMvid = "0000bbbb0000bbbb";
    private const string ShippedPartition = "shipped";
    private const string AdditivePartition = "additive";

    /// <summary>A static repo the image ships for <see cref="ShippedPartition"/>. It lists no nodes:
    /// the gate reads only the partition it materializes, and no import runs in this mesh.</summary>
    private sealed class ShippedRepo : IStaticRepoSource
    {
        public string Partition => ShippedPartition;
        public bool Versioned => false;
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => [];
        public MeshNode? PartitionRoot => null;
    }

    /// <summary>An image-shipped source that keeps nodes it did not write (the AI catalogs' mode):
    /// a leftover in its partition survives every boot, so it must stay gated.</summary>
    private sealed class AdditiveRepo : IStaticRepoSource
    {
        public string Partition => AdditivePartition;
        public bool Versioned => false;
        public PartitionSyncMode SyncMode => PartitionSyncMode.Additive;
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => [];
        public MeshNode? PartitionRoot => null;
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<IStaticRepoSource, ShippedRepo>();
                services.AddSingleton<IStaticRepoSource, AdditiveRepo>();
                return services;
            });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private NodeTypeCompileParkRegistry Parks =>
        Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

    /// <summary>A module's NodeType whose only build is gone — the shape of the four documentation
    /// types: adoption provenance, no loadable coordinates.</summary>
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

    /// <summary>A forced release request — the door that lands straight on the gate.</summary>
    private async Task RequestForcedRelease(string path)
    {
        using (Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Messaging.AccessService>().ImpersonateAsSystem())
            await Mesh.GetMeshNodeStream(path)
                .Update<NodeTypeDefinition>(d => d with
                {
                    RequestedReleaseAt = DateTimeOffset.UtcNow,
                    RequestedReleaseForce = true,
                    RequestedReleaseBy = "operator",
                })
                .Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>The record once the request settled: Error, or Ok with coordinates a compile wrote.</summary>
    private async Task<NodeTypeDefinition> Settled(string path)
    {
        var node = await Mesh.GetMeshNodeStream(path).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && (d.CompilationStatus == CompilationStatus.Error
                            || (d.CompilationStatus == CompilationStatus.Ok
                                && !string.Equals(d.LatestAssemblyMvid, StaleMvid, StringComparison.Ordinal))),
                "the request settles: a Roslyn pass or a named park",
                cancellationToken: TestContext.Current.CancellationToken);
        return node.Content.Should().BeOfType<NodeTypeDefinition>().Subject;
    }

    [Fact]
    public async Task TheSeam_CountsAnImageShippedPartitionAsTracked_AndNothingElse()
    {
        var shipped = await NodeTypeCompilationHelpers.PartitionTracksSources(Mesh, $"{ShippedPartition}/Widget")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the seam answers once, promptly", cancellationToken: TestContext.Current.CancellationToken);
        shipped.Should().BeTrue("the running image materializes this partition on every boot (#6930)");

        var untracked = await NodeTypeCompilationHelpers.PartitionTracksSources(Mesh, "untracked/Widget")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the seam answers once, promptly", cancellationToken: TestContext.Current.CancellationToken);
        untracked.Should().BeFalse("a partition nothing materializes or syncs is still untracked");

        NodeTypeCompilationHelpers.IsMaterializedFromTheImage(Mesh, "SHIPPED")
            .Should().BeTrue("partitions compare case-insensitively");

        var additive = await NodeTypeCompilationHelpers.PartitionTracksSources(Mesh, $"{AdditivePartition}/Widget")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the seam answers once, promptly", cancellationToken: TestContext.Current.CancellationToken);
        additive.Should().BeFalse(
            "an Additive import keeps nodes the image does not ship, so a leftover there is still a leftover");
    }

    [Fact]
    public async Task AModuleTypeTheImageShips_Compiles_WhileTheSameTypeInAnUntrackedPartitionParks()
    {
        const string shippedType = ShippedPartition + "/Widget";
        const string untrackedType = "untracked/Widget";
        await CreateModuleTypeWithoutABuild(shippedType);
        await CreateModuleTypeWithoutABuild(untrackedType);

        // ── THE ARM UNDER TEST: the image ships the partition → its live source is current. ──
        await RequestForcedRelease(shippedType);
        var compiled = await Settled(shippedType);
        compiled.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the image's own content compiles from the source the image ships (#6930); error: {compiled.CompilationError}");
        compiled.LatestAssemblyMvid.Should().NotBe(StaleMvid, "a real Roslyn pass wrote real coordinates");
        Parks.IsParked(shippedType).Should().BeFalse();

        // ── CONTROL: the same record where nothing materializes the partition still parks. ──
        await RequestForcedRelease(untrackedType);
        var parked = await Settled(untrackedType);
        parked.CompilationStatus.Should().Be(CompilationStatus.Error,
            "the gate stays armed for a partition nothing tracks");
        parked.CompilationError.Should().Contain("partition 'untracked'").And.Contain("tracks no source");
        Parks.IsParked(untrackedType).Should().BeTrue();
    }

    [Fact]
    public void TheDeclinedReason_DoesNotClaimAFingerprintMismatch()
        => PrebuiltAssemblySeeder.UntrackedPartitionParkReason("Doc/DataMesh/Example", offered: true)
            .Should().Contain("DECLINED")
            .And.Contain("names why", "the seeder's per-entry line carries the reason; #6930 measured a platform-range decline")
            .And.NotContain("DECLINED: its source fingerprint");
}
