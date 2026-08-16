#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The registry's serve rules for module bundles (#1664 Slice C, the serving half): a registry may
/// only fan out module bytes it could load itself — its own <c>modules/&lt;name&gt;/</c> tree,
/// gated on the activation sidecar exactly as boot is.
/// </summary>
public class ModuleBundleSourceTest : IDisposable
{
    private readonly string baseDirectory =
        Path.Combine(Path.GetTempPath(), "mw-bundle-src-" + Guid.NewGuid().ToString("N"));

    private const string LiveMvid = "aaaa0000aaaa0000aaaa0000aaaa0000";

    private static string? Gate(string? mvid) =>
        string.Equals(mvid, LiveMvid, StringComparison.Ordinal) ? null : $"stale ({mvid})";

    public ModuleBundleSourceTest() => Directory.CreateDirectory(baseDirectory);

    public void Dispose()
    {
        if (Directory.Exists(baseDirectory))
            Directory.Delete(baseDirectory, recursive: true);
    }

    private void LayOut(string name, params string[] extraFiles)
    {
        var folder = Path.Combine(baseDirectory, "modules", name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name + ".dll"), [1]);
        foreach (var file in extraFiles)
            File.WriteAllBytes(Path.Combine(folder, file), [2]);
    }

    private static ModuleActivationList Activation(params ModuleActivationEntry[] entries) =>
        new() { Entries = [.. entries] };

    [Fact]
    public void AnImageLaidOutModule_IsServed_EntryDllFirst()
    {
        // No sidecar entry at all — the image's own publish layout. Those bytes were compiled with
        // the image, so they match the running framework by construction.
        LayOut("MeshWeaver.Social", "MeshWeaver.Social.pdb", "Aux.dll", "readme.txt");

        var (files, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social", Activation(), Gate);

        Assert.Null(decline);
        Assert.Equal("MeshWeaver.Social.dll", Path.GetFileName(files[0]));
        Assert.Equal(
            new[] { "Aux.dll", "MeshWeaver.Social.pdb" },
            files.Skip(1).Select(Path.GetFileName).ToArray());
        Assert.DoesNotContain(files, f => f.EndsWith("readme.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void AStoreLandedModuleWithTheRunningFramework_IsServed()
    {
        LayOut("MeshWeaver.Social");

        var (files, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social",
            Activation(new ModuleActivationEntry { Name = "MeshWeaver.Social", FrameworkMvid = LiveMvid }),
            Gate);

        Assert.Null(decline);
        Assert.Single(files);
    }

    [Fact]
    public void AFrameworkStaleLanding_IsRefused()
    {
        // The registry itself rolled its image since it landed this module: the folder holds bytes
        // the registry's OWN boot skips, and serving them would hand a consumer assemblies stamped
        // with a framework neither side runs.
        LayOut("MeshWeaver.Social");

        var (files, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social",
            Activation(new ModuleActivationEntry { Name = "MeshWeaver.Social", FrameworkMvid = "ffff" }),
            Gate);

        Assert.Empty(files);
        Assert.Contains("stale", decline);
    }

    [Fact]
    public void AnUninstalledModule_IsRefused()
    {
        LayOut("MeshWeaver.Social");

        var (files, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social",
            Activation(new ModuleActivationEntry
            {
                Name = "MeshWeaver.Social", FrameworkMvid = LiveMvid, Enabled = false,
            }),
            Gate);

        Assert.Empty(files);
        Assert.Contains("uninstalled", decline);
    }

    [Fact]
    public void AModuleWithoutItsEntryDll_IsRefused()
    {
        // Covers the transitional publish state too: a module still riding the app closure prunes
        // its modules/ folder empty, and an empty folder must serve nothing rather than a partial
        // closure.
        Directory.CreateDirectory(Path.Combine(baseDirectory, "modules", "MeshWeaver.Social"));

        var (files, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social", Activation(), Gate);

        Assert.Empty(files);
        Assert.Contains("does not exist", decline);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("..")]
    public void AnInvalidModuleName_IsRefused(string name)
    {
        var (files, decline) = ModuleBundleSource.Collect(baseDirectory, name, Activation(), Gate);

        Assert.Empty(files);
        Assert.NotNull(decline);
    }
}
