#pragma warning disable CS1591

using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>#3510 — an install HOLDS its package root for as long as it runs, and releases it on every
/// way that run can end.</b>
///
/// <para><b>The defect.</b> The <c>Hosting</c> root hub was disposed while <c>Hosting</c>'s own
/// 145-file install was in flight. Its per-node children went with it, the writes those children
/// owed acks for were stranded (<c>ADVANCE_WITHOUT_HANDOFF … the owner never acknowledged this
/// write</c>), the <c>nodeops</c> handler that owed its reply to one of those acks never replied,
/// and the install ran out the gate's ten-minute bound. Six occurrences, four lost bake seals.
/// <i>The install is the writer that should own the root's lifetime.</i></para>
///
/// <para>The deferral itself — a recycle waiting on that lease, and an ordinary recycle proceeding
/// at once — is pinned in <c>MeshWeaver.Graph.Test</c>. What THIS file pins is the half that would
/// otherwise be a guard checking nothing: that the installer actually TAKES the lease, and that it
/// gives it back. A registry nobody holds is a deferral that never defers; a lease nobody releases
/// is strictly worse than the bug.</para>
///
/// <para>🚨 It lives here rather than beside the installer for the same reason
/// <c>RetypedRootRecycleNeedsAJobTest</c> does: this is the only test project in THIS repository
/// that both references <c>MeshWeaver.PluginCatalog</c> and appears in its
/// <c>InternalsVisibleTo</c> list.</para>
/// </summary>
public class AnInstallHoldsItsRootTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "LeaseHeldPkg";

    /// <summary>
    /// The catalog's own registration — without it the install's LAST step
    /// (<c>WriteInstalledRecord</c>) is refused with <c>NodeType 'Package' is not registered</c>,
    /// so the run would fault for a reason that has nothing to do with the lease.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    private static PackageManifest Manifest(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = PackageKind.Content,
        TargetPartition = id,
        SourceFolder = id,
        Version = "1.0.0",
    };

    /// <summary>
    /// 🚨 THE WIRING PIN, in both tenses. The hold is observed on the install's own emission — the
    /// lease is taken by <c>Observable.Using</c> at subscribe and released on the terminal, so an
    /// <c>OnNext</c> is the one moment at which "the install is running" is an observable fact
    /// rather than a timing guess. Remove <c>HoldRootDuringInstall</c> from
    /// <c>PackageInstaller.Install</c> and the middle assertion goes red.
    /// </summary>
    [Fact]
    public async Task TheRootIsHeldWhileTheInstallRuns_AndReleasedWhenItFinishes()
    {
        var leases = Mesh.ServiceProvider.GetRequiredService<PackageRootInstallLeases>();

        leases.IsHeld(Package).Should().BeFalse("nothing has started yet");

        var heldDuringInstall = false;
        var holderDuringInstall = (string?)null;
        var install = PackageInstaller
            .Install(
                Mesh,
                Manifest(Package),
                [new PackageFile("Readme.md", $"# {Package}")],
                "HEAD")
            .Do(_ =>
            {
                // Composed OUTSIDE the observable Install returns, so this runs while
                // Observable.Using's resource is still alive — the terminal that disposes it has
                // not been delivered yet.
                heldDuringInstall = leases.IsHeld(Package);
                holderDuringInstall = leases.HeldBy(Package);
            });

        var result = await install.Should().Within(TestTimeouts.CrossSilo)
            .Emit("the install must complete before anything about the lease can be read");
        result.Written.Should().BeGreaterThan(0, "the install wrote its one node");

        heldDuringInstall.Should().BeTrue(
            "the install holds its own package root for as long as it runs — that is the whole "
            + "point of #3510's remedy, and nothing else can hold it on its behalf");
        holderDuringInstall.Should().Be(
            PackageInstaller.InstallLeaseHolder(Manifest(Package)),
            "a deferred recycle prints the holder, so the holder must be NAMED — an anonymous "
            + "lease reads to the next person as no lease at all");

        leases.IsHeld(Package).Should().BeFalse(
            "the install has completed, so the root is released and any recycle waiting on it runs");
    }

    /// <summary>
    /// 🚨 THE CONTROL THAT MATTERS MOST. "Defer" is only allowed to mean <i>wait for a state that
    /// always arrives</i>. An install that FAILS is exactly when a recycle behind it must not be
    /// stranded — and a lease held by a dead install would be a permanently un-recyclable root,
    /// which is worse than the bug this fixes. The failure is genuine (a package with no
    /// installable content file), not simulated.
    /// </summary>
    [Fact]
    public async Task AnInstallThatFAILS_StillReleasesItsRoot()
    {
        var leases = Mesh.ServiceProvider.GetRequiredService<PackageRootInstallLeases>();
        var failing = $"{Package}Failing";

        var faults = PackageInstaller
            .Install(Mesh, Manifest(failing), [], "HEAD")
            .Materialize()
            .Where(n => n.Kind == System.Reactive.NotificationKind.OnError);

        await faults.Should().Within(TestTimeouts.CrossSilo)
            .Emit("a package with no installable content file is refused");

        leases.IsHeld(failing).Should().BeFalse(
            "an install that faulted has released its root — otherwise every recycle deferred "
            + "behind it waits for ever, and there is deliberately no timer to force it");
    }
}
