#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A landed pack the boot DECLINED is not "waiting for a restart", and the two identity
/// SCHEMES in the fleet are why every such pack was reported that way (MeshWeaver#4550).</b>
///
/// <para><b>The incident these pin — memex.systemorph.com, 2026-09-16.</b> <c>/health</c> read
/// <c>pending_module_activation: Degraded — 8 module(s) are landed but not yet loaded in this
/// process — a restart activates them: MeshWeaver.AI, MeshWeaver.Blazor.Chat, …</c>. The deployment
/// was restarted at 21:17Z on the same image; the new pods listed the same eight at 21:33Z. The
/// pod's own log said what was really happening, twice per boot and in two places that contradicted
/// each other: <c>[ModuleActivation] SKIPPED store-installed module 'MeshWeaver.AI': declined:
/// built for another platform (framework gce971b2c…; this deployment runs s4b28366…)</c>, and then
/// <c>[ModuleLoad] STALE PACK … no usable store-installed entry claims this name … Re-install the
/// module</c> — advice for a state this pod was not in.</para>
///
/// <para><b>And the comparison could never have succeeded.</b> <c>gce971b2c…</c> is a COMMIT
/// identity: every module-pack lane states one, because it reads the platform's own
/// <c>MeshWeaver.Compiler.dll</c>. <c>s4b28366…</c> is the API-SURFACE identity a portal resolves
/// from its surface manifest. Ordinal equality over two schemes answers "different" for every pair
/// that exists, so #4161's preference for the image's copy applied to every double-shipped module
/// on every boot, whatever the registry published — and the activation report, which cannot see the
/// decline, called it pending.</para>
///
/// <para><b>What makes these able to FAIL.</b> Every pack is landed through the REAL
/// <see cref="ModuleLandingService"/>, the boot decision is the REAL
/// <c>ModuleActivationBoot</c>, and the report is the REAL
/// <see cref="PendingModuleActivations"/> reading that landing off the volume. The control asserts
/// FIRST that the entry is enabled, its landed DLL exists and the pending derivation genuinely
/// sees it as not-loaded — so the assertion that it is not PENDING cannot pass because the entry
/// was out of the running for some other reason.</para>
/// </summary>
public class LandedPackDeclinedNeverSaysRestartTest : IDisposable
{
    private const string Plugin = "Acme.Widgets";
    private const string PackagePath = "Plugins/AcmeWidgets";
    private const string ImageBaseline = Plugin + ".dll";
    private const string RunningVersion = "3.0.0-ci.0";

    /// <summary>What a portal resolves for itself — the API-surface identity (memex's
    /// <c>s4b2836629f0c58ad4ed0aa683190981a</c> on the day).</summary>
    private const string SurfaceIdentity = "s4b2836629f0c58ad4ed0aa683190981a";

    /// <summary>What a packer reading the SAME platform's anchor states — the commit identity the
    /// image was built from.</summary>
    private const string PlatformCommit = "gafde4eabe0740ff658f3dae8a3073c33a2b3ea02";

    /// <summary>What the landed bundle on memex stated: a commit identity, from an EARLIER platform
    /// build (core <c>ce971b2c</c>, merged two hours before the image's own commit).</summary>
    private const string BundleCommit = "gce971b2cd3c81912748cb3786490ee2035addd29";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-declined-" + Guid.NewGuid().ToString("N"));

    private readonly ModuleLandingService landing;

    public LandedPackDeclinedNeverSaysRestartTest()
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

    // ───────────────────────────────────────────────── the scheme mismatch, measured

    /// <summary>
    /// 🚨 THE CONTROL for the comparison itself: a platform that states BOTH of its readings
    /// matches a bundle packed by its own build. On main the surface identity is the only reading,
    /// the commit-stated bundle can never equal it, and this store copy is declined — which is the
    /// state memex.systemorph.com was in for every module its image also ships.
    /// </summary>
    [Fact]
    public async Task APackFromThisPlatformsOwnBuild_Matches_AndOverridesTheImage()
    {
        await Land(PlatformCommit);

        var skips = new List<(string Module, string Reason)>();
        var effective = Boot([SurfaceIdentity, PlatformCommit], (m, r) => skips.Add((m, r)));

        var module = Assert.Single(effective);
        module.Landed.Should().NotBeNull(
            "the bundle states the identity a packer reads off THIS platform's anchor, so it was "
            + "built here — comparing it against the surface identity alone is what made that "
            + "unprovable and declined it for ever");
        module.Landed!.FrameworkMvid.Should().Be(PlatformCommit);
        module.BaselineEntry.Should().Be(ImageBaseline, "the displaced baseline still travels (#3735)");
        Assert.Empty(skips);
    }

    /// <summary>
    /// A pack from ANOTHER build of the platform is still declined — the #4161 preference, intact —
    /// and now the reason is a comparison that was actually made, naming both commits.
    /// </summary>
    [Fact]
    public async Task APackFromAnotherBuild_IsStillDeclined_NamingBothIdentities()
    {
        await Land(BundleCommit);

        var skips = new List<(string Module, string Reason)>();
        var effective = Boot([SurfaceIdentity, PlatformCommit], (m, r) => skips.Add((m, r)));

        Assert.Single(effective).Landed.Should().BeNull("the image's own copy is preferred");
        var skip = Assert.Single(skips);
        skip.Reason.Should().Contain("declined");
        skip.Reason.Should().Contain(BundleCommit, "the reason names what the store copy was built for");
        skip.Reason.Should().Contain(PlatformCommit, "and the reading it was measured against");
    }

    /// <summary>
    /// 🚨 A platform that states ONLY its surface identity still prefers its own copy — un-declining
    /// an uncomparable pack would reinstate Plugins#1483, where a store pack that LINKED fine
    /// rendered the Subscribe panel as a whole-tree <c>ToString()</c>. What changes is the words: it
    /// no longer asserts a difference nothing measured, and it no longer sends the reader to
    /// re-install.
    /// </summary>
    [Fact]
    public async Task APackThisPlatformCannotCompareItselfTo_IsDeclined_WithoutClaimingADifference()
    {
        await Land(BundleCommit);

        var skips = new List<(string Module, string Reason)>();
        Assert.Single(Boot([SurfaceIdentity], (m, r) => skips.Add((m, r)))).Landed.Should().BeNull();

        var skip = Assert.Single(skips);
        skip.Reason.Should().Contain("cannot compare itself to",
            "a scheme mismatch is the absence of a comparison, not the result of one");
        skip.Reason.Should().NotContain("No action is needed",
            "that line belongs to a measured difference, which the next publication clears");
        skip.Reason.Should().Contain("No restart and no re-install changes this",
            "both land the same bytes and reach the same verdict — the remedy is a publication");
    }

    // ─────────────────────────────────────── the report: what /health is handed

    /// <summary>
    /// 🚨 THE CONTROL this whole suite exists for. The declined pack is exactly the shape the
    /// pending derivation reads as "landed but not yet loaded" — asserted first, so this cannot
    /// pass by the entry being out of the running — and the report must nonetheless NOT call it
    /// pending, must name it as declined, and must never print the restart promise.
    /// </summary>
    [Fact]
    public async Task ADeclinedPack_IsReportedAsDeclined_AndNeverAsRestartRequired()
    {
        await Land(BundleCommit);

        var record = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        record.Enabled.Should().BeTrue("a disabled entry is an uninstall, which is a different state");
        ModuleActivationBoot.LandedModuleDllExists(root, record).Should().BeTrue(
            "the landed DLL EXISTS — an absent one is the UNRESOLVABLE state (#2093), not this one");

        // The pending derivation's own answer for this entry, with the process running the IMAGE's
        // copy (its generation leaf is the module's fixed folder, never the landed <name>@<gen>).
        // This is the reading that produced "a restart activates them" — if it were empty here, the
        // assertions below would pass having measured nothing.
        ModuleActivationStatus.NotYetLoaded(
                ModuleActivationSidecar.Read(root), Loaded, ImageGeneration,
                floor => ModulePlatformFloor.DeclineReason(floor, RunningVersion),
                entry => ModuleActivationBoot.LandedModuleDllExists(root, entry))
            .Should().ContainSingle(p => p.Name == Plugin,
                "the entry IS landed-and-not-loaded by that rule — which is why the decline has to "
                + "be subtracted by NAME rather than hoped away");

        var report = Report([SurfaceIdentity, PlatformCommit]).Read(Loaded, ImageGeneration);

        report.IsUndetermined.Should().BeFalse();
        report.HasPending.Should().BeFalse(
            "a restart re-runs the same comparison on the same bytes and declines again");
        report.HasUnresolvable.Should().BeFalse("the landed DLL is on the volume");
        report.HasDeclined.Should().BeTrue();

        var declined = Assert.Single(report.Declined);
        declined.Name.Should().Be(Plugin);
        declined.PackagePath.Should().Be(PackagePath, "a package card matches on this");
        declined.StatedIdentity.Should().Be(BundleCommit);
        declined.Generation.Should().Be(record.Directory, "the row names the generation NOT in effect");

        var described = report.Describe();
        described.Should().NotContain("a restart activates them",
            "that sentence is the false remedy two operators acted on");
        described.Should().Contain("DECLINED in favour of the copy this image ships");
        described.Should().Contain("A RESTART DOES NOT CHANGE THIS");
        described.Should().Contain(Plugin);
    }

    /// <summary>
    /// The other half: a pack this platform DID build is not declined, and the report says nothing
    /// about it at all — no pending row, no declined row, nothing for an operator to do. Declining
    /// unconditionally (or reporting every override as declined) reds this.
    /// </summary>
    [Fact]
    public async Task APackFromThisPlatformsOwnBuild_IsReportedAsNothingAtAll()
    {
        await Land(PlatformCommit);

        var landedGeneration = Assert.Single(ModuleActivationSidecar.Read(root).Entries).Directory!;
        var report = Report([SurfaceIdentity, PlatformCommit]).Read(
            Loaded,
            // The process is running the landed generation — which is what boot now hands the
            // loader, because the pack matches this platform's own reading.
            ImmutableDictionary<string, string>.Empty.Add(Plugin, landedGeneration));

        report.HasPending.Should().BeFalse("the generation the set activates is the one loaded here");
        report.HasDeclined.Should().BeFalse("nothing was preferred over it");
        report.Describe().Should().Contain("no module activation pending");
    }

    /// <summary>
    /// 🚨 A module the image does NOT ship is never declined and never reported as such, whatever
    /// it states: there is nothing to prefer it to, and calling it declined would put an unactionable
    /// row on a module that is simply installed. It stays PENDING, which for a Store-only module is
    /// the truth — a restart does load it.
    /// </summary>
    [Fact]
    public async Task AStoreOnlyModule_StaysPending_AndIsNeverDeclined()
    {
        await Land(BundleCommit);

        var report = new PendingModuleActivations(root)
        {
            // The image ships no copy of this module — the baseline is empty.
            PlatformIdentities = [SurfaceIdentity, PlatformCommit],
        }.Read(Loaded, ImageGeneration);

        report.HasDeclined.Should().BeFalse("there is no image copy to prefer");
        report.HasPending.Should().BeTrue("a restart genuinely activates a Store-only module");
        report.Describe().Should().Contain("a restart activates them");
    }

    // ───────────────────────────────────────────────────────────────────────── harness

    /// <summary>The module name, as this process's loaded set would report it — the image's copy
    /// IS loaded, which is precisely why the pending rule falls to the generation comparison.</summary>
    private static IReadOnlySet<string> Loaded =>
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, Plugin);

    /// <summary>Where the loaded copy came from: the image's fixed <c>modules/&lt;name&gt;/</c>
    /// folder, whose leaf is the bare module name — never a landed <c>&lt;name&gt;@&lt;gen&gt;</c>.</summary>
    private static IReadOnlyDictionary<string, string> ImageGeneration =>
        ImmutableDictionary<string, string>.Empty.Add(Plugin, Plugin);

    private async Task Land(string frameworkMvid) =>
        await landing.LandModule(
                Plugin, [(Plugin + ".dll", RealAssemblyBytes)],
                frameworkMvid: frameworkMvid, packagePath: PackagePath, version: "1.0.0",
                minMeshVersion: null)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    /// <summary>One replica's boot, with every reading the platform states about itself — the seam
    /// production fills from <c>PrebuiltAssemblySeeder.LiveFrameworkMvid</c> and
    /// <c>FrameworkBuildIdentity.ProducerStatedIdentity</c>.</summary>
    private IReadOnlyList<EffectiveModule> Boot(
        IReadOnlyCollection<string> platformIdentities,
        Action<string, string>? onSkipped = null) =>
        ModuleActivationBoot.ComputeEffectiveModuleEntriesForPlatform(
            [ImageBaseline],
            ModuleActivationSidecar.Read(root),
            floor => ModulePlatformFloor.DeclineReason(floor, RunningVersion),
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry),
            onSkipped,
            onAdvisory: null,
            platformIdentities);

    /// <summary>The reader as production registers it: the image's baseline names and both of the
    /// platform's readings.</summary>
    private PendingModuleActivations Report(IReadOnlyCollection<string> platformIdentities) =>
        new(root)
        {
            ImageShippedModules = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, Plugin),
            PlatformIdentities = platformIdentities,
        };

    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);
}
