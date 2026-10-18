using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <c>sort:subtreeLastModified</c> orders CONTAINERS by the newest change anywhere below them
/// (<see cref="SubtreeSort"/>) — the "last activity" a client roster is sorted by.
///
/// <para><b>The defect these pin.</b> <c>sort:lastModified-desc</c> keys a container on its OWN
/// stamp, and a client partition root is stamped by the platform (a retype, a sync), not by the
/// work done under it: measured on a live portal, a client root read 2026-09-21 while thousands of
/// versions had been written below it since. And any other selector naming the aggregate fell
/// through to a content lookup, keyed every row null, and came back in PATH order. The fixture is
/// built so own-stamp order, path order and subtree order all disagree.</para>
///
/// <para>Every case runs through the REAL in-memory provider (<see cref="StorageAdapterMeshQueryProvider"/>
/// over <see cref="InMemoryStorageAdapter"/>), behind the engine (<see cref="MeshQuery"/>).</para>
/// </summary>
public class SubtreeLastModifiedSortTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static readonly DateTimeOffset Epoch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Day(int day) => Epoch.AddDays(day);

    private static MeshNode Client(string id, DateTimeOffset? stamp) => new(id)
    {
        Name = id,
        NodeType = "Client",
        State = MeshNodeState.Active,
        LastModified = stamp ?? default,
    };

    private static MeshNode Child(string id, string ns, DateTimeOffset stamp) => new(id, ns)
    {
        Name = id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
        LastModified = stamp,
    };

    /// <summary>
    /// Own stamp, path order and subtree order all disagree:
    /// <list type="bullet">
    ///   <item><c>Alpha</c> — root re-stamped on day 30, nothing below touched since day 1. Newest by
    ///     its OWN stamp, oldest by activity.</item>
    ///   <item><c>Beta</c> — root day 1, a child day 20.</item>
    ///   <item><c>Delta</c> — empty, root day 10: ranked by its own stamp, not as never touched.</item>
    ///   <item><c>Gamma</c> — root day 2, a GRANDCHILD day 25: depth counts, not just children.</item>
    /// </list>
    /// </summary>
    private static readonly MeshNode[] Fixture =
    [
        Client("Alpha", Day(30)),
        Child("Notes", "Alpha", Day(1)),
        Client("Beta", Day(1)),
        Child("Deal", "Beta", Day(20)),
        Child("Older", "Beta", Day(3)),
        Client("Delta", Day(10)),
        Client("Gamma", Day(2)),
        Child("Case", "Gamma", Day(4)),
        Child("Offer", "Gamma/Case", Day(25)),
    ];

    private const string Roster = "nodeType:Client scope:subtree partitions:all";

    private static async Task<IMeshQueryCore> Engine(
        IEnumerable<MeshNode> nodes, CancellationToken ct, Func<IMeshQueryProvider, IMeshQueryProvider>? wrap = null,
        MeshOperationOptions? operationOptions = null)
    {
        var store = new InMemoryStorageAdapter();
        foreach (var node in nodes)
            await store.Write(node, Options).Await(ct);
        IMeshQueryProvider provider = new StorageAdapterMeshQueryProvider(persistence: store);
        if (wrap is not null)
            provider = wrap(provider);
        return operationOptions is null
            ? new MeshQuery([provider], hub: null!)
            : new MeshQuery([provider], hub: null!, operationOptions);
    }

    private static async Task<IReadOnlyList<string>> Ids(IMeshQueryCore query, MeshQueryRequest request, CancellationToken ct)
    {
        var change = await query.Query<MeshNode>(request, Options)
            .FirstAsync()
            // The in-memory adapter answers synchronously; the bound only turns a wedge into a failure.
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        return change.Items.Select(n => n.Id).ToList();
    }

    [Fact(Timeout = 60_000)]
    public async Task ContainersOrderByTheirNewestDescendant_NotTheirOwnStamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = await Engine(Fixture, ct);

        (await Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc" }, ct))
            .Should().Equal(["Gamma", "Beta", "Delta", "Alpha"],
                "Gamma's grandchild (day 25) beats Beta's child (day 20); empty Delta ranks by its own day 10; "
                + "Alpha's own day-30 stamp is NOT activity while something below it can be read");
        (await Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified" }, ct))
            .Should().Equal(["Alpha", "Delta", "Beta", "Gamma"], "ascending is the same key reversed");
    }

    /// <summary>Control: <c>sort:lastModified-desc</c> still means the node's OWN stamp.</summary>
    [Fact(Timeout = 60_000)]
    public async Task LastModifiedStillMeansTheOwnStamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = await Engine(Fixture, ct);

        (await Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:lastModified-desc" }, ct))
            .Should().Equal(["Alpha", "Delta", "Gamma", "Beta"]);
    }

    /// <summary>
    /// The window is taken AFTER the keys: a provider clip over any order it can compute (path,
    /// own stamp) would keep the wrong two. Both spellings of the limit, and paging.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheLimitAndSkipApplyAfterTheKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = await Engine(Fixture, ct);

        (await Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc limit:2" }, ct))
            .Should().Equal(["Gamma", "Beta"], "limit:N in the query string clips the ranked set");
        (await Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc", Limit = 2 }, ct))
            .Should().Equal(["Gamma", "Beta"], "so does MeshQueryRequest.Limit");

        var served = new List<string>();
        foreach (var skip in new[] { 0, 2 })
            served.AddRange(await Ids(query,
                new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc", Skip = skip, Limit = 2 }, ct));
        served.Should().Equal(["Gamma", "Beta", "Delta", "Alpha"], "two pages partition the ranked set");
    }

    /// <summary>A projected read (<c>select:</c>) keeps the ranked order — the projection runs after the merge.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AProjectedReadKeepsTheOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = await Engine(Fixture, ct);

        var change = await query.Query<object>(
                new MeshQueryRequest { Query = $"{Roster} select:path,name sort:subtreeLastModified-desc" }, Options)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        change.Items.Select(i => ((IDictionary<string, object?>)i)["name"]).Should()
            .Equal(["Gamma", "Beta", "Delta", "Alpha"]);
    }

    /// <summary>
    /// A container with no stamp of its own and nothing below has no key — LAST in both directions,
    /// like every other <c>sort:</c> selector.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task NoKeySortsLastInBothDirections()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = await Engine([.. Fixture, Client("Unstamped", null)], ct);

        (await Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc" }, ct))
            .Should().Equal(["Gamma", "Beta", "Delta", "Alpha", "Unstamped"]);
        (await Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified" }, ct))
            .Should().Equal(["Alpha", "Delta", "Beta", "Gamma", "Unstamped"]);
    }

    /// <summary>
    /// 🔒 The key never leaks a row the caller cannot read. <c>Beta</c> holds a restricted child
    /// stamped day 28 that only <c>owner</c> may read. For <c>owner</c> Beta leads; for
    /// <c>guest</c> the restricted stamp is invisible, so Beta ranks by the child it CAN read
    /// (day 20) — the order a guest sees says nothing about the row they cannot.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheKeyOnlyConsidersRowsTheCallerCanRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = await Engine([.. Fixture, Child("Restricted", "Beta", Day(28))], ct,
            provider => new ViewerScopedProvider(provider, restrictedPath: "Beta/Restricted", reader: "owner"));

        (await Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc", UserId = "owner" }, ct))
            .Should().Equal(["Beta", "Gamma", "Delta", "Alpha"], "the owner reads Beta's day-28 child");
        (await Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc", UserId = "guest" }, ct))
            .Should().Equal(["Gamma", "Beta", "Delta", "Alpha"],
                "a guest's key reads run as the guest, so the restricted stamp cannot reorder their list");
    }

    /// <summary>Past the declared candidate bound the query FAILS, naming the bound — never a silent subset.</summary>
    [Fact(Timeout = 60_000)]
    public async Task MoreCandidatesThanTheBoundFailLoudly()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = await Engine(Fixture, ct, operationOptions: new MeshOperationOptions { SubtreeSortMaxCandidates = 3 });

        var act = () => Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc" }, ct);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("SubtreeSortMaxCandidates").And.Contain("matched 4");
    }

    [Theory]
    [InlineData("nodeType:Client sort:subtreeLastModified-desc limit:5", "nodeType:Client limit:5")]
    [InlineData("sort:SubtreeLastModified nodeType:Client", "nodeType:Client")]
    [InlineData("nodeType:Client sort:name", "nodeType:Client sort:name")]
    [InlineData("nodeType:Client sort:subtreeLastModifiedX", "nodeType:Client sort:subtreeLastModifiedX")]
    public void OnlyTheSubtreeSortTokenIsStripped(string query, string expected)
        => SubtreeSort.WithoutSort(query).Should().Be(expected);

    [Theory]
    [InlineData("nodeType:Client sort:subtreeLastModified-desc", true)]
    [InlineData("nodeType:Client sort:SUBTREELASTMODIFIED", true)]
    [InlineData("nodeType:Client sort:lastModified-desc", false)]
    public void TheSelectorParsesWithoutAParserChange(string query, bool applies)
        => SubtreeSort.Applies(new QueryParser().Parse(query).OrderBy).Should().Be(applies);

    /// <summary>
    /// Stands in for a backend's row-level security: hides one path from everyone but
    /// <paramref name="reader"/>, deciding per request by <see cref="MeshQueryRequest.UserId"/> —
    /// which is exactly what every real provider decides it by.
    /// </summary>
    private sealed class ViewerScopedProvider(IMeshQueryProvider inner, string restrictedPath, string reader) : IMeshQueryProvider
    {
        public string Name => inner.Name;

        public bool Matches(IReadOnlyList<string> queryNamespaces) => inner.Matches(queryNamespaces);

        // Filtered BEFORE the window, as a backend's row filter is: the inner read runs unclipped,
        // the restricted row is dropped, and only then is the caller's limit taken — otherwise a
        // `limit:1` read would clip to the restricted row and filter down to nothing.
        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            if (string.Equals(request.UserId, reader, StringComparison.Ordinal))
                return inner.Query<T>(request, options);
            var limit = request.Limit ?? new QueryParser().Parse(request.EffectiveQueries[0]).Limit;
            return inner.Query<T>(request with { Limit = MeshQueryRequest.NoLimit }, options).Select(change =>
            {
                var visible = change.Items
                    .Where(i => i is not MeshNode n || !string.Equals(n.Path, restrictedPath, StringComparison.Ordinal));
                return change with
                {
                    Items = (limit is > 0 ? visible.Take(limit.Value) : visible).ToList(),
                    Scores = null,
                };
            });
        }

        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => inner.Query(request, options);

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => inner.Autocomplete(basePath, prefix, options, mode, limit, contextPath, context);

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => inner.Select<T>(path, property, options);
    }
}
