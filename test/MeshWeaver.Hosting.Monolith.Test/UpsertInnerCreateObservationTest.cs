using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the correlation contract behind #981 — "callbacks still pending past the teardown quiescing
/// budget", always reported as a <c>CreateNodeRequest@mesh/&lt;self&gt;</c> that the mesh hub never
/// answered (FutuRe EuropeRe, Cession, PandasExplorer).
///
/// <para><b>The mechanism.</b> <c>MessageHub.HandleCallbacks</c> DROPS a response whose correlation id
/// has no registered subject ("No subject found for response … treating as processed"). So the order of
/// the two steps decides whether a reply can be lost:</para>
/// <list type="bullet">
///   <item><b>Post, then <c>Observe(delivery)</c></b> — the subject is registered AFTER the post.
///     <c>PostImplGeneric</c> runs <c>ScheduleNotify</c> synchronously, so the delivery is already
///     enqueued and its turn scheduled onto <c>turnScheduler</c> (another thread) by the time
///     <c>Post</c> returns. Preempt the posting thread — routine on a saturated CI runner — and the
///     turn answers first, the reply is dropped, and the caller's callback is pending FOREVER.</item>
///   <item><b><c>Observe(request, options)</c></b> — registers the <c>AsyncSubject</c> BEFORE posting,
///     so however early the reply lands it is buffered and replayed to a late subscriber.</item>
/// </list>
///
/// <para>The upsert handler's <c>DispatchInnerCreate</c> ran the unsafe order on exactly this message
/// shape — a self-targeted <c>CreateNodeRequest</c> on the mesh hub, dispatched OFF the action block
/// from the <c>persistence.Read</c> continuation — which is what made <c>CreateOrUpdateNodeRequest</c>
/// silently never answer.</para>
///
/// <para>The race is made DETERMINISTIC here the same way
/// <c>WorkspaceCacheEvictionTest.GetMeshNode_WarmOwner_DropsResponse_WhenSubjectRegisteredAfterPost</c>
/// does it, but without a fixed sleep: the fence is the node becoming readable plus two full
/// request/response round-trips through the same mesh hub, which together prove the create's turn ran
/// to completion and its reply was pumped.</para>
/// </summary>
public class UpsertInnerCreateObservationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    /// <summary>
    /// Fences until the create's turn has demonstrably finished: the node is readable, and two
    /// complete round-trips have since travelled through the mesh hub. Anything the create posted is
    /// therefore long since pumped — which is the interleaving that loses a post-hoc registration.
    /// </summary>
    private async Task FenceCreateCompleted(string path)
    {
        await Mesh.GetMeshNode(path).Should().Within(15.Seconds()).Match(n => n is not null);
        await Mesh.GetMeshNode(path).Should().Within(15.Seconds()).Match(n => n is not null);
    }

    [Fact(Timeout = 60000)]
    public async Task SelfTargetedCreate_OnTheMeshHub_DropsItsReply_WhenTheSubjectIsRegisteredAfterPost()
    {
        // ARM 1 — the #981 shape: post first, register the callback afterwards.
        var doomedId = $"upsert-race-doomed-{Guid.NewGuid():N}";
        var doomedPath = $"{TestPartition}/{doomedId}";
        var doomed = Mesh.Post(
            new CreateNodeRequest(new MeshNode(doomedId, TestPartition)
            {
                Name = doomedId,
                NodeType = "Markdown"
            }),
            o => o.WithTarget(Mesh.Address));
        doomed.Should().NotBeNull();

        // The create really did run and answer — so its reply hit HandleCallbacks with no subject.
        await FenceCreateCompleted(doomedPath);

        // Registering now cannot recover a reply that was already consumed: the callback is orphaned.
        // THIS is the leaked `CreateNodeRequest@mesh/<self>` the quiescing budget reports.
        await Mesh.Observe(doomed!).Select(d => (object?)d.Message)
            .Should().NotEmit(within: 3.Seconds(),
                because: "a response with no registered subject is dropped by HandleCallbacks");

        // ARM 2 — the fix shape: pre-register, then post. Same fence, reply still delivered.
        var safeId = $"upsert-race-safe-{Guid.NewGuid():N}";
        var safePath = $"{TestPartition}/{safeId}";
        var safe = Mesh.Observe(
            new CreateNodeRequest(new MeshNode(safeId, TestPartition)
            {
                Name = safeId,
                NodeType = "Markdown"
            }),
            o => o.WithTarget(Mesh.Address));

        await FenceCreateCompleted(safePath);

        await safe.Select(d => d.Message)
            .Should().Within(10.Seconds())
            .Match(m => m is CreateNodeResponse { Success: true },
                "the pre-registered AsyncSubject buffers the reply for an arbitrarily late subscriber");
    }

    /// <summary>
    /// The production seam: <c>CreateOrUpdateNodeRequest</c> for a node that does NOT exist forwards an
    /// inner self-targeted <c>CreateNodeRequest</c> and must ANSWER. Before the fix that inner create
    /// used the post-then-observe order, so whenever the turn won the race the upsert produced no
    /// response at all and its caller's callback leaked.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Upsert_OfAMissingNode_Answers_ThroughTheInnerCreate()
    {
        var id = $"upsert-answers-{Guid.NewGuid():N}";
        var node = new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" };

        var response = await Mesh
            .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
            .Should().Within(30.Seconds()).Emit(
                "the upsert's inner CreateNodeRequest must never lose its reply");

        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.Path.Should().Be($"{TestPartition}/{id}");
    }
}
