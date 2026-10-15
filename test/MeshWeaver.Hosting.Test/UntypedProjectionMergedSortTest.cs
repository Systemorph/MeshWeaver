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

}
