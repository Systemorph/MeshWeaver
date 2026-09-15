using System;
using System.Linq;
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
/// 🚨 <b>A change-feed eviction RETAINS an unleased remote stream for the process lifetime — one
/// permanently-live <c>sync/</c> hub pair per eviction.</b> Systemorph/MeshWeaver#3432.
///
/// <para><b>The mechanism, in three lines of <c>Workspace</c>.</b> <c>EvictForPath</c> runs on
/// EVERY change-feed event and parks every cached remote stream for that owner:</para>
/// <code>
/// _evictedRemoteStreams[removed.Value] = 0;
/// ReclaimIfUnheld(removed.Value);
/// </code>
/// <para>and <c>ReclaimIfUnheld</c> opens with</para>
/// <code>
/// if (!_remoteStreamLeases.TryGetValue(stream, out var leases) || leases != 0)
///     return;
/// </code>
/// <para>A stream that <b>nobody ever leased</b> has no entry in <c>_remoteStreamLeases</c>, so
/// <c>TryGetValue</c> is false and the method returns having disposed nothing. The stream stays in
/// <c>_evictedRemoteStreams</c> — a field of the singleton workspace — with its client
/// <c>sync/</c> hub and its owner-side twin both alive, until <c>Workspace.Dispose()</c>, i.e.
/// process exit. The next caller misses the cache and builds a fresh one, which the next change
/// event parks the same way. <b>The population therefore grows once per (change event × unleased
/// path), not once per path.</b></para>
///
/// <para><b>Why nothing else collects them.</b> The only other drain,
/// <c>Workspace.DetachRemoteStreams</c>, is called from exactly one site —
/// <c>MeshNodeStreamHandle.DetachUpstreams()</c> — and always with <c>new MeshNodeReference()</c>.
/// It matches parked streams on <c>Equals(parked.Reference, reference)</c>, so a parked stream
/// carrying any OTHER reference (a <c>CollectionReference</c> as here, or the
/// <c>LayoutAreaReference</c> that <c>LayoutExtensions.GetControlStream</c> opens for every
/// rendered layout area) is matched by no reaper at all.</para>
///
/// <para><b>The two arms.</b> Both drive the identical sequence — resolve a remote stream, fire one
/// owner-path change event, repeat — and both count the live <c>sync/</c> hub population directly,
/// which is #3432's own metric: <c>SynchronizationStream</c>'s constructor mints exactly one hosted
/// <c>sync/{ClientId}</c> hub per stream, so walking
/// <c>HostedHubsCollection.Hubs</c> for <c>Address.Type == SynchronizationAddress.AddressType</c>
/// IS the live stream count. The <c>SubscribeRequest</c>/<c>UnsubscribeRequest</c> tallies
/// corroborate it from the owner's side. The arms differ in ONE thing, the lease:</para>
/// <list type="bullet">
///   <item><b>Unleased</b> (<c>GetRemoteStream</c>, the public API behind
///   <c>LayoutExtensions.GetControlStream</c>, <c>MeshOperations</c> and the
///   <c>MeshDataSource</c> reduce callback) — every predecessor is retained.</item>
///   <item><b>Leased</b> (<c>AcquireRemoteStreamUnchecked</c> + lease disposed, the #1324 fix that
///   the mesh-node cache and the write path use) — every predecessor is reclaimed.</item>
/// </list>
/// <para>🚨 The leased arm is the POSITIVE CONTROL, and it is why the unleased arm's number means
/// something. An assertion that "streams are retained" measured through a counter that never moves
/// would pass on a build where nothing is ever unsubscribed. The leased arm drives the same
/// evictions through the same code and watches the count come back DOWN, so the instrument is
/// proven to be able to observe a release before the other arm reports the absence of one.</para>
///
/// <para>🚨 <b>This test PINS CURRENT BEHAVIOUR — the unleased arm asserts the DEFECT.</b> It is a
/// characterization test, written to establish the cause of #3432 before anything is changed. When
/// the retention is fixed (by opting the unleased call sites into the lease — see
/// <c>Doc/Architecture/EvictedStreamRetention</c> §9), the unleased arm WILL go red, and that red is
/// the fix landing, not a regression: invert it to match the leased arm rather than deleting it.</para>
///
/// <para>🚨 <b>Which change events still evict — issue #1174.</b> A versioned <c>Updated</c> — one
/// per write, i.e. the event this test USED to drive — no longer evicts: the workspace records the
/// announced version as the next write's freshness floor and KEEPS the mirror, so there is no
/// predecessor to park (see <see cref="AVersionedUpdate_KeepsTheUnleasedStream_SoNothingIsParked"/>).
/// The retention itself is untouched, and it still applies to every event that DOES evict — a
/// <c>Deleted</c>, a <c>Created</c> (recreate), and the version-less <c>Updated</c> the operator
/// recycle broadcasts. The two characterization arms therefore drive that last shape, which keeps
/// them measuring the mechanism rather than an event that no longer reaches it.</para>
/// </summary>
public class EvictedUnleasedStreamRetentionTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Number of eviction cycles driven. Small — the defect is monotone, so a handful of
    /// cycles separates "retains every predecessor" (grows with N) from "reclaims them" (flat).</summary>
    private const int Cycles = 5;

    /// <summary>Long enough that the heartbeat timer never fires during the test, so every
    /// <c>SubscribeRequest</c> counted is one this test caused.</summary>
    private static readonly TimeSpan LongHeartbeat = TimeSpan.FromMinutes(5);

    /// <summary>The owner path the change events name — <c>Address.ToString()</c> of
    /// <see cref="HubTestBase.CreateHostAddress"/>, which is what <c>EvictForPath</c> compares
    /// against (<c>key.Owner.ToString() == path</c>).</summary>
    private const string OwnerPath = HostType + "/1";

    // Instance fields, never static — no cross-test bleed (xUnit builds a fresh instance per test).
    private int _subscribeCount;
    private int _unsubscribeCount;

    /// <summary>
    /// Minimal in-process <see cref="IMeshChangeFeed"/> — a plain Rx Subject, matching the
    /// production <c>InProcessMeshChangeFeed</c> contract. <c>Workspace</c> resolves this by name
    /// through reflection and subscribes with a <c>null</c> filter, so EVERY kind reaches
    /// <c>EvictForPath</c>.
    /// </summary>
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
            // Passive counters: both return the delivery UNPROCESSED so the framework's own
            // handlers still run (create / dispose the owner-side sync hub). Registered before
            // AddData so they see the request first in the rule chain.
            .WithHandler<SubscribeRequest>((_, delivery) =>
            {
                Interlocked.Increment(ref _subscribeCount);
                return delivery;
            })
            .WithHandler<UnsubscribeRequest>((_, delivery) =>
            {
                Interlocked.Increment(ref _unsubscribeCount);
                return delivery;
            })
            // A REAL data source so subscribe → SubscribeAck + initial DataChangedEvent, which
            // opens the client's data-context init gate exactly as in production.
            .AddData(data => data.AddSource(src => src
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))
                .WithType<LineOfBusiness>(t => t.WithInitialData(TestData.LinesOfBusiness))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services => services
                // The workspace resolves IMeshChangeFeed from ITS hub's service provider; the bare
                // Data-layer HubTestBase registers none, so wire the in-process feed here.
                .AddSingleton<IMeshChangeFeed, TestMeshChangeFeed>()
                .Configure<SyncStreamOptions>(o =>
                {
                    o.HeartbeatInterval = LongHeartbeat;
                    // JsonSynchronizationStream's own change-feed arm resubscribes a stream the
                    // feed says is behind — and a CollectionReference stream carries no version,
                    // so for it EVERY event opens that gate. Real and wanted in production; here it
                    // would add SubscribeRequests on the SAME stream that this test's per-cycle
                    // wait would count as a fresh mirror, so push it past the run.
                    o.ChangeFeedResubscribeWindow = LongHeartbeat;
                    o.ChangeFeedStalenessGrace = LongHeartbeat;
                }))
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>().WithType<LineOfBusiness>()));

    /// <summary>
    /// 🚨 <b>THE DEFECT.</b> Five change-feed evictions of an UNLEASED remote stream leave five
    /// live owner-side <c>sync/</c> hubs where one is in use: not a single
    /// <c>UnsubscribeRequest</c> is ever posted, because <c>ReclaimIfUnheld</c> returns on the
    /// missing lease entry before it can dispose anything.
    /// </summary>
    [HubFact]
    public async Task EvictingAnUnleasedRemoteStream_RetainsEveryPredecessor()
    {
        var (workspace, changeFeed, client) = await StartAndSettleAsync();
        var baselineSubscribes = Volatile.Read(ref _subscribeCount);
        var baselineUnsubscribes = Volatile.Read(ref _unsubscribeCount);
        var baselineSyncHubs = LiveSyncHubs(client);

        for (var cycle = 1; cycle <= Cycles; cycle++)
        {
            // UNLEASED: the public API. Nothing declares a hold, so nothing can ever release one.
            _ = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
                CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));

            await AwaitSubscribesAsync(baselineSubscribes + cycle,
                $"cycle {cycle} must open a fresh mirror — its predecessor was evicted from the cache");

            PublishOwnerChange(changeFeed, cycle);
        }

        var opened = Volatile.Read(ref _subscribeCount) - baselineSubscribes;
        var closed = Volatile.Read(ref _unsubscribeCount) - baselineUnsubscribes;
        var syncHubs = LiveSyncHubs(client) - baselineSyncHubs;
        Output.WriteLine(
            $"DIAG unleased: opened={opened} closed={closed} clientSyncHubs=+{syncHubs}");

        // 🚨 THE DENOMINATOR: ONE live mirror per (owner, reference, identity) is what
        // _remoteStreamCache is FOR — every caller in this loop asked for the SAME triple. Anything
        // above one is retained garbage: a stream out of the cache that no caller can reach and no
        // reaper matches, still holding its client sync/ hub and its owner-side twin.
        syncHubs.Should().Be(opened,
            $"all {opened} client sync/ hubs this test opened are still alive — an unleased stream "
            + "parked by EvictForPath is never disposed (ReclaimIfUnheld returns on the missing "
            + $"lease entry), so the population grows once per change event instead of staying at 1");
        closed.Should().Be(0,
            "no UnsubscribeRequest is posted for an unleased evicted stream — that is the retention");
    }

    /// <summary>
    /// 🚨 <b>THE POSITIVE CONTROL.</b> The identical sequence, differing only in that each stream
    /// is LEASED and the lease released, drives the same evictions and DOES reclaim: the owner
    /// receives an <c>UnsubscribeRequest</c> per superseded mirror and the live population stays
    /// flat. This is what proves the counter above can observe a release — without it, "closed ==
    /// 0" would also pass on a build that never unsubscribes anything.
    /// </summary>
    [HubFact]
    public async Task EvictingALeasedRemoteStream_ReclaimsEveryPredecessor()
    {
        var (workspace, changeFeed, client) = await StartAndSettleAsync();
        var baselineSubscribes = Volatile.Read(ref _subscribeCount);
        var baselineUnsubscribes = Volatile.Read(ref _unsubscribeCount);
        var baselineSyncHubs = LiveSyncHubs(client);

        var realLeases = 0;
        for (var cycle = 1; cycle <= Cycles; cycle++)
        {
            // LEASED: the #1324 shape the mesh-node cache and the write path use. The lease is
            // released immediately, so the moment this stream is evicted it is reclaimable —
            // and the eviction below finds no declared holder and disposes it at once.
            var (_, lease) = ((Workspace)workspace)
                .AcquireRemoteStreamUnchecked<InstanceCollection, CollectionReference>(
                    CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));

            // 🚨 AcquireRemoteStreamUnchecked hands back Disposable.Empty — NO lease at all — when
            // the stream it resolved is not usable at that instant. Count how often a REAL lease
            // was taken, so a control-arm failure cannot be misread as "leasing does not reclaim"
            // when the truth is "this arm never leased anything".
            if (!ReferenceEquals(lease, System.Reactive.Disposables.Disposable.Empty))
                realLeases++;

            await AwaitSubscribesAsync(baselineSubscribes + cycle,
                $"cycle {cycle} must open a fresh mirror — its predecessor was reclaimed");

            lease.Dispose();
            PublishOwnerChange(changeFeed, cycle);
        }

        var opened = Volatile.Read(ref _subscribeCount) - baselineSubscribes;

        // Wait for the reclaims to land, measured on the SAME metric the defect arm reports —
        // the live client sync/ hub population — so the two arms are strictly comparable.
        // Waiting on the CONDITION, not a sleep.
        var settled = await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => LiveSyncHubs(client) - baselineSyncHubs)
            .Should().Within(10.Seconds())
            .Match(h => h <= 1,
                $"a leased-then-released stream evicted by the change feed is reclaimed at once, so "
                + $"after {Cycles} evictions at most the one current mirror may remain — this is "
                + "also what proves the counter can observe a release, without which the unleased "
                + "arm's 'nothing was released' would be vacuous");

        var closed = Volatile.Read(ref _unsubscribeCount) - baselineUnsubscribes;
        Output.WriteLine(
            $"DIAG leased: opened={opened} closed={closed} realLeases={realLeases}/{Cycles} "
            + $"clientSyncHubs=+{settled}");
    }

    /// <summary>
    /// 🚨 <b>What #1174 changed about this population.</b> The same unleased sequence, driven by the
    /// event every WRITE produces — a versioned <c>Updated</c> — now leaves ONE mirror: the
    /// workspace keeps the stream and records the announced version instead of evicting it, so
    /// every caller in the loop gets the cached instance back and there is nothing to park. Before
    /// #1174 this exact loop was the unleased arm above (+1 live <c>sync/</c> hub pair per write);
    /// the retention mechanism is unchanged, it is simply no longer reached once per write.
    /// </summary>
    [HubFact]
    public async Task AVersionedUpdate_KeepsTheUnleasedStream_SoNothingIsParked()
    {
        var (workspace, changeFeed, client) = await StartAndSettleAsync();
        var baselineSubscribes = Volatile.Read(ref _subscribeCount);
        var baselineUnsubscribes = Volatile.Read(ref _unsubscribeCount);
        var baselineSyncHubs = LiveSyncHubs(client);

        var first = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
        await AwaitSubscribesAsync(baselineSubscribes + 1,
            "the first resolve opens the one mirror this key is entitled to");

        var sameEveryCycle = true;
        for (var cycle = 1; cycle <= Cycles; cycle++)
        {
            PublishVersionedWrite(changeFeed, version: cycle + 1);
            var again = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
                CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
            sameEveryCycle &= ReferenceEquals(first, again);
        }

        var opened = Volatile.Read(ref _subscribeCount) - baselineSubscribes;
        var closed = Volatile.Read(ref _unsubscribeCount) - baselineUnsubscribes;
        var syncHubs = LiveSyncHubs(client) - baselineSyncHubs;
        Output.WriteLine(
            $"DIAG versioned: opened={opened} closed={closed} clientSyncHubs=+{syncHubs} "
            + $"sameStream={sameEveryCycle}");

        sameEveryCycle.Should().BeTrue(
            "a versioned Updated keeps the cached mirror (#1174), so every resolve returns it");
        opened.Should().Be(1,
            $"{Cycles} writes on one owner must not open {Cycles} more mirrors");
        syncHubs.Should().Be(1,
            "ONE live mirror per (owner, reference, identity) — the cache's own invariant, and the "
            + "number the unleased arm above exceeds once per EVICTING event");
    }

    /// <summary>Activates both hubs and waits for the owner's initial snapshot, so the client's
    /// data-context init gate is open before any measurement is taken.</summary>
    private async Task<(IWorkspace Workspace, IMeshChangeFeed ChangeFeed, IMessageHub Client)>
        StartAndSettleAsync()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var changeFeed = client.ServiceProvider.GetRequiredService<IMeshChangeFeed>();

        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(10.Seconds())
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot",
                cancellationToken: TestContext.Current.CancellationToken);

        return (workspace, changeFeed, client);
    }

    /// <summary>
    /// #3432's own metric, in-process: <c>SynchronizationStream</c>'s constructor mints exactly one
    /// hosted <c>sync/{ClientId}</c> hub per stream, so the client's hosted-hub collection filtered
    /// to <see cref="SynchronizationAddress.AddressType"/> IS the count of client-side mirrors this
    /// workspace is keeping alive. Snapshot the collection (never tally <c>HubAdded</c>, which
    /// fires twice per hub).
    /// </summary>
    private static int LiveSyncHubs(IMessageHub hub) =>
        hub.ServiceProvider.GetRequiredService<HostedHubsCollection>()
            .Hubs.Count(h => h.Address.Type == SynchronizationAddress.AddressType);

    /// <summary>Waits until the owner has counted <paramref name="target"/> SubscribeRequests —
    /// the positive signal that this cycle's mirror actually reached the owner, so the next
    /// change event evicts a materialised stream rather than racing its creation.</summary>
    private Task AwaitSubscribesAsync(int target, string because) =>
        Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref _subscribeCount))
            .Should().Within(10.Seconds())
            .Match(c => c >= target, because, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>
    /// Fires one owner-path change event that still EVICTS: the version-less <c>Updated</c> the
    /// operator recycle broadcasts (<c>MeshOperations</c>, <c>Version: 0</c>). A versioned
    /// <c>Updated</c> no longer evicts (#1174) — see <see cref="PublishVersionedWrite"/>. The
    /// <paramref name="cycle"/> only distinguishes the events' timestamps; it is not a version.
    /// </summary>
    private static void PublishOwnerChange(IMeshChangeFeed changeFeed, int cycle) =>
        changeFeed.Publish(new MeshChangeEvent(
            Namespace: HostType,
            Id: "1",
            Path: OwnerPath,
            Kind: MeshChangeKind.Updated,
            NodeType: null,
            Version: 0,
            Timestamp: DateTimeOffset.UtcNow.AddTicks(cycle)));

    /// <summary>The event a WRITE produces: a versioned <c>Updated</c>, which the workspace answers
    /// by recording a freshness floor and keeping the mirror (#1174).</summary>
    private static void PublishVersionedWrite(IMeshChangeFeed changeFeed, long version) =>
        changeFeed.Publish(new MeshChangeEvent(
            Namespace: HostType,
            Id: "1",
            Path: OwnerPath,
            Kind: MeshChangeKind.Updated,
            NodeType: null,
            Version: version,
            Timestamp: DateTimeOffset.UtcNow));
}
