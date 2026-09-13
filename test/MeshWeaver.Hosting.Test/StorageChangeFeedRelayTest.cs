#pragma warning disable CS1591

using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the process-local half of storage-backed cache invalidation. In production every portal
/// replica owns a PostgreSQL LISTEN session and therefore its own <see cref="IStorageAdapter.Changes"/>
/// feed. Each feed must reach that replica's <see cref="InProcessMeshChangeFeed"/> directly; one
/// Orleans grain activation cannot deliver into the process memory of every silo.
///
/// <para>The compatibility read that a hint-less notification requires goes through
/// <see cref="ReReadCoalescing"/>, so these tests drive it on a <see cref="TestScheduler"/>: a
/// hinted or entity-bearing notification is relayed synchronously on <c>Announce</c>; a hint-less
/// one is relayed once the path has been quiet for <see cref="ReReadCoalescing.Window"/>, and only
/// if nothing newer arrived on that path in the meantime — the newest notification on a path
/// always wins, so a read in flight is overtaken by any later arrival and says nothing.</para>
/// </summary>
public class StorageChangeFeedRelayTest
{
    private readonly JsonSerializerOptions json = new();
    private const string Path = "Hosting/PlatformBuilds/plugins";

    [Fact]
    public async Task PersistenceRegistration_RelaysStorageOnlyToTheSameLocalInvalidationFeed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemoryPersistence(new InMemoryStorageAdapter());
        await using var provider = services.BuildServiceProvider();

        var contract = provider.GetRequiredService<IMeshChangeFeed>();
        var invalidations = provider.GetRequiredService<IMeshInvalidationFeed>();
        var local = provider.GetRequiredService<InProcessMeshChangeFeed>();
        contract.Should().BeSameAs(local,
            "logical events and direct cache invalidations must share one process-local owner");
        invalidations.Should().BeSameAs(local,
            "storage notifications and cache consumers must share that same process-local owner");

        var logical = new ImmutableSignal<MeshChangeEvent>();
        var invalidated = new ImmutableSignal<MeshChangeEvent>();
        using var logicalSubscription = contract.Subscribe(logical.Add);
        using var faultingInvalidator = invalidations.Subscribe(_ =>
            throw new InvalidOperationException("one broken cache"));
        using var invalidationSubscription = invalidations.Subscribe(invalidated.Add);
        var node = Node(version: 7, nodeType: "Hosting/Publication");

        await provider.GetRequiredService<IStorageAdapter>()
            .Write(node, json)
            .Should().Emit();

        logical.Items.Should().BeEmpty(
            "a durable-store echo must not re-run mail, instance sync or other logical consumers");
        invalidated.Items.Should().ContainSingle();
        invalidated.Items[0].Path.Should().Be(Path);
        invalidated.Items[0].Kind.Should().Be(MeshChangeKind.Updated);
        invalidated.Items[0].NodeType.Should().Be("Hosting/Publication");
        invalidated.Items[0].Version.Should().Be(7);

        contract.Publish(MeshChangeEvent.Updated(Node(version: 8, nodeType: "Hosting/Publication")));

        logical.Items.Select(e => e.Version).Should().Equal(8L);
        invalidated.Items.Select(e => e.Version).Should().Equal(new[] { 7L, 8L },
            "the writer's explicit logical publish must invalidate its own process too");
    }

    [Fact]
    public async Task LegacyCrossProcessNotification_RereadsBeforeTypeFilteringConsumersSeeIt()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        var stored = Node(version: 12, nodeType: "Hosting/Publication");
        await durable.Write(stored, json).Should().Emit();
        using var adapter = new ControllableNotificationAdapter(durable);
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var relay = new StorageChangeFeedRelay(adapter, received.Add, scheduler: scheduler);
        var committedAt = DateTimeOffset.Parse("2026-09-12T13:43:58Z");

        // The first core image may run briefly with an older PostgreSQL notifier whose payload has
        // path/op but no additive NodeType/Version members. This is the rollout boundary.
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, Timestamp: committedAt));

        received.Items.Should().BeEmpty(
            "the compatibility read is coalesced: nothing is read or relayed until the path has "
            + "been quiet for the coalescing window");
        adapter.ReadCalls.Should().Be(0);
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);

        received.Items.Should().ContainSingle();
        var change = received.Items[0];
        change.Path.Should().Be(Path);
        change.NodeType.Should().Be("Hosting/Publication");
        change.Version.Should().Be(12);
        change.Timestamp.Should().Be(committedAt);
        adapter.ReadCalls.Should().Be(1);
    }

    /// <summary>
    /// The read-storm guard, at the relay (#4139). The end-to-end measurement lives in the plugins
    /// repo (<c>CrossProcessChangeFeedTest.AnEntitylessBurst_ReReadsTheOwnPathAFewTimes_AndAnUnownedPathNotAtAll</c>,
    /// which failed with 201 reads for 200 notifications the moment the relay shipped reading once
    /// per notification); this pins the relay's own share of it: a burst on one path is ONE read
    /// per quiet window, the last notification of the burst is the one that reads, and a path the
    /// burst did not name is not read at all.
    /// </summary>
    [Fact]
    public async Task AnEntitylessBurstOnOnePath_CostsOneReadPerQuietWindow_NotOnePerNotification()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        await durable.Write(Node(version: 3, nodeType: "Hosting/Publication"), json).Should().Emit();
        using var adapter = new ControllableNotificationAdapter(durable);
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var relay = new StorageChangeFeedRelay(adapter, received.Add, scheduler: scheduler);

        for (var i = 0; i < 200; i++)
            adapter.Announce(new DataChangeNotification(
                Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));

        adapter.ReadCalls.Should().Be(0,
            "a notification is a trigger, never a read: the read waits for the quiet window");
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);

        adapter.ReadCalls.Should().Be(1,
            "a notification storm must not become a read storm: 200 notifications on one path "
            + "collapse to one coalesced read");
        adapter.ReadPaths.Should().Equal(new[] { Path }, "only the path the burst named is read");
        received.Items.Select(e => (e.Path, e.Version, e.NodeType)).Should().Equal(
            (Path, 3L, "Hosting/Publication"));

        // A later burst is a new quiet window — and the store has moved, which is what the read
        // after the LAST notification exists to observe.
        await durable.Write(Node(version: 4, nodeType: "Hosting/Publication"), json).Should().Emit();
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);

        adapter.ReadCalls.Should().Be(2);
        received.Items.Select(e => e.Version).Should().Equal(3L, 4L);
    }

    /// <summary>
    /// A hint-less create followed by a delete on the same path, inside the quiet window: the
    /// burst ends on the delete, which is self-contained and already the newest thing to say, so
    /// no read is owed at all — and nothing can publish a <c>Created</c> carrying no node and no
    /// version after the <c>Deleted</c>, which <c>NodeTypeRebindWatcher.RequiresRebind</c> would
    /// read as a retype to "(none)" and answer with a recycle of a hub the delete is tearing down.
    /// </summary>
    [Fact]
    public async Task AnEntitylessCreateFollowedByADeleteOnOnePath_NeverResurrectsIt()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        await durable.Write(Node(version: 1, nodeType: "Hosting/Publication"), json).Should().Emit();
        using var adapter = new ControllableNotificationAdapter(durable);
        var received = new ImmutableSignal<MeshChangeEvent>();
        var readBound = ReReadCoalescing.Window * 2;
        using var relay = new StorageChangeFeedRelay(
            adapter, received.Add, legacyReadTimeout: readBound, scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Created, Entity: null, DateTimeOffset.UtcNow));
        await durable.Delete(Path).Should().Emit();
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Deleted, Entity: null, DateTimeOffset.UtcNow));

        received.Items.Select(e => (e.Kind, e.Version)).Should().Equal(
            new[] { (MeshChangeKind.Deleted, 0L) },
            "a delete is self-contained and relayed at once");
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks + readBound.Ticks);

        adapter.ReadCalls.Should().Be(0,
            "the burst ended on the delete, which already said the newest thing there is to say "
            + "about this path — a read for the older create is not owed");
        received.Items.Select(e => (e.Kind, e.Version)).Should().Equal(
            new[] { (MeshChangeKind.Deleted, 0L) });
    }

    /// <summary>
    /// The same delete landing while the create's read is already IN FLIGHT: the delete is relayed
    /// at once and overtakes the read, which then says nothing — not a path-only fallback, not a
    /// stale row.
    /// </summary>
    [Fact]
    public async Task ADeleteDuringTheCreatesRead_OvertakesIt_AndNothingResurrectsThePath()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        await durable.Write(Node(version: 1, nodeType: "Hosting/Publication"), json).Should().Emit();
        using var adapter = new ControllableNotificationAdapter(durable) { NeverRead = true };
        var received = new ImmutableSignal<MeshChangeEvent>();
        var readBound = ReReadCoalescing.Window * 2;
        using var relay = new StorageChangeFeedRelay(
            adapter, received.Add, legacyReadTimeout: readBound, scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Created, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        adapter.ReadCalls.Should().Be(1, "the create's read is in flight");
        adapter.InFlightReads.Should().Be(1);

        await durable.Delete(Path).Should().Emit();
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Deleted, Entity: null, DateTimeOffset.UtcNow));

        received.Items.Select(e => (e.Kind, e.Version)).Should().Equal(
            new[] { (MeshChangeKind.Deleted, 0L) });
        adapter.InFlightReads.Should().Be(0, "the delete overtook the read and ended it");
        scheduler.AdvanceBy(readBound.Ticks);
        received.Items.Select(e => (e.Kind, e.Version)).Should().Equal(
            new[] { (MeshChangeKind.Deleted, 0L) },
            "an overtaken read says nothing — not even its path-only fallback at the bound");
    }

    /// <summary>
    /// A read that finds NO row says nothing: the row is gone, and whatever removed it was
    /// self-contained and relayed on arrival. The read is the positive control — it ran.
    /// </summary>
    [Fact]
    public void AReadThatFindsNoRow_SaysNothing()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable);
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var relay = new StorageChangeFeedRelay(adapter, received.Add, scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);

        adapter.ReadCalls.Should().Be(1, "the read ran");
        received.Items.Should().BeEmpty(
            "a row the read cannot find was deleted, and the delete already invalidated the path: "
            + "an Updated after it, carrying no node and no version, would read as a retype to "
            + "'(none)' to the rebind watcher");
    }

    /// <summary>
    /// The newest notification on a path wins across bursts too: a hint-less notification that
    /// lands while an earlier one's read is still in flight overtakes that read (it ends, and
    /// says nothing), and the newer burst's own read is the one that speaks. Reads on a path
    /// therefore never overlap.
    /// </summary>
    [Fact]
    public async Task AnArrivalDuringAnInFlightRead_OvertakesIt_AndOnlyTheNewerReadSpeaks()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        await durable.Write(Node(version: 5, nodeType: "Hosting/Publication"), json).Should().Emit();
        using var adapter = new ControllableNotificationAdapter(durable) { NeverRead = true };
        var received = new ImmutableSignal<MeshChangeEvent>();
        var readBound = ReReadCoalescing.Window * 2;
        using var relay = new StorageChangeFeedRelay(
            adapter, received.Add, legacyReadTimeout: readBound, scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        adapter.ReadCalls.Should().Be(1, "the first burst's read is in flight");
        adapter.InFlightReads.Should().Be(1);

        // The store moves and the next notification arrives while that read has not answered.
        adapter.NeverRead = false;
        await durable.Write(Node(version: 6, nodeType: "Hosting/Publication"), json).Should().Emit();
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        adapter.InFlightReads.Should().Be(0, "the newer notification overtook the in-flight read");
        received.Items.Should().BeEmpty("an overtaken read says nothing");

        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        adapter.ReadCalls.Should().Be(2, "the newer burst reads for itself");
        received.Items.Select(e => e.Version).Should().Equal(new[] { 6L },
            "only the newest information about the path is published");
        scheduler.AdvanceBy(readBound.Ticks);
        received.Items.Select(e => e.Version).Should().Equal(new[] { 6L },
            "the overtaken read cannot land its path-only fallback later either");
        adapter.MaxInFlightReads.Should().Be(1, "reads on a path never overlap");
    }

    /// <summary>
    /// Three waves, each landing while the previous read is still inside its bound, then a wave
    /// after the group has closed: every read is overtaken or over before the next starts, so the
    /// path's reads never overlap — including across the group boundary, where the previous
    /// design could close a group while a queued read was still running.
    /// </summary>
    [Fact]
    public void ThreeSlowWaves_ThenAWaveAfterTheGroupClosed_NeverOverlapReads()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable) { NeverRead = true };
        var received = new ImmutableSignal<MeshChangeEvent>();
        var readBound = ReReadCoalescing.Window * 2;
        using var relay = new StorageChangeFeedRelay(
            adapter, received.Add, legacyReadTimeout: readBound, scheduler: scheduler);

        for (var wave = 1; wave <= 3; wave++)
        {
            adapter.Announce(new DataChangeNotification(
                Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
            scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
            adapter.ReadCalls.Should().Be(wave, $"wave {wave} reads once its window closes");
            adapter.InFlightReads.Should().Be(1);
        }
        received.Items.Should().BeEmpty("every earlier read was overtaken before it answered");

        // The third read runs to its bound: one path-only fallback, and the group closes right
        // after it (window + bound past the last notification).
        scheduler.AdvanceBy(readBound.Ticks);
        received.Items.Select(e => e.Version).Should().Equal(new[] { 0L });
        adapter.InFlightReads.Should().Be(0);
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);

        // A wave after the group closed opens a new group; nothing is in flight for it to race.
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        adapter.ReadCalls.Should().Be(4);
        scheduler.AdvanceBy(readBound.Ticks);
        received.Items.Select(e => e.Version).Should().Equal(0L, 0L);
        adapter.MaxInFlightReads.Should().Be(1, "reads on a path never overlap, across groups too");
    }

    /// <summary>
    /// A hinted notification inside the quiet window ends the burst: it is relayed at once and it
    /// is newer than the hint-less one before it, so no read is owed for that burst at all.
    /// </summary>
    [Fact]
    public void AHintedNotificationInsideTheQuietWindow_EndsTheBurst_SoNoReadIsOwed()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable);
        var received = new ImmutableSignal<MeshChangeEvent>();
        var readBound = ReReadCoalescing.Window * 2;
        using var relay = new StorageChangeFeedRelay(
            adapter, received.Add, legacyReadTimeout: readBound, scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow)
        {
            NodeType = "Hosting/Publication",
            Version = 52,
        });

        received.Items.Select(e => (e.Path, e.Version)).Should().Equal(
            new[] { (Path, 52L) },
            "a hinted invalidation is self-contained and is relayed at once");
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks + readBound.Ticks);
        adapter.ReadCalls.Should().Be(0,
            "the burst ended on the hinted notification, which is newer than the hint-less one — "
            + "a read would only repeat what it already said");
        received.Items.Select(e => (e.Path, e.Version)).Should().Equal(new[] { (Path, 52L) });
    }

    [Fact]
    public async Task OneMetadataReadFailure_StillInvalidatesThePath_AndDoesNotStopTheNextCommit()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        await durable.Write(Node(version: 20, nodeType: "Hosting/Publication"), json)
            .Should().Emit();
        using var adapter = new ControllableNotificationAdapter(durable)
        {
            NextReadError = new InvalidOperationException("transient read fault")
        };
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var relay = new StorageChangeFeedRelay(adapter, received.Add, scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        received.Items.Select(e => (e.Path, e.Version, e.NodeType)).Should().Equal(
            new[] { (Path, 0L, (string?)null) },
            "a read that faults leaves the row's state unknown, so the path is still invalidated");

        // The fault did not terminate the path's read queue: the next hint-less notification
        // reads, and reads the row.
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        received.Items.Select(e => (e.Version, e.NodeType)).Should().Equal(
            (0L, (string?)null),
            (20L, "Hosting/Publication"));

        // And a hinted commit is never held behind any of it.
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow)
        {
            NodeType = "Hosting/Publication",
            Version = 21,
        });
        received.Items.Select(e => (e.Version, e.NodeType)).Should().Equal(
            (0L, (string?)null),
            (20L, "Hosting/Publication"),
            (21L, "Hosting/Publication"));
    }

    [Fact]
    public void OneMalformedNotification_DoesNotStopTheReplicaFromReceivingTheNextCommit()
    {
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable);
        using var feed = new InProcessMeshChangeFeed(adapter);
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var subscription = ((IMeshInvalidationFeed)feed).Subscribe(received.Add);

        adapter.Announce(new DataChangeNotification(
            Path, (DataChangeKind)int.MaxValue, Entity: null, DateTimeOffset.UtcNow)
        {
            NodeType = "Hosting/Publication",
            Version = 40,
        });
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow)
        {
            NodeType = "Hosting/Publication",
            Version = 41,
        });

        received.Items.Should().ContainSingle();
        received.Items[0].Version.Should().Be(41,
            "one unrecognised notification may be dropped, but must not terminate the relay");
    }

    [Fact]
    public async Task TwoProcessLocalFeeds_BothReceiveTheirStorageListenerCopy()
    {
        var durable = new InMemoryStorageAdapter();
        await durable.Write(Node(version: 31, nodeType: "Hosting/Publication"), json)
            .Should().Emit();
        using var replicaA = new ControllableNotificationAdapter(durable);
        using var replicaB = new ControllableNotificationAdapter(durable);
        using var feedA = new InProcessMeshChangeFeed(replicaA);
        using var feedB = new InProcessMeshChangeFeed(replicaB);
        var seenA = new ImmutableSignal<MeshChangeEvent>();
        var seenB = new ImmutableSignal<MeshChangeEvent>();
        var logicalA = new ImmutableSignal<MeshChangeEvent>();
        var logicalB = new ImmutableSignal<MeshChangeEvent>();
        using var subscriptionA = ((IMeshInvalidationFeed)feedA).Subscribe(seenA.Add);
        using var subscriptionB = ((IMeshInvalidationFeed)feedB).Subscribe(seenB.Add);
        using var logicalSubscriptionA = feedA.Subscribe(logicalA.Add);
        using var logicalSubscriptionB = feedB.Subscribe(logicalB.Add);
        var notification = new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow)
        {
            NodeType = "Hosting/Publication",
            Version = 31,
        };

        // PostgreSQL delivers the same committed NOTIFY to each process's LISTEN session. The
        // relays are independent; neither depends on where an Orleans grain activation landed.
        replicaA.Announce(notification);
        replicaB.Announce(notification);

        seenA.Items.Select(e => (e.Path, e.Version)).Should().Equal((Path, 31L));
        seenB.Items.Select(e => (e.Path, e.Version)).Should().Equal((Path, 31L));
        logicalA.Items.Should().BeEmpty("a storage listener copy is cache invalidation, not a logical event");
        logicalB.Items.Should().BeEmpty("a storage listener copy is cache invalidation, not a logical event");
    }

    [Fact]
    public void ANeverAnsweringRead_IsBounded_ByThePathOnlyFallback()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable) { NeverRead = true };
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var relay = new StorageChangeFeedRelay(
            adapter, received.Add, legacyReadTimeout: TimeSpan.FromTicks(10), scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        adapter.ReadCalls.Should().Be(1);
        received.Items.Should().BeEmpty("the read is inside its budget");

        scheduler.AdvanceBy(11);
        received.Items.Select(e => (e.Path, e.Version)).Should().Equal(new[] { (Path, 0L) },
            "a silent read is bounded: the path is invalidated without node or version");
    }

    [Fact]
    public void AHintedNotificationDuringAnInFlightRead_OvertakesIt_AndIsNeverHeldBehindIt()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable) { NeverRead = true };
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var relay = new StorageChangeFeedRelay(
            adapter, received.Add, legacyReadTimeout: TimeSpan.FromTicks(10), scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        adapter.InFlightReads.Should().Be(1, "the hint-less notification's read is in flight");

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow)
        {
            NodeType = "Hosting/Publication",
            Version = 52,
        });

        received.Items.Select(e => (e.Path, e.Version)).Should().Equal(
            new[] { (Path, 52L) },
            "a hinted invalidation is self-contained and is relayed at once — never behind a "
            + "compatibility read that may sit at its bound");
        adapter.InFlightReads.Should().Be(0, "and it overtook that read");
        scheduler.AdvanceBy(11);
        received.Items.Select(e => (e.Path, e.Version)).Should().Equal(new[] { (Path, 52L) },
            "the overtaken read's version-zero fallback can never land after the newer event — "
            + "the remote-stream resubscribe gate would read it as 'behind' and resubscribe a "
            + "healthy stream");
    }

    [Fact]
    public void JsonEntityAndStringEnumReadback_BothRecoverMetadata()
    {
        var serialization = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var entity = JsonSerializer.SerializeToElement(
            Node(version: 61, nodeType: "Hosting/Publication"), serialization);
        var factoryNotification = DataChangeNotification.Updated(Path, entity);
        factoryNotification.NodeType.Should().Be("Hosting/Publication");
        factoryNotification.Version.Should().Be(61L);

        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable)
        {
            SerializedRead = JsonSerializer.Serialize(
                Node(version: 62, nodeType: "Hosting/Publication"), serialization),
        };
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var relay = new StorageChangeFeedRelay(adapter, received.Add, scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: entity, DateTimeOffset.UtcNow));
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));

        received.Items.Select(e => (e.NodeType, e.Version)).Should().Equal(
            new[] { ((string?)"Hosting/Publication", 61L) },
            "the JSON entity is self-contained and relayed at once");
        adapter.ReadCalls.Should().Be(0);
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);

        received.Items.Select(e => (e.NodeType, e.Version)).Should().Equal(
            ("Hosting/Publication", 61L),
            ("Hosting/Publication", 62L));
        adapter.ReadCalls.Should().Be(1,
            "the JSON entity is self-contained; only the legacy path-only event needs a read");
    }

    [Fact]
    public void DeleteInvalidation_IsAlwaysVersionless_EvenWhenTheTombstoneCarriesAClock()
    {
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable);
        using var feed = new InProcessMeshChangeFeed(adapter);
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var subscription = ((IMeshInvalidationFeed)feed).Subscribe(received.Add);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Deleted,
            Entity: Node(version: 71, nodeType: "Hosting/Publication"),
            DateTimeOffset.UtcNow)
        {
            NodeType = "Hosting/Publication",
            Version = 71,
        });

        received.Items.Should().ContainSingle();
        received.Items[0].Kind.Should().Be(MeshChangeKind.Deleted);
        received.Items[0].Version.Should().Be(0,
            "remote-stream invalidation treats zero as the unconditional deletion signal");
    }

    private static MeshNode Node(long version, string nodeType) =>
        new("plugins", "Hosting/PlatformBuilds")
        {
            NodeType = nodeType,
            Version = version,
            State = MeshNodeState.Active,
        };

    /// <summary>
    /// Two instances over one durable adapter model two process-local PostgreSQL listener feeds:
    /// reads share the database, notifications do not share process memory.
    /// </summary>
    private sealed class ControllableNotificationAdapter(IStorageAdapter inner)
        : IStorageAdapter, IDisposable
    {
        private readonly Subject<DataChangeNotification> changes = new();

        public Exception? NextReadError { get; set; }
        public bool NeverRead { get; set; }
        public string? SerializedRead { get; set; }
        public int ReadCalls { get; private set; }
        public ImmutableList<string> ReadPaths { get; private set; } = ImmutableList<string>.Empty;
        /// <summary>Reads subscribed and neither completed nor disposed — the overlap instrument.</summary>
        public int InFlightReads { get; private set; }
        public int MaxInFlightReads { get; private set; }
        public IObservable<DataChangeNotification> Changes => changes;

        public void Announce(DataChangeNotification notification) => changes.OnNext(notification);

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => Observable.Defer(() =>
            {
                ReadCalls++;
                ReadPaths = ReadPaths.Add(path);
                InFlightReads++;
                MaxInFlightReads = Math.Max(MaxInFlightReads, InFlightReads);
                return ReadCore(path, options).Finally(() => InFlightReads--);
            });

        private IObservable<MeshNode?> ReadCore(string path, JsonSerializerOptions options)
        {
            if (NextReadError is { } ex)
            {
                NextReadError = null;
                return Observable.Throw<MeshNode?>(ex);
            }
            if (NeverRead)
                return Observable.Never<MeshNode?>();
            if (SerializedRead is { } serialized)
                return Observable.Return(JsonSerializer.Deserialize<MeshNode>(serialized, options));
            return inner.Read(path, options);
        }

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(node, options);

        public IObservable<string> Delete(string path) => inner.Delete(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath) => inner.ListChildPaths(parentPath);

        public IObservable<bool> Exists(string path) => inner.Exists(path);

        public IObservable<object> GetPartitionObjects(
            string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<Unit> SavePartitionObjects(
            string nodePath,
            string? subPath,
            IReadOnlyCollection<object> objects,
            JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(
            string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);

        public void Dispose()
        {
            changes.OnCompleted();
            changes.Dispose();
        }
    }

    private sealed class ImmutableSignal<T>
    {
        private ImmutableList<T> items = ImmutableList<T>.Empty;

        public ImmutableList<T> Items => Volatile.Read(ref items);

        public void Add(T item) => ImmutableInterlocked.Update(
            ref items,
            static (current, next) => current.Add(next),
            item);
    }
}
