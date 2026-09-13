// <meshweaver>
// Id: Testing/MessagingHub/HandlerFaultPastDisposeHostedHubsTest
// DisplayName: Testing/MessagingHub/HandlerFaultPastDisposeHostedHubsTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Systemorph/MeshWeaver#4072 — a handler that FAULTS on a hub that is already past
/// <see cref="MessageHubRunLevel.DisposeHostedHubs"/> must still answer its requester.
///
/// <para><b>How a handler comes to run there at all.</b> The turn loop is strictly serial, so a
/// hub cannot advance its own shutdown phases while a turn is in flight — but a delivery
/// pipeline stage may DETACH the handler from its turn: <c>AccessControlPipeline</c> takes its
/// permission decision outside the gate and invokes <c>next</c> from a pool continuation while
/// the turn itself has already returned <c>Forwarded</c>. From then on the handler runs
/// concurrently with whatever the pump does next — including the hub's own teardown. Measured on
/// <c>LeavingHubAdoptionSweepTest</c> (core <c>main</c>, 14 s, PASSING): a <c>SubscribeRequest</c>
/// entered its handler at +43 ms and faulted at +56 ms, and by the time the fault was classified
/// the hub read <c>RunLevel=Dead</c>. The stage that models this below is that shape reduced to
/// its mechanism — a stage that returns the turn and runs <c>next</c> when the test says so.</para>
///
/// <para><b>The two losses.</b> (1) <c>ReportFailure</c> declined to post at all once
/// <c>RunLevel &gt;= DisposeHostedHubs</c>, so the genuine-fault arm's verdict was computed and
/// dropped, even though the post seam already knew how to forward a correlated reply through a
/// live parent. (2) With the PARENT past <c>DisposeHostedHubs</c> too — the state every sibling
/// is answered in during a whole-tree teardown — <c>NackThroughParent</c> declined as well, and
/// nothing else was tried, while the requester sat <c>Quiescing</c> on exactly that callback and
/// re-armed its budget waiting for it. Each test below pins one loss; the contract they share is
/// that the requester hears a <see cref="DeliveryFailure"/> and not its own quiesce cut-off.</para>
/// </summary>
public class HandlerFaultPastDisposeHostedHubsTest(MeshTestContext context) : InMeshTestBase(context)
{
    private record FaultingRequest : IRequest<FaultResponse>;

    private record FaultResponse;

    private record ParkTurn;

    /// <summary>The fault the handler dies with — chosen per test.</summary>
    public enum Fault
    {
        Genuine,
        HubDisposing,
        DisposedContainer,
    }

    private static readonly Address ParentAddress = new("fault-parent", "1");
    private static readonly Address VictimAddress = new("fault-victim", "1");
    private static readonly Address ChildAddress = new("fault-child", "1");
    private static readonly Address RequesterAddress = new("fault-requester", "1");

    /// <summary>
    /// A hub whose <see cref="FaultingRequest"/> handler runs DETACHED from its turn — the
    /// production shape (<c>AccessControlPipeline</c>), with the release under the test's control
    /// so the fault can be made to land at a chosen run level. The turn returns <c>Forwarded</c>
    /// at once; <c>next</c> runs when <paramref name="release"/> completes.
    /// </summary>
    private static MessageHubConfiguration Victim(
        MessageHubConfiguration c, Fault fault, AsyncSubject<Unit> release, AsyncSubject<Unit> captured)
        => c
            // Plumbing-only fixtures post as infrastructure — the same identity HubTestBase gives
            // its host/client hubs; these hubs are created outside those routes.
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(FaultingRequest), typeof(FaultResponse), typeof(ParkTurn))
            .AddDeliveryPipeline(p => p.AddPipeline((delivery, ct, next) =>
            {
                if (delivery.Message is not FaultingRequest)
                    return next.Invoke(delivery, ct);
                release
                    .SelectMany(_ => next.Invoke(delivery, ct))
                    .Subscribe(_ => { }, _ => { });
                captured.OnNext(Unit.Default);
                captured.OnCompleted();
                return Observable.Return(delivery.Forwarded());
            }))
            .WithHandler<FaultingRequest>((h, _) =>
            {
                // Reflective dispatch wraps every handler fault — the trail must still classify
                // the INNER exception, which is what the wrapper here pins.
                Exception inner = fault switch
                {
                    Fault.HubDisposing => new HubDisposingException(h.Address, "stream"),
                    Fault.DisposedContainer => new ObjectDisposedException(
                        "LifetimeScope",
                        "Instances cannot be resolved and nested lifetimes cannot be created from this "
                        + "LifetimeScope as it (or one of its parent scopes) has already been disposed."),
                    _ => new InvalidOperationException("the handler itself is broken"),
                };
                throw new TargetInvocationException(inner);
            });

    /// <summary>A child that parks its own action block, so its owner's DisposeHostedHubs phase
    /// cannot complete until the test lets go — which pins the owner AT that phase.</summary>
    private static MessageHubConfiguration ParkedChild(
        MessageHubConfiguration c, AsyncSubject<Unit> parked, Func<bool> released)
        => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(ParkTurn))
            .WithHandler<ParkTurn>((_, d) =>
            {
                parked.OnNext(Unit.Default);
                parked.OnCompleted();
                SpinWait.SpinUntil(released, TimeSpan.FromSeconds(60));
                return d.Processed();
            });

    /// <summary>
    /// Loss (1): the victim is torn down on its own (a recycle) — its parent stays live. The
    /// fault is GENUINE, so the verdict must stay <see cref="ErrorType.Unknown"/> with the
    /// exception in the text, and it must reach the requester through the live parent instead of
    /// dying on <c>ReportFailure</c>'s former run-level gate.
    /// </summary>
    [MeshFact(TimeoutSeconds = 1)]
    public async Task AGenuineFaultOnAHubPastDisposeHostedHubs_IsReportedThroughTheLiveParent()
    {
        var ct = CancellationToken.None;
        var release = new AsyncSubject<Unit>();
        var captured = new AsyncSubject<Unit>();
        var parked = new AsyncSubject<Unit>();
        var releaseChild = 0;

        var requester = Mesh.GetHostedHub(RequesterAddress, c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(FaultingRequest), typeof(FaultResponse)), HostedHubCreation.Always)!;
        var victim = Mesh.GetHostedHub(VictimAddress, c => Victim(c, Fault.Genuine, release, captured), HostedHubCreation.Always)!;
        victim.GetHostedHub(ChildAddress,
                c => ParkedChild(c, parked, () => Volatile.Read(ref releaseChild) == 1), HostedHubCreation.Always)
            .Should().NotBeNull();

        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var response = requester
                .Observe((object)new FaultingRequest(), o => o.WithTarget(VictimAddress), requestId)!
                .FirstAsync()
                .Await(ct);
            await captured.Should().Within(TestTimeouts.Convergence).Emit(
                "the victim's turn must have taken the request and detached its handler");

            victim.Post(new ParkTurn(), o => o.WithTarget(ChildAddress));
            await parked.Should().Within(TestTimeouts.Convergence).Emit("the child's block must be parked");

            victim.Dispose();
            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Where(_ => victim.RunLevel == MessageHubRunLevel.DisposeHostedHubs)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            Mesh.RunLevel.Should().Be(MessageHubRunLevel.Started, "this is a TARGETED teardown — the parent is live");
            Output.WriteLine($"[fence] victim={victim.RunLevel} parent={Mesh.RunLevel} — the handler runs NOW");

            release.OnNext(Unit.Default);
            release.OnCompleted();

            var failure = await Assert.ThrowsAsync<DeliveryFailureException>(
                () => response.WaitAsync(TestTimeouts.Convergence, ct));
            Output.WriteLine($"NACK: errorType={failure.Failure!.ErrorType} message={failure.Failure.Message}");
            Output.WriteLine($"trail: {Mesh.DescribeRequestFate(requestId)}");

            failure.Failure.ErrorType.Should().Be(ErrorType.Unknown,
                "a handler that threw on an OPEN scope is a genuine fault, not a teardown fact — "
                + "the requester must not be told to retry");
            failure.Failure.Message.Should().Contain(nameof(InvalidOperationException),
                "the exception text is the requester's only diagnosis");
            var trail = Mesh.DescribeRequestFate(requestId);
            trail.Should().Contain("HANDLER_FAULT TargetInvocationException→InvalidOperationException",
                "the fate stage names the inner type, not only the reflective wrapper");
            trail.Should().Contain("FAILURE_REPORTED errorType=Unknown runLevel=DisposeHostedHubs",
                "ReportFailure no longer declines on its own run level");
            trail.Should().Contain("REPLY_FORWARDED_THROUGH_PARENT",
                "the post seam carried the NACK through the live parent");
        }
        finally
        {
            Volatile.Write(ref releaseChild, 1);
            release.OnNext(Unit.Default);
            release.OnCompleted();
        }
    }

    /// <summary>
    /// Loss (2): the whole subtree is torn down — parent, victim and requester together — so the
    /// parent is past <c>DisposeHostedHubs</c> when the victim's handler faults, and the requester
    /// is <c>Quiescing</c> on this very callback. The NACK is handed to the requester's hub
    /// IN-PROCESS; before the fix the requester heard nothing until its own quiesce budget cut the
    /// callback (<see cref="HubDisposedBeforeResponseException"/>, not a <see cref="DeliveryFailure"/>).
    /// </summary>
    [MeshTheory(TimeoutSeconds = 1)]
    [MeshInlineData(Fault.HubDisposing, ErrorType.ShuttingDown)]
    // An ObjectDisposedException naming SOME disposed scope while THIS hub's own scope is live is a
    // genuine fault: the classifier is probe-gated (ScopeTeardown), so it does not become a retry.
    [MeshInlineData(Fault.DisposedContainer, ErrorType.Unknown)]
    [MeshInlineData(Fault.Genuine, ErrorType.Unknown)]
    public async Task AFaultUnderAWholeTreeTeardown_IsNackedToTheQuiescingRequesterInProcess(Fault fault, ErrorType expected)
    {
        var ct = CancellationToken.None;
        var release = new AsyncSubject<Unit>();
        var captured = new AsyncSubject<Unit>();
        var parked = new AsyncSubject<Unit>();
        var releaseChild = 0;

        var parent = Mesh.GetHostedHub(ParentAddress, c => c.WithPostingIdentity(PostingIdentity.System), HostedHubCreation.Always)!;
        // A quiesce budget far beyond the test's own bound: if the requester is answered by its
        // budget expiring rather than by the NACK, the assertion below fails on TYPE (a cut-off is
        // HubDisposedBeforeResponseException) and on TIME.
        var requester = parent.GetHostedHub(RequesterAddress, c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(FaultingRequest), typeof(FaultResponse))
            .WithQuiesceTimeout(TimeSpan.FromMinutes(5)), HostedHubCreation.Always)!;
        var victim = parent.GetHostedHub(VictimAddress, c => Victim(c, fault, release, captured), HostedHubCreation.Always)!;
        victim.GetHostedHub(ChildAddress,
                c => ParkedChild(c, parked, () => Volatile.Read(ref releaseChild) == 1), HostedHubCreation.Always)
            .Should().NotBeNull();

        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var response = requester
                .Observe((object)new FaultingRequest(), o => o.WithTarget(VictimAddress), requestId)!
                .FirstAsync()
                .Await(ct);
            await captured.Should().Within(TestTimeouts.Convergence).Emit(
                "the victim's turn must have taken the request and detached its handler");

            victim.Post(new ParkTurn(), o => o.WithTarget(ChildAddress));
            await parked.Should().Within(TestTimeouts.Convergence).Emit("the child's block must be parked");

            parent.Dispose();
            // 🚨 The fence IS the precondition of both former declines: parent past
            // DisposeHostedHubs (NackThroughParent declines, the post seam cannot forward), victim
            // at DisposeHostedHubs (its own pump is closed), requester Quiescing (it admits
            // replies and is waiting for this one).
            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Where(_ => parent.RunLevel >= MessageHubRunLevel.DisposeHostedHubs
                            && victim.RunLevel == MessageHubRunLevel.DisposeHostedHubs
                            && requester.RunLevel == MessageHubRunLevel.Quiescing)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            Output.WriteLine(
                $"[fence] parent={parent.RunLevel} victim={victim.RunLevel} requester={requester.RunLevel} — the handler runs NOW");

            var released = DateTime.UtcNow;
            release.OnNext(Unit.Default);
            release.OnCompleted();

            var failure = await Assert.ThrowsAsync<DeliveryFailureException>(
                () => response.WaitAsync(TestTimeouts.Convergence, ct));
            var answeredAfter = DateTime.UtcNow - released;
            Output.WriteLine($"NACK after {answeredAfter.TotalMilliseconds:F0}ms: errorType={failure.Failure!.ErrorType} message={failure.Failure.Message}");
            var trail = Mesh.DescribeRequestFate(requestId);
            Output.WriteLine($"trail: {trail}");

            failure.Failure.ErrorType.Should().Be(expected);
            if (expected == ErrorType.ShuttingDown)
                ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, VictimAddress).Should().BeTrue(
                    "a teardown fact is answered in the OWNER's refusal vocabulary, so the caller can "
                    + "tell 'ask the fresh activation' from 'the routing layer lost me'");
            if (expected == ErrorType.ShuttingDown)
                trail.Should().Contain("NACK_DECLINED reason=parent-",
                    "a teardown fact takes the parent route first, and that decline is now on the trail");
            trail.Should().Contain("NACK_DELIVERED_IN_PROCESS",
                "the requester's hub took the NACK directly, under the same root");
            trail.Should().NotContain("REPLY_REFUSED_SHUTTING_DOWN",
                "nothing was refused: the answer reached its requester");

            // And the requester's teardown proceeds on the ANSWER, not on its budget: it leaves
            // Quiescing promptly once the callback it was waiting for is resolved.
            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Where(_ => requester.RunLevel > MessageHubRunLevel.Quiescing)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            Output.WriteLine($"[requester] {requester.RunLevel} — its quiesce ended on the answer, not on its 5-minute budget");
        }
        finally
        {
            Volatile.Write(ref releaseChild, 1);
            release.OnNext(Unit.Default);
            release.OnCompleted();
        }
    }
}
