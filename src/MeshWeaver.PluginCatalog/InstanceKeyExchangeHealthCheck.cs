using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚦 <b>The key exchange's own line on <c>/health</c></b> (MeshWeaver#7721): what this replica's
/// instance-key resolutions answered, and whether the newest one reached no verdict.
///
/// <para><b>The incident.</b> In #7484 the exchange (<c>POST /api/instances/token</c>) gave
/// registered keys no answer the registry hook could use for about two hours, so every estate
/// mirror and release failed. <c>/health</c> on the validating instance read Healthy on every line
/// except an overdue fleet watch, a different subsystem. Nothing on it described the exchange.</para>
///
/// <para><b>What it reads.</b> <see cref="InstanceRegistryAuthenticator.Outcomes"/>: resolved,
/// refused and unavailable counts since counting started, and the time of the newest of each. The
/// verdict is <b>Degraded</b> while the NEWEST resolution was unavailable and is younger than
/// <see cref="CurrentFor"/>: callers are being told to retry right now, and nothing has resolved
/// since. A later resolved or refused answer clears it, because either one is a verdict the read
/// path reached. An old unavailable answer with no traffic after it is reported, not degraded:
/// nobody is waiting on it.</para>
///
/// <para><b>Never Unhealthy, and not for readiness.</b> An unavailable resolution is a slow or
/// faulted mesh read, which the sibling replicas share. Taking this replica out of the Service
/// fixes nothing. The host registers this on the census tag, so its reading prints whatever the
/// status: "no key presented yet" and "every key resolved" are different sentences.</para>
/// </summary>
/// <param name="services">The host's services; the check reads the registered
/// <see cref="InstanceRegistryAuthenticator"/>, if any.</param>
public sealed class InstanceKeyExchangeHealthCheck(IServiceProvider services) : IHealthCheck
{
    /// <summary>The health check's registration name.</summary>
    public const string HealthCheckName = "instance_key_exchange";

    /// <summary>How long an unavailable newest resolution degrades the check.</summary>
    public static readonly TimeSpan CurrentFor = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var authenticator = services.GetService<InstanceRegistryAuthenticator>();
        return Task.FromResult(authenticator is null
            ? HealthCheckResult.Healthy("this host serves no instance-key exchange (no InstanceRegistryAuthenticator is registered)")
            : Evaluate(authenticator.Outcomes.Snapshot, DateTimeOffset.UtcNow));
    }

    /// <summary>The verdict over one reading.</summary>
    /// <param name="reading">The tally's snapshot.</param>
    /// <param name="now">The time to judge "current" against.</param>
    public static HealthCheckResult Evaluate(InstanceKeyResolutionSnapshot reading, DateTimeOffset now)
    {
        if (reading.Total == 0)
            return HealthCheckResult.Healthy(
                $"no instance key has been presented on this replica since {reading.Since:O} — nothing measured yet");

        var counts = $"{reading.Total} instance-key resolution(s) since {reading.Since:O}: "
            + $"{reading.Resolved} resolved, {reading.Refused} refused, {reading.Unavailable} unavailable"
            + (reading.LastResolvedAt is { } resolvedAt ? $"; last resolved {resolvedAt:O}" : "; none resolved");

        if (reading.Unavailable == 0)
            return HealthCheckResult.Healthy(counts);

        var lastUnavailable = $"last unavailable {reading.LastUnavailableAt:O} ({reading.LastUnavailableReason})";
        if (reading.LatestUnavailable && reading.LatestAt is { } latestAt && now - latestAt < CurrentFor)
            return HealthCheckResult.Degraded(
                "the NEWEST instance-key resolution reached no verdict, so callers of the key exchange are being "
                + "told to retry (503) and nothing has resolved since — " + counts + "; " + lastUnavailable);

        return HealthCheckResult.Healthy(counts + "; " + lastUnavailable);
    }
}
