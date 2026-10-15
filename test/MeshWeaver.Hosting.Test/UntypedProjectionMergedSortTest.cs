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
/// 🚨 An UNTYPED projection (<c>select:</c> read as <c>Query&lt;object&gt;</c>) is clipped in the
/// order the author asked for when two providers contribute (#6830).
///
/// <para>Each provider used to project its rows to <c>select:</c> dictionaries itself, so the
/// merge received rows it could not read a sort key from: <c>ClipMergedInitial</c> runs the
/// authoritative sort only over <c>MeshNode</c>s and fell back to score + path — and a dictionary
/// has no path, so the window was taken in provider-ARRIVAL order. <c>select:path,name
/// sort:name-desc limit:3</c> over two providers then answered one provider's top three, not the
/// mesh's. The fix runs an untyped query as nodes through the merge and projects AFTER the merged
/// sort and clip.</para>
///
/// <para>The fixture interleaves the two providers so every wrong answer is visible: the names are
/// A–F, the even letters in the storage-backed provider and the odd in the static-node provider, so the global top three by
/// <c>name-desc</c> (<c>F, E, D</c>) takes rows from BOTH, and either provider's own top three is
/// wrong.</para>
/// </summary>
public class UntypedProjectionMergedSortTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(string id, string name) => new(id, "Book")
    {
        Name = name,
        NodeType = "Chapter",
        State = MeshNodeState.Active,
    };

    /// <summary>
    /// The pairing every portal runs: a storage-backed provider and the static-node provider
    /// (seeded nodes, built-in catalogues). The two must carry different provider names — the
    /// fan-out folds providers by name.
    /// </summary>
    private static async Task<IMeshQueryCore> TwoProviders(CancellationToken cancellationToken)
    {
        var store = new InMemoryStorageAdapter();
        // Path order disagrees with name order inside each provider too, so a path-ordered clip is
        // wrong as well.
        foreach (var (id, name) in new[] { ("a1", "F"), ("a2", "D"), ("a3", "B") })
            await store.Write(Node(id, name), Options).Await(cancellationToken);
        var seeded = new StaticMeshNodeListProvider([Node("b1", "E"), Node("b2", "C"), Node("b3", "A")]);
        return new MeshQuery(
            [new StorageAdapterMeshQueryProvider(persistence: store), new StaticNodeQueryProvider([seeded], _ => true)],
            hub: null!);
    }

    private static async Task<IReadOnlyList<T>> Items<T>(
        IMeshQueryCore query, string text, CancellationToken cancellationToken)
    {
        var change = await query.Query<T>(new MeshQueryRequest { Query = text }, Options)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(cancellationToken);
        return change.Items;
    }

    private static string? Field(object row, string key) =>
        row is IReadOnlyDictionary<string, object?> dict && dict.TryGetValue(key, out var value)
            ? value?.ToString()
            : throw new InvalidOperationException($"an untyped select: row is a projection, got {row.GetType().Name}");

    [Theory(Timeout = 60_000)]
    [InlineData("namespace:Book nodeType:Chapter select:path,name sort:name-desc limit:3", "F", "E", "D")]
    [InlineData("namespace:Book nodeType:Chapter select:path,name sort:name limit:3", "A", "B", "C")]
    [InlineData("namespace:Book nodeType:Chapter select:name sort:path-desc limit:2", "A", "C")]
    public async Task AnUntypedProjectionIsClippedInTheRequestedOrder(string text, params string[] expected)
    {
        var query = await TwoProviders(TestContext.Current.CancellationToken);

        var rows = await Items<object>(query, text, TestContext.Current.CancellationToken);

        rows.Select(r => Field(r, "name")).Should().Equal(expected,
            $"'{text}' over two providers answers the mesh's window in the requested order, not one provider's");
    }

    /// <summary>
    /// The projection is still a projection: an untyped <c>select:</c> row carries exactly the
    /// selected fields, so deferring it past the sort did not hand untyped callers whole nodes.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheRowsAreStillProjections()
    {
        var query = await TwoProviders(TestContext.Current.CancellationToken);

        var rows = await Items<object>(query, "namespace:Book nodeType:Chapter select:path,name sort:name-desc limit:3",
            TestContext.Current.CancellationToken);

        rows.Should().OnlyContain(r => r is IReadOnlyDictionary<string, object?>, "an untyped select: row is a projection");
        foreach (var row in rows.Cast<IReadOnlyDictionary<string, object?>>())
            row.Keys.Select(k => k.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal)
                .Should().Equal(new[] { "name", "path" }, "exactly the selected fields");
        rows.Select(r => Field(r, "path")).Should().Equal("Book/a1", "Book/b1", "Book/a2");
    }

    /// <summary>
    /// The control: the TYPED read of the same query was already right (#6704) and must stay so —
    /// the untyped answer now agrees with it row for row.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheUntypedAnswerAgreesWithTheTypedOne()
    {
        var query = await TwoProviders(TestContext.Current.CancellationToken);
        const string text = "namespace:Book nodeType:Chapter select:path,name sort:name-desc limit:3";

        var typed = await Items<MeshNode>(query, text, TestContext.Current.CancellationToken);
        var untyped = await Items<object>(query, text, TestContext.Current.CancellationToken);

        untyped.Select(r => Field(r, "path")).Should().Equal(typed.Select(n => n.Path));
    }

    // ── free text: the provider's relevance survives the merge (#6830 review) ─────────────────

    /// <summary>
    /// Matches for <c>needle</c> whose relevance order is the REVERSE of their path order: the
    /// exact name sits on the last path, the weakest (longest, term buried) on the first.
    /// </summary>
    private static readonly (string Id, string Name)[] Needles =
    [
        ("n1", "a long haystack title that hides the word needle near its end"),
        ("n2", "the needle somewhere"),
        ("n3", "needle"),
    ];

    private static async Task<MeshQuery> OneStorageProvider(CancellationToken cancellationToken)
    {
        var store = new InMemoryStorageAdapter();
        foreach (var (id, name) in Needles)
            await store.Write(Node(id, name), Options).Await(cancellationToken);
        return new MeshQuery([new StorageAdapterMeshQueryProvider(persistence: store)], hub: null!);
    }

    /// <summary>
    /// The relevance order, from the very scorer the provider ranks by — never hand-guessed — and
    /// asserted to DISAGREE with path order, so the test cannot pass by a path-ordered answer.
    /// </summary>
    private static string[] RelevanceOrder()
    {
        var evaluator = new QueryEvaluator();
        var ranked = Needles
            .Select(n => (n.Name, Path: $"Book/{n.Id}", Score: evaluator.GetFuzzyScore(Node(n.Id, n.Name), "needle")))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .Select(r => r.Name)
            .ToArray();
        ranked.SequenceEqual(Needles.Select(n => n.Name)).Should().BeFalse(
            "the fixture is built so relevance order and path order disagree");
        return ranked;
    }

    public static TheoryData<string> FreeTextQueries => new()
    {
        "namespace:Book needle select:path,name limit:3",
        "namespace:Book needle limit:3",
    };

    /// <summary>
    /// The core entry point (<see cref="IMeshQueryCore"/>), typed and untyped, with and without a
    /// <c>select:</c>: a free-text read with no <c>sort:</c> answers best match first.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [MemberData(nameof(FreeTextQueries))]
    public async Task AFreeTextReadKeepsTheProvidersRelevanceOrder_Core(string text)
    {
        var ct = TestContext.Current.CancellationToken;
        var query = (IMeshQueryCore)await OneStorageProvider(ct);
        var expected = RelevanceOrder();

        (await Items<MeshNode>(query, text, ct)).Select(n => n.Name).Should().Equal(expected,
            "a typed free-text read is ranked by relevance, not by path");
        (await Items<object>(query, text, ct)).Select(r => r is MeshNode n ? n.Name : Field(r, "name"))
            .Should().Equal(expected, "an untyped free-text read is ranked by relevance, not by path");
    }

    /// <summary>
    /// The public entry point (<see cref="MeshQuery.Query{T}(MeshQueryRequest)"/>) merges the SECURED
    /// provider surface (<see cref="IMeshQueryProvider.Query{T}"/>) through the same
    /// <c>ClipMergedInitial</c> as the core entry — and resolves its serializer options from a hub,
    /// so it is pinned here at the frame it consumes: that surface's Initial carries one score per
    /// row, in relevance order, for typed and untyped reads alike. Without them the merge scores
    /// every row 0 and answers path order.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [MemberData(nameof(FreeTextQueries))]
    public async Task AFreeTextReadKeepsTheProvidersRelevanceOrder_Public(string text)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryStorageAdapter();
        foreach (var (id, name) in Needles)
            await store.Write(Node(id, name), Options).Await(ct);
        IMeshQueryProvider provider = new StorageAdapterMeshQueryProvider(persistence: store);
        var expected = RelevanceOrder();

        async Task<QueryResultChange<T>> Initial<T>() =>
            await provider.Query<T>(new MeshQueryRequest { Query = text }, Options)
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence)
                .Await(ct);

        var typed = await Initial<MeshNode>();
        typed.Items.Select(n => n.Name).Should().Equal(expected, "the provider ranks by relevance");
        typed.Scores.Should().NotBeNull("the rank the provider clipped by must reach the merge");
        typed.Scores!.Should().HaveCount(typed.Items.Count).And.BeInDescendingOrder();
        typed.Scores!.Distinct().Should().HaveCount(typed.Scores!.Count,
            "the fixture's matches differ in relevance, so path order cannot satisfy the merge");

        var untyped = await Initial<object>();
        untyped.Scores.Should().NotBeNull("an untyped read reports its rank too");
        untyped.Scores!.Should().Equal(typed.Scores, "the score is read off the matched node, before any projection");
    }

    /// <summary>
    /// <c>Skip</c> pages the RELEVANCE order: the second-best match is page two of size one.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SkipPagesTheRelevanceOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = (IMeshQueryCore)await OneStorageProvider(ct);
        var expected = RelevanceOrder();

        var change = await query.Query<object>(
                new MeshQueryRequest { Query = "namespace:Book needle select:path,name", Skip = 1, Limit = 1 }, Options)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

        change.Items.Select(r => Field(r, "name")).Should().Equal(new[] { expected[1] },
            "page two of the relevance order, not of the path order");
    }
}
