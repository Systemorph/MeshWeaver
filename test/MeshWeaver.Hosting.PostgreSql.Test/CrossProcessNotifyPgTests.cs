using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Fixture;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// The cross-process change feed, end to end against a real Postgres: a write committed by ONE
/// adapter reaches ANOTHER adapter's feed — through the row trigger, <c>pg_notify</c>, the
/// <c>LISTEN</c> session and the partition router — with no polling and no recycle.
///
/// <para>Two <see cref="PostgreSqlStorageAdapter"/> instances over one database is the model used
/// throughout this repo for two processes (<c>CrossClusterBuildClaimTest</c>): their in-process
/// feeds are separate by construction, so the ONLY way a notification crosses is the wire under
/// test. Before this wiring existed there was no wire at all — the listener registration was
/// commented out for the partitioned setup, so on memex-cloud a pod's mirror could be arbitrarily
/// far behind the other pod's write and would never be told (#1440 → #1814).</para>
///
/// <para>Uses <c>PostgreSqlIsolated</c>: the shared container's neighbouring tests write into their
/// own schemas and would fire <c>pg_notify</c> on the same channel.</para>
/// </summary>
[Collection("PostgreSqlIsolated")]
public class CrossProcessNotifyPgTests(IsolatedPostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string Partition = "Chess";
    private const string Schema = "chess";

    private readonly JsonSerializerOptions _options = new();
    private NpgsqlDataSource _writerDataSource = null!;
    private PostgreSqlStorageAdapter _processA = null!;
    private PostgreSqlPartitionStorageProvider _processB = null!;
    // The CONTROL: a third process wired exactly like B except that nothing listens — i.e. the
    // state `main` is in. Same database, same write, same instant, so "the notification arrived"
    // cannot be explained by anything but the wire under test.
    private PostgreSqlPartitionStorageProvider _processCWithoutListener = null!;
    private PartitionChangeRouter _router = null!;
    private PostgreSqlChangeListener _listener = null!;

    public async ValueTask InitializeAsync()
    {
        await fixture.CleanDataAsync();

        // Process A: its own adapter over the partition's schema — the writer.
        var (writerDataSource, writer) = await fixture.CreateSchemaAdapterAsync(
            Schema, ct: TestContext.Current.CancellationToken);
        _writerDataSource = writerDataSource;
        _processA = writer;

        // Process B: a full partitioned provider, plus the wiring this change adds — the listener
        // over its own dedicated connection, routing through the partition router.
        _processB = new PostgreSqlPartitionStorageProvider(
            fixture.DataSource, fixture.ConnectionString, new PostgreSqlStorageOptions());
        _processCWithoutListener = new PostgreSqlPartitionStorageProvider(
            fixture.DataSource, fixture.ConnectionString, new PostgreSqlStorageOptions());
        _router = new PartitionChangeRouter(_processB);
        _listener = PostgreSqlChangeListener.OwningDataSource(
            _processB.CreateChangeListenerDataSource(), _router);
        await _listener.StartAsync(TestContext.Current.CancellationToken);
        // The LISTEN session has to be OPEN before the write, or the NOTIFY has no subscriber —
        // Postgres does not replay. Poll pg_stat_activity for the named session rather than sleep:
        // a fixed delay is either flaky or slow, and this one is neither.
        await WaitForListenSession();
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
        _processB.Dispose();
        _processCWithoutListener.Dispose();
        await _writerDataSource.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    public async Task AWriteByAnotherProcess_ArrivesOnThisProcessesPartitionFeed()
    {
        // Materialise process B's adapter for the partition, exactly as any read of it would.
        var mirror = _processB.GetSchemaAdapter($"{Partition}/Board");
        mirror.Should().NotBeNull();

        var received = new List<DataChangeNotification>();
        using var sub = mirror!.Changes.Subscribe(n => { lock (received) received.Add(n); });

        var control = _processCWithoutListener.GetSchemaAdapter($"{Partition}/Board")!;
        var receivedByControl = new List<DataChangeNotification>();
        using var controlSub = control.Changes.Subscribe(n =>
        {
            lock (receivedByControl) receivedByControl.Add(n);
        });

        await _processA.WriteAsync(
            new MeshNode("Board", Partition) { Name = "written-by-A", NodeType = "Markdown" },
            _options, TestContext.Current.CancellationToken);

        var arrived = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => { lock (received) return received.ToArray(); })
            .Where(snap => snap.Any(n => n.Path == $"{Partition}/Board"))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(TestContext.Current.CancellationToken);

        var notification = arrived.First(n => n.Path == $"{Partition}/Board");
        notification.Kind.Should().Be(DataChangeKind.Created);
        notification.Entity.Should().BeNull(
            "a LISTEN payload is {path, op} and nothing else — populating the entity from it would "
            + "be the RLS bypass #1250 removed, which is exactly why the consumer must RE-READ");
        _router.RoutedCount.Should().BeGreaterThan(0L);

        // 🚨 The control, evaluated at the moment B's notification landed: an identically-configured
        // process with no listener sees NOTHING. That is the state this change ends — and it is why
        // "the mirror is stale" was a missing edge rather than a lost race, which no retry can win.
        lock (receivedByControl)
            receivedByControl.Should().BeEmpty(
                "without the listener there is no wire at all — a per-schema feed only ever carried "
                + "its OWN process's writes, which is #1440");
    }

    [Fact(Timeout = 120_000)]
    public async Task ADeleteByAnotherProcess_ArrivesToo()
    {
        var mirror = _processB.GetSchemaAdapter($"{Partition}/Doomed")!;
        await _processA.WriteAsync(
            new MeshNode("Doomed", Partition) { Name = "doomed", NodeType = "Markdown" },
            _options, TestContext.Current.CancellationToken);

        var received = new List<DataChangeNotification>();
        using var sub = mirror.Changes.Subscribe(n => { lock (received) received.Add(n); });

        await _processA.Delete($"{Partition}/Doomed").ToTask(TestContext.Current.CancellationToken);

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => { lock (received) return received.ToArray(); })
            .Where(snap => snap.Any(n =>
                n.Path == $"{Partition}/Doomed" && n.Kind == DataChangeKind.Deleted))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Waits until the listener's named session is visible in <c>pg_stat_activity</c> — the positive
    /// "the LISTEN is open" signal. Bounded: no session inside the budget fails the test rather than
    /// letting every assertion below time out on a missing subscription.
    /// </summary>
    private Task WaitForListenSession()
        => Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => fixture.DataSource.ScalarLong(
                "SELECT count(*) FROM pg_stat_activity "
                + "WHERE application_name = 'meshweaver-change-listener'",
                TestContext.Current.CancellationToken))
            .Where(count => count > 0)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .Select(_ => System.Reactive.Unit.Default)
            .ToTask(TestContext.Current.CancellationToken);
}
