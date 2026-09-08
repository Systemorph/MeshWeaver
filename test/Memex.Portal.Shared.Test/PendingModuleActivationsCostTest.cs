using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 #3664 — <see cref="PendingModuleActivations.Read()"/> is the startup/readiness probe's cost.
/// It used to walk the module volume on every call (the sidecar, every <c>activation.d/*.json</c>,
/// the set index, one <c>File.Exists</c> per landed DLL in three passes): 8–10 s per probe on
/// memex-cloud's Azure Files volume against a 5 s probe timeout, so a fresh pod never became
/// ready and every rollout stalled. These pin the rule that replaces it: the volume is read once
/// per CHANGE of the on-disk activation state, never per call — and a change is seen.
/// </summary>
public class PendingModuleActivationsCostTest : IDisposable
{
    private const string ModuleA = "MeshWeaver.Payments.Stripe";
    private const string ModuleB = "MeshWeaver.Social";
    private const string ModuleC = "MeshWeaver.Speech";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-pending-cost-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleLandingService landing;

    public PendingModuleActivationsCostTest()
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

    /// <summary>Two probes against an unchanged volume cost ONE disk read; the second is served from the snapshot and says the same thing.</summary>
    [Fact]
    public async Task AnUnchangedVolume_IsReadOnce_HoweverOftenTheProbeAsks()
    {
        await LandWave(ModuleA, ModuleB);
        var pending = new PendingModuleActivations(root);
        var nothingLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = pending.Read(nothingLoaded);
        var second = pending.Read(nothingLoaded);
        var third = pending.Read(nothingLoaded);

        Assert.Equal(1, pending.DiskReads);
        Assert.Equal(2, first.Pending.Count);
        Assert.Equal(first.Describe(), second.Describe());
        Assert.Equal(first.Describe(), third.Describe());
    }

    /// <summary>A landing (temp-file + rename into activation.d) moves the fingerprint: the next probe re-reads and reports the new module.</summary>
    [Fact]
    public async Task ALanding_IsSeenByTheNextProbe()
    {
        await LandWave(ModuleA);
        var pending = new PendingModuleActivations(root);
        var nothingLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Single(pending.Read(nothingLoaded).Pending);
        Assert.Equal(1, pending.DiskReads);

        await LandWave(ModuleB, ModuleC);

        var after = pending.Read(nothingLoaded);
        Assert.Equal(2, pending.DiskReads);
        Assert.Equal(3, after.Pending.Count);
        Assert.Contains(after.Pending, p => string.Equals(p.Name, ModuleC, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The in-process half is NOT memoised: the same snapshot, asked with this process's loaded set,
    /// answers for that set — a module that loaded since the last probe stops being pending without
    /// a disk read.
    /// </summary>
    [Fact]
    public async Task WhatThisProcessLoaded_IsReadFresh_WithoutTouchingTheVolume()
    {
        await LandWave(ModuleA, ModuleB);
        var pending = new PendingModuleActivations(root);
        var nothingLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, pending.Read(nothingLoaded).Pending.Count);

        var loadedA = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ModuleA };
        var report = pending.Read(loadedA);

        Assert.Equal(1, pending.DiskReads);
        Assert.Single(report.Pending);
        Assert.Equal(ModuleB, report.Pending.Single().Name, ignoreCase: true);
    }

    /// <summary>A missing modules tree is a fingerprint too — the first landing into it is a change, not an identical "nothing".</summary>
    [Fact]
    public async Task TheFirstLandingIntoAnEmptyRoot_IsAChange()
    {
        var pending = new PendingModuleActivations(root);
        var nothingLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(pending.Read(nothingLoaded).Pending);
        Assert.Equal(1, pending.DiskReads);

        await LandWave(ModuleA);

        Assert.Single(pending.Read(nothingLoaded).Pending);
        Assert.Equal(2, pending.DiskReads);
    }

    private async Task Land(string name) =>
        await landing.LandModule(name, [(name + ".dll", RealAssemblyBytes)])
            .Timeout(TestTimeouts.Convergence).Await();

    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);

    private async Task LandWave(params string[] names)
    {
        foreach (var name in names)
            await Land(name);
        await landing.ProposeModuleSet().Timeout(TestTimeouts.Convergence).Await();
    }
}
