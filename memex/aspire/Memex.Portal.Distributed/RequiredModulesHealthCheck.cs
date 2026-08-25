using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.Distributed;

/// <summary>
/// Reports what this deployment can and cannot serve of the modules it declares under
/// <c>Modules:Required</c> — and, crucially, tells the two apart (#2089).
///
/// <para><b>The hole this closes.</b> A listed-but-absent module is SKIPPED at boot, deliberately —
/// a host that will not start is worse than one missing a feature, and that rule is what stopped the
/// 3.0.0-rc5 boot loop. That silence was safe only while every module shipped in the image. Once
/// modules START LEAVING for the registry it becomes a trap: ship a build whose image no longer
/// carries a pack, land it where the package was never installed, and charts go blank, maps go
/// blank, voice goes mute — behind one stderr line and a green rollout.</para>
///
/// <para>🚨 <b>Unhealthy for a LOST PACK — and that difference IS the gate.</b> When the image's own
/// <c>Modules:Assemblies</c> names a required module the image does not actually carry, readiness
/// fails: Kubernetes holds the old ReplicaSet in service, so the rollout stalls instead of
/// completing into a portal that cannot do what it did yesterday. Degraded would be visible and
/// useless there — the bad build would take over while a dashboard turned amber. This is the one
/// case where refusing traffic is the kinder answer, because the pods that still have the module
/// are right there, already serving.</para>
///
/// <para>🚨 <b>Degraded for a STORE-DELIVERED one — and that is not leniency.</b> A module the
/// image never claimed to ship cannot be produced by holding the rollout: the registry that must
/// serve it is itself a portal downstream of this very deploy. Reporting it Unhealthy wedged both
/// prod rollouts on 2026-08-23 with no remedy but blanking <c>Modules__Required__0..4</c> on the
/// live deployment. So it is Degraded — which is NOT Healthy: the module is named in the payload
/// and in the message together with which of the four states it is in (never installed / landed and
/// awaiting a restart / landing incomplete / held above the platform floor) and what to do about
/// it. An operator can always separate "required, and nothing here can produce it" from "expected,
/// and here is exactly what it waits on".</para>
///
/// <para>It reports only what the deployment ITSELF declared required, so it is inert by default:
/// no <c>Modules:Required</c> means nothing to report.</para>
/// </summary>
public sealed class RequiredModulesHealthCheck(IConfiguration configuration) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var moduleRoot = ModuleRoot.Resolve(configuration);
        var activation = ModuleActivationSidecar.Read(moduleRoot);

        // The SAME resolution and the SAME gates the boot path used, asked the same way — a probe
        // that answered differently from the log line that preceded it would be worse than no probe.
        var verdicts = RequiredModuleStatus.Classify(
            Values(MeshBuilderModuleActivation.RequiredKey),
            Values(MeshBuilderModuleActivation.AssembliesKey),
            ModuleActivationStatus.LoadedAssemblyNames(),
            entry => File.Exists(MeshBuilder.ResolveModulePath(entry)),
            activation,
            entry => ModuleActivationBoot.LandedModuleDllExists(moduleRoot, entry),
            ModulePlatformFloor.DeclineReason);

        var absent = RequiredModuleStatus.Absent(verdicts);
        var expected = RequiredModuleStatus.ExpectedLater(verdicts);

        // 🚨 Both lists ship in the payload whatever the verdict, so an operator reading /health
        // never has to infer which bucket a module fell into from the status alone.
        var data = new Dictionary<string, object>
        {
            ["missing"] = absent.Select(v => v.Entry).ToArray(),
            ["expected"] = expected.Select(v => v.Entry).ToArray(),
            ["detail"] = verdicts.Select(v => $"{v.Name} [{v.State}]: {v.Reason}").ToArray(),
        };

        if (absent.Count > 0)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"{absent.Count} required module(s) the image is supposed to ship are absent: "
                + RequiredModuleStatus.Describe(absent)
                + (expected.Count > 0
                    ? $". Also awaiting the store lane: {RequiredModuleStatus.Describe(expected)}"
                    : string.Empty),
                data: data));

        if (expected.Count > 0)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{expected.Count} required module(s) are store-delivered and not here yet — the "
                + "features they provide are missing until the module lane catches up: "
                + RequiredModuleStatus.Describe(expected),
                data: data));

        return Task.FromResult(HealthCheckResult.Healthy("every required module is present", data));

        IEnumerable<string?> Values(string key) =>
            configuration.GetSection(key).GetChildren().Select(child => child.Value);
    }
}
