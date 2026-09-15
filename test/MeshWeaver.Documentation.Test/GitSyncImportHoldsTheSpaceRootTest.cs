using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>#3510 — the recycle gate #4009/#4202 built is only as wide as the writers that TAKE the
/// lease, and until now there was exactly ONE in the fleet.</b>
///
/// <para><c>NodeTypeRebindWatcher.WaitWhileAnInstallHoldsIt</c> and <c>MeshOperations.Recycle</c>'s
/// <c>WhenNoInstallHoldsRoot</c> both consult <see cref="PackageRootInstallLeases"/> before posting
/// a <c>DisposeRequest</c> — but a guard whose reach is assumed reads as a guarantee it does not
/// keep. <c>PackageInstaller.HoldRootDuringInstall</c> was the only production site; the GitSync
/// import lands a whole partition tree, including the <c>NodeType</c> retypes <c>RequiresRebind</c>
/// fires on, and held nothing. Both gates therefore found no holder and proceeded — #3510's shape
/// with a different writer.</para>
///
/// <para>This pins the seam the three import entry points now route through
/// (<c>UpdateToLatestFromGitHub</c>, <c>UpdateToProvenCommitFromGitHub</c>,
/// <c>ReconcileAtProvenCommitFromGitHub</c>) — <b>not</b> a GitHub round-trip: the property that
/// matters is when the root is held and when it is released, and that is a property of the wrapper,
/// drivable against a real hub with no network at all.</para>
///
/// <para>🚨 <b>Both directions.</b> A wrapper that held everything for ever would satisfy a
/// one-sided "is it held" test while deadlocking every recycle in the mesh, so the releases are
/// asserted on ALL THREE of Rx's endings, and a sibling Space is asserted NOT held — the exact
/// mis-keying #4009's own review caught in the installer (<i>"a lease on <c>&lt;id&gt;</c> for a
/// package writing <c>type/&lt;id&gt;</c> reads exactly like a lease that is working"</i>).</para>
///
/// <para>🚨 <b>No hand-woven gate and no sleep:</b> the import stand-in is a
/// <see cref="Subject{T}"/> the test drives, so every transition is caused rather than waited for.</para>
/// </summary>
public class GitSyncImportHoldsTheSpaceRootTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Space = "Doc";
    private const string Sibling = "DocSibling";

    private const string Holder = "GitSync: an import is writing 'Doc' from the branch HEAD";

    /// <summary>A hub that carries the lease registry, exactly as a mesh built by
    /// <c>MeshBuilder</c> does (<c>AddSingleton&lt;PackageRootInstallLeases&gt;</c>).</summary>
    private IMessageHub HubWithLeases() =>
        GetClient(c => c.WithServices(s => s.AddSingleton<PackageRootInstallLeases>()));

    [Fact]
    public async Task AnImportHoldsItsSpaceRoot_AndReleasesOnCompletion()
    {
        var hub = HubWithLeases();
        var leases = hub.ServiceProvider.GetRequiredService<PackageRootInstallLeases>();
        var import = new Subject<string>();

        leases.IsHeld(Space).Should().BeFalse("nothing holds the root before the import is subscribed");

        using (GitHubActivityExtensions.HoldSpaceDuringImport(hub, Space, Holder, import)
                   .Subscribe(_ => { }, _ => { }))
        {
            leases.IsHeld(Space).Should().BeTrue(
                "the import is running, so a recycle aimed at this root must WAIT — that is the "
                + "whole rule #4009 established and the reason #3510 stays open for writers that "
                + "do not take the lease");
            leases.HeldBy(Space).Should().Be(Holder,
                "the deferral log prints the holder verbatim, and an unnamed holder reads to the "
                + "next person as no holder at all");

            // 🚨 The control on the KEY, not on the mechanism: a lease that named the wrong root
            // would look identical from the holder's side and protect nothing.
            leases.IsHeld(Sibling).Should().BeFalse(
                "the lease is keyed to the Space the import WRITES; a sibling partition's root must "
                + "still recycle freely");

            import.OnNext("Doc/_Activity/abc");
            import.OnCompleted();
        }

        leases.IsHeld(Space).Should().BeFalse(
            "an import that completed has released its root — a hold that outlived its import would "
            + "deadlock every later recycle of this Space");
        await Task.CompletedTask;
    }

    [Fact]
    public void AnImportThatFAILS_StillReleasesItsRoot()
    {
        var hub = HubWithLeases();
        var leases = hub.ServiceProvider.GetRequiredService<PackageRootInstallLeases>();
        var import = new Subject<string>();

        using var subscription = GitHubActivityExtensions
            .HoldSpaceDuringImport(hub, Space, Holder, import)
            .Subscribe(_ => { }, _ => { });
        leases.IsHeld(Space).Should().BeTrue("the import is running");

        import.OnError(new InvalidOperationException("the branch could not be fetched"));

        leases.IsHeld(Space).Should().BeFalse(
            "a FAILED import is exactly when a recycle behind it must not be stranded — 'defer' is "
            + "only allowed to mean waiting for a state that always arrives");
    }

    [Fact]
    public void AnImportWhoseCallerGivesUp_StillReleasesItsRoot()
    {
        var hub = HubWithLeases();
        var leases = hub.ServiceProvider.GetRequiredService<PackageRootInstallLeases>();
        var import = new Subject<string>();

        var subscription = GitHubActivityExtensions
            .HoldSpaceDuringImport(hub, Space, Holder, import)
            .Subscribe(_ => { }, _ => { });
        leases.IsHeld(Space).Should().BeTrue("the import is running");

        subscription.Dispose();

        leases.IsHeld(Space).Should().BeFalse(
            "unsubscribe is the third of Rx's three endings, and Observable.Using releases on all "
            + "three — a GUI caller navigating away must not pin the root");
    }

    /// <summary>
    /// The other control: a host that never registered the lease registry cannot have the gate
    /// either, so the wrapper must be a pass-through rather than a throw or a silent no-op that
    /// changes the import's observable.
    /// </summary>
    [Fact]
    public async Task WithNoLeaseRegistry_TheImportIsPassedThroughUNCHANGED()
    {
        var hub = GetClient();
        hub.ServiceProvider.GetService<PackageRootInstallLeases>().Should().BeNull(
            "this control is only meaningful on a host that genuinely has no registry");

        var import = Observable.Return("Doc/_Activity/abc");
        var wrapped = GitHubActivityExtensions.HoldSpaceDuringImport(hub, Space, Holder, import);

        ReferenceEquals(wrapped, import).Should().BeTrue(
            "with no registry there is nothing to hold and nothing to gate, so the import is "
            + "returned as it was rather than wrapped in a resource that protects nothing");
        (await wrapped.Should().Emit("and it still runs")).Should().Be("Doc/_Activity/abc");
    }
}
