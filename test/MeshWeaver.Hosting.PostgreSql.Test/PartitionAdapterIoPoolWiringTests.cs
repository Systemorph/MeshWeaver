using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Issues #1310 / #1312 / #1313 / #1316 — the per-partition Postgres adapters must run their
/// I/O through the mesh's bounded <see cref="IIoPool"/>s, never <see cref="IoPool.Unbounded"/>.
///
/// <para><b>The defect these pin.</b> Every write / single-node read the portal performs goes
/// through an adapter minted by <see cref="PostgreSqlPartitionStorageProvider"/> (one per partition
/// schema, cached by <see cref="PostgreSqlPathRoutingAdapter"/>). Those two construction sites
/// passed <c>readPool:</c> but never <c>ioPool:</c>, so <see cref="PostgreSqlStorageAdapter"/>'s
/// optional parameter fell back to <see cref="IoPool.Unbounded"/> — and the cap-1 <c>pg:Postgres</c>
/// pool the design mandates (<see cref="IoPoolNames.PostgresAdapterPrefix"/>: "the gate IS the
/// connection") was created, held by the provider, and handed to nobody. Aggregate demand against
/// the single shared <c>NpgsqlDataSource</c> was therefore unbounded, while that data source is
/// capped at <c>MaxPoolSize=50</c> in the portal. On 2026-08-12 one memex-cloud pod duly reported
/// "The connection pool has been exhausted, either raise 'Max Pool Size' (currently 50)".</para>
///
/// <para><b>Why a test and not just the fix.</b> An unwired bound is invisible from behaviour: the
/// adapter is functionally identical until the shared pool is genuinely exhausted, which needs ~50
/// concurrent connections and a loaded server. Only the wiring itself is observable cheaply, so
/// that is what is asserted — the same reason the sibling Snowflake backend's wiring
/// (<c>SnowflakePathRoutingAdapter</c> passes <c>ioPool: _provider.WritePool</c>) survived while
/// Postgres' silently did not.</para>
///
/// <para>Pure wiring: no database, no Docker. The connection string is never connected to.</para>
/// </summary>
public class PartitionAdapterIoPoolWiringTests
{
    private const string NeverConnectedConnectionString =
        "Host=localhost;Port=1;Database=never;Username=never;Password=never";

    /// <summary>Provider + the data source it was built over, disposed together.</summary>
    private sealed record ProviderScope(
        PostgreSqlPartitionStorageProvider Provider,
        NpgsqlDataSource DataSource,
        IoPoolRegistry Registry) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            DataSource.Dispose();
            Registry.Dispose();
        }
    }

    private static ProviderScope CreateProvider()
    {
        var dataSource = NpgsqlDataSource.Create(NeverConnectedConnectionString);
        var registry = new IoPoolRegistry();
        return new ProviderScope(
            new PostgreSqlPartitionStorageProvider(
                dataSource,
                NeverConnectedConnectionString,
                new PostgreSqlStorageOptions { ConnectionString = NeverConnectedConnectionString },
                ioPoolRegistry: registry),
            dataSource,
            registry);
    }

    /// <summary>
    /// The routed per-schema adapter — the one every partition read/write actually goes through —
    /// must hold the registry's cap-1 <c>pg:Postgres</c> write pool.
    /// </summary>
    [Theory]
    [InlineData("Edu/Module")]
    [InlineData("rbuergi/SomeNode")]
    [InlineData("Admin/_Access/grant-1")]
    public void RoutedSchemaAdapter_UsesRegistryWritePool_NotUnbounded(string path)
    {
        using var scope = CreateProvider();

        var adapter = scope.Provider.GetSchemaAdapter(path);
        adapter.Should().NotBeNull("'{0}' routes to a partition schema", path);

        adapter!.WritePool.Should().NotBeSameAs(
            IoPool.Unbounded,
            "an unbounded write pool lets concurrent demand exceed the shared NpgsqlDataSource's "
            + "MaxPoolSize (50 in the portal) — the #1310 exhaustion");
        adapter.WritePool.Should().BeSameAs(
            scope.Registry.Get(IoPoolNames.PostgresAdapterPrefix + "Postgres"),
            "the write pool must be the mesh-scoped cap-1 pg:{adapter} pool the provider already owns");
    }

    /// <summary>
    /// The read pool was already wired; assert it too so a future refactor cannot silently drop
    /// one bound while fixing the other.
    /// </summary>
    [Fact]
    public void RoutedSchemaAdapter_UsesRegistryReadPool_NotUnbounded()
    {
        using var scope = CreateProvider();

        var adapter = scope.Provider.GetSchemaAdapter("Edu/Module");
        adapter.Should().NotBeNull();

        adapter!.ReadPool.Should().NotBeSameAs(IoPool.Unbounded);
        adapter.ReadPool.Should().BeSameAs(
            scope.Registry.Get(IoPoolNames.PostgresReadAdapterPrefix + "Postgres"));
    }

    /// <summary>
    /// The two bounds must be DISTINCT pools: reads are capped at
    /// <see cref="IoPoolOptions.PostgresRead"/> so a synced-query fan-out cannot starve writes,
    /// writes at 1 so the gate is the single connection. Sharing one pool would collapse that.
    /// </summary>
    [Fact]
    public void ReadAndWritePools_AreDistinct()
    {
        using var scope = CreateProvider();

        var adapter = scope.Provider.GetSchemaAdapter("Edu/Module");
        adapter!.ReadPool.Should().NotBeSameAs(adapter.WritePool);
    }

    /// <summary>
    /// An <see cref="IIoPool"/> that records that it was asked and then fails the operation with a
    /// NON-transient error, so the adapter's <c>WithTransientRetry</c> does not re-subscribe and no
    /// database is ever contacted. Records only which pool a call landed on — the one thing under
    /// test.
    /// </summary>
    private sealed class RecordingPool(string name, List<string> log) : IIoPool
    {
        public int CurrentInFlight => 0;

        private IObservable<T> Record<T>()
        {
            log.Add(name);
            // NotSupportedException is deliberately NOT in IsTransientConnectionFault's set, so the
            // adapter's retry wrapper lets it through on the first attempt.
            return Observable.Throw<T>(new NotSupportedException(name));
        }

        public IObservable<T> Invoke<T>(Func<CancellationToken, Task<T>> io) => Record<T>();
        public IObservable<T> InvokeStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> source) => Record<T>();
        public IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work) => Record<T>();
        public IObservable<T> SubscribeThroughPool<T>(IObservable<T> source) => Record<T>();
    }

    private static string PoolUsedBy(Func<PostgreSqlStorageAdapter, IObservable<object?>> operation)
    {
        var log = new List<string>();
        using var dataSource = NpgsqlDataSource.Create(NeverConnectedConnectionString);
        var adapter = new PostgreSqlStorageAdapter(
            dataSource,
            readPool: new RecordingPool("read", log),
            ioPool: new RecordingPool("write", log));

        // The operation always errors (RecordingPool never runs the leaf); we only care which pool
        // it asked for. Subscribe synchronously and swallow the recorded failure.
        operation(adapter).Subscribe(_ => { }, _ => { });

        log.Should().ContainSingle("the operation must take exactly one pool slot");
        return log[0];
    }

    /// <summary>
    /// 🚨 The classification itself — the half of #1310 that is NOT about wiring. Reads used to run
    /// on the cap-1 WRITE pool. That is wrong in both directions: unwired (as Postgres was) it left
    /// the hottest read path in the portal completely unbounded against the shared 50-connection
    /// data source; wired (as Snowflake was) it serialises every single-node read in the silo
    /// behind one connection. Reads belong on <c>pg-read:</c>, whose whole purpose is to bound
    /// exactly this fan-out.
    /// </summary>
    [Theory]
    [InlineData("Read")]
    [InlineData("Exists")]
    [InlineData("ResolvePath")]
    [InlineData("FindBestPrefixMatch")]
    [InlineData("GetPartitionMaxTimestamp")]
    [InlineData("ListPartitionSubPaths")]
    public void ReadShapedOperations_RunOnTheReadPool(string operation)
    {
        var options = new System.Text.Json.JsonSerializerOptions();
        var used = PoolUsedBy(a => operation switch
        {
            "Read" => a.Read("p/n", options).Select(n => (object?)n),
            "Exists" => a.Exists("p/n").Select(b => (object?)b),
            "ResolvePath" => a.ResolvePath("p/n", options).Select(n => (object?)n),
            "FindBestPrefixMatch" => a.FindBestPrefixMatch("p/n", options).Select(n => (object?)n),
            "GetPartitionMaxTimestamp" => a.GetPartitionMaxTimestamp("p/n", "sub").Select(t => (object?)t),
            "ListPartitionSubPaths" => a.ListPartitionSubPaths("p/n").Select(l => (object?)l),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        });

        used.Should().Be(
            "read",
            "{0} is a READ — it must be bounded by pg-read:, not filed on the cap-1 write gate",
            operation);
    }

    /// <summary>
    /// The other half: genuine writes must stay on the cap-1 write pool, so moving the reads off it
    /// cannot be "fixed" by moving everything off it.
    /// </summary>
    [Theory]
    [InlineData("Write")]
    [InlineData("Delete")]
    public void WriteShapedOperations_RunOnTheWritePool(string operation)
    {
        var options = new System.Text.Json.JsonSerializerOptions();
        var used = PoolUsedBy(a => operation switch
        {
            "Write" => a.Write(MeshNode.FromPath("p/n"), options).Select(n => (object?)n),
            "Delete" => a.Delete("p/n").Select(s => (object?)s),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        });

        used.Should().Be("write", "{0} is a WRITE — the cap-1 pg: gate IS its connection", operation);
    }

    /// <summary>
    /// The caps themselves, so the wiring assertions above mean what they claim: the write pool is
    /// the cap-1 "gate IS the connection" pool and the read pool is bounded well below the shared
    /// data source's MaxPoolSize.
    /// </summary>
    [Fact]
    public void PoolCaps_BoundTotalDemandBelowSharedConnectionPool()
    {
        var options = new IoPoolOptions();

        options.MaxConcurrencyFor(IoPoolNames.PostgresAdapterPrefix + "Postgres")
            .Should().Be(1, "the pg:{adapter} gate IS the single Npgsql write connection");
        options.MaxConcurrencyFor(IoPoolNames.PostgresReadAdapterPrefix + "Postgres")
            .Should().Be(options.PostgresRead);

        // The portal builds its shared data source with MaxPoolSize=50
        // (memex/aspire/Memex.Portal.Distributed/Program.cs). Reads + writes together must stay
        // comfortably under it, which is the entire point of both bounds.
        (options.PostgresRead + 1).Should().BeLessThan(
            50, "reads + the single write connection must not be able to drain the shared pool");
    }
}
