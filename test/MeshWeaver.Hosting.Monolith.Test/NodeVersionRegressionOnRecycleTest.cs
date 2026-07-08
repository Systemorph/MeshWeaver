using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Deterministic proof for the fix of issue #325 — a MeshNode's <see cref="MeshNode.Version"/>
/// must NOT regress (roll BACKWARD) when the owning per-node hub's <see cref="IMessageHub.Version"/>
/// clock is low relative to the version the node already carries. That "low hub clock + high node
/// version" state is exactly what a recycle / idle-release / replica restart produces: the hub
/// clock is "incremented once per message processed" and starts at <b>0 on every activation</b>,
/// while the node reloads its persisted (high) Version verbatim.
///
/// <para><b>The #325 trigger, reproduced deterministically.</b> Rather than race a recycle against
/// the debounced persistence sampler (a bursty write followed by an immediate deactivate can drop
/// unflushed writes AND races the data-source reload — a SEPARATE effect), this test DURABLY seeds
/// the node at a high Version (1000) via <c>CreateNode</c> (which preserves a caller-supplied
/// Version &gt; 0). The owner then activates with a fresh, LOW hub clock — the identical
/// post-recycle state — with no reload race to confound the measurement.</para>
///
/// <para><b>The fix (what this proves).</b> The node's persistence version is decoupled from the
/// per-activation hub clock: every place the owner mints a node Version advances it forward-only
/// from the node's OWN version via <see cref="MeshNode.NextVersion(long, long)"/>
/// (<c>max(Hub.Version, current.Version + 1)</c>) — the own-write path (UpdateOwn), the persistence
/// re-stamp (MeshNodeTypeSource.UpdateImpl), and the cross-hub merge apply
/// (DataExtensions.NextMeshNodeVersion). So a write made while the hub clock sits far below the node
/// version still lands a Version strictly ABOVE it — the authoritative <c>get</c> never regresses.
/// Crucially the fix does NOT touch <c>Hub.Version</c> (re-seeding that shared clock backward is
/// what dropped live layout Fulls, the atioz 2026-06-18 "cannot find pinned doc" wedge), and it
/// does NOT retie the sync-stream FRAME version to the content Version (that version is absent on a
/// partial cross-hub patch frame, so the frame would flap and drop legitimate mid-stream updates —
/// it broke the activity/export relay). The residual cross-SILO mirror-drop after a recycle (the
/// "index vs node-resolution split-brain", observable only on a multi-replica portal) is a separate
/// mirror-side concern tracked on #325.</para>
///
/// <para>Before the fix the low hub clock stamped a Version BELOW the live node version (the repro
/// measured 64 → 2, "v113 read back as v3"). This test was GREEN while it asserted that defect; it
/// now asserts <c>afterVersion &gt; nodeVersionBefore</c> — the fix's proof.</para>
/// </summary>
public class NodeVersionRegressionOnRecycleTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    [Fact(Timeout = 60000)]
    public async Task NodeVersion_DoesNotRegress_WhenHubClockIsBelowNodeVersion_Proves325Fix()
    {
        var path = $"{TestPartition}/version-regress-target";
        const long seededVersion = 1000L;

        // Arrange — DURABLY persist the node at a HIGH version (1000). CreateNode preserves a
        // caller-supplied Version > 0 (HandleCreateNodeRequest, pinned by MeshNodeVersionSyncTest),
        // so 1000 is persisted verbatim and the owner loads it verbatim on activation. This is the
        // faithful post-recycle state: high persisted node version, fresh low hub clock.
        await NodeFactory.CreateNode(
                new MeshNode("version-regress-target", TestPartition)
                { Name = "seed", NodeType = "Markdown", Version = seededVersion })
            .Should().Within(30.Seconds()).Emit();

        // Confirm the owner loaded the seeded high version (authoritative owner round-trip).
        var before = await ReadNode(path).Should().Within(30.Seconds())
            .Match(n => n is { Version: seededVersion });
        var nodeVersionBefore = before!.Version;

        // The #325 TRIGGER is present: the owning hub's per-activation clock sits FAR BELOW the
        // node version. The old code stamped the node's next write straight off this low clock →
        // the version rolled backward. (Resolve the owner and read its live Hub.Version to prove
        // the gap; the fix must survive it WITHOUT re-seeding this shared clock.)
        var resolution = await PathResolver.ResolvePath(path).Should().Within(30.Seconds()).Emit();
        resolution.Should().NotBeNull($"path '{path}' should resolve to an owning hub");
        var address = new Address(resolution!.Prefix.ToString()!);
        var owner = Mesh.GetHostedHub(address, HostedHubCreation.Never);
        owner.Should().NotBeNull("the owner hub must be live after the seeded read");
        var hubClock = owner!.Version;
        Output.WriteLine(
            $"[#325] nodeVersionBefore(seeded)={nodeVersionBefore}, ownerHubClock={hubClock}");
        hubClock.Should().BeLessThan(nodeVersionBefore,
            $"the owner hub clock ({hubClock}) sits BELOW the live node Version ({nodeVersionBefore}) "
            + "— the exact #325 trigger the fix must survive WITHOUT re-seeding Hub.Version.");

        // Act — write through the canonical mesh-node stream (the same surface the GUI/agents use).
        var client = GetClient(c => c.AddData());
        var stream = client.GetWorkspace().GetMeshNodeStream(path);
        stream.Update(n => n with { Name = "after" })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[#325] write error: {ex.Message}"));

        // Assert — THE FIX. Read the authoritative post-write node from the owner. Its Version must
        // have advanced FORWARD, above the pre-write version — never rolled back to the low hub
        // clock (MeshNode.NextVersion floors it at current.Version + 1). Before the fix it stamped
        // ~hubClock, so the authoritative get read a version BELOW the confirmed writes ("v113 read
        // back as v3").
        var after = await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => ReadNode(path).Catch((Exception _) => Observable.Return<MeshNode?>(null)))
            .Should().Within(30.Seconds()).Match(n => n is { Name: "after" });
        Output.WriteLine(
            $"[#325] nodeVersionBefore={nodeVersionBefore}, nodeVersionAfterWrite={after!.Version}");

        after.Version.Should().BeGreaterThan(nodeVersionBefore,
            $"#325 FIX: the write's node Version ({after.Version}) must stay ABOVE the pre-write "
            + $"Version ({nodeVersionBefore}) even though the owner hub clock is only {hubClock} — "
            + "the node's persistence version is monotonic (MeshNode.NextVersion), decoupled from "
            + "the per-activation Hub.Version clock that resets on every reactivation.");
    }
}
