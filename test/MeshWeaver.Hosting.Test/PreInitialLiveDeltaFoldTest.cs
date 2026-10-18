using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A live delta a provider emits BEFORE the merged Initial is out belongs to the Initial.
///
/// <para>The merged Initial gates on every provider's Initial. A provider that answers early can
/// see a write land, and emit its <c>Removed</c> / <c>Added</c> / <c>Updated</c>, while a slower
/// provider has not answered yet. The merge used to run that delta through the LIVE dedup, whose
/// set of live paths is filled only when the merged Initial is emitted: a <c>Removed</c> found the
/// path "never live" and was DROPPED, and the merged Initial then carried the deleted row out of
/// the early provider's held hits — so the result set held a node that no longer exists, for good,
/// since no later write to that path ever comes. An <c>Added</c> was forwarded AHEAD of the Initial,
/// which every fold treats as a reset, so the added row was lost the same way.</para>
///
/// <para>Production instance: <c>RegistryUpdateReconcilerTest.ABroadcastFromAnUnknownRegistry_IsDroppedWithoutARequest</c>
/// (core #6968, run 39153661189). The unknown-registry drain deletes its delivery at once, in the
/// window in which the test's own inbox listing was being opened — the storage provider's Initial
/// read the delivery, its <c>Removed</c> arrived before the other providers' Initials, and the
/// listing reported the deleted delivery for the remaining 108 s.</para>
///
/// <para>The rig is deterministic: each provider is a <see cref="ReplaySubject{T}"/> with its frames
/// pushed before the query is subscribed, so the merge replays the early provider's Initial and
/// its delta, and only then the late provider's Initial — exactly the ordering of the race, with no
/// clock in it.</para>
/// </summary>
public class PreInitialLiveDeltaFoldTest
{
    private static readonly JsonSerializerOptions Options = new();

    private sealed class ReplayProvider(string name, IObservable<QueryResultChange<MeshNode>> frames)
        : IMeshQueryProvider
    {
        public string Name => name;
        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;
        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => (IObservable<QueryResultChange<T>>)frames;
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

    private static QueryResultChange<MeshNode> Frame(QueryChangeType type, params MeshNode[] nodes) => new()
    {
        ChangeType = type,
        Items = nodes,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private static MeshNode Node(string id, string content = "v1") => new(id, "Inbox/_Inbox")
    {
        Name = content,
        NodeType = "WebhookEvent",
        State = MeshNodeState.Active,
    };

    private static IObservable<QueryResultChange<MeshNode>> Replay(params QueryResultChange<MeshNode>[] frames)
    {
        var subject = new ReplaySubject<QueryResultChange<MeshNode>>();
        foreach (var frame in frames)
            subject.OnNext(frame);
        return subject;
    }

    private static Task<QueryResultChange<MeshNode>> FirstFrame(CancellationToken ct, params IMeshQueryProvider[] providers)
        => ((IMeshQueryCore)new MeshQuery(providers, hub: null!))
            .Query<MeshNode>(new MeshQueryRequest { Query = "path:Inbox/_Inbox scope:children", Limit = 50 }, Options)
            .FirstAsync()
            .Timeout(TestTimeouts.Quick)
            .Await(ct);

    [Fact]
    public async Task ARemovalBeforeTheMergedInitial_IsNotInTheInitial()
    {
        var delivery = Node("d1");
        var early = new ReplayProvider("early",
            Replay(Frame(QueryChangeType.Initial, delivery), Frame(QueryChangeType.Removed, delivery)));
        var late = new ReplayProvider("late", Replay(Frame(QueryChangeType.Initial)));

        var first = await FirstFrame(TestContext.Current.CancellationToken, early, late);

        first.ChangeType.Should().Be(QueryChangeType.Initial);
        first.Items.Select(n => n.Path).Should().NotContain(delivery.Path,
            "the early provider removed the row before the merged Initial was emitted; carrying it in "
            + "the Initial reports a deleted node, and nothing will ever remove it again");
    }

    [Fact]
    public async Task AnAdditionBeforeTheMergedInitial_IsInTheInitial()
    {
        var added = Node("d2");
        var early = new ReplayProvider("early",
            Replay(Frame(QueryChangeType.Initial), Frame(QueryChangeType.Added, added)));
        var late = new ReplayProvider("late", Replay(Frame(QueryChangeType.Initial)));

        var first = await FirstFrame(TestContext.Current.CancellationToken, early, late);

        first.ChangeType.Should().Be(QueryChangeType.Initial,
            "nothing may precede the merged Initial — every fold treats the Initial as a reset, so a "
            + "delta forwarded ahead of it is erased");
        first.Items.Select(n => n.Path).Should().Contain(added.Path);
    }

    [Fact]
    public async Task AnUpdateBeforeTheMergedInitial_IsTheVersionInTheInitial()
    {
        var early = new ReplayProvider("early",
            Replay(Frame(QueryChangeType.Initial, Node("d3", "v1")), Frame(QueryChangeType.Updated, Node("d3", "v2"))));
        var late = new ReplayProvider("late", Replay(Frame(QueryChangeType.Initial)));

        var first = await FirstFrame(TestContext.Current.CancellationToken, early, late);

        first.ChangeType.Should().Be(QueryChangeType.Initial);
        first.Items.Single(n => n.Id == "d3").Name.Should().Be("v2", "the newest version the provider announced");
    }

    /// <summary>A provider whose Initial arrives AFTER the removal may have read before it: its copy
    /// is stale and must not resurrect the deleted row (review of #6973).</summary>
    [Fact]
    public async Task ARemovalBeforeTheMergedInitial_IsNotUndoneByALaterOverlappingInitial()
    {
        var delivery = Node("d5");
        var early = new ReplayProvider("early",
            Replay(Frame(QueryChangeType.Initial, delivery), Frame(QueryChangeType.Removed, delivery)));
        var late = new ReplayProvider("late", Replay(Frame(QueryChangeType.Initial, delivery)));

        var first = await FirstFrame(TestContext.Current.CancellationToken, early, late);

        first.ChangeType.Should().Be(QueryChangeType.Initial);
        first.Items.Select(n => n.Path).Should().NotContain(delivery.Path,
            "the late provider's copy predates the removal the early provider already announced");
    }

    /// <summary>…while a row RE-CREATED before the Initial is kept: the mark is cleared by the Added.</summary>
    [Fact]
    public async Task ARowRecreatedBeforeTheMergedInitial_IsInTheInitial()
    {
        var delivery = Node("d6");
        var early = new ReplayProvider("early",
            Replay(Frame(QueryChangeType.Initial, delivery), Frame(QueryChangeType.Removed, delivery),
                Frame(QueryChangeType.Added, delivery)));
        var late = new ReplayProvider("late", Replay(Frame(QueryChangeType.Initial, delivery)));

        var first = await FirstFrame(TestContext.Current.CancellationToken, early, late);

        first.Items.Select(n => n.Path).Should().Contain(delivery.Path);
    }

    /// <summary>An unscored Updated keeps the rank of the copy it replaces (review of #6973): the
    /// storage provider's live frames carry no Scores, and a re-read row must not fall below a weaker
    /// match under Limit.</summary>
    [Fact]
    public async Task AnUnscoredUpdateBeforeTheMergedInitial_KeepsItsRank()
    {
        var strong = Node("z-strong", "v1");
        var weak = Node("a-weak");
        var ranked = Frame(QueryChangeType.Initial, strong, weak) with { Scores = [10.0, 1.0] };
        var early = new ReplayProvider("early",
            Replay(ranked, Frame(QueryChangeType.Updated, Node("z-strong", "v2"))));
        var late = new ReplayProvider("late", Replay(Frame(QueryChangeType.Initial)));

        var first = await ((IMeshQueryCore)new MeshQuery([early, late], hub: null!))
            // Free text: a ranked query, where the merge orders by score (a filter-only query is
            // ordered by DefaultFilterOrdering instead and would not read the score at all).
            .Query<MeshNode>(new MeshQueryRequest { Query = "delivery", Limit = 1 }, Options)
            .FirstAsync()
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);

        first.Items.Select(n => n.Id).Should().Equal(["z-strong"], "the updated row keeps its rank of 10 — path order alone would put a-weak first");
        first.Items.Single().Name.Should().Be("v2");
    }

    /// <summary>The fold keeps the live contract after the Initial: a row the Initial carries is
    /// live, so a later removal of it still flows.</summary>
    [Fact]
    public async Task ARemovalAfterTheMergedInitial_StillFlows()
    {
        var delivery = Node("d4");
        var early = new ReplayProvider("early", Replay(Frame(QueryChangeType.Initial, delivery)));
        var lateFrames = new ReplaySubject<QueryResultChange<MeshNode>>();
        lateFrames.OnNext(Frame(QueryChangeType.Initial));
        lateFrames.OnNext(Frame(QueryChangeType.Removed, delivery));
        var late = new ReplayProvider("late", lateFrames);

        var frames = await ((IMeshQueryCore)new MeshQuery([early, late], hub: null!))
            .Query<MeshNode>(new MeshQueryRequest { Query = "path:Inbox/_Inbox scope:children", Limit = 50 }, Options)
            .Take(2)
            .ToList()
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);

        frames[0].ChangeType.Should().Be(QueryChangeType.Initial);
        frames[0].Items.Select(n => n.Path).Should().Contain(delivery.Path);
        frames[1].ChangeType.Should().Be(QueryChangeType.Removed);
        frames[1].Items.Select(n => n.Path).Should().Equal([delivery.Path]);
    }
}
