using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A platform roll is held only by a module that provably cannot load on the target (#3651;
/// maintainer rule of 2026-09-07, <c>Doc/Architecture/ModuleAdoptionPolicy</c>).</b>
///
/// <para>On 2026-09-07 every production portal sat on its morning build all day: the declared
/// floors (a) declined every candidate (fixed by #3648), and a missing bake for the new identity
/// (b) would have held it again the moment (a) was lifted — while a boot compile of those courses
/// succeeds on every PR and every candidate would have loaded. These pin the rule that replaces
/// both: the ONLY hold on the module lane is MEASURED — a landed generation, for which the target
/// publishes no build, linked against the target's published type surface
/// (<see cref="ModulePlatformSurface.PublishedFileName"/>) and found <c>Unlinkable</c>. A missing
/// bake is reported as a cost. A link check that could not be made is reported and decides
/// nothing. <see cref="RollSelection"/> walks newest-first and stops at the first release with no
/// unloadable module.</para>
///
/// <para>Nothing is mocked: the modules are REAL assemblies compiled with Roslyn, landed in a
/// generation directory of the shape boot loads from; the target's surface is the running
/// process's own document with one type removed (an older platform, verbatim the memex-cloud
/// shape of #3538); the observation is <see cref="PublishedBundleCatalogue.Read"/> over a root on
/// disk plus <see cref="ModuleLinkObservation.Measure"/>; the rule is
/// <see cref="ReleaseAvailability.IsUpdatable(ReleaseTarget, IEnumerable{RequiredPackage}, ReleaseArtifacts, ReleaseGatePolicy)"/>.</para>
/// </summary>
public class ReleaseLinkGateTest : IDisposable
{
    private const string ContractAssembly = "MeshWeaver.Mesh.Contract";
    private const string Version = "3.0.0-ci.8100";
    private const string Identity = "s3651target000000000000000000000";
    private const string OlderVersion = "3.0.0-ci.8090";
    private const string OlderIdentity = "s3651older0000000000000000000000";

    /// <summary>The module that binds <c>MeshNode</c> — loadable here, unloadable on a platform
    /// whose contract does not carry that type.</summary>
    private const string ViewPack = "MeshWeaver.Test.LinkGateViewPack";

    /// <summary>A module binding nothing the target lacks — the positive control.</summary>
    private const string PlainPack = "MeshWeaver.Test.LinkGatePlainPack";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-3651-" + Guid.NewGuid().ToString("N"));

    private readonly string modules;

    public ReleaseLinkGateTest()
    {
        Directory.CreateDirectory(root);
        modules = Path.Combine(root, "modules");
        Directory.CreateDirectory(modules);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        GC.SuppressFinalize(this);
    }

    // ── the hold: measured, named ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE HOLD. The environment has landed a view pack built against THIS platform; the target
    /// publishes no build of it and its surface does not carry <c>MeshWeaver.Mesh.MeshNode</c>.
    /// The roll is held, the hold names the module and the missing type, and it is a definite
    /// incompatibility — never Indeterminate.
    /// </summary>
    [Fact]
    public void AModuleUnlinkableAgainstTheTargetSurface_HoldsTheRoll_NamingTheModule()
    {
        var published = PublishedRoot(Version, Identity, surface: OlderPlatformSurface(Identity));
        var landed = Land(ViewPack, ModuleBindingMeshNode(ViewPack));

        var verdict = Judge(published, Version,
        [
            new RequiredPackage("Widget", "Widget"),
            new RequiredPackage("Views", "Views", HasContent: false)
                { ModuleName = ViewPack, LandedModulePath = landed },
        ]);

        Assert.False(verdict.IsUpdatable);
        Assert.False(verdict.IsIndeterminate,
            "an unlinkable module is a MEASURED incompatibility, not an unreadability");
        var blocker = Assert.Single(verdict.Blockers);
        Assert.Equal("Views", blocker.Package);
        Assert.Equal(PackageAvailabilityKind.ModuleUnloadable, blocker.Kind);
        Assert.Contains(ViewPack, blocker.Reason!, StringComparison.Ordinal);
        Assert.Contains(typeof(MeshNode).FullName!, blocker.Reason!, StringComparison.Ordinal);
        Assert.Contains(Version, blocker.Reason!, StringComparison.Ordinal);
        Assert.Contains(ViewPack, verdict.HoldReason!, StringComparison.Ordinal);
        // The content package beside it is fine and says so — the blast radius is one module.
        Assert.Equal(PackageAvailabilityKind.Available,
            Assert.Single(verdict.Packages, p => p.Package == "Widget").Kind);
        // And the measurement is on the observation, denominator and all.
        var link = Assert.Contains("Views", verdict.IsUpdatable ? [] : Links(published, Version, landed));
        Assert.Equal(ModuleLinkState.Unlinkable, link.State);
        Assert.True(link.CheckedTypeReferences > 0, link.Report());
    }

    /// <summary>The positive control: a landed module that links against the target clears, with
    /// nothing on the advisories about it.</summary>
    [Fact]
    public void ALandedModuleThatLinksAgainstTheTarget_Clears()
    {
        var published = PublishedRoot(Version, Identity, surface: OlderPlatformSurface(Identity));
        var landed = Land(PlainPack, ModuleBindingNothingTheTargetLacks(PlainPack));

        var verdict = Judge(published, Version,
        [
            new RequiredPackage("Widget", "Widget"),
            new RequiredPackage("Plain", "Plain", HasContent: false)
                { ModuleName = PlainPack, LandedModulePath = landed },
        ]);

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.Empty(verdict.Blockers);
        Assert.Empty(verdict.Advisories);
        Assert.All(verdict.Packages, p => Assert.Equal(PackageAvailabilityKind.Available, p.Kind));
    }

    /// <summary>
    /// A module the TARGET publishes a build of will be adopted at the roll (#3650), so the landed
    /// generation — unloadable there or not — is not what keeps running and is not what decides.
    /// The published build's consistency is the sealed-set rule's business (#3175), not this one's.
    /// </summary>
    [Fact]
    public void AModuleWithABuildPublishedForTheTarget_IsNotHeldOnItsLandedGeneration()
    {
        var bytes = ModuleBindingMeshNode(ViewPack);
        var published = PublishedRoot(Version, Identity,
            surface: OlderPlatformSurface(Identity), sealedModule: (ViewPack, bytes));
        var landed = Land(ViewPack, bytes);

        var verdict = Judge(published, Version,
        [
            new RequiredPackage("Views", "Views", HasContent: false)
                { ModuleName = ViewPack, LandedModulePath = landed },
        ]);

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.Empty(verdict.Advisories);
    }

    /// <summary>A module the environment has NOT landed cannot keep running across the roll, so
    /// there is nothing to measure and nothing to hold on.
    ///
    /// <para>🚨 This case DOES speak now, and the distinction is the point (#3706). The LINK lane is
    /// still silent — there are no bytes to link, which is what "is not measured" means and what
    /// this test is named for. What speaks is the ORPHAN advisory: the target's sealed set does not
    /// carry the module and nothing is landed, so no publisher produces it. That is a statement
    /// about the INSTALL RECORD, not about the link, and it neither measures nor holds. The
    /// assertion moved from "says nothing at all" to "says nothing about the link", because the
    /// blanket form was written when a link advisory was the only one this lane could emit, and
    /// blanket emptiness would now re-assert the silence #3706 was filed about.</para></summary>
    [Fact]
    public void AModuleWithNoLandedGeneration_IsNotMeasured()
    {
        var published = PublishedRoot(Version, Identity, surface: OlderPlatformSurface(Identity));

        var verdict = Judge(published, Version,
            [new RequiredPackage("Views", "Views", HasContent: false) { ModuleName = ViewPack }]);

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.DoesNotContain(verdict.Advisories,
            a => a.Contains("could not be determined", StringComparison.Ordinal)
                 || a.Contains("cannot load", StringComparison.Ordinal));
        Assert.Contains(
            verdict.Advisories,
            a => a.Contains("no publisher produces it", StringComparison.Ordinal));
    }

    // ── Indeterminate is REPORTED — neither clearance nor a hold ───────────────────────────────

    /// <summary>
    /// 🚨 A release with NO published surface — every publication sealed before #3651 — cannot
    /// be linked against. That is Indeterminate for the link check ONLY: reported on the
    /// advisories, naming the module and why, and the roll neither clears on it (the advisory is
    /// there) nor holds on it (holding would freeze the fleet exactly as the floors did; the
    /// boot-time probe and the keep-the-previous-generation fallback are the safety net).
    /// </summary>
    [Fact]
    public void AReleaseWithNoPublishedSurface_IsIndeterminate_ReportedNeitherClearanceNorHold()
    {
        var published = PublishedRoot(Version, Identity, surface: null);
        var landed = Land(ViewPack, ModuleBindingMeshNode(ViewPack));

        var observation = PublishedBundleCatalogue.Read(published, Version);
        Assert.Null(observation.Artifacts.PlatformSurface);
        Assert.NotNull(observation.Artifacts.PlatformSurfaceDetail);
        Assert.Contains(ModulePlatformSurface.PublishedFileName, observation.Artifacts.PlatformSurfaceDetail);

        var verdict = Judge(published, Version,
        [
            new RequiredPackage("Views", "Views", HasContent: false)
                { ModuleName = ViewPack, LandedModulePath = landed },
        ]);

        Assert.True(verdict.IsUpdatable, "an unmeasurable link is not a hold");
        Assert.False(verdict.IsIndeterminate, "…and not the store-unreadable Indeterminate that holds");
        Assert.Empty(verdict.Blockers);
        var advisory = Assert.Single(verdict.Advisories);
        Assert.StartsWith("Views:", advisory, StringComparison.Ordinal);
        Assert.Contains(ViewPack, advisory, StringComparison.Ordinal);
        Assert.Contains("could not be determined", advisory, StringComparison.Ordinal);
        Assert.Contains(ModulePlatformSurface.PublishedFileName, advisory, StringComparison.Ordinal);
        Assert.Contains("not a hold", advisory, StringComparison.Ordinal);
    }

    /// <summary>A published document that does not PARSE is the same answer — reported, with the
    /// parse failure named, never an empty surface that would refuse every module.</summary>
    [Fact]
    public void AnUnreadablePublishedSurface_IsIndeterminate_NeverAnEmptySurface()
    {
        var published = PublishedRoot(Version, Identity, surface: "{ this is not a surface");
        var landed = Land(ViewPack, ModuleBindingMeshNode(ViewPack));

        var observation = PublishedBundleCatalogue.Read(published, Version);
        Assert.Null(observation.Artifacts.PlatformSurface);
        Assert.Contains("could not be read", observation.Artifacts.PlatformSurfaceDetail!);

        var verdict = Judge(published, Version,
        [
            new RequiredPackage("Views", "Views", HasContent: false)
                { ModuleName = ViewPack, LandedModulePath = landed },
        ]);

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.Empty(verdict.Blockers);
        Assert.Contains(verdict.Advisories, a => a.Contains("could not be read", StringComparison.Ordinal));
    }

    /// <summary>Landed bytes that cannot be read are the probe's own Indeterminate — reported
    /// with the probe's detail, not a hold (a boot that cannot read them does not load them).</summary>
    [Fact]
    public void UnreadableLandedBytes_AreReported_NotHeld()
    {
        var published = PublishedRoot(Version, Identity, surface: OlderPlatformSurface(Identity));
        var landed = Land("MeshWeaver.Test.Garbage", [0x4D, 0x5A, 0x90]);

        var verdict = Judge(published, Version,
        [
            new RequiredPackage("Garbage", "Garbage", HasContent: false)
                { ModuleName = "MeshWeaver.Test.Garbage", LandedModulePath = landed },
        ]);

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        var advisory = Assert.Single(verdict.Advisories);
        Assert.Contains("could not be determined", advisory, StringComparison.Ordinal);
        Assert.Contains("BadImageFormatException", advisory, StringComparison.Ordinal);
    }

    // ── the missing bake: a cost, not a hold ───────────────────────────────────────────────────

    /// <summary>
    /// 🚨 (b) of the 2026-09-07 hold. A content package with no sealed bake for the target is
    /// NAMED ("would recompile at boot: …") and the roll proceeds: the compile is what every PR of
    /// that content already proves green. Only a <c>Modules:RequirePrebuilt</c> instance — where
    /// the seeder refuses the compile and the type would park — keeps it as the hold it used to
    /// be everywhere, and the same fixture shows both arms.
    /// </summary>
    [Fact]
    public void AMissingContentBake_IsACostTheVerdictNames_AndAHoldOnlyUnderRequirePrebuilt()
    {
        var published = PublishedRoot(Version, Identity, surface: OlderPlatformSurface(Identity));
        RequiredPackage[] required =
        [
            new("Widget", "Widget"),
            new("Education", "Education"),
            new("Crm", "Crm"),
        ];

        var verdict = Judge(published, Version, required);

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.Empty(verdict.Blockers);
        Assert.Equal(["Education", "Crm"], verdict.BootCompiles);
        var cost = Assert.Single(verdict.Advisories);
        Assert.Contains("would recompile at boot", cost, StringComparison.Ordinal);
        Assert.Contains("Education, Crm", cost, StringComparison.Ordinal);
        Assert.Contains(Identity, cost, StringComparison.Ordinal);
        // The kind still SAYS what it is — the tab and the API read it — only its weight changed.
        var education = Assert.Single(verdict.Packages, p => p.Package == "Education");
        Assert.Equal(PackageAvailabilityKind.ContentBakeMissing, education.Kind);
        Assert.True(education.IsAdvisory);
        Assert.True(education.IsAvailable);
        Assert.Contains("recompile it at boot", education.Reason!, StringComparison.Ordinal);

        // The strict arm: the same observation, under the opt-in policy, is the hold it was.
        var strict = Judge(published, Version, required, new ReleaseGatePolicy(RequirePrebuilt: true));
        Assert.False(strict.IsUpdatable);
        Assert.Equal(
            ["Education", "Crm"],
            strict.Blockers.Select(b => b.Package).ToArray());
        Assert.All(strict.Blockers, b =>
        {
            Assert.Equal(PackageAvailabilityKind.ContentBakeMissing, b.Kind);
            Assert.False(b.IsAdvisory);
            Assert.Contains(PrebuiltAssemblySeederKey, b.Reason!, StringComparison.Ordinal);
        });
        Assert.Empty(strict.BootCompiles);
    }

    private const string PrebuiltAssemblySeederKey = "Modules:RequirePrebuilt";

    /// <summary>The unloadable module is not shadowed by a content advisory on the SAME package —
    /// a mixed package (content plus a module, the SocialMedia shape) holds on its module.</summary>
    [Fact]
    public void AMixedPackage_HoldsOnItsUnloadableModule_NotOnItsMissingBake()
    {
        var published = PublishedRoot(Version, Identity, surface: OlderPlatformSurface(Identity));
        var landed = Land(ViewPack, ModuleBindingMeshNode(ViewPack));

        var verdict = Judge(published, Version,
        [
            new RequiredPackage("SocialMedia", "SocialMedia", HasContent: true)
                { ModuleName = ViewPack, LandedModulePath = landed },
        ]);

        Assert.False(verdict.IsUpdatable);
        Assert.Equal(PackageAvailabilityKind.ModuleUnloadable, Assert.Single(verdict.Blockers).Kind);
        Assert.Empty(verdict.BootCompiles);
    }

    // ── the walk ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 <see cref="RollSelection"/> walks newest-first and stops at the first release with no
    /// UNLOADABLE module. The newest publishes a surface the landed view pack cannot link against
    /// (and, for good measure, no bake for the content package); the one below it publishes a
    /// surface it links against. The newest is declined NAMING the module; the older is selected,
    /// and the cost it carries is said on the outcome.
    /// </summary>
    [Fact]
    public async Task TheWalkStopsAtTheFirstReleaseWithNoUnloadableModule()
    {
        var published = PublishedRoot(Version, Identity, surface: OlderPlatformSurface(Identity));
        AddRelease(published, OlderVersion, OlderIdentity, surface: ThisPlatformSurface(OlderIdentity), bundles: []);
        var landed = Land(ViewPack, ModuleBindingMeshNode(ViewPack));
        RequiredPackage[] required =
        [
            new("Widget", "Widget"),
            new RequiredPackage("Views", "Views", HasContent: false)
                { ModuleName = ViewPack, LandedModulePath = landed },
        ];
        var asked = ImmutableList.CreateBuilder<string>();

        var outcome = await RollSelection.Select(
                new RollSelectionInputs(
                    "memex", "3.0.0-ci.8009", [Version, OlderVersion],
                    PluginInventory.Of(required, "the install records")),
                version =>
                {
                    asked.Add(version);
                    return Observable.Return(Judge(published, version, required));
                })
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.Update, outcome.Kind);
        Assert.Equal(OlderVersion, outcome.SelectedVersion);
        Assert.Equal([Version, OlderVersion], asked.ToImmutable());
        var declined = Assert.Single(outcome.Declined);
        Assert.Equal(Version, declined.Version);
        Assert.Contains(ViewPack, declined.Reason, StringComparison.Ordinal);
        Assert.Equal(PackageAvailabilityKind.ModuleUnloadable, Assert.Single(declined.Blockers).Kind);
        // The older release seals no Widget bake: a COST the outcome names, not a reason to walk on.
        Assert.Equal(["Widget"], outcome.BootCompiles);
        Assert.Contains("would recompile at boot: Widget", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains(ViewPack, outcome.Summary, StringComparison.Ordinal);
    }

    /// <summary>"No complete release" — every candidate's surface lacks the type — stays put and
    /// NAMES the module. Not Indeterminate: the selector looked, and the answer is about the
    /// publications.</summary>
    [Fact]
    public async Task WhenEveryCandidateCannotLoadTheModule_NothingIsSelected_AndTheModuleIsNamed()
    {
        var published = PublishedRoot(Version, Identity, surface: OlderPlatformSurface(Identity));
        AddRelease(published, OlderVersion, OlderIdentity, surface: OlderPlatformSurface(OlderIdentity), bundles: ["Widget"]);
        var landed = Land(ViewPack, ModuleBindingMeshNode(ViewPack));
        RequiredPackage[] required =
        [
            new("Widget", "Widget"),
            new RequiredPackage("Views", "Views", HasContent: false)
                { ModuleName = ViewPack, LandedModulePath = landed },
        ];

        var outcome = await RollSelection.Select(
                new RollSelectionInputs(
                    "memex", "3.0.0-ci.8009", [Version, OlderVersion],
                    PluginInventory.Of(required, "the install records")),
                version => Observable.Return(Judge(published, version, required)))
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.NoCompleteRelease, outcome.Kind);
        Assert.Null(outcome.SelectedVersion);
        Assert.False(outcome.IsIndeterminate);
        Assert.Equal(2, outcome.Declined.Length);
        Assert.All(outcome.Declined, d =>
            Assert.Equal(PackageAvailabilityKind.ModuleUnloadable, Assert.Single(d.Blockers).Kind));
        Assert.Contains(ViewPack, outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("3.0.0-ci.8009", outcome.Summary, StringComparison.Ordinal);
    }

    /// <summary>A release whose surface is unpublished is walked PAST nothing and INTO nothing on
    /// that account: with no unloadable module measured, the newest is selected, and the outcome
    /// carries the unmeasured module as an advisory.</summary>
    [Fact]
    public async Task AReleaseWithNoSurface_IsSelectedWithTheUnmeasuredModuleReported()
    {
        var published = PublishedRoot(Version, Identity, surface: null);
        var landed = Land(ViewPack, ModuleBindingMeshNode(ViewPack));
        RequiredPackage[] required =
        [
            new("Widget", "Widget"),
            new RequiredPackage("Views", "Views", HasContent: false)
                { ModuleName = ViewPack, LandedModulePath = landed },
        ];

        var outcome = await RollSelection.Select(
                new RollSelectionInputs(
                    "memex", "3.0.0-ci.8009", [Version],
                    PluginInventory.Of(required, "the install records")),
                version => Observable.Return(Judge(published, version, required)))
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(RollSelectionKind.Update, outcome.Kind);
        Assert.Equal(Version, outcome.SelectedVersion);
        Assert.Contains(outcome.Advisories, a => a.Contains(ViewPack, StringComparison.Ordinal)
                                                 && a.Contains("could not be determined", StringComparison.Ordinal));
    }

    // ── driving the SHARED predicate ────────────────────────────────────────────────────────────

    /// <summary>The exact composition <c>ReleaseAvailabilityService.Judge</c> runs: read, measure,
    /// decide.</summary>
    private static UpdatabilityVerdict Judge(
        string published, string version, IEnumerable<RequiredPackage> required,
        ReleaseGatePolicy? policy = null)
    {
        var packages = required.ToImmutableArray();
        var observation = PublishedBundleCatalogue.Read(published, version);
        var measured = ModuleLinkObservation.Measure(observation.Artifacts, packages);
        return ReleaseAvailability.IsUpdatable(
            observation.Target, packages, measured, policy ?? ReleaseGatePolicy.Default);
    }

    private static ImmutableDictionary<string, ModuleLinkVerdict> Links(
        string published, string version, string landed)
    {
        var observation = PublishedBundleCatalogue.Read(published, version);
        return ModuleLinkObservation.Measure(observation.Artifacts,
            [new RequiredPackage("Views", "Views", HasContent: false) { ModuleName = ViewPack, LandedModulePath = landed }])
            .ModuleLinks;
    }

    // ── fixture: the published root ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One release under one identity, laid out as <c>publish-bake-bundles.sh</c> writes it: the
    /// marker, one sealed <c>plugins</c> source with a <c>Widget</c> content bundle, an empty module
    /// set (or one declaring <paramref name="sealedModule"/>), the platform surface when given,
    /// and <c>_complete</c> last.
    /// </summary>
    private string PublishedRoot(
        string version, string identity, string? surface, (string Name, byte[] Bytes)? sealedModule = null)
    {
        var published = Path.Combine(root, "published");
        Directory.CreateDirectory(Path.Combine(published, PublishedBundleCatalogue.ReleaseMarkerDirectoryName));
        AddRelease(published, version, identity, surface, ["Widget"], sealedModule);
        return published;
    }

    private static void AddRelease(
        string published, string version, string identity, string? surface,
        string[] bundles, (string Name, byte[] Bytes)? sealedModule = null)
    {
        File.WriteAllText(
            Path.Combine(published, PublishedBundleCatalogue.ReleaseMarkerDirectoryName, version), identity);
        var source = Path.Combine(published, identity, "plugins");
        var moduleDirectory = Path.Combine(source, PublishedBundleCatalogue.ModulesDirectoryName);
        Directory.CreateDirectory(moduleDirectory);

        var index = new List<string>();
        if (sealedModule is { } module)
        {
            WriteZip(Path.Combine(moduleDirectory, "views.module.nupkg"),
                (NuGetPackageWriter.ManifestEntry,
                    Encoding.UTF8.GetBytes($$$"""{"plugin":"Views","module":{"assemblyName":"{{{module.Name}}}"}}""")),
                ($"{NuGetPackageWriter.ModuleFolder}/{module.Name}.dll", module.Bytes));
            index.Add("views.module.nupkg");
        }
        File.WriteAllLines(Path.Combine(moduleDirectory, PublishedBundleCatalogue.ModulesIndexFileName), index);

        var names = new List<string>();
        foreach (var bundle in bundles)
        {
            var manifest = new BundleReader.Manifest(
                bundle, "1.0", identity,
                [
                    new BundleReader.AssemblyRef($"{bundle}/Thing", $"{bundle}_Thing.dll",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["MeshWeaver.Layout"] = CompiledDependencies.RefAsmScheme + "abc",
                        }),
                ]);
            WriteZip(Path.Combine(source, bundle + ".zip"),
                (NuGetPackageWriter.ManifestEntry,
                    JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            names.Add(bundle + ".zip");
        }
        if (surface is not null)
            File.WriteAllText(Path.Combine(source, PublishedBundleCatalogue.PlatformSurfaceFileName), surface);
        File.WriteAllText(
            Path.Combine(source, ShippedPrebuiltBundles.CompletionSentinelFileName),
            string.Join('\n', names.Order(StringComparer.Ordinal)) + "\n");
    }

    /// <summary>The running platform's own surface, as the bake would publish it.</summary>
    private static string ThisPlatformSurface(string identity) =>
        ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory).ToJson(identity);

    /// <summary>A platform three days BEHIND this one: the same surface without <c>MeshNode</c> —
    /// the memex-cloud shape of #3538, seen from the other side.</summary>
    private static string OlderPlatformSurface(string identity) =>
        ModulePlatformSurfaceJsonTest.Without(
            ThisPlatformSurface(identity), ContractAssembly, typeof(MeshNode).FullName!);

    // ── fixture: the landed generation ──────────────────────────────────────────────────────────

    /// <summary>Lands a module the way the landing service does: its own generation directory
    /// under <c>modules/</c>, the entry DLL inside — the path boot resolves and loads.</summary>
    private string Land(string name, byte[] bytes)
    {
        var generation = Path.Combine(modules, $"{name}@{Guid.NewGuid():N}");
        Directory.CreateDirectory(generation);
        var path = Path.Combine(generation, name + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] ModuleBindingMeshNode(string name) =>
        ModulePlatformSurfaceJsonTest.Emit(name, """
            using MeshWeaver.Mesh;
            public static class CodeViews { public static string Show(MeshNode node) => node.Id; }
            """);

    private static byte[] ModuleBindingNothingTheTargetLacks(string name) =>
        ModulePlatformSurfaceJsonTest.Emit(name, """
            using MeshWeaver.Mesh;
            public static class PlainViews
            {
                public static string Name(ModulePlatformSurface surface) => surface.Identity ?? "";
            }
            """);

    private static void WriteZip(string path, params (string Entry, byte[] Bytes)[] entries)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(bytes);
        }
    }
}
