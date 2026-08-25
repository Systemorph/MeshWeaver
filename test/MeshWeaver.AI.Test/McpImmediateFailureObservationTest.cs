using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the response-observation contract behind the fix for the
/// <c>McpNegativeOperationsTest.DeletedNode_EveryVerb_FailsCleanly</c> CI flake
/// (PR #500, run 29571004819 shard 0: the MOVE verb produced NO emission for 45.5s
/// under CI load while passing locally in 646ms).
///
/// <para><b>The race:</b> <c>MeshOperations</c>' mutation verbs used the
/// Post-then-<c>Observe(delivery)</c> shape: the response subject was registered
/// only AFTER the request was posted. A target that fails immediately (a DELETED
/// node → instant NotFound/DeliveryFailure) can have its failure pumped through
/// the caller hub's action block inside that window; the failure finds no
/// registered callback and is dropped, and the later-registered subject never
/// fires — the caller then sits until the hub's 60s <c>RequestTimeout</c>, past
/// the test's 45s bound. Under CI load the posting thread is routinely preempted
/// between the two calls, which is why the flake was load-shaped.</para>
///
/// <para><b>The fix:</b> the verbs now use the pre-registering
/// <c>hub.Observe(request, options)</c> overload — the subject is registered
/// BEFORE the post and (being an AsyncSubject) buffers the terminal event for
/// any subscriber, however late. The test below proves that property in the
/// worst case: the terminal event is FORCED to be fully pumped (fenced by two
/// complete unrelated request/response round-trips) before the caller ever
/// subscribes — the exact interleaving that lost the response pre-fix.</para>
/// </summary>
public class McpImmediateFailureObservationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshOperations Ops => new(Mesh);

    [Fact(Timeout = 60000)]
    public async Task PreRegisteredObserve_DeliversTerminalEvent_ToArbitrarilyLateSubscriber()
    {
        // A live node to fence round-trips against, and a node we create then delete
        // so its address fails fast — the exact shape of the flaky MOVE verb.
        var fenceId = $"fence-{Guid.NewGuid():N}";
        var doomedId = $"doomed-{Guid.NewGuid():N}";
        await NodeFactory.CreateNode(new MeshNode(fenceId, TestPartition)
        {
            Name = fenceId, NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# fence" }
        }).Should().Emit();
        await NodeFactory.CreateNode(new MeshNode(doomedId, TestPartition)
        {
            Name = doomedId, NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# doomed" }
        }).Should().Emit();
        await NodeFactory.DeleteNode($"{TestPartition}/{doomedId}").Should().Emit();

        // Observe(request, options) posts EAGERLY and registers the response subject
        // BEFORE the post. Hold the returned observable WITHOUT subscribing.
        var deletedAddress = new Address($"{TestPartition}/{doomedId}");
        var pending = Mesh.Observe(
            new MoveNodeRequest($"{TestPartition}/{doomedId}", $"{TestPartition}/dest-{Guid.NewGuid():N}"),
            o => o.WithTarget(deletedAddress));

        // FENCE: two complete request/response round-trips through the same hub.
        // By the time both responses have arrived, the deleted-target request's
        // terminal event (response or DeliveryFailure) has been pumped long since —
        // this is the interleaving in which the old Post-then-Observe(delivery)
        // shape had already dropped it.
        var fenceAddress = new Address($"{TestPartition}/{fenceId}");
        await Mesh.Observe(new PingRequest(), o => o.WithTarget(fenceAddress)).Should().Emit();
        await Mesh.Observe(new PingRequest(), o => o.WithTarget(fenceAddress)).Should().Emit();

        // LATE subscribe: the buffered terminal event must still be delivered —
        // as a response (Move handler answering Success=false) or as an error
        // (DeliveryFailure). Either way it must arrive promptly; only a LOST
        // event would leave the stream silent (the pre-fix 45s hang).
        var notification = await pending.Materialize()
            .Should().Within(TimeSpan.FromSeconds(15)).Emit(
                "the pre-registered response subject buffers the terminal event for late subscribers");
        if (notification.Kind == System.Reactive.NotificationKind.OnNext)
        {
            (notification.Value.Message is MoveNodeResponse { Success: false })
                .Should().BeTrue("a move of a deleted node must not succeed");
        }
        else
        {
            notification.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
                "the only acceptable non-response terminal event is the delivery failure");
        }

        // And the user-facing surface: MOVE of a deleted node must produce its
        // error envelope promptly — never a silent stall.
        var moveResult = await Ops.Move($"{TestPartition}/{doomedId}", $"{TestPartition}/dest2-{Guid.NewGuid():N}")
            .Should().Within(TimeSpan.FromSeconds(15)).Emit();
        moveResult.Should().Contain("Error",
            "moving a deleted node must surface an error envelope");
    }
}
