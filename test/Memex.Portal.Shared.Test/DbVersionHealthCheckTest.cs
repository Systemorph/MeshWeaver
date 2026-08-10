using System;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Issue #1183: the <c>db_version</c> health probe being CANCELLED mid-query (probe
/// deadline, client disconnect, shutdown) is an EXPECTED outcome, not a database failure.
/// The old catch-all folded the <see cref="OperationCanceledException"/> into
/// <c>HealthCheckResult.Unhealthy("db_version check threw", ex)</c> — which
/// <c>DefaultHealthCheckService</c> logs at Error and ops auto-files as a production
/// incident — for a probe nobody was waiting on any more. The health-check framework
/// already classifies <see cref="OperationCanceledException"/> by token (its
/// <c>RunCheckAsync</c> catch filter converts only a NON-caller-requested cancellation
/// into the standard "A timeout occurred while running check." entry, and lets a
/// caller-requested one propagate to the middleware, which ends the abandoned probe
/// without a fail-level report), so the check must let the cancellation PROPAGATE
/// instead of reporting it as a database fault.
/// </summary>
public class DbVersionHealthCheckTest
{
    // Never actually connected to: the cancelled-token test throws before any I/O
    // (Npgsql checks the token when renting from the pool — the exact frame in the
    // incident's stack), and the genuine-failure test gets connection-refused
    // immediately on loopback (Timeout=1 bounds the worst case).
    private static NpgsqlDataSource CreateUnreachableDataSource() =>
        NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=none;Password=none;Database=none;Timeout=1");

    private static HealthCheckContext Context(IHealthCheck check) => new()
    {
        Registration = new HealthCheckRegistration("db_version", check, null, null)
    };

    [Fact]
    public async Task CancelledProbe_PropagatesTheCancellation_InsteadOfReportingUnhealthy()
    {
        await using var dataSource = CreateUnreachableDataSource();
        var check = new DbVersionHealthCheck(dataSource);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Before the fix this RETURNED Unhealthy("db_version check threw", oce) — the
        // fail-level misclassification that auto-filed #1183. Propagating hands the
        // decision to DefaultHealthCheckService, which knows whose token cancelled.
        var act = () => check.CheckHealthAsync(Context(check), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task UnreachableDatabase_WithoutCancellation_StillReportsUnhealthyWithTheReason()
    {
        await using var dataSource = CreateUnreachableDataSource();
        var check = new DbVersionHealthCheck(dataSource);

        var result = await check.CheckHealthAsync(Context(check), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "a genuine connection failure is a real Unhealthy verdict — only cancellations propagate");
        result.Exception.Should().NotBeNull(
            "the entry must carry the underlying reason for operators");
    }
}
