#pragma warning disable CS1591

using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the process-local half of storage-backed cache invalidation. In production every portal
/// replica owns a PostgreSQL LISTEN session and therefore its own <see cref="IStorageAdapter.Changes"/>
/// feed. Each feed must reach that replica's <see cref="InProcessMeshChangeFeed"/> directly; one
/// Orleans grain activation cannot deliver into the process memory of every silo.
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

        var logical = new List<MeshChangeEvent>();
        var invalidated = new List<MeshChangeEvent>();
        using var logicalSubscription = contract.Subscribe(logical.Add);
        using var faultingInvalidator = invalidations.Subscribe(_ =>
            throw new InvalidOperationException("one broken cache"));
        using var invalidationSubscription = invalidations.Subscribe(invalidated.Add);
        var node = Node(version: 7, nodeType: "Hosting/Publication");

        await provider.GetRequiredService<IStorageAdapter>()
            .Write(node, json)
            .Should().Emit();

        logical.Should().BeEmpty(
            "a durable-store echo must not re-run mail, instance sync or other logical consumers");
        invalidated.Should().ContainSingle();
        invalidated[0].Path.Should().Be(Path);
        invalidated[0].Kind.Should().Be(MeshChangeKind.Updated);
        invalidated[0].NodeType.Should().Be("Hosting/Publication");
        invalidated[0].Version.Should().Be(7);

        contract.Publish(MeshChangeEvent.Updated(Node(version: 8, nodeType: "Hosting/Publication")));

        logical.Select(e => e.Version).Should().Equal(8L);
        invalidated.Select(e => e.Version).Should().Equal(new[] { 7L, 8L },
            "the writer's explicit logical publish must invalidate its own process too");
    }

    [Fact]
    public async Task LegacyCrossProcessNotification_RereadsBeforeTypeFilteringConsumersSeeIt()
    {
        var durable = new InMemoryStorageAdapter();
        var stored = Node(version: 12, nodeType: "Hosting/Publication");
        await durable.Write(stored, json).Should().Emit();
        using var adapter = new ControllableNotificationAdapter(durable);
        using var feed = new InProcessMeshChangeFeed(adapter);
        MeshChangeEvent? received = null;
        using var subscription = ((IMeshInvalidationFeed)feed)
            .Subscribe(change => received = change);
        var committedAt = DateTimeOffset.Parse("2026-09-12T13:43:58Z");

        // The first core image may run briefly with an older PostgreSQL notifier whose payload has
        // path/op but no additive NodeType/Version members. This is the rollout boundary.
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, Timestamp: committedAt));

        received.Should().NotBeNull();
        received!.Path.Should().Be(Path);
        received.NodeType.Should().Be("Hosting/Publication");
        received.Version.Should().Be(12);
        received.Timestamp.Should().Be(committedAt);
        adapter.ReadCalls.Should().Be(1);
    }

    [Fact]
    public async Task OneMetadataReadFailure_DoesNotStopTheReplicaFromReceivingTheNextCommit()
    {
        var durable = new InMemoryStorageAdapter();
        await durable.Write(Node(version: 20, nodeType: "Hosting/Publication"), json)
            .Should().Emit();
        using var adapter = new ControllableNotificationAdapter(durable)
        {
            NextReadError = new InvalidOperationException("transient read fault")
        };
        using var feed = new InProcessMeshChangeFeed(adapter);
        var received = new List<MeshChangeEvent>();
        using var subscription = ((IMeshInvalidationFeed)feed).Subscribe(received.Add);

        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow));
        adapter.Announce(new DataChangeNotification(
            Path, DataChangeKind.Updated, Entity: null, DateTimeOffset.UtcNow)
        {
            NodeType = "Hosting/Publication",
            Version = 21,
        });

        received.Should().ContainSingle();
        received[0].Version.Should().Be(21,
            "a failed legacy-payload reread may drop that event, but must not terminate the relay");
    }

    [Fact]
    public void OneMalformedNotification_DoesNotStopTheReplicaFromReceivingTheNextCommit()
    {
        var durable = new InMemoryStorageAdapter();
        using var adapter = new ControllableNotificationAdapter(durable);
        using var feed = new InProcessMeshChangeFeed(adapter);
        var received = new List<MeshChangeEvent>();
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

        received.Should().ContainSingle();
        received[0].Version.Should().Be(41,
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
        var seenA = new List<MeshChangeEvent>();
        var seenB = new List<MeshChangeEvent>();
        var logicalA = new List<MeshChangeEvent>();
        var logicalB = new List<MeshChangeEvent>();
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

        seenA.Select(e => (e.Path, e.Version)).Should().Equal((Path, 31L));
        seenB.Select(e => (e.Path, e.Version)).Should().Equal((Path, 31L));
        logicalA.Should().BeEmpty("a storage listener copy is cache invalidation, not a logical event");
        logicalB.Should().BeEmpty("a storage listener copy is cache invalidation, not a logical event");
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
        public int ReadCalls { get; private set; }
        public IObservable<DataChangeNotification> Changes => changes;

        public void Announce(DataChangeNotification notification) => changes.OnNext(notification);

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        {
            ReadCalls++;
            if (NextReadError is { } ex)
            {
                NextReadError = null;
                return Observable.Throw<MeshNode?>(ex);
            }
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
}
