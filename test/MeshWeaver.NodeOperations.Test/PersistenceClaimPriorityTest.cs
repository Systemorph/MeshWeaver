using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Pins the claim-precedence contract of <see cref="PersistenceService"/>:
/// within the wildcard band, a DURABLE provider (Priority 100) claims writes
/// ahead of the in-memory catch-all (Priority 0) REGARDLESS of registration
/// order. Without the priority sort, registration order decided — and
/// <c>AddOrleansMeshServices</c> registers the in-memory wildcard before the
/// host wires its real backend, so every node write on an Orleans portal was
/// silently persisted into RAM (the 2026-06-11 atioz create-loss: every MCP
/// create acked "Created: …", zero rows in Postgres, gone on pod restart).
/// </summary>
public class PersistenceClaimPriorityTest
{
    private sealed class FakeDurableProvider : IPartitionStorageProvider
    {
        public string Name => "FakeDurable";
        public bool IsReadOnly => false;
        public int Priority => 100;
        public IStorageAdapter Adapter { get; }
        public ImmutableHashSet<string> Contexts => [];
        public bool Claimed { get; private set; }

        public FakeDurableProvider()
            => Adapter = new ClaimingAdapter(() => Claimed = true);
    }

    private sealed class ClaimingAdapter(Action onWrite) : IStorageAdapter
    {
        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => Observable.Return<MeshNode?>(null);
        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        {
            onWrite();
            return Observable.Return<MeshNode?>(node);
        }
        public IObservable<string> Delete(string path) => Observable.Return(path);
        public IObservable<bool> Exists(string path) => Observable.Return(false);
        public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
            string fullPath, JsonSerializerOptions options)
            => Observable.Return<(MeshNode?, int)>((null, 0));
        public IObservable<(System.Collections.Generic.IEnumerable<string> NodePaths,
            System.Collections.Generic.IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
            => Observable.Return<(System.Collections.Generic.IEnumerable<string>,
                System.Collections.Generic.IEnumerable<string>)>(([], []));
        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => Observable.Empty<object>();
        public IObservable<System.Reactive.Unit> SavePartitionObjects(
            string nodePath, string? subPath, System.Collections.Generic.IReadOnlyCollection<object> objects,
            JsonSerializerOptions options)
            => Observable.Return(System.Reactive.Unit.Default);
        public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => Observable.Return(System.Reactive.Unit.Default);
        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => Observable.Return<DateTimeOffset?>(null);
        public IObservable<DataChangeNotification> Changes => Observable.Never<DataChangeNotification>();
    }

    [Fact]
    public void DurableProvider_ClaimsWrites_AheadOfEarlierRegisteredInMemoryWildcard()
    {
        // Registration order reproduces the Orleans-portal wiring: the in-memory
        // catch-all FIRST (AddOrleansMeshServices baseline), the durable backend
        // SECOND (host's AddPartitioned*Persistence).
        var inMemory = new InMemoryPartitionStorageProvider(new InMemoryStorageAdapter(null));
        var durable = new FakeDurableProvider();
        var service = new PersistenceService([inMemory, durable]);

        var node = new MeshNode("Probe", "SomeSpace") { Name = "Probe", NodeType = "Markdown" };
        var saved = service.Write(node, new JsonSerializerOptions())
            .Should().Within(10.Seconds()).Emit();

        saved.Should().NotBeNull();
        durable.Claimed.Should().BeTrue(
            "the durable backend (Priority 100) must claim the write ahead of the in-memory wildcard (Priority 0), " +
            "regardless of registration order — otherwise every write on an Orleans portal lands in RAM");

        // And the in-memory store must NOT hold the node (it never got asked).
        inMemory.Adapter.Read(node.Path, new JsonSerializerOptions())
            .Should().Within(10.Seconds()).Emit()
            .Should().BeNull("the write must not have been claimed by the in-memory wildcard");
    }
}
