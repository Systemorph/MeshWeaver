using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
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
