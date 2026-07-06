using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Deterministic repro for issue #325 — a MeshNode's <see cref="MeshNode.Version"/>
/// regresses (rolls BACKWARD) after the owning per-node hub is recycled / reactivated
/// (idle-release, <c>Recycle</c>/<see cref="DisposeRequest"/>, or a replica restart).
///
/// <para><b>Root cause (verified here).</b> Every owner-side write stamps the node's
/// Version from the owning hub's per-activation monotonic message counter
/// (<c>_workspace.Hub.Version</c> — <c>MeshNodeStreamExtensions.cs:592</c>). The SAME
/// clock stamps every sync-stream frame the owner originates — the init/base Full, value
/// updates, and layout renders — via <c>SynchronizationStream.OwnerVersion()</c>
/// (<c>Owner.Equals(Host.Address) ? Hub.Version : …</c>). <see cref="IMessageHub.Version"/>
/// is "incremented once per message processed" and starts at 0 on EVERY activation; it is
/// NEVER seeded from the persisted node's Version (the <c>SetInitialVersion(node.Version)</c>
/// seed was removed from <c>MeshNodeTypeSource</c> because it re-stamped the clock BACKWARD
/// on catalog pushes and dropped live layout Fulls). So after a deactivate → reactivate
/// cycle the fresh hub's LOW counter sits BELOW the version the node already carries, and
/// the node's next write stamps a Version below the old one → the counter rolls back
/// (the "v113 read back as v3" symptom).</para>
///
/// <para><b>Downstream (why the writes "vanish" / a write "hangs").</b> A mirror that
/// cached the higher pre-recycle version applies the sync monotonicity guard
/// (<c>SynchronizationStream</c> drops an incoming frame whose <c>Version &lt; Current.Version</c>).
/// It therefore DROPS the regressed post-recycle Full — staying stuck on the stale snapshot
/// and never confirming the write (the "index/resolution split-brain", "no initial state",
/// and "first UpdateNode hung indefinitely" symptoms). This test pins the trigger (the clock
/// reset below the live node version) directly, without depending on that downstream hang.</para>
///
/// <para><b>Status: pins the cause; does NOT fix it.</b> The fix is to seed the owner hub's
/// Version clock forward-only on activation (a guarded <c>SetInitialVersion</c> that never
/// regresses the clock and never reintroduces the catalog-push Full-drop) — a change to a
/// pervasive, load-bearing clock, out of scope for this repro. The final assertion therefore
/// documents the CURRENT (defective) behaviour so the test is green until the fix lands; when
/// the root cause is fixed, FLIP it to <c>clockAfter &gt;= nodeVersionBefore</c> and this test
/// becomes the fix's proof.</para>
/// </summary>
public class NodeVersionRegressionOnRecycleTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    [Fact(Timeout = 60000)]
    public async Task OwnerVersionClock_ResetsBelowLiveNodeVersion_OnRecycle_Pins325()
    {
        var path = $"{TestPartition}/version-regress-target";

        // Arrange — create the node. Markdown activates without a Roslyn compile.
        await NodeFactory.CreateNode(
                new MeshNode("version-regress-target", TestPartition)
                { Name = "v0", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit();

        // Resolve the owning per-node hub address (the hub that stamps this node's writes).
        var resolution = await PathResolver.ResolvePath(path).Should().Within(30.Seconds()).Emit();
        resolution.Should().NotBeNull($"path '{path}' should resolve to an owning hub");
        var address = new Address(resolution!.Prefix.ToString()!);

        // Drive the owning hub's Version clock UP with a burst of distinct writes. Each
        // write is a message on the owner → Hub.Version climbs and is stamped onto
        // node.Version. Distinct names guarantee every write applies (the no-op guard
        // drops value-equal updates).
        for (var i = 1; i <= 30; i++)
        {
            var name = $"v{i}";
            await NodeFactory.UpdateNode(
                    new MeshNode("version-regress-target", TestPartition)
                    { Name = name, NodeType = "Markdown" })
                .Should().Within(30.Seconds()).Match(n => n.Name == name);
        }

        // The authoritative version the node now carries, and the owner clock behind it.
        var before = await ReadNode(path).Should().Within(30.Seconds())
            .Match(n => n is { Name: "v30" });
        var nodeVersionBefore = before!.Version;
        var ownerBefore = Mesh.GetHostedHub(address, HostedHubCreation.Never);
        ownerBefore.Should().NotBeNull("the owner hub must be live after the write burst");
        var clockBefore = ownerBefore!.Version;
        Output.WriteLine(
            $"[#325] nodeVersionBefore={nodeVersionBefore}, ownerClockBefore={clockBefore}");
        nodeVersionBefore.Should().BeGreaterThan(0,
            "the write burst must have advanced the node Version off the owner clock");

        // Act — recycle the owner hub (DisposeRequest). Its next activation resets
        // Hub.Version to 0.
        Mesh.Post(new DisposeRequest(), o => o.WithTarget(address));

        // Reactively wait for the reset: keep pinging to force reactivation, then read the
        // (re)activated owner's Version. The pre-dispose hub still reports its high clock
        // (filtered out); the reset is proven the moment the reactivated owner reports a
        // clock strictly BELOW clockBefore. No fixed delay — we wait on the actual condition.
        var clockAfter = await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => Mesh.Observe(new PingRequest(), o => o.WithTarget(address))
                .Select(_ => Mesh.GetHostedHub(address, HostedHubCreation.Never)?.Version ?? -1L)
                .Catch((Exception _) => Observable.Return(-1L)))
            .Should().Within(30.Seconds()).Match(v => v >= 0 && v < clockBefore);
        Output.WriteLine($"[#325] ownerClockAfter(reactivated)={clockAfter}");

        // Assert — PIN #325. The reactivated owner's Version clock (the SAME clock that
        // stamps the node's next write, and every sync Full) sits BELOW the version the
        // node already carries. So the node's very next write rolls the version backward,
        // and any mirror that cached the higher version drops the regressed frame. The
        // CORRECT post-fix invariant is clockAfter >= nodeVersionBefore (seed the clock
        // forward-only on activation).
        clockAfter.Should().BeLessThan(nodeVersionBefore,
            $"#325 repro: the reactivated owner clock ({clockAfter}) is below the live node "
            + $"Version ({nodeVersionBefore}) — the next write rolls the Version backward. "
            + "This documents the defect; the correct post-fix invariant is "
            + "clockAfter >= nodeVersionBefore.");
    }
}
