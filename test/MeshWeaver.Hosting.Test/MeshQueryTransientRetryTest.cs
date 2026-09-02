using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Reactive.Testing;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the bounded transient-connect retry the query fan-in composes on every provider
/// observable (issue #2521: a single timed-out Npgsql connector open —
/// <c>PoolingDataSource.OpenNewConnector → RawOpen → TimeoutException</c> — failed the whole
/// layout-area render; warm pooled connections kept working, so only renders needing a fresh
/// connector died, in bursts). The contract, all on virtual time:
/// <list type="bullet">
///   <item>a transient connect fault BEFORE the first emission is retried a bounded number of
///     times with exponential backoff, then the LAST error surfaces on OnError;</item>
///   <item>a non-transient error fails fast — one subscription, no delay;</item>
///   <item>an error AFTER the first emission propagates immediately (a resubscribe there would
///     mint a second Initial into a merge whose per-provider accounting already closed);</item>
///   <item>the classifier matches ONLY the transient connect/timeout class on the BCL
///     <see cref="DbException"/> surface — core does not reference Npgsql.</item>
/// </list>
/// </summary>
public class MeshQueryTransientRetryTest
{
    /// <summary>
    /// Stand-in for a driver exception (NpgsqlException derives from <see cref="DbException"/>);
    /// core never references the driver, so neither does this test.
    /// </summary>
    private sealed class FakeDbException(string message, Exception? inner = null, string? sqlState = null)
        : DbException(message, inner)
    {
        public override string? SqlState { get; } = sqlState;
    }

    /// <summary>The #2521 incident shape: "Failed to connect …" wrapping a connect timeout.</summary>
    private static DbException ConnectTimeout() =>
        new FakeDbException("Failed to connect to 10.42.18.4:5432",
            new TimeoutException("Timeout during connection attempt"));

    // ————————————————————————— classifier

    [Fact]
    public void Classifier_MatchesOnlyTheTransientConnectClass()
    {
        // The incident shape and its network-level variants.
        TransientStorageFaults.IsTransientConnectFault(ConnectTimeout()).Should().BeTrue();
        TransientStorageFaults.IsTransientConnectFault(
            new FakeDbException("connect", new SocketException())).Should().BeTrue();
        TransientStorageFaults.IsTransientConnectFault(
            new FakeDbException("connect", new System.IO.IOException("broken pipe"))).Should().BeTrue();
        // Wrapped one level deeper (providers re-wrap driver faults).
        TransientStorageFaults.IsTransientConnectFault(
            new InvalidOperationException("query failed", ConnectTimeout())).Should().BeTrue();

        // Server-side connection-class SQLSTATEs.
        foreach (var state in new[] { "08000", "08006", "57P03", "53300" })
            TransientStorageFaults.IsTransientConnectFault(
                    new FakeDbException("server refusing", sqlState: state))
                .Should().BeTrue($"SQLSTATE {state} is a transient connect refusal");

        // Real query/schema errors are NOT transient — they must propagate.
        TransientStorageFaults.IsTransientConnectFault(
            new FakeDbException("undefined_table", sqlState: "42P01")).Should().BeFalse();
        TransientStorageFaults.IsTransientConnectFault(
            new FakeDbException("unique_violation", sqlState: "23505")).Should().BeFalse();
        // In-query races belong to the layer owning the statement, not the query fan-in.
        TransientStorageFaults.IsTransientConnectFault(
            new FakeDbException("deadlock_detected", sqlState: "40P01")).Should().BeFalse();

        // A timeout WITHOUT a database exception is a hub/request timeout — different policy.
        TransientStorageFaults.IsTransientConnectFault(new TimeoutException()).Should().BeFalse();
        TransientStorageFaults.IsTransientConnectFault(new OperationCanceledException()).Should().BeFalse();
        TransientStorageFaults.IsTransientConnectFault(null).Should().BeFalse();
    }

    /// <summary>
    /// 🚨 ONE rule, TWO consumers (#2876). This layer decides whether a fault is worth a bounded
    /// RETRY; <c>MeshWeaver.Layout.AreaErrorClassifier.IsStorageUnavailable</c> decides what an area
    /// SHOWS once that retry is spent. They sit on opposite sides of the assembly graph and both
    /// forward to <see cref="MeshWeaver.Data.StorageFaults.IsTransientConnectFault"/>.
    ///
    /// <para>The drift this pins is silent in both directions: a fault the fan-in retries but the
    /// renderer reports as a defect sends an operator hunting for a bug in a view, and an outage
    /// the renderer excuses ("temporarily unavailable, come back later") that this layer never
    /// retried is a real error dressed up as weather. The corpus below is the incident shape and
    /// its boundaries, asserted through BOTH surfaces.</para>
    /// </summary>
    [Fact]
    public void TheRetryAndTheRenderFrame_ClassifyTheSameFaults()
    {
        (Exception? Fault, bool Transient)[] corpus =
        [
            (ConnectTimeout(), true),
            (new FakeDbException("connect", new SocketException()), true),
            (new FakeDbException("connect", new System.IO.IOException("broken pipe")), true),
            (new InvalidOperationException("query failed", ConnectTimeout()), true),
            (new FakeDbException("server refusing", sqlState: "57P03"), true),
            (new FakeDbException("undefined_table", sqlState: "42P01"), false),
            (new FakeDbException("deadlock_detected", sqlState: "40P01"), false),
            (new TimeoutException(), false),
            (new OperationCanceledException(), false),
            (null, false),
        ];

        foreach (var (fault, transient) in corpus)
        {
            TransientStorageFaults.IsTransientConnectFault(fault).Should().Be(transient,
                $"the query fan-in's verdict on {fault?.GetType().Name ?? "null"}");
            MeshWeaver.Layout.AreaErrorClassifier.IsStorageUnavailable(fault).Should().Be(transient,
                "the render frame must reach the SAME verdict — two copies of this rule drift, and "
                + "the drift is invisible from either side");
        }
    }

    // ————————————————————————— retry chain (virtual time)

    [Fact]
    public void TransientFault_RetriesBoundedThenSurfacesLastError()
    {
        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        var last = ConnectTimeout();
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            return Observable.Throw<int>(last, scheduler);
        });

        var observer = scheduler.CreateObserver<int>();
        var retries = new List<(int Attempt, TimeSpan Delay)>();
        source
            .RetryTransientConnect(scheduler: scheduler,
                onRetry: (_, attempt, delay) => retries.Add((attempt, delay)))
            .Subscribe(observer);
        scheduler.Start();

        // 1 initial subscription + DefaultMaxRetries resubscriptions — bounded, never a spin.
        subscribeCount.Should().Be(1 + TransientStorageFaults.DefaultMaxRetries);
        // Exponential backoff: 250 → 500 → 1000 ms.
        retries.Select(r => r.Delay.TotalMilliseconds).Should().Equal(250, 500, 1000);
        // Terminal: the LAST error, surfaced — not swallowed, not replaced.
        observer.Messages.Should().HaveCount(1);
        observer.Messages[0].Value.Kind.Should().Be(NotificationKind.OnError);
        observer.Messages[0].Value.Exception.Should().BeSameAs(last);
    }

    [Fact]
    public void TransientFault_RecoveringWithinBudget_EmitsNormally()
    {
        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            return subscribeCount < 3
                ? Observable.Throw<int>(ConnectTimeout(), scheduler)
                : Observable.Return(42, scheduler);
        });

        var observer = scheduler.CreateObserver<int>();
        source.RetryTransientConnect(scheduler: scheduler).Subscribe(observer);
        scheduler.Start();

        subscribeCount.Should().Be(3);
        observer.Messages.Should().HaveCount(2); // OnNext(42) + OnCompleted
        observer.Messages[0].Value.Value.Should().Be(42);
        observer.Messages[1].Value.Kind.Should().Be(NotificationKind.OnCompleted);
    }

    [Fact]
    public void NonTransientError_FailsFast_NoRetry()
    {
        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            return Observable.Throw<int>(new FakeDbException("undefined_table", sqlState: "42P01"), scheduler);
        });

        var observer = scheduler.CreateObserver<int>();
        source.RetryTransientConnect(scheduler: scheduler).Subscribe(observer);
        scheduler.Start();

        subscribeCount.Should().Be(1);
        observer.Messages.Should().HaveCount(1);
        observer.Messages[0].Value.Kind.Should().Be(NotificationKind.OnError);
    }

    [Fact]
    public void TransientFault_AfterFirstEmission_PropagatesWithoutResubscribe()
    {
        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        // Emits once (the Initial), then dies with a transient fault — the live-feed shape.
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            return Observable.Return(1, scheduler)
                .Concat(Observable.Throw<int>(ConnectTimeout(), scheduler));
        });

        var observer = scheduler.CreateObserver<int>();
        source.RetryTransientConnect(scheduler: scheduler).Subscribe(observer);
        scheduler.Start();

        // No resubscribe: a second attempt would mint a second Initial into a closed merge.
        subscribeCount.Should().Be(1);
        observer.Messages.Should().HaveCount(2);
        observer.Messages[0].Value.Value.Should().Be(1);
        observer.Messages[1].Value.Kind.Should().Be(NotificationKind.OnError);
    }

    // ————————————————————————— through the MeshQuery fan-in (the actual caller)

    private static readonly JsonSerializerOptions Options = new();

    private sealed class FakeProvider(string name, Func<IObservable<QueryResultChange<MeshNode>>> factory)
        : IMeshQueryProvider
    {
        public string Name => name;

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => (IObservable<QueryResultChange<T>>)factory();

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

    private static QueryResultChange<MeshNode> Initial(params MeshNode[] nodes) => new()
    {
        ChangeType = QueryChangeType.Initial,
        Items = nodes,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private static MeshNode Node(string path) => new(path.Split('/').Last(),
        path.Contains('/') ? path[..path.LastIndexOf('/')] : null)
    {
        Name = path,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
    };

    /// <summary>
    /// The fault-injected end-to-end shape: a provider whose FIRST subscription dies with the
    /// #2521 connect timeout and whose second succeeds — exactly what a blip during
    /// <c>OpenNewConnector</c> looks like from the fan-in. The consumer must receive the Initial,
    /// not the error (real scheduler; one 250 ms backoff).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task MeshQuery_TransientConnectBlip_HealsWithinTheBoundedRetry()
    {
        var subscribeCount = 0;
        var flaky = new FakeProvider("flaky", () => Observable.Defer(() =>
            ++subscribeCount == 1
                ? Observable.Throw<QueryResultChange<MeshNode>>(ConnectTimeout())
                : Observable.Return(Initial(Node("a/one")))));

        var query = new MeshQuery([flaky], hub: null!);

        var change = await ((IMeshQueryCore)query)
            .Query<MeshNode>(new MeshQueryRequest { Query = "nodeType:Markdown", Limit = 10 }, Options)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .Await();

        subscribeCount.Should().Be(2, "the fan-in retries the transient connect fault once");
        change.ChangeType.Should().Be(QueryChangeType.Initial);
        change.Items.Select(n => n.Path).Should().Equal("a/one");
    }

    /// <summary>
    /// A PERSISTENT outage exhausts the bound and the consumer sees the real error — the retry
    /// must never convert a hard failure into silence or an empty result.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task MeshQuery_PersistentConnectFailure_SurfacesTheErrorAfterTheBudget()
    {
        var subscribeCount = 0;
        var down = new FakeProvider("down", () => Observable.Defer(() =>
        {
            subscribeCount++;
            return Observable.Throw<QueryResultChange<MeshNode>>(ConnectTimeout());
        }));

        var query = new MeshQuery([down], hub: null!);

        var act = () => ((IMeshQueryCore)query)
            .Query<MeshNode>(new MeshQueryRequest { Query = "nodeType:Markdown", Limit = 10 }, Options)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(15))
            .Await();

        await act.Should().ThrowAsync<DbException>();
        subscribeCount.Should().Be(1 + TransientStorageFaults.DefaultMaxRetries);
    }
}
