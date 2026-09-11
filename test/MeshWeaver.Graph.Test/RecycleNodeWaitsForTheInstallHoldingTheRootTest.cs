using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#3510 — the ONE public recycle surface waits while an install holds the root, and is a
/// straight pass-through when none does.</b>
///
/// <para><c>HubRecycleExtensions.RecycleNode</c> is what every caller outside the framework's own
/// automatic recyclers uses — a reconcile, an operations action, a confirmation flow. Before this
/// change any of them could tear a package root down in the middle of that package's install,
/// stranding the writes its per-node children owed acks for and leaving the install to run out its
/// ten-minute bound (CD 7950 and five occurrences after it).</para>
///
/// <para>Both cases run against a REAL Monolith mesh — the registry is resolved from the mesh's own
/// <c>ServiceProvider</c>, so this also pins the <c>MeshBuilder</c> registration: a lease taken by
/// one party has to be visible to a recycler on a different hub, which is exactly what a
/// hub-level (rather than mesh-root) registration would silently break.</para>
///
/// <para>🚨 The recycle is issued on <c>RequestHub</c>, never on <c>Mesh</c>: the root
/// <c>mesh/{id}</c> hub is transient routing infrastructure and a read routed through it always
/// faults — the rule the base class states on its own <c>ReadHub</c>.</para>
/// </summary>
public class RecycleNodeWaitsForTheInstallHoldingTheRootTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Holder =
        "PackageInstaller: the install of package 'Hosting' is writing under this root";

    private async Task<string> SeedNode(string id)
    {
        var path = $"{TestPartition}/{id}";
        await NodeFactory
            .CreateNode(new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .Should().Emit("the node the recycle targets has to exist first");
        return path;
    }

    /// <summary>
    /// 🚨 THE REGRESSION CASE. While an install holds the root, the recycle does not post its
    /// <c>DisposeRequest</c> at all — so the hub stays up and the writes beneath it keep their
    /// owner. The second half is what makes this a DEFERRAL rather than a silent drop: the moment
    /// the install releases the root, the recycle runs and the caller is answered by the fresh
    /// activation.
    /// </summary>
    [Fact]
    public async Task WhileAnInstallHoldsTheRoot_TheRecycleWaitsAndThenRuns()
    {
        var leases = Mesh.ServiceProvider.GetRequiredService<PackageRootInstallLeases>();
        var path = await SeedNode("recycle-held-root");

        var lease = leases.Hold(path, Holder);
        leases.HeldBy(path).Should().Be(Holder, "the holder is named, never anonymous");

        var recycled = RequestHub.RecycleNode(path).Replay(1);
        using var connection = recycled.Connect();

        await recycled.Should().NotEmit(
            TestTimeouts.Quick,
            "an install is writing under this root — recycling it now would strand that install's "
            + "writes, which is #3510's whole measured harm");

        lease.Dispose();

        await recycled.Should().Within(TestTimeouts.Convergence).Emit(
            "the recycle is DEFERRED, never dropped: the install released the root, so the address "
            + "must now recycle and answer again");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. #3965 measured that a root recycling with no install of its own is
    /// the COMMON case, so a fix that blocked every recycle would pass the regression case above
    /// and break the mesh. With nothing held this is the pre-#3510 composition exactly — post, then
    /// read the address back.
    /// </summary>
    [Fact]
    public async Task WithNoInstallHoldingTheRoot_TheRecycleProceedsAtOnce()
    {
        var leases = Mesh.ServiceProvider.GetRequiredService<PackageRootInstallLeases>();
        var path = await SeedNode("recycle-free-root");

        leases.IsHeld(path).Should().BeFalse("no install is running");

        await RequestHub.RecycleNode(path).Should().Within(TestTimeouts.Convergence).Emit(
            "an ordinary recycle under a reconcile must still proceed promptly — the deferral is "
            + "scoped to a root an install actually holds");
    }
}
