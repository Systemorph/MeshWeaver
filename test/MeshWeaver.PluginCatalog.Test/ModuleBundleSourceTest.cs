#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The registry's serve rules for module bundles (#1664 Slice C, the serving half): a registry
/// fans out the module bytes on its SHELF — its own <c>modules/&lt;name&gt;/</c> tree, minus what
/// was uninstalled. 🚨 Deliberately NO serve-side platform-floor gate (2026-08-22): the shelf carries
/// modules for platforms NEWER than the instance serving them (that inversion is what broke the
/// publish/update deadlock — see <see cref="ModuleBundleSource"/>'s type doc); the floor rides
/// the index and manifest, and the CONSUMER's own gate is what decides loadability there. The
/// recorded MVID is diagnostic and never withholds a serve.
/// </summary>
public class ModuleBundleSourceTest : IDisposable
{
    private readonly string baseDirectory =
        Path.Combine(Path.GetTempPath(), "mw-bundle-src-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>Writes one static web asset at its module-relative path, directories and all —
    /// the shape <c>ModuleLandingService</c> materializes from a bundle.</summary>
    private void LayOutAsset(string name, string relativePath)
    {
        var target = Path.Combine(
            baseDirectory, "modules", name,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, [3]);
    }

    private static ModuleActivationList Activation(params ModuleActivationEntry[] entries) =>
        new() { Entries = [.. entries] };

    /// <summary>
    /// A landed pack's STATIC WEB ASSETS are served with it, each keeping its module-relative
    /// path.
    ///
    /// <para>🚨 The serve half of #2221. The publish fix alone leaves the registry holding a
    /// complete pack while handing consumers an assemblies-only bundle — every downstream portal
    /// then renders unstyled while the registry's own copy looks fine, which is the harder version
    /// of the same bug to notice. The relative path is asserted, not just the count: a component
    /// requests <c>_content/&lt;pack&gt;/Components/x.razor.js</c>, so a flattened asset 404s
    /// exactly like a dropped one.</para>
    /// </summary>
    [Fact]
    public void ALandedModulesStaticAssets_AreServedWithTheirRelativePaths()
    {
        LayOut("MeshWeaver.Social");
        LayOutAsset("MeshWeaver.Social", "wwwroot/MeshWeaver.Social.styles.css");
        LayOutAsset("MeshWeaver.Social", "wwwroot/Components/Feed.razor.js");

        var (files, assets, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social", Activation());

        Assert.Null(decline);
        Assert.Equal(
            new[] { "wwwroot/Components/Feed.razor.js", "wwwroot/MeshWeaver.Social.styles.css" },
            assets.Select(a => a.RelativePath).ToArray());
        Assert.All(assets, a => Assert.True(File.Exists(a.FullPath)));
        // The asset walk must not leak into the assembly closure: a bundle that seeded CSS as a
        // module assembly would fail only at load.
        Assert.DoesNotContain(files, f => f.Contains("wwwroot", StringComparison.Ordinal));
    }

    /// <summary>A pack with no <c>wwwroot</c> serves none — the serve side reports what landed and
    /// never fabricates an asset set.</summary>
    [Fact]
    public void AModuleWithoutAssets_ServesNone()
    {
        LayOut("MeshWeaver.Social", "Aux.dll");

        var (files, assets, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social", Activation());

        Assert.Null(decline);
        Assert.NotEmpty(files);
        Assert.Empty(assets);
    }

    [Fact]
    public void AnImageLaidOutModule_IsServed_EntryDllFirst()
    {
        // No sidecar entry at all — the image's own publish layout. Those bytes were compiled with
        // the image, so they match the running framework by construction.
        LayOut("MeshWeaver.Social", "MeshWeaver.Social.pdb", "Aux.dll", "readme.txt");

        var (files, _, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social", Activation());

        Assert.Null(decline);
        Assert.Equal("MeshWeaver.Social.dll", Path.GetFileName(files[0]));
        Assert.Equal(
            new[] { "Aux.dll", "MeshWeaver.Social.pdb" },
            files.Skip(1).Select(Path.GetFileName).ToArray());
        Assert.DoesNotContain(files, f => f.EndsWith("readme.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void AStoreLandedModuleWhoseFloorIsSatisfied_IsServed()
    {
        LayOut("MeshWeaver.Social");

        var (files, _, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social",
            Activation(new ModuleActivationEntry
            {
                Name = "MeshWeaver.Social", MinMeshVersion = "3.0.0",
            }));

        Assert.Null(decline);
        Assert.Single(files);
    }

    [Fact]
    public void ALandingBuiltByAnotherPlatformBuild_IsStillServed_TheMvidIsDiagnostic()
    {
        // The landing records which exact build produced the bytes — but modules bind by simple
        // name, so those bytes load (and therefore serve) on every platform satisfying their
        // floor. Under the old MVID-equality rule this landing went dark on the registry's first
        // image roll, which is precisely the bake semantics the module lane rejects.
        LayOut("MeshWeaver.Social");

        var (files, _, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social",
            Activation(new ModuleActivationEntry
            {
                Name = "MeshWeaver.Social",
                FrameworkMvid = "ffff9999ffff9999ffff9999ffff9999",
                MinMeshVersion = "3.0.0",
            }));

        Assert.Null(decline);
        Assert.Single(files);
    }

    [Fact]
    public void ALandingWhoseFloorExceedsTheRunningPlatform_IsStillServed()
    {
        // 🚨 The SHELF inversion (2026-08-22). This entry is either a HELD publish (a module for a
        // platform newer than this registry — the extracted-modules deadlock case) or a landing
        // the platform rolled back below; in both states this instance's own boot skips it while
        // the shelf SERVES it. The old serve-side refusal ("never fan out a module it could not
        // load itself") is exactly what deadlocked the registry against the platform update that
        // needed these modules: the floor is the CONSUMER's gate, applied against the consumer's
        // platform off the index/manifest — never the warehouse's.
        LayOut("MeshWeaver.Social");

        var (files, _, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social",
            Activation(new ModuleActivationEntry
            {
                Name = "MeshWeaver.Social", MinMeshVersion = "9.0.0",
            }));

        Assert.Null(decline);
        Assert.Single(files);
    }

    [Fact]
    public void AnUninstalledModule_IsRefused()
    {
        LayOut("MeshWeaver.Social");

        var (files, _, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social",
            Activation(new ModuleActivationEntry
            {
                Name = "MeshWeaver.Social", Enabled = false,
            }));

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

        var (files, _, decline) = ModuleBundleSource.Collect(
            baseDirectory, "MeshWeaver.Social", Activation());

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
        var (files, _, decline) = ModuleBundleSource.Collect(baseDirectory, name, Activation());

        Assert.Empty(files);
        Assert.NotNull(decline);
    }
}
