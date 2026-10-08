using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>One transient database fault must not end the live NodeType-record census for the
/// process's life</b> (MeshWeaver#6183).
///
/// <para><b>The incident.</b> memex-cloud, <c>memex-portal-deployment-65fcf777bc-pt5dc</c>,
/// 2026-10-05 23:30:44Z: <c>NpgsqlException: Exception while reading from stream → IOException →
/// SocketException (104) Connection reset by peer</c> while the PostgreSQL provider streamed the
/// catalog's rows. The census subscription terminated, and nothing re-opened it until the replica
/// restarted.</para>
///
/// <para><b>The reproduction is the real mesh.</b> The real <c>MeshQuery</c> fan-in, the real
/// synced-query cache (which evicts the faulted chain), the real census fold. The one injected
/// piece is a test <see cref="IMeshQueryProvider"/> — the extension point every backend plugs into
/// — that answers the catalog query and then dies mid-stream with the incident's exception shape,
/// on a switch the test throws. Core never references Npgsql, so the driver exception is a
/// <see cref="DbException"/> carrying the same inner chain.</para>
///
/// <para><b>The controls.</b> <see cref="NegativeControl_TheRawCensus_TerminatesOnTheSameFault"/>
/// runs the SAME fault through the census as the hosted service held it before this fix and proves
/// the fault reaches it and ends it — so the fixed arm passing is not the injected fault failing to
/// arrive. <see cref="ANonTransientFault_StillEndsTheWatch_WithoutReopening"/> proves the re-open is
/// TYPED: a defect is not retried.</para>
/// </summary>
public class LiveRecordCensusReopensAfterTransientFaultTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan QuickBackoff = TimeSpan.FromMilliseconds(50);

    private readonly CatalogFaultProvider provider = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(provider));

    private sealed class FakeDbException(string message, Exception inner) : DbException(message, inner);

    /// <summary>The #6183 shape: the driver's read fault wrapping a reset socket.</summary>
    private static Exception ConnectionResetMidRead() =>
        new FakeDbException("Exception while reading from stream",
            new IOException("Unable to read data from the transport connection: Connection reset by peer.",
                new SocketException(104)));

    [HubFact]
    public async Task ATransientFaultMidStream_ReopensTheCensus_AndItReadsAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var reopened = new AsyncSubject<Exception>();
        var reopenedFlag = 0;
        var afterReopen = new ReplaySubject<NodeTypeLiveRecordCensus>();
        var terminal = new AsyncSubject<Exception>();

        using var watch = DynamicTypePreWarmer.WatchLiveRecordCensus(
                Mesh, DateTimeOffset.UtcNow.AddHours(-1),
                (ex, attempt, wait) =>
                {
                    Volatile.Write(ref reopenedFlag, 1);
                    reopened.OnNext(ex);
                    reopened.OnCompleted();
                },
                backoff: _ => QuickBackoff)
            .Subscribe(
                census =>
                {
                    if (Volatile.Read(ref reopenedFlag) == 1)
                        afterReopen.OnNext(census);
                    provider.Readings.OnNext(census);
                },
                ex => { terminal.OnNext(ex); terminal.OnCompleted(); });

        await provider.Readings.Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the census must read the catalog once before the fault is injected", ct);
        var opened = provider.CatalogSubscriptions;
        opened.Should().BeGreaterThanOrEqualTo(1, "the injected provider answers the census's catalog query");

        provider.Fail(ConnectionResetMidRead());

        var reason = await reopened
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a connection reset mid-read is a TRANSIENT infrastructure fault — the watch must "
                  + "re-open from it rather than end", ct);
        reason.Should().BeAssignableTo<DbException>();

        await afterReopen.Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("after the re-open the census must READ AGAIN — a re-open that is replayed the "
                  + "latched fault from the cache would never produce a reading", ct);
        provider.CatalogSubscriptions.Should().BeGreaterThan(opened,
            "the re-open must open a FRESH upstream: the faulted chain was evicted, and replaying it "
            + "would re-deliver the same fault");
        await terminal
            .Should().NotEmit(TimeSpan.FromMilliseconds(500),
                "one transient fault must not end the watch", ct);
    }

    [HubFact]
    public async Task NegativeControl_TheRawCensus_TerminatesOnTheSameFault()
    {
        var ct = TestContext.Current.CancellationToken;
        var terminal = new AsyncSubject<Exception>();
        using var raw = DynamicTypePreWarmer.ObserveLiveRecordCensus(Mesh, DateTimeOffset.UtcNow.AddHours(-1))
            .Subscribe(
                census => provider.Readings.OnNext(census),
                ex => { terminal.OnNext(ex); terminal.OnCompleted(); });

        await provider.Readings.Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the census must read the catalog once before the fault is injected", ct);

        provider.Fail(ConnectionResetMidRead());

        var fault = await terminal
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the injected fault must REACH the census and end it — the latch #6183 reports. "
                  + "Without this the fixed arm could pass because no fault ever arrived", ct);
        fault.Should().BeAssignableTo<DbException>();
    }

    [HubFact]
    public async Task ANonTransientFault_StillEndsTheWatch_WithoutReopening()
    {
        var ct = TestContext.Current.CancellationToken;
        var reopens = 0;
        var terminal = new AsyncSubject<Exception>();
        using var watch = DynamicTypePreWarmer.WatchLiveRecordCensus(
                Mesh, DateTimeOffset.UtcNow.AddHours(-1),
                (_, _, _) => Interlocked.Increment(ref reopens),
                backoff: _ => QuickBackoff)
            .Subscribe(
                census => provider.Readings.OnNext(census),
                ex => { terminal.OnNext(ex); terminal.OnCompleted(); });

        await provider.Readings.Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the census must read the catalog once before the fault is injected", ct);

        provider.Fail(new InvalidOperationException("a defect in the catalog read, not the infrastructure"));

        var fault = await terminal
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a fault that is not a typed infrastructure fault must end the watch on its first "
                  + "occurrence — re-reading a defect only repeats it on a timer", ct);
        fault.Should().BeOfType<InvalidOperationException>();
        Volatile.Read(ref reopens).Should().Be(0);
    }

    /// <summary>
    /// The gap is RECORDED and then CLEARED: an interruption degrades /health while the watch is
    /// re-opening, and the next reading proves it is live again. A terminal fault is never followed
    /// by a reading, so it keeps degrading — that half is pinned by <c>LiveRecordCensusTest</c>.
    /// </summary>
    [Fact]
    public void AnInterruption_DegradesUntilTheNextReading()
    {
        var registry = new NodeTypeBakeReportRegistry();
        var reading = NodeTypeLiveRecordCensus.Of(
            [], "live-framework", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);
        registry.RecordLiveRecords(reading);
        registry.RecordLiveRecordsFault("DbException interrupted the catalog subscription — re-opening (1/5) in 2s");
        registry.LiveRecordsFault.Should().NotBeNull("the gap must be visible while the watch re-opens");

        registry.RecordLiveRecords(reading);

        registry.LiveRecordsFault.Should().BeNull("a reading proves the re-opened watch is live again");
    }

    [Fact]
    public void TheClassification_IsTyped()
    {
        StandingWatchRecovery.IsTransient(ConnectionResetMidRead()).Should().BeTrue();
        StandingWatchRecovery.IsTransient(
            new QueryProviderStalledException("P", TimeSpan.FromSeconds(15), "nodeType:NodeType", "system-security"))
            .Should().BeTrue("the fan-in's stall terminal is an availability failure, never a verdict");
        StandingWatchRecovery.IsTransient(new InvalidOperationException("defect")).Should().BeFalse();
        StandingWatchRecovery.IsTransient(new JsonException("bad content")).Should().BeFalse();
        StandingWatchRecovery.IsTransient(new ObjectDisposedException("mesh")).Should().BeFalse();
        StandingWatchRecovery.IsTransient(null).Should().BeFalse();

        // An aggregate is transient only when EVERY branch is: a mixed one carries a real defect.
        StandingWatchRecovery.IsTransient(
                new AggregateException(ConnectionResetMidRead(), new JsonException("bad content")))
            .Should().BeFalse("a transient branch must not launder a defect in another branch");
        StandingWatchRecovery.IsTransient(
                new AggregateException(new JsonException("bad content"), ConnectionResetMidRead()))
            .Should().BeFalse("branch order must not matter");
        StandingWatchRecovery.IsTransient(
                new AggregateException(ConnectionResetMidRead(), ConnectionResetMidRead()))
            .Should().BeTrue();
        StandingWatchRecovery.IsTransient(
                new InvalidOperationException("wrapped",
                    new AggregateException(ConnectionResetMidRead(), new JsonException("bad content"))))
            .Should().BeFalse("a mixed aggregate deeper in the chain is still mixed");
    }

    /// <summary>
    /// Answers the census's catalog query (<c>nodeType:NodeType</c>) with an empty slice, so the
    /// rest of the mesh's providers supply the real records. Every catalog subscription opened
    /// BEFORE <see cref="Fail"/> then dies with the given fault, mid-stream, after its Initial;
    /// every one opened after it stays healthy — the reset happened once.
    /// </summary>
    private sealed class CatalogFaultProvider : IMeshQueryProvider
    {
        private readonly AsyncSubject<Exception> fault = new();
        private int catalogSubscriptions;

        /// <summary>Readings the test's census subscription saw (fed by the test, read by the test).</summary>
        public ReplaySubject<NodeTypeLiveRecordCensus> Readings { get; } = new();

        public int CatalogSubscriptions => Volatile.Read(ref catalogSubscriptions);

        public void Fail(Exception error)
        {
            fault.OnNext(error);
            fault.OnCompleted();
        }

        public string Name => nameof(CatalogFaultProvider);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => false;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options) =>
            Observable.Defer(() =>
            {
                var initial = Observable.Return(new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = Array.Empty<T>(),
                    Timestamp = DateTimeOffset.UtcNow,
                });
                var catalog = request.EffectiveQueries.Any(q =>
                    q.Contains($"nodeType:{MeshNode.NodeTypePath}", StringComparison.OrdinalIgnoreCase));
                if (!catalog)
                    return initial;
                Interlocked.Increment(ref catalogSubscriptions);
                // Already failed ⇒ this is a re-open after the reset: healthy, and stays open.
                if (fault.IsCompleted)
                    return initial.Concat(Observable.Never<QueryResultChange<T>>());
                return initial.Concat(fault.SelectMany(ex => Observable.Throw<QueryResultChange<T>>(ex)));
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
