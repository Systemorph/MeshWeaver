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

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A declared <c>minMeshVersion</c> floor is ADVISORY at every runtime decision point
/// (#3648)</b> — it never refuses, holds or skips; loadability is measured.
///
/// <para><b>The incident these pin — 2026-09-07.</b> memex-cloud ran <c>3.0.0-ci.8055</c>. Every
/// installed <c>Plugins/*</c> record carried an <c>rc</c> floor — <c>3.0.0-rc8</c> for most — and
/// the comparator ranks <c>ci &lt; rc &lt; clean</c>, so no <c>ci</c> build could ever satisfy
/// them. The self-updater declined all 11 candidate releases ("77 plugins required … every one
/// declined"), the boot union would have skipped every one of those modules had a candidate
/// rolled, and the reconcile answered <c>SkipPlatformBelowFloor</c> for every served bundle. Every
/// production portal sat on its morning build for the whole day, and every module concerned would
/// have LOADED — the measured link probe (#3552) had nothing against any of them.</para>
///
/// <para><b>What makes these able to FAIL.</b> The pair is the production pair — floor
/// <see cref="Floor"/> against running <see cref="Running"/> — and the first test asserts that the
/// comparator still ranks them the old way, so a comparator "fix" that made the pair satisfied
/// would red that test rather than let the others pass vacuously. Every other test then asks the
/// exact decision point that used to refuse, hold or skip on that pair, and expects it to proceed
/// — with the floor WORDED into its reason, log or status row, since an advisory that is not
/// surfaced is indistinguishable from a floor that was never read.</para>
///
/// <para>The landing-service tests run against the REAL running version (the comparison there is
/// bound to <see cref="ModulePlatformFloor.RunningVersion"/>), which CI stamps as a clean
/// <c>3.0.0</c> — so they use a floor no build of this line satisfies, and assert that
/// precondition first for the same reason.</para>
/// </summary>
public class ModuleFloorAdvisoryTest : IDisposable
{
    /// <summary>The floor every rc-line module declared on 2026-09-07.</summary>
    private const string Floor = "3.0.0-rc8";

    /// <summary>The build memex-cloud ran that day — the one no rc floor could be satisfied by.</summary>
    private const string Running = "3.0.0-ci.8055";

    private const string Module = "MeshWeaver.Graph.Views";
    private const string PackagePath = "Plugins/DefaultViews";

    /// <summary>The production wording of the floor, bound to the deadlock's running version.</summary>
    private static string? Gate(string? floor) => ModulePlatformFloor.DeclineReason(floor, Running);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-flooradvisory-" + Guid.NewGuid().ToString("N"));

    private readonly ModuleLandingService landing;

    /// <summary>Creates the per-test deployment root and the REAL landing service over it.</summary>
    public ModuleFloorAdvisoryTest()
    {
        Directory.CreateDirectory(root);
        landing = new ModuleLandingService(baseDirectory: root);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    // ───────────────────────────────────────────── the pair, and why it is the pair

    /// <summary>
    /// 🚨 The precondition every other test rests on: the comparator still says <c>3.0.0-rc8</c>
    /// is NOT satisfied by <c>3.0.0-ci.8055</c>. That is the pack-time lint's rule (pinned against
    /// the script by <c>ModulePlatformFloorScriptParityTest</c>) and it is unchanged — what #3648
    /// changes is that no runtime decision reads the answer as a gate. If this ever flips, the
    /// tests below stop testing anything and must be re-pointed at a pair that does not satisfy.
    /// </summary>
    [Fact]
    public void TheDeadlockPair_IsStillAnUnsatisfiedFloor_ByTheComparator()
    {
        var advisory = Gate(Floor);

        Assert.NotNull(advisory);
        Assert.Contains(Floor, advisory);
        Assert.Contains(Running, advisory);
        Assert.Contains("advisory", advisory);
    }

    // ───────────────────────────────────────────── decision point 1: the reconcile

    /// <summary>
    /// <c>ModuleUpdateDecision.Decide</c> used to answer <c>SkipPlatformBelowFloor</c> here — step 2
    /// of the decision — and that is the verdict the reconcile logged for every module on
    /// 2026-09-07. A served bundle whose floor exceeds the platform now proceeds to LAND, with the
    /// floor worded into the reason, and the landing's link probe decides.
    /// </summary>
    [Fact]
    public void TheReconcile_LandsABundleWhoseFloorExceedsThePlatform()
    {
        var neverLanded = ModuleUpdateDecision.Decide(
            "1.2.0", Floor, Gate, landed: null, policyDecline: null, _ => true, "s-new");
        var upgrade = ModuleUpdateDecision.Decide(
            "1.2.0", Floor, Gate, Landed("1.1.0", "s-old"), policyDecline: null, _ => true, "s-new");

        Assert.Equal(ModuleUpdateAction.Land, neverLanded.Action);
        Assert.Equal(ModuleUpdateAction.Land, upgrade.Action);
        Assert.NotEqual(ModuleUpdateAction.SkipPlatformBelowFloor, neverLanded.Action);
        // The claim rides the reason: an advisory nobody can read is a gate nobody removed.
        Assert.Contains(Floor, neverLanded.Reason);
        Assert.Contains(Running, neverLanded.Reason);
        Assert.Contains(Floor, upgrade.Reason);
    }

    /// <summary>The floor does not override the OTHER skips either — an up-to-date module stays
    /// up to date, an uninstalled one stays uninstalled; the floor merely stops being a step.</summary>
    [Fact]
    public void TheReconcile_KeepsEveryOtherVerdict_WhateverTheFloorSays()
    {
        Assert.Equal(ModuleUpdateAction.SkipUpToDate, ModuleUpdateDecision.Decide(
            "1.1.0", Floor, Gate, Landed("1.1.0", "s-old"), null, _ => true, "s-old").Action);
        Assert.Equal(ModuleUpdateAction.SkipUninstalled, ModuleUpdateDecision.Decide(
            "1.2.0", Floor, Gate, Landed("1.1.0", "s-old", enabled: false), null, _ => true, "s-new").Action);
        Assert.Equal(ModuleUpdateAction.SkipOlder, ModuleUpdateDecision.Decide(
            "1.0.0", Floor, Gate, Landed("1.1.0", "s-old"), null, _ => true, "s-old").Action);
        Assert.Equal(ModuleUpdateAction.SkipNoBundle, ModuleUpdateDecision.Decide(
            null, Floor, Gate, Landed("1.1.0", "s-old"), null, _ => true, null).Action);
    }

    // ───────────────────────────────────────────── decision point 2: the boot union

    /// <summary>
    /// <c>ModuleActivationBoot.ComputeEffectiveModuleEntries</c> used to SKIP an entry whose
    /// recorded floor the running platform did not satisfy — "the platform rolled back below the
    /// module's requirement" was the story, and on 2026-09-07 it would have skipped every store
    /// module on every ci-built portal. The entry is handed to the loader now; the claim goes out
    /// on the advisory channel, not the skip channel, and the link probe in
    /// <c>MeshBuilder.InstallAssemblies</c> decides on the bytes.
    /// </summary>
    [Fact]
    public void TheBootUnion_HandsAFlooredEntryToTheLoader_AndAnnouncesTheClaim()
    {
        var skips = new List<(string Module, string Reason)>();
        var advisories = new List<(string Module, string Reason)>();

        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            baselineEntries: [],
            new ModuleActivationList { Entries = [Entry(Floor)] },
            Gate,
            _ => true,
            (m, r) => skips.Add((m, r)),
            (m, r) => advisories.Add((m, r)));

        var module = Assert.Single(effective);
        Assert.Equal(Module + ".dll", module.Entry);
        Assert.Same(Floor, module.Landed?.MinMeshVersion);
        Assert.Empty(skips);
        var advisory = Assert.Single(advisories);
        Assert.Equal(Module, advisory.Module);
        Assert.Contains(Floor, advisory.Reason);
        Assert.Contains(Running, advisory.Reason);
    }

    /// <summary>The one skip that stays: a landed DLL that is gone. The floor is not consulted for
    /// a skipped entry — its line already names it, and an advisory about bytes that are not there
    /// would be noise.</summary>
    [Fact]
    public void TheBootUnion_StillSkipsAMissingDll_AndSaysNothingAboutItsFloor()
    {
        var skips = new List<(string Module, string Reason)>();
        var advisories = new List<(string Module, string Reason)>();

        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            [], new ModuleActivationList { Entries = [Entry(Floor)] }, Gate, _ => false,
            (m, r) => skips.Add((m, r)), (m, r) => advisories.Add((m, r)));

        Assert.Empty(effective);
        Assert.Contains("does not exist", Assert.Single(skips).Reason);
        Assert.Empty(advisories);
    }

    // ───────────────────────────────────────────── decision point 3: the release gate

    /// <summary>
    /// <c>ReleaseAvailability.IsUpdatable</c> used to answer <c>ModuleFloorExceedsTarget</c> — a
    /// BLOCKER — for every package whose floor the target did not satisfy, and that is the exact
    /// verdict behind "77 plugins required … every one declined". The floor is an ADVISORY beside
    /// the verdict now: <c>IsUpdatable</c> is true, <c>Blockers</c> is empty, and the sentence is
    /// on <c>Advisories</c> so the log can still say what the modules claim.
    /// </summary>
    [Fact]
    public void TheReleaseGate_ReportsAFlooredModuleAsAnAdvisory_NeverAsABlocker()
    {
        var target = new ReleaseTarget(Running, "s8055");
        var verdict = ReleaseAvailability.IsUpdatable(
            target,
            [
                new RequiredPackage("DefaultViews", "DefaultViews", Floor, HasContent: true),
                new RequiredPackage("Speech", "Speech", Floor, HasContent: false),
                new RequiredPackage("Store", "Store", null, HasContent: true),
            ],
            ReleaseArtifacts.Of(["DefaultViews.zip", "Store.zip"]));

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.Null(verdict.HoldReason);
        Assert.Empty(verdict.Blockers);
        Assert.DoesNotContain(verdict.Packages, p => p.Kind == PackageAvailabilityKind.ModuleFloorExceedsTarget);
        Assert.All(verdict.Packages, p => Assert.Equal(PackageAvailabilityKind.Available, p.Kind));

        Assert.Equal(2, verdict.Advisories.Length);
        Assert.All(verdict.Advisories, a =>
        {
            Assert.Contains(Floor, a);
            Assert.Contains(Running, a);
        });
        Assert.Contains(verdict.Advisories, a => a.StartsWith("DefaultViews:", StringComparison.Ordinal));
        Assert.Contains(verdict.Advisories, a => a.StartsWith("Speech:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A missing bake is a COST beside the floor advisory since #3651 — the two answer different
    /// questions and neither hides the other, and neither holds. Under
    /// <c>Modules:RequirePrebuilt</c> the bake is still the hold it was, and the floor still rides
    /// beside it as an advisory.
    /// </summary>
    [Fact]
    public void TheReleaseGate_ReportsAMissingBakeAsACost_WithTheFloorAdvisoryBesideIt()
    {
        var verdict = ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(Running, "s8055"),
            [new RequiredPackage("DefaultViews", "DefaultViews", Floor, HasContent: true)],
            ReleaseArtifacts.Of([]));

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.Empty(verdict.Blockers);
        Assert.Equal(["DefaultViews"], verdict.BootCompiles);
        Assert.Equal(2, verdict.Advisories.Length);
        Assert.Contains(verdict.Advisories, a => a.Contains("would recompile at boot", StringComparison.Ordinal));
        Assert.Contains(verdict.Advisories, a => a.Contains(Floor, StringComparison.Ordinal));

        var strict = ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(Running, "s8055"),
            [new RequiredPackage("DefaultViews", "DefaultViews", Floor, HasContent: true)],
            ReleaseArtifacts.Of([]),
            new ReleaseGatePolicy(RequirePrebuilt: true));

        Assert.False(strict.IsUpdatable);
        Assert.Equal(PackageAvailabilityKind.ContentBakeMissing, Assert.Single(strict.Blockers).Kind);
        Assert.Contains(Floor, Assert.Single(strict.Advisories));
    }

    // ───────────────────────────────────────────── decision point 4: the required-module probe

    /// <summary>
    /// <c>RequiredModuleStatus.Classify</c> used to report a landed entry whose floor exceeded the
    /// platform as "HELD above this platform … a platform update satisfies the floor and that
    /// boot loads it". Boot loads it now, so the honest sentence is the ordinary one — landed,
    /// awaiting a restart — with the claim appended, never a hold.
    /// </summary>
    [Fact]
    public void TheRequiredModuleProbe_ReportsAFlooredLandedModuleAsAwaitingRestart_NotHeld()
    {
        var verdicts = RequiredModuleStatus.Classify(
            requiredEntries: [Module + ".dll"],
            baselineEntries: [],
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvesFromDeployment: _ => false,
            activation: new ModuleActivationList { Entries = [Entry(Floor)] },
            landedDllExists: _ => true,
            platformGate: Gate,
            incompatibleModules: []);

        var expected = Assert.Single(RequiredModuleStatus.ExpectedLater(verdicts));
        Assert.Equal(Module, expected.Name);
        Assert.Contains("a restart activates it", expected.Reason);
        Assert.DoesNotContain("HELD", expected.Reason);
        Assert.Contains(Floor, expected.Reason);
        Assert.Contains(Running, expected.Reason);
        Assert.Empty(RequiredModuleStatus.Absent(verdicts));
        Assert.Empty(RequiredModuleStatus.Incompatible(verdicts));
    }

    // ───────────────────────────────────────────── decision point 5: the activation report

    /// <summary>
    /// <c>ModuleActivationStatus.NotYetLoaded</c> used to EXCLUDE an entry whose floor the gate
    /// refused — "a restart cannot activate a held module" — which was true only because boot
    /// skipped it. It is pending now, with the claim on the record.
    /// </summary>
    [Fact]
    public void TheActivationReport_CountsAFlooredEntryAsPending_WithTheAdvisory()
    {
        var list = new ModuleActivationList { Entries = [Entry(Floor)] };
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var pending = Assert.Single(ModuleActivationStatus.NotYetLoaded(list, loaded, Gate, _ => true));
        Assert.Equal(Module, pending.Name);
        Assert.Contains(Floor, pending.Advisory);
        Assert.Contains(Running, pending.Advisory);

        // And a floored entry whose bytes are gone is UNRESOLVABLE, exactly like one without a
        // floor — the floor moves nothing between the two buckets.
        Assert.Empty(ModuleActivationStatus.NotYetLoaded(list, loaded, Gate, _ => false));
        Assert.Equal(Module, Assert.Single(
            ModuleActivationStatus.Unresolvable(list, loaded, Gate, _ => false)).Name);
    }

    /// <summary>An entry whose floor the platform satisfies carries no advisory — the record says
    /// nothing when there is nothing to say.</summary>
    [Fact]
    public void ASatisfiedFloor_CarriesNoAdvisory()
    {
        var pending = Assert.Single(ModuleActivationStatus.NotYetLoaded(
            new ModuleActivationList { Entries = [Entry("3.0.0-ci.100")] },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), Gate, _ => true));

        Assert.Null(pending.Advisory);
    }

    // ───────────────────────────────────────────── decision points 6 and 7: the landing itself

    /// <summary>
    /// <c>ModuleLandingService.LandCore</c> used to REFUSE — "Module 'X' refused: the module
    /// requires platform … or newer but this deployment runs …" — before the link probe even ran.
    /// The floor is recorded and the bytes are measured: a module that links LANDS whatever its
    /// author wrote, restart raised, floor on the entry for the status row to say.
    /// </summary>
    [Fact]
    public async Task TheLanding_LandsAModuleWhoseFloorExceedsThePlatform_AndRecordsTheClaim()
    {
        var floor = FloorAboveTheRunningPlatform();

        await landing.LandModule(
                Module, [(Module + ".dll", LinkableBytes)],
                frameworkMvid: "s-built-against", packagePath: PackagePath, version: "1.2.0",
                minMeshVersion: floor)
            .Timeout(TestTimeouts.Convergence).Await();

        var list = ModuleActivationSidecar.Read(root);
        var entry = Assert.Single(list.Entries);
        Assert.Equal(Module, entry.Name);
        Assert.True(entry.Enabled);
        Assert.Equal(floor, entry.MinMeshVersion);
        Assert.Equal("1.2.0", entry.Version);
        Assert.True(list.PendingRestart, "an unheld landing loads at the next restart");
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, entry));
    }

    /// <summary>
    /// The SHELF path (the registry's publish endpoint) used to HOLD the same landing — bytes on
    /// the shelf, no restart, boot skipping the entry until a platform update. Bytes that link are
    /// not held by a string any more; only what the probe cannot load is
    /// (<see cref="ModulePlatformLinkTest.AnUnloadableModule_IsShelvedRatherThanRefused_OnThePublishPath"/>).
    /// </summary>
    [Fact]
    public async Task TheShelf_LandsAModuleWhoseFloorExceedsThePlatform_Unheld()
    {
        var floor = FloorAboveTheRunningPlatform();

        var outcome = await landing.ShelveModule(
                Module, [(Module + ".dll", LinkableBytes)],
                frameworkMvid: "s-built-against", packagePath: PackagePath, version: "1.2.0",
                minMeshVersion: floor)
            .Timeout(TestTimeouts.Convergence).Await();

        Assert.False(outcome.Held, outcome.HoldReason);
        Assert.Null(outcome.HoldReason);
        Assert.True(ModuleActivationSidecar.Read(root).PendingRestart);
    }

    /// <summary>
    /// The whole report, read off the disk the landing wrote: the floored module is PENDING (a
    /// restart activates it), and it is listed as an advisory with both versions — for the health
    /// payload and for the package card, which looks it up by install-record path.
    /// </summary>
    [Fact]
    public async Task TheActivationReportOnDisk_ListsTheFlooredModuleAsPendingAndAsAnAdvisory()
    {
        var floor = FloorAboveTheRunningPlatform();
        await landing.LandModule(
                Module, [(Module + ".dll", LinkableBytes)],
                packagePath: PackagePath, version: "1.2.0", minMeshVersion: floor)
            .Timeout(TestTimeouts.Convergence).Await();

        var report = new PendingModuleActivations(root).Read(
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            loadedModuleGenerations: ImmutableDictionary<string, string>.Empty);

        Assert.False(report.IsUndetermined, report.UndeterminedReason);
        Assert.True(report.HasPending, "boot loads the entry, so a restart genuinely activates it");
        Assert.Equal(Module, Assert.Single(report.Pending).Name);
        Assert.True(report.IsPendingForPackage(PackagePath));
        Assert.False(report.HasQuarantined, "a floor is not a refusal — only the link probe quarantines");
        Assert.Empty(report.Unresolvable);

        Assert.True(report.HasFloorAdvisories);
        var advisory = Assert.Single(report.FloorAdvisories);
        Assert.Equal(Module, advisory.Name);
        Assert.Equal(floor, advisory.DeclaredFloor);
        Assert.Equal(ModulePlatformFloor.RunningVersion, advisory.RunningVersion);
        Assert.Same(advisory, report.FloorAdvisoryForPackage(PackagePath));
        Assert.Null(report.FloorAdvisoryForPackage(null));
        Assert.Contains("advisory only", report.Describe());
        Assert.Contains(floor, report.Describe());
    }

    // ───────────────────────────────────────────── harness

    private static ModuleActivationEntry Entry(string? floor) => new()
    {
        Name = Module,
        PackagePath = PackagePath,
        Version = "1.2.0",
        MinMeshVersion = floor,
        FrameworkMvid = "s-built-against",
        Enabled = true,
        Directory = Module + "@advisory1",
    };

    private static ModuleActivationEntry Landed(string version, string frameworkMvid, bool enabled = true) =>
        Entry(Floor) with { Version = version, FrameworkMvid = frameworkMvid, Enabled = enabled };

    /// <summary>
    /// A floor the REAL running platform does not satisfy — asserted, not assumed: the landing
    /// tests compare against <see cref="ModulePlatformFloor.RunningVersion"/>, which CI stamps as
    /// a clean <c>3.0.0</c> (above every <c>rc</c>), so the production pair cannot serve there. A
    /// floor nothing on this line reaches does, and the assertion is what keeps these tests from
    /// passing because the floor happened to be satisfied.
    /// </summary>
    private static string FloorAboveTheRunningPlatform()
    {
        const string floor = "999.0.0";
        Assert.NotNull(ModulePlatformFloor.RunningVersion);
        Assert.NotNull(ModulePlatformFloor.DeclineReason(floor));
        return floor;
    }

    /// <summary>
    /// Real, loadable managed-assembly bytes — the landing MEASURES the module's link requirements
    /// against this platform (#3538), so a byte stand-in would be refused as unreadable and these
    /// tests would be about that gate instead of the floor. The packaging assembly links against
    /// nothing this process lacks; the same choice <c>ModuleSetConvergenceTest</c> makes.
    /// </summary>
    private static byte[] LinkableBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);
}
