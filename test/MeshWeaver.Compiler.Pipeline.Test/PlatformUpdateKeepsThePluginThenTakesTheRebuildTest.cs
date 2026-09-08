#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The module adoption policy, as the maintainer stated it (2026-09-07, #3648):</b>
///
/// <blockquote>platform version 1 and plugin version A. platform updates to version 2 ⇒ uses
/// plugin version A. plugin rebuilds to version B ⇒ updates to B after dispose request.</blockquote>
///
/// <para>Driven through the REAL code paths in a temp <c>Modules:Root</c>, never stubs: plugin A
/// lands through <see cref="ModuleLandingService.LandModule"/>; each "boot" is
/// <see cref="ModuleActivationBoot.ComputeEffectiveModuleEntries"/> composed exactly as
/// <c>MemexConfiguration.ConfigureMemexMesh</c> composes it; each registry answer is
/// <see cref="ModuleUpdateDecision.Decide"/>; plugin B lands through the same landing service
/// into a NEW generation directory and raises the <c>.pending-restart</c> marker — the "dispose
/// request": the process has to be recycled to swap, nothing swaps live.</para>
///
/// <para><b>What makes this able to fail.</b> Step 2 boots the SAME activation record on a
/// platform the declared floor does not rank above — the shape that skipped every store module on
/// 2026-09-07 — and expects A to be handed to the loader anyway (the floor is advisory now; the DLL
/// exists). Restoring the floor skip in the boot union reds it; making the reconcile remove a
/// module it has no newer bundle for reds it; a landing that overwrote A's generation instead of
/// writing B's beside it reds it.</para>
/// </summary>
public class PlatformUpdateKeepsThePluginThenTakesTheRebuildTest : IDisposable
{
    private const string Platform1 = "3.0.0-ci.100";
    private const string Platform2 = "3.0.0-ci.200";
    private const string Platform1Identity = "s0000000000000000000000000platform1";
    private const string Platform2Identity = "s0000000000000000000000000platform2";

    private const string Plugin = "Acme.Widgets";
    private const string PackagePath = "Plugins/AcmeWidgets";
    private const string VersionA = "1.0.0";
    private const string VersionB = "1.1.0";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-adoption-" + Guid.NewGuid().ToString("N"));

    private readonly ModuleLandingService landing;

    public PlatformUpdateKeepsThePluginThenTakesTheRebuildTest()
    {
        Directory.CreateDirectory(root);
        landing = new ModuleLandingService(baseDirectory: root);
    }

    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PlatformOneWithPluginA_PlatformTwoKeepsA_PluginBLandsAndAsksForTheRestart()
    {
        // ── (1) platform version 1 and plugin version A ──────────────────────────────────────
        await Land(VersionA, Platform1Identity, minMeshVersion: Platform1);

        var recordA = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal(Plugin, recordA.Name);
        Assert.Equal(VersionA, recordA.Version);
        Assert.Equal(Platform1, recordA.MinMeshVersion);
        Assert.Equal(Platform1Identity, recordA.FrameworkMvid);
        Assert.True(recordA.Enabled);
        var generationA = recordA.Directory;
        Assert.False(string.IsNullOrWhiteSpace(generationA), "a landing writes a generation directory");

        var bootOnPlatform1 = Boot(runningVersion: Platform1);
        Assert.Equal(VersionA, Assert.Single(bootOnPlatform1).Landed?.Version);
        // The restart that loaded A: the marker is cleared, as boot clears it.
        ModuleActivationSidecar.SetPendingRestart(root, false);
        Assert.False(ModuleActivationSidecar.Read(root).PendingRestart);

        // ── (2) platform updates to version 2 ⇒ uses plugin version A ────────────────────────
        var skips = new List<(string Module, string Reason)>();
        var bootOnPlatform2 = Boot(runningVersion: Platform2, onSkipped: (m, r) => skips.Add((m, r)));
        var stillA = Assert.Single(bootOnPlatform2);
        Assert.Equal(VersionA, stillA.Landed?.Version);
        Assert.Equal(generationA, stillA.Landed?.Directory);
        Assert.Empty(skips);

        // The floor is advisory now; the DLL exists — so even a platform the declared floor does
        // NOT rank above (the string order that held every portal on 2026-09-07) uses plugin
        // version A. The claim is announced, and nothing is skipped on it.
        var advisories = new List<(string Module, string Reason)>();
        var bootBelowTheDeclaredFloor = Boot(
            runningVersion: "3.0.0-ci.99",
            onSkipped: (m, r) => skips.Add((m, r)),
            onAdvisory: (m, r) => advisories.Add((m, r)));
        Assert.Equal(VersionA, Assert.Single(bootBelowTheDeclaredFloor).Landed?.Version);
        Assert.Empty(skips);
        Assert.Contains(Platform1, Assert.Single(advisories).Reason);

        // No plugin version is shipped ⇒ we use the old one. A registry that still serves A
        // (same version, same framework identity) is up to date; a registry that serves nothing
        // has nothing to decide about — never a removal.
        var stillServesA = ModuleUpdateDecision.Decide(
            VersionA, Platform1, floor => ModulePlatformFloor.DeclineReason(floor, Platform2),
            recordA, policyDecline: null, BytesPresent, Platform1Identity);
        Assert.Equal(ModuleUpdateAction.SkipUpToDate, stillServesA.Action);
        var servesNothing = ModuleUpdateDecision.Decide(
            bundleVersion: null, null, floor => ModulePlatformFloor.DeclineReason(floor, Platform2),
            recordA, policyDecline: null, BytesPresent, null);
        Assert.Equal(ModuleUpdateAction.SkipNoBundle, servesNothing.Action);
        Assert.DoesNotContain(
            new[] { stillServesA.Action, servesNothing.Action },
            action => action is ModuleUpdateAction.SkipPlatformBelowFloor);

        // ── (3) plugin rebuilds to version B ⇒ updates to B after dispose request ────────────
        var servesB = ModuleUpdateDecision.Decide(
            VersionB, Platform2, floor => ModulePlatformFloor.DeclineReason(floor, Platform2),
            recordA, policyDecline: null, BytesPresent, Platform2Identity);
        Assert.Equal(ModuleUpdateAction.Land, servesB.Action);

        await Land(VersionB, Platform2Identity, minMeshVersion: Platform2);

        var afterB = ModuleActivationSidecar.Read(root);
        Assert.True(afterB.PendingRestart, "updates to B after dispose request — the marker asks for it");
        Assert.True(File.Exists(ModuleActivationSidecar.PendingRestartMarkerPath(root)));
        var recordB = Assert.Single(afterB.Entries);
        Assert.Equal(VersionB, recordB.Version);
        Assert.Equal(Platform2Identity, recordB.FrameworkMvid);
        Assert.NotEqual(generationA, recordB.Directory);
        Assert.True(Directory.Exists(Path.Combine(root, "modules", generationA!)),
            "a landing never deletes the generation a running process may hold open");
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, recordB));

        var bootAfterTheRestart = Boot(runningVersion: Platform2);
        var nowB = Assert.Single(bootAfterTheRestart);
        Assert.Equal(VersionB, nowB.Landed?.Version);
        Assert.Equal(recordB.Directory, nowB.Landed?.Directory);

        // ── (4) version A rebuilt for platform 2 is also an update (Plugins#931) ─────────────
        var aRebuiltForPlatform2 = ModuleUpdateDecision.Decide(
            VersionA, Platform2, floor => ModulePlatformFloor.DeclineReason(floor, Platform2),
            recordA, policyDecline: null, BytesPresent, Platform2Identity);
        Assert.Equal(ModuleUpdateAction.Land, aRebuiltForPlatform2.Action);
        Assert.Contains(Platform1Identity, aRebuiltForPlatform2.Reason);
        Assert.Contains(Platform2Identity, aRebuiltForPlatform2.Reason);
    }

    // ───────────────────────────────────────────────────────────── harness

    /// <summary>Lands one version of the plugin through the REAL landing service.</summary>
    /// <remarks>
    /// 🚨 A rebuild carries DIFFERENT BYTES, and since #3656 that is what makes it a different
    /// generation: the generation directory leaf is the CONTENT ADDRESS of the landing, so two
    /// bundles whose assemblies are byte-identical resolve to one generation no matter what their
    /// manifests say the version is — correctly, because identical bytes are identical bytes. The
    /// version and the framework identity therefore ride a marker asset here, exactly as a real
    /// rebuild would differ. Landing one fixed byte array under two version strings would model a
    /// re-land, not the rebuild this test is about.
    /// </remarks>
    private async Task Land(string version, string frameworkMvid, string minMeshVersion) =>
        await landing.LandModule(
                Plugin, [(Plugin + ".dll", RealAssemblyBytes)],
                frameworkMvid: frameworkMvid, packagePath: PackagePath, version: version,
                minMeshVersion: minMeshVersion,
                staticAssets: [("wwwroot/build.txt",
                    System.Text.Encoding.UTF8.GetBytes($"{version} {frameworkMvid}"))])
            .Timeout(TestTimeouts.Convergence).Await();

    /// <summary>
    /// One replica's boot on a platform stamped <paramref name="runningVersion"/>, composed as
    /// <c>MemexConfiguration.ConfigureMemexMesh</c> composes it: read the record, project it onto
    /// the mesh's set, compute the union with the production existence check.
    /// </summary>
    private IReadOnlyList<EffectiveModule> Boot(
        string runningVersion,
        Action<string, string>? onSkipped = null,
        Action<string, string>? onAdvisory = null)
    {
        var persisted = ModuleActivationSidecar.Read(root);
        var onMeshSet = ModuleActivationBoot.ProjectOntoMeshSet(
            persisted,
            ModuleSetStore.Read(root).Proposed,
            onDeferred: null,
            landedDllExists: entry => ModuleActivationBoot.LandedModuleDllExists(root, entry));
        return ModuleActivationBoot.ComputeEffectiveModuleEntries(
            baselineEntries: null,
            onMeshSet,
            floor => ModulePlatformFloor.DeclineReason(floor, runningVersion),
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry),
            onSkipped,
            onAdvisory);
    }

    private bool BytesPresent(ModuleActivationEntry entry) =>
        ModuleActivationBoot.LandedModuleDllExists(root, entry);

    /// <summary>Real, loadable managed-assembly bytes — the landing measures the module's link
    /// requirements (#3538), so a byte stand-in would be refused as unreadable.</summary>
    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);
}
