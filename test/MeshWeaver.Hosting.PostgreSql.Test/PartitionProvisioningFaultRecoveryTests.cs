using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Issue #1369 — a TRANSIENT provisioning failure must not permanently break a partition.
///
/// <para><b>The defect.</b> <see cref="PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned"/>
/// promise-caches its <c>CREATE SCHEMA</c> round-trip so it runs at most once per (silo, schema).
/// The cache used to be a bare <c>ConcurrentDictionary&lt;string, IObservable&lt;Unit&gt;&gt;</c>, and
/// <c>IIoPool.Run</c> is <c>ReplaySubject</c>-backed — a <c>ReplaySubject</c> latches <c>OnError</c>
/// just as it latches a value. So ONE transient DDL failure (a connect blip, a lock timeout, a
/// momentary permission problem) was replayed to every later caller for the life of the process.
/// Since every write to a not-yet-provisioned partition composes on this gate
/// (<c>EnsurePartitionProvisioned(p).SelectMany(_ =&gt; write…)</c>), the partition was then
/// permanently un-writable — <c>42P01 undefined_table</c> forever, with no self-heal short of
/// restarting the pod.</para>
///
/// <para><b>How the fault is induced — no mocking.</b> All per-partition DDL funnels through the
/// <c>public.ensure_partition_schema(text)</c> stored proc. Renaming it away makes the very next
/// provisioning attempt fail for real (<c>42883 undefined_function</c>, which
/// <c>ExecuteDdlWithRetryAsync</c> deliberately does NOT retry — it is not a concurrent-DDL race);
/// renaming it back heals the database. That is a genuine transient failure against a genuine
/// Postgres, which is what the class of bug is about.</para>
///
/// <para><b>Fail-before / pass-after.</b> Against the bare dictionary the second
/// <c>EnsurePartitionProvisioned</c> replays the first attempt's <c>PostgresException</c> and the
/// write 42P01s; with the evicting <see cref="MeshWeaver.Mesh.Threading.PromiseCache{TKey,TValue}"/>
/// the second call provisions for real and the write lands.</para>
/// </summary>
[Collection("PostgreSql")]
public class PartitionProvisioningFaultRecoveryTests(PostgreSqlFixture fixture)
{
    private const string OfflineName = "ensure_partition_schema__offline_for_test";

    private PostgreSqlPartitionStorageProvider NewProvider() =>
        new(fixture.DataSource, fixture.ConnectionString, fixture.Options);

    private IObservable<long> SchemaCount(string schema, CancellationToken ct) =>
        fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s",
            new[] { ("s", (object)schema) }, ct);

    [Fact(Timeout = 120000)]
    public async Task ATransientDdlFailure_DoesNotPoisonThePartitionForever()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = NewProvider();
        var part = $"flaky{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var offline = false;
        try
        {
            // ── Break the one DDL entry point: the transient failure. ────────────────────────
            await fixture.DataSource.ExecuteNonQuery(
                    $"ALTER FUNCTION public.ensure_partition_schema(text) RENAME TO {OfflineName}", ct)
                .Should().Within(30.Seconds()).Emit();
            offline = true;

            // The first caller fails, and SEES the failure — eviction must never swallow a fault.
            var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
                provider.EnsurePartitionProvisioned(part)
                    .Timeout(TimeSpan.FromSeconds(60))
                    .FirstAsync()
                    .ToTask(ct));
            Assert.Contains("ensure_partition_schema", failure.Message, StringComparison.OrdinalIgnoreCase);
            await SchemaCount(part, ct).Should().Within(30.Seconds()).Be(0L);

            // ── The blip passes. ─────────────────────────────────────────────────────────────
            await fixture.DataSource.ExecuteNonQuery(
                    $"ALTER FUNCTION public.{OfflineName}(text) RENAME TO ensure_partition_schema", ct)
                .Should().Within(30.Seconds()).Emit();
            offline = false;

            // 🚨 THE REGRESSION. Before the fix this replayed the same PostgresException — the
            // faulted promise was pinned in the dictionary and nothing re-attempted.
            await provider.EnsurePartitionProvisioned(part).Should().Within(60.Seconds()).Emit();
            await SchemaCount(part, ct).Should().Within(30.Seconds()).Be(1L);

            // …and the consequence that actually hurt: a write to the partition now LANDS instead
            // of 42P01-ing for the life of the process.
            var node = new MeshNode("Foo", part) { Name = "Foo", NodeType = "Markdown" };
            await provider.Adapter.Write(node, JsonSerializerOptions.Default)
                .Should().Within(30.Seconds()).Emit();
            await fixture.DataSource.ScalarLong($"SELECT COUNT(*) FROM \"{part}\".mesh_nodes", ct)
                .Should().Within(30.Seconds()).Be(1L);

            // Still a promise: the healed entry is cached, so a repeat call does no more DDL.
            await provider.EnsurePartitionProvisioned(part).Should().Within(30.Seconds()).Emit();
            await SchemaCount(part, ct).Should().Within(30.Seconds()).Be(1L);
        }
        finally
        {
            if (offline)
                await fixture.DataSource.ExecuteNonQuery(
                        $"ALTER FUNCTION public.{OfflineName}(text) RENAME TO ensure_partition_schema", ct)
                    .Should().Within(30.Seconds()).Emit();
            await fixture.DataSource.ExecuteNonQuery($"DROP SCHEMA IF EXISTS \"{part}\" CASCADE", ct)
                .Should().Within(30.Seconds()).Emit();
        }
    }
}
