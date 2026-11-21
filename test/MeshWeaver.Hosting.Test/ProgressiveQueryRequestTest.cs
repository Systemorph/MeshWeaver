using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <see cref="MeshQueryRequest.Progressive"/> — the opt-in a multi-partition provider reads before it
/// emits an early <see cref="QueryChangeType.Initial"/> and delivers the remaining partitions' rows
/// as <see cref="QueryChangeType.Added"/> changes (Doc/Architecture/ProgressiveQueries). Pins what such
/// a provider relies on from the fan-in: the flag reaches the provider, the early subset reaches the
/// caller on BOTH surfaces without waiting for the late rows, and the late rows follow.
/// </summary>
public class ProgressiveQueryRequestTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static MeshOperationOptions Ladder => new() { Timeout = TimeSpan.FromSeconds(4) };

    private static MeshNode Node(string path)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(path[(slash + 1)..], path[..slash]) { Name = path[(slash + 1)..], NodeType = "Markdown" };
    }

    /// <summary>
    /// A provider that answers like a partitioned store would: a progressive request gets the fast
    /// partition's rows as the Initial and the slow partition's rows as an Added once
    /// <paramref name="slow"/> fires; any other request gets ONE complete Initial.
    /// </summary>
    private sealed class PartitionedProvider(IObservable<MeshNode> slow, List<bool> seen) : IMeshQueryProvider
    {
        public string Name => "partitioned";

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            lock (seen)
                seen.Add(request.Progressive);
            var fast = Node("acme/alpha-one");
            QueryResultChange<T> Change(QueryChangeType type, params MeshNode[] items) => new()
            {
                ChangeType = type,
                Items = items.Cast<T>().ToList(),
                Query = ParsedQuery.Empty,
                Timestamp = DateTimeOffset.UtcNow,
            };
            return request.Progressive
                ? Observable.Return(Change(QueryChangeType.Initial, fast))
                    .Concat(slow.Take(1).Select(n => Change(QueryChangeType.Added, n)))
                    .Concat(Observable.Never<QueryResultChange<T>>())
                : slow.Take(1).Select(n => Change(QueryChangeType.Initial, fast, n))
                    .Concat(Observable.Never<QueryResultChange<T>>());
        }

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return<IReadOnlyCollection<QueryResult>>([]);

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }

    private static MeshQueryRequest Request(bool progressive) => new()
    {
        Query = "*alpha* scope:descendants partitions:all",
        UserId = "reader",
        Progressive = progressive,
    };

    /// <summary>The snapshot surface the search bar binds: the early subset is a frame of its own,
    /// and the late partition's row merges into the next one.</summary>
    [Fact(Timeout = 240_000)]
    public async Task SnapshotSurface_ShowsTheEarlySubset_ThenMergesTheLateRows()
    {
        var slow = new ReplaySubject<MeshNode>(1);
        var seen = new List<bool>();
        var mesh = new MeshQuery([new PartitionedProvider(slow, seen)], hub: Mesh, Ladder);
        var stream = mesh.Query(Request(progressive: true)).Replay();
        using var connection = stream.Connect();

        var first = await stream.Should().Within(TestTimeouts.Convergence)
            .Emit("the fast partition's rows need not wait for the slow one", TestContext.Current.CancellationToken);
        first.Select(r => r.Path).Should().Equal("acme/alpha-one");

        slow.OnNext(Node("globex/alpha-two"));
        var merged = await stream.Where(rows => rows.Count == 2).Should().Within(TestTimeouts.Convergence)
            .Emit("the slow partition's row joins the snapshot", TestContext.Current.CancellationToken);
        merged.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal)
            .Should().Equal("acme/alpha-one", "globex/alpha-two");
        lock (seen)
            seen.Should().OnlyContain(p => p, "the opt-in must reach the provider unchanged");
    }

    /// <summary>The typed surface forwards the early Initial and then the Added — the provider's own
    /// frames, unchanged by the merge.</summary>
    [Fact(Timeout = 240_000)]
    public async Task TypedSurface_ForwardsTheEarlyInitial_ThenTheAdded()
    {
        var slow = new ReplaySubject<MeshNode>(1);
        var mesh = new MeshQuery([new PartitionedProvider(slow, [])], hub: Mesh, Ladder);
        var stream = mesh.Query<MeshNode>(Request(progressive: true)).Replay();
        using var connection = stream.Connect();

        var initial = await stream.Where(c => c.ChangeType == QueryChangeType.Initial).Should()
            .Within(TestTimeouts.Convergence).Emit("the early Initial", TestContext.Current.CancellationToken);
        initial.Items.Select(n => n.Path).Should().Equal("acme/alpha-one");

        slow.OnNext(Node("globex/alpha-two"));
        var added = await stream.Where(c => c.ChangeType == QueryChangeType.Added).Should()
            .Within(TestTimeouts.Convergence).Emit("the late partition's rows", TestContext.Current.CancellationToken);
        added.Items.Select(n => n.Path).Should().Equal("globex/alpha-two");
    }

    /// <summary>The control: a caller that did NOT opt in gets one complete Initial, so taking the
    /// first Initial as the whole answer stays correct for every existing caller.</summary>
    [Fact(Timeout = 240_000)]
    public async Task WithoutTheOptIn_TheFirstInitialIsComplete()
    {
        var slow = new ReplaySubject<MeshNode>(1);
        var seen = new List<bool>();
        var mesh = new MeshQuery([new PartitionedProvider(slow, seen)], hub: Mesh, Ladder);
        var pending = mesh.Query<MeshNode>(Request(progressive: false))
            .Where(c => c.ChangeType == QueryChangeType.Initial).Should()
            .Within(TestTimeouts.Convergence).Emit("the one complete Initial", TestContext.Current.CancellationToken);
        slow.OnNext(Node("globex/alpha-two"));

        var initial = await pending;
        initial.Items.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal)
            .Should().Equal("acme/alpha-one", "globex/alpha-two");
        lock (seen)
            seen.Should().OnlyContain(p => !p, "the default is not progressive");
    }
}
