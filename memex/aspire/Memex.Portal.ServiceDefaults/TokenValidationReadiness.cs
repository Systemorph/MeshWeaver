using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// 🚦 <b>Readiness = this replica can authenticate an API token</b> (maintainer, 2026-09-30:
/// <i>"we must show as ready only when this stuff can be served"</i>). Every MCP and API call
/// starts with a token verdict; a replica that cannot reach one answers 503 to all of them, and an
/// MCP client reads a run of 503s as "sign in again" (Doc/Architecture/TokenValidationHotPath).
///
/// <para><b>What is measured.</b> Every <see cref="Period"/> a background canary runs the SAME
/// verdict the auth middleware runs (<see cref="ApiTokenVerdict.Decide"/>) over the SAME
/// authoritative-store read, for a token that cannot exist. A definitive "not found" within
/// <see cref="ApiTokenVerdict.ReadBound"/> proves the path end to end; UNAVAILABLE (a faulted or
/// unanswered read) is a failed sample.</para>
///
/// <para><b>Why a sampler and not a probe-time read.</b> <c>/ready</c> must stay light — a heavy
/// readiness check that times out under load is the 2026-07-21 death spiral (a pod yanked from
/// the Service, its traffic landing on siblings with the same problem). The probe reads the last
/// verdict; it never does I/O.</para>
///
/// <para><b>Why it takes <see cref="FailuresToUnready"/> failures.</b> One slow read must not take
/// a replica — let alone the fleet — out of rotation. Three consecutive failed samples (~45 s) do:
/// that is a replica whose token path is down, which answers 503 to every API call whether or not
/// it is in the Service. If the STORE is down for every replica, every replica goes unready —
/// correct, because none of them can serve an authenticated call, and the browser sessions share
/// the same store. Readiness returns on the first good sample.</para>
///
/// <para>Until the first sample completes the replica is NOT ready: an unmeasured path is not a
/// working one. A host without a mesh store (a test host, a setup-only boot) has nothing to
/// measure and reports Healthy with that said.</para>
/// </summary>
public sealed class TokenValidationReadiness(IServiceProvider services, ILogger<TokenValidationReadiness> logger)
    : IHealthCheck, IHostedService, IDisposable
{
    /// <summary>The health check's registration name.</summary>
    public const string HealthCheckName = "token_validation";

    /// <summary>How often the canary samples.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(15);

    /// <summary>Consecutive failed samples before the replica leaves rotation.</summary>
    public const int FailuresToUnready = 3;

    // A token that cannot exist: the prefix makes it well-formed, the random tail makes its hash
    // unmatchable, so the only honest answers are "not found" (path works) or UNAVAILABLE.
    private readonly string canaryToken = ValidateTokenRequest.TokenPrefix + "readiness-canary-" + Guid.NewGuid().ToString("N");
    private readonly object gate = new();
    private IDisposable? sampling;
    private Sample? last;
    private int consecutiveFailures;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The FIRST sample is subscribed inside this call, so a host with nothing to measure (no
        // mesh store) records that synchronously and is never reported "not yet measured" to a
        // probe that arrives the instant the host has started. A real store read stays async.
        // Measure() catches every fault into a failed Sample, so a terminal error here is a defect in
        // the sampler itself — logged, then RESUBSCRIBED (a sampler whose whole job is continuous
        // sampling must never freeze readiness at its last reading).
        sampling = Observable.Defer(Measure)
            .Concat(Observable.Interval(Period).Select(_ => Measure()).Concat())
            .Do(_ => { }, ex => logger.LogError(ex, "Token-validation readiness sampler FAULTED — resubscribing"))
            .Retry()
            .Subscribe(Record);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref sampling, null)?.Dispose();

    private IObservable<Sample> Measure()
    {
        var hub = services.GetService<IMessageHub>();
        var storage = hub?.ServiceProvider.GetService<IStorageAdapter>();
        if (hub is null || storage is null)
            return Observable.Return(new Sample(DateTimeOffset.UtcNow, Ok: true, NoStore: true, "no mesh store in this host — nothing to measure", 0));

        var started = DateTimeOffset.UtcNow;
        return ApiTokenVerdict.Decide(canaryToken, path => storage.Read(path, hub.JsonSerializerOptions), hub.JsonSerializerOptions)
            .Take(1)
            .Select(verdict =>
            {
                var ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                return verdict.IsUnavailable
                    ? new Sample(DateTimeOffset.UtcNow, Ok: false, NoStore: false, verdict.Error ?? "unavailable", ms)
                    : new Sample(DateTimeOffset.UtcNow, Ok: true, NoStore: false, $"token store answered a definitive verdict in {ms} ms", ms);
            })
            .Catch((Exception ex) => Observable.Return(
                new Sample(DateTimeOffset.UtcNow, Ok: false, NoStore: false, $"{ex.GetType().Name}: {ex.Message}", 0)));
    }

    private void Record(Sample sample)
    {
        int failures;
        lock (gate)
        {
            last = sample;
            consecutiveFailures = sample.Ok ? 0 : consecutiveFailures + 1;
            failures = consecutiveFailures;
        }
        if (!sample.Ok)
            logger.Log(failures >= FailuresToUnready ? LogLevel.Error : LogLevel.Warning,
                "Token-validation readiness sample FAILED ({Failures}/{Threshold}): {Detail}{Consequence}",
                failures, FailuresToUnready, sample.Detail,
                failures >= FailuresToUnready ? " — this replica reports NOT READY until a sample succeeds" : "");
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Sample? sample;
        int failures;
        lock (gate)
        {
            sample = last;
            failures = consecutiveFailures;
        }

        var result = Classify(sample, failures);
        return Task.FromResult(result);
    }

    /// <summary>
    /// The readiness verdict from the last sample and the run of failures — pure, so the
    /// thresholds are tested without a clock or a store.
    /// </summary>
    /// <param name="sample">The last sample, or null before the first one completed.</param>
    /// <param name="failures">Consecutive failed samples up to and including the last.</param>
    /// <returns>Unhealthy before the first sample and from <see cref="FailuresToUnready"/> failures on;
    /// Degraded (still ready) below that; Healthy otherwise.</returns>
    internal static HealthCheckResult Classify(Sample? sample, int failures) => sample switch
    {
        null => HealthCheckResult.Unhealthy("token validation not yet measured — not ready until the first canary answers"),
        { NoStore: true } => HealthCheckResult.Healthy(sample.Detail),
        _ when failures >= FailuresToUnready => HealthCheckResult.Unhealthy(
            $"token validation UNAVAILABLE for {failures} consecutive samples (last {sample.At:O}): {sample.Detail}"),
        _ when failures > 0 => HealthCheckResult.Degraded(
            $"token validation failed {failures}/{FailuresToUnready} recent samples (last {sample.At:O}): {sample.Detail}"),
        _ => HealthCheckResult.Healthy(sample.Detail),
    };

    /// <summary>One canary measurement.</summary>
    internal sealed record Sample(DateTimeOffset At, bool Ok, bool NoStore, string Detail, int Milliseconds);
}
