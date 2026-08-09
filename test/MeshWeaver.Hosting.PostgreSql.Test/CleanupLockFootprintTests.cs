using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Measures what <see cref="PostgreSqlFixture.CleanDataAsync"/> costs the Postgres LOCK TABLE, and
/// pins that the cost does not grow with the number of partition schemas the run has accumulated
/// (issue #977).
///
/// <para><b>The defect this measures.</b> Cleanup emptied every <c>(schema, table)</c> pair in the
/// container from ONE command. Npgsql sends a multi-statement command as a single implicit
/// transaction, so every targeted table AND every one of its indexes was locked simultaneously
/// (<c>mesh_nodes</c> alone carries 11 indexes). PostgreSQL's lock table is a fixed shared-memory
/// array — <c>max_locks_per_transaction × (max_connections + max_prepared_transactions)</c>, 6400
/// slots at this container's defaults — and nothing ever dropped a partition schema, so the price
/// every later test paid only ever went up, until a test tipped it over into
/// <c>Npgsql.PostgresException 53200: out of shared memory</c>. The victim was whichever test ran at
/// the tipping point, which is why it read as an unrelated regression in that test's own class.</para>
///
/// <para><b>Why a measurement and not an integration run.</b> The failure is CI-only BY
/// CONSTRUCTION: a local container is short-lived and never accumulates ~50 partition schemas, so a
/// green suite proves nothing about it. What proves the fix is the SHAPE of the cost — this test
/// accumulates partitions itself and reads the lock count straight out of <c>pg_locks</c> from
/// inside the very transaction that holds them, so the number is exact and never races.</para>
/// </summary>
[Collection("PostgreSql")]
public class CleanupLockFootprintTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    /// <summary>Partitions accumulated for the first measurement.</summary>
    private const int SmallAccumulation = 4;

    /// <summary>Partitions accumulated for the second measurement — 5× the first.</summary>
    private const int LargeAccumulation = 20;

    /// <summary>
    /// The lock footprint of the cleanup must be a function of the BATCH BOUND, not of how many
    /// partition schemas the container has accumulated.
    ///
    /// <para>Both shapes are produced by the shipping <see cref="PostgreSqlFixture.BuildCleanupBatches"/>
    /// — the pre-fix shape is simply the same call with the bound set to "everything", so this
    /// compares the real code against its own unbounded predecessor rather than a re-implementation
    /// of it.</para>
    /// </summary>
    [Fact(Timeout = 600000)]
    public async Task CleanupLockFootprint_StaysBounded_WhileUnchunkedGrowsWithAccumulatedPartitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = "zzlock" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // ── Accumulate a handful of partitions, and measure. ──────────────────────────────
            await AccumulatePartitionsAsync(prefix, 0, SmallAccumulation, ct);
            var smallTargets = await CleanupTargetsAsync(prefix, ct);
            var smallUnchunked = await PeakRelationLocksAsync(Unchunked(smallTargets), ct);
            var smallChunked = await PeakRelationLocksAsync(
                PostgreSqlFixture.BuildCleanupBatches(smallTargets), ct);

            // ── Accumulate 5× as many, and measure the same two shapes again. ─────────────────
            await AccumulatePartitionsAsync(prefix, SmallAccumulation, LargeAccumulation, ct);
            var largeTargets = await CleanupTargetsAsync(prefix, ct);
            var largeUnchunked = await PeakRelationLocksAsync(Unchunked(largeTargets), ct);
            var largeChunked = await PeakRelationLocksAsync(
                PostgreSqlFixture.BuildCleanupBatches(largeTargets), ct);

            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"""
                 partitions={SmallAccumulation}  pairs={smallTargets.Count}  unchunked={smallUnchunked} locks  chunked={smallChunked} locks
                 partitions={LargeAccumulation}  pairs={largeTargets.Count}  unchunked={largeUnchunked} locks  chunked={largeChunked} locks
                 """));

            // The premise: the work itself IS proportional to accumulated partitions. 5× the
            // partitions, 5× the (schema, table) pairs to empty. That is inherent and fine — what
            // must not be proportional is how many of those locks are held AT ONCE.
            largeTargets.Count.Should().Be(
                smallTargets.Count * LargeAccumulation / SmallAccumulation,
                "each accumulated partition adds the same fixed set of tables to clean");

            // 🚨 The defect, reproduced: as a single transaction, the lock footprint tracks the
            // pair count — 5× the partitions, ~5× the locks held simultaneously. Extrapolated to a
            // full suite's worth of accumulated schemas, that is what reaches 53200.
            largeUnchunked.Should().BeGreaterThan(
                smallUnchunked * 4,
                "unchunked, every accumulated partition's tables and indexes are locked at once");

            // 🚨 The fix: chunked, the peak is set by MaxTablesPerCleanupBatch alone. Accumulating
            // 5× the partitions does not raise it at all.
            largeChunked.Should().Be(
                smallChunked,
                "the chunk bound, not the accumulated schema count, decides the lock footprint");

            // And the bound is a real reduction, not a rounding: cleaning 20 partitions chunked
            // holds fewer locks at once than cleaning 4 partitions in one transaction did.
            largeChunked.Should().BeLessThan(
                smallUnchunked,
                "the bounded footprint is below the unbounded one at a fifth of the load");

            // No batch may exceed the bound — the property the lock count follows from.
            foreach (var batch in PostgreSqlFixture.BuildCleanupBatches(largeTargets))
                CountDeletes(batch).Should().BeLessThanOrEqualTo(PostgreSqlFixture.MaxTablesPerCleanupBatch);
        }
        finally
        {
            // The fixture created these schemas, so the fixture drops them — the same call
            // CleanDataAsync makes. Nothing is left behind for the next test to pay for.
            await fixture.DropTrackedSchemasAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The source-side half of #977: a schema the fixture created is GONE after a clean, together
    /// with its <c>public.searchable_schemas</c> registration — so accumulation stops at zero
    /// instead of growing for the container's lifetime.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task CleanData_DropsTheSchemasTheFixtureCreated_AndDeregistersThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = "zzdrop" + Guid.NewGuid().ToString("N")[..8];

        await fixture.CreateSchemaAdapterAsync(schema, StandardPartition(schema), ct);
        // Register it the way a provisioned partition registers itself, so the de-registration is
        // actually exercised rather than trivially true.
        await using (var register = fixture.DataSource.CreateCommand(
            "INSERT INTO public.searchable_schemas (schema_name) VALUES ($1) ON CONFLICT DO NOTHING"))
        {
            register.Parameters.AddWithValue(schema);
            await register.ExecuteNonQueryAsync(ct);
        }

        (await SchemaCountAsync(schema, ct)).Should().Be(1L, "the fixture just created it");
        (await CleanupTargetsAsync(schema, ct)).Count.Should()
            .BeGreaterThan(0, "its tables are cleanup targets while it exists");

        await fixture.CleanDataAsync(ct);

        (await SchemaCountAsync(schema, ct)).Should().Be(0L, "CleanData drops what the fixture created");
        (await CleanupTargetsAsync(schema, ct)).Count.Should()
            .Be(0, "a dropped schema costs later tests nothing");
        (await RegisteredCountAsync(schema, ct)).Should()
            .Be(0L, "a searchable_schemas row naming a dropped schema would break the cross-schema union");
    }

    /// <summary>
    /// 🚨 A schema the fixture did NOT create survives a clean, even when a test builds an adapter
    /// over it. Ownership means "we created it", never "we have seen the name".
    ///
    /// <para><b>Why this is the sharpest edge of the drop.</b> <c>CREATE SCHEMA IF NOT EXISTS</c>
    /// succeeds silently on a schema somebody else made, so tracking every adapted name would make
    /// CleanData drop a partition the mesh provisioned — destructively, silently, and only once some
    /// future test adapts a pre-existing partition. The blast radius is another test's data, which
    /// surfaces as cross-test bleed that looks like an unrelated defect: exactly the confusion #977
    /// cost time on in the first place.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task CleanData_LeavesAPreExistingSchemaStanding_WhenATestOnlyAdaptsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = "zzpre" + Guid.NewGuid().ToString("N")[..8];

        // Provisioned by somebody else — the mesh's own partition DDL, exactly how a real partition
        // arrives in the container.
        await using (var provision = fixture.DataSource.CreateCommand(
            "SELECT public.ensure_partition_schema($1)"))
        {
            provision.Parameters.AddWithValue(schema);
            await provision.ExecuteNonQueryAsync(ct);
        }
        try
        {
            // A test now adapts it. The CREATE inside raises 42P06 — not ours.
            await fixture.CreateSchemaAdapterAsync(schema, StandardPartition(schema), ct);

            await fixture.CleanDataAsync(ct);

            (await SchemaCountAsync(schema, ct)).Should()
                .Be(1L, "the fixture did not create this schema, so it is not the fixture's to drop");
        }
        finally
        {
            await using var drop = fixture.DataSource.CreateCommand(
                $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A framework schema <see cref="PostgreSqlFixture.InitializeAsync"/> provisions once per
    /// container survives a clean even when a test built an adapter over it — dropping it would
    /// take the container's framework state (the V27 access-object mirror) with it.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task CleanData_NeverDropsAFrameworkSchema_EvenWhenATestAdaptsIt()
    {
        var ct = TestContext.Current.CancellationToken;

        await fixture.CreateSchemaAdapterAsync("auth", null, ct);
        await fixture.CleanDataAsync(ct);

        (await SchemaCountAsync("auth", ct)).Should().Be(1L, "auth is framework state, not test data");
        (await SchemaCountAsync("system_access", ct)).Should().Be(1L, "so is system_access");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pre-#977 shape: ONE batch holding every DELETE, i.e. the same builder with the bound
    /// lifted to the whole target list.
    /// </summary>
    private static IReadOnlyList<string> Unchunked(IReadOnlyList<(string Schema, string Table)> targets)
        => PostgreSqlFixture.BuildCleanupBatches(targets, Math.Max(targets.Count, 1));

    /// <summary>A standard content partition — the satellite layout a real Space/User carries.</summary>
    private static PartitionDefinition StandardPartition(string schema) => new()
    {
        Namespace = schema,
        Schema = schema,
        TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
        NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
    };

    /// <summary>
    /// Provisions partitions <paramref name="from"/>..<paramref name="to"/> through the fixture's
    /// own <see cref="PostgreSqlFixture.CreateSchemaAdapterAsync"/> — real schemas with the real
    /// table and index layout, so the lock counts measured are the ones production cleanup incurs.
    /// The per-schema data sources are released immediately (each holds a physical connection).
    /// </summary>
    private async Task AccumulatePartitionsAsync(string prefix, int from, int to, CancellationToken ct)
    {
        for (var i = from; i < to; i++)
        {
            var schema = prefix + i.ToString(CultureInfo.InvariantCulture);
            await fixture.CreateSchemaAdapterAsync(schema, StandardPartition(schema), ct);
        }
        await fixture.DisposeTrackedSchemaDataSourcesAsync();
    }

    /// <summary>
    /// The cleanup targets the fixture itself would resolve, narrowed to the schemas this test
    /// accumulated so the measurement is unaffected by whatever else the collection left behind.
    /// </summary>
    private async Task<IReadOnlyList<(string Schema, string Table)>> CleanupTargetsAsync(
        string prefix, CancellationToken ct)
        => (await fixture.DiscoverPerSchemaCleanupTargetsAsync(ct))
            .Where(t => t.Schema.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// Executes each batch in its own transaction and reads, FROM INSIDE that transaction, how many
    /// relation locks it holds — <c>pg_locks</c> filtered to this backend is the exact set of locks
    /// the batch put in the shared lock table, so there is nothing to sample and nothing to race.
    /// Every transaction is rolled back: this measures the cost, it does not perform the cleanup.
    /// </summary>
    private async Task<int> PeakRelationLocksAsync(IReadOnlyList<string> batches, CancellationToken ct)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(ct);
        var peak = 0;
        foreach (var batch in batches)
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(batch, connection, tx))
                await cmd.ExecuteNonQueryAsync(ct);
            await using (var probe = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks WHERE pid = pg_backend_pid() AND locktype = 'relation'",
                connection, tx))
                peak = Math.Max(peak, (int)(long)(await probe.ExecuteScalarAsync(ct))!);
            await tx.RollbackAsync(ct);
        }
        return peak;
    }

    private async Task<long> SchemaCountAsync(string schema, CancellationToken ct)
    {
        await using var cmd = fixture.DataSource.CreateCommand(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = $1");
        cmd.Parameters.AddWithValue(schema);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<long> RegisteredCountAsync(string schema, CancellationToken ct)
    {
        await using var cmd = fixture.DataSource.CreateCommand(
            "SELECT COUNT(*) FROM public.searchable_schemas WHERE schema_name = $1");
        cmd.Parameters.AddWithValue(schema);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static int CountDeletes(string batch)
        => batch.Split("DELETE FROM", StringSplitOptions.None).Length - 1;
}
