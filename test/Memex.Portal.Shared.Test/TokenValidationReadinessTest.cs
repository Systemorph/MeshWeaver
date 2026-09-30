using System;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚦 Readiness = this replica can authenticate an API token (maintainer, 2026-09-30: "we must show
/// as ready only when this stuff can be served"). The thresholds, pure: not ready until measured,
/// still ready through fewer than <see cref="TokenValidationReadiness.FailuresToUnready"/>
/// consecutive failures (one slow read must never take a replica — let alone the fleet — out of
/// rotation), not ready from there on, ready again on the first good sample.
/// </summary>
public class TokenValidationReadinessTest
{
    private static TokenValidationReadiness.Sample Ok() => new(DateTimeOffset.UtcNow, Ok: true, NoStore: false, "ok", 12);
    private static TokenValidationReadiness.Sample Failed() => new(DateTimeOffset.UtcNow, Ok: false, NoStore: false, "TimeoutException", 10_000);

    /// <summary>An unmeasured path is not a working one.</summary>
    [Fact]
    public void NotMeasuredYet_IsNotReady()
        => Assert.Equal(HealthStatus.Unhealthy, TokenValidationReadiness.Classify(null, 0).Status);

    /// <summary>A good sample is ready.</summary>
    [Fact]
    public void GoodSample_IsReady()
        => Assert.Equal(HealthStatus.Healthy, TokenValidationReadiness.Classify(Ok(), 0).Status);

    /// <summary>
    /// Below the threshold the replica stays in rotation — Degraded, which /ready answers 200 —
    /// so a transient never flaps a replica out.
    /// </summary>
    [Fact]
    public void FewerFailuresThanTheThreshold_StayReady()
    {
        for (var failures = 1; failures < TokenValidationReadiness.FailuresToUnready; failures++)
            Assert.Equal(HealthStatus.Degraded, TokenValidationReadiness.Classify(Failed(), failures).Status);
    }

    /// <summary>A sustained failure takes the replica out of rotation.</summary>
    [Fact]
    public void SustainedFailure_IsNotReady()
        => Assert.Equal(HealthStatus.Unhealthy,
            TokenValidationReadiness.Classify(Failed(), TokenValidationReadiness.FailuresToUnready).Status);

    /// <summary>A host with no mesh store has nothing to measure and is not held back by it.</summary>
    [Fact]
    public void NoStore_IsReady()
        => Assert.Equal(HealthStatus.Healthy,
            TokenValidationReadiness.Classify(new(DateTimeOffset.UtcNow, Ok: true, NoStore: true, "no store", 0), 0).Status);
}
