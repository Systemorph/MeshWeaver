using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <see cref="StorageAdapterMeshQueryProvider"/> answers EVERY exact-path probe of a multi-query
/// request with ONE batched read, never one read per query (MeshWeaver#5315 / #1186).
///
/// <para><b>The defect.</b> Each query of a request issued its own <c>ReadMany</c>, and the queries
/// run one after the other, so a request of N exact <c>path:</c> queries cost N SEQUENTIAL storage
/// round-trips. On Postgres each of those waits its turn on the process-wide <c>pg-read:</c> pool
/// first. The Store's standard-pack reconcile asks one such request per viewer — the entitlement
/// marker and the grant of every free pack, two <c>path:</c> queries per pack, about thirty on
/// memex-cloud — and on 2026-09-24 that request ran past the query fan-in's 15 s Initial budget
/// twelve times on one pod, reported as
/// <c>path:AppleMaps/_Entitlements/{user} select:path</c> (the request's FIRST query).</para>
///
/// <para><b>What is pinned.</b> The storage adapter counts <c>ReadMany</c> calls. One request of
/// <see cref="Plugins"/> × 2 exact queries must reach the store ONCE, carrying every path, and the
/// Initial must still hold exactly the records that exist — the <c>_Access</c> grants included, which
/// the provider used to drop by judging every row against the request's FIRST query. Negative control
/// (the unfixed provider): one <c>ReadMany</c> per query, and no grant in the Initial.</para>
/// </summary>
public class ExactProbeBatchingTest
{
    private static readonly JsonSerializerOptions Options = new();
    private const string Viewer = "goedel";
    private const int Plugins = 15;

    /// <summary>Delegates to a real <see cref="InMemoryStorageAdapter"/> and counts batched reads.</summary>
    private sealed class CountingAdapter(InMemoryStorageAdapter inner) : IStorageAdapter
    {
        private int readManyCalls;
        private int pathsRequested;

        public int ReadManyCalls => Volatile.Read(ref readManyCalls);
        public int PathsRequested => Volatile.Read(ref pathsRequested);

        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => Observable.Defer(() =>
            {
                Interlocked.Increment(ref readManyCalls);
                Interlocked.Add(ref pathsRequested, paths.Count);
                return ((IStorageAdapter)inner).ReadMany(paths, options);
            });

        public IObservable<DataChangeNotification> Changes => inner.Changes;
        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath) => inner.ListChildPaths(parentPath);
        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => inner.Read(path, options);
        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(node, options);
        public IObservable<string> Delete(string path) => inner.Delete(path);
        public IObservable<bool> Exists(string path) => inner.Exists(path);
        public IObservable<object> GetPartitionObjects(
            string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);
        public IObservable<Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);
        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);
        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }

    private static MeshNode Node(string path, string nodeType)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(path[(slash + 1)..], path[..slash]) { NodeType = nodeType };
    }

    private static string Marker(int plugin) => $"Pack{plugin:D2}/_Entitlements/{Viewer}";
    private static string Grant(int plugin) => $"Pack{plugin:D2}/_Access/{Viewer}_Access";

    private static async Task<QueryResultChange<MeshNode>> FirstFrame(
        StorageAdapterMeshQueryProvider provider, MeshQueryRequest request, CancellationToken ct) =>
        await ((IMeshQueryCore)new MeshQuery([provider], hub: null!))
            .Query<MeshNode>(request, Options)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    [Fact(Timeout = 60_000)]
    public async Task ARequestOfManyExactPathQueries_ReachesTheStoreOnce_AndAnswersEveryRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryStorageAdapter();
        // The viewer holds the marker of every third pack and the grant of every fifth.
        var present = new List<string>();
        for (var i = 0; i < Plugins; i++)
        {
            if (i % 3 == 0)
            {
                await store.Write(Node(Marker(i), "Entitlement"), Options).Await(ct);
                present.Add(Marker(i));
            }
            if (i % 5 == 0)
            {
                await store.Write(Node(Grant(i), "AccessAssignment"), Options).Await(ct);
                present.Add(Grant(i));
            }
        }

        var adapter = new CountingAdapter(store);
        var provider = new StorageAdapterMeshQueryProvider(adapter);
        var queries = Enumerable.Range(0, Plugins)
            .SelectMany(i => new[] { $"path:{Marker(i)} select:path", $"path:{Grant(i)} select:path" })
            .ToList();

        var frame = await FirstFrame(provider, MeshQueryRequest.FromQueries(queries, "system-security"), ct);

        adapter.ReadManyCalls.Should().Be(1,
            $"{queries.Count} exact-path queries in ONE request must reach the store as ONE batched read — "
            + "one read per query, run one after the other, is what ran the Store's entitlement prefetch "
            + "past the fan-in's 15 s Initial budget");
        adapter.PathsRequested.Should().Be(queries.Count, "the one read must carry every probed path");
        // Per-query exclusion: the grants are `_Access` satellite rows, asked for by the request's
        // SECOND half. Judged against the first query alone they were dropped.
        frame.Items.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            present.OrderBy(p => p, StringComparer.Ordinal),
            "batching the reads must not change the answer: every record that exists, and nothing else");
    }

    /// <summary>
    /// Each query keeps its OWN filter over the shared read: a path read on behalf of one query is
    /// admitted only if THAT query's conditions hold. Without per-query attribution the batch would
    /// hand a node to the union because some other query happened to probe it too.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task EachQueryFiltersTheSharedReadByItsOwnConditions()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryStorageAdapter();
        await store.Write(Node("Acme/Alpha", "Story"), Options).Await(ct);
        await store.Write(Node("Acme/Beta", "Task"), Options).Await(ct);

        var adapter = new CountingAdapter(store);
        var provider = new StorageAdapterMeshQueryProvider(adapter);

        var frame = await FirstFrame(provider, MeshQueryRequest.FromQueries(
            ["path:Acme/Alpha nodeType:Task", "path:Acme/Beta nodeType:Task"], "system-security"), ct);

        frame.Items.Select(n => n.Path).Should().Equal(["Acme/Beta"],
            "Acme/Alpha is a Story and the only query that probed it asked for Tasks");
        adapter.ReadManyCalls.Should().Be(1, "both probes share the request's one read");
    }
}
