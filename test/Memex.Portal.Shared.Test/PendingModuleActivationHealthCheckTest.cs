#pragma warning disable CS1591

using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.PluginCatalog;
using Memex.Portal.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The restart-as-activation signal, surfaced (#1979). PendingRestart was written by the landing
/// sites, consumed at boot, and read by NOTHING — so a package could install, report success, and
/// leave its module inert until someone happened to restart the pod.
///
/// <para>Reads the PERSISTED sidecar on purpose: the process that landed the module is not
/// necessarily the process being asked, which is the whole reason the state is durable.</para>
/// </summary>
public class PendingModuleActivationHealthCheckTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-pending-" + Guid.NewGuid().ToString("N"));

    public PendingModuleActivationHealthCheckTest() =>
        Directory.CreateDirectory(Path.Combine(root, "modules"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private Task<HealthCheckResult> Check() =>
        new PendingModuleActivationHealthCheck(root)
            .CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task APendingActivation_IsReportedDegraded_AndNamesTheModules()
    {
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            PendingRestart = true,
            Entries =
            [
                new ModuleActivationEntry { Name = "MeshWeaver.Acme", Enabled = true },
                new ModuleActivationEntry { Name = "MeshWeaver.Widgets", Enabled = true },
            ],
        });

        var result = await Check();

        // DEGRADED, never Unhealthy: the pod serves correctly with what it loaded, and failing
        // readiness would stall a rollout over work the rollout itself performs.
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("MeshWeaver.Acme", result.Description);
        Assert.Contains("MeshWeaver.Widgets", result.Description);
    }

    [Fact]
    public async Task NoPendingActivation_IsHealthy()
    {
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            PendingRestart = false,
            Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Acme", Enabled = true }],
        });

        Assert.Equal(HealthStatus.Healthy, (await Check()).Status);
    }

    [Fact]
    public async Task AnUnreadableOrAbsentSidecar_IsHealthy_NeverAnInventedPendingRestart()
    {
        // Absence of a "yes" is a "no": a missing sidecar must not read as a pending activation.
        var result = await new PendingModuleActivationHealthCheck(
                Path.Combine(root, "no-such-deployment"))
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ALandingSetsIt_AndTheSurfaceSeesIt_WithoutAnInMemoryHandoff()
    {
        // End to end over the durable state: land through the service, then ask the health check,
        // which shares nothing with it but the sidecar on disk.
        using var landing = new ModuleLandingService(baseDirectory: root);
        await landing.LandModule(
                "MeshWeaver.Acme",
                [("MeshWeaver.Acme.dll", [1])],
                MeshWeaver.Graph.Configuration.PrebuiltAssemblySeeder.LiveFrameworkMvid,
                version: "1.0.0")
            .FirstAsync().ToTask();

        var result = await Check();

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("MeshWeaver.Acme", result.Description);
    }
}
