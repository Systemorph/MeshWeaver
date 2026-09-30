using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// A live RAW query confined to a set of node types does not re-read its scope for a write of
/// another type (Doc/Architecture/LiveQueryRequeryCost).
///
/// <para><b>The defect.</b> <see cref="StorageAdapterMeshQueryProvider"/> decided whether a change
/// was relevant from its PATH alone, and every relevant change costs a full re-read of the query's
/// scope. The security fold holds three such queries open for the life of the mesh — every
/// partition's <c>nodeType:AccessAssignment</c> and <c>_Policy</c> index and the mesh-wide
/// <c>nodeType:GroupMembership partitions:all scope:subtree</c> — so every write, of anything,
/// re-walked the partition and the whole mesh. One write's cost grew with the size of the mesh:
/// 3.2 ms per write at 150 nodes, 22 ms at 1,200 and 88 ms at 4,500 in a monolith test mesh, and
/// 0.7–1.1 s per node at the end of the plugin gate's install, which put the Hosting package past
/// the installer's 600 s bound and the Fleet Console's live journey past its 10 s step.</para>
///
/// <para><b>How it is measured.</b> The walk is counted, not timed: a pass-through adapter over a
/// real <see cref="InMemoryStorageAdapter"/> (its own change feed, carrying the prior state) counts
/// every listing of the query's base path. A write of an unrelated type must cost no listing; a
/// write that enters, changes or leaves the result must still re-query and reach the subscriber.</para>
/// </summary>
public class LiveQueryIgnoresOtherNodeTypesTest
{
    private static readonly JsonSerializerOptions Options = new();
    private const string Base = "acme";
    private const string Watched = "ProbeWatched";
    private const string Other = "Markdown";
    private const int UnrelatedWrites = 40;

    /// <summary>Pass-through over a real adapter that counts listings of <see cref="Base"/>.</summary>
    private sealed class CountingAdapter(InMemoryStorageAdapter inner) : IStorageAdapter
    {
        private int walks;

        public int Walks => Volatile.Read(ref walks);

        public IObservable<DataChangeNotification> Changes => inner.Changes;

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath)
            => inner.ListChildPaths(parentPath).Do(_ =>
            {
                if (string.Equals(parentPath, Base, StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref walks);
            });

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

    private static MeshNode Node(string id, string nodeType) =>
        new(id, Base) { Name = id, NodeType = nodeType };

    private static IObservable<MeshNode?> Write(IStorageAdapter adapter, MeshNode node) =>
        adapter.Write(node, Options);

    /// <summary>Opens the raw live query and returns every change it emits, replayed.</summary>
    private static (ReplaySubject<QueryResultChange<MeshNode>> Changes, IDisposable Subscription) Open(
        StorageAdapterMeshQueryProvider provider)
    {
        var changes = new ReplaySubject<QueryResultChange<MeshNode>>();
        var subscription = ((IMeshQueryCore)provider)
            .Query<MeshNode>(
                MeshQueryRequest.FromQueries(
                    [$"path:{Base} scope:descendants nodeType:{Watched} select:path,id,namespace,name,nodeType,content"],
                    "system-security"),
                Options)
            .Subscribe(changes);
        return (changes, subscription);
    }

    private static IObservable<QueryResultChange<MeshNode>> Carrying(
        IObservable<QueryResultChange<MeshNode>> changes, QueryChangeType type, string id) =>
        changes.Where(c => c.ChangeType == type
            && (c.Items ?? []).Any(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// 🚨 THE REGRESSION. Forty writes of another type under the query's scope cost NO walk; the
    /// one write of the watched type then costs one and reaches the subscriber. Before the fix
    /// every one of the forty re-read the scope.
    /// </summary>
    [Fact]
    public async Task WritesOfAnotherNodeType_UnderTheScope_DoNotReReadIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var adapter = new CountingAdapter(new InMemoryStorageAdapter());
        var provider = new StorageAdapterMeshQueryProvider(adapter);
        var (changes, subscription) = Open(provider);
        using var _ = subscription;
        await changes.Where(c => c.ChangeType == QueryChangeType.Initial)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the live query never emitted its Initial", cancellationToken: ct);
        var afterInitial = adapter.Walks;

        for (var i = 0; i < UnrelatedWrites; i++)
            await Write(adapter, Node($"doc{i:D2}", Other))
                .Should().Within(TestTimeouts.Convergence).Emit($"write doc{i:D2}", cancellationToken: ct);

        // The re-query runs inline on the writing thread, so by now every trigger the forty writes
        // raised has walked. None may have.
        adapter.Walks.Should().Be(afterInitial,
            $"{UnrelatedWrites} writes of '{Other}' cannot enter or leave a '{Watched}' result, so they must not "
            + "re-read the scope — a walk per write is what made one write's cost grow with the mesh");

        await Write(adapter, Node("hit", Watched))
            .Should().Within(TestTimeouts.Convergence).Emit("write hit", cancellationToken: ct);
        await Carrying(changes, QueryChangeType.Added, "hit")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a write of the watched type must still reach the live query", cancellationToken: ct);
        adapter.Walks.Should().Be(afterInitial + 1, "the one relevant write costs exactly one re-read");
    }

    /// <summary>
    /// A row that LEAVES the result by being retyped is still seen: the notification carries the
    /// type the path held before, so a write whose new type is foreign but whose old type was
    /// watched is relevant.
    /// </summary>
    [Fact]
    public async Task ARetypeAwayFromTheWatchedType_StillRemovesTheRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new InMemoryStorageAdapter();
        var provider = new StorageAdapterMeshQueryProvider(inner);
        var (changes, subscription) = Open(provider);
        using var _ = subscription;
        await changes.Where(c => c.ChangeType == QueryChangeType.Initial)
            .Should().Within(TestTimeouts.Convergence).Emit("Initial", cancellationToken: ct);

        await Write(inner, Node("row", Watched))
            .Should().Within(TestTimeouts.Convergence).Emit("write row", cancellationToken: ct);
        await Carrying(changes, QueryChangeType.Added, "row")
            .Should().Within(TestTimeouts.Convergence).Emit("row entered the result", cancellationToken: ct);

        await Write(inner, Node("row", Other) with { Version = 2 })
            .Should().Within(TestTimeouts.Convergence).Emit("retype row", cancellationToken: ct);
        await Carrying(changes, QueryChangeType.Removed, "row")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a row retyped away from the watched type must leave the result — its NEW type is foreign, "
                + "only its PRIOR type says the change is relevant", cancellationToken: ct);
    }

    /// <summary>A delete of a watched row is relevant through the deleted row's own type.</summary>
    [Fact]
    public async Task ADeleteOfAWatchedRow_StillRemovesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new InMemoryStorageAdapter();
        var provider = new StorageAdapterMeshQueryProvider(inner);
        var (changes, subscription) = Open(provider);
        using var _ = subscription;
        await changes.Where(c => c.ChangeType == QueryChangeType.Initial)
            .Should().Within(TestTimeouts.Convergence).Emit("Initial", cancellationToken: ct);

        await Write(inner, Node("gone", Watched))
            .Should().Within(TestTimeouts.Convergence).Emit("write gone", cancellationToken: ct);
        await Carrying(changes, QueryChangeType.Added, "gone")
            .Should().Within(TestTimeouts.Convergence).Emit("gone entered the result", cancellationToken: ct);

        await inner.Delete($"{Base}/gone")
            .Should().Within(TestTimeouts.Convergence).Emit("delete gone", cancellationToken: ct);
        await Carrying(changes, QueryChangeType.Removed, "gone")
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a deleted watched row must leave the result", cancellationToken: ct);
    }

    /// <summary>
    /// The rule itself, over every shape it must refuse to reason about. Anything it cannot prove
    /// is relevant — the worst case of the rule is the re-query that always used to run.
    /// </summary>
    [Fact]
    public void TheRuleProvesIrrelevanceOnlyWhenItCan()
    {
        var parser = new QueryParser();
        ImmutableHashSet<string>? Required(params string[] queries) =>
            NodeTypeChangeRelevance.RequiredNodeTypes(queries.Select(q => parser.Parse(q)));
        DataChangeNotification Change(string? type, bool known, string? previous = null,
            DataChangeKind kind = DataChangeKind.Updated) =>
            new($"{Base}/x", kind, null, DateTimeOffset.UtcNow)
            { NodeType = type, PriorStateKnown = known, PreviousNodeType = previous };

        var watched = Required($"path:{Base} scope:descendants nodeType:{Watched}");
        watched.Should().NotBeNull();

        // Provably irrelevant: a foreign type arriving where nothing (or something foreign) was.
        NodeTypeChangeRelevance.CanAffect(Change(Other, known: true), watched).Should().BeFalse();
        NodeTypeChangeRelevance.CanAffect(Change(Other, known: true, previous: "Space"), watched).Should().BeFalse();
        NodeTypeChangeRelevance.CanAffect(Change(null, known: true, previous: Other, DataChangeKind.Deleted), watched)
            .Should().BeFalse("deleting a foreign row cannot shrink the result");

        // Relevant: the watched type arriving or leaving, in any case spelling.
        NodeTypeChangeRelevance.CanAffect(Change(Watched.ToLowerInvariant(), known: true), watched).Should().BeTrue();
        NodeTypeChangeRelevance.CanAffect(Change(Other, known: true, previous: Watched), watched).Should().BeTrue();
        NodeTypeChangeRelevance.CanAffect(Change(null, known: true, previous: Watched, DataChangeKind.Deleted), watched)
            .Should().BeTrue();

        // Not provable, so relevant.
        NodeTypeChangeRelevance.CanAffect(Change(Other, known: false), watched)
            .Should().BeTrue("a feed that cannot see the replaced row says nothing about what left");
        NodeTypeChangeRelevance.CanAffect(Change(null, known: true), watched)
            .Should().BeTrue("a change with no type is unclassified");
        NodeTypeChangeRelevance.CanAffect(Change(Other, known: true), null).Should().BeTrue();

        Required($"path:{Base} scope:descendants").Should().BeNull("an unconstrained query admits any type");
        Required($"path:{Base} scope:descendants (nodeType:{Watched} OR name:x)")
            .Should().BeNull("a disjunction is not reasoned about");
        Required($"path:{Base} scope:descendants nodeType:{Watched}", $"path:{Base} scope:children")
            .Should().BeNull("ONE unconstrained query of a request makes every change relevant");
        var union = Required($"path:{Base} scope:descendants nodeType:{Watched}", $"path:{Base} scope:children nodeType:Other2");
        // Assert.NotNull flows non-nullability, so the line below needs no null-forgiving operator.
        Assert.NotNull(union);
        union.Order(StringComparer.Ordinal).Should().Equal("Other2", Watched);
    }
}
