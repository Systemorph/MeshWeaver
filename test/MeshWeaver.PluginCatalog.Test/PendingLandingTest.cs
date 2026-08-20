using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The deferred-swap half of module landing (2026-08-20): a re-land onto a RUNNING instance
/// cannot delete the loaded copy's files on an SMB volume, so the bytes park at
/// <c>modules/.pending-&lt;name&gt;</c> and boot swaps them in before anything is loaded — and
/// the serving side prefers the pending folder, so consumers always fetch what was published.
/// </summary>
public class PendingLandingTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-pending-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void ApplyPendingLandings_SwapsThePendingCopyIn()
    {
        Write("MeshWeaver.Widget", "MeshWeaver.Widget.dll", "OLD");
        Write(".pending-MeshWeaver.Widget", "MeshWeaver.Widget.dll", "NEW");
        Write(".pending-MeshWeaver.Widget", "Widget.Dep.dll", "DEP");

        var applied = ModuleLandingService.ApplyPendingLandings(root);

        Assert.Equal(1, applied);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(Modules, "MeshWeaver.Widget", "MeshWeaver.Widget.dll")));
        Assert.True(File.Exists(Path.Combine(Modules, "MeshWeaver.Widget", "Widget.Dep.dll")),
            "the whole pending closure moves, not just the entry");
        Assert.False(Directory.Exists(Path.Combine(Modules, ".pending-MeshWeaver.Widget")),
            "an applied pending folder is gone");
    }

    [Fact]
    public void ApplyPendingLandings_WithNoTarget_StillLands()
    {
        // A pending for a module whose folder vanished (fresh volume) simply becomes the folder.
        Write(".pending-MeshWeaver.Fresh", "MeshWeaver.Fresh.dll", "NEW");

        var applied = ModuleLandingService.ApplyPendingLandings(root);

        Assert.Equal(1, applied);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(Modules, "MeshWeaver.Fresh", "MeshWeaver.Fresh.dll")));
    }

    [Fact]
    public void ApplyPendingLandings_WithNothingPending_IsAQuietZero()
    {
        Write("MeshWeaver.Widget", "MeshWeaver.Widget.dll", "OLD");
        Assert.Equal(0, ModuleLandingService.ApplyPendingLandings(root));
        Assert.Equal(0, ModuleLandingService.ApplyPendingLandings(
            Path.Combine(root, "no-such-deployment")));
    }

    [Fact]
    public void Collect_PrefersThePendingCopy_SoConsumersFetchWhatWasPublished()
    {
        Write("MeshWeaver.Widget", "MeshWeaver.Widget.dll", "OLD");
        Write(".pending-MeshWeaver.Widget", "MeshWeaver.Widget.dll", "NEW");
        Write(".pending-MeshWeaver.Widget", "Widget.Dep.dll", "DEP");

        var (files, decline) = ModuleBundleSource.Collect(
            root, "MeshWeaver.Widget",
            new ModuleActivationList(), _ => null);

        Assert.Null(decline);
        Assert.All(files, f => Assert.Contains(".pending-MeshWeaver.Widget", f));
        Assert.Equal("NEW", File.ReadAllText(files[0]));
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void Collect_WithoutAPending_ServesTheModuleFolder()
    {
        Write("MeshWeaver.Widget", "MeshWeaver.Widget.dll", "CURRENT");

        var (files, decline) = ModuleBundleSource.Collect(
            root, "MeshWeaver.Widget",
            new ModuleActivationList(), _ => null);

        Assert.Null(decline);
        Assert.Equal("CURRENT", File.ReadAllText(files[0]));
    }

    [Fact]
    public void PendingPathFor_IsThePendingConvention()
    {
        Assert.Equal(
            Path.Combine(root, "modules", ".pending-X"),
            ModuleLandingService.PendingPathFor(root, "X"));
    }
}
