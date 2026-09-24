using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>A lease and a reclaim must be ONE atomic decision — Systemorph/MeshWeaver#5087.</b>
///
/// <para><c>Workspace.ReclaimIfUnheld</c> disposes an evicted mirror once its last declared holder
/// has left. It used to READ the lease count, then remove the lease entry unconditionally and
/// dispose. A writer that resolved the mirror BEFORE the eviction could take its lease inside that
/// window: the count went 0 → 1 after the reclaim had read 0, the writer's post-lease
/// <c>IsUsable</c> re-check still passed (nothing was disposed yet), and then the reclaim dropped
/// the writer's lease entry and disposed the mirror under it. The writer's base read then completed
/// EMPTY — <c>RequireBaseState</c>'s <i>"Update aborted: this hub's mirror ended without ever
/// carrying the node's state"</i>, the terminal #5087 was filed for.</para>
///
/// <para>The window is a few instructions wide, so it is driven deterministically: the
/// <c>ReclaimClaimed</c> seam runs at the point the reclaim has decided "unheld", and the second
/// writer's lease is taken right there, on the same thread. The invariant is the one the writer
/// relies on: <b>a lease that is GRANTED is a lease on a mirror that stays live until the lease is
/// released.</b> A lease that loses the race is REFUSED, so <c>AcquireRemoteStreamUnchecked</c>
/// resolves again and builds a fresh mirror.</para>
/// </summary>
public class ReclaimLeaseAtomicityTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Long enough that no heartbeat or change-feed resubscribe fires during the test.</summary>
    private static readonly TimeSpan LongHeartbeat = TimeSpan.FromMinutes(5);

    /// <summary>The owner path the change event names — what <c>EvictForPath</c> compares against.</summary>
    private const string OwnerPath = HostType + "/1";

    // Instance field, never static — no cross-test bleed.
    private int _subscribeCount;

    /// <summary>Minimal in-process <see cref="IMeshChangeFeed"/> — a plain Rx Subject.</summary>
    private sealed class TestMeshChangeFeed : IMeshChangeFeed, IDisposable
    {
        private readonly Subject<MeshChangeEvent> _subject = new();

        public void Publish(MeshChangeEvent change) => _subject.OnNext(change);

        public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
            => filter is null
                ? _subject.Subscribe(handler)
                : _subject.Subscribe(e => { if (e.Kind == filter.Value) handler(e); });

        public void Dispose() => _subject.Dispose();
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithHandler<SubscribeRequest>((_, delivery) =>
            {
                Interlocked.Increment(ref _subscribeCount);
                return delivery;
            })
            .AddData(data => data.AddSource(src => src
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))
                .WithType<LineOfBusiness>(t => t.WithInitialData(TestData.LinesOfBusiness))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services => services
                .AddSingleton<IMeshChangeFeed, TestMeshChangeFeed>()
                .Configure<SyncStreamOptions>(o =>
                {
                    o.HeartbeatInterval = LongHeartbeat;
                    o.ChangeFeedResubscribeWindow = LongHeartbeat;
                    o.ChangeFeedStalenessGrace = LongHeartbeat;
                }))
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>().WithType<LineOfBusiness>()));

    /// <summary>
    /// 🚨 <b>THE RACE.</b> Writer A holds the only lease on a mirror; a change event evicts it
    /// (parked, because A holds it). Writer B resolved the same mirror before the eviction and
    /// takes its lease at the instant A's release has decided "unheld". B's lease must be refused
    /// — never granted on a mirror that is then disposed under it.
    /// </summary>
    [HubFact]
    public async Task ALeaseTakenWhileTheReclaimDecides_IsRefused_NeverGrantedOnADyingMirror()
    {
        var (workspace, changeFeed) = await StartAndSettleAsync();
        var ws = (Workspace)workspace;
        var baselineSubscribes = Volatile.Read(ref _subscribeCount);

        var (mirror, leaseA) = ws.AcquireRemoteStreamUnchecked<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
        ReferenceEquals(leaseA, Disposable.Empty).Should().BeFalse(
            "writer A must hold a REAL lease, or this test drives no reclaim at all");
        await AwaitSubscribesAsync(baselineSubscribes + 1, "writer A's mirror must reach the owner");

        PublishOwnerChange(changeFeed);
        StreamLiveness.IsUsable(mirror).Should().BeTrue(
            "an evicted mirror that writer A still holds is PARKED, not disposed");

        IDisposable? leaseB = null;
        var decided = 0;
        ws.ReclaimClaimed = stream =>
        {
            if (!ReferenceEquals(stream, mirror) || Interlocked.Exchange(ref decided, 1) != 0)
                return;
            // Writer B resolved `mirror` before the eviction; it takes its hold NOW.
            leaseB = ws.TryLeaseResolved(mirror);
        };
        try
        {
            leaseA.Dispose();
        }
        finally
        {
            ws.ReclaimClaimed = null;
        }

        Volatile.Read(ref decided).Should().Be(1,
            "A's release of the last lease on an evicted mirror must reach the reclaim decision");

        var mirrorUsable = StreamLiveness.IsUsable(mirror);
        Output.WriteLine($"DIAG leaseBGranted={leaseB is not null} mirrorUsable={mirrorUsable}");

        (leaseB is null || mirrorUsable).Should().BeTrue(
            "a GRANTED lease is a promise that the mirror stays live until it is released — "
            + "granting B's lease and then disposing the mirror under it is exactly how B's base "
            + "read completes empty: \"Update aborted: this hub's mirror ended without ever carrying "
            + "the node's state\" (#5087)");
        leaseB?.Dispose();

        // And the refusal costs B nothing: a fresh acquire builds a new, live mirror.
        var (fresh, freshLease) = ws.AcquireRemoteStreamUnchecked<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
        try
        {
            ReferenceEquals(fresh, mirror).Should().BeFalse(
                "the reclaimed mirror is out of the cache, so the retry resolves a new one");
            StreamLiveness.IsUsable(fresh).Should().BeTrue("the retry's mirror is live");
        }
        finally
        {
            freshLease.Dispose();
        }
    }

    /// <summary>
    /// 🚨 <b>THE CONTROL — the refusal is not overbroad.</b> A lease taken BEFORE A releases keeps
    /// the evicted mirror alive through A's release, and the mirror is reclaimed only when B, the
    /// last holder, releases too.
    /// </summary>
    [HubFact]
    public async Task ALeaseTakenBeforeTheLastRelease_KeepsTheMirror_UntilItIsReleased()
    {
        var (workspace, changeFeed) = await StartAndSettleAsync();
        var ws = (Workspace)workspace;
        var baselineSubscribes = Volatile.Read(ref _subscribeCount);

        var (mirror, leaseA) = ws.AcquireRemoteStreamUnchecked<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
        await AwaitSubscribesAsync(baselineSubscribes + 1, "writer A's mirror must reach the owner");

        var leaseB = ws.TryLeaseResolved(mirror);
        leaseB.Should().NotBeNull("a live mirror grants a lease");

        PublishOwnerChange(changeFeed);
        leaseA.Dispose();
        StreamLiveness.IsUsable(mirror).Should().BeTrue(
            "B still holds the evicted mirror, so A's release must not reclaim it");

        leaseB!.Dispose();
        StreamLiveness.IsUsable(mirror).Should().BeFalse(
            "the last holder left an evicted mirror, so it is reclaimed at once");
    }

    /// <summary>
    /// 🚨 <b>THE HANDOVER.</b> Between the reclaim's claim and its taking the parking entry, the
    /// mesh-node cache's idle release DETACHES the stream (taking ownership and dropping the lease
    /// bookkeeping) and, having lost its own race to a re-attached consumer, PARKS it again. The
    /// re-parked stream is owned afresh; the reclaim whose claim was superseded must not take that
    /// new parking entry and dispose a stream a consumer has just re-attached to.
    /// </summary>
    [HubFact]
    public async Task ADetachAndRePark_InsideTheClaim_SupersedesTheReclaim()
    {
        var (workspace, changeFeed) = await StartAndSettleAsync();
        var ws = (Workspace)workspace;
        var baselineSubscribes = Volatile.Read(ref _subscribeCount);
        var reference = new CollectionReference(nameof(BusinessUnit));

        var (mirror, leaseA) = ws.AcquireRemoteStreamUnchecked<InstanceCollection, CollectionReference>(
            CreateHostAddress(), reference);
        await AwaitSubscribesAsync(baselineSubscribes + 1, "writer A's mirror must reach the owner");
        PublishOwnerChange(changeFeed);

        var reparked = 0;
        ws.ReclaimClaimed = stream =>
        {
            if (!ReferenceEquals(stream, mirror) || Interlocked.Exchange(ref reparked, 1) != 0)
                return;
            // The idle release: detach (ownership moves out, lease bookkeeping dropped) ...
            var detached = ws.DetachRemoteStreams(CreateHostAddress(), reference);
            detached.Any(s => ReferenceEquals(s, mirror)).Should().BeTrue("the parked mirror is what the idle release detaches");
            // ... then loses its zero-subscriber re-check and hands the stream back.
            ws.ParkRemoteStreams(detached);
        };
        try
        {
            leaseA.Dispose();
        }
        finally
        {
            ws.ReclaimClaimed = null;
        }

        Volatile.Read(ref reparked).Should().Be(1,
            "A's release must reach the claim, or this test drove no handover at all");
        StreamLiveness.IsUsable(mirror).Should().BeTrue(
            "the stream was detached and RE-PARKED after the reclaim's claim — that is a new "
            + "ownership, and a superseded claim must not dispose it under the consumer that "
            + "re-attached");
    }

    /// <summary>Activates both hubs and waits for the owner's initial snapshot.</summary>
    private async Task<(IWorkspace Workspace, IMeshChangeFeed ChangeFeed)> StartAndSettleAsync()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var changeFeed = client.ServiceProvider.GetRequiredService<IMeshChangeFeed>();

        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(10.Seconds())
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot",
                cancellationToken: TestContext.Current.CancellationToken);

        return (workspace, changeFeed);
    }

    /// <summary>Waits until the owner has counted <paramref name="target"/> SubscribeRequests.</summary>
    private Task AwaitSubscribesAsync(int target, string because) =>
        Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref _subscribeCount))
            .Should().Within(10.Seconds())
            .Match(c => c >= target, because, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Fires one owner-path change event that EVICTS: the version-less <c>Updated</c> the
    /// operator recycle broadcasts.</summary>
    private static void PublishOwnerChange(IMeshChangeFeed changeFeed) =>
        changeFeed.Publish(new MeshChangeEvent(
            Namespace: HostType,
            Id: "1",
            Path: OwnerPath,
            Kind: MeshChangeKind.Updated,
            NodeType: null,
            Version: 0,
            Timestamp: DateTimeOffset.UtcNow));
}
