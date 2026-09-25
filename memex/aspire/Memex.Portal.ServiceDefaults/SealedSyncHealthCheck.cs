using MeshWeaver.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// The <c>/health</c> check over <see cref="SealedSyncCensus"/>: whether this replica's framework
/// identity still has a publication of each module-bearing repository at or after that
/// repository's last green build — and, when it does not, which repositories are frozen and for
/// how long (MeshWeaver#4063).
///
/// <para><b>What it answers.</b> The identity this pod resolved, every publication sealed under it
/// (source, producing repository, baked commit, sealed or torn), and — since policy
/// <c>module-sync-per-manifest-hash</c>, under which the seal holds no source — every GitSynced
/// MODULE's last outcome: unchanged, synced, or declined with its reason (a declared platform floor
/// above the running platform). A decline that outlives the CI job cap reads Degraded.</para>
///
/// <para>🚨 <b>A freeze looks exactly like a quiet week, which is why the CLEAN reading must print
/// too.</b> Both are "no import happened". On 2026-09-12 both production portals read
/// <c>lastSyncOutcome: Imported</c>, attempted == synced, <c>lastAttemptWasFinal: true</c> — the
/// signature of a settled source — while nine hours of tagged releases had never arrived, and two
/// sessions read "settled" off that node before an activity list gave the hold away. So this entry
/// carries <see cref="ProbeEndpoints.CensusTag"/>: "nothing was measured here", "this deployment
/// consumes no CI bakes", "green builds arrived and none was held" and "a repository has been held
/// for nine hours" are four different printed sentences instead of the same silence.</para>
///
/// <para>🚨 <b>Degraded only past <see cref="SealedSyncCensus.HoldIndictsAfter"/>, never on a hold
/// as such.</b> The ordinary hold is correct behaviour — the green-build hook fires before the
/// repository's publish-bake job seals for this identity, so a source waits out one publish. A
/// check that degraded on that would be non-Healthy on every portal every time any satellite
/// merged. The threshold is the fleet's 45-minute CI job cap, past which the ordering explanation
/// no longer exists.</para>
///
/// <para>Degraded, never Unhealthy, and no probe tag: a frozen GitSync costs CONTENT, and pulling
/// the replica from the Service delivers none of it while taking away the pod that could still
/// serve what it has. The registry is resolved per call because the mesh's services are composed
/// after the host's health checks are registered — the same reason
/// <see cref="ContentTypeHealthCheck"/> does.</para>
/// </summary>
public sealed class SealedSyncHealthCheck(IServiceProvider services) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        // 🚨 A null REGISTRY is not a null reading — it is a host that never registered the
        // instrument, and Describe(null, …) already says "measured NOTHING" in those words.
        var census = services.GetService<SealedSyncCensus>();
        var holds = census?.Holds() ?? [];
        // Policy module-sync-per-manifest-hash: per MODULE, not per Space — which module is
        // unchanged, which synced, which is declined and why.
        var modules = census?.ModuleOutcomes() ?? [];
        var description = SealedSyncCensus.DescribeWithModules(census?.Published, holds, modules, now);

        var data = holds.ToDictionary(
            h => h.Repository,
            h => (object)h.Line(now),
            StringComparer.Ordinal);
        foreach (var module in modules)
            data[$"module:{module.Space}#{module.Module}"] = module.Line(now);

        return Task.FromResult(SealedSyncCensus.IsIndicted(holds, now)
                               || SealedSyncCensus.IsModuleDeclineIndicted(modules, now)
            ? HealthCheckResult.Degraded(description, data: data)
            : HealthCheckResult.Healthy(description, data: data));
    }
}
