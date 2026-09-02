using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the lifetime contract of <see cref="ActivityControlPlaneExtensions.SubscribeHubWatcher{T}"/>
/// — the primitive every hub-owned watcher (compile, sources, release-request, adopted-stamp,
/// control-plane, submission, build-claim) is installed through (Systemorph/MeshWeaver#3026).
///
/// <para><b>The defect.</b> A watcher's <see cref="IDisposable"/> is handed to
/// <c>hub.RegisterForDisposal</c>, which disposes it in the hub's ShutDown phase — the LAST phase,
/// after Quiescing has waited up to its whole budget for pending callbacks and after the hosted
/// subtree was joined. And a hub is part of a shutdown even earlier than its own <c>Dispose()</c>:
/// an ancestor's disposal freezes the subtree first and reaches the hub only in its
/// DisposeHostedHubs phase. Through that whole window the watcher kept reacting to emissions and
/// kept issuing cross-hub requests — which are precisely the callbacks Quiescing then waited 2 s
/// for and errored with <c>ObjectDisposedException</c>: the 22
/// <c>SourceIncludeUnavailableException</c> faults per <c>MeshWeaver.FutuRe.Test</c> shard, each
/// exactly one quiesce budget after <c>DISPOSE_INVOKED</c>.</para>
///
/// <para>Deterministic by construction: the hub's single-threaded action block is stalled by a
/// gated handler, so no disposal PHASE can run during the assertions — if the watcher has let go
/// of its source after <c>Dispose()</c> returned, the only thing that can have stopped it is the
/// hub's synchronous teardown signal.</para>
/// </summary>
public class HubWatcherStopsAtTeardownStartTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Blocker;

    /// <summary>
    /// A deliberately parked action block. Handler → test is the <see cref="Entered"/> subject the
    /// handler completes; test → parked handler is the volatile <see cref="Release"/> flag polled
    /// under a bounded <see cref="SpinWait.SpinUntil(Func{bool},TimeSpan)"/>, written in the test's
    /// <c>finally</c> so a failing assertion cannot strand the action block.
    /// </summary>
    private sealed class Stall
    {
        public readonly AsyncSubject<Unit> Entered = new();
        public int Release;

        public IMessageDelivery Handle(IMessageDelivery request)
        {
            Entered.OnNext(Unit.Default);
            Entered.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref Release) == 1, TestTimeouts.Convergence);
            return request.Processed();
        }
    }

    private IMessageHub CreateStallableRoot(Stall stall, string id) =>
        GetClient().ServiceProvider.CreateMessageHub(
            new Address("watcher-root", id),
            c => c
                .WithPostingIdentity(PostingIdentity.System)
                .WithTypes(typeof(Blocker))
                .WithHandler<Blocker>((_, request) => stall.Handle(request)));

    private static async Task ParkTheActionBlock(IMessageHub root, Stall stall)
    {
        root.Post(new Blocker(), o => o.WithTarget(root.Address));
        await stall.Entered.Should().Within(10.Seconds()).Emit("the blocker handler must be running");
    }

    [Fact]
    public async Task OwnDispose_StopsTheWatcher_BeforeAnyDisposalPhaseRuns()
    {
        var stall = new Stall();
        var hub = CreateStallableRoot(stall, "own");
        var source = new Subject<int>();
        var delivered = 0;
        using var watcher = ActivityControlPlaneExtensions.SubscribeHubWatcher<int>(
            hub, () => source, _ => delivered++, logger: null, "test watcher");
        try
        {
            await ParkTheActionBlock(hub, stall);

            source.HasObservers.Should().BeTrue("the watcher is live while its hub is");
            source.OnNext(1);
            delivered.Should().Be(1, "a live hub's watcher delivers");

            hub.Dispose();

            source.HasObservers.Should().BeFalse(
                "the watcher must let go of its source at the FIRST instant of teardown — Dispose() "
                + "has returned, no disposal phase has run (the action block is parked), and "
                + "RegisterForDisposal would only have reached it in the ShutDown phase, after the "
                + "Quiescing budget it would have kept feeding (#3026)");
            source.OnNext(2);
            delivered.Should().Be(1,
                "nothing may be delivered to a watcher whose hub has begun shutting down");
        }
        finally
        {
            Volatile.Write(ref stall.Release, 1);
        }
    }

    /// <summary>
    /// The FutuRe.Test shape exactly: the MESH is disposed, the per-NodeType hub is still live
    /// (its own DisposeRequest arrives only in the mesh's DisposeHostedHubs phase), and its sources
    /// watcher is still running — issuing the include reads the hub's own Quiescing will later wait
    /// 2 s for. The freeze that reaches the hub synchronously inside the ancestor's Dispose() is its
    /// first instant of teardown, and the watcher must stop there.
    /// </summary>
    [Fact]
    public async Task AncestorDispose_StopsAHostedHubsWatcher_WhileThatHubIsStillLive()
    {
        var stall = new Stall();
        var root = CreateStallableRoot(stall, "ancestor");
        var child = root.GetHostedHub(new Address("watcher-child", "1"));
        var source = new Subject<int>();
        var delivered = 0;
        using var watcher = ActivityControlPlaneExtensions.SubscribeHubWatcher<int>(
            child, () => source, _ => delivered++, logger: null, "test watcher");
        try
        {
            await ParkTheActionBlock(root, stall);
            source.HasObservers.Should().BeTrue("the watcher is live while its hub is");

            root.Dispose();

            child.IsDisposing.Should().BeFalse(
                "the child's own disposal has not begun — the root's DisposeHostedHubs phase cannot "
                + "run while its action block is parked");
            child.RunLevel.Should().Be(MessageHubRunLevel.Started, "the child is fully live");
            child.IsShuttingDown.Should().BeTrue("…but the ancestor's freeze has reached it");

            source.HasObservers.Should().BeFalse(
                "an ancestor's Dispose() is the hosted hub's first instant of teardown; a watcher that "
                + "waits for the hosted hub's OWN Dispose keeps issuing requests for the whole ancestor "
                + "quiesce window — the 2 s between DISPOSE_INVOKED and the [FAULT] in #3026");
            source.OnNext(1);
            delivered.Should().Be(0, "nothing may be delivered once the subtree is shutting down");
        }
        finally
        {
            Volatile.Write(ref stall.Release, 1);
        }
    }

    [Fact]
    public async Task InstallingOnAHubThatIsAlreadyShuttingDown_IsInert()
    {
        var stall = new Stall();
        var hub = CreateStallableRoot(stall, "late");
        var source = new Subject<int>();
        var factoryCalls = 0;
        try
        {
            await ParkTheActionBlock(hub, stall);
            hub.Dispose();

            using var watcher = ActivityControlPlaneExtensions.SubscribeHubWatcher<int>(
                hub, () => { factoryCalls++; return source; }, _ => { }, logger: null, "test watcher");

            factoryCalls.Should().Be(0,
                "a watcher installed on a hub that is already shutting down must not open a "
                + "subscription at all — there is nothing for it to observe and nobody to act for");
            source.HasObservers.Should().BeFalse("nothing was subscribed");
        }
        finally
        {
            Volatile.Write(ref stall.Release, 1);
        }
    }

    /// <summary>
    /// The raw-thread escape. A transient fault schedules a re-establish on a 1 s
    /// <c>Observable.Timer</c>; the re-establish evaluates the source FACTORY again — on the timer's
    /// thread, with nothing above it. A factory that throws synchronously there (a
    /// <c>GetMeshNodeStream()</c> on a hub whose scope is gone) used to throw straight out of the
    /// tick: an unhandled exception on a scheduler thread, i.e. the host kill. The factory must be
    /// evaluated under <c>Observable.Defer</c>, so its throw is a stream fault the classifier owns.
    /// Bounded, synchronous seam: the re-establish runs inline, so an escape would surface HERE, as
    /// a throw out of the install call.
    /// </summary>
    [Fact]
    public void AReEstablishWhoseFactoryThrows_IsClassifiedAsAFault_NotThrownOnTheScheduler()
    {
        var factoryCalls = 0;
        var scheduled = 0;
        IDisposable? watcher = null;

        Action install = () => watcher = ActivityControlPlaneExtensions.SubscribeWithReEstablish<int>(
            () =>
            {
                factoryCalls++;
                if (factoryCalls == 1)
                    return Observable.Throw<int>(new InvalidOperationException("transient hub hiccup"));
                throw new InvalidOperationException("the source factory itself threw");
            },
            _ => { },
            new Address("mesh", "1"),
            logger: null,
            faultLogContext: "test",
            scheduleReEstablish: reEstablish =>
            {
                if (scheduled++ < 2) reEstablish();
                return Disposable.Empty;
            });

        install.Should().NotThrow(
            "a synchronous factory throw on a re-establish runs on the schedule's thread — in "
            + "production a bare Observable.Timer tick — so it must reach the fault classifier as a "
            + "stream fault, never be thrown out of the re-establish");
        factoryCalls.Should().Be(3,
            "the first subscribe faulted transiently, and each of the two bounded re-establishes ran "
            + "the factory again and classified its throw as one more transient fault");
        watcher?.Dispose();
    }
}
