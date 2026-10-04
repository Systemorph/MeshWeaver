#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Where the framework identity cannot tell an image copy from a store copy of one module,
/// the VERSION the image's copy was built as decides (MeshWeaver#6044).</b>
///
/// <para><b>The incident these pin — memex.meshweaver.cloud, 2026-09-28.</b> The image
/// <c>3.0.0-ci.9554</c> was built from Plugins <c>8931656f</c> and shipped its own
/// <c>MeshWeaver.AI</c>; the module store held <c>MeshWeaver.AI</c> 1.16.3 built from the OLDER
/// <c>6d21172e</c>. Both stated the same framework identity — under the compatibility key every
/// build of one epoch does — so boot's pass 1 had nothing to decline on and the store copy
/// overrode the image's: a correct image ran pre-<c>8931656f</c> code (the admin page rendered the
/// old AI / Instance-registration groups). The image's lock still read 1.16.3 because it was not
/// yet settled, so the two copies carried the SAME version number over different sources.</para>
///
/// <para><b>What makes these able to FAIL.</b> Every store copy is landed through the REAL
/// <see cref="ModuleLandingService"/> stating the identity this platform states, and the control
/// first proves the store copy WINS when no image stamp is consulted — that is the defect,
/// measured, not assumed. The image stamp is read through the production reader
/// (<see cref="ImageModuleSeed.Read"/>) from a file in the exact shape the closure lane writes.
/// Reverting the rule in <c>ComputeEffectiveModuleEntriesAgainstImage</c> reds the control on
/// <c>EffectiveModule.Landed</c>.</para>
///
/// <para><b>The negatives are the boundary.</b> A strictly NEWER store release still wins (the
/// upgrade path, #2548). No stamp, a stamp without a version, or a store entry without one decides
/// nothing (rule R2). A module the image does not ship is never declined.</para>
/// </summary>
public class ImageCopyVersionDiscriminatorTest : IDisposable
{
    private const string Plugin = "Acme.Widgets";
    private const string PackagePath = "Plugins/AcmeWidgets";
    private const string ImageBaseline = Plugin + ".dll";
    private const string RunningVersion = "3.0.0-ci.0";

    /// <summary>What a portal resolves for itself (the API-surface reading).</summary>
    private const string SurfaceIdentity = "s4b2836629f0c58ad4ed0aa683190981a";

    /// <summary>The producer reading every bundle of this epoch states — and the store copy too,
    /// which is exactly why the identity cannot decide here.</summary>
    private const string PlatformCommit = "gafde4eabe0740ff658f3dae8a3073c33a2b3ea02";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-imageseed-" + Guid.NewGuid().ToString("N"));

    /// <summary>Stands in for the image's own <c>modules/&lt;Name&gt;/</c> folder.</summary>
    private readonly string imageModuleDir;

    private readonly ModuleLandingService landing;

    public ImageCopyVersionDiscriminatorTest()
    {
        Directory.CreateDirectory(root);
        imageModuleDir = Path.Combine(root, "image", "modules", Plugin);
        Directory.CreateDirectory(imageModuleDir);
        landing = new ModuleLandingService(baseDirectory: root);
    }

    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    // ─────────────────────────────────────────────────────────────────── the control

    /// <summary>
    /// 🚨 THE CONTROL — the incident. A store copy at the SAME version as the image's own copy,
    /// stating this platform's identity: without the stamp it overrides the image (the defect);
    /// with it, the image's copy runs and the store copy is reported declined.
    /// </summary>
    [Fact]
    public async Task AStoreCopyAtTheImagesOwnVersion_IsDeclined_AndTheImagesCopyRuns()
    {
        await Land("1.16.3");
        Stamp("1.16.3");

        // The defect, measured: the identity matches, so the identity rule alone lets the older
        // store copy override the image. If this did not hold, the assertions below would pass
        // having measured nothing.
        var withoutStamp = Assert.Single(BootForPlatform());
        withoutStamp.Landed.Should().NotBeNull(
            "the store copy states this platform's identity, so the identity rule cannot decline it — "
            + "this is how the image built from 8931656f ran the store's 6d21172e build");

        var skips = new List<(string Module, string Reason)>();
        var module = Assert.Single(Boot((m, r) => skips.Add((m, r))));

        module.Entry.Should().Be(ImageBaseline, "the image's own Modules:Assemblies line is handed to the loader");
        module.Landed.Should().BeNull(
            "a store copy that is not a strictly newer release than the image's copy must not "
            + "override it — a non-null Landed here IS #6044");
        module.PreferImageCopy.Should().BeTrue(
            "the resolver must not hand the declined bytes back through the landed probe");
        var (skipped, reason) = Assert.Single(skips);
        skipped.Should().Be(Plugin);
        reason.Should().Contain("1.16.3").And.Contain("SAME version").And.Contain(ImageModuleSeed.FileName);
    }

    /// <summary>An OLDER store release is declined the same way.</summary>
    [Fact]
    public async Task AnOlderStoreRelease_IsDeclined()
    {
        await Land("1.16.2");
        Stamp("1.16.4");

        var skips = new List<(string Module, string Reason)>();
        Assert.Single(Boot((m, r) => skips.Add((m, r)))).Landed.Should().BeNull();
        Assert.Single(skips).Reason.Should().Contain("OLDER than");
    }

    // ─────────────────────────────────────────────────────────────────── the boundary

    /// <summary>A strictly NEWER store release overrides the image's copy — the ordinary upgrade
    /// path (#2548), and the reason a registry can ship a fix without an image. Declining
    /// unconditionally passes the control and reds this.</summary>
    [Fact]
    public async Task ANewerStoreRelease_StillOverridesTheImage()
    {
        await Land("1.16.4");
        Stamp("1.16.3");

        var skips = new List<(string Module, string Reason)>();
        var module = Assert.Single(Boot((m, r) => skips.Add((m, r))));
        module.Landed.Should().NotBeNull("1.16.4 is a newer release than the image's 1.16.3");
        module.Landed!.Version.Should().Be("1.16.4");
        module.BaselineEntry.Should().Be(ImageBaseline, "the displaced baseline still travels (#3735)");
        Assert.Empty(skips);
    }

    /// <summary>No stamp — an image built before it existed — decides nothing: today's rule.</summary>
    [Fact]
    public async Task NoImageStamp_DecidesNothing()
    {
        await Land("1.16.3");

        Assert.Single(Boot()).Landed.Should().NotBeNull(
            "absence of the image's record is absence of evidence (rule R2), never 'older'");
    }

    /// <summary>A stamp with no version, or one describing ANOTHER module, decides nothing.</summary>
    [Fact]
    public async Task AStampWithoutAVersion_OrForAnotherModule_DecidesNothing()
    {
        await Land("1.16.3");

        File.WriteAllText(Path.Combine(imageModuleDir, ImageModuleSeed.FileName),
            $$"""{ "schema": "{{ImageModuleSeed.Schema}}", "module": "{{Plugin}}", "package": "AcmeWidgets" }""");
        Assert.Single(Boot()).Landed.Should().NotBeNull("the stamp states no version");

        File.WriteAllText(Path.Combine(imageModuleDir, ImageModuleSeed.FileName),
            $$"""{ "schema": "{{ImageModuleSeed.Schema}}", "module": "Other.Module", "version": "9.9.9" }""");
        ImageModuleSeed.Read(imageModuleDir, Plugin).Should().BeNull(
            "a stamp describing another module must never decide for this one");
        Assert.Single(Boot()).Landed.Should().NotBeNull();

        File.WriteAllText(Path.Combine(imageModuleDir, ImageModuleSeed.FileName), "{ not json");
        ImageModuleSeed.Read(imageModuleDir, Plugin).Should().BeNull("unreadable states nothing");
    }

    /// <summary>A store entry that recorded no version decides nothing either.</summary>
    [Fact]
    public void AStoreEntryWithoutAVersion_DecidesNothing()
    {
        ImageModuleSeed.DeclineReason(
                new ModuleActivationEntry { Name = Plugin, Version = null },
                new ImageModuleSeed(Plugin, "AcmeWidgets", "1.16.3", null))
            .Should().BeNull();
    }

    /// <summary>A module the image does NOT ship is never declined, whatever a stamp lookup says —
    /// there is nothing to prefer it to.</summary>
    [Fact]
    public async Task AStoreOnlyModule_IsNeverDeclined()
    {
        await Land("1.0.0");
        Stamp("9.0.0");

        var module = Assert.Single(ModuleActivationBoot.ComputeEffectiveModuleEntriesAgainstImage(
            baselineEntries: [],
            ModuleActivationSidecar.Read(root),
            floor => ModulePlatformFloor.DeclineReason(floor, RunningVersion),
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry),
            onSkipped: null,
            onAdvisory: null,
            [SurfaceIdentity, PlatformCommit],
            name => ImageModuleSeed.Read(imageModuleDir, name)));
        module.Landed.Should().NotBeNull();
    }

    // ─────────────────────────────────────────────────────── the report agrees with the boot

    /// <summary>
    /// 🚨 The pending report re-applies the SAME rule, so a store copy the boot declines as not
    /// newer than the image's copy is reported DECLINED — never "landed, a restart activates it",
    /// which a restart would refute (the false remedy of MeshWeaver#4550, one rule over).
    /// </summary>
    [Fact]
    public async Task TheReport_CallsItDeclined_NeverRestartRequired()
    {
        await Land("1.16.3");
        Stamp("1.16.3");

        var report = new PendingModuleActivations(root)
        {
            ImageShippedModules = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, Plugin),
            PlatformIdentities = [SurfaceIdentity, PlatformCommit],
            ImageSeedOf = name => ImageModuleSeed.Read(imageModuleDir, name),
        }.Read(
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, Plugin),
            // The process runs the image's copy: its generation leaf is the fixed module folder.
            ImmutableDictionary<string, string>.Empty.Add(Plugin, Plugin));

        report.IsUndetermined.Should().BeFalse();
        report.HasPending.Should().BeFalse("a restart re-runs the same comparison and declines again");
        var declined = Assert.Single(report.Declined);
        declined.Name.Should().Be(Plugin);
        declined.Version.Should().Be("1.16.3");
        report.Describe().Should().NotContain("a restart activates them");
    }

    // ─────────────────────── what the boot tells the import it runs (MeshWeaver.Plugins#2715)

    /// <summary>
    /// The package release each module this boot chose IS — what the GitSync import judges an
    /// incoming module's <c>requires</c> against. A landed copy states its recorded version and the
    /// package of its install record; the image's own copy states its stamp. The two cases are the
    /// two the #6044 rule produces, so a declined store copy must report the IMAGE's release.
    /// </summary>
    [Fact]
    public async Task TheActivatedVersion_IsTheReleaseTheBootChose()
    {
        await Land("1.16.4");
        Stamp("1.16.3");
        var newer = Assert.Single(Boot());
        ModuleActivationBoot.ActivatedVersionOf(newer, name => ImageModuleSeed.Read(imageModuleDir, name))
            .Should().Be(new MeshWeaver.Mesh.ActivatedModuleVersion(Plugin, "AcmeWidgets", "1.16.4"),
                "the newer store copy won, so the instance runs its release");

        Stamp("1.16.5");
        var image = Assert.Single(Boot());
        image.Landed.Should().BeNull("1.16.4 is older than the image's 1.16.5");
        ModuleActivationBoot.ActivatedVersionOf(image, name => ImageModuleSeed.Read(imageModuleDir, name))
            .Should().Be(new MeshWeaver.Mesh.ActivatedModuleVersion(Plugin, "AcmeWidgets", null),
                "the image's copy runs, and its stamp comes from a committed lock that can lag its sources — "
                + "so it states no version (judging nothing), and never the declined store copy's 1.16.4");
    }

    // ───────────────────────────────────────────────────────────────────────── harness

    /// <summary>Writes the stamp exactly as the closure lane (<c>WriteMeshModuleSeedStamps</c>)
    /// does.</summary>
    private void Stamp(string version) =>
        File.WriteAllText(Path.Combine(imageModuleDir, ImageModuleSeed.FileName),
            "{\n  \"schema\": \"mw-module-seed/1\",\n  \"module\": \"" + Plugin
            + "\",\n  \"package\": \"AcmeWidgets\",\n  \"version\": \"" + version
            + "\",\n  \"moduleVersion\": \"0123456789abcdef\"\n}\n");

    private async Task Land(string version) =>
        await landing.LandModule(
                Plugin, [(Plugin + ".dll", RealAssemblyBytes)],
                frameworkMvid: PlatformCommit, packagePath: PackagePath, version: version,
                minMeshVersion: null)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    /// <summary>One replica's boot, composed as <c>MemexConfiguration</c> composes it, with the
    /// image's stamp read through the production reader.</summary>
    private IReadOnlyList<EffectiveModule> Boot(Action<string, string>? onSkipped = null) =>
        ModuleActivationBoot.ComputeEffectiveModuleEntriesAgainstImage(
            [ImageBaseline],
            ModuleActivationSidecar.Read(root),
            floor => ModulePlatformFloor.DeclineReason(floor, RunningVersion),
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry),
            onSkipped,
            onAdvisory: null,
            [SurfaceIdentity, PlatformCommit],
            name => ImageModuleSeed.Read(imageModuleDir, name));

    /// <summary>The same boot with no stamp consulted — the pre-#6044 rule.</summary>
    private IReadOnlyList<EffectiveModule> BootForPlatform() =>
        ModuleActivationBoot.ComputeEffectiveModuleEntriesForPlatform(
            [ImageBaseline],
            ModuleActivationSidecar.Read(root),
            floor => ModulePlatformFloor.DeclineReason(floor, RunningVersion),
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry),
            onSkipped: null,
            onAdvisory: null,
            [SurfaceIdentity, PlatformCommit]);

    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);
}
