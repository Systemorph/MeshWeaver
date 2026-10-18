using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>The owner holds the delete seal</b> (MeshWeaver#6856). A delete guarded by
/// <see cref="DeleteNodeRequest.ExpectedVersion"/> runs on silo 1; the node's OWNER lives on silo 0.
/// Before the fix the only seal was held by the deleting process, so the owner on silo 0 never saw it:
/// after it confirmed the version it could still acknowledge an own write, and when the delete then
/// reached the store first, that write's later flush stored the node again while the delete had
/// reported success. Now the owner takes the seal in the turn that confirms the version, defers its
/// own writes while it holds it, and releases it on the deleter's commit or abort message — or, when
/// the deleter dies, on its own teardown. No timer is involved anywhere.
///
/// <para>Controlled switches, no clocks: the deleter's store holds the delete (<c>HoldDeletes</c>) or the
/// delete's child enumeration (<c>HoldEnumerations</c>) — both AFTER the owner's verdict — and the owner's
/// store holds the owner's flush (<c>HoldWrites</c>), so that on the old code the acknowledged write lands
/// after the delete, the resurrection order. The only fixed waits are the short negative windows in which
/// a deferred write must NOT answer.</para>
/// </summary>
public abstract class OwnerHeldDeleteSealTestBase(FaultInjectionCluster mesh)
{
    /// <summary>Silo the node's owner is activated on (the primary; never killed).</summary>
    protected const int OwnerSilo = 0;

    /// <summary>Silo the delete runs on.</summary>
    protected const int DeleterSilo = 1;

    /// <summary>How long a deferred write is watched for an answer it must not give.</summary>
    protected static readonly TimeSpan DeferredWindow = TimeSpan.FromSeconds(2);

    protected FaultInjectionCluster Mesh => mesh;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Creates <c>{prefix}-{guid}/Doc</c> on the owner silo and proves its owner is activated THERE.</summary>
    protected async Task<(string Path, IMessageHub Owner)> ArrangeOwnedNode(string prefix, bool withChild = false)
    {
        var services = mesh.Silo(OwnerSilo);
        var hub = mesh.Hub(OwnerSilo);
        var access = services.GetRequiredService<AccessService>();
        var meshService = services.GetRequiredService<IMeshService>();
        var ns = $"{prefix}-{Guid.NewGuid():N}";
        var path = $"{ns}/Doc";
        await access.RunAsSystem(() => meshService.CreateNode(
                new MeshNode("Doc", ns) { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active }))
            .Should().Within(TestTimeouts.Convergence).Emit("the node is created on its owner's silo", Ct);
        if (withChild)
            await access.RunAsSystem(() => meshService.CreateNode(
                    new MeshNode("Child", path) { Name = "child", NodeType = "Markdown", State = MeshNodeState.Active }))
                .Should().Within(TestTimeouts.Convergence).Emit("the child is created", Ct);
        await access.RunAsSystem(() => hub.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence).Emit($"the owner answers a ping on silo {OwnerSilo}", Ct);
        var owner = hub.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull(
            $"the owner must be activated on silo {OwnerSilo} — with the owner beside the deleter the case "
            + "measures the in-process seal, not the cross-process one");
        mesh.Hub(DeleterSilo).GetHostedHub(new Address(path), HostedHubCreation.Never).Should().BeNull(
            "the deleter's silo must not host the owner");
        return (path, owner!);
    }

    /// <summary>The version the store of record holds — what the deleter read.</summary>
    protected long StoredVersion(string path)
        => (mesh.Stored(path) ?? throw new InvalidOperationException($"'{path}' is not stored")).Version;

    /// <summary>A version-guarded delete issued on the deleter silo, connected and replayed.</summary>
    protected IConnectableObservable<IMessageDelivery<DeleteNodeResponse>> Delete(string path, long expected)
    {
        var deleter = mesh.Hub(DeleterSilo);
        var access = mesh.Silo(DeleterSilo).GetRequiredService<AccessService>();
        return access.RunAsSystem(() => deleter.NodeOperationIssuingHub().Observe(
                new DeleteNodeRequest(path) { ExpectedVersion = expected },
                o => o.WithTarget(deleter.NodeOperationTarget())))
            .Replay(1);
    }

    /// <summary>
    /// An OWN write on the owner (the commit turn the seal guards), materialized and replayed so its
    /// one answer — the acknowledged node or the refusal — can be asserted on, or its absence.
    /// </summary>
    protected IConnectableObservable<Notification<MeshNode>> OwnWrite(IMessageHub owner, string name)
        => mesh.Silo(OwnerSilo).GetRequiredService<AccessService>()
            .RunAsSystem(() => owner.GetWorkspace().GetMeshNodeStream().Update(n => n with { Name = name }))
            .Materialize()
            .Where(n => n.Kind != NotificationKind.OnCompleted)
            .Replay();

    /// <summary>The owner silo's registry — the process that must hold the seal.</summary>
    protected RecentlyDeletedRegistry OwnerRegistry
        => mesh.Silo(OwnerSilo).GetRequiredService<RecentlyDeletedRegistry>();
}

/// <summary>Commit and abort, on one cluster (neither case removes a silo).</summary>
public class OwnerHeldDeleteSealTest(OwnerHeldDeleteSealTest.Cluster cluster)
    : OwnerHeldDeleteSealTestBase(cluster), IClassFixture<OwnerHeldDeleteSealTest.Cluster>
{
    /// <summary>
    /// THE FALSIFIER. The guarded delete passed the owner's version check and is paused at the
    /// deleter's store. An own write on the owner in that window must not be acknowledged: it is
    /// deferred, and refused once the delete commits. The node stays deleted. On the old code the
    /// write was acknowledged at once (the owner's process held no seal), and its flush — held here
    /// until after the delete — stored the node again.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AWriteOnTheOwnerAfterTheVersionCheck_IsDeferredAndRefusedOnCommit_AndTheNodeStaysDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, owner) = await ArrangeOwnedNode("seal-commit");
        var read = StoredVersion(path);

        using var deleteHold = Mesh.Storage(DeleterSilo).HoldDeletes(path);
        using var flushHold = Mesh.Storage(OwnerSilo).HoldWrites(path);
        var delete = Delete(path, read);
        using var deleteConnection = delete.Connect();
        await deleteHold.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the guarded delete passed the owner's verdict and reached the deleter's store", ct);
        var write = OwnWrite(owner, "written after the version check");
        using var writeConnection = write.Connect();
        await write.Should().NotEmit(DeferredWindow,
            "a write the confirmed delete would remove is neither acknowledged nor refused while the seal is held",
            ct);
        OwnerRegistry.IsVersionGuardedDeletionInFlight(path).Should().BeTrue(
            "the OWNER's process holds the seal from its verdict until the deleter commits or aborts");

        deleteHold.Release();
        var response = (await delete.Should().Within(TestTimeouts.Convergence).Emit("a delete always answers", ct)).Message;
        response.Success.Should().BeTrue(response.Error ?? "the deleter named the version the owner confirmed");

        var answer = await write.Should().Within(TestTimeouts.Convergence)
            .Emit("the deferred write is answered once the delete commits", ct);
        answer.Kind.Should().Be(NotificationKind.OnError,
            "the node is deleted: a write deferred by its seal is refused, never acknowledged");
        answer.Exception.Should().BeOfType<SubtreeDeletionInFlightException>();

        flushHold.Release();
        Mesh.Stored(path).Should().BeNull("nothing acknowledged after the version check can store the node again");
    }

    /// <summary>
    /// NEGATIVE CONTROL: the same deferred write is APPLIED when the delete aborts after the owner's
    /// check. The delete here is non-recursive and the node has a child, so it gives up at its
    /// has-children decision — paused on the deleter's child enumeration, which runs after the
    /// owner's verdict. Its abort message releases the seal and the write commits.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AWriteDeferredByTheSeal_IsApplied_WhenTheDeleteAborts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, owner) = await ArrangeOwnedNode("seal-abort", withChild: true);
        var read = StoredVersion(path);

        using var enumerationHold = Mesh.Storage(DeleterSilo).HoldEnumerations(path);
        var delete = Delete(path, read);
        using var deleteConnection = delete.Connect();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => OwnerRegistry.IsVersionGuardedDeletionInFlight(path))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the owner confirmed the version and took the seal", ct);
        await enumerationHold.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the delete is paused at its child enumeration, after the owner's verdict", ct);

        var write = OwnWrite(owner, "written while the delete was undecided");
        using var writeConnection = write.Connect();
        await write.Should().NotEmit(DeferredWindow, "the seal defers the write while the delete is undecided", ct);

        enumerationHold.Release();
        var response = (await delete.Should().Within(TestTimeouts.Convergence).Emit("a delete always answers", ct)).Message;
        response.Success.Should().BeFalse("the node has a child and the delete is not recursive");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.HasChildren);

        var answer = await write.Should().Within(TestTimeouts.Convergence)
            .Emit("the abort releases the seal and the deferred write runs", ct);
        answer.Kind.Should().Be(NotificationKind.OnNext, answer.Exception?.Message ?? "the delete aborted");
        answer.Value.Name.Should().Be("written while the delete was undecided");
        OwnerRegistry.IsVersionGuardedDeletionInFlight(path).Should().BeFalse("the abort lifted the owner's seal");
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.Stored(path))
            .Where(n => n?.Name == "written while the delete was undecided")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the applied write reaches the store", ct);
    }

    /// <summary>
    /// A flush the OWNER's process admitted before the seal — an own write acknowledged before the
    /// version check, still on its way to the store — must settle before the owner confirms. Held
    /// here below the owner's storage guard. Before, the owner confirmed at once, the delete on the
    /// other silo removed the older row, and the released flush then stored the node again; the
    /// deleter's own settle step sees only its own process.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AFlushAdmittedBeforeTheSeal_SettlesBeforeTheOwnerConfirms_AndTheNodeStaysDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, owner) = await ArrangeOwnedNode("seal-admitted");

        using var flushHold = Mesh.Storage(OwnerSilo).HoldWrites(path);
        var acked = OwnWrite(owner, "acknowledged before the version check");
        using var ackConnection = acked.Connect();
        var ack = await acked.Should().Within(TestTimeouts.Convergence).Emit("an own write is acknowledged from memory", ct);
        ack.Kind.Should().Be(NotificationKind.OnNext, ack.Exception?.Message ?? "no seal yet");
        await Mesh.Storage(OwnerSilo).HeldWrites(path).Should().Within(TestTimeouts.Convergence)
            .Emit("its flush was admitted by the owner's storage guard and is held below it", ct);

        var delete = Delete(path, ack.Value!.Version);
        using var deleteConnection = delete.Connect();
        await delete.Should().NotEmit(DeferredWindow,
            "the owner does not confirm while a flush it admitted before the seal is still on its way", ct);

        flushHold.Release();
        var response = (await delete.Should().Within(TestTimeouts.Convergence).Emit("a delete always answers", ct)).Message;
        response.Success.Should().BeTrue(response.Error ?? "the deleter named the version the owner acknowledged");
        Mesh.Stored(path).Should().BeNull("the admitted flush landed BEFORE the delete, never after it");
    }

    /// <summary>
    /// A delete answers success only once the owner's seal — and its row guard — is lifted, so a
    /// caller that recreates the path at once is accepted. Before, the release was posted and the
    /// success sent in the same breath, to different hubs, and the recreate could meet the guard.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ADeleteAnswersAfterTheOwnersGuardIsLifted_SoAnImmediateRecreateIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, _) = await ArrangeOwnedNode("seal-recreate");
        var delete = Delete(path, StoredVersion(path));
        using var deleteConnection = delete.Connect();
        var response = (await delete.Should().Within(TestTimeouts.Convergence).Emit("a delete always answers", ct)).Message;
        response.Success.Should().BeTrue(response.Error ?? "nothing changed since the read");

        OwnerRegistry.IsVersionGuardedDeletionInFlight(path).Should().BeFalse(
            "the success is sent only after the owner acknowledged the release");
        OwnerRegistry.IsUnderActiveDeletion(path, out _).Should().BeFalse("the owner's row guard is lifted too");

        var services = Mesh.Silo(OwnerSilo);
        var slash = path.LastIndexOf('/');
        await services.GetRequiredService<AccessService>().RunAsSystem(() => services.GetRequiredService<IMeshService>()
                .CreateNode(new MeshNode(path[(slash + 1)..], path[..slash]) { Name = "recreated", NodeType = "Markdown", State = MeshNodeState.Active }))
            .Should().Within(TestTimeouts.Convergence).Emit("the path can be recreated the moment the delete answers", ct);
    }

    /// <summary>
    /// THE STORE FENCE (the owner's decision on #6856): the owner is RECYCLED while its deleter is alive
    /// and paused before the store. Its teardown moves the stored row past the confirmed version with a
    /// compare-and-set, so the paused delete's compare-and-delete is refused with Conflict, and a
    /// replacement activation's acknowledged write survives. Negative control (recorded, PR #6933 head
    /// d4f88c31bb, which has no fence): nothing moved the row, the replacement acknowledged its write,
    /// the delete removed the row at the confirmed version and answered SUCCESS, and the acknowledged
    /// write was gone — in this harness the owner silo heard the delete before the held flush landed and
    /// dropped it; when the flush wins that race instead, the node is stored again behind a delete that
    /// reported success. Either way the delete's verdict contradicts an acknowledged write.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnOwnerRecycledWhileTheDeleteIsPaused_FencesTheRow_AndTheDeleteIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, owner) = await ArrangeOwnedNode("seal-fence");
        var read = StoredVersion(path);

        using var deleteHold = Mesh.Storage(DeleterSilo).HoldDeletes(path);
        var delete = Delete(path, read);
        using var deleteConnection = delete.Connect();
        await deleteHold.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the guarded delete passed the owner's verdict and is paused at the deleter's store", ct);

        owner.Dispose(); // the owner is recycled; its deleter is alive and paused
        var fenced = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.Stored(path))
            .Where(n => n is not null && n.Version > read)
            .Amb(Observable.Timer(TimeSpan.FromSeconds(10)).Select(_ => (MeshNode?)null))
            .Should().Within(TestTimeouts.Convergence).Emit("the fence lands, or the window closes", ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"[fence] stored after the recycle: v{fenced?.Version.ToString() ?? "-"}");

        // A replacement activation acknowledges an own write; its flush is held until after the delete.
        var services = Mesh.Silo(OwnerSilo);
        await services.GetRequiredService<AccessService>().RunAsSystem(() => Mesh.Hub(OwnerSilo).NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence).Emit("a replacement activation answers", ct);
        var replacement = Mesh.Hub(OwnerSilo).GetHostedHub(new Address(path), HostedHubCreation.Never);
        replacement.Should().NotBeNull("the replacement is activated on the owner's silo");
        using var flushHold = Mesh.Storage(OwnerSilo).HoldWrites(path);
        var write = OwnWrite(replacement!, "written by the replacement");
        using var writeConnection = write.Connect();
        var ack = await write.Should().Within(TestTimeouts.Convergence).Emit("the replacement acknowledges its write", ct);
        ack.Kind.Should().Be(NotificationKind.OnNext, ack.Exception?.Message ?? "the replacement is unsealed");

        deleteHold.Release();
        var response = (await delete.Should().Within(TestTimeouts.Convergence).Emit("a delete always answers", ct)).Message;
        flushHold.Release();
        var stored = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.Stored(path))
            .Where(n => n?.Name == "written by the replacement")
            .Amb(Observable.Timer(TimeSpan.FromSeconds(10)).Select(_ => (MeshNode?)null))
            .Should().Within(TestTimeouts.Convergence).Emit("the replacement's flush lands, or the window closes", ct);

        response.Success.Should().BeFalse(
            $"the fence moved the row past the confirmed version {read}; a delete that still went through reported "
            + $"success for a node that is now stored again (v{stored?.Version.ToString() ?? "-"})");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.Conflict);
        stored.Should().NotBeNull("the acknowledged write of the replacement survives");
    }

    /// <summary>
    /// The store fence FAULTS (#6856 review): the owner is recycled while its deleter is paused, and the
    /// fence's compare-and-set cannot reach the store. A failed fence proves nothing, so the owner's
    /// process keeps the path sealed: a replacement's own write is deferred, not acknowledged — the
    /// paused delete could still remove the row it would land on. The delete then commits, its commit
    /// message settles the seal, and the deferred write is refused. Before, a fence fault lifted the
    /// guard at once, and the replacement acknowledged a write the delete removed.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AFenceThatFaults_KeepsThePathSealed_UntilTheDeletesCommitSettlesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, owner) = await ArrangeOwnedNode("seal-fence-fault");
        var read = StoredVersion(path);

        using var deleteHold = Mesh.Storage(DeleterSilo).HoldDeletes(path);
        using var fenceFault = Mesh.Storage(OwnerSilo).FailConditionalWrites(path);
        var delete = Delete(path, read);
        using var deleteConnection = delete.Connect();
        await deleteHold.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the guarded delete passed the owner's verdict and is paused at the deleter's store", ct);

        owner.Dispose();
        await fenceFault.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the teardown's fence tried its compare-and-set and faulted", ct);

        var services = Mesh.Silo(OwnerSilo);
        await services.GetRequiredService<AccessService>().RunAsSystem(() => Mesh.Hub(OwnerSilo).NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence).Emit("a replacement activation answers", ct);
        var replacement = Mesh.Hub(OwnerSilo).GetHostedHub(new Address(path), HostedHubCreation.Never);
        replacement.Should().NotBeNull();
        var write = OwnWrite(replacement!, "written while the fence had failed");
        using var writeConnection = write.Connect();
        await write.Should().NotEmit(DeferredWindow,
            "the failed fence keeps the path sealed: a write the paused delete could remove is not acknowledged", ct);

        deleteHold.Release();
        var response = (await delete.Should().Within(TestTimeouts.Convergence).Emit("a delete always answers", ct)).Message;
        response.Success.Should().BeTrue(response.Error ?? "nothing moved the row past the confirmed version");
        var answer = await write.Should().Within(TestTimeouts.Convergence)
            .Emit("the delete's commit settles the seal and answers the deferred write", ct);
        answer.Kind.Should().Be(NotificationKind.OnError, "the node is deleted; the deferred write was never acknowledged");
        Mesh.Stored(path).Should().BeNull("nothing acknowledged survives into a stored row the delete reported removed");
    }

    /// <summary>Its own cluster.</summary>
    public class Cluster : FaultInjectionCluster;
}

/// <summary>
/// The deleter DIES after the owner's check: no commit or abort message will ever come. The owner
/// keeps the seal — nothing times it out — until its own activation is torn down, which releases it.
/// </summary>
public class OwnerHeldDeleteSealDeleterDeathTest(OwnerHeldDeleteSealDeleterDeathTest.Cluster cluster)
    : OwnerHeldDeleteSealTestBase(cluster), IClassFixture<OwnerHeldDeleteSealDeleterDeathTest.Cluster>
{
    [Fact(Timeout = 180_000)]
    public async Task WhenTheDeleterDies_TheOwnersTeardownReleasesTheSeal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, owner) = await ArrangeOwnedNode("seal-death");
        var read = StoredVersion(path);

        var deleteHold = Mesh.Storage(DeleterSilo).HoldDeletes(path);
        var delete = Delete(path, read);
        using var deleteConnection = delete.Connect();
        await deleteHold.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the guarded delete passed the owner's verdict and reached the deleter's store", ct);

        var write = OwnWrite(owner, "written before the deleter died");
        using var writeConnection = write.Connect();
        await write.Should().NotEmit(DeferredWindow, "the seal defers the write", ct);

        await Mesh.Kill(DeleterSilo);
        await write.Should().NotEmit(DeferredWindow,
            "no message will release the seal now, and nothing may time it out: it lasts as long as the owner",
            ct);
        OwnerRegistry.IsVersionGuardedDeletionInFlight(path).Should().BeTrue("the owner still holds the seal");

        owner.Dispose();
        var answer = await write.Should().Within(TestTimeouts.Convergence)
            .Emit("the owner's teardown releases the seal and answers the deferred write", ct);
        answer.Kind.Should().Be(NotificationKind.OnError, "a write deferred by a seal the teardown released was never acknowledged");
        // The teardown fences the row (the deleter may only be paused, as far as the owner can tell) and
        // then lifts the guard.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => !OwnerRegistry.IsVersionGuardedDeletionInFlight(path))
            .Should().Within(TestTimeouts.Convergence).Emit("the teardown's fence lands and the seal is lifted", ct);
        Mesh.Stored(path)!.Version.Should().BeGreaterThan(read,
            "the fence moved the row past the confirmed version, so no delete still on its way can remove it");

        // The delete never reached the store, so the node lives on and the next activation is unsealed.
        Mesh.Stored(path).Should().NotBeNull("the deleter died before its delete reached the store");
        var services = Mesh.Silo(OwnerSilo);
        await services.GetRequiredService<AccessService>().RunAsSystem(() =>
                services.GetRequiredService<IMeshNodeStreamCache>()
                    .Update(path, n => n with { Name = "after the teardown" }, Mesh.Hub(OwnerSilo).JsonSerializerOptions))
            .Should().Within(TestTimeouts.Convergence).Emit("a write to the fresh activation is not deferred", ct);
    }

    /// <summary>Its own cluster: the case kills a silo.</summary>
    public class Cluster : FaultInjectionCluster;
}

/// <summary>
/// Review round 7 (#6856): a delete whose COMMIT stage times out while its root storage delete is
/// still running. Unsubscribing does not stop that storage call, so the outcome is INDETERMINATE —
/// never an abort. The owner runs the store fence and keeps its deferred writes waiting until it has
/// landed; the late storage delete is then refused at its compare-and-delete, and the write survives.
/// Before, the deleter sent Aborted, the owner applied the deferred write at once, and the storage
/// delete — still running — could remove the row that write was about to update.
/// </summary>
public class AnIndeterminateDeleteIsFencedTest(AnIndeterminateDeleteIsFencedTest.Cluster cluster)
    : OwnerHeldDeleteSealTestBase(cluster), IClassFixture<AnIndeterminateDeleteIsFencedTest.Cluster>
{
    [Fact(Timeout = 180_000)]
    public async Task ACommitStageTimeout_WhileTheStorageDeleteRuns_IsFenced_NotAborted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, owner) = await ArrangeOwnedNode("seal-indeterminate");
        var read = StoredVersion(path);

        using var deleteHold = Mesh.Storage(DeleterSilo).HoldDeletes(path);
        var delete = Delete(path, read);
        using var deleteConnection = delete.Connect();
        await deleteHold.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the root storage delete was issued and is held at the deleter's store", ct);

        var write = OwnWrite(owner, "written while the delete was undecided");
        using var writeConnection = write.Connect();
        using var flushHold = Mesh.Storage(OwnerSilo).HoldWrites(path); // holds the fence's write too
        var response = (await delete.Should().Within(TestTimeouts.Convergence)
            .Emit("the commit stage times out while its storage delete is still held", ct)).Message;
        response.Success.Should().BeFalse("the commit stage gave up");

        await write.Should().NotEmit(DeferredWindow,
            "the outcome is indeterminate: the deferred write waits for the fence, it is not resumed as if aborted",
            ct);

        flushHold.Release(); // the fence lands: the row moves past the confirmed version
        var answer = await write.Should().Within(TestTimeouts.Convergence)
            .Emit("the fence settled the seal and the deferred write runs", ct);
        answer.Kind.Should().Be(NotificationKind.OnNext, answer.Exception?.Message ?? "the fence moved the row");

        deleteHold.Release(); // the storage delete, still running, now meets the fenced row
        // The held delete ACTUALLY runs — the mesh, not the timed-out pipeline, keeps it subscribed —
        // and the store refuses it.
        (await Mesh.Storage(DeleterSilo).ConditionalDeleteResults(path).Should().Within(TestTimeouts.Convergence)
                .Emit("the storage delete that outlived its pipeline reached the store", ct))
            .Should().BeFalse("it met the fenced row, past the version it was confirmed against");
        var stored = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.Stored(path))
            .Where(n => n?.Name == "written while the delete was undecided")
            .Amb(Observable.Timer(TimeSpan.FromSeconds(10)).Select(_ => (MeshNode?)null))
            .Should().Within(TestTimeouts.Convergence).Emit("the write is stored, or the window closes", ct);
        stored.Should().NotBeNull("the late storage delete was refused at the fenced row; the acknowledged write survives");
    }

    /// <summary>A cluster whose node operations time out quickly, so the commit stage gives up in seconds.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(ShortBudgetSiloConfigurator);
    }

    /// <summary>The fault-injection silo with a 4 s node-operation budget.</summary>
    public class ShortBudgetSiloConfigurator : FaultInjectionSiloConfigurator
    {
        /// <inheritdoc />
        protected override MeshBuilder ConfigureAdditional(MeshBuilder builder)
            => builder.ConfigureServices(services =>
                services.AddSingleton(new MeshOperationOptions { Timeout = TimeSpan.FromSeconds(4) }));
    }
}
