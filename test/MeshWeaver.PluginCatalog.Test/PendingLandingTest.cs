using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The GENERATIONAL landing contract (2026-08-20): a landing writes a fresh
/// <c>modules/&lt;name&gt;@&lt;id&gt;/</c> and moves the activation pointer — nothing on the
/// landing path ever deletes or overwrites a directory a running pod may hold open. The
/// delete-based swap could not be made safe on a shared volume: open files refuse deletion on
/// SMB, and the boot-time deferred apply raced the other pods of a rolling restart — 13 of 15
/// module closures were reduced to their entry DLL. Boot GC reclaims what nothing references.
/// </summary>
public class PendingLandingTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-gen-" + Guid.NewGuid().ToString("N"));

    public PendingLandingTest() => Directory.CreateDirectory(Path.Combine(root, "modules"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private string Modules => Path.Combine(root, "modules");

    private void Write(string folder, string file, string content)
    {
        Directory.CreateDirectory(Path.Combine(Modules, folder));
        File.WriteAllText(Path.Combine(Modules, folder, file), content);
    }

    private ModuleActivationEntry Entry(string name, string? directory) =>
        new() { Name = name, Directory = directory };

    [Fact]
    public void ModuleDirectoryFor_FollowsThePointer_AndFallsBackToLegacy()
    {
        Assert.Equal(
            Path.Combine(Modules, "MeshWeaver.Widget@abc12345"),
            ModuleLandingService.ModuleDirectoryFor(root, "MeshWeaver.Widget",
                Entry("MeshWeaver.Widget", "MeshWeaver.Widget@abc12345")));
        // Legacy entry (no generation) and no entry at all both resolve the fixed folder.
        Assert.Equal(
            Path.Combine(Modules, "MeshWeaver.Widget"),
            ModuleLandingService.ModuleDirectoryFor(root, "MeshWeaver.Widget",
                Entry("MeshWeaver.Widget", null)));
        Assert.Equal(
            Path.Combine(Modules, "MeshWeaver.Widget"),
            ModuleLandingService.ModuleDirectoryFor(root, "MeshWeaver.Widget", null));
    }

    /// <summary>Backdates a directory's write time past the default GC grace window so a test can
    /// assert immediate collection of a genuinely orphaned (not merely fresh) directory.</summary>
    private static void Backdate(string dir) =>
        Directory.SetLastWriteTimeUtc(dir,
            DateTime.UtcNow - ModuleLandingService.DefaultGarbageMinAge - TimeSpan.FromMinutes(1));

    [Fact]
    public void CollectGarbage_RemovesOnlyTheUnreferenced()
    {
        // Two generations of Widget; the sidecar references the second. Plus legacy folder,
        // leftover staging, and a retired pending — the legacy folder must SURVIVE (entries
        // without a generation still resolve there), everything else goes. All three collectable
        // directories are backdated past the grace window (#2303) — they are genuine orphans here,
        // not a landing whose entry has not landed yet, which is the OTHER test below.
        Write("MeshWeaver.Widget@old00001", "MeshWeaver.Widget.dll", "OLD");
        Write("MeshWeaver.Widget@new00002", "MeshWeaver.Widget.dll", "NEW");
        Write("MeshWeaver.Widget", "MeshWeaver.Widget.dll", "LEGACY");
        Write(".staging-MeshWeaver.Widget-x", "half.dll", "H");
        Write(".pending-MeshWeaver.Widget", "MeshWeaver.Widget.dll", "P");
        Backdate(Path.Combine(Modules, "MeshWeaver.Widget@old00001"));
        Backdate(Path.Combine(Modules, ".staging-MeshWeaver.Widget-x"));
        Backdate(Path.Combine(Modules, ".pending-MeshWeaver.Widget"));
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            Entries = [new ModuleActivationEntry
            {
                Name = "MeshWeaver.Widget",
                Directory = "MeshWeaver.Widget@new00002",
            }],
        });

        var removed = ModuleLandingService.CollectGarbage(root);

        Assert.Equal(3, removed);
        Assert.False(Directory.Exists(Path.Combine(Modules, "MeshWeaver.Widget@old00001")));
        Assert.False(Directory.Exists(Path.Combine(Modules, ".staging-MeshWeaver.Widget-x")));
        Assert.False(Directory.Exists(Path.Combine(Modules, ".pending-MeshWeaver.Widget")));
        Assert.True(Directory.Exists(Path.Combine(Modules, "MeshWeaver.Widget@new00002")),
            "the referenced generation survives");
        Assert.True(Directory.Exists(Path.Combine(Modules, "MeshWeaver.Widget")),
            "the legacy fixed folder survives — un-generationed entries resolve there");
    }

    /// <summary>
    /// #2303's root cause, pinned: a landing's two writes (move the bytes, THEN write the
    /// activation entry) are not atomic across replicas. A GC pass that runs in the gap — after
    /// the bytes landed, before the entry that claims them — must NOT delete the generation just
    /// because nothing references it YET, or a real activation entry ends up pointing at nothing a
    /// moment later. This reproduces the exact interleave without threads: land the bytes (as
    /// <c>LandCore</c>'s <c>Directory.Move</c> would, freshly timestamped), run GC BEFORE the entry
    /// exists, then write the entry — the generation must have survived GC for the entry to be
    /// resolvable.
    /// </summary>
    [Fact]
    public void CollectGarbage_LeavesAFreshGeneration_ThatNoEntryReferencesYet()
    {
        // The bytes land first — exactly LandCore's ordering — with NO activation entry yet.
        Write("MeshWeaver.Widget@race00001", "MeshWeaver.Widget.dll", "RACE");

        // A concurrent replica's boot GC runs in the gap before this landing's WriteEntry.
        var removed = ModuleLandingService.CollectGarbage(root);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(Path.Combine(Modules, "MeshWeaver.Widget@race00001")),
            "a fresh, unreferenced generation must survive GC — it may be a landing whose entry "
            + "has not reached this replica's read yet (#2303)");

        // The landing's second write lands afterward, exactly as LandCore performs it.
        ModuleActivationSidecar.WriteEntry(root, new ModuleActivationEntry
        {
            Name = "MeshWeaver.Widget",
            Directory = "MeshWeaver.Widget@race00001",
        });

        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root,
            ModuleActivationSidecar.Read(root).Entries.Single(e => e.Name == "MeshWeaver.Widget")),
            "the entry's bytes must still be there — GC must not have won the race");
    }

    /// <summary>The other half of the invariant: once a directory is genuinely old AND still
    /// unreferenced, GC must still reclaim it — the grace period defers collection, it does not
    /// disable it (a real orphan is not a permanent leak).</summary>
    [Fact]
    public void CollectGarbage_ReclaimsAnUnreferencedGeneration_OnceItIsOldEnough()
    {
        Write("MeshWeaver.Widget@stale00001", "MeshWeaver.Widget.dll", "STALE");

        // Still inside the window: survives.
        var removedTooSoon = ModuleLandingService.CollectGarbage(
            root, minAge: TimeSpan.FromMinutes(5),
            nowUtc: DateTime.UtcNow + TimeSpan.FromMinutes(4));
        Assert.Equal(0, removedTooSoon);
        Assert.True(Directory.Exists(Path.Combine(Modules, "MeshWeaver.Widget@stale00001")));

        // Past the window: a real orphan is still collected — the fix defers, never leaks.
        var removed = ModuleLandingService.CollectGarbage(
            root, minAge: TimeSpan.FromMinutes(5),
            nowUtc: DateTime.UtcNow + TimeSpan.FromMinutes(6));
        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(Path.Combine(Modules, "MeshWeaver.Widget@stale00001")));
    }

    [Fact]
    public void Collect_ServesTheEntrysGeneration()
    {
        Write("MeshWeaver.Widget", "MeshWeaver.Widget.dll", "LEGACY");
        Write("MeshWeaver.Widget@gen00001", "MeshWeaver.Widget.dll", "NEW");
        Write("MeshWeaver.Widget@gen00001", "Widget.Dep.dll", "DEP");
        var activation = new ModuleActivationList
        {
            Entries = [new ModuleActivationEntry
            {
                Name = "MeshWeaver.Widget",
                Directory = "MeshWeaver.Widget@gen00001",
            }],
        };

        var (files, _, decline) = ModuleBundleSource.Collect(
            root, "MeshWeaver.Widget", activation);

        Assert.Null(decline);
        Assert.All(files, f => Assert.Contains("MeshWeaver.Widget@gen00001", f));
        Assert.Equal("NEW", File.ReadAllText(files[0]));
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void Collect_WithoutAGeneration_ServesTheLegacyFolder()
    {
        Write("MeshWeaver.Widget", "MeshWeaver.Widget.dll", "CURRENT");

        var (files, _, decline) = ModuleBundleSource.Collect(
            root, "MeshWeaver.Widget", new ModuleActivationList());

        Assert.Null(decline);
        Assert.Equal("CURRENT", File.ReadAllText(files[0]));
    }
}
