#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The FRAMEWORK IDENTITY is what decides between two copies of the same module — not the
/// existence of a DLL (#4161).</b>
///
/// <para><b>The incident these pin — memex-local, 2026-09-08/09 (Plugins#1483).</b> The portal was
/// built from source that day (<c>3.0.0-ci.0</c>) and its image shipped its own
/// <c>MeshWeaver.Blazor.Views</c>, written <c>2026-09-07 08:30:50Z</c>, mvid <c>8c833e20</c>. The
/// self-registry also held a store pack of the same name built <c>2026-08-27 14:00:29Z</c>, mvid
/// <c>a87ef06f</c>, declaring an <c>rc8</c> floor. Boot's Pass 1 made the store copy an override on
/// the strength of ONE fact — its landed DLL existed — the link probe passed (the module's linked
/// types exist, which is a DIFFERENT property from "correct for this platform"), the module loaded,
/// and the view registrations inside no longer matched the control types the platform emits. The
/// Subscribe panel's OUTERMOST control rendered as <c>StackControl { … }</c>: a whole-tree
/// <c>ToString()</c> fallback, not a missing-view error, and therefore nothing that reads as a
/// module problem to anyone looking at the screen.</para>
///
/// <para><b>What makes these able to FAIL.</b> Every copy here is landed through the REAL
/// <see cref="ModuleLandingService"/>, so the identity under test is the one a real landing
/// RECORDS, never a hand-built entry; the control asserts the landed DLL exists and that the entry
/// is enabled FIRST, so it can never pass because the store copy was skipped for some other
/// reason. Reverting the discriminator reds the control on
/// <c>EffectiveModule.Landed</c>: main emits the store copy there, with the image's entry demoted
/// to <see cref="EffectiveModule.BaselineEntry"/>.</para>
///
/// <para><b>And the three negatives are the boundary.</b> A Store-only module (the image ships no
/// copy) still loads whatever it was built against — there is nothing to prefer it to, and
/// declining would turn a working module into a missing one. A store copy with NO recorded
/// identity still wins, because an unrecorded identity is absence of evidence, not evidence of
/// difference (rule R2 of <c>Doc/Architecture/ModuleAdoptionPolicy</c> — the same reading the
/// landing service gives an unrecorded VERSION). And a store copy built against the identity the
/// platform reports still wins, which is the ordinary upgrade path and the whole point of
/// installing a module. Declining unconditionally passes the control and reds all three.</para>
/// </summary>
public class ModuleIdentityDiscriminatorTest : IDisposable
{
    private const string Plugin = "Acme.Widgets";
    private const string PackagePath = "Plugins/AcmeWidgets";

    /// <summary>The identity the booting platform reports — the same-day source build whose copy
    /// of the pack the image ships (<c>8c833e20</c> in the measurement).</summary>
    private const string LiveIdentity = "8c833e20c0de4e2f9a7b1d3c5e6f7a8b";

    /// <summary>The identity the 2026-08-27 store pack was built against (<c>a87ef06f</c>).</summary>
    private const string StoreIdentity = "a87ef06f11223344556677889900aabb";

    /// <summary>The image's own <c>Modules:Assemblies</c> line for the same module.</summary>
    private const string ImageBaseline = Plugin + ".dll";

    /// <summary>The platform version the boot runs at — held constant, because the FLOOR decides
    /// nothing here (#3648) and this suite is about the identity.</summary>
    private const string RunningVersion = "3.0.0-ci.0";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-identitydisc-" + Guid.NewGuid().ToString("N"));

    private readonly ModuleLandingService landing;

    public ModuleIdentityDiscriminatorTest()
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

    // ─────────────────────────────────────────────────────────────────── the control

    /// <summary>
    /// 🚨 THE CONTROL. A store copy built against another platform's framework, with the image
    /// shipping its own copy of the same module: the image's copy runs, and the store copy is
    /// reported DECLINED — naming both identities, because a decline nobody can read is
    /// indistinguishable from a module that was never installed.
    /// </summary>
    [Fact]
    public async Task AStoreCopyBuiltForAnotherPlatform_IsDeclined_AndTheImagesOwnCopyRuns()
    {
        await Land(StoreIdentity);

        // The preconditions the defect rests on — asserted, never assumed. If either of these
        // failed, the store copy would be out of the running for a reason that has nothing to do
        // with its identity, and the control below would pass having measured nothing.
        var record = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        record.FrameworkMvid.Should().Be(StoreIdentity,
            "the landing RECORDS the identity its producer stated — that record is the discriminator");
        record.Enabled.Should().BeTrue("a disabled entry is an uninstall, which is a different skip");
        ModuleActivationBoot.LandedModuleDllExists(root, record).Should().BeTrue(
            "the landed DLL EXISTS — which on main is the only gate, and is why this copy won");

        var skips = new List<(string Module, string Reason)>();
        var advisories = new List<(string Module, string Reason)>();
        var effective = Boot(LiveIdentity, [ImageBaseline],
            (m, r) => skips.Add((m, r)), (m, r) => advisories.Add((m, r)));

        var module = Assert.Single(effective);
        module.Entry.Should().Be(ImageBaseline,
            "the image's own Modules:Assemblies line is what boot hands the loader");
        module.Landed.Should().BeNull(
            "the store copy was built against another platform's framework, so the image's own "
            + "copy is preferred — a non-null Landed here IS the defect: the store copy overriding "
            + "the image's on the strength of its DLL existing");
        module.BaselineEntry.Should().BeNull(
            "nothing displaced the baseline, so there is no displaced entry to carry (#3735)");

        var skip = Assert.Single(skips);
        skip.Module.Should().Be(Plugin);
        skip.Reason.Should().Contain("declined",
            "the operator has to be able to tell a declined copy from one that was never installed");
        skip.Reason.Should().Contain(StoreIdentity, "the reason names what the store copy was built for");
        skip.Reason.Should().Contain(LiveIdentity, "and what this deployment runs");
        Assert.Empty(advisories);
    }

    // ───────────────────────────────────────────────────────────── the three negatives

    /// <summary>
    /// A Store-only module — the image ships no copy of it — loads whatever it was built against,
    /// exactly as it does today. There is nothing to prefer it TO: declining here would turn a
    /// working module into a missing one, which is strictly worse than running a copy whose
    /// identity does not match.
    /// </summary>
    [Fact]
    public async Task AStoreOnlyModule_BuiltForAnotherPlatform_StillLoads()
    {
        await Land(StoreIdentity);

        var skips = new List<(string Module, string Reason)>();
        var effective = Boot(LiveIdentity, baseline: [], onSkipped: (m, r) => skips.Add((m, r)));

        var module = Assert.Single(effective);
        module.Entry.Should().Be(Plugin + ".dll");
        module.Landed.Should().NotBeNull(
            "the image ships no copy of a Store-only module, so the store copy is the only copy");
        module.Landed!.FrameworkMvid.Should().Be(StoreIdentity);
        Assert.Empty(skips);
    }

    /// <summary>
    /// A store copy whose identity was never RECORDED still overrides the image's copy — rule R2:
    /// an unrecorded value is absence of evidence, not evidence of difference. Reading it as
    /// "different" would decline every module landed before identities were recorded at all, on a
    /// string.
    /// </summary>
    [Fact]
    public async Task AStoreCopyWithNoRecordedIdentity_StillOverridesTheImage()
    {
        await Land(frameworkMvid: null);

        var record = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        record.FrameworkMvid.Should().BeNullOrEmpty("this is the unrecorded case, not a mismatch");

        var skips = new List<(string Module, string Reason)>();
        var effective = Boot(LiveIdentity, [ImageBaseline], (m, r) => skips.Add((m, r)));

        var module = Assert.Single(effective);
        module.Landed.Should().NotBeNull("an unrecorded identity is not a difference");
        module.BaselineEntry.Should().Be(ImageBaseline,
            "the displaced baseline still travels with the override (#3735)");
        Assert.Empty(skips);
    }

    /// <summary>
    /// A store copy built against the identity the platform reports still overrides the image's
    /// copy — the ordinary upgrade path (#2548), and the whole reason installing a module does
    /// anything. The discriminator declines on a DIFFERENCE, never on being a store copy.
    /// </summary>
    [Fact]
    public async Task AStoreCopyBuiltForThisPlatform_StillOverridesTheImage()
    {
        await Land(LiveIdentity);

        var skips = new List<(string Module, string Reason)>();
        var effective = Boot(LiveIdentity, [ImageBaseline], (m, r) => skips.Add((m, r)));

        var module = Assert.Single(effective);
        module.Entry.Should().Be(Plugin + ".dll");
        module.Landed.Should().NotBeNull("this copy was built for the platform that is booting");
        module.Landed!.FrameworkMvid.Should().Be(LiveIdentity);
        module.BaselineEntry.Should().Be(ImageBaseline,
            "the displaced baseline travels with the override so the loader can still fall back (#3735)");
        Assert.Empty(skips);
    }

    // ─────────────────────────────────────────────── the seam is a seam, not a hidden default

    /// <summary>
    /// 🚨 A host compiled against the PREVIOUS platform binds the six-argument overload, which
    /// states no live identity — and gets today's behaviour, unchanged. An optional parameter is a
    /// compile-time default and not a binary one, which is why that overload is a real method
    /// (the same reason the five-argument one is, #3648); a discriminator that silently read the
    /// process's own identity instead would change what those hosts do at their next boot without
    /// anything in their diff.
    /// </summary>
    [Fact]
    public async Task TheSixArgumentOverload_StatesNoIdentity_AndDeclinesNothing()
    {
        await Land(StoreIdentity);

        var skips = new List<(string Module, string Reason)>();
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            [ImageBaseline],
            ModuleActivationSidecar.Read(root),
            floor => ModulePlatformFloor.DeclineReason(floor, RunningVersion),
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry),
            (m, r) => skips.Add((m, r)),
            onAdvisory: null);

        Assert.Single(effective).Landed.Should().NotBeNull(
            "no live identity was stated, so there is nothing to compare and nothing to decline");
        Assert.Empty(skips);
    }

    // ───────────────────────────────────────────────────────────────────────── harness

    /// <summary>Lands one copy of the plugin through the REAL landing service, so the recorded
    /// identity is the one a real landing writes.</summary>
    private async Task Land(string? frameworkMvid) =>
        await landing.LandModule(
                Plugin, [(Plugin + ".dll", RealAssemblyBytes)],
                frameworkMvid: frameworkMvid, packagePath: PackagePath, version: "1.0.0",
                minMeshVersion: null,
                staticAssets: [("wwwroot/build.txt",
                    System.Text.Encoding.UTF8.GetBytes(frameworkMvid ?? "(unrecorded)"))])
            .Timeout(TestTimeouts.Convergence).Await();

    /// <summary>
    /// One replica's boot, composed exactly as <c>MemexConfiguration.ConfigureMemexMesh</c>
    /// composes it — the record off disk, the production existence check, the production floor
    /// wording — with the framework identity the booting platform reports stated explicitly, which
    /// is the seam production fills from <c>PrebuiltAssemblySeeder.LiveFrameworkMvid</c>.
    /// </summary>
    private IReadOnlyList<EffectiveModule> Boot(
        string? liveFrameworkIdentity,
        IReadOnlyList<string>? baseline = null,
        Action<string, string>? onSkipped = null,
        Action<string, string>? onAdvisory = null) =>
        ModuleActivationBoot.ComputeEffectiveModuleEntries(
            baseline,
            ModuleActivationSidecar.Read(root),
            floor => ModulePlatformFloor.DeclineReason(floor, RunningVersion),
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry),
            onSkipped,
            onAdvisory,
            liveFrameworkIdentity);

    /// <summary>Real, loadable managed-assembly bytes — the landing measures the module's link
    /// requirements (#3538), so a byte stand-in would be refused as unreadable.</summary>
    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);
}
