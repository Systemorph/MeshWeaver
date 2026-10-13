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
/// <c>sort:content.&lt;field&gt;</c> orders by the field's TYPED value on every path a query can take:
/// the in-memory provider (before its load cap), and the merge layer
/// (<c>MeshQuery.ClipMergedInitial</c>) that re-sorts every multi-provider and every Postgres result.
///
/// <para><b>The defect these pin.</b> The merge layer's sort key was the raw CLR value with a
/// fallback of <c>value.ToString()</c>. A content type compiled from a node type carries a date as
/// <see cref="DateOnly"/>, whose invariant <c>ToString()</c> is <c>MM/dd/yyyy</c> — so the merge
/// re-ordered a correctly ordered Postgres page lexically by MONTH: <c>01/01/2027</c> &lt;
/// <c>04/01/2027</c> &lt; <c>07/01/2027</c> &lt; <c>10/01/2026</c>, the 2026 row last ascending and
/// first descending. On the in-memory provider a content selector fell to <c>node.Name</c>, so the
/// load cap kept the wrong rows before any merge saw them.</para>
///
/// <para><b>The contract.</b> Numbers compare numerically; dates, date-times and every other scalar
/// compare as their ISO-8601 / invariant text (which is what Postgres orders, and which is
/// chronological for ISO forms), case-insensitively; numbers rank before text; null or absent
/// sorts LAST in both directions.</para>
/// </summary>
public class ContentFieldSortTest
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>A typed content record, standing in for a node type's compiled content class.</summary>
    private sealed record Submission(DateOnly? Inception, decimal? Premium, Terms? Terms = null);

    /// <summary>Nested content, for <c>content.terms.renewal</c>.</summary>
    private sealed record Terms(DateTime Renewal);

    private static MeshNode Node(string id, object? content) => new(id, "Book")
    {
        Name = id,
        NodeType = "Submission",
        State = MeshNodeState.Active,
        Content = content,
    };

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static async Task<IReadOnlyList<string>> Ids(
        IMeshQueryCore query, MeshQueryRequest request, CancellationToken ct)
    {
        var change = await query.Query<MeshNode>(request, Options)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        return change.Items.Select(n => n.Id).ToList();
    }

    /// <summary>The measured defect, reduced: typed <see cref="DateOnly"/> content through the merge.</summary>
    [Fact(Timeout = 60_000)]
    public async Task TypedDateOnly_SortsChronologically_ThroughTheMerge()
    {
        var nodes = new[]
        {
            Node("jul27", new Submission(new DateOnly(2027, 7, 1), 1m)),
            Node("oct26", new Submission(new DateOnly(2026, 10, 1), 2m)),
            Node("jan27", new Submission(new DateOnly(2027, 1, 1), 3m)),
            Node("apr27", new Submission(new DateOnly(2027, 4, 1), 4m)),
        };
        // Two providers: the shape of a multi-partition union, and of Postgres beside a static one.
        var query = (IMeshQueryCore)new MeshQuery(
            [new CannedProvider("a", nodes[..2]), new CannedProvider("b", nodes[2..])], hub: null!);
        var ct = TestContext.Current.CancellationToken;

        (await Ids(query, new MeshQueryRequest { Query = "path:Book scope:descendants sort:content.inception" }, ct))
            .Should().Equal(["oct26", "jan27", "apr27", "jul27"],
                "a date sorts chronologically — not by the MM/dd/yyyy text DateOnly.ToString() gives");
        (await Ids(query, new MeshQueryRequest { Query = "path:Book scope:descendants sort:content.inception-desc" }, ct))
            .Should().Equal(["jul27", "apr27", "jan27", "oct26"]);
        (await Ids(query, new MeshQueryRequest { Query = "path:Book scope:descendants sort:content.inception-desc", Limit = 2 }, ct))
            .Should().Equal(["jul27", "apr27"], "the sort is applied before the limit");
    }

    /// <summary>Typed and as-written JSON content of the same field order as one sequence.</summary>
    [Fact]
    public void TypedAndJsonDates_InterleaveChronologically()
    {
        var nodes = new[]
        {
            Node("typed-jul27", new Submission(new DateOnly(2027, 7, 1), null)),
            Node("json-oct26", Json("""{"inception":"2026-10-01"}""")),
            Node("typed-jan27", new Submission(new DateOnly(2027, 1, 1), null)),
            Node("json-apr27", Json("""{"inception":"2027-04-01"}""")),
        };

        new QueryEvaluator().OrderResults(nodes, new OrderByClause("content.inception"))
            .Select(n => n.Id)
            .Should().Equal("json-oct26", "typed-jan27", "json-apr27", "typed-jul27");
    }

    /// <summary>Numbers compare as numbers — 9 &lt; 10 &lt; 100 — whether typed or JSON.</summary>
    [Fact]
    public void Numbers_SortNumerically_NotAsText()
    {
        var nodes = new[]
        {
            Node("hundred", Json("""{"premium":100}""")),
            Node("nine", new Submission(null, 9m)),
            Node("ten", Json("""{"premium":10.0}""")),
            Node("ninety", Json("""{"premium":90.5}""")),
        };
        var evaluator = new QueryEvaluator();

        evaluator.OrderResults(nodes, new OrderByClause("content.premium")).Select(n => n.Id)
            .Should().Equal("nine", "ten", "ninety", "hundred");
        evaluator.OrderResults(nodes, new OrderByClause("content.premium", Descending: true)).Select(n => n.Id)
            .Should().Equal("hundred", "ninety", "ten", "nine");
    }

    /// <summary>A null or absent key is last whichever way the sort runs.</summary>
    [Fact]
    public void NullsAndAbsentKeys_SortLast_InBothDirections()
    {
        var nodes = new[]
        {
            Node("absent", Json("""{"other":1}""")),
            Node("b", Json("""{"inception":"2027-01-01"}""")),
            Node("null", Json("""{"inception":null}""")),
            Node("a", Json("""{"inception":"2026-01-01"}""")),
            Node("no-content", null),
        };
        var evaluator = new QueryEvaluator();

        evaluator.OrderResults(nodes, new OrderByClause("content.inception")).Select(n => n.Id).Take(2)
            .Should().Equal("a", "b");
        evaluator.OrderResults(nodes, new OrderByClause("content.inception", Descending: true)).Select(n => n.Id).Take(2)
            .Should().Equal("b", "a");
    }

    /// <summary>A nested selector walks the content: typed and JSON alike.</summary>
    [Fact]
    public void NestedContentSelector_SortsByTheLeafValue()
    {
        var nodes = new[]
        {
            Node("late", new Submission(null, null, new Terms(new DateTime(2028, 3, 1, 0, 0, 0, DateTimeKind.Utc)))),
            Node("early", Json("""{"terms":{"renewal":"2027-02-01T00:00:00.0000000Z"}}""")),
            Node("mid", new Submission(null, null, new Terms(new DateTime(2027, 9, 1, 0, 0, 0, DateTimeKind.Utc)))),
        };

        new QueryEvaluator().OrderResults(nodes, new OrderByClause("content.terms.renewal")).Select(n => n.Id)
            .Should().Equal("early", "mid", "late");
    }

    /// <summary>Text compares case-insensitively.</summary>
    [Fact]
    public void Text_SortsCaseInsensitively()
    {
        var nodes = new[]
        {
            Node("B", Json("""{"status":"bound"}""")),
            Node("Q", Json("""{"status":"Quoted"}""")),
            Node("A", Json("""{"status":"Accepted"}""")),
        };

        new QueryEvaluator().OrderResults(nodes, new OrderByClause("content.status")).Select(n => n.Id)
            .Should().Equal("A", "B", "Q");
    }

    /// <summary>
    /// The in-memory provider sorts BEFORE its load cap, on the content field — not on the name it
    /// used to fall back to for every selector it did not know.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task InMemoryProvider_SortsByTheContentFieldBeforeTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryStorageAdapter();
        await store.Write(new MeshNode("Book") { Name = "Book", NodeType = "Group", State = MeshNodeState.Active }, Options).Await(ct);
        // Names run opposite to premium, so a name sort would pick exactly the wrong two.
        foreach (var (id, premium) in new[] { ("a", 100), ("b", 50), ("c", 10), ("d", 9) })
            await store.Write(Node(id, Json($$"""{"premium":{{premium}}}""")), Options).Await(ct);

        var query = (IMeshQueryCore)new MeshQuery(
            [new StorageAdapterMeshQueryProvider(persistence: store)], hub: null!);

        (await Ids(query, new MeshQueryRequest
            {
                Query = "path:Book nodeType:Submission scope:descendants sort:content.premium-desc",
                Limit = 2,
            }, ct))
            .Should().Equal(["a", "b"], "the top two by premium, 100 and 50 — a name sort would keep d and c");
        (await Ids(query, new MeshQueryRequest
            {
                Query = "path:Book nodeType:Submission scope:descendants sort:content.premium",
                Limit = 2,
            }, ct))
            .Should().Equal(["d", "c"], "9 before 10 — numeric, not text, and not by name");
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
}
