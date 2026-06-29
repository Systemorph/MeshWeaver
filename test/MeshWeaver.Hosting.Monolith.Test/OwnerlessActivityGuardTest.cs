using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Repro + contract for the atioz <c>_Activity/*</c> resubscribe / re-route storm caused by an
/// <b>Activity MeshNode anchored at a top-level / ownerless path</b>.
///
/// <para><b>The defect.</b> An activity lives at <c>{ownerPath}/_Activity/{id}</c> and is routed
/// through its owning node's partition. The markdown kernel views built a bare
/// <c>_Activity/markdown-{id}</c> (empty owner, <c>MainNode = ""</c>) whenever the view had no
/// owning node, and other paths could likewise produce a top-level activity. A bare
/// <c>_Activity/{id}</c> has no per-node hub to route to, so every poster (<c>SubmitCodeRequest</c>)
/// and subscriber (progress panels) gets a <c>[ROUTE] NotFound</c> from the RoutingGrain — and a
/// re-subscriber hammers it hundreds of times, pegging CPU and wedging the hub (the observed
/// ~500×/~125× storms that survived pod restarts).</para>
///
/// <para><b>The fix.</b> The node-create boundary (<c>HandleCreateNodeRequest</c>, shared by
/// <c>CreateNode</c> AND <c>CreateOrUpdateNode</c>) rejects an ownerless activity up front via
/// <see cref="ActivityNodeGuard"/> — loudly, at the source, for EVERY identity including System —
/// instead of letting a phantom escape and storm the router downstream.</para>
/// </summary>
public class OwnerlessActivityGuardTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// THE REPRO. Creating an Activity at a bare, top-level <c>_Activity/{id}</c> (empty owner,
    /// <c>MainNode = ""</c>) — the exact shape the markdown views produced with no owning node — MUST
    /// be rejected at the create boundary. Pre-fix the create silently SUCCEEDS (the node persists,
    /// then every reader/poster NotFound-storms the router); post-fix it fails fast with a clear error
    /// that names the path and the missing owner.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CreatingTopLevelOwnerlessActivity_IsRejectedAtTheBoundary()
    {
        var kernelId = Guid.NewGuid().AsString();
        var ownerlessActivity = new MeshNode($"markdown-{kernelId}", "_Activity")
        {
            Name = "ownerless markdown activity",
            NodeType = "Activity",
            MainNode = string.Empty,
            State = MeshNodeState.Active,
            Content = new ActivityLog("MarkdownExecution")
            {
                Id = $"markdown-{kernelId}",
                HubPath = string.Empty,
                Status = ActivityStatus.Running
            }
        };

        var notification = await MeshService.CreateNode(ownerlessActivity)
            .Materialize()
            .Should().Within(TimeSpan.FromSeconds(20))
            .Match(n => n.Kind == NotificationKind.OnError,
                "creating a top-level/ownerless Activity must be rejected at the create boundary — "
                + "never silently persisted (a bare _Activity/{id} has no hub to route to, so it "
                + "NotFound-storms the router)");

        notification.Exception.Should().NotBeNull();
        notification.Exception!.Message.Should().Contain("_Activity",
            "the rejection must name the offending activity path so the bug is easy to hunt");
    }

    /// <summary>
    /// NO OVER-REJECTION. A properly-OWNED activity at <c>{ownerPath}/_Activity/{id}</c> with a real
    /// <c>MainNode</c> creates normally — the guard only ever rejects the top-level/ownerless shape.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CreatingOwnedActivity_UnderARealOwner_Succeeds()
    {
        var activityId = $"markdown-{Guid.NewGuid().AsString()}";
        var ownedActivity = new MeshNode(activityId, $"{TestPartition}/_Activity")
        {
            Name = "owned markdown activity",
            NodeType = "Activity",
            MainNode = TestPartition,
            State = MeshNodeState.Active,
            Content = new ActivityLog("MarkdownExecution")
            {
                Id = activityId,
                HubPath = TestPartition,
                Status = ActivityStatus.Running
            }
        };

        await MeshService.CreateNode(ownedActivity)
            .Should().Within(TimeSpan.FromSeconds(20))
            .Emit("an activity under a real owning node ({owner}/_Activity/{id}, MainNode set) must "
                + "create normally — the guard rejects only the top-level/ownerless shape");
    }

    /// <summary>
    /// GENERALIZATION (Phase 3). The same ownerless defect applies to EVERY satellite — here a bare,
    /// top-level <c>_Thread/{id}</c> (the shape the voice bridge could anchor when handed an empty
    /// namespace). It MUST be rejected at the create boundary exactly like the bare <c>_Activity</c>:
    /// a bare <c>_Thread</c> has no partition / per-node hub to route to, so the chat view + submission
    /// watcher NotFound-storm the router. The guard fires structurally (before the NodeType-existence
    /// check), so this is rejected even though <c>Thread</c> isn't registered in this AddGraph-only mesh.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CreatingTopLevelOwnerlessThread_IsRejectedAtTheBoundary()
    {
        var ownerlessThread = new MeshNode($"voice-{Guid.NewGuid().AsString()}", "_Thread")
        {
            Name = "ownerless voice thread",
            NodeType = "Thread",
            State = MeshNodeState.Active,
        };

        var notification = await MeshService.CreateNode(ownerlessThread)
            .Materialize()
            .Should().Within(TimeSpan.FromSeconds(20))
            .Match(n => n.Kind == NotificationKind.OnError,
                "creating a top-level/ownerless _Thread must be rejected at the create boundary — "
                + "the same ownerless invariant that protects _Activity now covers all satellites");

        notification.Exception.Should().NotBeNull();
        notification.Exception!.Message.Should().Contain("_Thread",
            "the rejection must name the offending satellite segment so the bug is easy to hunt");
    }

    /// <summary>
    /// NO OVER-REJECTION for a NON-Activity satellite. A properly-OWNED <c>_Notification</c> at
    /// <c>{ownerPath}/_Notification/{id}</c> with a real <c>MainNode</c> creates normally — the
    /// generalized guard rejects only the top-level/ownerless shape, never a legitimately-owned
    /// satellite, and (unlike <c>_Activity</c>) does not require a non-empty MainNode for non-Activity
    /// satellites.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CreatingOwnedNotification_UnderARealOwner_Succeeds()
    {
        var notifId = $"notif-{Guid.NewGuid().AsString()}";
        var ownedNotification = new MeshNode(notifId, $"{TestPartition}/_Notification")
        {
            Name = "owned notification",
            NodeType = "Notification",
            MainNode = TestPartition,
            State = MeshNodeState.Active,
            Content = new MeshWeaver.Mesh.Notification { Title = "test", Message = "hello" }
        };

        await MeshService.CreateNode(ownedNotification)
            .Should().Within(TimeSpan.FromSeconds(20))
            .Emit("an owned satellite ({owner}/_Notification/{id}, MainNode set) must create normally — "
                + "the generalized guard rejects only the top-level/ownerless shape");
    }

    /// <summary>
    /// The full predicate matrix for the pure, synchronous guard (<see cref="ActivityNodeGuard.IsOwnerless"/>)
    /// — the same predicate the create boundary uses. Proves: (a) EVERY owner-requiring satellite is
    /// rejected top-level; (b) owned satellites pass (including an owned <c>_Thread</c> — the "success"
    /// direction, deterministic); (c) the empty-MainNode check is Activity-ONLY (an owned <c>_Thread</c>
    /// with empty MainNode passes); (d) <c>_Access</c> is exempt at every level (root + partition-root
    /// grants with <c>MainNode=""</c>); (e) type-definition + non-satellite nodes are never flagged.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void OwnerlessSatelliteGuard_PredicateMatrix_RejectsOnlyTheDefectShape()
    {
        // (a) Owner-requiring satellites at a bare top-level path → ownerless.
        AssertOwnerless(new MeshNode("t1", "_Thread") { NodeType = "Thread" }, "_Thread");
        AssertOwnerless(new MeshNode("c1", "_Comment") { NodeType = "Comment" }, "_Comment");
        AssertOwnerless(new MeshNode("n1", "_Notification") { NodeType = "Notification" }, "_Notification");
        AssertOwnerless(new MeshNode("u1", "_UserActivity") { NodeType = "UserActivity" }, "_UserActivity");
        AssertOwnerless(new MeshNode("a1", "_Activity") { NodeType = "Activity", MainNode = "" }, "_Activity");

        // (c) Activity-ONLY empty-MainNode check: an OWNED activity with empty MainNode is ownerless …
        AssertOwnerless(new MeshNode("a2", $"{TestPartition}/_Activity") { NodeType = "Activity", MainNode = "" }, "MainNode");

        // (b) … but owned satellites pass, including an owned _Thread.
        AssertOwned(new MeshNode("t2", $"{TestPartition}/_Thread") { NodeType = "Thread", MainNode = TestPartition });
        AssertOwned(new MeshNode("c2", $"{TestPartition}/Doc/_Comment") { NodeType = "Comment", MainNode = $"{TestPartition}/Doc" });
        // (c) … and the empty-MainNode rejection does NOT apply to a non-Activity satellite.
        AssertOwned(new MeshNode("t3", $"{TestPartition}/_Thread") { NodeType = "Thread", MainNode = "" });

        // (d) _Access is exempt at every level — root global grant + partition-root grant (MainNode="").
        AssertOwned(new MeshNode("Public_Access", "_Access") { NodeType = "AccessAssignment", MainNode = "" });
        AssertOwned(new MeshNode("rbuergi_Access", "Admin/_Access") { NodeType = "AccessAssignment", MainNode = "" });

        // (e) Type-definition (namespace null) + non-satellite nodes are never flagged.
        AssertOwned(new MeshNode("Thread") { NodeType = "NodeType" });
        AssertOwned(new MeshNode("Activity") { NodeType = "NodeType" });
        AssertOwned(new MeshNode("AiSettings", $"{TestPartition}/_Memex") { NodeType = "AiSettings" });
    }

    private static void AssertOwnerless(MeshNode node, string expectedSegmentInReason)
    {
        ActivityNodeGuard.IsOwnerless(node, out var reason).Should().BeTrue(
            "'{0}' is a top-level/ownerless satellite and must be rejected", node.Path);
        reason.Should().Contain(expectedSegmentInReason,
            "the reason must name the offending segment / field for '{0}'", node.Path);
    }

    private static void AssertOwned(MeshNode node)
        => ActivityNodeGuard.IsOwnerless(node, out _).Should().BeFalse(
            "'{0}' is a legitimately-owned / exempt / non-satellite node and must NOT be rejected", node.Path);
}
