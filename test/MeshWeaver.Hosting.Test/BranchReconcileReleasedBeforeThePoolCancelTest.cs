using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #7992 — after the mesh-lifetime catalogs were owned until the IoPool drain, the silo stops on
/// the production portals kept naming one core reader family: <c>GitHubWebhookProcessor.QueryConfigNodesAsSystem</c>
/// legs reached from <c>ReconcileBranches</c> (through <c>TriggerSyncAtCommit</c> and <c>MatchingSyncTargets</c>),
/// still attached 8 to 50 s after they opened, several per stop.
///
/// <para><b>The defect.</b> Their only owner is <see cref="GitSyncBranchReconcileService"/>'s schedule, which a
/// hosted-service <c>StopAsync</c> disposes. On a silo the pools are cancelled by <c>IoPoolSiloTeardown</c> at
/// the silo's stop, before the host reaches that <c>StopAsync</c>, so a reconcile pass in flight at a stop was
/// torn down by the pool with no terminal and reported as unowned. The contract
/// (<c>Doc/Architecture/ControlledIoPooling</c>, "any pooled subscription whose owner can outlive the silo's
/// stop") is that such an owner hands its subscription to <see cref="IoPoolOwnershipExtensions.OwnUntilIoDrain"/>.</para>
///
/// <para><b>Negative control, measured:</b> with the service's <c>OwnUntilIoDrain</c> call reverted, the
/// ownership count does not move on start and the first assertion fails.</para>
/// </summary>
public class BranchReconcileReleasedBeforeThePoolCancelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    [Fact]
    public void ASiloStop_ReleasesTheReconcileSchedule_AndTheHostsLaterStopIsHarmless()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        using var service = new GitSyncBranchReconcileService(
            Mesh, Mesh.ServiceProvider.GetRequiredService<GitHubWebhookProcessor>());

        var owned = registry.OwnedUntilDrainCount;
        service.StartAsync(ct).IsCompletedSuccessfully.Should().BeTrue();
        registry.OwnedUntilDrainCount.Should().Be(owned + 1,
            "the reconcile schedule runs pooled queries and its host StopAsync comes after the silo's pool "
            + "cancel, so it is owned until the IoPool drain (#7992)");

        // The silo's stop: the registry releases what it owns before it cancels the pools.
        registry.Dispose();
        registry.OwnedUntilDrainCount.Should().Be(0, "the drain released the schedule before the cancel");
        registry.UnownedLeaks().SelectMany(r => r.Sites)
            .Should().NotContain(s => s.Contains(nameof(GitHubWebhookProcessor), StringComparison.Ordinal));

        // The host's hosted-service stop comes later: it must find a released schedule and do nothing harmful.
        service.StopAsync(ct).IsCompletedSuccessfully.Should().BeTrue(
            "StopAsync on an already-released schedule completes cleanly");
        service.Dispose();
    }
}
