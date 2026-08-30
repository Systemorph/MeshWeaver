#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.PluginCatalog;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Static web assets travelling with a compiled module (the view-pack lane). An assembly is a FLAT
/// file beside the entry DLL; an asset keeps its RELATIVE path, because the pack's own components
/// request <c>_content/&lt;pack&gt;/leaflet/leaflet.js</c> and the host serves the module folder's
/// <c>wwwroot</c> — the shape has to survive the trip or the pack lands unstyled.
/// </summary>
public class ModuleStaticAssetLandingTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-assets-" + Guid.NewGuid().ToString("N"));

    public ModuleStaticAssetLandingTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private ModuleLandingService Service => new(baseDirectory: root);

    [Fact]
    public async Task Assets_LandUnderTheGeneration_KeepingTheirRelativePath()
    {
        using var service = Service;
        await service.LandModule(
                "Acme.Views",
                [("Acme.Views.dll", [1])],
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                version: "1.0.0",
                staticAssets:
                [
                    ("wwwroot/Acme.Views.styles.css", "CSS"u8.ToArray()),
                    ("wwwroot/leaflet/leaflet.js", "JS"u8.ToArray()),
                ])
            .FirstAsync().Await();

        var entry = ModuleActivationSidecar.Read(root).Entries.Single();
        var dir = ModuleLandingService.ModuleDirectoryFor(root, "Acme.Views", entry);

        Assert.Equal("CSS", File.ReadAllText(Path.Combine(dir, "wwwroot", "Acme.Views.styles.css")));
        Assert.Equal("JS", File.ReadAllText(Path.Combine(dir, "wwwroot", "leaflet", "leaflet.js")));
        // The entry DLL stays FLAT beside them — the two placements are different on purpose.
        Assert.True(File.Exists(Path.Combine(dir, "Acme.Views.dll")));
    }

    [Theory]
    [InlineData("../escape.js")]
    [InlineData("wwwroot/../../escape.js")]
    [InlineData("/etc/passwd")]
    [InlineData("wwwroot\\windows\\sep.js")]
    [InlineData("")]
    public void AnAssetPathThatEscapesTheModuleFolder_IsRefused(string path)
    {
        // These strings become paths UNDER the module folder, so a segment that escapes it is a
        // write anywhere the process can reach. Refused before any byte touches disk.
        Assert.Throws<ArgumentException>(
            () => ModuleLandingService.ValidateAssetPath(path, "Acme.Views"));
    }

    [Fact]
    public void AnOrdinaryNestedPath_IsAccepted()
    {
        ModuleLandingService.ValidateAssetPath("wwwroot/leaflet/images/marker.png", "Acme.Views");
        ModuleLandingService.ValidateAssetPath("wwwroot/x.css", "Acme.Views");
    }

    [Fact]
    public async Task ARefusedAssetPath_LandsNOTHING()
    {
        using var service = Service;
        var act = () => service.LandModule(
                "Acme.Views", [("Acme.Views.dll", [1])],
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                staticAssets: [("../escape.js", [9])])
            .FirstAsync().Await();

        await Assert.ThrowsAsync<ArgumentException>(act);
        var modules = Path.Combine(root, "modules");
        Assert.True(!Directory.Exists(modules) || Directory.GetDirectories(modules).Length == 0);
        Assert.Empty(ModuleActivationSidecar.Read(root).Entries);
    }
}
