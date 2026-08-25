#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Memex.Portal.Distributed;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The <c>required_modules</c> probe, over the real configuration and the real activation record
/// (#2089) — and specifically the fact that it now gives DIFFERENT answers to two situations it
/// used to conflate.
///
/// <para><b>Why this probe matters more than most.</b> The chart points the STARTUP probe at
/// <c>/health</c>, and ASP.NET maps Unhealthy to 503. So Unhealthy here does not merely colour a
/// dashboard: it fails the startup probe, Kubernetes never marks the new pods ready, and the
/// rollout stalls with the old ReplicaSet still serving. That is exactly right for a build that
/// dropped a pack it claims to ship. It was exactly wrong for a store-delivered module — the
/// registry that must serve it is a portal downstream of the same rollout, so the stall could
/// never end, and the only remedy anyone found was blanking <c>Modules__Required__0..4</c> on the
/// live deployment as standing revert-debt.</para>
///
/// <para>🚨 Every test here has a partner asserting the OPPOSITE status from a neighbouring input.
/// A probe that always passed would be the skip-trapdoor this repo forbids; a probe that always
/// failed is the wedge being fixed. Both must be impossible.</para>
/// </summary>
public class RequiredModulesHealthCheckTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-required-" + Guid.NewGuid().ToString("N"));

    public RequiredModulesHealthCheckTest() => Directory.CreateDirectory(Path.Combine(root, "modules"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private Task<HealthCheckResult> Check(
        string[] required, string[] baseline, params IncompatibleModule[] incompatible)
    {
        var settings = new Dictionary<string, string?> { [ModuleRoot.ConfigKey] = root };
        for (var i = 0; i < required.Length; i++)
            settings[$"Modules:Required:{i}"] = required[i];
        for (var i = 0; i < baseline.Length; i++)
            settings[$"Modules:Assemblies:{i}"] = baseline[i];

        return new RequiredModulesHealthCheck(
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                incompatible)
            .CheckHealthAsync(new HealthCheckContext());
    }

    /// <summary>
    /// A module that IS on the deployment and loaded, whose registration threw against this build
    /// (#2234). Without a bucket of its own this fell through every check above it — the assembly
    /// is present, so "absent" is false and "expected later" is false — and landed on
    /// "every required module is present", i.e. Healthy for a replica whose feature is entirely
    /// gone. That fall-through is the regression this pins.
    /// </summary>
    [Fact]
    public async Task AnIncompatibleModule_IsUnhealthy_NotHealthy()
    {
        var broken = IncompatibleModule.From(
            "MeshWeaver.AI.AzureFoundry.dll",
            new MissingMethodException("Method not found: 'Void Ns.T..ctor(String)'."));

        var result = await Check(
            ["MeshWeaver.AI.AzureFoundry.dll"], ["MeshWeaver.AI.AzureFoundry.dll"], broken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotEqual(HealthStatus.Healthy, result.Status);
        Assert.Contains("did not install against this platform build", result.Description);
        Assert.Contains("incompatible", result.Data.Keys);
    }

    /// <summary>
    /// The partner: the same declaration with nothing broken stays Healthy, so the bucket above
    /// discriminates rather than failing every deployment that declares a required module.
    /// </summary>
    [Fact]
    public async Task NothingIncompatible_StaysHealthy()
    {
        // A module genuinely loaded in THIS process, so it classifies Present on the same evidence
        // production uses (ModuleActivationStatus.LoadedAssemblyNames). Declaring a name that is
        // merely recorded would classify Absent and fail here for a reason unrelated to the bucket
        // under test — which is exactly how this assertion misled me the first time.
        var result = await Check(
            ["MeshWeaver.PluginCatalog.dll"], ["MeshWeaver.PluginCatalog.dll"]);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>Lands an activation record, with or without the bytes it points at.</summary>
    private void Record(string name, bool withBytes, string? minMeshVersion = null)
    {
        var directory = name + "@gen";
        ModuleActivationSidecar.WriteEntry(root, new ModuleActivationEntry
        {
            Name = name, Directory = directory, MinMeshVersion = minMeshVersion,
        });
        if (!withBytes)
            return;
        var dir = Path.Combine(root, "modules", directory);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".dll"), [1]);
    }

    [Fact]
    public async Task DeclaringNothingRequired_IsHealthy()
    {
        // Inert by default — a probe that fired on deployments declaring nothing would fail every
        // portal on the first boot after it shipped.
        Assert.Equal(HealthStatus.Healthy, (await Check([], ["MeshWeaver.Social.dll"])).Status);
    }

    /// <summary>
    /// 🚨 A gate must not read its own input silently. Everything declared is accounted for, so the
    /// verdicts alone would read Healthy — but a record that could not be read means the evidence
    /// behind that answer is partial, and the reassuring answer must not stand on partial evidence.
    /// </summary>
    [Fact]
    public async Task AnUnreadableActivationRecord_IsDEGRADED_EvenWhenNothingLooksMissing()
    {
        Directory.CreateDirectory(ModuleActivationSidecar.EntriesDirectory(root));
        File.WriteAllText(ModuleActivationSidecar.EntryPath(root, "MeshWeaver.Broken"), "{ not json");

        var result = await Check(required: [], baseline: []);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("could not be read", result.Description);
        Assert.NotEmpty(Assert.IsType<string[]>(result.Data["unreadableActivationRecords"]));
    }

    /// <summary>
    /// 🚨 THE TEETH. The image's own <c>Modules:Assemblies</c> claims the pack and the image does
    /// not carry it. Nothing on this deployment can produce it and the previous pods still have it,
    /// so readiness must FAIL and hold the rollout.
    /// </summary>
    [Fact]
    public async Task APackTheImageClaimsAndLost_IsUNHEALTHY_SoTheRolloutStalls()
    {
        var result = await Check(
            required: ["MeshWeaver.Blazor.Views.dll"],
            baseline: ["MeshWeaver.Blazor.Views.dll"]);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("MeshWeaver.Blazor.Views", result.Description);
    }

    /// <summary>
    /// 🚨 THE WEDGE, gone. The same declaration for a module the image never claimed to ship is
    /// store-delivered by construction. It is reported — Degraded, named, with the reason — but it
    /// does not fail readiness, because holding the rollout cannot deliver it.
    /// </summary>
    [Fact]
    public async Task AStoreDeliveredModuleNotYetLanded_IsDEGRADED_AndNamed()
    {
        var result = await Check(
            required: ["MeshWeaver.Speech.dll"],
            baseline: ["MeshWeaver.Social.dll"]);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("MeshWeaver.Speech", result.Description);
        Assert.Contains("install the package", result.Description);
        // 🚨 Degraded is NOT silence: the operator surface must be able to enumerate it without
        // parsing prose, so both buckets ship in the payload whatever the status.
        Assert.Contains("MeshWeaver.Speech.dll", Assert.IsType<string[]>(result.Data["expected"]));
        Assert.Empty(Assert.IsType<string[]>(result.Data["missing"]));
    }

    /// <summary>The partner direction — the SAME declaration, once the module has landed and
    /// loaded, is simply Healthy. A probe stuck on Degraded would fail here.</summary>
    [Fact]
    public async Task AStoreModuleThisProcessHasLoaded_IsHEALTHY()
    {
        var loaded = typeof(ModuleActivationList).Assembly.GetName().Name!;

        var result = await Check(required: [loaded + ".dll"], baseline: []);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>Landed on the volume, awaiting the restart the rollout itself performs: Degraded,
    /// and the reason says a restart is what it waits on.</summary>
    [Fact]
    public async Task AStoreModuleLandedButNotLoaded_IsDEGRADED_AndSaysARestartActivatesIt()
    {
        Record("MeshWeaver.Speech", withBytes: true);

        var result = await Check(required: ["MeshWeaver.Speech.dll"], baseline: []);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("a restart activates it", result.Description);
    }

    /// <summary>
    /// The half-completed landing (#2093): the record says installed, the assembly is not there. A
    /// restart will not fix it, so the reason must say RE-INSTALL — never "a restart activates it",
    /// which is the false promise that left <c>/mcp</c> 404ing for a pod's whole lifetime.
    /// </summary>
    [Fact]
    public async Task AStoreModuleRecordedWithoutBytes_SaysReinstall_NotRestart()
    {
        Record("MeshWeaver.Mcp", withBytes: false);

        var result = await Check(required: ["MeshWeaver.Mcp.dll"], baseline: []);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Re-install", result.Description);
        Assert.DoesNotContain("a restart activates it", result.Description);
    }

    /// <summary>
    /// Both at once — the state that actually shipped. Unhealthy wins the STATUS (a lost pack must
    /// still stall the rollout) but the store-lane half is still named, so fixing the build does
    /// not leave the second problem undiscovered.
    /// </summary>
    [Fact]
    public async Task ALostPackAndAnOwedModule_AreBothReported_AndTheLostPackDecidesTheStatus()
    {
        var result = await Check(
            required: ["MeshWeaver.Blazor.Views.dll", "MeshWeaver.Speech.dll"],
            baseline: ["MeshWeaver.Blazor.Views.dll"]);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("MeshWeaver.Blazor.Views", result.Description);
        Assert.Contains("MeshWeaver.Speech", result.Description);
        Assert.Contains("MeshWeaver.Blazor.Views.dll", Assert.IsType<string[]>(result.Data["missing"]));
        Assert.Contains("MeshWeaver.Speech.dll", Assert.IsType<string[]>(result.Data["expected"]));
    }
}
