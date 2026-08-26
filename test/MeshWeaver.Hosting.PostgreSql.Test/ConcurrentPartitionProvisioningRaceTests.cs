using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Issue #2130 — two sessions provisioning the SAME partition at the same time must both end up
/// with a fully provisioned partition. Neither may lose its install.
///
/// <para><b>The defect.</b> All per-partition DDL funnels through
/// <c>public.ensure_partition_schema</c>, whose first statement is
/// <c>CREATE SCHEMA IF NOT EXISTS</c>. That guard is <b>not</b> race-free: PostgreSQL performs the
/// existence check and the <c>pg_namespace</c> insert as two separate steps under no common lock,
/// so a second session can pass the check while a first session's insert is still uncommitted, then
/// block on the catalog's unique index and — the moment the first commits — fail with
/// <c>23505: duplicate key value violates unique constraint "pg_namespace_nspname_index"</c>.
/// <c>ExecuteDdlWithRetryAsync</c> retried <c>40P01</c>/<c>40001</c>/"tuple concurrently updated"
/// but NOT that, so the loser's provisioning faulted, <c>InstanceAutoRegistrationService</c>'s
/// <c>[DefaultInstall]</c> logged it and moved on, and the pod came up healthy while silently
/// MISSING whichever package lost the race (observed in prod: AppleMaps, ICloud, GoogleMaps,
/// Agent). Different replicas of one deployment ended up with different package sets.</para>
///
/// <para><b>How the race is induced — deterministically, and with no mocking.</b> An ordinary
/// second connection opens a transaction and runs a bare <c>CREATE SCHEMA</c> for the partition,
/// leaving the catalog row written but uncommitted. Provisioning is then started on the provider
/// and <i>observed to block</i> on that row (a real <c>pg_stat_activity</c> lock wait — this is the
/// anti-vacuous guard: without it a mistimed commit would let provisioning sail through and the
/// test would pass having raced nothing). Committing the blocker then hands the provider the exact
/// 23505 production saw.</para>
///
/// <para><b>Fail-before / pass-after.</b> Before the fix the provisioning observable faults with
/// that <c>PostgresException</c> and the partition is left as a bare schema with no tables — the
/// dropped install. After it, the bounded jittered retry re-runs the idempotent proc, which now
/// observes the winner's committed schema, creates every table, and the partition takes writes.</para>
/// </summary>
[Collection("PostgreSql")]
public class ConcurrentPartitionProvisioningRaceTests(PostgreSqlFixture fixture)
{
    /// <summary>Captures the provider's retry diagnostics — the proof the race was REAL.</summary>
    private sealed class CapturingLogger : ILogger<PostgreSqlPartitionStorageProvider>
    {
        private readonly List<string> lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (lines) return lines.ToArray(); }
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception) + " " + (exception?.ToString() ?? string.Empty);
            lock (lines) lines.Add(line);
        }
    }

    private IObservable<long> SchemaCount(string schema, CancellationToken ct) =>
        fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s",
            [("s", (object)schema)], ct);

    /// <summary>
    /// Backends parked on a lock. A catalog unique-index conflict against an uncommitted tuple
    /// waits on the inserter's transaction id, which surfaces here as
    /// <c>wait_event_type = 'Lock'</c>.
    /// </summary>
    private IObservable<long> BlockedBackends(CancellationToken ct) =>
        fixture.DataSource.ScalarLong(
            """
            SELECT COUNT(*) FROM pg_stat_activity
            WHERE datname = current_database()
              AND state = 'active'
              AND wait_event_type = 'Lock'
            """, ct);

    [Fact(Timeout = 120000)]
    public async Task AConcurrentSchemaCreator_DoesNotDropTheLosersProvisioning()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger();
        var provider = new PostgreSqlPartitionStorageProvider(
            fixture.DataSource, fixture.ConnectionString, fixture.Options, logger: logger);

        // Lowercase by construction — the router lowercases a namespace to reach its schema, so the
        // blocker below must create exactly the name provisioning will resolve to.
        var part = $"race{Guid.NewGuid():N}"[..12];

        await using var blocker = await fixture.DataSource.OpenConnectionAsync(ct);
        var uncommitted = await blocker.BeginTransactionAsync(ct);
        var committed = false;
        try
        {
            // ── The winner: a catalog row written and held uncommitted. ──────────────────────
            await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{part}\"", blocker, uncommitted))
                await create.ExecuteNonQueryAsync(ct);

            // ── The loser: provisioning starts, passes IF NOT EXISTS, and parks on the index. ─
            var provisioning = provider.EnsurePartitionProvisioned(part)
                .Timeout(TimeSpan.FromSeconds(60))
                .FirstAsync()
                .ToTask(ct);

            // 🚨 The anti-vacuous guard. Commit before the loser is actually waiting and it simply
            // observes a committed schema — no 23505, nothing retried, and a green test that
            // exercised none of this. Wait for the real lock wait instead of guessing with a delay.
            await Observable.Interval(TimeSpan.FromMilliseconds(50))
                .StartWith(0L)
                .SelectMany(_ => BlockedBackends(ct))
                .Where(blocked => blocked > 0)
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(ct);

            // ── The winner commits. The loser's insert now conflicts: 23505 on a pg_* index. ──
            await uncommitted.CommitAsync(ct);
            committed = true;

            // 🚨 THE REGRESSION: this used to fault with the unique violation, leaving a bare
            // schema and a package that never installed.
            await provisioning;

            // The race was genuinely run and genuinely recovered — not avoided.
            Assert.Contains(logger.Lines, l =>
                l.Contains("transient concurrent-DDL error", StringComparison.OrdinalIgnoreCase));

            // Exactly one schema: the retry re-observed the winner's, it did not create a second.
            await SchemaCount(part, ct).Should().Within(30.Seconds()).Be(1L);

            // …and the consequence that actually hurt: nothing was dropped — the partition is
            // fully provisioned (the proc's tables exist) and takes writes.
            var node = new MeshNode("Installed", part) { Name = "Installed", NodeType = "Markdown" };
            await provider.Adapter.Write(node, JsonSerializerOptions.Default)
                .Should().Within(30.Seconds()).Emit();
            await fixture.DataSource.ScalarLong($"SELECT COUNT(*) FROM \"{part}\".mesh_nodes", ct)
                .Should().Within(30.Seconds()).Be(1L);
        }
        finally
        {
            if (!committed)
                await uncommitted.RollbackAsync(ct);
            await uncommitted.DisposeAsync();
            await fixture.DataSource.ExecuteNonQuery($"DROP SCHEMA IF EXISTS \"{part}\" CASCADE", ct)
                .Should().Within(30.Seconds()).Emit();
            await fixture.DataSource.ExecuteNonQuery(
                    $"DELETE FROM public.searchable_schemas WHERE schema_name = '{part}'", ct)
                .Should().Within(30.Seconds()).Emit();
        }
    }
}
