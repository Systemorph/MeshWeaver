using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.Distributed;

/// <summary>
/// Fails readiness when a module this deployment declares under <c>Modules:Required</c> is not
/// present in the image.
///
/// <para><b>The hole this closes.</b> A listed-but-absent module is SKIPPED at boot, deliberately —
/// a host that will not start is worse than one missing a feature, and that rule is what stopped the
/// 3.0.0-rc5 boot loop. That silence was safe only while every module shipped in the image. Once
/// modules START LEAVING for the registry it becomes a trap: ship a build whose image no longer
/// carries a pack, land it where the package was never installed, and charts go blank, maps go
/// blank, voice goes mute — behind one stderr line and a green rollout.</para>
///
/// <para>🚨 <b>Unhealthy, not Degraded — and that difference IS the gate.</b> Readiness failing on
/// the new pods means Kubernetes holds the old ReplicaSet in service: the rollout stalls instead of
/// completing into a portal that cannot do what it did yesterday. Degraded would be visible and
/// useless — the bad build would take over while a dashboard turned amber. This is the one case
/// where refusing traffic is the kinder answer, because the pods that still have the module are
/// right there, already serving.</para>
///
/// <para>It reports only what the deployment ITSELF declared required, so it is inert by default:
/// no <c>Modules:Required</c> means nothing to fail on, and today's behaviour is unchanged.</para>
/// </summary>
public sealed class RequiredModulesHealthCheck(IConfiguration configuration) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The SAME pure rule the boot path used, asked the same way — a probe that answered
        // differently from the log line that preceded it would be worse than no probe.
        var missing = MeshBuilderModuleActivation.MissingRequired(
            configuration, MeshBuilder.ResolveModulePath, File.Exists);

        if (missing.Length == 0)
            return Task.FromResult(HealthCheckResult.Healthy("every required module is present"));

        var data = new Dictionary<string, object> { ["missing"] = missing };
        return Task.FromResult(HealthCheckResult.Unhealthy(
            $"{missing.Length} required module(s) absent: {string.Join(", ", missing)}. "
            + "This image does not ship them and no install landed them — the features they provide "
            + "are gone. Install the packages, or delist them from Modules:Required.",
            data: data));
    }
}
