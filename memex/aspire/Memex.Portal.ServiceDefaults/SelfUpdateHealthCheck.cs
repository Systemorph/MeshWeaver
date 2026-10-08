using MeshWeaver.Hosting.SelfUpdate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// The <c>/health</c> entry over <see cref="SelfUpdateCheckCensus"/>: the verdict of the last
/// self-update check this process reported, Degraded when that verdict is a FAILURE.
///
/// <para>🚨 <b>Policy <c>control-first-never-silent</c>.</b> The fleet is offered a platform build
/// only after the control instance runs it, so a control whose self-update FAILS freezes every
/// instance — and until this entry the failure lived only on control's own
/// <c>Admin/UpdatePolicy</c>, behind authentication. <c>/health</c> is public, so the CD arming
/// (<c>.github/scripts/arm-promoted-set.py</c>) and the <c>control-always-latest</c> alarm quote this
/// line when control falls behind, and an operator reading any instance's <c>/health</c> sees it.</para>
///
/// <para>Census-tagged: it prints whatever its status, because "no check reported", "checked and
/// nothing newer" and "every check fails" must be three different printed sentences — a Healthy
/// entry that printed nothing would be indistinguishable from one never registered. Degraded,
/// never Unhealthy, and no probe tag: a failing self-update costs DELIVERY, and pulling the replica
/// out of the Service (or failing its startup probe) delivers nothing while taking away the pod that
/// still serves. The census is resolved per call, as <see cref="SealedSyncHealthCheck"/> does, and
/// a host without one (no self-updater registered) says so rather than reading clean.</para>
/// </summary>
public sealed class SelfUpdateHealthCheck(IServiceProvider services) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate(services.GetService<SelfUpdateCheckCensus>(), DateTimeOffset.UtcNow));

    /// <summary>The entry's verdict for a census (or none) at <paramref name="now"/>. Pure.</summary>
    /// <param name="census">The census, or null when this host registers no self-updater.</param>
    /// <param name="now">The clock the reading's age is read against.</param>
    /// <returns>Degraded on a failing last check; Healthy with the reading otherwise.</returns>
    public static HealthCheckResult Evaluate(SelfUpdateCheckCensus? census, DateTimeOffset now)
    {
        if (census is null)
            return HealthCheckResult.Healthy(
                "no self-updater is registered in this host — nothing here checks for platform builds");
        var reading = census.Last;
        var description = SelfUpdateCheckCensus.Describe(reading, now);
        return reading is { Failed: true }
            ? HealthCheckResult.Degraded(description)
            : HealthCheckResult.Healthy(description);
    }
}
