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

    /// <summary>
    /// 🚨 The multi-replica hole in <c>PendingRestart</c>. It is ONE deployment-wide boolean that
    /// the next boot clears — and on a fleet the pod that clears it is not the pod that is missing
    /// the module. Replica A lands one; replica B restarts for its own reasons, applies the list
    /// and resets the flag; A keeps serving WITHOUT the module while every surface reads "nothing
    /// pending". The check must answer for THIS process, so a cleared flag with an unloaded module
    /// is still Degraded.
    /// </summary>
    [Fact]
    public async Task AClearedFlagWithAnUnloadedModule_IsStillDegraded()
    {
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            PendingRestart = false,   // another replica's boot consumed it
            Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Acme", Enabled = true }],
        });

        var result = await Check();

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("MeshWeaver.Acme", result.Description);
    }

    [Fact]
    public async Task AModuleThisProcessHasLOADED_IsNotPending()
    {
        // The entry names an assembly this test process genuinely has loaded, so it is running
        // here and is not waiting on anything — whatever the deployment-wide flag says.
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            PendingRestart = true,
            Entries =
            [
                new ModuleActivationEntry
                {
                    Name = typeof(ModuleActivationList).Assembly.GetName().Name!,
                    Enabled = true,
                },
            ],
        });

        Assert.Equal(HealthStatus.Healthy, (await Check()).Status);
    }

    [Fact]
    public async Task ADisabledEntry_IsNeverPending()
    {
        // A disabled entry is the record of an UNINSTALL. Its module being absent from this
        // process is the outcome, not a to-do.
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            PendingRestart = true,
            Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Removed", Enabled = false }],
        });

        Assert.Equal(HealthStatus.Healthy, (await Check()).Status);
    }

    /// <summary>
    /// 🚨 FAIL CLOSED. A sidecar that cannot be parsed is the ABSENCE of an answer.
    /// <see cref="ModuleActivationSidecar.Read"/> swallows corruption into the empty list, so a
    /// surface that ignores its corruption callback reports "no pending activation" — cheerfully,
    /// forever. A check that cannot reach its evidence must say so.
    /// </summary>
    [Fact]
    public async Task ACorruptSidecar_IsDegraded_NeverHealthy()
    {
        File.WriteAllText(
            ModuleActivationSidecar.SidecarPath(root), "{ this is not activation json");

        var result = await Check();

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("could not be determined", result.Description);
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
