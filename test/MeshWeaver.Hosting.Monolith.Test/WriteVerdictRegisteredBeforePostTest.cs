using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>#2882 — the WRITE path's twin of
/// <see cref="WorkspaceCacheEvictionTest.GetMeshNode_WarmOwner_DropsResponse_WhenSubjectRegisteredAfterPost"/>.</b>
///
/// <para><c>MeshNodeStreamHandle.UpdateRemote</c> used to <c>Post</c> the
/// <see cref="PatchDataRequest"/> and only THEN register the response subject
/// (<c>Observe(delivery)</c>). The hub DROPS any response whose requestId has no registered
/// subject yet ("No subject found for response … treating as processed",
/// <c>MessageHub.HandleCallbacks</c>). A WARM owning per-node hub acks in sub-millisecond time, so
/// a thread-pool preemption between Post and Observe lost the verdict — the caller waited out the
/// full 31 s <c>WriteVerdictBound</c> and failed <c>OwnerUnreachable</c>, and the request trail
/// could only say <c>REGISTERED_AFTER_POST … earlier stages not recorded</c>. That is the
/// signature behind the intermittent 31 s write timeouts (1-in-6 bulk runs of
/// <c>MeshWeaver.AI.Test</c>; Plugins#1014's <c>Patch_ConcurrentUpdates_NoDeadlock</c>).</para>
///
/// <para>The fix is the same as the read path's: register the subject BEFORE posting, via the
/// caller-supplied-id <c>Observe(request, options, messageId)</c> seam — which the write needs
/// because it must also arm <c>LatePatchResponseRegistry</c> under that id before the post. Here
/// the preemption is made explicit (an awaited delay) so the race is DETERMINISTIC in both
/// directions.</para>
/// </summary>
public class WriteVerdictRegisteredBeforePostTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 30000)]
    public async Task PatchAck_WarmOwner_IsDropped_WhenSubjectRegisteredAfterPost_AndKept_WhenBefore()
    {
        var path = $"{TestPartition}/write-race";
        await NodeFactory.CreateNode(
            new MeshNode("write-race", TestPartition) { Name = "Warm", NodeType = "Markdown" }).Should().Emit();

        // WARM the owning per-node hub so a patch is acked in sub-ms — the precondition under
        // which the post-then-observe window loses the verdict.
        await Mesh.GetMeshNode(path).Should().Match(n => n is not null);
        var addr = new Address(path);

        // (1) OLD shape — post, then a deliberate preemption, then register the subject. The warm
        //     owner's ack lands during the delay, finds no subject, and is dropped: observing
        //     afterwards never emits. This arm is what fails if anyone reverts to post-then-observe.
        var dropped = RequestHub.Post(
            new PatchDataRequest(new MeshNodeReference(), new RawJson("""{"name":"AfterPost"}""")),
            o => o.WithTarget(addr));
        dropped.Should().NotBeNull();
        await Task.Delay(300); // let the warm ack complete before the subject exists
        await RequestHub.Observe(dropped!).Select(d => (object?)d.Message)
            .Should().NotEmit(within: 2.Seconds());

        // (2) FIX shape — the #2882 seam: a caller-minted id, subject registered before the post.
        //     The same preemption is harmless: the ack is buffered in the AsyncSubject and
        //     replayed to the late subscriber.
        var requestId = Guid.NewGuid().AsString();
        var safe = RequestHub.Observe(
            new PatchDataRequest(new MeshNodeReference(), new RawJson("""{"name":"BeforePost"}""")),
            o => o.WithTarget(addr),
            requestId);
        Assert.NotNull(safe); // the target address resolves, so the seam returns the observable
        await Task.Delay(300); // same preemption — but the subject was registered before the post
        await safe!.Select(d => d.Message as PatchDataResponse)
            .Should().Within(5.Seconds()).Match(r => r != null && r.Success);
    }
}
