using System;
using System.Collections.Generic;
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
/// 🚨 <b>A write's freshness floor lives and dies with the MIRROR it was announced for</b> —
/// issue #1174, review finding on PR #4428.
///
/// <para><b>The defect this pins.</b> The first cut recorded the floor in a per-owner map beside
/// the stream cache: the change-feed handler checked "does the cache still mirror this owner?" and
/// then wrote the map. The two steps were not atomic with the cache's removals, so a mirror removed
/// in between — an idle release, an eviction, a fault — left a floor with no mirror behind it, and
/// the NEXT mirror built for that owner inherited it. A fresh mirror hydrates at or past any
/// version announced before it, so the inherited floor was never needed; what it could do was make
/// that mirror's first write wait out the catch-up bound (and, had it ever carried an older node,
/// evict it) for nothing. The map also kept the entry for as long as nothing else swept it.</para>
///
/// <para><b>The shape of the fix.</b> The floor is a field OF the mirror instance. The handler
/// raises it on the exact instance it found in the cache; whatever removes that instance — any
/// removal path, now or later — removes the floor with it, and a new mirror is a new instance that
/// starts with none. There is nothing to keep in step, so nothing can fall out of step.</para>
///
/// <para>Both arms reproduce the interleaving DETERMINISTICALLY: a test seam on the workspace runs
/// the removal exactly between "the handler located the mirror" and "the handler recorded the
/// floor", which is the window the review named.</para>
/// </summary>
public class AnnouncedFloorLifetimeTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Long enough that no heartbeat or change-feed resubscribe fires during the test.</summary>
    private static TimeSpan NeverDuringTheTest => TestTimeouts.Convergence * 10;

    /// <summary><c>Address.ToString()</c> of <see cref="HubTestBase.CreateHostAddress"/> — what the
    /// workspace compares a change event's path against.</summary>
    private const string OwnerPath = HostType + "/1";

    // Instance, never static — no cross-test bleed.
    private int _subscribeCount;

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
                    o.HeartbeatInterval = NeverDuringTheTest;
                    o.ChangeFeedResubscribeWindow = NeverDuringTheTest;
                    o.ChangeFeedStalenessGrace = NeverDuringTheTest;
                }))
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>().WithType<LineOfBusiness>()));

    /// <summary>
    /// 🚨 <b>THE REVIEW'S INTERLEAVING.</b> The handler has located the owner's last mirror; before
    /// it records the floor, that mirror is removed (here by the shared mesh-node cache's idle
    /// release, <c>DetachRemoteStreams</c>). A NEW mirror is then built for the owner. It must start
    /// with no floor — it hydrates from the owner's current state, which already carries the
    /// announced commit.
    ///
    /// <para>RED against the per-owner map (the floor was written after the removal and the new
    /// mirror inherited it); GREEN when the floor is a field of the mirror it was recorded on.</para>
    /// </summary>
    [HubFact]
    public async Task AFloorRecordedWhileItsMirrorIsRemoved_NeverReachesTheNextMirror()
    {
        var (workspace, changeFeed) = await StartAndSettleAsync();

        var first = await AcquireSettledMirrorAsync(workspace);

        // The removal the review named, placed exactly in the window it named.
        var detached = new List<ISynchronizationStream>();
        var removalsRun = 0;
        workspace.BeforeAnnouncedVersionRecorded = path =>
        {
            if (!string.Equals(path, OwnerPath, StringComparison.OrdinalIgnoreCase)
                || Interlocked.Exchange(ref removalsRun, 1) == 1)
                return;
            detached.AddRange(workspace.DetachRemoteStreams(
                CreateHostAddress(), new CollectionReference(nameof(BusinessUnit))));
        };

        PublishCommit(changeFeed, MeshChangeKind.Updated, version: 7);
        workspace.BeforeAnnouncedVersionRecorded = null;

        // POSITIVE CONTROLS: the interleaving actually happened, and it removed THIS mirror.
        Volatile.Read(ref removalsRun).Should().Be(1,
            "the seam must have run between locating the mirror and recording its floor — without "
            + "it this test reproduces nothing");
        detached.Should().Contain(first, "the removal in the window must have taken the mirror");

        var next = AcquireAndRelease(workspace);
        ReferenceEquals(first, next).Should().BeFalse(
            "the first mirror left the cache, so the next acquire builds a new one");

        var inherited = Workspace.AnnouncedVersionFor(next);
        Output.WriteLine($"DIAG interleaving: floorOnNewMirror={inherited}");
        inherited.Should().Be(0L,
            "🚨 a floor recorded for a mirror that was removed before the record landed must not "
            + "reach the NEXT mirror. That mirror hydrates from the owner's current state, so the "
            + "inherited floor can only make its first write wait out the catch-up bound for "
            + "nothing — and it would sit in the workspace until some later event swept it");

        foreach (var stream in detached)
            stream.Dispose();
    }

    /// <summary>
    /// 🚨 <b>The faulted-removal path takes the floor with it.</b> A mirror that recorded a floor
    /// and then FAULTED is dropped from the cache by the next resolve
    /// (<c>GetExternalClientSynchronizationStream</c> → <c>DiscardFaultedRemoteStream</c>), which the
    /// per-owner map never heard about. The rebuilt mirror must start with no floor.
    /// </summary>
    [HubFact]
    public async Task AFaultedMirror_TakesItsFloorWithIt()
    {
        var (workspace, changeFeed) = await StartAndSettleAsync();

        var first = await AcquireSettledMirrorAsync(workspace);

        PublishCommit(changeFeed, MeshChangeKind.Updated, version: 5);
        Workspace.AnnouncedVersionFor(first).Should().Be(5L,
            "POSITIVE CONTROL: the versioned commit recorded a floor on the live mirror — without "
            + "it, 'no floor after the fault' below would pass on a build that records none");

        first.OnError(new InvalidOperationException("the owner answered this mirror's re-ask with a fault"));

        var rebuilt = AcquireAndRelease(workspace);
        ReferenceEquals(first, rebuilt).Should().BeFalse(
            "a faulted mirror is dropped from the cache, so the next resolve builds a new one");

        var inherited = Workspace.AnnouncedVersionFor(rebuilt);
        Output.WriteLine($"DIAG faulted: floorOnRebuiltMirror={inherited}");
        inherited.Should().Be(0L,
            "the floor belonged to the mirror that faulted; the rebuilt one hydrates fresh and "
            + "must not inherit it");
    }

    /// <summary>Acquires the owner's mirror the way a write does and waits for ITS
    /// SubscribeRequest to reach the owner, so every later step acts on a materialised mirror.</summary>
    private async Task<ISynchronizationStream<InstanceCollection>> AcquireSettledMirrorAsync(Workspace workspace)
    {
        var before = Volatile.Read(ref _subscribeCount);
        var mirror = AcquireAndRelease(workspace);
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Select(_ => Volatile.Read(ref _subscribeCount))
            .Should().Within(TestTimeouts.Quick)
            .Match(c => c >= before + 1, "the mirror must have reached the owner",
                cancellationToken: TestContext.Current.CancellationToken);
        return mirror;
    }

    /// <summary>The LEASED acquire a cross-hub write uses.</summary>
    private ISynchronizationStream<InstanceCollection> AcquireAndRelease(Workspace workspace)
    {
        var (stream, lease) = workspace.AcquireRemoteStreamUnchecked<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
        lease.Dispose();
        return stream;
    }

    private async Task<(Workspace Workspace, IMeshChangeFeed ChangeFeed)> StartAndSettleAsync()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var changeFeed = client.ServiceProvider.GetRequiredService<IMeshChangeFeed>();

        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(TestTimeouts.Quick)
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot",
                cancellationToken: TestContext.Current.CancellationToken);

        return ((Workspace)workspace, changeFeed);
    }

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
