using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 The query merge must never hold its gate while its consumer runs (MeshWeaver#7117).
///
/// <para>The incident: a case-only rename's staging creates stalled at stage <c>write</c> on CI and
/// never answered. The two thread stacks were a lock-order deadlock. Thread B delivered a provider's
/// Initial into <c>MeshQuery</c>'s merge, which called its observer INSIDE its gate. That observer, a
/// create's <c>CreatableTypesCreationValidator</c>, ran on synchronously into the storage write, and
/// the write's change notification needs the storage feed's serializing <c>Observable.Merge</c> gate.
/// Thread A held that feed gate, delivering an earlier write's change into a live query over the same
/// store, and waited for the merge's gate. Each held what the other waited for.</para>
///
/// <para>This rig pins the merge's half of the cycle without the storage half: a second provider's
/// live delta is delivered from another thread WHILE the consumer is still handling the merged
/// Initial. That delivery must return at once, and the delta must reach the consumer after the
/// Initial. Before the fix it blocked on the gate the consumer's thread held, for as long as the
/// consumer ran. In the incident the consumer could only finish once that same thread let go of the
/// storage feed, so it never finished.</para>
/// </summary>
public class AQueryMergeNeverHoldsItsGateWhileItsConsumerRunsTest
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>How long the consumer waits for the other thread's delivery to return. Ample for a
    /// non-blocking call; a delivery still blocked after it is blocked by the consumer itself.</summary>
    private static readonly TimeSpan DeliveryBound = TimeSpan.FromSeconds(5);

    private sealed class SubjectProvider(string name, IObservable<QueryResultChange<MeshNode>> frames)
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

    private static MeshNode Node(string id) => new(id, "Inbox/_Inbox")
    {
        Name = id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
    };

    [Fact]
    public void ALiveDeltaFromAnotherThread_IsNotBlocked_WhileTheConsumerHandlesTheInitial()
    {
        var early = new Subject<QueryResultChange<MeshNode>>();
        var late = new Subject<QueryResultChange<MeshNode>>();
        var added = Node("added-while-the-consumer-runs");

        var frames = ImmutableList<QueryChangeType>.Empty;
        // Set by the other thread once its delivery RETURNED; read by the consumer under a bound.
        var deltaDeliveryReturned = 0;
        var deltaReturnedWhileConsumerBusy = false;
        var otherThreadStarted = 0;

        using var subscription = ((IMeshQueryCore)new MeshQuery(
                [new SubjectProvider("early", early), new SubjectProvider("late", late)], hub: null!))
            .Query<MeshNode>(new MeshQueryRequest { Query = "path:Inbox/_Inbox scope:children", Limit = 50 }, Options)
            .Subscribe(change =>
            {
                ImmutableInterlocked.Update(ref frames, f => f.Add(change.ChangeType));
                if (change.ChangeType != QueryChangeType.Initial)
                    return;
                // The consumer is busy with the merged Initial — as the create validator was, on its
                // way into a storage write — while another thread delivers the early provider's delta.
                var other = new Thread(() =>
                {
                    try
                    {
                        Volatile.Write(ref otherThreadStarted, 1);
                        early.OnNext(Frame(QueryChangeType.Added, added));
                    }
                    finally
                    {
                        // Never strand the consumer, whatever the delivery did.
                        Volatile.Write(ref deltaDeliveryReturned, 1);
                    }
                }) { IsBackground = true, Name = "live-delta" };
                other.Start();
                deltaReturnedWhileConsumerBusy = SpinWait.SpinUntil(
                    () => Volatile.Read(ref deltaDeliveryReturned) == 1, DeliveryBound);
            });

        // The query subscribes through its pool; wait until both providers are subscribed.
        SpinWait.SpinUntil(() => early.HasObservers && late.HasObservers, DeliveryBound)
            .Should().BeTrue("the merge subscribes both providers");

        early.OnNext(Frame(QueryChangeType.Initial));
        late.OnNext(Frame(QueryChangeType.Initial));

        SpinWait.SpinUntil(() => Volatile.Read(ref deltaDeliveryReturned) == 1, DeliveryBound * 3)
            .Should().BeTrue("the other thread's delivery returns in the end, fix or no fix");
        Volatile.Read(ref otherThreadStarted).Should().Be(1, "the consumer started the other thread");

        deltaReturnedWhileConsumerBusy.Should().BeTrue(
            "a provider delivering a live delta must never wait for the merge's consumer to finish the "
            + "previous frame: the merge called its consumer under its gate, so the delivery blocked on "
            + "that gate — and in the incident the consumer was waiting for a lock the delivering thread "
            + "held, so neither ever finished");
        SpinWait.SpinUntil(() => Volatile.Read(ref frames).Count >= 2, DeliveryBound)
            .Should().BeTrue("the delta reaches the consumer once it is done with the Initial");
        Volatile.Read(ref frames).Should().Equal(
            [QueryChangeType.Initial, QueryChangeType.Added],
            "the Initial first, then the delta, each exactly once");
    }
}

/// <summary>
/// 🚨 The snapshot fan-out (<see cref="MeshQuery.Query(MeshQueryRequest)"/> and
/// <see cref="MeshQuery.Autocomplete"/>) must never run its consumer under a lock a provider's
/// delivery needs — the same lock-order deadlock as <see cref="AQueryMergeNeverHoldsItsGateWhileItsConsumerRunsTest"/>
/// (review of #7149). It combined its providers with <c>CombineLatest</c>, which calls its subscriber
/// under its OWN serializing lock: releasing the merge's gate before the consumer ran was not enough,
/// because a second provider's delivery still blocked on CombineLatest's lock for as long as the
/// consumer ran.
/// </summary>
public class ASnapshotFanOutNeverHoldsALockWhileItsConsumerRunsTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly TimeSpan DeliveryBound = TimeSpan.FromSeconds(5);

    private sealed class SnapshotProvider(string name, IObservable<IReadOnlyCollection<QueryResult>> snapshots)
        : IMeshQueryProvider
    {
        public string Name => name;
        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;
        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Never<QueryResultChange<T>>();
        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => snapshots;
        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => snapshots;
        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }

    private static IReadOnlyCollection<QueryResult> Rows(params string[] paths)
        => paths.Select(p => new QueryResult
        {
            Path = p,
            Name = p.Split('/').Last(),
            NodeType = "Markdown",
            Score = 1,
        }).ToList();

    /// <summary>A second provider's newer snapshot, delivered from another thread while the consumer
    /// is still handling the first frame, must return at once and reach the consumer afterwards.</summary>
    [Theory]
    [InlineData("query")]
    [InlineData("autocomplete")]
    public void ASnapshotFromAnotherThread_IsNotBlocked_WhileTheConsumerHandlesTheFirstFrame(string surface)
    {
        var early = new Subject<IReadOnlyCollection<QueryResult>>();
        var late = new Subject<IReadOnlyCollection<QueryResult>>();
        var mesh = new MeshQuery([new SnapshotProvider("early", early), new SnapshotProvider("late", late)], Mesh);
        var source = surface == "query"
            ? mesh.Query(new MeshQueryRequest { Query = "*alpha* scope:descendants partitions:all", UserId = "reader" })
            : mesh.Autocomplete("", "alpha");

        var frameSizes = ImmutableList<int>.Empty;
        var deliveryReturned = 0;
        var otherThreadStarted = 0;
        var returnedWhileConsumerBusy = false;

        using var subscription = source.Subscribe(rows =>
        {
            var isFirst = Volatile.Read(ref frameSizes).IsEmpty;
            ImmutableInterlocked.Update(ref frameSizes, f => f.Add(rows.Count));
            if (!isFirst)
                return;
            // The consumer is busy with the first frame — as a create's validator was, on its way
            // into a storage write — while another thread delivers the early provider's newer answer.
            var other = new Thread(() =>
            {
                try
                {
                    Volatile.Write(ref otherThreadStarted, 1);
                    early.OnNext(Rows("Docs/alpha-1", "Docs/alpha-2"));
                }
                finally
                {
                    // Never strand the consumer, whatever the delivery did.
                    Volatile.Write(ref deliveryReturned, 1);
                }
            }) { IsBackground = true, Name = "snapshot-delivery" };
            other.Start();
            returnedWhileConsumerBusy = SpinWait.SpinUntil(
                () => Volatile.Read(ref deliveryReturned) == 1, DeliveryBound);
        });

        SpinWait.SpinUntil(() => early.HasObservers && late.HasObservers, DeliveryBound)
            .Should().BeTrue("the fan-out subscribes both providers");

        early.OnNext(Rows("Docs/alpha-1"));
        late.OnNext(Rows());

        SpinWait.SpinUntil(() => Volatile.Read(ref deliveryReturned) == 1, DeliveryBound * 3)
            .Should().BeTrue("the other thread's delivery returns in the end, fix or no fix");
        Volatile.Read(ref otherThreadStarted).Should().Be(1, "the consumer started the other thread");

        returnedWhileConsumerBusy.Should().BeTrue(
            "a provider delivering a newer snapshot must never wait for the consumer to finish the "
            + "previous frame: CombineLatest called the consumer under its own lock, so the delivery "
            + "blocked on it for as long as the consumer ran");
        SpinWait.SpinUntil(() => Volatile.Read(ref frameSizes).Count >= 3, DeliveryBound)
            .Should().BeTrue("the newer snapshot reaches the consumer once it is done with the first frame");
        Volatile.Read(ref frameSizes).Should().Equal(
            [1, 2, 2],
            "the early provider's first answer (progressive: the late one is still silent), then its newer "
            + "answer queued by the other thread, then the late provider's empty answer — in that order");
    }

    /// <summary>A provider whose leg factory THROWS while the fan-out is subscribing must not leave the
    /// providers subscribed before it running: the fault reaches the consumer and the earlier
    /// provider's subscription is disposed with the failed fan-out (review of #7149).</summary>
    [Theory]
    [InlineData("query")]
    [InlineData("autocomplete")]
    public void ALegFactoryThatThrows_FailsTheFanOut_AndReleasesTheProvidersAlreadySubscribed(string surface)
    {
        var early = new Subject<IReadOnlyCollection<QueryResult>>();
        var mesh = new MeshQuery([new SnapshotProvider("early", early), new ThrowingProvider("broken")], Mesh);
        var source = surface == "query"
            ? mesh.Query(new MeshQueryRequest { Query = "*alpha* scope:descendants partitions:all", UserId = "reader" })
            : mesh.Autocomplete("", "alpha");

        Exception? fault = null;
        using var subscription = source.Subscribe(_ => { }, ex => Volatile.Write(ref fault, ex));

        SpinWait.SpinUntil(() => Volatile.Read(ref fault) is not null, DeliveryBound)
            .Should().BeTrue("a provider that cannot build its leg fails the fan-out");
        Volatile.Read(ref fault).Should().BeOfType<InvalidOperationException>();
        SpinWait.SpinUntil(() => !early.HasObservers, DeliveryBound).Should().BeTrue(
            "the provider subscribed before the throwing one must be released with the failed fan-out — "
            + "the leg factory ran inside the subscription's setup, so its throw left the setup before the "
            + "composite holding the earlier subscription was ever returned for disposal");
    }

    private sealed class ThrowingProvider(string name) : IMeshQueryProvider
    {
        public string Name => name;
        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;
        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Never<QueryResultChange<T>>();
        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => throw new InvalidOperationException("leg factory fault");
        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => throw new InvalidOperationException("leg factory fault");
        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }
}
