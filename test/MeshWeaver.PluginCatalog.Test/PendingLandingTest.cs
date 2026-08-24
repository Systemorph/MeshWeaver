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

    [Fact]
    public void CollectGarbage_RemovesOnlyTheUnreferenced()
    {
        // Two generations of Widget; the sidecar references the second. Plus legacy folder,
        // leftover staging, and a retired pending — the legacy folder must SURVIVE (entries
        // without a generation still resolve there), everything else goes.
        Write("MeshWeaver.Widget@old00001", "MeshWeaver.Widget.dll", "OLD");
        Write("MeshWeaver.Widget@new00002", "MeshWeaver.Widget.dll", "NEW");
        Write("MeshWeaver.Widget", "MeshWeaver.Widget.dll", "LEGACY");
        Write(".staging-MeshWeaver.Widget-x", "half.dll", "H");
        Write(".pending-MeshWeaver.Widget", "MeshWeaver.Widget.dll", "P");
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

        var (files, decline) = ModuleBundleSource.Collect(
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

        var (files, decline) = ModuleBundleSource.Collect(
            root, "MeshWeaver.Widget", new ModuleActivationList());

        Assert.Null(decline);
        Assert.Equal("CURRENT", File.ReadAllText(files[0]));
    }
}
