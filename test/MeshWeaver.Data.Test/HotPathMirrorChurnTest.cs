using System;
using System.Collections.Generic;
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
/// 🚨 <b>A HOT path must not hydrate a brand-new mirror per write</b> — Systemorph/MeshWeaver#1174.
///
/// <para><b>The mechanism.</b> <c>Workspace.EvictForPath</c> fired on EVERY change-feed
/// <c>Created</c>/<c>Updated</c>/<c>Deleted</c> and evicted every cached mirror for that owner —
/// <i>including the one the writer itself had just used, on the writer's own commit</i>. A write
/// leases its mirror only for its <c>Observable.Create</c> subscription, so the eviction plus the
/// release made <c>ReclaimIfUnheld</c> DISPOSE it (<c>UnsubscribeRequest</c> to the owner, both
/// <c>sync/{id}</c> hubs gone), and the next write on that path resolved a <i>fresh</i> mirror:
/// another <c>SubscribeRequest</c>, another initial-state round trip, another pair of <c>sync/</c>
/// hubs. <b>Per write.</b></para>
///
/// <para><b>What that cost in production.</b> Each of those hydrations has to complete inside the
/// writer's <c>MeshNodeStreamHandle.BaseStateWaitBound</c> (30 s) while the mirror it replaced is
/// being torn down on the SAME owner address. <c>{user}/_UserActivity/{user}</c> — written on every
/// cold page load, measured at version 6498 — had paid that cycle thousands of times; a cold path
/// pays it once. That is why #1174's 414 <c>TimeoutException</c>s over five weeks concentrated on
/// hot paths and on repeatedly-visited pages, and why cold paths never appeared in the sample —
/// the correlation the issue thread called unexplained.</para>
///
/// <para><b>The fix, and what it is NOT.</b> The change feed already carries the committed
/// <c>Version</c>. The workspace now RECORDS it against the owner and KEEPS the mirror; the
/// writer's base read waits for the mirror to REACH that version before diffing
/// (<c>MeshNodeStreamHandle.RebaseSource</c>), so freshness is <i>proven per write</i> instead of
/// bought by throwing the mirror away. 🚨 This is NOT the LIVENESS gate that was tried, measured
/// and reverted (<c>Doc/Architecture/LiveMirrorsAndTheChangeFeed</c>): liveness asks how the mirror
/// LOOKS and lets a healthy-but-behind one through, which lost a whole 25-message append batch in
/// <c>StaticRepoImportActivityWriteCountTest</c>. A mirror that cannot prove it carries the
/// announced commit is evicted by the writer itself — the old behaviour, paid once on proof rather
/// than once per commit.</para>
///
/// <para>Every arm drives the LEASED acquire — <c>AcquireRemoteStreamUnchecked</c> + release, the
/// exact shape the mesh-node cache and the cross-hub write path use — so what is measured is the
/// write path's own mirror lifetime, not a reader's.</para>
/// </summary>
public class HotPathMirrorChurnTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Commits driven on the hot path. Small: the defect is monotone (one fresh mirror
    /// per commit), so a handful separates "one mirror" from "one per write".</summary>
    private const int Commits = 5;

    /// <summary>Long enough that no heartbeat, and no change-feed-triggered resubscribe, fires
    /// during the measurement — so every <c>SubscribeRequest</c> counted is a RE-RESOLVE this test
    /// caused, never background repair traffic.</summary>
    private static TimeSpan NeverDuringTheTest => TestTimeouts.Convergence * 10;

    /// <summary>The owner path the change events name — <c>Address.ToString()</c> of
    /// <see cref="HubTestBase.CreateHostAddress"/>, which is what the workspace compares against
    /// (<c>key.Owner.ToString() == path</c>).</summary>
    private const string OwnerPath = HostType + "/1";

    // Instance fields, never static — no cross-test bleed (xUnit builds a fresh instance per test).
    private int _subscribeCount;
    private int _unsubscribeCount;

    /// <summary>
    /// Minimal in-process <see cref="IMeshChangeFeed"/> — a plain Rx Subject, matching the
    /// production <c>InProcessMeshChangeFeed</c> contract. <c>Workspace</c> resolves this by name
    /// through reflection and subscribes with a <c>null</c> filter, so EVERY kind reaches it.
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
                    o.HeartbeatInterval = NeverDuringTheTest;
                    // JsonSynchronizationStream's own change-feed arm coalesces then resubscribes
                    // when its stream is behind. That repair is real and wanted in production; here
                    // it would add SubscribeRequests this test would have to explain away, so push
                    // it past the run and let the measurement be exactly the re-resolves.
                    o.ChangeFeedResubscribeWindow = NeverDuringTheTest;
                    o.ChangeFeedStalenessGrace = NeverDuringTheTest;
                }))
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>().WithType<LineOfBusiness>()));

    /// <summary>
    /// 🚨 <b>THE REGRESSION.</b> Five versioned commits on one owner — the hot path — must leave the
    /// writer looking at ONE mirror, not five. RED before the fix: every commit evicted the mirror,
    /// so each acquire handed back a different instance and posted a fresh <c>SubscribeRequest</c>
    /// while the predecessor's <c>UnsubscribeRequest</c> went the other way on the same address.
    /// </summary>
    [HubFact]
    public async Task AHotPath_KeepsOneMirror_AcrossEveryCommit()
    {
        var (workspace, changeFeed, _) = await StartAndSettleAsync();
        var mirrors = new List<ISynchronizationStream<InstanceCollection>>();

        // Acquire once BEFORE the first commit, exactly as a write does, and take the baseline off
        // that — so the counts below are the churn the COMMITS caused and nothing else. 🚨 Wait
        // for THAT acquire's SubscribeRequest (count before it + 1), never for "the count it
        // already has": the request is posted when the stream is built and reaches the owner
        // asynchronously, so a baseline read before it lands counts it as churn mid-loop.
        var subscribesBeforeFirstMirror = Volatile.Read(ref _subscribeCount);
        mirrors.Add(AcquireAndRelease(workspace));
        await AwaitSubscribesAsync(subscribesBeforeFirstMirror + 1,
            "the first mirror must have reached the owner before any commit is published");
        var baselineSubscribes = Volatile.Read(ref _subscribeCount);
        var baselineUnsubscribes = Volatile.Read(ref _unsubscribeCount);

        for (var commit = 1; commit <= Commits; commit++)
        {
            PublishCommit(changeFeed, MeshChangeKind.Updated, version: commit);
            mirrors.Add(AcquireAndRelease(workspace));
        }

        var opened = Volatile.Read(ref _subscribeCount) - baselineSubscribes;
        var closed = Volatile.Read(ref _unsubscribeCount) - baselineUnsubscribes;
        Output.WriteLine(
            $"DIAG hot path: commits={Commits} distinctMirrors={DistinctCount(mirrors)} "
            + $"opened={opened} closed={closed}");

        DistinctCount(mirrors).Should().Be(1,
            $"{Commits} commits on ONE owner must leave the writer on ONE mirror. A distinct "
            + "instance per commit is the per-write re-hydration of #1174: each one costs a "
            + "SubscribeRequest, an initial-state round trip and a pair of sync/ hubs, and each "
            + "has to complete inside the writer's 30 s BaseStateWaitBound while its predecessor "
            + "is torn down on the same owner address");
        opened.Should().Be(0,
            "a kept mirror needs no SubscribeRequest — the owner fans its commits out to the "
            + "subscription that is already open, and the writer's base read waits for the "
            + "announced version rather than for a fresh snapshot");
        closed.Should().Be(0,
            "…and nothing is torn down: an UnsubscribeRequest per commit is the other half of the "
            + "churn, and the owner's StreamEndedEvent to the subscriber it just lost is #2776");
    }

    /// <summary>
    /// 🚨 <b>THE NEGATIVE CONTROL, and it is not optional.</b> A <c>Deleted</c> event announces
    /// version 0 — there is no version for a mirror to reach, and a recreate restarts the counter —
    /// so it must STILL evict. Without this arm, "no eviction happened" above would also pass on a
    /// build where the change feed reached the workspace not at all, or where eviction was deleted
    /// outright; this proves the instrument can observe one.
    /// </summary>
    [HubFact]
    public async Task ADeleteStillEvictsTheMirror()
        => await AssertTheMirrorIsEvictedBy(MeshChangeKind.Deleted, version: 0,
            "a version-less event carries nothing a base read could wait for, so the pre-#1174 "
            + "eviction is the only safe answer — and for a delete the node itself is gone, so a "
            + "remembered high-water version could never be reached again");

    /// <summary>
    /// 🚨 …and a <c>Created</c> on a path this workspace ALREADY mirrors is a RECREATE — a new
    /// incarnation whose version counter restarted — so it evicts even though it carries a
    /// version. Holding the old mirror to the new incarnation's version would let a node from the
    /// previous life pass as fresh (its version is HIGHER). This is also the event
    /// <c>JsonSynchronizationStream</c> treats as the recreate signal; the two must not disagree.
    /// </summary>
    [HubFact]
    public async Task ARecreateStillEvictsTheMirror_EvenThoughItCarriesAVersion()
        => await AssertTheMirrorIsEvictedBy(MeshChangeKind.Created, version: 1,
            "only a versioned UPDATE may keep a mirror: a create on a mirrored path starts a new "
            + "incarnation, and the old mirror's versions say nothing about it");

    private async Task AssertTheMirrorIsEvictedBy(MeshChangeKind kind, long version, string because)
    {
        var (workspace, changeFeed, _) = await StartAndSettleAsync();

        // Same baseline rule as the regression arm: wait for THIS acquire's SubscribeRequest.
        var subscribesBeforeFirstMirror = Volatile.Read(ref _subscribeCount);
        var before = AcquireAndRelease(workspace);
        await AwaitSubscribesAsync(subscribesBeforeFirstMirror + 1,
            "the mirror must have reached the owner before the event is published");
        var baselineSubscribes = Volatile.Read(ref _subscribeCount);

        PublishCommit(changeFeed, kind, version);
        var after = AcquireAndRelease(workspace);

        await AwaitSubscribesAsync(baselineSubscribes + 1,
            "an evicted mirror is gone from the cache, so the next acquire must open a fresh one");

        Output.WriteLine(
            $"DIAG {kind}: sameMirror={ReferenceEquals(before, after)} "
            + $"opened={Volatile.Read(ref _subscribeCount) - baselineSubscribes}");

        // ReferenceEquals, not `.Should().NotBeSameAs(...)`: ISynchronizationStream<T> IS an
        // IObservable<ChangeItem<T>>, so `.Should()` binds to the observable assertions.
        ReferenceEquals(before, after).Should().BeFalse(because);
    }

    /// <summary>The LEASED acquire — <c>AcquireRemoteStreamUnchecked</c> then release — which is
    /// exactly what a cross-hub write does for the span of its subscription.</summary>
    private ISynchronizationStream<InstanceCollection> AcquireAndRelease(IWorkspace workspace)
    {
        var (stream, lease) = ((Workspace)workspace)
            .AcquireRemoteStreamUnchecked<InstanceCollection, CollectionReference>(
                CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
        lease.Dispose();
        return stream;
    }

    /// <summary>How many DISTINCT stream instances a run touched — reference identity, because
    /// "the same mirror" is an object-lifetime claim, not a value one.</summary>
    private static int DistinctCount(IEnumerable<ISynchronizationStream<InstanceCollection>> mirrors)
        => mirrors.Distinct(ReferenceEqualityComparer.Instance).Count();

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
            .Should().Within(TestTimeouts.Quick)
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot");

        return (workspace, changeFeed, client);
    }

    /// <summary>Waits until the owner has counted <paramref name="target"/> SubscribeRequests —
    /// the positive signal that the mirror actually reached the owner, so a change event evicts a
    /// materialised stream rather than racing its creation.</summary>
    private Task AwaitSubscribesAsync(int target, string because) =>
        Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Select(_ => Volatile.Read(ref _subscribeCount))
            .Should().Within(TestTimeouts.Quick)
            .Match(c => c >= target, because);

    /// <summary>One commit on the hot path, as the persistence layer publishes it.</summary>
    private static void PublishCommit(IMeshChangeFeed changeFeed, MeshChangeKind kind, long version) =>
        changeFeed.Publish(new MeshChangeEvent(
            Namespace: HostType,
            Id: "1",
            Path: OwnerPath,
            Kind: kind,
            NodeType: null,
            Version: version,
            Timestamp: DateTimeOffset.UtcNow));
}
