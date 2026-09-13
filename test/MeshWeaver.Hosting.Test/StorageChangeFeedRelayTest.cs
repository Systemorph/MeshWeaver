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
/// <para>The compatibility read a hint-less notification owes goes through
/// <see cref="ReReadCoalescing"/>, so these tests drive it on a <see cref="TestScheduler"/>: a
/// hinted or entity-bearing notification is relayed synchronously on <c>Announce</c>, a hint-less
/// one is relayed once the path has been quiet for <see cref="ReReadCoalescing.Window"/>.</para>
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
    /// The one reordering the two lanes can produce on a path: a hint-less create/update whose
    /// coalesced read runs AFTER a delete that was relayed at once. The read finds no row, and the
    /// relay must then say nothing — the delete already invalidated the path, and a
    /// <c>Created</c>/<c>Updated</c> carrying no node and no version after it is a retype to
    /// "(none)" as far as <c>NodeTypeRebindWatcher.RequiresRebind</c> can tell (a recycle of a
    /// hub the delete is already tearing down, with a misleading reason). The read itself is
    /// the positive control: it ran, and found nothing.
    /// </summary>
    [Fact]
    public async Task AnEntitylessCreateFollowedByADeleteOnOnePath_NeverResurrectsIt()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        await durable.Write(Node(version: 1, nodeType: "Hosting/Publication"), json).Should().Emit();
        using var adapter = new ControllableNotificationAdapter(durable);
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var relay = new StorageChangeFeedRelay(adapter, received.Add, scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Created, Entity: null, DateTimeOffset.UtcNow));
        await durable.Delete(Path).Should().Emit();
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Deleted, Entity: null, DateTimeOffset.UtcNow));

        received.Items.Select(e => (e.Kind, e.Version)).Should().Equal(
            new[] { (MeshChangeKind.Deleted, 0L) },
            "a delete is self-contained and relayed at once, ahead of the coalesced read");
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);

        adapter.ReadCalls.Should().Be(1, "the create's coalesced read ran — and found no row");
        received.Items.Select(e => (e.Kind, e.Version)).Should().Equal(
            new[] { (MeshChangeKind.Deleted, 0L) },
            "a row the read cannot find was deleted, and the delete already invalidated the path: "
            + "a Created after it, carrying no node and no version, would read as a retype to "
            + "'(none)' to the rebind watcher");
    }

    /// <summary>
    /// Reads on one path are serialised even when a burst lands while the previous read is still
    /// in flight: the second read queues behind the first instead of racing it, so a slow answer
    /// for an older commit can never be published after the answer for a newer one.
    /// </summary>
    [Fact]
    public void ABurstDuringAnInFlightRead_QueuesBehindIt_AndNeverRacesIt()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable) { NeverRead = true };
        var received = new ImmutableSignal<MeshChangeEvent>();
        // A read bound LONGER than the quiet window: the second burst's window closes while the
        // first read is still inside its bound, which is the only way two reads could overlap.
        var readBound = ReReadCoalescing.Window * 2;
        using var relay = new StorageChangeFeedRelay(
            adapter, received.Add, legacyReadTimeout: readBound, scheduler: scheduler);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        adapter.ReadCalls.Should().Be(1, "the first burst's read is in flight");

        // The next notification arrives while that read has not answered, and its own quiet
        // window closes while the first read is still in flight.
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        adapter.ReadCalls.Should().Be(1,
            "the second read is queued behind the in-flight one, not started beside it");
        received.Items.Should().BeEmpty();

        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        received.Items.Select(e => e.Version).Should().Equal(new[] { 0L },
            "the first read hit its bound and resolved to a path-only invalidation");
        adapter.ReadCalls.Should().Be(2, "only now does the queued read start");
        scheduler.AdvanceBy(readBound.Ticks);
        received.Items.Select(e => e.Version).Should().Equal(0L, 0L);
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
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow)
        {
            NodeType = "Hosting/Publication",
            Version = 21,
        });

        received.Items.Select(e => (e.Path, e.Version, e.NodeType)).Should().Equal(
            new[] { (Path, 21L, (string?)"Hosting/Publication") },
            "a hinted notification needs no read and is never held behind a coalescing window");
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        received.Items.Select(e => (e.Path, e.Version, e.NodeType)).Should().Equal(
            (Path, 21L, "Hosting/Publication"),
            (Path, 0L, (string?)null));

        // The fault did not terminate the path's serialised read queue: the next legacy
        // notification reads, and reads the row.
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        received.Items.Select(e => (e.Version, e.NodeType)).Should().Equal(
            (21L, "Hosting/Publication"),
            (0L, (string?)null),
            (20L, "Hosting/Publication"));
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
    public void NeverAnsweringLegacyRead_IsBounded_AndCannotBlockTheNextHintedInvalidation()
    {
        var scheduler = new TestScheduler();
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable) { NeverRead = true };
        var received = new ImmutableSignal<MeshChangeEvent>();
        using var relay = new StorageChangeFeedRelay(
            adapter, received.Add, legacyReadTimeout: TimeSpan.FromTicks(10), scheduler: scheduler);

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
            "a hinted invalidation is self-contained and is relayed at once — never behind a "
            + "compatibility read that may sit at its bound");
        scheduler.AdvanceBy(ReReadCoalescing.Window.Ticks);
        adapter.ReadCalls.Should().Be(1);
        scheduler.AdvanceBy(11);

        received.Items.Select(e => (e.Path, e.Version)).Should().Equal(
            (Path, 52L),
            (Path, 0L));
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
        public IObservable<DataChangeNotification> Changes => changes;

        public void Announce(DataChangeNotification notification) => changes.OnNext(notification);

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        {
            ReadCalls++;
            ReadPaths = ReadPaths.Add(path);
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
