using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Issue #7992 — every silo stop on the production portals logged
/// <c>IoPoolSiloTeardown: pool 'Query' was cancelled with 7–11 subscription(s) still attached</c>, naming
/// the same mesh-lifetime readers each time: <c>AccountLockCatalog</c>, <c>InstanceSecretCatalog</c>,
/// <c>UiContributionCatalog</c>, <c>InstanceAdminRecordCatalog</c>, <c>EventSubscriptionRunner</c>,
/// <c>UserIdentityCache</c> (and two in MeshWeaver.Plugins).
///
/// <para><b>The defect.</b> On a silo, <c>IoPoolSiloTeardown</c> disposes the <see cref="IoPoolRegistry"/>
/// at the silo's stop, while the mesh hub and every mesh-scoped singleton live on until the host's
/// <c>StoppedAsync</c> and its container disposal. Those are the readers' only owners, so each reader
/// was still attached at the cancel — torn down with no terminal and reported as unowned. This test
/// reproduces exactly that order on a real mesh: open the readers, dispose the registry while the
/// mesh is alive, read the registry's own leak report.</para>
///
/// <para><b>Not vacuous:</b> an UNOWNED control read, opened the same way through the same mesh query,
/// must be named by the same report — so the instrument demonstrably sees pooled query legs here.
/// <b>Negative control, measured:</b> with the consumers' <c>OwnUntilIoDrain</c> calls reverted, the
/// report names <c>AccountLockCatalog</c>, <c>InstanceSecretCatalog</c> and <c>UiContributionCatalog</c>
/// and this test fails.</para>
/// </summary>
public class MeshLifetimeReadsReleasedBeforeThePoolCancelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static TimeSpan Budget => TestTimeouts.Convergence;

    // A named selector so the leak line can be asserted to name the control read.
    private static QueryResultChange<MeshNode> UnownedControlRead(QueryResultChange<MeshNode> change) => change;

    [Fact]
    public async Task ASiloStop_FindsNoMeshLifetimeReaderStillAttached_ButStillNamesAnUnownedRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using var locks = new AccountLockCatalog(Mesh);
        using var secrets = new InstanceSecretCatalog(Mesh);
        using var contributions = new UiContributionCatalog(Mesh);

        // Open each standing read and wait for its first answer — the read is then attached.
        await locks.WhenDetermined("someone").Should().Within(Budget)
            .Emit("the account-lock register answered", cancellationToken: ct);
        secrets.Start();
        await secrets.WhenLoaded.Should().Within(Budget)
            .Emit("the instance-secret catalog listed", cancellationToken: ct);
        await contributions.Contributions.Take(1).Should().Within(Budget)
            .Emit("the UI contribution catalog listed", cancellationToken: ct);

        // The control: the same kind of standing mesh query, owned by nobody.
        IDisposable control;
        using (access.ImpersonateAsSystem())
            control = meshService.Query<MeshNode>(MeshQueryRequest.FromQuery("namespace:Admin scope:children"))
                .Select(UnownedControlRead)
                .Subscribe(_ => { }, _ => { });
        try
        {
            await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery("namespace:Admin scope:children"))
                .Take(1).Should().Within(Budget).Emit("precondition: the query answers in this mesh", cancellationToken: ct);

            // The silo's stop: IoPoolSiloTeardown disposes the registry while the mesh lives on.
            registry.Dispose();
            await registry.Disposed.Should().Within(Budget)
                .Emit("every pool reported once its cancel returned", cancellationToken: ct);

            var leaks = registry.UnownedLeaks().SelectMany(r => r.Sites).ToArray();
            Output.WriteLine($"unowned at the cancel ({leaks.Length}):\n  " + string.Join("\n  ", leaks));

            leaks.Should().Contain(s => s.Contains(nameof(UnownedControlRead), StringComparison.Ordinal),
                "precondition: the report sees a pooled query leg nobody owns — otherwise its silence about "
                + "the catalogs below would prove nothing");
            foreach (var reader in new[]
                     { nameof(AccountLockCatalog), nameof(InstanceSecretCatalog), nameof(UiContributionCatalog) })
                leaks.Should().NotContain(s => s.Contains(reader, StringComparison.Ordinal),
                    "{0} lives as long as the mesh and is owned until the IoPool drain, so the registry released "
                    + "it before cancelling the pools (#7992)", reader);
        }
        finally
        {
            control.Dispose();
        }
    }
}
