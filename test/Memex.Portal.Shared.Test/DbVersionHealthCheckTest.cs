using System;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// What <see cref="DbVersionHealthCheck"/> is allowed to call a database failure — issue #1183.
///
/// <para>Production, 2026-08-10 19:28:57Z:</para>
/// <code>
/// fail: Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService[103]
///       Health check db_version with status Unhealthy completed after 3462.0492ms with message 'db_version check threw'
///       System.OperationCanceledException: The operation was canceled.
///          at Npgsql.PoolingDataSource.RentAsync(...)
/// </code>
/// <para>The pod was NOT shutting down — it logged "Application started." at 19:28:38.552Z and
/// served for three more hours. It was 19 s into a fresh rollout, mid NodeType-bake, and the
/// startup probe's <c>/health</c> request was cancelled while the check was still waiting to RENT a
/// connector — before a connection was even open. Nothing was learned about Postgres.</para>
///
/// <para>The check nevertheless converted its caller's cancellation into
/// <c>HealthCheckResult.Unhealthy(exception)</c>. <c>DefaultHealthCheckService</c> logs an Unhealthy
/// entry at ERROR with the attached stack, and the red-log filer turns Error into an incident — so
/// an aborted probe opened a production ticket. The framework's own catch filter is
/// <c>catch (Exception ex) when (ex as OperationCanceledException == null)</c>, commented
/// "Allow cancellation to propagate if it's not a timeout"; catching first is what denied it that
/// classification.</para>
///
/// <para>Both halves are pinned: a caller's cancellation propagates, and a database that genuinely
/// cannot be reached is still Unhealthy.</para>
/// </summary>
public class DbVersionHealthCheckTest
{
    /// <summary>
    /// A data source that can never answer — nothing listens on this loopback port. Which failure
    /// the connect produces is irrelevant to both tests below; what matters is that the check has
    /// to go to the database, so the cancellation is observed on the same code path production
    /// took (<c>NpgsqlConnection.Open</c> → <c>PoolingDataSource</c>).
    /// </summary>
    private static NpgsqlDataSource UnreachableDataSource()
        => NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=59321;Username=nobody;Password=nothing;Database=nowhere;Timeout=2");

    /// <summary>
    /// 🚨 The regression pin. A cancellation raised by the CALLER is not a verdict about the
    /// database, so it must leave the check as a cancellation — not as an Unhealthy report carrying
    /// an exception for <c>DefaultHealthCheckService</c> to log at Error.
    /// </summary>
    [Fact]
    public async Task ACallerCancellation_Propagates_InsteadOfBeingReportedAsAnUnhealthyDatabase()
    {
        await using var dataSource = UnreachableDataSource();
        var check = new DbVersionHealthCheck(dataSource);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }

    /// <summary>
    /// The guard that keeps the fix honest: propagating cancellations must not stop the check
    /// reporting a database it genuinely could not reach. This is the whole reason the check
    /// exists (a half-migrated or unreachable DB behind a portal that started anyway).
    /// </summary>
    [Fact]
    public async Task AnUnreachableDatabase_IsStillReportedUnhealthy()
    {
        await using var dataSource = UnreachableDataSource();
        var check = new DbVersionHealthCheck(dataSource);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().NotBeNull(
            "the cause has to reach the log — only CANCELLATION is excluded, never a real fault");
    }
}
