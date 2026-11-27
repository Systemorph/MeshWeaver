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
/// The untracked-module gate must judge a prebuilt adoption by IDENTITY, not by node version.
///
/// <para><b>Measured</b> on a client portal's 4.0.14338 pod: eleven types adopted from a prebuilt bundle
/// were parked milliseconds later ("no prebuilt build for it landed"). The adoption's own write bumps
/// the node past the version the adoption observed, and so does any later bookkeeping write (a Pending
/// flip, a release request), so a coverage test written as "snapshot version at or below the observed
/// version" read every such record as not covered. Coverage is now "the record still names the build
/// this process adopted, under the live framework, and the source has not moved" — see
/// <see cref="NodeTypeAdoptionRegistry.StampNaming"/>.</para>
///
/// </summary>
public class AnAdoptionIsCoveredByIdentityNotByVersionTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
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

    private static NodeTypeDefinition AdoptedStampOf(string path, string? fingerprint = null) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = $"adopted/{path.Replace('/', '_')}.dll",
        LatestAssemblyMvid = AdoptedMvid,
        LastCompiledVersion = 38,
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        AdoptedSourceFingerprint = fingerprint,
        BuildProvenance = BuildProvenance.AdoptedVerified,
    };

    private async Task CreateModuleType(string path)
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
                LatestAssemblyMvid = "0000cccc0000cccc",
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                AdoptedSourceFingerprint = "bundlefingerprint0000000",
                BuildProvenance = BuildProvenance.AdoptedVerified,
            },
        };
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem())
            await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task Update(string path, Func<NodeTypeDefinition, NodeTypeDefinition> change)
    {
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem())
            await Mesh.GetMeshNodeStream(path)
                .Update<NodeTypeDefinition>(change)
                .Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

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

    /// <summary>
    /// The adoption lands (its write bumps the node past the version it observed), THEN a bookkeeping
    /// write moves the record again (release request + Pending), THEN the gate checks. By version the
    /// record reads "not covered"; by identity it still names the adopted build.
    /// </summary>
    private async Task AdoptThenBookkeep(string path, string? liveFingerprint, bool force)
    {
        await CreateModuleType(path);
        var hold = Adoptions.HoldBootSeeding();
        try
        {
            var observed = (await Mesh.GetMeshNodeStream(path).Should().Within(TestTimeouts.Convergence)
                .Match(n => n is not null, cancellationToken: TestContext.Current.CancellationToken)).Version;
            var stamp = AdoptedStampOf(path, "bundlefingerprint0000000");
            await Update(path, d => d with
            {
                CompilationStatus = CompilationStatus.Ok,
                LatestAssemblyCollection = stamp.LatestAssemblyCollection,
                LatestAssemblyPath = stamp.LatestAssemblyPath,
                LatestAssemblyMvid = stamp.LatestAssemblyMvid,
                LastCompiledVersion = stamp.LastCompiledVersion,
            });
            Adoptions.RecordAdopted(path, observed, stamp);

            // Bookkeeping after the adoption: moves the version past ObservedVersion + 1.
            await Update(path, d => d with
            {
                CurrentSourceFingerprint = liveFingerprint,
                // A write composed from a pre-adoption read of the record puts the old (empty)
                // coordinates back and flips Pending: the adoption's bytes are no longer named.
                LatestAssemblyCollection = null,
                LatestAssemblyPath = null,
                LatestAssemblyMvid = null,
                LastCompiledVersion = null,
                CompiledFrameworkVersion = null,
                CompilationStatus = CompilationStatus.Pending,
                RequestedReleaseAt = DateTimeOffset.UtcNow,
                RequestedReleaseForce = force,
                RequestedReleaseBy = "operator",
            });
        }
        finally
        {
            hold.Dispose();
        }
    }

    [Fact]
    public async Task ABookkeepingWriteAfterTheAdoption_DoesNotMakeTheGateParkIt()
    {
        const string path = "untracked/Purchase";
        await AdoptThenBookkeep(path, "bundlefingerprint0000000", force: false);

        (await ReachesError(path, TimeSpan.FromSeconds(6))).Should().BeFalse(
            "the record still names the build this process adopted under the live framework; a later "
            + "write to the record is bookkeeping, not a reason to park");
        Parks.IsParked(path).Should().BeFalse();
    }

    [Fact]
    public async Task ARealSourceChangeAfterTheAdoption_StillReachesTheGate()
    {
        const string path = "untracked/Gallery";
        await AdoptThenBookkeep(path, "moved-source-fingerprint", force: false);

        (await ReachesError(path, TestTimeouts.CrossSilo)).Should().BeTrue(
            "negative control: the live sources moved past the bytes the adoption installed, so the "
            + "adoption no longer covers the type and the untracked-module gate judges it");
    }

    // ---- the identity rule itself, pure ------------------------------------------------------------

    private static NodeTypeAdoptionRegistry Recorded(string path, NodeTypeDefinition stamp)
    {
        var registry = new NodeTypeAdoptionRegistry();
        registry.RecordAdopted(path, observedVersion: 10, stamp);
        return registry;
    }

    private static readonly string Live = NodeTypeCompilationHelpers.FrameworkVersion;

    [Fact]
    public void StampNaming_CoversTheSameBuildAtAnyLaterVersion_AndIgnoresBookkeeping()
    {
        var stamp = AdoptedStampOf("T/A", "fp");
        var registry = Recorded("T/A", stamp);
        registry.StampNaming("T/A", stamp with
        {
            CompilationStatus = CompilationStatus.Pending,
            RequestedReleaseAt = DateTimeOffset.UtcNow,
            CurrentSourceFingerprint = "fp",
        }, Live).Should().NotBeNull();
    }

    [Fact]
    public void StampNaming_RefusesADifferentBuild_AForcedRelease_MovedSource_AndAForeignFramework()
    {
        var stamp = AdoptedStampOf("T/A", "fp");
        var registry = Recorded("T/A", stamp);
        registry.StampNaming("T/A", stamp with { LatestAssemblyPath = "other.dll" }, Live)
            .Should().BeNull("a compile or a later adoption replaced the build under the live framework");
        registry.StampNaming("T/A", stamp with { LastCompiledVersion = 39 }, Live).Should().BeNull();
        registry.StampNaming("T/A", stamp with { RequestedReleaseForce = true }, Live)
            .Should().BeNull("a forced release asks for the live source");
        registry.StampNaming("T/A", stamp with { CurrentSourceFingerprint = "moved" }, Live)
            .Should().BeNull("the source moved past the bytes");
        registry.StampNaming("T/A", stamp with { CompiledFrameworkVersion = "old" }, Live)
            .Should().NotBeNull("another framework's coordinates are a clobber, not a newer build");
        registry.StampNaming("T/A", new NodeTypeDefinition { Configuration = "config => config" }, Live)
            .Should().NotBeNull("a record with no coordinates is a clobber, not a newer build");
        registry.StampNaming("T/B", stamp, Live).Should().BeNull("nothing was adopted for T/B");
        registry.StampNaming("T/A", null, Live).Should().BeNull();
    }
}
