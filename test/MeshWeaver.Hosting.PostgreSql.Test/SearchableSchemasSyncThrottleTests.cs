using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.PostgreSql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Reproduces the prod incident on 2026-05-20: opening a thread fans out
/// to N cross-schema queries on PostgreSqlPartitionedMeshQuery, each of
/// which previously called <see cref="PostgreSqlCrossSchemaQueryProvider.SyncSearchableSchemasAsync"/>
/// unconditionally. Per-query SELECT + DELETE + N INSERTs on
/// public.searchable_schemas combined with MaxPoolSize=1 on the public
/// connection pool melted the pool and made /authorize hang.
///
/// The throttle (single-flight + TTL) MUST collapse N concurrent calls
/// into ONE actual DB sync. Pure unit test — no DB needed since we
/// only assert on the in-process counter the throttle increments.
/// </summary>
[Collection("PostgreSql")]
public class SearchableSchemasSyncThrottleTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task SyncSearchableSchemasAsync_HundredConcurrentCalls_RunsActualSyncOnce()
    {
        // Repro: 100 fan-outs hit the provider in the same render tick.
        // Without the throttle every one of them did a full DELETE+INSERT.
        // With the throttle we expect exactly 1 actual sync.
        var provider = new PostgreSqlCrossSchemaQueryProvider(fixture.DataSource);
        provider.SyncTtl = System.TimeSpan.FromSeconds(30);

        var tasks = new Task[100];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = provider.SyncSearchableSchemasAsync(CancellationToken.None);
        await Task.WhenAll(tasks);

        provider.ActualSyncCount.Should().Be(1,
            "all 100 concurrent calls within the TTL must collapse to one DB sync — "
            + "this is the throttle that fixes the prod thread-load deadlock");
    }

    [Fact]
    public async Task SyncSearchableSchemasAsync_AfterTtlElapses_RunsAgain()
    {
        // Ensure the throttle doesn't permanently lock us out — a NEW partition
        // created mid-session must become visible after SyncTtl elapses.
        var provider = new PostgreSqlCrossSchemaQueryProvider(fixture.DataSource);
        // Tiny TTL so the test doesn't wait long; the prod default is 30 s.
        provider.SyncTtl = System.TimeSpan.FromMilliseconds(50);

        await provider.SyncSearchableSchemasAsync(CancellationToken.None);
        provider.ActualSyncCount.Should().Be(1);

        await Task.Delay(120, TestContext.Current.CancellationToken);

        await provider.SyncSearchableSchemasAsync(CancellationToken.None);
        provider.ActualSyncCount.Should().Be(2,
            "after the TTL elapses, the next call must actually sync so a newly-created "
            + "partition becomes visible to the cross-schema fan-out");
    }

    [Fact]
    public async Task SyncSearchableSchemasAsync_BurstAfterTtl_StillOneSyncPerWindow()
    {
        // Walk through several windows verifying that each window costs exactly
        // one sync regardless of burst size. This is the load-shape the prod
        // thread-render generates: N parallel fan-outs per render, repeated as
        // the user scrolls / new messages stream in.
        var provider = new PostgreSqlCrossSchemaQueryProvider(fixture.DataSource);
        provider.SyncTtl = System.TimeSpan.FromMilliseconds(50);

        for (int window = 0; window < 3; window++)
        {
            var burst = new Task[20];
            for (int i = 0; i < burst.Length; i++)
                burst[i] = provider.SyncSearchableSchemasAsync(CancellationToken.None);
            await Task.WhenAll(burst);
            await Task.Delay(70, TestContext.Current.CancellationToken);
        }

        provider.ActualSyncCount.Should().BeInRange(3, 4,
            "three windows of 20 concurrent calls each = 3 (or 4 if a sync straddled "
            + "the boundary) actual syncs, never 60");
    }
}
