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
/// 🚨 <b>A capped filter-only result keeps the NEWEST rows</b> (MeshWeaver #4950).
///
/// <para>A query with no <c>sort:</c> and no free-text term has no relevance signal, so before
/// <see cref="ParsedQuery.EffectiveOrderBy"/> each clip site took its window over whatever order
/// the backend happened to enumerate — path-alphabetical on the in-memory walk, heap order on
/// Postgres. A reader diagnosing an outage then read the newest row of a truncated page as the
/// newest row there is, and it was six days stale. The fix resolves the default ONCE on the
/// contract and applies it at BOTH clip sites, because the per-provider load cap runs before the
/// merge ever sees a row: a merge-layer sort alone would order the wrong subset.</para>
///
/// <para>The fixture is built so the old behaviour and the new one disagree: the seven rows'
/// path order is the REVERSE of their recency, so a clip over path order keeps the three
/// OLDEST and a clip over the contract keeps the three NEWEST. Every case here failed before the
/// default landed; the controls (an authored <c>sort:</c>, a free-text term) pin what the default
/// must NOT override.</para>
/// </summary>
public class FilterOnlyQueryClipsNewestFirstTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Seven actions whose path order is the reverse of their recency: <c>Roll-01</c> is the
    /// alphabetically first AND the oldest, <c>Roll-07</c> the last and the newest.
    /// </summary>
    private static readonly string[] Actions =
        Enumerable.Range(1, 7).Select(i => $"Roll-{i:00}").ToArray();

    private static MeshNode Node(int index) => new(Actions[index - 1], "Ops/Actions")
    {
        Name = Actions[index - 1],
        NodeType = "InstanceAction",
        State = MeshNodeState.Active,
        LastModified = Epoch.AddMinutes(index),
    };

    private const string FilterOnly = "path:Ops/Actions scope:children nodeType:InstanceAction";

    private static MeshQueryRequest Request(string query, int limit = 3) =>
        new() { Query = query, Limit = limit };

    private static async Task<IReadOnlyList<string>> Names(
        IMeshQueryCore query, MeshQueryRequest request, CancellationToken cancellationToken)
    {
        var change = await query.Query<MeshNode>(request, Options)
            .FirstAsync()
            // Nothing here waits: the in-memory adapter answers synchronously. The bound exists so
            // a regression that wedges the merge fails with a timeout instead of hanging the shard.
            .Timeout(TestTimeouts.Convergence)
            .Await(cancellationToken);
        return change.Items.Select(n => n.Name!).ToList();
    }

    private static async Task<IMeshQueryCore> RealProvider(CancellationToken cancellationToken)
    {
        var store = new InMemoryStorageAdapter();
        await store.Write(new MeshNode("Actions", "Ops") { Name = "Actions", NodeType = "Group", State = MeshNodeState.Active }, Options)
            .Await(cancellationToken);
        foreach (var index in Enumerable.Range(1, 7))
            await store.Write(Node(index), Options).Await(cancellationToken);
        return new MeshQuery([new StorageAdapterMeshQueryProvider(persistence: store)], hub: null!);
    }

    // ── The contract ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("nodeType:InstanceAction")]
    [InlineData("path:Ops/Actions scope:descendants nodeType:InstanceAction limit:25")]
    [InlineData("partitions:all nodeType:InstanceAction")]
    [InlineData("namespace:Ops scope:descendants")]
    public void AFilterOnlyQueryResolvesToNewestFirst(string query)
    {
        var parsed = new QueryParser().Parse(query);

        parsed.OrderBy.Should().BeNull("the author wrote no sort:, and OrderBy keeps meaning what was asked");
        parsed.EffectiveOrderBy.Should().Be(ParsedQuery.DefaultFilterOrdering,
            $"'{query}' has no relevance signal, so the contract orders it newest first");
        ParsedQuery.DefaultFilterOrdering.Property.Should().Be("lastModified",
            "the one key every backend resolves the same way — n.last_modified on Postgres, the node field in memory");
        ParsedQuery.DefaultFilterOrdering.Descending.Should().BeTrue("newest FIRST is the point");
    }

    [Theory]
    [InlineData("nodeType:InstanceAction sort:name", "name", false)]
    [InlineData("nodeType:InstanceAction sort:path-desc", "path", true)]
    [InlineData("laptop nodeType:Story sort:lastModified", "lastModified", false)]
    public void AnAuthoredSortIsTheEffectiveOrdering(string query, string property, bool descending)
    {
        var parsed = new QueryParser().Parse(query);

        parsed.OrderBy.Should().Be(new OrderByClause(property, descending));
        parsed.EffectiveOrderBy.Should().BeSameAs(parsed.OrderBy, "intent always wins, text term or not");
    }

    [Theory]
    [InlineData("laptop nodeType:Story")]
    [InlineData("nodeType:InstanceAction source:activity")]
    [InlineData("nodeType:InstanceAction source:accessed")]
    public void RelevanceAndChangeFeedsKeepTheirOwnRanking(string query)
    {
        var parsed = new QueryParser().Parse(query);

        parsed.OrderBy.Should().BeNull();
        parsed.EffectiveOrderBy.Should().BeNull(
            $"'{query}' is ranked by its provider — relevance for a text term, the joined satellite's recency for a feed — "
            + "and a recency default here would override that ranking at the merge");
    }

    // ── The provider's clip, before the merge ──────────────────────────────────────────────────

    /// <summary>
    /// Through the REAL provider: the load cap is taken over the contract's order, so the three
    /// rows that survive <c>limit:3</c> are the three NEWEST, newest first. Before the default the
    /// same request returned <c>Roll-01, Roll-02, Roll-03</c> — the three OLDEST, in path order.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheProviderClipsAFilterOnlyQueryNewestFirst()
    {
        var query = await RealProvider(TestContext.Current.CancellationToken);

        var page = await Names(query, Request(FilterOnly), TestContext.Current.CancellationToken);

        page.Should().Equal(["Roll-07", "Roll-06", "Roll-05"],
            "the rows a diagnosing reader is asking about are the ones that survive the cap");
    }

    /// <summary>Control: an authored <c>sort:</c> is untouched by the default — path order, oldest first here.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AnAuthoredSortStillWinsAtTheProvider()
    {
        var query = await RealProvider(TestContext.Current.CancellationToken);

        var page = await Names(query, Request($"{FilterOnly} sort:path"), TestContext.Current.CancellationToken);

        page.Should().Equal(["Roll-01", "Roll-02", "Roll-03"], "the author asked for path order");
    }

    /// <summary>
    /// Control: a free-text term makes relevance the ordering. Every name here matches the term
    /// identically, so the fuzzy scores tie and the path tiebreak decides — which is NOT recency.
    /// A default that leaked into the text branch would return <c>Roll-07</c> first.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AFreeTextQueryIsRankedByRelevanceNotRecency()
    {
        var query = await RealProvider(TestContext.Current.CancellationToken);

        var page = await Names(query, Request($"Roll {FilterOnly}"), TestContext.Current.CancellationToken);

        page.Should().Equal(["Roll-01", "Roll-02", "Roll-03"],
            "seven equal relevance scores tie-break on path; recency is not a dimension of a text query");
    }

    /// <summary>Paging over the default is still a partition: three pages of three serve every row once, newest first.</summary>
    [Fact(Timeout = 60_000)]
    public async Task PagesOverTheDefaultPartitionTheSetNewestFirst()
    {
        var query = await RealProvider(TestContext.Current.CancellationToken);

        var served = new List<string>();
        foreach (var skip in new[] { 0, 3, 6 })
            served.AddRange(await Names(query, new MeshQueryRequest { Query = FilterOnly, Skip = skip, Limit = 3 },
                TestContext.Current.CancellationToken));

        served.Should().Equal(Actions.Reverse(), "3 + 3 + 1 rows, each once, newest first");
    }

    // ── The merge's clip ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The merge layer's half, isolated: two providers, each handing back its rows in the order
    /// that would keep the OLDEST if the merge clipped over arrival order. The window is taken
    /// over the contract instead.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheMergeClipsAFilterOnlyQueryNewestFirst()
    {
        var oldestFirst = new CannedProvider("oldest-first", Enumerable.Range(1, 4).Select(Node).ToArray());
        var alsoOldestFirst = new CannedProvider("also-oldest-first", Enumerable.Range(5, 3).Select(Node).ToArray());
        var query = (IMeshQueryCore)new MeshQuery([oldestFirst, alsoOldestFirst], hub: null!);

        var page = await Names(query, Request(FilterOnly), TestContext.Current.CancellationToken);

        page.Should().Equal(["Roll-07", "Roll-06", "Roll-05"],
            "two providers' rows are one set, clipped over the same order each provider clipped over");
    }

    /// <summary>A provider that answers every query with the same canned rows, in the given order, unscored.</summary>
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
}
