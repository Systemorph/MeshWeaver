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

    /// <summary>
    /// The candidate query loses EVERY <c>sort:</c>, cut where the parser reads it, and keeps every
    /// other byte. Review #4305983917: a regex missed the QUOTED spelling the parser accepts, so the
    /// candidate query still asked for the subtree sort and the engine re-entered itself until the
    /// stack overflowed; and splitting on whitespace collapsed <c>name:"A  B"</c> to <c>name:"A B"</c>.
    /// </summary>
    [Theory]
    [InlineData("nodeType:Client sort:subtreeLastModified-desc limit:5", "nodeType:Client limit:5")]
    [InlineData("sort:SubtreeLastModified nodeType:Client", "nodeType:Client")]
    [InlineData("nodeType:Client sort:\"subtreeLastModified-desc\"", "nodeType:Client")]
    [InlineData("nodeType:Client sort:'subtreeLastModified'", "nodeType:Client")]
    [InlineData("name:\"A  B\" sort:subtreeLastModified-desc", "name:\"A  B\"")]
    [InlineData("sort:subtreeLastModified  name:\"x   y\"\tnodeType:Client", "name:\"x   y\"\tnodeType:Client")]
    [InlineData("nodeType:Client sort:name sort:subtreeLastModified", "nodeType:Client")]
    public void EverySortTermIsCutAndEveryOtherByteKept(string query, string expected)
    {
        var candidate = SubtreeSort.WithoutSort(query);

        candidate.Should().Be(expected);
        new QueryParser().Parse(candidate).OrderBy.Should().BeNull(
            "a candidate query that still orders could re-enter the subtree sort");
    }

    /// <summary>The quoted selector the parser accepts ranks like the bare one — and terminates.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AQuotedSelectorRanksAndTerminates()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = await Engine(Fixture, ct);

        (await Ids(query, new MeshQueryRequest { Query = $"{Roster} sort:\"subtreeLastModified-desc\"" }, ct))
            .Should().Equal(["Gamma", "Beta", "Delta", "Alpha"]);
    }

    /// <summary>
    /// A candidate path is read as a LITERAL (review #4305983944). Spliced bare,
    /// <c>Org/Project Files</c> became namespace <c>Org/Project</c> plus the text <c>Files</c> (and
    /// read <c>Org/Project/Files</c>'s day 30), <c>Org/A|B</c> an alternation reaching <c>Org/A</c>'s
    /// day 35, and <c>Org/X*</c> a glob reaching <c>Org/XY</c>'s day 40 — each container keyed on
    /// a neighbour's activity.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ACandidatePathIsReadAsALiteral()
    {
        var ct = TestContext.Current.CancellationToken;
        MeshNode Container(string id) => new(id, "Org") { Name = id, NodeType = "Client", State = MeshNodeState.Active, LastModified = Day(1) };
        var query = await Engine(
        [
            new MeshNode("Org") { Name = "Org", NodeType = "Group", State = MeshNodeState.Active, LastModified = Day(1) },
            Container("Project Files"), Child("Spec", "Org/Project Files", Day(5)),
            Container("Project"), Child("Files", "Org/Project", Day(30)),
            Container("A|B"), Child("Memo", "Org/A|B", Day(6)),
            Container("A"), Child("Memo", "Org/A", Day(35)),
            Container("X*"), Child("Memo", "Org/X*", Day(7)),
            Container("XY"), Child("Memo", "Org/XY", Day(40)),
        ], ct);

        (await Ids(query, new MeshQueryRequest { Query = "namespace:Org nodeType:Client sort:subtreeLastModified-desc" }, ct))
            .Should().Equal(["XY", "A", "Project", "X*", "A|B", "Project Files"],
                "each container is keyed on its OWN descendants only");
    }

    [Theory]
    [InlineData("Org/Project Files", "\"Org/Project Files\"")]
    [InlineData("Org/A|B", "\"Org/A|B\"")]
    [InlineData("Org/X*", "\"Org/X*\"")]
    [InlineData("Org/say \"hi\"", "'Org/say \"hi\"'")]
    public void APathIsQuotedAsALiteral(string path, string literal)
        => SubtreeSort.NewestDescendantQuery(path).Should().Be($"namespace:{literal} scope:descendants sort:lastModified-desc limit:1");

    [Theory]
    [InlineData("nodeType:Client sort:subtreeLastModified-desc", true)]
    [InlineData("nodeType:Client sort:SUBTREELASTMODIFIED", true)]
    [InlineData("nodeType:Client sort:lastModified-desc", false)]
    public void TheSelectorParsesWithoutAParserChange(string query, bool applies)
        => SubtreeSort.Applies(new QueryParser().Parse(query).OrderBy).Should().Be(applies);

    /// <summary>
    /// 🚨 Live changes never leave the window (review #4305983928). The candidate read is unlimited,
    /// so its deltas name off-page rows too: forwarded unchanged, an Updated off-page container grew
    /// a <c>Limit = 2</c> result to three, an Added bypassed the window and a delete did not backfill.
    /// Every frame after the Initial must be the re-taken window, in order.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task LiveChangesAreReRankedIntoTheWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var live = new LiveProvider(Fixture);
        var query = (IMeshQueryCore)new MeshQuery([live], hub: null!);
        var frames = new System.Reactive.Subjects.ReplaySubject<QueryResultChange<MeshNode>>();
        using var subscription = query.Query<MeshNode>(
                new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc", Limit = 2, UserId = "viewer" }, Options)
            .Subscribe(frames);

        Task<QueryResultChange<MeshNode>> Frame(int index) => frames.Skip(index).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(ct);
        static string[] Ids(QueryResultChange<MeshNode> c) => [.. c.Items.Select(n => n.Id)];

        var initial = await Frame(0);
        Ids(initial).Should().Equal(["Gamma", "Beta"]);

        // An off-page container changes: nothing inside the window moved, so nothing is emitted —
        // and in particular never a frame carrying Alpha.
        live.Push(QueryChangeType.Updated, Client("Alpha", Day(31)));
        // A new container whose child is the newest row of all enters at the top…
        live.Add(Child("Kickoff", "Epsilon", Day(40)));
        live.Push(QueryChangeType.Added, Client("Epsilon", Day(39)));
        var evictBeta = await Frame(1);
        evictBeta.ChangeType.Should().Be(QueryChangeType.Removed, "the row that leaves the window is named first");
        Ids(evictBeta).Should().Equal(["Beta"]);
        var added = await Frame(2);
        added.ChangeType.Should().Be(QueryChangeType.Reset, "a re-taken window replaces the result");
        Ids(added).Should().Equal(["Epsilon", "Gamma"], "the Added row is ranked INTO the window; Beta drops off");

        // …and deleting it backfills the window from the candidates.
        live.Push(QueryChangeType.Removed, Client("Epsilon", Day(39)));
        Ids(await Frame(3)).Should().Equal(["Epsilon"], "the deleted row is evicted by name");
        var backfilled = await Frame(4);
        Ids(backfilled).Should().Equal(["Gamma", "Beta"], "a delete backfills");

        // An Updated row INSIDE the window re-emits it, order intact.
        live.Push(QueryChangeType.Updated, Client("Beta", Day(2)) with { Name = "Beta renamed" });
        var updated = await Frame(5);
        Ids(updated).Should().Equal(["Gamma", "Beta"]);
        updated.Items[1].Name.Should().Be("Beta renamed");

        new[] { initial, added, backfilled, updated }.SelectMany(c => c.Items).Select(n => n.Id)
            .Should().NotContain("Alpha", "no frame ever carries a row outside the window — "
                + "the off-page Updated produced none, so the frame after the Initial is the Added's");

        // Folded the way SyncedQueryMeshNodes folds them — a Reset ADDS, a Removed removes — no
        // snapshot ever holds more than the window (review #4306054491: it held three under limit:2).
        var cache = new Dictionary<string, MeshNode>();
        var sizes = new List<int>();
        foreach (var frame in frames.Take(6).ToEnumerable())
        {
            foreach (var node in frame.Items)
                if (frame.ChangeType == QueryChangeType.Removed) cache.Remove(node.Path);
                else cache[node.Path] = node;
            sizes.Add(cache.Count);
        }
        sizes.Should().AllSatisfy(size => size.Should().BeLessThanOrEqualTo(2));
        cache.Values.Select(n => n.Id).Order(StringComparer.Ordinal).Should().Equal(["Beta", "Gamma"]);
    }

    /// <summary>
    /// A delta that arrives while the Initial's key reads are still pending is ranked once they
    /// finish, never dropped (review #4306054478: <c>Select</c> ran the ranking eagerly, saw no window
    /// yet and discarded the delta for good).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ADeltaDuringTheInitialKeyReadsIsRankedNotDropped()
    {
        var ct = TestContext.Current.CancellationToken;
        var live = new LiveProvider(Fixture);
        var (release, requested) = live.HoldKeyReadsOf("Gamma");
        var query = (IMeshQueryCore)new MeshQuery([live], hub: null!);
        var frames = new System.Reactive.Subjects.ReplaySubject<QueryResultChange<MeshNode>>();
        using var subscription = query.Query<MeshNode>(
                new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc", Limit = 2, UserId = "viewer" }, Options)
            .Subscribe(frames);

        // Gamma's key read is held, so the Initial cannot be ranked yet — and Epsilon arrives now.
        // Waiting for the held read to be ASKED FOR proves the candidate stream is subscribed (it
        // already delivered the Initial being ranked), so the push cannot be lost at the source.
        await requested.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        live.Add(Child("Kickoff", "Epsilon", Day(40)));
        live.Push(QueryChangeType.Added, Client("Epsilon", Day(39)));
        release.OnNext(System.Reactive.Unit.Default);

        var ranked = await frames
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Select(c => string.Join(",", c.Items.Select(n => n.Id)))
            .FirstAsync(ids => ids == "Epsilon,Gamma")
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        ranked.Should().Be("Epsilon,Gamma", "the delta that came in during the key reads is ranked into the window");
    }

    /// <summary>
    /// An off-page candidate whose key read turns incomplete leaves the visible rows as they were,
    /// and the result must still stop claiming to be complete (review #4306054499).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnOffPageKeyTurningIncompleteIsReported()
    {
        var ct = TestContext.Current.CancellationToken;
        var live = new LiveProvider(Fixture);
        var query = (IMeshQueryCore)new MeshQuery([live], hub: null!);
        var frames = new System.Reactive.Subjects.ReplaySubject<QueryResultChange<MeshNode>>();
        using var subscription = query.Query<MeshNode>(
                new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc", Limit = 2, UserId = "viewer" }, Options)
            .Subscribe(frames);

        var initial = await frames.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        initial.SnapshotIncomplete.Should().BeFalse("control: every read answered in full");

        live.IncompleteKeyReadsOf("Alpha");
        live.Push(QueryChangeType.Updated, Client("Alpha", Day(31)));
        var next = await frames.Skip(1).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        next.Items.Select(n => n.Id).Should().Equal(["Gamma", "Beta"], "the visible rows did not change");
        next.SnapshotIncomplete.Should().BeTrue("but the ranking now rests on an incomplete read, and says so");
    }

    /// <summary>
    /// A ranking built on an incomplete key read says so (review #4305983936). A key read whose
    /// frame is <c>SnapshotIncomplete</c> may have missed the newest row, so the order is a floor.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnIncompleteKeyReadMarksTheRankingIncomplete()
    {
        var ct = TestContext.Current.CancellationToken;
        var complete = await Engine(Fixture, ct);
        var incomplete = await Engine(Fixture, ct, provider => new IncompleteKeyReads(provider));
        var request = new MeshQueryRequest { Query = $"{Roster} sort:subtreeLastModified-desc" };

        (await complete.Query<MeshNode>(request, Options).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .SnapshotIncomplete.Should().BeFalse("control: every read answered in full");
        (await incomplete.Query<MeshNode>(request, Options).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .SnapshotIncomplete.Should().BeTrue("a complete-looking ranking must not rest on an incomplete read");
    }

    /// <summary>Key reads (the <c>namespace:"…" scope:descendants</c> reads) answer incomplete.</summary>
    private sealed class IncompleteKeyReads(IMeshQueryProvider inner) : DelegatingProvider(inner)
    {
        public override IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => request.Query.StartsWith("namespace:\"", StringComparison.Ordinal)
                ? Inner.Query<T>(request, options).Select(c => c with { SnapshotIncomplete = true })
                : Inner.Query<T>(request, options);
    }

    /// <summary>
    /// A live store: the candidate query answers an Initial and then whatever <see cref="Push"/>
    /// sends; every other read (the key reads) is evaluated over the current rows.
    /// </summary>
    private sealed class LiveProvider(IEnumerable<MeshNode> seed) : IMeshQueryProvider
    {
        private readonly List<MeshNode> rows = [.. seed];
        private readonly System.Reactive.Subjects.Subject<QueryResultChange<MeshNode>> deltas = new();
        private readonly QueryEvaluator evaluator = new();
        private readonly QueryParser parser = new();

        private readonly Dictionary<string, (System.Reactive.Subjects.ReplaySubject<System.Reactive.Unit> Release, System.Reactive.Subjects.ReplaySubject<System.Reactive.Unit> Requested)> held = new(StringComparer.Ordinal);
        private readonly HashSet<string> incomplete = new(StringComparer.Ordinal);

        public void Add(MeshNode node) { lock (rows) rows.Add(node); }

        /// <summary>
        /// Key reads under <paramref name="ns"/> wait until <c>Release</c> fires; <c>Requested</c>
        /// fires when such a read is asked for.
        /// </summary>
        public (IObserver<System.Reactive.Unit> Release, IObservable<System.Reactive.Unit> Requested) HoldKeyReadsOf(string ns)
        {
            var gate = (Release: new System.Reactive.Subjects.ReplaySubject<System.Reactive.Unit>(1),
                Requested: new System.Reactive.Subjects.ReplaySubject<System.Reactive.Unit>(1));
            lock (rows) held[ns] = gate;
            return gate;
        }

        /// <summary>Key reads under <paramref name="ns"/> answer <c>SnapshotIncomplete</c> from now on.</summary>
        public void IncompleteKeyReadsOf(string ns) { lock (rows) incomplete.Add(ns); }

        public void Push(QueryChangeType type, MeshNode node)
        {
            lock (rows)
            {
                rows.RemoveAll(r => r.Path == node.Path);
                if (type != QueryChangeType.Removed)
                    rows.Add(node);
            }
            deltas.OnNext(new QueryResultChange<MeshNode> { ChangeType = type, Items = [node], Timestamp = DateTimeOffset.UtcNow });
        }

        public string Name => "live";

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            var parsed = parser.Parse(request.Query);
            if (parsed.ExtractNodeType() == "Client")
            {
                MeshNode[] clients;
                lock (rows) clients = [.. rows.Where(r => r.NodeType == "Client")];
                return (IObservable<QueryResultChange<T>>)(object)deltas
                    .StartWith(new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial, Items = clients, Query = parsed, Timestamp = DateTimeOffset.UtcNow });
            }
            var ns = parsed.Path ?? "";
            IObservable<System.Reactive.Unit>? gate = null;
            lock (rows)
                if (held.TryGetValue(ns, out var hold))
                {
                    gate = hold.Release;
                    hold.Requested.OnNext(System.Reactive.Unit.Default);
                }
            var read = Observable.Defer(() =>
            {
                MeshNode[] below;
                bool partial;
                lock (rows)
                {
                    below = [.. rows.Where(r => r.Path.StartsWith(ns + "/", StringComparison.Ordinal))
                        .OrderByDescending(r => r.LastModified).Take(1)];
                    partial = incomplete.Contains(ns);
                }
                return Observable.Return(new QueryResultChange<MeshNode>
                {
                    ChangeType = QueryChangeType.Initial, Items = below, Query = parsed,
                    SnapshotIncomplete = partial, Timestamp = DateTimeOffset.UtcNow,
                });
            });
            return (IObservable<QueryResultChange<T>>)(object)(gate is null ? read : gate.Take(1).SelectMany(_ => read));
        }

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

    /// <summary>Forwards everything to <see cref="Inner"/>; a test overrides what it changes.</summary>
    private abstract class DelegatingProvider(IMeshQueryProvider inner) : IMeshQueryProvider
    {
        protected IMeshQueryProvider Inner => inner;

        public string Name => inner.Name;

        public bool Matches(IReadOnlyList<string> queryNamespaces) => inner.Matches(queryNamespaces);

        public abstract IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options);

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
