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

    /// <summary>
    /// 🚨 MeshWeaver#6829 — the module volume is an SMB share, on which a directory's last-write
    /// time need not move when a file is renamed into it. Reproduced here by putting the three
    /// directories' timestamps back after the second wave: the volume's CONTENT changed (two more
    /// modules landed, set 2 proposed) while every directory timestamp reads as before. A reader
    /// keyed on those timestamps keeps its first snapshot — 1 pending, the shape that made the
    /// self-updater answer NothingToActivate — while 3 are pending on disk.
    /// </summary>
    [Fact]
    public async Task ALandingIsSeen_WhenTheVolumeDoesNotMoveDirectoryTimestamps()
    {
        await LandWave(ModuleA);
        var pending = new PendingModuleActivations(root);
        var nothingLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Single(pending.Read(nothingLoaded).Pending);
        Assert.Equal(1, pending.DiskReads);

        var directories = new[]
        {
            Path.Combine(root, "modules"),
            ModuleActivationSidecar.EntriesDirectory(root),
            ModuleSetStore.SetsDirectory(root),
        };
        var before = directories.Select(Directory.GetLastWriteTimeUtc).ToArray();

        await LandWave(ModuleB, ModuleC);
        for (var i = 0; i < directories.Length; i++)
            Directory.SetLastWriteTimeUtc(directories[i], before[i]);
        Assert.Equal(before, directories.Select(Directory.GetLastWriteTimeUtc).ToArray());

        var after = pending.Read(nothingLoaded);

        Assert.Equal(2, pending.DiskReads);
        Assert.Equal(3, after.Pending.Count);
        Assert.Empty(after.Deferred);

        // And the memo still holds: an unchanged volume is not re-read.
        pending.Read(nothingLoaded);
        Assert.Equal(2, pending.DiskReads);
    }

    /// <summary>
    /// 🚨 MeshWeaver#6829, review of #6833 — a module's state is derived from the RECORDS in its own
    /// directory (<c>activation.d/&lt;Name&gt;/</c>), and a record lands by a rename into that
    /// directory, which leaves <c>activation.d/</c>'s own listing as it was. An uninstall tombstone
    /// written on its own (its projection write failed) is that change: with every directory
    /// timestamp put back, a fingerprint that lists only the three top directories keeps serving
    /// "2 pending" while the volume says 1.
    /// </summary>
    [Fact]
    public async Task ARecordOnlyChange_InsideAModulesRecordDirectory_IsSeen()
    {
        await LandWave(ModuleA, ModuleB);
        var pending = new PendingModuleActivations(root);
        var nothingLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, pending.Read(nothingLoaded).Pending.Count);
        Assert.Equal(1, pending.DiskReads);

        var entries = ModuleActivationSidecar.EntriesDirectory(root);
        var directories = new[]
            {
                Path.Combine(root, "modules"),
                entries,
                ModuleSetStore.SetsDirectory(root),
            }
            .Concat(Directory.EnumerateDirectories(entries))
            .ToArray();
        var before = directories.Select(Directory.GetLastWriteTimeUtc).ToArray();

        // The tombstone alone — no projection rewrite of activation.d/<Name>.json.
        ModuleActivationSidecar.WriteUninstall(root, ModuleB, after: null);
        for (var i = 0; i < directories.Length; i++)
            Directory.SetLastWriteTimeUtc(directories[i], before[i]);

        var after = pending.Read(nothingLoaded);

        Assert.Equal(2, pending.DiskReads);
        Assert.Equal(ModuleA, Assert.Single(after.Pending).Name, ignoreCase: true);
    }

    /// <summary>
    /// The fingerprint is volume work (a listing per directory and per module record directory), so a
    /// cancelled refresh must not start it: the token is observed before the first listing.
    /// </summary>
    [Fact]
    public async Task ACancelledFingerprint_StartsNoListing()
    {
        await LandWave(ModuleA);
        using var cancelled = new System.Threading.CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.ThrowsAny<OperationCanceledException>(
            () => PendingModuleActivations.ListingFingerprint(root, cancelled.Token));
        Assert.NotNull(PendingModuleActivations.ListingFingerprint(root));
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
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);

    private async Task LandWave(params string[] names)
    {
        foreach (var name in names)
            await Land(name);
        await landing.ProposeModuleSet().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
    }
}
