using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A gated delivery whose permission verdict is still in flight when its hub is disposed is
/// ANSWERED — never dropped.</b>
///
/// <para><b>The defect.</b> A <c>[RequiresPermission]</c> delivery is acted on when its verdict
/// arrives, off the turn that admitted it (<c>AccessControlPipeline.SenderOrderedRelease</c>). The
/// verdict's connection is owned by the hub, so the hub's ShutDown phase releases it — and a released
/// connection emits NOTHING to the release waiting on it. The delivery then got no handler, no
/// refusal and no NACK, and its sender waited out its whole budget. Measured on CI as
/// <c>ActivityActionReferenceDurabilityTest</c>: <c>RecycleNode</c>'s own read reached the activity
/// hub it had just asked to dispose (still <c>Started</c>, so admitted), and <c>GetMeshNode</c> timed
/// out after 60 s with the trail ending at <c>ROUTED onTarget=True state=Submitted</c>.</para>
///
/// <para><b>The answer forks on WHY the hub went down</b>, exactly as
/// <c>MessageService.NackThroughParent</c> does: a recycle answers the transient
/// <see cref="ErrorType.ShuttingDown"/> (the address is coming back), a node DELETE answers the
/// authoritative <see cref="ErrorType.NotFound"/> with <see cref="DeliveryFailure.NodeAbsent"/> (it is
/// not — "retry" would park the reader on an address that never answers, #1029).</para>
///
/// <para><b>The lever</b> is the framework's evaluator seam, narrowed to one identity and one armed
/// check (the same one <see cref="GatedWritesKeepTheSendersOrderTest"/> uses): the read's verdict is
/// held until the test releases it, so "the hub is disposed while the verdict is in flight" is the
/// state of the test, not a race it hopes to hit.</para>
///
/// <para><b>The controls</b> are the two tests against each other: the same held read gets
/// <see cref="ErrorType.ShuttingDown"/> under a recycle and <see cref="ErrorType.NotFound"/> under a
/// delete, so neither answer is the pipeline's only possible one. The recycle test also reads the
/// address again from the fresh activation, so its NACK is about the activation, not the node.</para>
/// </summary>
public class GatedDeliveryReleasedAtTeardownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Reader = "released-at-teardown-reader";

    /// <summary>1 ⇒ the next permission check for <see cref="Reader"/> is answered by <see cref="held"/>.</summary>
    private int armed;

    /// <summary>The held verdict. Completed by the test (always, in a <c>finally</c>).</summary>
    private readonly AsyncSubject<Permission> held = new();

    /// <summary>Completed when the armed check has been asked — i.e. the read reached the gate.</summary>
    private readonly AsyncSubject<Unit> heldAsked = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Applied after AddRowLevelSecurity, so the per-node hubs carry this delegate. Every
            // identity but Reader keeps the real evaluator.
            .ConfigureDefaultNodeHub(c => c.WithPermissionEvaluator(Evaluate));

    private IObservable<Permission> Evaluate(IMessageHub hub, string nodePath, string userId)
    {
        if (userId != Reader)
            return PermissionEvaluator.GetEffectivePermissions(hub, nodePath, userId);
        if (Interlocked.Exchange(ref armed, 0) == 1)
        {
            heldAsked.OnNext(Unit.Default);
            heldAsked.OnCompleted();
            return held;
        }
        return Observable.Return(Permission.All);
    }

    private void ReleaseHeldVerdict()
    {
        held.OnNext(Permission.All);
        held.OnCompleted();
    }

    private static AccessContext ReaderContext => new() { ObjectId = Reader, Name = Reader };

    /// <summary>Creates the probe node and activates its hub with an ordinary read.</summary>
    private async Task<string> CreateActiveNode(string id)
    {
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition) { Name = "probe", NodeType = "Markdown" })
            .Should().Emit();
        // Activate the node hub with an ordinary read, so the hold below is the only variable.
        await RequestHub.GetMeshNode(path).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("the node hub is active");
        return path;
    }

    /// <summary>
    /// Posts a gated read whose verdict is HELD, waits until it reached the gate, runs
    /// <paramref name="teardown"/> against its hub, and returns how the read was answered.
    /// </summary>
    private async Task<(string Answer, DeliveryFailure? Failure)> HeldReadAnsweredAcross(
        string path, Func<Task> teardown)
    {
        try
        {
            Volatile.Write(ref armed, 1);
            // From a client of its own, so the teardown's own traffic is not ordered behind it. A
            // COLLECTION read, deliberately: it is gated by [RequiresPermission(Read)] alone, so the
            // held check is the access gate's. A MeshNodeReference read is also checked by the node
            // hub's read validator, which asks the same evaluator first and would take the hold.
            var answer = GetClient()
                .Observe<GetDataResponse>(
                    new GetDataRequest(new CollectionReference(nameof(MeshNode))),
                    o => o.WithTarget(new Address(path)).WithAccessContext(ReaderContext))
                .Take(1)
                .Select(r => ($"answered by the old activation (error: {r.Message.Error ?? "none"})",
                    (DeliveryFailure?)null))
                .Catch((Exception ex) => Observable.Return(ex is DeliveryFailureException failure
                    ? ($"nack:{failure.Failure.ErrorType}", (DeliveryFailure?)failure.Failure)
                    : ($"error:{ex.GetType().Name}: {ex.Message}", (DeliveryFailure?)null)))
                .Replay(1);
            using var connection = answer.Connect();

            await heldAsked.Should().Within(TestTimeouts.Convergence)
                .Emit("the read reached the permission gate, whose verdict is now held");

            await teardown();

            var result = await answer.Should().Within(TestTimeouts.Convergence)
                .Emit("the held read is owed an answer once its hub is gone");
            Output.WriteLine($"[answer] {result.Item1} nodeAbsent={result.Item2?.NodeAbsent} "
                + $"message={result.Item2?.Message}");
            return result;
        }
        finally
        {
            ReleaseHeldVerdict();
        }
    }

    [Fact]
    public async Task AReadWhoseVerdictIsInFlight_WhenItsHubIsRecycled_IsNackedShuttingDown()
    {
        var path = await CreateActiveNode("released-at-teardown");

        var (answer, failure) = await HeldReadAnsweredAcross(path, async () =>
            await RequestHub.RecycleNode(path, reason: "GatedDeliveryReleasedAtTeardownTest: dispose the hub "
                    + "while a gated read's verdict is in flight")
                .Should().Within(TestTimeouts.Convergence)
                .Emit("control: the address answers again from a fresh activation"));

        answer.Should().Be($"nack:{ErrorType.ShuttingDown}",
            "the activation that admitted the read is gone and the address is coming back, so "
            + "the caller must hear the transient recycling NACK and re-probe — not silence");
        failure!.NodeAbsent.Should().BeFalse("a recycled address is not absent");
    }

    [Fact]
    public async Task AReadWhoseVerdictIsInFlight_WhenItsNodeIsDeleted_IsNackedNotFound()
    {
        var path = await CreateActiveNode("released-at-delete");

        var (answer, failure) = await HeldReadAnsweredAcross(path, async () =>
            await NodeFactory.DeleteNode(path)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the node is deleted while the read's verdict is held"));

        answer.Should().Be($"nack:{ErrorType.NotFound}",
            "the node was deleted, so the address will not reactivate — a transient ShuttingDown "
            + "would send the reader re-probing an address that never answers (#1029)");
        failure!.NodeAbsent.Should().BeTrue("the typed form of the delete tombstone's verdict");
        failure.Message.Should().Contain("No node found").And.NotContain("shutting down",
            "the wording is contract: classifiers read the transient marker off the text");
    }
}
