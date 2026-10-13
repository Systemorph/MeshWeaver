using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>An update whose patch reaches an absent node is refused as
/// <see cref="MeshNodeErrorCode.NotFound"/>, never as <see cref="MeshNodeErrorCode.Unknown"/>
/// around "No node found" prose (#6626).</b>
///
/// <para>When <c>GetMeshNodeStream(p).Update</c> races <c>DeleteNode(p)</c> and the delete wins
/// before the patch is routed, the OWNER never sees the <see cref="PatchDataRequest"/>: the router's
/// path resolution matches only an ancestor and answers a <see cref="DeliveryFailure"/>
/// (<see cref="ErrorType.NotFound"/>, "No node found at 'p'. Closest ancestor is …"). The writer's
/// update arms mapped every such failure to <see cref="MeshNodeErrorCode.Unknown"/>, so a racing
/// writer got an unclassified refusal for a plain absence and had to parse prose to tell.</para>
///
/// <para><see cref="ErrorType.NotFound"/> alone cannot carry the fact — it also means "no handler"
/// and "no route to that host". So the SENDER stamps <see cref="DeliveryFailure.NodeAbsent"/> where
/// it established that no node exists at the path, and the writer reads that stamp. Both halves are
/// driven here directly — no race loop: the router's answer to a patch aimed at an absent path, and
/// the writer's classification of exactly that answer.</para>
/// </summary>
public class UpdateOfAnAbsentNodeIsClassifiedNotFoundTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private async Task<DeliveryFailure> PatchAnAbsentChildOf(string parentId)
    {
        var parent = await NodeFactory.CreateNode(
                new MeshNode(parentId, TestPartition) { Name = parentId, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var absentPath = $"{parent.Path}/absent";

        // The exact request UpdateRemote posts (MeshNodeReference + a merge patch), aimed at a path
        // whose closest existing ancestor is `parent` — the shape the router sees for a patch that
        // a delete overtook.
        var patch = new PatchDataRequest(new MeshNodeReference(), new RawJson("{\"name\":\"late\"}"));
        // The response is the fault: success maps to null so a stray ack fails the type check below.
        var error = await Mesh.Observe<PatchDataResponse>(patch, o => o.WithTarget(new Address(absentPath)))
            .Take(1)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the router must answer a patch for an absent node, never stay silent",
                TestContext.Current.CancellationToken);
        var dfx = error.Should().BeOfType<DeliveryFailureException>().Subject;
        Output.WriteLine($"[nack] {dfx.Failure.ErrorType} nodeAbsent={dfx.Failure.NodeAbsent}: {dfx.Failure.Message}");
        return dfx.Failure;
    }

    /// <summary>The sender half: the router stamps the absence it established.</summary>
    [Fact]
    public async Task ThePatchOfAnAbsentNode_IsAnsweredWithTheTypedAbsenceStamp()
    {
        var failure = await PatchAnAbsentChildOf("patch-absent-sender");

        failure.ErrorType.Should().Be(ErrorType.NotFound);
        failure.Message.Should().Contain("No node found",
            "the premise: this is the very answer #6626 saw arrive as Unknown");
        failure.NodeAbsent.Should().BeTrue(
            "the router established that no node exists at the exact path; that fact must travel "
            + "TYPED, so no writer has to read it out of the message text");
    }

    /// <summary>The writer half: the stamped failure becomes the node-level NotFound.</summary>
    [Fact]
    public async Task TheWriterClassifiesTheRoutersAbsence_AsNotFound()
    {
        var failure = await PatchAnAbsentChildOf("patch-absent-writer");
        var path = failure.Delivery?.Target?.ToString() ?? "unknown";

        MeshNodeStreamHandle.ClassifyPatchDeliveryFailure(failure, path, failure.Message ?? "")
            .Code.Should().Be(MeshNodeErrorCode.NotFound,
                "an update whose patch met an absent node is refused for the same reason as one the "
                + "owner refuses after a delete — NotFound — and a writer must not have to parse prose");
    }

    /// <summary>
    /// The negative control: <see cref="ErrorType.NotFound"/> WITHOUT the stamp — "no handler for
    /// this request" from a live hub — says nothing about the node and stays
    /// <see cref="MeshNodeErrorCode.Unknown"/>.
    /// </summary>
    [Fact]
    public void ANotFoundWithoutTheStamp_StaysUnknown()
    {
        var unhandled = new DeliveryFailure(new MessageDelivery<RawJson>
            {
                Message = new RawJson("{}"),
                Sender = new Address("test/sender"),
                Target = new Address("TestData/live"),
            })
        {
            ErrorType = ErrorType.NotFound,
            Message = "No handler found for message type PatchDataRequest",
        };

        MeshNodeStreamHandle.ClassifyPatchDeliveryFailure(unhandled, "TestData/live", unhandled.Message!)
            .Code.Should().Be(MeshNodeErrorCode.Unknown,
                "ErrorType.NotFound alone also means 'no handler' or 'no route' — only the sender's "
                + "NodeAbsent stamp says the node itself is absent");
    }
}
