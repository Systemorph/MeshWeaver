using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>Skip/Limit is paging only over a TOTAL order.</b> Three page queries are three
/// independent evaluations, so unless each one sorts the matched set the same way, page 2 can
/// re-serve a row page 1 already returned while dropping one nobody ever sees — with every page
/// COUNT correct, which is why it reads as a data bug rather than an ordering one.
///
/// <para><b>The order that was missing.</b> Neither clip site defined one.
/// <c>StorageAdapterMeshQueryProvider</c> left <c>sorted = matchedNodes</c> whenever the query
/// carried no <c>sort:</c> and no free text, then clipped to <c>Skip + Limit</c>;
/// <c>MeshQuery.ClipMergedInitial</c> sorted by score and documented "insertion order as the final
/// tiebreaker". Insertion order is not an order: the scope walk reads each path with
/// <c>SelectMany(path =&gt; persistence.Read(path))</c>, which MERGES, so rows arrive in the order
/// the pooled reads COMPLETE. Idle that is the walk order and three queries agree; under load they
/// interleave differently every time.</para>
///
/// <para><b>What these tests do about the load.</b> Nothing — they remove it. Both drive a source
/// that reorders DETERMINISTICALLY, once per query, which is the same input the runner produces by
/// accident. There is no timing, no repetition and no CPU pressure here, so neither test can flake
/// and neither can go green by getting lucky. Measured against the real flake
/// (MeshWeaver.Plugins#1135, <c>HierarchicalBrowsingSuite</c> "Skip and Limit paginate without
/// overlap"): 11/120 failures under 36 CPU burners, 0/120 idle, 0/120 under the same 36 burners
/// once the total order landed.</para>
/// </summary>
public class PagedQueryTotalOrderTest
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>The seven story paths of the suite this reduces, in creation order.</summary>
    private static readonly string[] Stories =
    [
        "Marketing/ClaimsProcessing",
        "Marketing/DataIngestionStrategy",
        "Marketing/ClaimsProcessing/EmailTriage",
        "Marketing/ClaimsProcessing/DocumentExtraction",
        "Marketing/ClaimsProcessing/ClientCorrespondence",
        "Marketing/DataIngestionStrategy/AnnotatedDataModel",
        "Marketing/DataIngestionStrategy/HistoricIngestion",
    ];

    private static MeshNode Node(string path, string nodeType = "Markdown") => new(
        path.Split('/').Last(),
        path.Contains('/') ? path[..path.LastIndexOf('/')] : null)
    {
        Name = path.Split('/').Last(),
        NodeType = nodeType,
        State = MeshNodeState.Active,
    };

    private const string All = "path:Marketing nodeType:Markdown scope:descendants";

    /// <summary>Page N of <see cref="All"/>, three rows at a time — the suite's own request shape.</summary>
    private static MeshQueryRequest Page(int skip) =>
        new() { Query = All, Skip = skip, Limit = 3 };

    private static async Task<IReadOnlyList<string>> Paths(IMeshQueryCore query, MeshQueryRequest request)
    {
        var change = await query.Query<MeshNode>(request, Options)
            .FirstAsync()
            // TestTimeouts, not a hand-written literal: nothing here is expected to WAIT — the
            // fake provider answers synchronously and the in-memory adapter's reads are Defer'd —
            // so this bound exists only so a regression that wedges the merge fails with a
            // timeout instead of hanging the shard.
            .Timeout(TestTimeouts.Convergence)
            .Await();
        return change.Items.Select(n => n.Path!).ToList();
    }

    /// <summary>
    /// The end-to-end claim, through the REAL provider: the three pages partition the result set
    /// even though the storage walk hands them the seven paths in a different rotation each time.
    ///
    /// <para>The rotation is what makes this test deterministic AND what makes it a fair stand-in:
    /// the provider clips to <c>Skip + Limit</c> BEFORE the merge sees a row, so page 1 is
    /// "whatever three the walk yielded first". A merge-layer sort cannot repair that — only an
    /// order applied before the load cap can, which is the half of the fix this test covers.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ThreePages_OverAWalkThatReordersEachTime_PartitionTheResultSet()
    {
        var store = new InMemoryStorageAdapter();
        foreach (var path in Stories.Prepend("Marketing"))
            await store.Write(Node(path, path == "Marketing" ? "Group" : "Markdown"), Options).Await();

        var rotating = new RotatingWalkStorageAdapter(store);
        var query = (IMeshQueryCore)new MeshQuery(
            [new StorageAdapterMeshQueryProvider(persistence: rotating)], hub: null!);

        var page1 = await Paths(query, Page(0));
        var page2 = await Paths(query, Page(3));
        var page3 = await Paths(query, Page(6));

        rotating.Rotations.Should().BeGreaterThan(1,
            "the point of this test is that the walk order CHANGED between the page queries — if "
            + "it did not, the test proves nothing");

        var served = page1.Concat(page2).Concat(page3).ToList();
        served.Should().HaveCount(7, "3 + 3 + 1 rows must be served in total");
        served.Distinct(StringComparer.Ordinal).Should().HaveCount(7,
            "the pages must not overlap — a repeated path means paging re-served a row while "
            + "another row was never served at all");
        foreach (var story in Stories)
            served.Should().Contain(story,
                "the union of the pages IS the result set — a row served by no page is the other "
                + "half of an overlap");
    }

    /// <summary>
    /// The merge layer's half, isolated: given providers whose Initial arrives in an arbitrary
    /// order, the clip must still be taken over a total order. Two providers, so the emissions
    /// race the way two real backends do; the second one's rows are handed back reversed.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheMerge_ClipsOverATotalOrder_NotOverProviderArrivalOrder()
    {
        var forward = new CannedProvider("forward", Stories.Take(4).Select(p => Node(p)).ToArray());
        var reversed = new CannedProvider("reversed",
            Stories.Skip(4).Reverse().Select(p => Node(p)).ToArray());

        var query = (IMeshQueryCore)new MeshQuery([forward, reversed], hub: null!);

        var page1 = await Paths(query, Page(0));
        var page2 = await Paths(query, Page(3));
        var page3 = await Paths(query, Page(6));

        var served = page1.Concat(page2).Concat(page3).ToList();
        served.Should().HaveCount(7);
        served.Distinct(StringComparer.Ordinal).Should().HaveCount(7,
            "two providers contributing to one result set must still be paged as one ordered set");
        served.Should().ContainInOrder(Stories.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            "read back page by page, the rows come out in the order the paging is defined over");
    }

    /// <summary>A provider that answers every query with the same canned rows, in the given order.</summary>
    private sealed class CannedProvider(string name, MeshNode[] nodes) : IMeshQueryProvider
    {
        public string Name => name;

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => (IObservable<QueryResultChange<T>>)(object)Observable.Return(new QueryResultChange<MeshNode>
            {
                ChangeType = QueryChangeType.Initial,
                Items = nodes,
                Timestamp = DateTimeOffset.UtcNow,
            });

        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }

    /// <summary>
    /// The store, with its walk order ROTATED one position per call — the deterministic stand-in
    /// for the merged, load-sensitive read completion order the real walk emits. Everything else
    /// forwards to the real <see cref="InMemoryStorageAdapter"/>, so the rows, the matching and
    /// the clipping are the production code paths.
    /// </summary>
    private sealed class RotatingWalkStorageAdapter(IStorageAdapter inner) : IStorageAdapter
    {
        private int _rotations;

        /// <summary>How many times the walk order has been rotated — asserted, so a refactor that
        /// stops calling <see cref="ListChildPaths"/> cannot leave this test silently vacuous.</summary>
        public int Rotations => _rotations;

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath)
            => inner.ListChildPaths(parentPath).Select(result =>
            {
                var nodePaths = result.NodePaths.ToList();
                var rotation = System.Threading.Interlocked.Increment(ref _rotations);
                if (nodePaths.Count < 2)
                    return (result.NodePaths, result.DirectoryPaths);
                var offset = rotation % nodePaths.Count;
                return ((IEnumerable<string>)nodePaths.Skip(offset).Concat(nodePaths.Take(offset)).ToList(),
                    result.DirectoryPaths);
            });

        public IObservable<DataChangeNotification> Changes => inner.Changes;

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => inner.Read(path, options);

        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => inner.ReadMany(paths, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(node, options);

        public IObservable<string> Delete(string path) => inner.Delete(path);

        public IObservable<bool> Exists(string path) => inner.Exists(path);

        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }
}
