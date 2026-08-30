using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Security.Test;

/// <summary>
/// 🚨 <b>Issue #2679 — a permission fold must not resolve services from its hub's DI scope AFTER it
/// has been subscribed.</b>
///
/// <para><b>What production reported.</b> A layout area's render chain carried a live
/// <c>PermissionEvaluator.GetEffectivePermissions</c> fold — long-lived by design, it re-emits on
/// every <c>AccessAssignment</c> change. The host hub's Autofac scope was disposed while that fold
/// was still subscribed, and the fold's next emission ran its <c>SelectMany</c> selector, which
/// called <c>GetRequiredService&lt;IMeshNodeStreamCache&gt;</c> for the recursive Public leg (and
/// <c>GetRole</c>) — on the dead scope. Autofac's <see cref="ObjectDisposedException"/> went
/// straight into the render chain, was reported at Error as "Rendering failed for area Overview",
/// and the error placeholder then faulted a second time on the same scope.</para>
///
/// <para><b>The fix.</b> Every service the fold needs is resolved ONCE, at the entry point, on the
/// caller's thread, and carried through the recursion; a subscribed fold never touches
/// <c>hub.ServiceProvider</c> again. The residual — a fold that DOES fault with an
/// <see cref="ObjectDisposedException"/> while its hub's scope is gone — terminates as the typed
/// <see cref="HubDisposingException"/> teardown signal, gated on the same scope probe
/// <c>MessageHub.HandleInitialize</c> uses for #2444 (<see cref="ScopeTeardown"/>).</para>
///
/// <para><b>How the race is pinned, deterministically.</b> The hub's scope is disposed OUT-OF-BAND
/// (the #2444 mechanism: <c>((IDisposable)hub.ServiceProvider).Dispose()</c>, no hub
/// <c>Dispose()</c> call), the fold stays subscribed (its subscription belongs to this test, its
/// sources are the mesh-scoped cache's queries), and then a grant lands. The grant is the positive
/// signal: RED before the fix — the re-emission resolves from the dead scope and the fold
/// <c>OnError</c>s with the ObjectDisposedException; GREEN after — the fold answers the new grant
/// from the services it resolved when it was built.</para>
/// </summary>
public class PermissionFoldAfterScopeTeardownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Subject = "user-2679";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    protected override async Task SetupAccessRightsAsync()
    {
        // Grant the test runner Admin on TestPartition so the System-impersonated seed writes
        // land deterministically (same rationale as LiveQueryRowLevelSecurityTest: TestData is a
        // statically seeded partition, so routing the first write through a non-System identity
        // races PartitionWriteGuard's cold-partition provisioning).
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(
                    AssignmentNodeFactory.UserRole(
                        Mesh.Address.ToFullString(), "Admin", TestPartition))
                .Should().Emit();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AFoldWhoseHubScopeIsDisposed_StillAnswersTheNextGrant_FromTheServicesItResolvedOnce()
    {
        var scope = $"{TestPartition}/fold2679_{Guid.NewGuid().AsString()}";
        var target = $"{scope}/Target";

        // A hub with a lifetime scope of its OWN (a hosted hub gets BeginLifetimeScope — see
        // MessageHubConfiguration.CreateServiceProvider / OwnsServiceProvider), evaluating with the
        // real algorithm. This is the shape of every per-node hub a layout area renders on.
        var hub = Mesh.GetHostedHub(
            new Address("fold-teardown", Guid.NewGuid().AsString()),
            c => c.AddRowLevelSecurity());

        // The fold under test. Replay(1) so the assertions below observe ONE subscription — the
        // one that outlives the scope — rather than each building a fresh fold on a dead hub.
        var fold = hub.GetEffectivePermissions(target, Subject).Replay(1);
        using var live = fold.Connect();

        // Barrier 1 — the fold is live and has answered from the synced queries: no grant yet.
        await fold.Should().Within(20.Seconds()).Match(p => p == Permission.None);

        // 🚨 NEGATIVE CONTROL for the classifier, taken while the scope is ALIVE: an
        // ObjectDisposedException from an unrelated disposed dependency is a genuine defect and
        // must NOT read as teardown. This is what keeps the probe honest (#2444, #2638).
        hub.IsServiceScopeDisposed().Should().BeFalse("the hub's scope is alive at this point");
        hub.IsTerminatedByScopeTeardown(new ObjectDisposedException("SomeCache"))
            .Should().BeFalse("a disposed DEPENDENCY on a live scope is a defect, not a teardown");

        // Out-of-band scope teardown — exactly the production sequence (#2444): the scope's
        // disposed flag flips FIRST, every resolve from it throws from that instant, and the hub
        // instance is disposed later in the same sweep. No hub.Dispose() is called here.
        ((IDisposable)hub.ServiceProvider).Dispose();
        await hub.DisposalCompleted.FirstAsync().Timeout(TimeSpan.FromSeconds(15)).Await();

        hub.IsServiceScopeDisposed().Should().BeTrue(
            "the probe is the positive signal every teardown classification is gated on");
        hub.IsTerminatedByScopeTeardown(new ObjectDisposedException("LifetimeScope"))
            .Should().BeTrue("an ObjectDisposedException on a hub whose scope is gone IS the teardown");
        hub.IsTerminatedByScopeTeardown(new InvalidOperationException("a real defect"))
            .Should().BeFalse("the probe must not turn EVERY fault during teardown into a benign one");

        // A grant lands. The fold's sources are the mesh-scoped cache's synced queries, which are
        // alive and re-emit; the SelectMany selector then runs on the dead hub's fold. Before the
        // fix this is where GetRequiredService<IMeshNodeStreamCache> threw — the exact frame the
        // production stack shows (PermissionEvaluator.cs:126 from <GetEffectivePermissions>b__2).
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(AssignmentNodeFactory.UserRole(Subject, "Viewer", scope))
                .Should().Emit();
        }

        // Barrier 2 — the pin. RED before the fix: the Replay'd fold has latched the
        // ObjectDisposedException and this assertion throws it. GREEN after: the Viewer grant is
        // answered from the cache the fold resolved when it was built.
        await fold.Should().Within(20.Seconds()).Match(p => p.HasFlag(Permission.Read));
    }
}
