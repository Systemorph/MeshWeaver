using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>A multicast chain's connection belongs to its owner, and the owner's disposal releases it —
/// including a connect that is still QUEUED.</b>
///
/// <para><b>The live defect.</b> <c>Defer(…).SubscribeOn(TaskPoolScheduler).Replay(1).AutoConnect(1)</c>:
/// the first subscriber's <c>Connect()</c> only queues the upstream subscribe on the pool, and the
/// handle <c>Connect()</c> returns stays inside <c>AutoConnect</c>. Nothing in a teardown joins a
/// pool-queued Rx subscribe, so the item ran whenever the pool reached it — after the owner's scope
/// had closed, resolving services from a disposed container. Measured on MeshWeaver.Plugins run
/// 34222933802 (2026-09-08): 11 disposed-scope stragglers across three suites, all this shape
/// (core #3737 fixed the one site; <c>OwnedConnectionExtensions</c> is the shape for every site).</para>
///
/// <para><b>The contract, each arm with its control.</b> A connect still queued when the owner is
/// released never runs (the control arm proves the connection WAS registered — a helper that
/// registered nothing would read zero before and after); a live upstream is unsubscribed with the
/// owner; a subscriber arriving after the release is refused, naming the owner; a chain that
/// terminates drops its handle so the owner tracks live connections only; and a hub-owned
/// connection is released in the hub's ShutDown phase.</para>
///
/// <para><b>Negative control</b> (run once, by hand, when this test was written): replacing the
/// helper's body with a bare <c>source.AutoConnect(minObservers)</c> fails
/// <see cref="AConnectStillQueuedWhenTheOwnerIsReleased_NeverRuns"/> at the control arm
/// (<c>owner.Count</c> reads 0, not 1) and, with the control removed, at the assertion that the
/// factory never ran — the queued connect goes ahead against the disposed owner.</para>
/// </summary>
public class OwnedConnectionTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Fact(Timeout = 60_000)]
    public async Task AConnectStillQueuedWhenTheOwnerIsReleased_NeverRuns()
    {
        var scheduler = new TestScheduler();
        var owner = new CompositeDisposable();
        var factoryRuns = 0;

        // The #3737 shape, verbatim: the factory is the Defer that resolved a workspace from a
        // disposed scope, SubscribeOn is the pool hand-off, Replay(1) the shared buffer.
        var shared = Observable
            .Defer(() =>
            {
                factoryRuns++;
                return Observable.Return(42);
            })
            .SubscribeOn(scheduler)
            .Replay(1)
            .AutoConnectOwnedBy(owner, new ReleaseLane(), "test-owner");

        var received = shared.Materialize().Replay();
        using var subscription = received.Connect();

        factoryRuns.Should().Be(0, "precondition: SubscribeOn queued the connect; the pool has not run it");
        owner.Count.Should().Be(1,
            "CONTROL ARM: the first subscriber's Connect() must be registered with the owner the instant "
            + "it runs — a zero here means the helper registered nothing, and the assertion below would be "
            + "vacuous");

        owner.Dispose();
        scheduler.Start(); // the pool gets to the queued item — AFTER the owner is gone

        factoryRuns.Should().Be(0,
            "the queued connect must be CANCELLED by the owner's release, never run against a disposed "
            + "owner — this is the GetQueryRaw → GetWorkspace disposed-scope straggler");

        // The subscriber that queued the connect is told why nothing will come (#5135): the release
        // terminal is delivered off the disposing thread, so wait for it rather than read at once.
        var terminal = await received.Should().Within(TestTimeouts.Convergence).Emit(
            "the subscriber that was attached when the owner released must receive a terminal, never silence",
            TestContext.Current.CancellationToken);
        terminal.Kind.Should().Be(NotificationKind.OnError,
            "a cancelled connect feeds no VALUE — only the release terminal naming the owner");
        terminal.Exception.Should().BeOfType<ObjectDisposedException>()
            .Which.ObjectName.Should().Be("test-owner");
    }

    /// <summary>
    /// 🚨 #5135: a subscriber ATTACHED while the one-shot is in flight is terminated by the owner's
    /// release. Disposing the connection unsubscribes the replay subject from its upstream and emits
    /// nothing to that subject's observers, so before the fix this subscriber waited forever — the
    /// MCP upload parked on <c>ContentService.GetCollection</c> when the owning hub tore down.
    /// <para><b>Negative control</b> (run by hand): without the <c>TakeUntil(released)</c> in
    /// <c>AutoConnectOwnedBy</c> the <c>Emit</c> below fails on its timeout.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ASubscriberAttachedWhileInFlight_IsTerminatedByTheRelease()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, new ReleaseLane(), "test-owner");

        var waiting = shared.Materialize().Replay(1);
        using var connection = waiting.Connect();
        upstream.HasObservers.Should().BeTrue("CONTROL ARM: the in-flight one-shot is connected");

        owner.Dispose();

        var terminal = await waiting.Should().Within(TestTimeouts.Convergence).Emit(
            "the subscriber attached before the release must receive a terminal, never silence",
            TestContext.Current.CancellationToken);
        terminal.Kind.Should().Be(NotificationKind.OnError);
        terminal.Exception.Should().BeOfType<ObjectDisposedException>()
            .Which.ObjectName.Should().Be("test-owner");
        upstream.HasObservers.Should().BeFalse("the release still unsubscribes the upstream");
    }

    /// <summary>
    /// 🚨 ONE sweep releasing TWO connections a consumer composes must not deliver their terminals
    /// concurrently. Measured in MeshWeaver.Plugins CI (set 3.0.0-ci.9296,
    /// <c>LayoutAreaIdentityTest.AuthorizedUser_CanSubscribe_ToLayoutArea</c>): the test body passed,
    /// the mesh disposed cleanly, and the service provider's disposal then hung for good — a stack
    /// capture showed ThreadPool threads, each delivering one query connection's release, parked on
    /// each other's Rx gates inside the permission fold (a <c>Zip</c> nested under a
    /// <c>CombineLatest</c>), and the disposing thread parked behind them.
    ///
    /// <para>The consumer here is that shape at its smallest: <c>a.CombineLatest(b.Zip(…))</c>. An
    /// error from <c>a</c> enters the CombineLatest gate and, still holding it, disposes its sources —
    /// the Zip's disposal takes the Zip gate. An error from <c>b</c> enters the Zip gate and, still
    /// holding it, forwards into the CombineLatest — which takes the CombineLatest gate. The hooks
    /// below FORCE that interleaving (A holds its gate until B is blocked on it), so if the two
    /// releases ever run at the same time the lock-order inversion is certain rather than a race; if
    /// they run one after the other, the first tears the consumer down and the second finds nothing
    /// left.</para>
    ///
    /// <para><b>Negative control</b> (run by hand): <c>Release</c> scheduling its own
    /// <c>TaskPoolScheduler</c> work item per connection (the #5135 shape) — the consumer still
    /// receives its terminal, and its teardown never finishes: the <c>sourcesReleased</c> wait fails on
    /// its timeout.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ReleasingTwoConnectionsAConsumerComposes_NeverDeliversTheirTerminalsConcurrently()
    {
        var lane = new ReleaseLane();
        var registry = new CompositeDisposable();
        var ownerA = new CompositeDisposable();
        var ownerB = new CompositeDisposable();
        registry.Add(ownerA);
        registry.Add(ownerB);

        var upstreamA = new Subject<int>();
        var upstreamB = new Subject<int>();
        var a = upstreamA.Replay(1).AutoConnectOwnedBy(ownerA, lane, "owner-a");
        var b = upstreamB.Replay(1).AutoConnectOwnedBy(ownerB, lane, "owner-b");

        // The two releases MEET, bounded: each waits, holding its first gate, until the other holds
        // its own. Volatile ints and a bounded SpinUntil, never a gate primitive. Once they have met
        // the deadlock no longer depends on timing — A is holding the CombineLatest gate when it goes
        // for the Zip gate, and B is holding the Zip gate when it goes for the CombineLatest gate.
        var aHoldsCombineLatestGate = 0;
        var bHoldsZipGate = 0;
        var bMetAWhileItHeldItsGate = -1; // -1 = A's hook never ran, 0 = B never came, 1 = they met
        var meetBudget = TimeSpan.FromSeconds(2);

        // B's error reaches the CombineLatest through a Subject it subscribed to directly, so no sink
        // A's disposal could silence stands between them — as in the fold, where B's error had already
        // passed every intermediate sink when it reached the outer gate.
        var bridge = new Subject<int>();
        var zipped = b.Zip(Observable.Never<int>(), (x, _) => x);
        // The first source carries `a` AND the Zip subscription, so disposing it — which the
        // CombineLatest does under its gate on A's error — disposes the Zip, taking the Zip gate.
        // Finally runs once disposing the source has RETURNED — i.e. once the Zip's disposal got its
        // gate. That, not the error notification (which the consumer receives before the disposal
        // starts), is what a deadlock withholds: in the CI hang the terminal was delivered and the
        // teardown behind it never finished.
        var sourcesReleased = new AsyncSubject<Unit>();
        var first = Observable.Create<int>(observer => new CompositeDisposable(
                a.Subscribe(observer),
                zipped.Subscribe(
                    _ => { },
                    ex =>
                    {
                        // Inside Zip's error forwarding: the Zip gate is held.
                        Volatile.Write(ref bHoldsZipGate, 1);
                        SpinWait.SpinUntil(() => Volatile.Read(ref aHoldsCombineLatestGate) == 1, meetBudget);
                        bridge.OnError(ex); // enters the CombineLatest gate
                    })))
            .Finally(() =>
            {
                sourcesReleased.OnNext(Unit.Default);
                sourcesReleased.OnCompleted();
            });

        var received = first
            .CombineLatest(bridge, (x, _) => x)
            .Materialize()
            // Inside CombineLatest's error forwarding — the CombineLatest gate is held — and BEFORE
            // it disposes its sources (the Zip among them).
            .Do(n =>
            {
                if (n.Kind != NotificationKind.OnError)
                    return;
                Volatile.Write(ref aHoldsCombineLatestGate, 1);
                // An OBSERVATION window, not a synchronisation: on a shared lane B cannot start while
                // A runs, so this runs out and records that B never overlapped A's hold — asserted
                // below. With one work item per release B arrives inside it and the two deadlock.
                var met = SpinWait.SpinUntil(() => Volatile.Read(ref bHoldsZipGate) == 1, meetBudget);
                Volatile.Write(ref bMetAWhileItHeldItsGate, met ? 1 : 0);
            })
            .Replay(1);
        using var subscription = received.Connect();
        (upstreamA.HasObservers && upstreamB.HasObservers).Should().BeTrue(
            "CONTROL ARM: the consumer is connected to both owned connections");

        registry.Dispose(); // ONE sweep releases both owners

        var terminal = await received.Should().Within(TestTimeouts.Convergence).Emit(
            "the consumer composing both released connections must receive a terminal",
            TestContext.Current.CancellationToken);
        terminal.Kind.Should().Be(NotificationKind.OnError);
        terminal.Exception.Should().BeOfType<ObjectDisposedException>();

        await sourcesReleased.Should().Within(TestTimeouts.Convergence).Emit(
            "the release that terminated the consumer must also finish tearing it down — two releases "
            + "delivered at once leave it parked on the Zip gate the other release holds, while that one "
            + "is parked on the CombineLatest gate",
            TestContext.Current.CancellationToken);
        Volatile.Read(ref bMetAWhileItHeldItsGate).Should().Be(0,
            "the releases of one sweep are delivered ONE AT A TIME: B's release must not have entered "
            + "the consumer while A's held the CombineLatest gate (and A's hook must have run at all)");
    }

    [Fact]
    public void ASettledPromise_KeepsReplaying_AfterItsHandleLeavesALiveOwner()
    {
        // The release signal must fire on OWNER disposal only: a terminated chain removes its handle
        // from an owner that is still alive, and that removal must not fault later subscribers.
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, new ReleaseLane(), "test-owner");
        using (shared.Subscribe(_ => { }))
        {
            upstream.OnNext(5);
            upstream.OnCompleted();
        }

        var late = new List<Notification<int>>();
        shared.Materialize().Subscribe(late.Add);
        late.Should().HaveCount(2);
        late[0].Value.Should().Be(5);
        late[1].Kind.Should().Be(NotificationKind.OnCompleted,
            "dropping a settled chain's handle is not a release — the promise replays normally");
    }

    [Fact]
    public void ALiveConnection_IsReleasedWithItsOwner()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, new ReleaseLane(), "test-owner");

        // The error arm is NOT optional: the release terminates this attached subscriber on the lane
        // (ASubscriberAttachedWhileInFlight_IsTerminatedByTheRelease), and an unobserved OnError on a
        // pool thread is an unhandled exception that takes the test host down.
        using var subscription = shared.Subscribe(_ => { }, _ => { });
        upstream.HasObservers.Should().BeTrue("CONTROL ARM: the first subscriber connected the upstream");
        owner.Count.Should().Be(1, "CONTROL ARM: the connection is registered");

        owner.Dispose();

        upstream.HasObservers.Should().BeFalse(
            "the owner's disposal unsubscribes the upstream — the connection is no longer rooted by the chain");
    }

    [Fact(Timeout = 60_000)]
    public async Task ASubscriptionAfterTheRelease_IsRefusedNamingTheOwner_OnTheLane()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, new ReleaseLane(), "test-owner");

        using (shared.Subscribe(_ => { }))
            upstream.OnNext(7);
        owner.Dispose();

        // The refusal is delivered on the release lane, never on the subscribing thread (see
        // ARefusalAndAReleaseMeetingInOneConsumer_NeverDeadlock for why), so it is awaited — and the
        // thread it arrived on is recorded against this one.
        var subscribingThread = Environment.CurrentManagedThreadId;
        var deliveredOnThread = -1;
        var terminal = await shared.Materialize()
            .Do(_ => Volatile.Write(ref deliveredOnThread, Environment.CurrentManagedThreadId))
            .Should().Within(TestTimeouts.Convergence).Emit(
                "a subscriber arriving after the release must be TOLD — never parked",
                TestContext.Current.CancellationToken);

        terminal.Kind.Should().Be(NotificationKind.OnError,
            "a subscriber arriving after the release must TERMINATE — a bare Replay(1) would hand it the "
            + "buffered 7 and then go silent forever, the burst-then-silence hang");
        terminal.Exception.Should().BeOfType<ObjectDisposedException>()
            .Which.ObjectName.Should().Be("test-owner", "the refusal names the owner so the straggler is attributable");
        Volatile.Read(ref deliveredOnThread).Should().NotBe(subscribingThread,
            "a refusal is a release terminal and goes on the lane — thrown on the subscriber's thread it "
            + "races the lane's releases into whatever gates the consumer composes");
        upstream.HasObservers.Should().BeFalse("a refused subscription opens no upstream");
    }

    /// <summary>
    /// 🚨 <b>A refusal and a release reaching one consumer are delivered ONE AT A TIME</b> — the
    /// deadlock that left a Layout render leaf running after its mesh's teardown
    /// (MeshWeaver.Plugins scheduled run 36090164894, <c>SendDocumentIdentityPanelTest</c>: <c>teardown
    /// DIRTY … [Layout=1 [Defer&lt;EntityStoreAndUpdates&gt;]]</c>).
    ///
    /// <para><b>The shape, as captured</b> (<c>dotnet-stack</c> on the reproduced hang, Linux, 2 CPUs):
    /// the permission fold is a <c>SelectMany</c> whose inners are <c>Zip</c>s of cache queries. The
    /// RELEASE LANE was delivering one query's release: it held the first <c>Zip</c>'s gate and was
    /// entering the <c>SelectMany</c> gate. The RENDER thread was subscribing the next inner, whose
    /// query was already released and so REFUSED — synchronously, on the render thread: it held the
    /// <c>SelectMany</c> gate forwarding that error and was disposing the first <c>Zip</c>, which takes
    /// the first <c>Zip</c>'s gate. #5660 serialised release against release; the refusal was the
    /// same terminal delivered off the lane.</para>
    ///
    /// <para><b>Made deterministic.</b> The lane is parked INSIDE the first Zip's gate (a <c>Do</c> on
    /// its error arm) until the subscriber either holds the SelectMany gate forwarding an error on its
    /// own thread — the old shape — or has finished subscribing the second inner without being told
    /// anything on its own thread, which is the fix. In the old shape the subscriber then waits, still
    /// inside the SelectMany gate, until the lane has left the park and is BLOCKED on that gate (its
    /// thread reads <see cref="ThreadState.WaitSleepJoin"/>) — only then does it go on to dispose the
    /// first Zip. That order is load-bearing: a disposal that ran first would detach the <c>Do</c>'s
    /// observer before the lane forwarded through it, and the lane's error would be dropped instead of
    /// reaching the gate — the incident's lane had already passed every intermediate sink. So nothing
    /// here depends on timing: both outcomes are reached by construction.</para>
    ///
    /// <para><b>Negative control</b> (run by hand when this test was written): restoring
    /// <c>Observable.Throw</c> for the refusal in <c>AutoConnectOwnedBy</c> makes the subscriber
    /// thread never return — the assertion on <c>subscriberReturned</c> fails and the two threads
    /// stay deadlocked for the life of the process, on the very frames captured from the CI hang.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARefusalAndAReleaseMeetingInOneConsumer_NeverDeadlock()
    {
        var lane = new ReleaseLane();
        var owner = new CompositeDisposable();
        var upstreamA = new Subject<int>();
        var upstreamB = new Subject<int>();
        var a = upstreamA.Replay(1).AutoConnectOwnedBy(owner, lane, "owner");
        var b = upstreamB.Replay(1).AutoConnectOwnedBy(owner, lane, "owner");

        var laneHoldsFirstZipGate = 0;
        var laneLeftThePark = 0;
        Thread? laneThread = null;
        var subscriberHoldsSelectManyGate = 0;
        var subscriberReturned = 0;
        var subscriberThread = -1;
        var parkBudget = TestTimeouts.Convergence;

        var outer = new Subject<int>();
        var received = outer
            .SelectMany(i => a.Zip(b, (x, y) => x + y)
                // Inside the Zip's error forwarding — its gate is held — and BEFORE the SelectMany
                // gate. Only the FIRST inner parks, and only the lane reaches it (it is the release).
                .Do(_ => { }, _ =>
                {
                    if (i != 1)
                        return;
                    Volatile.Write(ref laneThread, Thread.CurrentThread);
                    Volatile.Write(ref laneHoldsFirstZipGate, 1);
                    try
                    {
                        SpinWait.SpinUntil(
                            () => Volatile.Read(ref subscriberHoldsSelectManyGate) == 1
                                  || Volatile.Read(ref subscriberReturned) == 1,
                            parkBudget);
                    }
                    finally
                    {
                        // From here the lane forwards straight on to the SelectMany gate.
                        Volatile.Write(ref laneLeftThePark, 1);
                    }
                }))
            .Materialize()
            // Inside the SelectMany's error forwarding — its gate is held — and BEFORE it disposes its
            // inners (the first Zip among them).
            .Do(n =>
            {
                if (n.Kind != NotificationKind.OnError
                    || Environment.CurrentManagedThreadId != Volatile.Read(ref subscriberThread))
                    return;
                Volatile.Write(ref subscriberHoldsSelectManyGate, 1);
                // Hold the SelectMany gate until the lane is blocked on it, then let the disposal of
                // the first Zip go ahead — the order the incident's two threads met in.
                SpinWait.SpinUntil(
                    () => Volatile.Read(ref laneLeftThePark) == 1
                          && Volatile.Read(ref laneThread) is { } lane
                          && (lane.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    parkBudget);
            })
            .Replay();
        // NOT `using`: disposed at the end of the GREEN path only. If the regression returns, the
        // two threads below are blocked on each other's Monitor for good — nothing can release a
        // lock-order deadlock from outside — and tearing the composition down would walk into those
        // same gates from the test thread, turning a named assertion failure into a hung teardown.
        // Both parks are bounded, so no callback of this test is left waiting on a flag; what is
        // left behind on failure is the deadlock itself, on background threads.
        var connection = received.Connect();

        outer.OnNext(1);
        upstreamA.OnNext(1);
        upstreamB.OnNext(2);
        var first = await received.Should().Within(TestTimeouts.Convergence).Emit(
            "CONTROL ARM: the first inner is live and zips both connections",
            TestContext.Current.CancellationToken);
        first.Value.Should().Be(3);

        owner.Dispose(); // the release of both connections is posted to the lane
        SpinWait.SpinUntil(() => Volatile.Read(ref laneHoldsFirstZipGate) == 1, parkBudget).Should().BeTrue(
            "CONTROL ARM: the lane is delivering the release and holds the first Zip's gate");

        // The render thread of the incident: subscribes the next inner while the lane holds that gate.
        var subscriber = new Thread(() =>
        {
            Volatile.Write(ref subscriberThread, Environment.CurrentManagedThreadId);
            try
            {
                outer.OnNext(2);
            }
            finally
            {
                Volatile.Write(ref subscriberReturned, 1);
            }
        })
        { IsBackground = true, Name = "render-subscribe" };
        subscriber.Start();

        SpinWait.SpinUntil(() => Volatile.Read(ref subscriberReturned) == 1, parkBudget).Should().BeTrue(
            "subscribing the next inner must RETURN — a refusal thrown on the subscribing thread takes the "
            + "SelectMany gate and waits for the Zip gate the lane holds, while the lane waits for the "
            + "SelectMany gate: a deadlock neither side leaves");
        Volatile.Read(ref subscriberHoldsSelectManyGate).Should().Be(0,
            "the refusal must not be delivered on the subscribing thread at all — it goes on the lane, "
            + "behind the releases posted before it");

        var terminal = await received.Where(n => n.Kind != NotificationKind.OnNext)
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the consumer must still be TERMINATED — by the lane, once",
                TestContext.Current.CancellationToken);
        terminal.Kind.Should().Be(NotificationKind.OnError);
        terminal.Exception.Should().BeOfType<ObjectDisposedException>();

        // The worker has provably returned (asserted above), so joining it cannot park the test.
        subscriber.Join();
        connection.Dispose();
    }

    /// <summary>
    /// The lane drains on POOLED work items. Taking <c>ObserveOn</c>'s long-running path parked one
    /// dedicated thread per lane in <c>Monitor.Wait</c> forever — the lane is never disposed, so that
    /// thread never exited (visible in every stack capture of a test host: one
    /// <c>ObserveOnObserverLongRunning.Drain</c> per torn-down mesh).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AReleaseRunsOnAPooledThread_NeverOnADedicatedOne()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, new ReleaseLane(), "test-owner");

        var onPool = new AsyncSubject<bool>();
        using var subscription = shared.Subscribe(_ => { }, _ =>
        {
            onPool.OnNext(Thread.CurrentThread.IsThreadPoolThread);
            onPool.OnCompleted();
        });
        owner.Dispose();

        var pooled = await onPool.Should().Within(TestTimeouts.Convergence).Emit(
            "the release must reach the attached subscriber", TestContext.Current.CancellationToken);
        pooled.Should().BeTrue(
            "a lane that drains on a dedicated long-running thread keeps that thread parked for the "
            + "lane's whole life — one leaked thread per mesh");
    }

    [Fact]
    public void ATerminatedChain_DropsItsHandleFromTheOwner_AndStillReplays()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, new ReleaseLane(), "test-owner");

        using var subscription = shared.Subscribe(_ => { });
        owner.Count.Should().Be(1, "CONTROL ARM: registered while live");

        upstream.OnNext(1);
        upstream.OnCompleted();

        owner.Count.Should().Be(0,
            "a terminated chain's connection holds nothing — it leaves the owner so a faulted-then-rebuilt "
            + "promise cannot accumulate one dead handle per attempt for the life of the process");

        var late = new List<Notification<int>>();
        shared.Materialize().Subscribe(late.Add);
        late.Should().HaveCount(2, "the owner is NOT disposed, so the settled promise still replays to a late subscriber");
        late[0].Value.Should().Be(1);
        late[1].Kind.Should().Be(NotificationKind.OnCompleted);
    }

    [Fact]
    public void AFaultedChain_DropsItsHandleFromTheOwner()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(owner, new ReleaseLane(), "test-owner");

        using var subscription = shared.Subscribe(_ => { }, _ => { });
        owner.Count.Should().Be(1, "CONTROL ARM: registered while live");

        upstream.OnError(new InvalidOperationException("upstream fault"));

        owner.Count.Should().Be(0, "a faulted chain's connection is released with its eviction");
    }

    [Fact]
    public void ConnectOwnedBy_ConnectsNow_AndReleasesWithTheOwner()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        var published = upstream.Publish();

        upstream.HasObservers.Should().BeFalse("precondition: nothing is connected before ConnectOwnedBy");
        published.ConnectOwnedBy(owner);
        upstream.HasObservers.Should().BeTrue("CONTROL ARM: ConnectOwnedBy connects eagerly — a feed fills without subscribers");
        owner.Count.Should().Be(1);

        owner.Dispose();
        upstream.HasObservers.Should().BeFalse("the owner's disposal ends the feed");
    }

    [Fact]
    public void AConnectionRegisteredOnADisposedOwner_IsReleasedOnTheSpot()
    {
        var upstream = new Subject<int>();
        var owner = new CompositeDisposable();
        owner.Dispose();

        // The sliver the Defer guard cannot see: a Connect() that ran between the guard's read and
        // the owner's disposal. CompositeDisposable's contract — Add on a disposed composite
        // disposes the item — is what closes it; ConnectOwnedBy exercises that path directly.
        upstream.Publish().ConnectOwnedBy(owner);

        upstream.HasObservers.Should().BeFalse(
            "a registration arriving after the owner's disposal is disposed as it is added — the "
            + "late-connect race resolved by construction");
    }

    [Fact(Timeout = 120_000)]
    public async Task AHubOwnedConnection_IsReleasedInTheHubsShutDown()
    {
        var host = GetHost();
        var hub = host.GetHostedHub(new Address("owned-connection", "1"), c => c);

        var upstream = new Subject<int>();
        var shared = upstream.Replay(1).AutoConnectOwnedBy(hub, "hosted-owner");
        using var subscription = shared.Subscribe(_ => { }, _ => { });
        upstream.HasObservers.Should().BeTrue("CONTROL ARM: connected on the live hub");

        hub.Dispose();
        await hub.DisposalCompleted.FirstOrDefaultAsync().Await(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

        upstream.HasObservers.Should().BeFalse(
            "RegisterForDisposal runs the release in the hub's ShutDown phase — strictly inside its "
            + "DisposalCompleted, before any scope closes");

        // Refused on the mesh's release lane, not on this thread — awaited, never read synchronously.
        var terminal = await shared.Materialize().Should().Within(TestTimeouts.Convergence).Emit(
            "a subscriber after the hub's teardown must be told", TestContext.Current.CancellationToken);
        terminal.Kind.Should().Be(NotificationKind.OnError, "a subscriber after the hub's teardown is refused");
        terminal.Exception.Should().BeOfType<ObjectDisposedException>().Which.ObjectName.Should().Be("hosted-owner");
    }
}
